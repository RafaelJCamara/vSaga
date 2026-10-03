using Microsoft.AspNetCore.Identity;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using static VSaga.Dashboard.Identity.Tests.Services.ServiceTestContext;

namespace VSaga.Dashboard.Identity.Tests.Services;

/// <summary>
/// First-run setup below the HTTP layer (design §8.8): <see cref="FirstAdministratorService.CompleteSetupAsync"/>
/// creates the administrator only when setup is open and the submitted code is its code, and refuses a wrong
/// or missing code before it looks at anything else. The service keeps no attempt count of its own: the
/// per-address window that limits guesses lives in the API's rate limits, so a wrong code here changes nothing
/// and the right one still works afterwards.
/// </summary>
public sealed class FirstAdministratorServiceTests : IAsyncLifetime
{
    private const string Code = "K7QD-M2XH-9TPA-W4RC";
    private const string Password = "first administrator password";
    private const int AccessChangedEvent = 7100;
    private const int AccessChangeRejectedEvent = 7101;

    private static readonly AuditContext Setup = new(FirstAdministratorService.SetupActor, "203.0.113.9");

    private ServiceTestContext _context = null!;
    private FirstRunState _state = null!;
    private FirstAdministratorService _service = null!;

    public async Task InitializeAsync()
    {
        _context = await CreateAsync();
        _state = new FirstRunState();
        _service = NewService(_state);
        await _service.ApplyAtStartAsync(None);
        Assert.True(_state.IsSetupOpen);
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task CompleteSetup_WithTheRightCode_CreatesTheAdministrator_ClosesSetup_AndAuditsIt()
    {
        // Case, spaces and hyphens do not matter: the code is copied by hand from a log line.
        var result = await _service.CompleteSetupAsync("root", " Root User ", Password, " k7qd m2xh-9tpa w4rc ", Setup, None);

        Assert.Equal(SetupStatus.Completed, result.Status);
        Assert.Null(result.Detail);
        var created = result.User!;
        var stored = await _context.ReadUserAsync(created.Id);
        Assert.Equal("root", stored.Username);
        Assert.Equal("Root User", stored.DisplayName);
        Assert.True(stored.IsEnabled);
        Assert.False(stored.MustChangePassword);
        Assert.Equal(T0, stored.LastSignInAtUtc);
        Assert.Equal(PasswordVerificationResult.Success, _context.Hasher.VerifyHashedPassword(stored, stored.PasswordHash, Password));
        var grant = Assert.Single(stored.Grants);
        Assert.Equal(BuiltInRoles.AdministratorId, grant.RoleId);
        Assert.True(grant.AllSagaTypes);
        Assert.False(_state.IsSetupOpen);
        var entry = Assert.Single(_context.Logs.Audit);
        Assert.Equal(AccessChangedEvent, entry.EventId.Id);
        Assert.Equal(FirstAdministratorService.SetupActor, entry.Property("Actor"));
        Assert.Equal(AccessActions.SetupAdministrator, entry.Property("Action"));
        Assert.Equal(created.Id.ToString(), entry.Property("TargetId"));
        Assert.Equal("root", entry.Property("Target"));
        Assert.Equal("succeeded", entry.Property("Outcome"));
        AssertNoSecretLogged(Password, "9TPA", "9tpa");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("K7QD-M2XH-9TPA-W4RD")]
    [InlineData("K7QD-M2XH-9TPA")]
    [InlineData("K7QD-M2XH-9TPA-W4RC-2345")]
    public async Task CompleteSetup_WithoutTheCodeOrWithAWrongOne_IsRefused_CreatesNobody_AndTheRightCodeStillWorks(string? code)
    {
        var result = await _service.CompleteSetupAsync("root", "Root", Password, code, Setup, None);

        Assert.Equal(SetupStatus.WrongCode, result.Status);
        Assert.Null(result.User);
        Assert.Null(result.Detail);
        Assert.Equal(0, await _context.NewStore().CountUsersAsync(None));
        Assert.True(_state.IsSetupOpen);
        var entry = Assert.Single(_context.Logs.Audit);
        Assert.Equal(AccessChangeRejectedEvent, entry.EventId.Id);
        Assert.Equal(AccessActions.SetupAdministrator, entry.Property("Action"));
        Assert.Equal(FirstAdministratorService.SetupActor, entry.Property("Actor"));
        Assert.Equal("203.0.113.9", entry.Property("ClientAddress"));
        Assert.Equal("invalid_credentials", entry.Property("Outcome"));
        AssertNoSecretLogged(Password, "9TPA", "9tpa");

        // No attempt count is spent here: the guess limit is the API's per-address window.
        for (var attempt = 0; attempt < 10; attempt++)
            Assert.Equal(SetupStatus.WrongCode, (await _service.CompleteSetupAsync("root", "Root", Password, code, Setup, None)).Status);
        var right = await _service.CompleteSetupAsync("root", "Root", Password, Code, Setup, None);
        Assert.Equal(SetupStatus.Completed, right.Status);
        Assert.Equal(1, await _context.NewStore().CountUsersAsync(None));
    }

    [Fact]
    public async Task CompleteSetup_ChecksTheCodeBeforeTheBody_SoAWrongCodeNeverLearnsWhatTheBodyGotWrong()
    {
        var wrong = await _service.CompleteSetupAsync("-x", " ", "short", "K7QD-M2XH-9TPA-W4RD", Setup, None);

        Assert.Equal(SetupStatus.WrongCode, wrong.Status);
        Assert.Equal("invalid_credentials", Assert.Single(_context.Logs.Audit).Property("Outcome"));

        // The right code is what makes the same body a validation problem, and that spends nothing either.
        var error = await Assert.ThrowsAsync<IdentityValidationException>(
            () => _service.CompleteSetupAsync("-x", " ", "short", Code, Setup, None));

        Assert.Equal(["displayName", "password", "username"], error.Errors.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.Equal(["invalid_credentials", "validation"], _context.Logs.Audit.Select(e => e.Property("Outcome")), StringComparer.Ordinal);
        Assert.Equal(0, await _context.NewStore().CountUsersAsync(None));
        Assert.True(_state.IsSetupOpen);
    }

    [Fact]
    public async Task CompleteSetup_BeforeStartUpHasOpenedSetup_IsUnavailable_EvenWithTheRightCode()
    {
        var closed = new FirstRunState();

        var result = await NewService(closed).CompleteSetupAsync("root", "Root", Password, Code, Setup, None);

        Assert.Equal(SetupStatus.Unavailable, result.Status);
        Assert.Null(result.User);
        Assert.Equal(closed.SetupUnavailableReason, result.Detail);
        Assert.Equal(0, await _context.NewStore().CountUsersAsync(None));
        Assert.Equal("setup_unavailable", Assert.Single(_context.Logs.Audit).Property("Outcome"));
    }

    [Fact]
    public async Task CompleteSetup_OnceAUserExists_IsUnavailable_AndClosesSetupForGood()
    {
        await _context.SeedAdministratorAsync("alice");

        var result = await _service.CompleteSetupAsync("root", "Root", Password, Code, Setup, None);

        Assert.Equal(SetupStatus.Unavailable, result.Status);
        Assert.Contains("already exists", result.Detail, StringComparison.Ordinal);
        Assert.Equal(1, await _context.NewStore().CountUsersAsync(None));
        Assert.Null(await _context.NewStore().FindUserByNameAsync("root", None));
        Assert.Equal("setup_unavailable", Assert.Single(_context.Logs.Audit).Property("Outcome"));
    }

    private FirstAdministratorService NewService(FirstRunState state) =>
        new(
            _context.NewStore(), _context.Hasher, new PasswordPolicy(_context.Settings),
            new FirstAdministratorSettings(null, null, resetOnStart: false, Code), state, _context.Time, _context.Logs);

    private void AssertNoSecretLogged(params string[] secrets)
    {
        foreach (var secret in secrets)
            Assert.DoesNotContain(_context.Logs.Entries, e => e.Message.Contains(secret, StringComparison.Ordinal));
    }
}
