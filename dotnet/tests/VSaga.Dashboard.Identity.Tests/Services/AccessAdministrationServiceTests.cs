using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;
using static VSaga.Dashboard.Identity.Tests.Services.ServiceTestContext;
using static VSaga.Dashboard.Identity.Tests.Services.RecordingAccessChangeObserver;

namespace VSaga.Dashboard.Identity.Tests.Services;

/// <summary>Users: creation, changes, deletion, passwords, stamps, notifications and audit events.</summary>
public sealed class AccessAdministrationServiceTests : IAsyncLifetime
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
    public async Task CreateUser_StoresAValidatedUserWithAHashedPassword()
    {
        var created = await _context.NewAdministration().CreateUserAsync(
            new NewUser("Bob.Smith", "  Bob Smith ", "a long enough secret", MustChangePassword: true, [Named(BuiltInRoles.ViewerId, " OrderSaga ")]),
            Admin, None);

        var stored = await _context.ReadUserAsync(created.Id);
        Assert.Equal("Bob.Smith", stored.Username);
        Assert.Equal("Bob Smith", stored.DisplayName);
        Assert.True(stored.IsEnabled);
        Assert.True(stored.MustChangePassword);
        Assert.Equal(T0, stored.CreatedAtUtc);
        Assert.Matches("^[0-9A-F]{32}$", stored.SecurityStamp);
        Assert.Equal(PasswordVerificationResult.Success, _context.Hasher.VerifyHashedPassword(stored, stored.PasswordHash, "a long enough secret"));
        Assert.Equal(["OrderSaga"], Assert.Single(stored.Grants).SagaTypes);
        Assert.Empty(_context.Observer.UserNotifications);
    }

    [Fact]
    public async Task CreateUser_TakenUsernameIgnoringCase_IsUsernameTaken()
    {
        var error = await Assert.ThrowsAsync<IdentityRuleException>(() => _context.NewAdministration().CreateUserAsync(
            new NewUser("ROOT", "Root again", StrongPassword, MustChangePassword: true, null), Admin, None));

        Assert.Equal(IdentityRuleCodes.UsernameTaken, error.Code);
    }

    [Theory]
    [InlineData("api-key")]
    [InlineData("API-Key")]
    public async Task CreateUser_ReservedUsername_IsAValidationError(string username)
    {
        var error = await Assert.ThrowsAsync<IdentityValidationException>(() => _context.NewAdministration().CreateUserAsync(
            new NewUser(username, "Impostor", StrongPassword, MustChangePassword: true, null), Admin, None));

        Assert.Contains("reserved", Assert.Single(error.Errors["username"]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("-alice")]
    [InlineData(".alice")]
    [InlineData("alice smith")]
    [InlineData("alice!")]
    [InlineData("élodie")]
    [InlineData("")]
    [InlineData(null)]
    public async Task CreateUser_MalformedUsername_IsAValidationError(string? username)
    {
        var error = await Assert.ThrowsAsync<IdentityValidationException>(() => _context.NewAdministration().CreateUserAsync(
            new NewUser(username, "Someone", StrongPassword, MustChangePassword: true, null), Admin, None));

        Assert.True(error.Errors.ContainsKey("username"));
    }

    [Fact]
    public async Task UsernameRule_AcceptsTheWholeAlphabetAndTheLengthBounds()
    {
        Assert.True(AccessValidation.IsValidUsername("a.b_c@d+e-f9"));
        Assert.True(AccessValidation.IsValidUsername("abc"));
        Assert.True(AccessValidation.IsValidUsername(new string('a', 64)));
        Assert.False(AccessValidation.IsValidUsername(new string('a', 65)));
        Assert.False(AccessValidation.IsValidUsername("api-KEY"));

        var created = await _context.NewAdministration().CreateUserAsync(
            new NewUser("9lives", "Nine", StrongPassword, MustChangePassword: false, null), Admin, None);
        Assert.Equal("9lives", created.Username);
    }

    [Fact]
    public async Task CreateUser_ReportsEveryProblemKeyedByRequestPath()
    {
        var error = await Assert.ThrowsAsync<IdentityValidationException>(() => _context.NewAdministration().CreateUserAsync(
            new NewUser("x", " ", "short", MustChangePassword: true, null), Admin, None));

        Assert.Equal(["displayName", "password", "username"], error.Errors.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    [Fact]
    public async Task CreateUser_PasswordEqualToTheUsername_IsRejected()
    {
        var error = await Assert.ThrowsAsync<IdentityValidationException>(() => _context.NewAdministration().CreateUserAsync(
            new NewUser("longusername1", "Long", "LongUsername1", MustChangePassword: true, null), Admin, None));

        Assert.True(error.Errors.ContainsKey("password"));
    }

    [Fact]
    public async Task UpdateUser_NullMembersLeaveFieldsAlone_AndADisplayNameChangeKeepsTheStamp()
    {
        var bob = await _context.SeedUserAsync("bob", grants: AllTypes(BuiltInRoles.ViewerId));

        var updated = await _context.NewAdministration().UpdateUserAsync(bob.Id, new UserChanges("Robert", null, null), Admin, None);

        Assert.Equal("Robert", updated.DisplayName);
        var stored = await _context.ReadUserAsync(bob.Id);
        Assert.Equal("Robert", stored.DisplayName);
        Assert.True(stored.IsEnabled);
        Assert.Equal(bob.SecurityStamp, stored.SecurityStamp);
        Assert.Single(stored.Grants);
        Assert.Empty(_context.Observer.UserNotifications);
    }

    [Fact]
    public async Task UpdateUser_DisableAndEnable_EachRotateTheStampAndNotifyTheUser()
    {
        var bob = await _context.SeedUserAsync("bob");
        var administration = _context.NewAdministration();

        var disabled = await administration.UpdateUserAsync(bob.Id, new UserChanges(null, false, null), Admin, None);
        var enabled = await administration.UpdateUserAsync(bob.Id, new UserChanges(null, true, null), Admin, None);

        Assert.False(disabled.IsEnabled);
        Assert.NotEqual(bob.SecurityStamp, disabled.SecurityStamp, StringComparer.Ordinal);
        Assert.NotEqual(disabled.SecurityStamp, enabled.SecurityStamp, StringComparer.Ordinal);
        Assert.Equal(enabled.SecurityStamp, (await _context.ReadUserAsync(bob.Id)).SecurityStamp);
        Assert.Equal([Ids(bob.Id), Ids(bob.Id)], _context.Observer.Notified);
    }

    [Fact]
    public async Task UpdateUser_GrantsChange_ReplacesThemAndNotifiesTheUser()
    {
        var bob = await _context.SeedUserAsync("bob", grants: AllTypes(BuiltInRoles.ViewerId));

        await _context.NewAdministration().UpdateUserAsync(
            bob.Id, new UserChanges(null, null, [Named(BuiltInRoles.OperatorId, "OrderSaga", "PaymentSaga")]), Admin, None);

        var grant = Assert.Single((await _context.ReadUserAsync(bob.Id)).Grants);
        Assert.Equal(BuiltInRoles.OperatorId, grant.RoleId);
        Assert.Equal(["OrderSaga", "PaymentSaga"], grant.SagaTypes);
        Assert.Equal([Ids(bob.Id)], _context.Observer.Notified);
    }

    [Fact]
    public Task UpdateUser_Unknown_IsNotFound() =>
        Assert.ThrowsAsync<IdentityNotFoundException>(() =>
            _context.NewAdministration().UpdateUserAsync(Guid.NewGuid(), new UserChanges("x", null, null), Admin, None));

    [Fact]
    public async Task DeleteUser_RemovesTheUserAndTheirMemberships_AndNotifiesThem()
    {
        var bob = await _context.SeedUserAsync("bob");
        var team = await _context.SeedTeamAsync("Ops", [bob.Id, _root.Id]);

        await _context.NewAdministration().DeleteUserAsync(bob.Id, Admin, None);

        Assert.Null(await _context.NewStore().FindUserAsync(bob.Id, None));
        Assert.Equal([_root.Id], (await _context.NewStore().FindTeamAsync(team.Id, None))!.MemberIds);
        Assert.Equal([Ids(bob.Id)], _context.Observer.Notified);
    }

    [Fact]
    public async Task ResetPassword_SetsTheNewPassword_RotatesTheStamp_AndNotifies()
    {
        var bob = await _context.SeedUserAsync("bob");

        var reset = await _context.NewAdministration().ResetPasswordAsync(bob.Id, "a brand new secret", mustChangePassword: true, Admin, None);

        var stored = await _context.ReadUserAsync(bob.Id);
        Assert.True(stored.MustChangePassword);
        Assert.NotEqual(bob.SecurityStamp, stored.SecurityStamp, StringComparer.Ordinal);
        Assert.Equal(reset.SecurityStamp, stored.SecurityStamp);
        Assert.Equal(PasswordVerificationResult.Success, _context.Hasher.VerifyHashedPassword(stored, stored.PasswordHash, "a brand new secret"));
        Assert.Equal([Ids(bob.Id)], _context.Observer.Notified);
    }

    [Fact]
    public async Task ResetPassword_WeakPassword_IsAValidationErrorOnNewPassword()
    {
        var bob = await _context.SeedUserAsync("bob");

        var error = await Assert.ThrowsAsync<IdentityValidationException>(() =>
            _context.NewAdministration().ResetPasswordAsync(bob.Id, "short", mustChangePassword: true, Admin, None));

        Assert.True(error.Errors.ContainsKey("newPassword"));
        Assert.Equal(bob.PasswordHash, (await _context.ReadUserAsync(bob.Id)).PasswordHash);
    }

    [Fact]
    public async Task UnlockUser_ClearsTheLockout_KeepsTheStamp_AndNotifiesNobody()
    {
        var bob = await _context.SeedUserAsync("bob");
        await _context.NewStore().RecordFailedSignInAsync(bob.Id, 1, T0, TimeSpan.FromMinutes(15), None);

        await _context.NewAdministration().UnlockUserAsync(bob.Id, Admin, None);

        var stored = await _context.ReadUserAsync(bob.Id);
        Assert.Null(stored.LockoutEndUtc);
        Assert.Equal(0, stored.FailedSignInCount);
        Assert.Equal(bob.SecurityStamp, stored.SecurityStamp);
        Assert.True((await _context.NewVerifier().VerifyAsync("bob", StrongPassword, None)).Succeeded);
        Assert.Empty(_context.Observer.UserNotifications);
    }

    [Fact]
    public async Task ChangeOwnPassword_Success_RotatesTheStampClearsMustChangeAndNotifies()
    {
        var bob = await _context.SeedUserAsync("bob", mustChangePassword: true);

        var result = await _context.NewAdministration().ChangeOwnPasswordAsync(bob.Id, StrongPassword, "my own new secret", Bob(), None);

        Assert.Equal(PasswordChangeStatus.Changed, result.Status);
        var stored = await _context.ReadUserAsync(bob.Id);
        Assert.False(stored.MustChangePassword);
        Assert.NotEqual(bob.SecurityStamp, stored.SecurityStamp, StringComparer.Ordinal);
        Assert.Equal(PasswordVerificationResult.Success, _context.Hasher.VerifyHashedPassword(stored, stored.PasswordHash, "my own new secret"));
        Assert.Equal([Ids(bob.Id)], _context.Observer.Notified);
    }

    [Fact]
    public async Task ChangeOwnPassword_SameAsTheCurrent_IsRejected()
    {
        var bob = await _context.SeedUserAsync("bob", mustChangePassword: true);

        var error = await Assert.ThrowsAsync<IdentityValidationException>(() =>
            _context.NewAdministration().ChangeOwnPasswordAsync(bob.Id, StrongPassword, StrongPassword, Bob(), None));

        Assert.Contains("differ", Assert.Single(error.Errors["newPassword"]), StringComparison.Ordinal);
        Assert.Equal(bob.SecurityStamp, (await _context.ReadUserAsync(bob.Id)).SecurityStamp);
    }

    [Fact]
    public async Task ChangeOwnPassword_WrongCurrentPassword_CountsAndLocksAtTheThreshold_EndingEverySession()
    {
        var bob = await _context.SeedUserAsync("bob");
        var administration = _context.NewAdministration();

        for (var i = 1; i < 5; i++)
        {
            var wrong = await administration.ChangeOwnPasswordAsync(bob.Id, "not my password", "my own new secret", Bob(), None);
            Assert.Equal(PasswordChangeStatus.WrongCurrentPassword, wrong.Status);
            Assert.Null(wrong.LockedUntilUtc);
            var counted = await _context.ReadUserAsync(bob.Id);
            Assert.Equal(i, counted.FailedSignInCount);
            Assert.Equal(bob.SecurityStamp, counted.SecurityStamp);
        }

        Assert.Empty(_context.Observer.UserNotifications);
        var fifth = await administration.ChangeOwnPasswordAsync(bob.Id, "not my password", "my own new secret", Bob(), None);
        var locked = await _context.ReadUserAsync(bob.Id);
        var afterwards = await administration.ChangeOwnPasswordAsync(bob.Id, StrongPassword, "my own new secret", Bob(), None);

        Assert.Equal(PasswordChangeStatus.WrongCurrentPassword, fifth.Status);
        Assert.Equal(T0.AddMinutes(15), fifth.LockedUntilUtc);
        Assert.Equal(T0.AddMinutes(15), locked.LockoutEndUtc);
        Assert.NotEqual(bob.SecurityStamp, locked.SecurityStamp, StringComparer.Ordinal);
        Assert.Equal([Ids(bob.Id)], _context.Observer.Notified);
        Assert.Equal(PasswordChangeStatus.Refused, afterwards.Status);
        var stored = await _context.ReadUserAsync(bob.Id);
        Assert.Equal(bob.PasswordHash, stored.PasswordHash);
        Assert.Equal(locked.SecurityStamp, stored.SecurityStamp);
    }

    [Fact]
    public async Task ChangeOwnPassword_DisabledAccount_IsRefusedWithoutCheckingOrCounting()
    {
        var bob = await _context.SeedUserAsync("bob", isEnabled: false);
        var hasher = new InterleavingHasher(_context.Hasher);

        var result = await _context.NewAdministration(hasher).ChangeOwnPasswordAsync(bob.Id, StrongPassword, "my own new secret", Bob(), None);

        Assert.Equal(PasswordChangeStatus.Refused, result.Status);
        Assert.Equal(0, hasher.Calls);
        var stored = await _context.ReadUserAsync(bob.Id);
        Assert.Equal(0, stored.FailedSignInCount);
        Assert.Equal(bob.PasswordHash, stored.PasswordHash);
        Assert.Equal(bob.SecurityStamp, stored.SecurityStamp);
        Assert.Empty(_context.Observer.UserNotifications);
    }

    /// <summary>An administrator's reset committed after the unlocked check: the current password is checked again under the lock.</summary>
    [Fact]
    public async Task ChangeOwnPassword_AResetCommittedAfterTheCheck_IsCheckedAgainAndKept()
    {
        var bob = await _context.SeedUserAsync("bob");
        var hasher = new InterleavingHasher(_context.Hasher, async () =>
            await _context.NewAdministration().ResetPasswordAsync(bob.Id, "the reset password", mustChangePassword: true, Admin, None));

        var result = await _context.NewAdministration(hasher).ChangeOwnPasswordAsync(bob.Id, StrongPassword, "my own new secret", Bob(), None);

        Assert.Equal(PasswordChangeStatus.WrongCurrentPassword, result.Status);
        Assert.Null(result.LockedUntilUtc);
        var stored = await _context.ReadUserAsync(bob.Id);
        Assert.Equal(PasswordVerificationResult.Success, _context.Hasher.VerifyHashedPassword(stored, stored.PasswordHash, "the reset password"));
        Assert.True(stored.MustChangePassword);
        Assert.Equal(0, stored.FailedSignInCount);
        Assert.Equal([Ids(bob.Id)], _context.Observer.Notified);
    }

    [Fact]
    public async Task ChangeOwnPassword_AnAccountDisabledAfterTheCheck_IsRefused()
    {
        var bob = await _context.SeedUserAsync("bob");
        var hasher = new InterleavingHasher(_context.Hasher, async () =>
            await _context.NewAdministration().UpdateUserAsync(bob.Id, new UserChanges(null, false, null), Admin, None));

        var result = await _context.NewAdministration(hasher).ChangeOwnPasswordAsync(bob.Id, StrongPassword, "my own new secret", Bob(), None);

        Assert.Equal(PasswordChangeStatus.Refused, result.Status);
        Assert.Equal(bob.PasswordHash, (await _context.ReadUserAsync(bob.Id)).PasswordHash);
    }

    [Fact]
    public async Task Audit_ASuccessfulChange_LogsActorActionTargetAndOutcome_AndNoSecret()
    {
        var created = await _context.NewAdministration().CreateUserAsync(
            new NewUser("carol", "Carol", "carol's long secret", MustChangePassword: true, null), Admin, None);

        var entry = Assert.Single(_context.Logs.Audit);
        Assert.Equal(7100, entry.EventId.Id);
        Assert.Equal("AccessChanged", entry.EventId.Name);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("dashboard:root", entry.Property("Actor"));
        Assert.Equal(AccessActions.CreateUser, entry.Property("Action"));
        Assert.Equal("user", entry.Property("TargetKind"));
        Assert.Equal(created.Id.ToString(), entry.Property("TargetId"));
        Assert.Equal("carol", entry.Property("Target"));
        Assert.Equal("203.0.113.7", entry.Property("ClientAddress"));
        Assert.Equal("succeeded", entry.Property("Outcome"));

        var everything = string.Join("\n", _context.Logs.Entries.Select(e => e.Message + " " + string.Join(" ", e.Properties.Values)));
        Assert.DoesNotContain("carol's long secret", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(created.PasswordHash, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(created.SecurityStamp, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_ARejectedChange_LogsTheProblemCode()
    {
        await Assert.ThrowsAsync<IdentityRuleException>(() => _context.NewAdministration().DeleteUserAsync(_root.Id, Admin, None));
        await Assert.ThrowsAsync<IdentityValidationException>(() => _context.NewAdministration().CreateUserAsync(
            new NewUser("api-key", "Impostor", StrongPassword, MustChangePassword: true, null), Admin, None));

        var entries = _context.Logs.Audit.ToList();
        Assert.All(entries, e => Assert.Equal(7101, e.EventId.Id));
        Assert.Equal([IdentityRuleCodes.LastAdministrator, "validation"], entries.Select(e => e.Property("Outcome")), StringComparer.Ordinal);
        Assert.Equal(_root.Id.ToString(), entries[0].Property("TargetId"));
        Assert.DoesNotContain(entries, e => e.Message.Contains("api-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Audit_EveryMutation_EmitsOneEvent()
    {
        var administration = _context.NewAdministration();
        var role = await administration.CreateRoleAsync(new RoleDraft("Auditors", null, [Permissions.SagasView]), Admin, None);
        var user = await administration.CreateUserAsync(new NewUser("dave", "Dave", StrongPassword, true, null), Admin, None);
        await administration.UpdateUserAsync(user.Id, new UserChanges("David", null, null), Admin, None);
        await administration.ResetPasswordAsync(user.Id, "another long secret", true, Admin, None);
        await administration.UnlockUserAsync(user.Id, Admin, None);
        await administration.ChangeOwnPasswordAsync(user.Id, "another long secret", "dave's chosen secret", Admin, None);
        var team = await administration.CreateTeamAsync(new TeamDraft("Audit", null, [user.Id], [AllTypes(role.Id)]), Admin, None);
        await administration.UpdateTeamAsync(team.Id, new TeamDraft("Audit team", null, [user.Id], null), Admin, None);
        await administration.UpdateRoleAsync(role.Id, new RoleDraft("Auditors", "Read only", [Permissions.SagasData]), Admin, None);
        await administration.DeleteTeamAsync(team.Id, Admin, None);
        await administration.DeleteRoleAsync(role.Id, Admin, None);
        await administration.DeleteUserAsync(user.Id, Admin, None);

        Assert.Equal(
            [
                AccessActions.CreateRole, AccessActions.CreateUser, AccessActions.UpdateUser, AccessActions.ResetPassword,
                AccessActions.UnlockUser, AccessActions.ChangeOwnPassword, AccessActions.CreateTeam, AccessActions.UpdateTeam,
                AccessActions.UpdateRole, AccessActions.DeleteTeam, AccessActions.DeleteRole, AccessActions.DeleteUser,
            ],
            _context.Logs.Audit.Select(e => e.Property("Action")),
            StringComparer.Ordinal);
        Assert.All(_context.Logs.Audit, e => Assert.Equal("succeeded", e.Property("Outcome")));
    }

    [Fact]
    public async Task Observer_IsCalledAfterTheCommit()
    {
        var bob = await _context.SeedUserAsync("bob");
        _context.Observer.Observe = async store => "enabled=" + (await store.FindUserAsync(bob.Id, None))!.IsEnabled;

        await _context.NewAdministration().UpdateUserAsync(bob.Id, new UserChanges(null, false, null), Admin, None);

        Assert.Equal(["enabled=False"], _context.Observer.Observations);
    }

    [Fact]
    public async Task Observer_IsNotCalledForARejectedChange()
    {
        await Assert.ThrowsAsync<IdentityRuleException>(() =>
            _context.NewAdministration().UpdateUserAsync(_root.Id, new UserChanges(null, false, null), Admin, None));

        Assert.Empty(_context.Observer.UserNotifications);
        Assert.True((await _context.ReadUserAsync(_root.Id)).IsEnabled);
    }

    [Fact]
    public async Task Observer_AFailureDoesNotUndoTheCommittedChange()
    {
        var bob = await _context.SeedUserAsync("bob");
        var administration = new AccessAdministrationService(
            _context.NewStore(), _context.Hasher, new PasswordPolicy(_context.Settings), new ThrowingObserver(),
            _context.Settings, _context.Time, _context.Logs);

        await administration.UpdateUserAsync(bob.Id, new UserChanges(null, false, null), Admin, None);

        Assert.False((await _context.ReadUserAsync(bob.Id)).IsEnabled);
        Assert.Contains(_context.Logs.Audit, e => e.EventId.Id == 7102);
    }

    private static AuditContext Bob() => new("dashboard:bob", "198.51.100.2");

    private sealed class ThrowingObserver : IAccessChangeObserver
    {
        public Task UsersChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The hub is gone.");

        public Task AllUsersChangedAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The hub is gone.");
    }
}
