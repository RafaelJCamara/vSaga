using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;
using static VSaga.Dashboard.Identity.Tests.Services.RecordingAccessChangeObserver;
using static VSaga.Dashboard.Identity.Tests.Services.ServiceTestContext;

namespace VSaga.Dashboard.Identity.Tests.Services;

/// <summary>
/// The last-administrator invariant through every mutation path, built-in immutability, grant, team and
/// role validation, and who is notified for team and role changes.
/// </summary>
public sealed class AccessRulesTests : IAsyncLifetime
{
    private ServiceTestContext _context = null!;
    private DashboardUser _root = null!;

    public async Task InitializeAsync()
    {
        _context = await CreateAsync();
        _root = await _context.SeedAdministratorAsync();
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task LastAdministrator_CannotBeDeleted()
    {
        await AssertLastAdministratorAsync(a => a.DeleteUserAsync(_root.Id, Admin, None));
        Assert.NotNull(await _context.NewStore().FindUserAsync(_root.Id, None));
    }

    [Fact]
    public async Task LastAdministrator_CannotBeDisabled()
    {
        await AssertLastAdministratorAsync(a => a.UpdateUserAsync(_root.Id, new UserChanges(null, false, null), Admin, None));
        Assert.True((await _context.ReadUserAsync(_root.Id)).IsEnabled);
    }

    [Fact]
    public async Task LastAdministrator_CannotBeDemotedByAGrantChange()
    {
        await AssertLastAdministratorAsync(a => a.UpdateUserAsync(_root.Id, new UserChanges(null, null, [AllTypes(BuiltInRoles.OperatorId)]), Admin, None));
        await AssertLastAdministratorAsync(a => a.UpdateUserAsync(_root.Id, new UserChanges(null, null, []), Admin, None));
        Assert.Equal(BuiltInRoles.AdministratorId, Assert.Single((await _context.ReadUserAsync(_root.Id)).Grants).RoleId);
    }

    /// <summary><c>access.manage</c> counts only in a grant for all saga types.</summary>
    [Fact]
    public Task LastAdministrator_CannotBeNarrowedToNamedSagaTypes() =>
        AssertLastAdministratorAsync(a =>
            a.UpdateUserAsync(_root.Id, new UserChanges(null, null, [Named(BuiltInRoles.AdministratorId, "OrderSaga")]), Admin, None));

    [Fact]
    public async Task AScopedManager_DoesNotCountAsAnAdministrator()
    {
        await _context.SeedUserAsync("scoped", grants: Named(BuiltInRoles.AdministratorId, "OrderSaga"));

        await AssertLastAdministratorAsync(a => a.DeleteUserAsync(_root.Id, Admin, None));
    }

    [Fact]
    public async Task ADisabledAdministrator_DoesNotCount()
    {
        await _context.SeedUserAsync("dormant", isEnabled: false, grants: AllTypes(BuiltInRoles.AdministratorId));

        await AssertLastAdministratorAsync(a => a.DeleteUserAsync(_root.Id, Admin, None));
    }

    /// <summary>A user who must change their password regains access by changing it, so they count.</summary>
    [Fact]
    public async Task AnAdministratorWhoMustChangeTheirPassword_Counts()
    {
        await _context.SeedUserAsync("successor", mustChangePassword: true, grants: AllTypes(BuiltInRoles.AdministratorId));

        await _context.NewAdministration().DeleteUserAsync(_root.Id, Admin, None);

        Assert.Null(await _context.NewStore().FindUserAsync(_root.Id, None));
    }

    [Fact]
    public async Task WithASecondAdministrator_TheFirstCanBeDisabledAndDeleted()
    {
        await _context.SeedAdministratorAsync("second");
        var administration = _context.NewAdministration();

        await administration.UpdateUserAsync(_root.Id, new UserChanges(null, false, null), Admin, None);
        await administration.DeleteUserAsync(_root.Id, Admin, None);

        Assert.Null(await _context.NewStore().FindUserAsync(_root.Id, None));
    }

    [Fact]
    public async Task LastAdministratorThroughATeam_CannotLeaveOrLoseTheTeam()
    {
        var teamAdmin = await _context.SeedUserAsync("teamadmin");
        var team = await _context.SeedTeamAsync("Admins", [teamAdmin.Id], AllTypes(BuiltInRoles.AdministratorId));
        await _context.NewAdministration().DeleteUserAsync(_root.Id, Admin, None);

        await AssertLastAdministratorAsync(a => a.UpdateTeamAsync(team.Id, new TeamDraft("Admins", null, [], [AllTypes(BuiltInRoles.AdministratorId)]), Admin, None));
        await AssertLastAdministratorAsync(a => a.UpdateTeamAsync(team.Id, new TeamDraft("Admins", null, [teamAdmin.Id], [AllTypes(BuiltInRoles.ViewerId)]), Admin, None));
        await AssertLastAdministratorAsync(a => a.DeleteTeamAsync(team.Id, Admin, None));

        var stored = await _context.NewStore().FindTeamAsync(team.Id, None);
        Assert.Equal([teamAdmin.Id], stored!.MemberIds);
    }

    [Fact]
    public async Task LastAdministratorThroughACustomRole_CannotLoseItsAccessManage()
    {
        var keepers = await _context.SeedRoleAsync("Keepers", Permissions.AccessManage, Permissions.SagasView);
        var keeper = await _context.SeedUserAsync("keeper", grants: AllTypes(keepers.Id));
        await _context.NewAdministration().DeleteUserAsync(_root.Id, Admin, None);

        await AssertLastAdministratorAsync(a => a.UpdateRoleAsync(keepers.Id, new RoleDraft("Keepers", null, [Permissions.SagasView]), Admin, None));

        Assert.Contains(Permissions.AccessManage, (await _context.NewStore().FindRoleAsync(keepers.Id, None))!.Permissions, StringComparer.Ordinal);
        Assert.True((await _context.ReadUserAsync(keeper.Id)).IsEnabled);
    }

    [Theory]
    [InlineData("a0000000-0000-0000-0000-000000000001")]
    [InlineData("a0000000-0000-0000-0000-000000000002")]
    [InlineData("a0000000-0000-0000-0000-000000000003")]
    public async Task BuiltInRoles_CannotBeChangedOrDeleted(string id)
    {
        var roleId = Guid.Parse(id);
        var administration = _context.NewAdministration();

        var update = await Assert.ThrowsAsync<IdentityRuleException>(() =>
            administration.UpdateRoleAsync(roleId, new RoleDraft("Renamed", null, [Permissions.SagasView]), Admin, None));
        var delete = await Assert.ThrowsAsync<IdentityRuleException>(() => administration.DeleteRoleAsync(roleId, Admin, None));

        Assert.Equal(IdentityRuleCodes.RoleImmutable, update.Code);
        Assert.Equal(IdentityRuleCodes.RoleImmutable, delete.Code);
        Assert.Equal(BuiltInRoles.Find(roleId)!.Permissions, (await _context.NewStore().FindRoleAsync(roleId, None))!.Permissions);
    }

    [Fact]
    public async Task CreateRole_ABuiltInNameIgnoringCase_IsNameTaken()
    {
        var error = await Assert.ThrowsAsync<IdentityRuleException>(() =>
            _context.NewAdministration().CreateRoleAsync(new RoleDraft(" operator ", null, [Permissions.SagasView]), Admin, None));

        Assert.Equal(IdentityRuleCodes.NameTaken, error.Code);
    }

    [Fact]
    public async Task CreateRole_StoresPermissionsOnceInCatalogueOrder()
    {
        var role = await _context.NewAdministration().CreateRoleAsync(
            new RoleDraft(" Support ", "  Looks at payloads ", [Permissions.SagasData, Permissions.SagasView, Permissions.SagasData]), Admin, None);

        var stored = await _context.NewStore().FindRoleAsync(role.Id, None);
        Assert.Equal("Support", stored!.Name);
        Assert.Equal("Looks at payloads", stored.Description);
        Assert.False(stored.IsBuiltIn);
        Assert.Equal([Permissions.SagasView, Permissions.SagasData], stored.Permissions);
        Assert.Equal(0, _context.Observer.AllUsersNotifications);
    }

    [Theory]
    [InlineData(new string[0], "permissions")]
    [InlineData(new[] { "sagas.view", "sagas.delete" }, "permissions[1]")]
    public async Task CreateRole_PermissionsMustBeANonEmptySubsetOfTheCatalogue(string[] permissions, string path)
    {
        var error = await Assert.ThrowsAsync<IdentityValidationException>(() =>
            _context.NewAdministration().CreateRoleAsync(new RoleDraft("Odd", null, permissions), Admin, None));

        Assert.True(error.Errors.ContainsKey(path), string.Join(", ", error.Errors.Keys));
    }

    [Fact]
    public async Task RoleAndTeamNames_AreBoundedAndDescriptionsToo()
    {
        var administration = _context.NewAdministration();

        var role = await Assert.ThrowsAsync<IdentityValidationException>(() =>
            administration.CreateRoleAsync(new RoleDraft(new string('r', 65), new string('d', 257), [Permissions.SagasView]), Admin, None));
        var team = await Assert.ThrowsAsync<IdentityValidationException>(() =>
            administration.CreateTeamAsync(new TeamDraft("  ", null, null, null), Admin, None));

        Assert.Equal(["description", "name"], role.Errors.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.Equal(["Use at most 256 characters."], role.Errors["description"]);
        Assert.Equal(["name"], team.Errors.Keys, StringComparer.Ordinal);
        Assert.NotNull(await administration.CreateRoleAsync(new RoleDraft(new string('r', 64), new string('d', 256), [Permissions.SagasView]), Admin, None));
    }

    /// <summary>A short description with a control character is told about the character, not the length.</summary>
    [Fact]
    public async Task Description_AControlCharacterOtherThanABreakOrTab_IsRejectedWithItsOwnMessage()
    {
        var administration = _context.NewAdministration();

        var team = await Assert.ThrowsAsync<IdentityValidationException>(() =>
            administration.CreateTeamAsync(new TeamDraft("Ops", "Ring the " + (char)7 + " bell", null, null), Admin, None));
        var accepted = await administration.CreateTeamAsync(new TeamDraft("Ops", "Line one\r\nLine\ttwo", null, null), Admin, None);

        Assert.Equal(["Use no control characters other than line breaks and tabs."], team.Errors["description"]);
        Assert.Equal("Line one\r\nLine\ttwo", accepted.Description);
    }

    /// <summary>A JSON body can carry <c>"grants":[null]</c>: a validation error at its index, not a server error.</summary>
    [Fact]
    public async Task Grant_Null_IsAValidationErrorAtItsIndex()
    {
        var bob = await _context.SeedUserAsync("bob");

        var error = await Assert.ThrowsAsync<IdentityValidationException>(() => _context.NewAdministration().UpdateUserAsync(
            bob.Id, new UserChanges(null, null, [Named(BuiltInRoles.ViewerId, "OrderSaga"), null!]), Admin, None));

        Assert.Equal(["grants[1]"], error.Errors.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task UpdateRole_NotifiesEveryone()
    {
        var role = await _context.SeedRoleAsync("Readers", Permissions.SagasView);

        await _context.NewAdministration().UpdateRoleAsync(role.Id, new RoleDraft("Readers", null, [Permissions.SagasData]), Admin, None);

        Assert.Equal(1, _context.Observer.AllUsersNotifications);
        Assert.Empty(_context.Observer.UserNotifications);
    }

    /// <summary>
    /// The API key can act as a custom role (<c>Dashboard:ApiKeyRole</c>) that no grant names, so deleting a role
    /// changes what live connections may do even though no user or team held it.
    /// </summary>
    [Fact]
    public async Task DeleteRole_NotifiesEveryone()
    {
        var role = await _context.SeedRoleAsync("Readers", Permissions.SagasView);

        await _context.NewAdministration().DeleteRoleAsync(role.Id, Admin, None);

        Assert.Null(await _context.NewStore().FindRoleAsync(role.Id, None));
        Assert.Equal(1, _context.Observer.AllUsersNotifications);
        Assert.Empty(_context.Observer.UserNotifications);
    }

    [Fact]
    public async Task DeleteRole_InUse_IsRoleInUse()
    {
        var role = await _context.SeedRoleAsync("Readers", Permissions.SagasView);
        var reader = await _context.SeedUserAsync("reader");
        var team = await _context.SeedTeamAsync("Readers", [reader.Id], Named(role.Id, "OrderSaga"));
        var administration = _context.NewAdministration();

        var inUse = await Assert.ThrowsAsync<IdentityRuleException>(() => administration.DeleteRoleAsync(role.Id, Admin, None));
        await administration.DeleteTeamAsync(team.Id, Admin, None);
        await administration.DeleteRoleAsync(role.Id, Admin, None);

        Assert.Equal(IdentityRuleCodes.RoleInUse, inUse.Code);
        Assert.Null(await _context.NewStore().FindRoleAsync(role.Id, None));
    }

    /// <summary>A blank saga type would mean "no filter" to every provider (review-security finding 11).</summary>
    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    [InlineData("Order\tSaga")]
    [InlineData("Order\nSaga")]
    public async Task Grant_ABlankOrControlCharacterSagaType_IsRejected(string sagaType)
    {
        var bob = await _context.SeedUserAsync("bob");

        var error = await Assert.ThrowsAsync<IdentityValidationException>(() => _context.NewAdministration().UpdateUserAsync(
            bob.Id, new UserChanges(null, null, [Named(BuiltInRoles.ViewerId, "OrderSaga", sagaType)]), Admin, None));

        Assert.Equal(["grants[0].sagaTypes"], error.Errors.Keys, StringComparer.Ordinal);
        Assert.Empty((await _context.ReadUserAsync(bob.Id)).Grants);
    }

    [Fact]
    public async Task Grant_SagaTypesAreTrimmedAndStoredAsValidated()
    {
        var bob = await _context.SeedUserAsync("bob");

        await _context.NewAdministration().UpdateUserAsync(
            bob.Id, new UserChanges(null, null, [Named(BuiltInRoles.ViewerId, "  OrderSaga ", "Payments.RefundSaga")]), Admin, None);

        Assert.Equal(["OrderSaga", "Payments.RefundSaga"], Assert.Single((await _context.ReadUserAsync(bob.Id)).Grants).SagaTypes);
    }

    [Fact]
    public async Task Grant_ShapeRules_AreEachKeyedByPath()
    {
        var bob = await _context.SeedUserAsync("bob");
        AccessGrant[] grants =
        [
            new(BuiltInRoles.ViewerId, AllSagaTypes: true, ["OrderSaga"]),
            new(BuiltInRoles.ViewerId, AllSagaTypes: true, []),
            new(Guid.NewGuid(), AllSagaTypes: true, []),
            Named(BuiltInRoles.OperatorId),
            Named(BuiltInRoles.AdministratorId, "OrderSaga", " OrderSaga"),
        ];

        var error = await Assert.ThrowsAsync<IdentityValidationException>(() =>
            _context.NewAdministration().UpdateUserAsync(bob.Id, new UserChanges(null, null, grants), Admin, None));

        Assert.Equal(
            ["grants[0].sagaTypes", "grants[1].roleId", "grants[2].roleId", "grants[3].sagaTypes", "grants[4].sagaTypes"],
            error.Errors.Keys.Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Grant_Limits_TwentyPerSubjectAndAHundredSagaTypes()
    {
        var bob = await _context.SeedUserAsync("bob");
        var roles = new List<DashboardRole>();
        for (var i = 0; i < 21; i++)
            roles.Add(await _context.SeedRoleAsync("Role " + i, Permissions.SagasView));
        var administration = _context.NewAdministration();

        var tooMany = await Assert.ThrowsAsync<IdentityValidationException>(() => administration.UpdateUserAsync(
            bob.Id, new UserChanges(null, null, [.. roles.Select(r => AllTypes(r.Id))]), Admin, None));
        var tooManyTypes = await Assert.ThrowsAsync<IdentityValidationException>(() => administration.UpdateUserAsync(
            bob.Id, new UserChanges(null, null, [Named(roles[0].Id, [.. Enumerable.Range(0, 101).Select(i => "Saga" + i)])]), Admin, None));
        var tooLong = await Assert.ThrowsAsync<IdentityValidationException>(() => administration.UpdateUserAsync(
            bob.Id, new UserChanges(null, null, [Named(roles[0].Id, new string('s', 201))]), Admin, None));

        Assert.Equal(["grants"], tooMany.Errors.Keys, StringComparer.Ordinal);
        Assert.Equal(["grants[0].sagaTypes"], tooManyTypes.Errors.Keys, StringComparer.Ordinal);
        Assert.Equal(["grants[0].sagaTypes"], tooLong.Errors.Keys, StringComparer.Ordinal);

        await administration.UpdateUserAsync(bob.Id, new UserChanges(null, null, [.. roles.Take(20).Select(r => AllTypes(r.Id))]), Admin, None);
        await administration.UpdateUserAsync(
            bob.Id, new UserChanges(null, null, [Named(roles[0].Id, [.. Enumerable.Range(0, 100).Select(i => "Saga" + i).Append(new string('s', 200)).Skip(1)])]), Admin, None);
    }

    [Fact]
    public async Task Team_CreateWritesMembersAndGrants_AndNotifiesTheMembers()
    {
        var bob = await _context.SeedUserAsync("bob");
        var carol = await _context.SeedUserAsync("carol");

        var team = await _context.NewAdministration().CreateTeamAsync(
            new TeamDraft(" Payments ", "", [bob.Id, carol.Id], [Named(BuiltInRoles.OperatorId, "PaymentSaga")]), Admin, None);

        var stored = await _context.NewStore().FindTeamAsync(team.Id, None);
        Assert.Equal("Payments", stored!.Name);
        Assert.Null(stored.Description);
        Assert.Equal(Ids(bob.Id, carol.Id), Ids([.. stored.MemberIds]));
        Assert.Equal([Ids(bob.Id, carol.Id)], _context.Observer.Notified);
    }

    [Fact]
    public async Task Team_UpdateNotifiesEveryMemberBeforeAndAfter()
    {
        var bob = await _context.SeedUserAsync("bob");
        var carol = await _context.SeedUserAsync("carol");
        var team = await _context.SeedTeamAsync("Payments", [bob.Id]);

        await _context.NewAdministration().UpdateTeamAsync(team.Id, new TeamDraft("Payments", null, [carol.Id], null), Admin, None);

        Assert.Equal([carol.Id], (await _context.NewStore().FindTeamAsync(team.Id, None))!.MemberIds);
        Assert.Equal([Ids(bob.Id, carol.Id)], _context.Observer.Notified);
    }

    [Fact]
    public async Task Team_DeleteNotifiesItsMembers()
    {
        var bob = await _context.SeedUserAsync("bob");
        var team = await _context.SeedTeamAsync("Payments", [bob.Id], AllTypes(BuiltInRoles.ViewerId));

        await _context.NewAdministration().DeleteTeamAsync(team.Id, Admin, None);

        Assert.Null(await _context.NewStore().FindTeamAsync(team.Id, None));
        Assert.Equal([Ids(bob.Id)], _context.Observer.Notified);
    }

    [Fact]
    public async Task Team_MembersMustBeDistinctUsers_AndNamesUniqueIgnoringCase()
    {
        var bob = await _context.SeedUserAsync("bob");
        await _context.SeedTeamAsync("Payments", []);
        var administration = _context.NewAdministration();

        var members = await Assert.ThrowsAsync<IdentityValidationException>(() =>
            administration.CreateTeamAsync(new TeamDraft("Shipping", null, [bob.Id, Guid.NewGuid(), bob.Id], null), Admin, None));
        var taken = await Assert.ThrowsAsync<IdentityRuleException>(() =>
            administration.CreateTeamAsync(new TeamDraft("PAYMENTS", null, null, null), Admin, None));

        Assert.Equal(["memberIds[1]", "memberIds[2]"], members.Errors.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.Equal(IdentityRuleCodes.NameTaken, taken.Code);
    }

    [Fact]
    public async Task Unknown_TeamsAndRoles_AreNotFound()
    {
        var administration = _context.NewAdministration();

        await Assert.ThrowsAsync<IdentityNotFoundException>(() => administration.UpdateTeamAsync(Guid.NewGuid(), new TeamDraft("x", null, null, null), Admin, None));
        await Assert.ThrowsAsync<IdentityNotFoundException>(() => administration.DeleteTeamAsync(Guid.NewGuid(), Admin, None));
        await Assert.ThrowsAsync<IdentityNotFoundException>(() => administration.UpdateRoleAsync(Guid.NewGuid(), new RoleDraft("x", null, [Permissions.SagasView]), Admin, None));
        await Assert.ThrowsAsync<IdentityNotFoundException>(() => administration.DeleteRoleAsync(Guid.NewGuid(), Admin, None));
        await Assert.ThrowsAsync<IdentityNotFoundException>(() => administration.DeleteUserAsync(Guid.NewGuid(), Admin, None));
    }

    private Task AssertLastAdministratorAsync<T>(Func<AccessAdministrationService, Task<T>> change) =>
        AssertLastAdministratorAsync(async administration =>
        {
            _ = await change(administration);
        });

    /// <summary>The change is refused with <c>last_administrator</c>, and nobody is notified of it.</summary>
    private async Task AssertLastAdministratorAsync(Func<AccessAdministrationService, Task> change)
    {
        var usersBefore = _context.Observer.UserNotifications.Count;
        var allBefore = _context.Observer.AllUsersNotifications;

        var error = await Assert.ThrowsAsync<IdentityRuleException>(() => change(_context.NewAdministration()));

        Assert.Equal(IdentityRuleCodes.LastAdministrator, error.Code);
        Assert.Equal(usersBefore, _context.Observer.UserNotifications.Count);
        Assert.Equal(allBefore, _context.Observer.AllUsersNotifications);
    }
}
