using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Identity.Tests.Stores;

/// <summary>
/// What every <see cref="IDashboardIdentityStore"/> and <see cref="IDashboardKeyRingStore"/> must do,
/// whatever keeps the data. A store implementation gets a subclass supplying its harness.
/// </summary>
public abstract class DashboardIdentityStoreContractTests : IAsyncLifetime
{
    /// <summary>Sub-millisecond ticks on purpose: the store keeps the full instant, not a rounded one.</summary>
    protected static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.Zero).AddTicks(1_234_567);

    private static readonly CancellationToken None = CancellationToken.None;

    private IIdentityStoreHarness? _harness;

    protected IIdentityStoreHarness Harness => _harness ?? throw new InvalidOperationException("The harness is created in InitializeAsync.");

    /// <summary>The store most cases use; parallel and exclusion cases create more with <see cref="IIdentityStoreHarness.CreateStore"/>.</summary>
    protected IDashboardIdentityStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _harness = await CreateHarnessAsync();
        Store = _harness.CreateStore();
        foreach (var role in BuiltInRoles.All)
            await Store.CreateRoleAsync(role, None);
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
            await _harness.DisposeAsync();
    }

    protected abstract Task<IIdentityStoreHarness> CreateHarnessAsync();

    [Fact]
    public async Task CanConnect_OnAReadyStore_IsTrue()
    {
        Assert.True(await Store.CanConnectAsync(None));
    }

    [Fact]
    public async Task CreateUser_ThenFind_RoundTripsEveryFieldAndGrant()
    {
        var user = NewUser("alice") with
        {
            DisplayName = "Alice Example",
            PasswordHash = "hash-1",
            SecurityStamp = "stamp-1",
            IsEnabled = false,
            MustChangePassword = true,
            FailedSignInCount = 3,
            LockoutEndUtc = T0.AddMinutes(15),
            LastSignInAtUtc = T0.AddDays(-1),
            CreatedAtUtc = T0.AddDays(-7),
            UpdatedAtUtc = T0,
            Grants =
            [
                new AccessGrant(BuiltInRoles.ViewerId, AllSagaTypes: true, []),
                new AccessGrant(BuiltInRoles.OperatorId, AllSagaTypes: false, ["OrderSaga", "Payments.RefundSaga"]),
            ],
        };

        await Store.CreateUserAsync(user, None);
        var found = await Harness.CreateStore().FindUserAsync(user.Id, None);

        Assert.NotNull(found);
        AssertSameUser(user, found);
    }

    /// <summary>
    /// Saga type names are validated by the administration service and then stored exactly as validated;
    /// the store never trims, folds or reorders them (review-security finding 11).
    /// </summary>
    [Fact]
    public async Task CreateUser_StoresSagaTypeNamesExactlyAsGiven()
    {
        var user = NewUser("bob") with
        {
            Grants = [new AccessGrant(BuiltInRoles.ViewerId, AllSagaTypes: false, ["zeta", " Padded ", "ALPHA", "Ünïcode"])],
        };

        await Store.CreateUserAsync(user, None);
        var found = await Store.FindUserAsync(user.Id, None);

        Assert.Equal(["zeta", " Padded ", "ALPHA", "Ünïcode"], found!.Grants.Single().SagaTypes);
    }

    [Fact]
    public async Task FindUserByName_IgnoresCaseAndSurroundingWhitespace()
    {
        var user = NewUser("Carol.Smith");
        await Store.CreateUserAsync(user, None);

        Assert.Equal(user.Id, (await Store.FindUserByNameAsync("carol.smith", None))?.Id);
        Assert.Equal(user.Id, (await Store.FindUserByNameAsync("  CAROL.SMITH ", None))?.Id);
        Assert.Null(await Store.FindUserByNameAsync("carol", None));
        Assert.Equal("Carol.Smith", (await Store.FindUserByNameAsync("carol.smith", None))!.Username);
    }

    [Fact]
    public async Task FindUserByName_FoldsNonAsciiCase()
    {
        var user = NewUser("élodie");
        await Store.CreateUserAsync(user, None);

        Assert.Equal(user.Id, (await Store.FindUserByNameAsync("Élodie", None))?.Id);
        Assert.Equal(user.Id, (await Store.FindUserByNameAsync(" ÉLODIE ", None))?.Id);
        Assert.Equal("élodie", (await Store.FindUserByNameAsync("ÉLODIE", None))!.Username);
    }

    [Fact]
    public async Task FindUser_Unknown_IsNull()
    {
        Assert.Null(await Store.FindUserAsync(Guid.NewGuid(), None));
        Assert.Null(await Store.FindUserByNameAsync("nobody", None));
    }

    [Fact]
    public async Task ListAndCountUsers_ReturnEveryUserOrderedByNormalisedName()
    {
        Assert.Equal(0, await Store.CountUsersAsync(None));
        await Store.CreateUserAsync(NewUser("mallory"), None);
        await Store.CreateUserAsync(NewUser("Bob"), None);
        await Store.CreateUserAsync(NewUser("alice"), None);

        var users = await Store.ListUsersAsync(None);

        Assert.Equal(3, await Store.CountUsersAsync(None));
        Assert.Equal(["alice", "Bob", "mallory"], users.Select(u => u.Username), StringComparer.Ordinal);
    }

    /// <summary>
    /// The non-ASCII cases are why names go through a normalised column: SQLite's NOCASE folds ASCII
    /// only, so a store comparing through it (or an ASCII-only fold) passes the other cases and not these.
    /// </summary>
    [Theory]
    [InlineData("alice", "ALICE")]
    [InlineData("alice", "Alice")]
    [InlineData("alice", " alice ")]
    [InlineData("jürgen", "JÜRGEN")]
    [InlineData("ÉLODIE", "élodie")]
    public async Task CreateUser_UsernameTakenIgnoringCase_IsAConflictAndWritesNothing(string existing, string duplicate)
    {
        await Store.CreateUserAsync(NewUser(existing), None);

        var conflict = await Assert.ThrowsAsync<IdentityConflictException>(() => Store.CreateUserAsync(NewUser(duplicate), None));

        Assert.Equal(IdentityEntityKind.User, conflict.Kind);
        Assert.Equal(duplicate, conflict.Name);
        Assert.Equal(1, await Store.CountUsersAsync(None));
    }

    [Fact]
    public async Task UpdateUser_ToAnotherUsersNameIgnoringCase_IsAConflict()
    {
        await Store.CreateUserAsync(NewUser("alice"), None);
        var bob = NewUser("bob");
        await Store.CreateUserAsync(bob, None);

        await Assert.ThrowsAsync<IdentityConflictException>(() => ReplaceUserAsync(bob with { Username = "ALICE" }));

        Assert.Equal("bob", (await Store.FindUserAsync(bob.Id, None))!.Username);
    }

    [Fact]
    public async Task UpdateUser_ChangingOnlyTheCaseOfItsOwnName_IsNotAConflict()
    {
        var bob = NewUser("bob");
        await Store.CreateUserAsync(bob, None);

        await ReplaceUserAsync(bob with { Username = "Bob" });

        Assert.Equal("Bob", (await Store.FindUserAsync(bob.Id, None))!.Username);
    }

    [Fact]
    public async Task UpdateUser_ReplacesEveryFieldAndTheWholeGrantList()
    {
        var user = NewUser("dave") with
        {
            Grants =
            [
                new AccessGrant(BuiltInRoles.ViewerId, AllSagaTypes: false, ["OrderSaga"]),
                new AccessGrant(BuiltInRoles.OperatorId, AllSagaTypes: true, []),
            ],
        };
        await Store.CreateUserAsync(user, None);

        var replaced = user with
        {
            DisplayName = "David",
            PasswordHash = "hash-2",
            SecurityStamp = "stamp-2",
            IsEnabled = false,
            MustChangePassword = true,
            FailedSignInCount = 2,
            LockoutEndUtc = T0.AddHours(1),
            LastSignInAtUtc = T0.AddMinutes(-3),
            UpdatedAtUtc = T0.AddDays(1),
            Grants =
            [
                new AccessGrant(BuiltInRoles.ViewerId, AllSagaTypes: false, ["ShippingSaga", "OrderSaga"]),
                new AccessGrant(BuiltInRoles.AdministratorId, AllSagaTypes: true, []),
            ],
        };
        await ReplaceUserAsync(replaced);

        AssertSameUser(replaced, (await Harness.CreateStore().FindUserAsync(user.Id, None))!);
        Assert.False(await Store.IsRoleInUseAsync(BuiltInRoles.OperatorId, None));
    }

    [Fact]
    public async Task UpdateUser_ToNoGrants_LeavesNone()
    {
        var user = NewUser("erin") with { Grants = [new AccessGrant(BuiltInRoles.ViewerId, AllSagaTypes: true, [])] };
        await Store.CreateUserAsync(user, None);

        await ReplaceUserAsync(user with { Grants = [] });

        Assert.Empty((await Store.FindUserAsync(user.Id, None))!.Grants);
    }

    [Fact]
    public async Task UpdateUser_Unknown_IsNotFound()
    {
        var missing = await Assert.ThrowsAsync<IdentityNotFoundException>(() => ReplaceUserAsync(NewUser("ghost")));

        Assert.Equal(IdentityEntityKind.User, missing.Kind);
    }

    /// <summary>
    /// A full replace also writes the sign-in counters, so a record read outside the scope could undo a
    /// failed sign-in recorded in between; the store refuses the update rather than trusting the caller.
    /// </summary>
    [Fact]
    public async Task UpdateUser_OutsideAnExclusiveScope_ThrowsAndWritesNothing()
    {
        var user = NewUser("olga");
        await Store.CreateUserAsync(user, None);
        var stale = (await Store.FindUserAsync(user.Id, None))!;
        await Store.RecordFailedSignInAsync(user.Id, maxAttempts: 5, T0, TimeSpan.FromMinutes(15), None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.UpdateUserAsync(stale with { DisplayName = "Olga" }, None));

        var stored = (await Store.FindUserAsync(user.Id, None))!;
        Assert.Equal(1, stored.FailedSignInCount);
        Assert.Equal("olga", stored.DisplayName);
    }

    [Fact]
    public async Task CreateUser_GrantNamingAnUnknownRole_IsRejectedAndWritesNothing()
    {
        var user = NewUser("frank") with { Grants = [new AccessGrant(Guid.NewGuid(), AllSagaTypes: true, [])] };

        await Assert.ThrowsAsync<IdentityReferenceException>(() => Store.CreateUserAsync(user, None));

        Assert.Equal(0, await Store.CountUsersAsync(None));
    }

    [Fact]
    public async Task UpdateUser_GrantNamingAnUnknownRole_IsRejectedAndKeepsTheOldGrants()
    {
        var user = NewUser("grace") with { Grants = [new AccessGrant(BuiltInRoles.ViewerId, AllSagaTypes: true, [])] };
        await Store.CreateUserAsync(user, None);

        await Assert.ThrowsAsync<IdentityReferenceException>(() =>
            ReplaceUserAsync(user with { Grants = [new AccessGrant(Guid.NewGuid(), AllSagaTypes: true, [])] }));

        Assert.Equal(BuiltInRoles.ViewerId, (await Store.FindUserAsync(user.Id, None))!.Grants.Single().RoleId);
    }

    [Fact]
    public async Task DeleteUser_RemovesTheUserItsGrantsAndItsMembershipOfEveryTeam()
    {
        var leaving = NewUser("heidi") with { Grants = [new AccessGrant(BuiltInRoles.OperatorId, AllSagaTypes: true, [])] };
        var staying = NewUser("ivan");
        await Store.CreateUserAsync(leaving, None);
        await Store.CreateUserAsync(staying, None);
        var ops = NewTeam("Ops") with { MemberIds = [leaving.Id, staying.Id] };
        var night = NewTeam("Night shift") with { MemberIds = [leaving.Id] };
        await Store.CreateTeamAsync(ops, None);
        await Store.CreateTeamAsync(night, None);

        await Store.DeleteUserAsync(leaving.Id, None);

        Assert.Null(await Store.FindUserAsync(leaving.Id, None));
        Assert.Equal([staying.Id], (await Store.FindTeamAsync(ops.Id, None))!.MemberIds);
        Assert.Empty((await Store.FindTeamAsync(night.Id, None))!.MemberIds);
        Assert.Empty(await Store.ListTeamsForUserAsync(leaving.Id, None));
        Assert.False(await Store.IsRoleInUseAsync(BuiltInRoles.OperatorId, None));
        Assert.NotNull(await Store.FindUserAsync(staying.Id, None));
    }

    [Fact]
    public Task DeleteUser_Unknown_IsNotFound() =>
        Assert.ThrowsAsync<IdentityNotFoundException>(() => Store.DeleteUserAsync(Guid.NewGuid(), None));

    [Fact]
    public async Task CreateTeam_ThenFind_RoundTripsMembersAndGrants()
    {
        var a = NewUser("judy");
        var b = NewUser("ken");
        await Store.CreateUserAsync(a, None);
        await Store.CreateUserAsync(b, None);
        var team = NewTeam("Payments") with
        {
            Description = "Owns the payment sagas.",
            MemberIds = [a.Id, b.Id],
            Grants =
            [
                new AccessGrant(BuiltInRoles.OperatorId, AllSagaTypes: false, ["Payments.RefundSaga"]),
                new AccessGrant(BuiltInRoles.ViewerId, AllSagaTypes: true, []),
            ],
        };

        await Store.CreateTeamAsync(team, None);
        var found = await Harness.CreateStore().FindTeamAsync(team.Id, None);

        Assert.NotNull(found);
        AssertSameTeam(team, found);
    }

    [Fact]
    public async Task ListTeamsForUser_ReturnsOnlyTheTeamsTheUserIsIn()
    {
        var user = NewUser("leo");
        var other = NewUser("mia");
        await Store.CreateUserAsync(user, None);
        await Store.CreateUserAsync(other, None);
        await Store.CreateTeamAsync(NewTeam("Zulu") with { MemberIds = [user.Id] }, None);
        await Store.CreateTeamAsync(NewTeam("alpha") with { MemberIds = [user.Id, other.Id] }, None);
        await Store.CreateTeamAsync(NewTeam("Bravo") with { MemberIds = [other.Id] }, None);

        var teams = await Store.ListTeamsForUserAsync(user.Id, None);

        Assert.Equal(["alpha", "Zulu"], teams.Select(t => t.Name), StringComparer.Ordinal);
        Assert.Equal(["alpha", "Bravo", "Zulu"], (await Store.ListTeamsAsync(None)).Select(t => t.Name), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("Ops", " OPS")]
    [InlineData("Équipe nuit", "équipe NUIT")]
    public async Task CreateTeam_NameTakenIgnoringCase_IsAConflict(string existing, string duplicate)
    {
        await Store.CreateTeamAsync(NewTeam(existing), None);

        var conflict = await Assert.ThrowsAsync<IdentityConflictException>(() => Store.CreateTeamAsync(NewTeam(duplicate), None));

        Assert.Equal(IdentityEntityKind.Team, conflict.Kind);
        Assert.Single(await Store.ListTeamsAsync(None));
    }

    [Theory]
    [InlineData("ops")]
    [InlineData("überwachung")]
    public async Task UpdateTeam_ToAnotherTeamsNameIgnoringCase_IsAConflict(string taken)
    {
        await Store.CreateTeamAsync(NewTeam("Ops"), None);
        await Store.CreateTeamAsync(NewTeam("Überwachung"), None);
        var night = NewTeam("Night");
        await Store.CreateTeamAsync(night, None);

        var conflict = await Assert.ThrowsAsync<IdentityConflictException>(() => Store.UpdateTeamAsync(night with { Name = taken }, None));

        Assert.Equal(IdentityEntityKind.Team, conflict.Kind);
        Assert.Equal("Night", (await Store.FindTeamAsync(night.Id, None))!.Name);
    }

    [Fact]
    public async Task UpdateTeam_ReplacesNameDescriptionMembersAndGrants()
    {
        var a = NewUser("nina");
        var b = NewUser("oscar");
        await Store.CreateUserAsync(a, None);
        await Store.CreateUserAsync(b, None);
        var team = NewTeam("Ops") with
        {
            MemberIds = [a.Id],
            Grants = [new AccessGrant(BuiltInRoles.OperatorId, AllSagaTypes: true, [])],
        };
        await Store.CreateTeamAsync(team, None);

        var replaced = team with
        {
            Name = "Operations",
            Description = "Renamed.",
            MemberIds = [b.Id],
            Grants = [new AccessGrant(BuiltInRoles.ViewerId, AllSagaTypes: false, ["OrderSaga"])],
        };
        await Store.UpdateTeamAsync(replaced, None);

        AssertSameTeam(replaced, (await Harness.CreateStore().FindTeamAsync(team.Id, None))!);
        Assert.Empty(await Store.ListTeamsForUserAsync(a.Id, None));
        Assert.False(await Store.IsRoleInUseAsync(BuiltInRoles.OperatorId, None));
    }

    [Fact]
    public async Task CreateTeam_MemberThatIsNotAUser_IsRejected()
    {
        await Assert.ThrowsAsync<IdentityReferenceException>(() =>
            Store.CreateTeamAsync(NewTeam("Ops") with { MemberIds = [Guid.NewGuid()] }, None));

        Assert.Empty(await Store.ListTeamsAsync(None));
    }

    [Fact]
    public async Task DeleteTeam_RemovesTheTeamAndItsGrantsButNotItsMembers()
    {
        var user = NewUser("peggy");
        await Store.CreateUserAsync(user, None);
        var team = NewTeam("Ops") with
        {
            MemberIds = [user.Id],
            Grants = [new AccessGrant(BuiltInRoles.OperatorId, AllSagaTypes: true, [])],
        };
        await Store.CreateTeamAsync(team, None);

        await Store.DeleteTeamAsync(team.Id, None);

        Assert.Null(await Store.FindTeamAsync(team.Id, None));
        Assert.NotNull(await Store.FindUserAsync(user.Id, None));
        Assert.False(await Store.IsRoleInUseAsync(BuiltInRoles.OperatorId, None));
        await Assert.ThrowsAsync<IdentityNotFoundException>(() => Store.DeleteTeamAsync(team.Id, None));
    }

    [Fact]
    public async Task CreateRole_ThenFind_RoundTripsByIdAndByNameIgnoringCase()
    {
        var role = NewRole("Auditor", Permissions.SagasView, Permissions.SagasData);

        await Store.CreateRoleAsync(role, None);

        AssertSameRole(role, (await Harness.CreateStore().FindRoleAsync(role.Id, None))!);
        Assert.Equal(role.Id, (await Store.FindRoleByNameAsync(" auditor ", None))?.Id);
        AssertSameRole(BuiltInRoles.Administrator, (await Store.FindRoleAsync(BuiltInRoles.AdministratorId, None))!);
        Assert.Equal(
            ["Administrator", "Auditor", "Operator", "Viewer"],
            (await Store.ListRolesAsync(None)).Select(r => r.Name),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task CreateRole_NameTakenIgnoringCase_IsAConflict()
    {
        var conflict = await Assert.ThrowsAsync<IdentityConflictException>(() =>
            Store.CreateRoleAsync(NewRole("VIEWER", Permissions.SagasView), None));

        Assert.Equal(IdentityEntityKind.Role, conflict.Kind);
    }

    [Fact]
    public async Task CreateRole_NonAsciiNameDifferingOnlyInCase_IsAConflict()
    {
        var auditor = NewRole("Prüfer", Permissions.SagasView);
        await Store.CreateRoleAsync(auditor, None);

        var conflict = await Assert.ThrowsAsync<IdentityConflictException>(() =>
            Store.CreateRoleAsync(NewRole("PRÜFER", Permissions.SagasView), None));

        Assert.Equal(IdentityEntityKind.Role, conflict.Kind);
        Assert.Equal(auditor.Id, (await Store.FindRoleByNameAsync("prüfer", None))?.Id);
    }

    [Fact]
    public async Task UpdateRole_ReplacesNameDescriptionAndPermissions()
    {
        var role = NewRole("Auditor", Permissions.SagasView);
        await Store.CreateRoleAsync(role, None);

        var replaced = role with { Name = "Data auditor", Description = "Reads saga data.", Permissions = [Permissions.SagasData, Permissions.SagasView] };
        await Store.UpdateRoleAsync(replaced, None);

        AssertSameRole(replaced, (await Store.FindRoleAsync(role.Id, None))!);
        await Assert.ThrowsAsync<IdentityConflictException>(() => Store.UpdateRoleAsync(replaced with { Name = "operator" }, None));
        await Assert.ThrowsAsync<IdentityNotFoundException>(() => Store.UpdateRoleAsync(NewRole("Ghost", Permissions.SagasView), None));
    }

    [Fact]
    public async Task IsRoleInUse_SeesUserGrantsAndTeamGrants()
    {
        var byUser = NewRole("Held by a user", Permissions.SagasView);
        var byTeam = NewRole("Held by a team", Permissions.SagasView);
        var unused = NewRole("Unused", Permissions.SagasView);
        foreach (var role in new[] { byUser, byTeam, unused })
            await Store.CreateRoleAsync(role, None);
        await Store.CreateUserAsync(NewUser("quinn") with { Grants = [new AccessGrant(byUser.Id, AllSagaTypes: false, ["OrderSaga"])] }, None);
        await Store.CreateTeamAsync(NewTeam("Ops") with { Grants = [new AccessGrant(byTeam.Id, AllSagaTypes: true, [])] }, None);

        Assert.True(await Store.IsRoleInUseAsync(byUser.Id, None));
        Assert.True(await Store.IsRoleInUseAsync(byTeam.Id, None));
        Assert.False(await Store.IsRoleInUseAsync(unused.Id, None));
    }

    [Fact]
    public async Task DeleteRole_InUse_IsRefusedAndUnused_IsRemoved()
    {
        var used = NewRole("Used", Permissions.SagasView);
        var unused = NewRole("Unused", Permissions.SagasView);
        await Store.CreateRoleAsync(used, None);
        await Store.CreateRoleAsync(unused, None);
        await Store.CreateTeamAsync(NewTeam("Ops") with { Grants = [new AccessGrant(used.Id, AllSagaTypes: true, [])] }, None);

        await Assert.ThrowsAsync<IdentityReferenceException>(() => Store.DeleteRoleAsync(used.Id, None));
        await Store.DeleteRoleAsync(unused.Id, None);

        Assert.NotNull(await Store.FindRoleAsync(used.Id, None));
        Assert.Null(await Store.FindRoleAsync(unused.Id, None));
        await Assert.ThrowsAsync<IdentityNotFoundException>(() => Store.DeleteRoleAsync(unused.Id, None));
    }

    [Fact]
    public async Task RecordFailedSignIn_BelowTheThreshold_CountsWithoutLocking()
    {
        var user = NewUser("rita");
        await Store.CreateUserAsync(user, None);

        var first = await Store.RecordFailedSignInAsync(user.Id, maxAttempts: 3, T0, TimeSpan.FromMinutes(15), None);
        var second = await Store.RecordFailedSignInAsync(user.Id, maxAttempts: 3, T0, TimeSpan.FromMinutes(15), None);

        Assert.Null(first);
        Assert.Null(second);
        var stored = (await Store.FindUserAsync(user.Id, None))!;
        Assert.Equal(2, stored.FailedSignInCount);
        Assert.Null(stored.LockoutEndUtc);
    }

    [Fact]
    public async Task RecordFailedSignIn_AtTheThreshold_LocksAndStartsTheCountAgain()
    {
        var user = NewUser("sam") with { FailedSignInCount = 2 };
        await Store.CreateUserAsync(user, None);

        var lockedUntil = await Store.RecordFailedSignInAsync(user.Id, maxAttempts: 3, T0, TimeSpan.FromMinutes(15), None);

        Assert.Equal(T0.AddMinutes(15), lockedUntil);
        var stored = (await Store.FindUserAsync(user.Id, None))!;
        Assert.Equal(0, stored.FailedSignInCount);
        Assert.Equal(T0.AddMinutes(15), stored.LockoutEndUtc);
    }

    [Fact]
    public async Task RecordFailedSignIn_WithLockoutDisabled_CountsAndNeverLocks()
    {
        var user = NewUser("tina") with { FailedSignInCount = 99 };
        await Store.CreateUserAsync(user, None);

        var lockedUntil = await Store.RecordFailedSignInAsync(user.Id, maxAttempts: 0, T0, TimeSpan.FromMinutes(15), None);

        Assert.Null(lockedUntil);
        var stored = (await Store.FindUserAsync(user.Id, None))!;
        Assert.Equal(100, stored.FailedSignInCount);
        Assert.Null(stored.LockoutEndUtc);
    }

    [Fact]
    public Task RecordFailedSignIn_UnknownUser_IsNotFound() =>
        Assert.ThrowsAsync<IdentityNotFoundException>(() =>
            Store.RecordFailedSignInAsync(Guid.NewGuid(), maxAttempts: 5, T0, TimeSpan.FromMinutes(15), None));

    /// <summary>Each attempt on its own unit of work, all at once: a read-modify-write would lose increments.</summary>
    [Fact]
    public async Task RecordFailedSignIn_InParallel_LosesNoIncrement()
    {
        const int attempts = 24;
        var user = NewUser("uma");
        await Store.CreateUserAsync(user, None);
        var stores = Enumerable.Range(0, attempts).Select(_ => Harness.CreateStore()).ToList();

        await Task.WhenAll(stores.Select(store => Task.Run(() =>
            store.RecordFailedSignInAsync(user.Id, maxAttempts: 1000, T0, TimeSpan.FromMinutes(15), None), None)));

        Assert.Equal(attempts, (await Store.FindUserAsync(user.Id, None))!.FailedSignInCount);
    }

    /// <summary>Exactly one of the concurrent failures crosses the threshold, and only that one reports the lockout.</summary>
    [Fact]
    public async Task RecordFailedSignIn_InParallel_LocksExactlyOnceAtTheThreshold()
    {
        const int attempts = 5;
        var user = NewUser("victor");
        await Store.CreateUserAsync(user, None);
        var stores = Enumerable.Range(0, attempts).Select(_ => Harness.CreateStore()).ToList();

        var results = await Task.WhenAll(stores.Select(store => Task.Run(() =>
            store.RecordFailedSignInAsync(user.Id, maxAttempts: attempts, T0, TimeSpan.FromMinutes(15), None), None)));

        Assert.Equal(T0.AddMinutes(15), Assert.Single(results, r => r is not null));
        var stored = (await Store.FindUserAsync(user.Id, None))!;
        Assert.Equal(0, stored.FailedSignInCount);
        Assert.Equal(T0.AddMinutes(15), stored.LockoutEndUtc);
    }

    [Fact]
    public async Task RecordSignIn_ClearsTheCountAndLockoutAndStampsTheTime()
    {
        var user = NewUser("wendy") with { FailedSignInCount = 4, LockoutEndUtc = T0.AddMinutes(-1), PasswordHash = "old-hash" };
        await Store.CreateUserAsync(user, None);

        await Store.RecordSignInAsync(user.Id, T0, rehash: null, None);

        var stored = (await Store.FindUserAsync(user.Id, None))!;
        Assert.Equal(0, stored.FailedSignInCount);
        Assert.Null(stored.LockoutEndUtc);
        Assert.Equal(T0, stored.LastSignInAtUtc);
        Assert.Equal("old-hash", stored.PasswordHash);
        Assert.Equal(user.UpdatedAtUtc, stored.UpdatedAtUtc);
    }

    [Fact]
    public async Task RecordSignIn_WithARehash_ReplacesThePasswordHash()
    {
        var user = NewUser("xavier") with { PasswordHash = "old-hash" };
        await Store.CreateUserAsync(user, None);

        await Store.RecordSignInAsync(user.Id, T0, rehash: "new-hash", None);

        Assert.Equal("new-hash", (await Store.FindUserAsync(user.Id, None))!.PasswordHash);
        await Assert.ThrowsAsync<IdentityNotFoundException>(() => Store.RecordSignInAsync(Guid.NewGuid(), T0, null, None));
    }

    /// <summary>
    /// Two "create the first user only if there is none" writers: the second scope cannot even begin until
    /// the first commits, so it sees the first user and writes nothing.
    /// </summary>
    [Fact]
    public async Task BeginExclusive_SerialisesCheckThenWrite()
    {
        var first = Harness.CreateStore();
        var second = Harness.CreateStore();

        var scope = await first.BeginExclusiveAsync(None);
        Task<bool> secondWriter;
        try
        {
            secondWriter = Task.Run(() => CreateIfNoUsersAsync(second, NewUser("second")), None);
            await Task.Delay(TimeSpan.FromMilliseconds(300), None);
            Assert.False(secondWriter.IsCompleted, "A second exclusive scope began while the first was open.");

            if (await first.CountUsersAsync(None) == 0)
                await first.CreateUserAsync(NewUser("first"), None);
            await scope.CommitAsync(None);
        }
        finally
        {
            await scope.DisposeAsync();
        }

        Assert.False(await secondWriter);
        Assert.Equal(["first"], (await Store.ListUsersAsync(None)).Select(u => u.Username), StringComparer.Ordinal);
    }

    [Fact]
    public async Task BeginExclusive_DisposedWithoutCommit_DiscardsItsWrites()
    {
        await using (await Store.BeginExclusiveAsync(None))
        {
            await Store.CreateUserAsync(NewUser("yara"), None);
            await Store.CreateRoleAsync(NewRole("Temporary", Permissions.SagasView), None);
            Assert.Equal(1, await Store.CountUsersAsync(None));
        }

        Assert.Equal(0, await Store.CountUsersAsync(None));
        Assert.Null(await Store.FindRoleByNameAsync("Temporary", None));
    }

    [Fact]
    public async Task BeginExclusive_CommittedWrites_AreVisibleToOtherUnitsOfWork()
    {
        var user = NewUser("zane");
        await using (var scope = await Store.BeginExclusiveAsync(None))
        {
            await Store.CreateUserAsync(user, None);
            await Store.UpdateUserAsync(user with { DisplayName = "Zane Z" }, None);
            await scope.CommitAsync(None);
        }

        Assert.Equal("Zane Z", (await Harness.CreateStore().FindUserAsync(user.Id, None))!.DisplayName);
    }

    [Fact]
    public async Task BeginExclusive_WhileOneIsOpenOnTheSameStore_Throws()
    {
        await using var scope = await Store.BeginExclusiveAsync(None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.BeginExclusiveAsync(None));
    }

    [Fact]
    public void KeyRing_SavedEntries_LoadInOrderOnAnotherUnitOfWork()
    {
        var writer = Harness.CreateKeyRingStore();
        Assert.Empty(writer.Load());

        writer.Save(new KeyRingEntry("key-1", "<key id=\"1\" />"));
        writer.Save(new KeyRingEntry(null, "<key id=\"2\" />"));

        var entries = Harness.CreateKeyRingStore().Load();
        Assert.Equal([new KeyRingEntry("key-1", "<key id=\"1\" />"), new KeyRingEntry(null, "<key id=\"2\" />")], entries);
    }

    /// <summary>A full user replace the way a service makes one: inside an exclusive scope, committed.</summary>
    private async Task ReplaceUserAsync(DashboardUser user)
    {
        await using var scope = await Store.BeginExclusiveAsync(None);
        await Store.UpdateUserAsync(user, None);
        await scope.CommitAsync(None);
    }

    private static async Task<bool> CreateIfNoUsersAsync(IDashboardIdentityStore store, DashboardUser user)
    {
        await using var scope = await store.BeginExclusiveAsync(None);
        if (await store.CountUsersAsync(None) > 0)
            return false;

        await store.CreateUserAsync(user, None);
        await scope.CommitAsync(None);
        return true;
    }

    protected static DashboardUser NewUser(string username) => new(
        Guid.NewGuid(),
        username,
        DisplayName: username,
        PasswordHash: "hash",
        SecurityStamp: "stamp",
        IsEnabled: true,
        MustChangePassword: false,
        FailedSignInCount: 0,
        LockoutEndUtc: null,
        LastSignInAtUtc: null,
        CreatedAtUtc: T0,
        UpdatedAtUtc: T0,
        Grants: []);

    protected static DashboardTeam NewTeam(string name) => new(Guid.NewGuid(), name, Description: null, MemberIds: [], Grants: []);

    protected static DashboardRole NewRole(string name, params string[] permissions) =>
        new(Guid.NewGuid(), name, Description: null, IsBuiltIn: false, permissions);

    private static void AssertSameUser(DashboardUser expected, DashboardUser actual)
    {
        Assert.Equal(expected, actual, UserScalarsComparer.Instance);
        AssertSameGrants(expected.Grants, actual.Grants);
    }

    private static void AssertSameTeam(DashboardTeam expected, DashboardTeam actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.MemberIds.Order(), actual.MemberIds.Order());
        AssertSameGrants(expected.Grants, actual.Grants);
    }

    private static void AssertSameRole(DashboardRole expected, DashboardRole actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.IsBuiltIn, actual.IsBuiltIn);
        Assert.Equal(expected.Permissions, actual.Permissions);
    }

    /// <summary>Grants are a set per subject (one per role); their saga type lists keep their order.</summary>
    private static void AssertSameGrants(IReadOnlyList<AccessGrant> expected, IReadOnlyList<AccessGrant> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var grant in expected)
        {
            var match = Assert.Single(actual, g => g.RoleId == grant.RoleId);
            Assert.Equal(grant.AllSagaTypes, match.AllSagaTypes);
            Assert.Equal(grant.SagaTypes, match.SagaTypes);
        }
    }

    /// <summary>Record equality over every scalar; the grant list is compared separately because lists compare by reference.</summary>
    private sealed class UserScalarsComparer : IEqualityComparer<DashboardUser>
    {
        public static readonly UserScalarsComparer Instance = new();

        public bool Equals(DashboardUser? x, DashboardUser? y) =>
            x is not null && y is not null
            && x.Id == y.Id
            && string.Equals(x.Username, y.Username, StringComparison.Ordinal)
            && string.Equals(x.DisplayName, y.DisplayName, StringComparison.Ordinal)
            && string.Equals(x.PasswordHash, y.PasswordHash, StringComparison.Ordinal)
            && string.Equals(x.SecurityStamp, y.SecurityStamp, StringComparison.Ordinal)
            && x.IsEnabled == y.IsEnabled
            && x.MustChangePassword == y.MustChangePassword
            && x.FailedSignInCount == y.FailedSignInCount
            && x.LockoutEndUtc == y.LockoutEndUtc
            && x.LastSignInAtUtc == y.LastSignInAtUtc
            && x.CreatedAtUtc == y.CreatedAtUtc
            && x.UpdatedAtUtc == y.UpdatedAtUtc;

        public int GetHashCode(DashboardUser obj) => obj.Id.GetHashCode();
    }
}
