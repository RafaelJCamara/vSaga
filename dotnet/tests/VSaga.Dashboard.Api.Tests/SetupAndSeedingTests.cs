using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;
using static VSaga.Dashboard.Api.Tests.TestSessions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The first administrator (design §8.8): seeded from <c>Dashboard:Admin:*</c>, failing closed when the seed
/// cannot be applied, reset by <c>Dashboard:Admin:ResetOnStart</c>, or claimed through first-run setup with a
/// one-time code the API logs at start or <c>Dashboard:Setup:Code</c> presets, with no time window.
/// </summary>
public sealed partial class SetupAndSeedingTests : IAsyncLifetime, IAsyncDisposable
{
    private const string PresetCode = "TEST-CODE-2345-6789-WXYZ";
    private const string AdminPassword = "seeded admin password";
    private const string SetupPassword = "first administrator password";
    private const int SetupOpenEvent = 7210;
    private const int SetupOpenWithPresetCodeEvent = 7211;
    private const int SeededEvent = 7212;
    private const int SeedNotAppliedEvent = 7213;
    private const int ResetOnStartEvent = 7214;
    private const int AccessChangedEvent = 7100;
    private const int AccessChangeRejectedEvent = 7101;
    private const int SignedInEvent = 7110;

    private static readonly string[] AllPermissions = [.. Permissions.All.Select(p => p.Key)];

    private readonly DashboardApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Setup_WithTheCode_CreatesAnAdministratorForAllSagaTypes_SignsIn_AndThenIsClosed()
    {
        const string changedPassword = "a changed administrator password";
        var logs = new LogCapture();
        await using var host = Host(logs, (FirstAdministratorSettings.SetupCodeKey, PresetCode));
        using var client = await SignInClient.StartAsync(host);
        var before = await client.SessionAsync();
        Assert.True(before.GetProperty("setupRequired").GetBoolean());
        Assert.True(before.GetProperty("setupAvailable").GetBoolean());

        // Case, spaces and hyphens do not matter: the code is copied by hand from a log line.
        using var setup = await PostSetupAsync(client, "test code 2345 6789 wxyz");

        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var session = await SignInClient.ReadJsonAsync(setup);
        Assert.True(session.GetProperty("authenticated").GetBoolean());
        Assert.False(session.GetProperty("setupRequired").GetBoolean());
        Assert.False(session.GetProperty("setupAvailable").GetBoolean());
        Assert.Equal("root", session.GetProperty("user").GetProperty("username").GetString());
        Assert.False(session.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(AllPermissions, Strings(session.GetProperty("access").GetProperty("permissions")));
        Assert.NotNull(SignInClient.SetCookie(setup, CookieName(host.Services)));
        Assert.NotNull(SignInClient.SetCookie(setup, AntiforgeryEnforcement.RequestTokenCookieName));
        using var sagas = await client.GetAsync("/api/sagas");
        Assert.Equal(HttpStatusCode.OK, sagas.StatusCode);
        var stored = await FindUserAsync(host, "root");
        Assert.Equal("Root", stored!.DisplayName);
        Assert.False(stored.MustChangePassword);
        Assert.NotNull(stored.LastSignInAtUtc);
        var grant = Assert.Single(stored.Grants);
        Assert.Equal(BuiltInRoles.AdministratorId, grant.RoleId);
        Assert.True(grant.AllSagaTypes);

        // The token issued with the session is bound to the new administrator: an unsafe request with it passes.
        using var changed = await client.ChangePasswordAsync(SetupPassword, changedPassword);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        using var again = await PostSetupAsync(client, PresetCode, username: "second");

        await AssertSetupUnavailableAsync(again, "already exists");
        Assert.Null(await FindUserAsync(host, "second"));
        Assert.Contains(logs.Entries, e => IsAudit(e, AccessChangedEvent, AccessActions.SetupAdministrator, FirstAdministratorService.SetupActor, "'root'"));
        Assert.Contains(logs.Entries, e => e.EventId == SignedInEvent && e.Message.Contains("dashboard:root", StringComparison.Ordinal));
        AssertNoSecretLogged(logs, SetupPassword, changedPassword, "2345-6789", "2345 6789");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TEST-CODE-2345-6789-WXYA")]
    public async Task Setup_WithoutTheCodeOrWithAWrongOne_IsRefused_AndCreatesNobody(string? code)
    {
        var logs = new LogCapture();
        await using var host = Host(logs, (FirstAdministratorSettings.SetupCodeKey, PresetCode));
        using var client = await SignInClient.StartAsync(host);

        using var response = code is null
            ? await client.PostAsync("/api/auth/setup", JsonSerializer.Serialize(new { username = "root", displayName = "Root", password = SetupPassword }))
            : await PostSetupAsync(client, code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await SignInClient.ReadJsonAsync(response);
        Assert.Equal(AuthProblems.InvalidCredentialsCode, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("code", out _));
        Assert.Null(await FindUserAsync(host, "root"));
        Assert.True((await client.SessionAsync()).GetProperty("setupAvailable").GetBoolean());
        Assert.Contains(logs.Entries, e => IsAudit(e, AccessChangeRejectedEvent, AccessActions.SetupAdministrator, FirstAdministratorService.SetupActor, AuthProblems.InvalidCredentialsCode));
        Assert.DoesNotContain(logs.Entries, e => e.EventId == AccessChangedEvent);
        AssertNoSecretLogged(logs, SetupPassword, "2345-6789", "WXYA");
    }

    [Fact]
    public async Task Setup_WithTheCodeAndAnUnknownMember_Is400NamingIt_CreatesNobody_AndKeepsTheCodeUsable()
    {
        await using var host = Host(null, (FirstAdministratorSettings.SetupCodeKey, PresetCode));
        using var client = await SignInClient.StartAsync(host);

        using var response = await client.PostAsync(
            "/api/auth/setup",
            JsonSerializer.Serialize(new { username = "root", displayName = "Root", password = SetupPassword, code = PresetCode, rememberMe = true }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await SignInClient.ReadJsonAsync(response);
        Assert.Equal(AuthProblems.ValidationCode, problem.GetProperty("code").GetString());
        Assert.Equal(["rememberMe"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name), StringComparer.Ordinal);
        Assert.Null(await FindUserAsync(host, "root"));
        Assert.True((await client.SessionAsync()).GetProperty("setupAvailable").GetBoolean());

        // The refusal spent nothing: the same body without the stray member still claims the dashboard.
        using var retry = await PostSetupAsync(client, PresetCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.NotNull(await FindUserAsync(host, "root"));
    }

    [Fact]
    public async Task Setup_IsRateLimitedPerClientAddress_BeforeTheCodeIsChecked()
    {
        await using var host = Host(null, (FirstAdministratorSettings.SetupCodeKey, PresetCode), (DashboardSecuritySettings.AuthPerMinuteKey, "2"));
        using var client = await SignInClient.StartAsync(host);

        using var first = await PostSetupAsync(client, "WRONG-CODE-AAAA-BBBB");
        using var second = await PostSetupAsync(client, "WRONG-CODE-CCCC-DDDD");
        using var third = await PostSetupAsync(client, PresetCode);

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.True(third.Headers.Contains("Retry-After"));
        Assert.Equal(AuthProblems.RateLimitedCode, (await SignInClient.ReadJsonAsync(third)).GetProperty("code").GetString());
        Assert.Null(await FindUserAsync(host, "root"));
    }

    [Fact]
    public async Task Setup_OnceAUserExistsHoweverItGotThere_Is409_AndTheCodeNeverWorksAgain()
    {
        var logs = new LogCapture();
        await using var host = Host(logs, (FirstAdministratorSettings.SetupCodeKey, PresetCode));
        using var client = await SignInClient.StartAsync(host);
        await CreateUserWithPasswordAsync(host.Services, "alice", SetupPassword, grants: AllTypes(BuiltInRoles.AdministratorId));

        using var response = await PostSetupAsync(client, PresetCode);

        await AssertSetupUnavailableAsync(response, "already exists");
        Assert.False(host.Services.GetRequiredService<FirstRunState>().IsSetupOpen);
        Assert.Null(await FindUserAsync(host, "root"));
        Assert.Contains(logs.Entries, e => IsAudit(e, AccessChangeRejectedEvent, AccessActions.SetupAdministrator, FirstAdministratorService.SetupActor, AuthProblems.SetupUnavailableCode));
    }

    [Fact]
    public async Task Setup_WithSeedKeysSet_Is409_EvenWithAPresetCode()
    {
        await using var host = Host(
            null,
            (FirstAdministratorSettings.UsernameKey, "admin"),
            (FirstAdministratorSettings.PasswordKey, AdminPassword),
            (FirstAdministratorSettings.SetupCodeKey, PresetCode));
        using var client = await SignInClient.StartAsync(host);

        using var response = await PostSetupAsync(client, PresetCode);

        await AssertSetupUnavailableAsync(response, "already exists");
        Assert.Null(await FindUserAsync(host, "root"));
    }

    [Fact]
    public async Task Seed_CreatesAnAdministratorForAllSagaTypes_WhoSignsInWithoutAForcedChange()
    {
        var logs = new LogCapture();
        await using var host = Host(logs, (FirstAdministratorSettings.UsernameKey, "admin"), (FirstAdministratorSettings.PasswordKey, AdminPassword));
        using var client = await SignInClient.StartAsync(host);

        var anonymous = await client.SessionAsync();
        using var login = await client.LoginAsync("admin", AdminPassword);

        Assert.False(anonymous.GetProperty("setupRequired").GetBoolean());
        Assert.False(anonymous.GetProperty("setupAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, anonymous.GetProperty("setupProblem").ValueKind);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await SignInClient.ReadJsonAsync(login);
        Assert.False(session.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(AllPermissions, Strings(session.GetProperty("access").GetProperty("permissions")));
        var grant = Assert.Single((await FindUserAsync(host, "admin"))!.Grants);
        Assert.Equal(BuiltInRoles.AdministratorId, grant.RoleId);
        Assert.True(grant.AllSagaTypes);
        Assert.Contains(logs.Entries, e => e.EventId == SeededEvent);
        Assert.DoesNotContain(logs.Entries, e => e.EventId is SetupOpenEvent or SetupOpenWithPresetCodeEvent or ResetOnStartEvent);
        Assert.Contains(logs.Entries, e => IsAudit(e, AccessChangedEvent, AccessActions.SeedAdministrator, FirstAdministratorService.ConfigurationActor, "'admin'"));
        AssertNoSecretLogged(logs, AdminPassword);
    }

    [Fact]
    public async Task ResetOnStart_OnAnEmptyStore_CreatesTheSeedUser_AndStillWarnsToSetTheFlagBack()
    {
        var logs = new LogCapture();
        await using var host = Host(
            logs,
            (FirstAdministratorSettings.UsernameKey, "admin"),
            (FirstAdministratorSettings.PasswordKey, AdminPassword),
            (FirstAdministratorSettings.ResetOnStartKey, "true"));
        using var client = await SignInClient.StartAsync(host);

        using var login = await client.LoginAsync("admin", AdminPassword);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var warning = Assert.Single(logs.Entries, e => e.EventId == ResetOnStartEvent);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(FirstAdministratorSettings.ResetOnStartKey, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Entries, e => e.EventId == SeededEvent);
        Assert.Contains(logs.Entries, e => IsAudit(e, AccessChangedEvent, AccessActions.ResetAdministratorOnStart, FirstAdministratorService.ConfigurationActor, "'admin'"));
        AssertNoSecretLogged(logs, AdminPassword);
    }

    [Fact]
    public async Task Seed_IsIgnored_WhenTheStoreAlreadyHasUsers()
    {
        await CreateUserWithPasswordAsync(_factory.Services, "alice", SetupPassword, grants: AllTypes(BuiltInRoles.AdministratorId));
        await using var host = Host(null, (FirstAdministratorSettings.UsernameKey, "admin"), (FirstAdministratorSettings.PasswordKey, AdminPassword));
        using var client = await SignInClient.StartAsync(host);

        using var login = await client.LoginAsync("admin", AdminPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Null(await FindUserAsync(host, "admin"));
        Assert.NotNull(await FindUserAsync(host, "alice"));
        await AssertHealthyAsync(host);
    }

    [Theory]
    [InlineData("admin", "too short", FirstAdministratorSettings.PasswordKey)]
    [InlineData("admin", null, FirstAdministratorSettings.PasswordKey)]
    [InlineData(null, AdminPassword, FirstAdministratorSettings.UsernameKey)]
    [InlineData("api-key", AdminPassword, FirstAdministratorSettings.UsernameKey)]
    [InlineData("x", AdminPassword, FirstAdministratorSettings.UsernameKey)]
    public async Task ASeedThatCannotBeApplied_DoesNotOpenSetup_DegradesHealth_AndSaysWhy(string? username, string? password, string namedKey)
    {
        var logs = new LogCapture();
        var settings = new List<(string, string)> { (FirstAdministratorSettings.SetupCodeKey, PresetCode) };
        if (username is not null)
            settings.Add((FirstAdministratorSettings.UsernameKey, username));
        if (password is not null)
            settings.Add((FirstAdministratorSettings.PasswordKey, password));
        await using var host = Host(logs, [.. settings]);
        using var client = await SignInClient.StartAsync(host);

        var session = await client.SessionAsync();
        using var setup = await PostSetupAsync(client, PresetCode);
        using var http = host.CreateClient();
        using var health = await http.GetAsync("/health");

        Assert.True(session.GetProperty("setupRequired").GetBoolean());
        Assert.False(session.GetProperty("setupAvailable").GetBoolean());
        var problem = session.GetProperty("setupProblem");
        Assert.Equal(AuthProblems.SetupUnavailableCode, problem.GetProperty("code").GetString());
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains(namedKey, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(password ?? AdminPassword, detail, StringComparison.Ordinal);
        await AssertSetupUnavailableAsync(setup, namedKey);
        Assert.Null(await FindUserAsync(host, "root"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var report = await HealthResponse.ReadAsync(health);
        Assert.Equal("degraded", report.Checks["identity"].Status);
        Assert.Contains(namedKey, report.Checks["identity"].Description, StringComparison.Ordinal);
        Assert.Contains(logs.Entries, e => e.EventId == SeedNotAppliedEvent && e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task ResetOnStart_RestoresALockedDisabledDemotedSeedUser_AndEndsItsSessions()
    {
        string oldCookie;
        await using (var first = Host(null, (FirstAdministratorSettings.UsernameKey, "admin"), (FirstAdministratorSettings.PasswordKey, AdminPassword)))
        {
            using var client = await SignInClient.StartAsync(first);
            using var login = await client.LoginAsync("admin", AdminPassword);
            oldCookie = $"{CookieName(first.Services)}={SignInClient.SetCookie(login, CookieName(first.Services))}";
            await DemoteAsync(first, "admin");
        }

        var logs = new LogCapture();
        const string newPassword = "a brand new admin password";
        await using var reset = Host(
            logs,
            (FirstAdministratorSettings.UsernameKey, "admin"),
            (FirstAdministratorSettings.PasswordKey, newPassword),
            (FirstAdministratorSettings.ResetOnStartKey, "true"));
        using var resetClient = await SignInClient.StartAsync(reset);

        using var login2 = await resetClient.LoginAsync("admin", newPassword);
        using var http = reset.CreateClient();
        using var replayed = await http.SendAsync(Get("/api/sagas", oldCookie));

        Assert.Equal(HttpStatusCode.OK, login2.StatusCode);
        var session = await SignInClient.ReadJsonAsync(login2);
        Assert.False(session.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(AllPermissions, Strings(session.GetProperty("access").GetProperty("permissions")));
        var admin = (await FindUserAsync(reset, "admin"))!;
        Assert.True(admin.IsEnabled);
        Assert.Null(admin.LockoutEndUtc);
        Assert.Contains(admin.Grants, g => g.RoleId == BuiltInRoles.AdministratorId && g.AllSagaTypes);
        Assert.DoesNotContain(admin.Grants, g => g.RoleId == BuiltInRoles.AdministratorId && !g.AllSagaTypes);
        Assert.Contains(admin.Grants, g => g.RoleId == BuiltInRoles.ViewerId);

        // Re-enabled, but with a new stamp: a session from before the reset does not come back. This host can
        // read that cookie (the key ring is in the shared store), so the 401 is the stamp's doing.
        var oldTicket = reset.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(DashboardAuthExtensions.CookieScheme).TicketDataFormat.Unprotect(oldCookie.Split('=', 2)[1]);
        Assert.NotNull(oldTicket);
        Assert.NotEqual(admin.SecurityStamp, oldTicket.Principal.FindFirst(DashboardClaims.SecurityStamp)?.Value, StringComparer.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Contains(logs.Entries, e => e.EventId == ResetOnStartEvent && e.Level == LogLevel.Warning);
        Assert.Contains(logs.Entries, e => IsAudit(e, AccessChangedEvent, AccessActions.ResetAdministratorOnStart, FirstAdministratorService.ConfigurationActor, "'admin'"));
        AssertNoSecretLogged(logs, newPassword);
        await AssertHealthyAsync(reset);
    }

    [Fact]
    public async Task ResetOnStart_CreatesTheSeedUser_WhenItIsMissing()
    {
        await CreateUserWithPasswordAsync(_factory.Services, "alice", SetupPassword, grants: AllTypes(BuiltInRoles.AdministratorId));
        await using var host = Host(
            null,
            (FirstAdministratorSettings.UsernameKey, "admin"),
            (FirstAdministratorSettings.PasswordKey, AdminPassword),
            (FirstAdministratorSettings.ResetOnStartKey, "true"));
        using var client = await SignInClient.StartAsync(host);

        using var login = await client.LoginAsync("admin", AdminPassword);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(AllPermissions, Strings((await SignInClient.ReadJsonAsync(login)).GetProperty("access").GetProperty("permissions")));
        Assert.NotNull(await FindUserAsync(host, "alice"));
    }

    [Fact]
    public async Task ResetOnStart_WithAPasswordThePolicyRejects_ResetsNothing_AndDegradesHealth()
    {
        await using (var first = Host(null, (FirstAdministratorSettings.UsernameKey, "admin"), (FirstAdministratorSettings.PasswordKey, AdminPassword)))
        {
            using var client = await SignInClient.StartAsync(first);
        }

        await using var reset = Host(
            null,
            (FirstAdministratorSettings.UsernameKey, "admin"),
            (FirstAdministratorSettings.PasswordKey, "too short"),
            (FirstAdministratorSettings.ResetOnStartKey, "true"));
        using var resetClient = await SignInClient.StartAsync(reset);
        using var http = reset.CreateClient();

        using var login = await resetClient.LoginAsync("admin", AdminPassword);
        using var health = await http.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var identity = (await HealthResponse.ReadAsync(health)).Checks["identity"];
        Assert.Equal("degraded", identity.Status);
        Assert.Contains(FirstAdministratorSettings.ResetOnStartKey, identity.Description, StringComparison.Ordinal);
        Assert.Contains(FirstAdministratorSettings.PasswordKey, identity.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGeneratedCode_IsLoggedOnceAtWarning_AndCompletesSetup()
    {
        var logs = new LogCapture();
        await using var host = Host(logs);
        using var client = await SignInClient.StartAsync(host);
        using var http = host.CreateClient();
        using var health = await http.GetAsync("/health");

        var entry = Assert.Single(logs.Entries, e => e.EventId == SetupOpenEvent);
        Assert.Equal(LogLevel.Warning, entry.Level);
        var code = GeneratedCode().Match(entry.Message);
        Assert.True(code.Success, entry.Message);

        using var setup = await PostSetupAsync(client, code.Value);

        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.NotNull(await FindUserAsync(host, "root"));
    }

    [Fact]
    public async Task APresetCode_ReplacesTheGeneratedOne_AndIsNeverLogged()
    {
        var logs = new LogCapture();
        await using var host = Host(logs, (FirstAdministratorSettings.SetupCodeKey, PresetCode));
        using var client = await SignInClient.StartAsync(host);

        using var setup = await PostSetupAsync(client, PresetCode);

        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.DoesNotContain(logs.Entries, e => e.EventId == SetupOpenEvent);
        Assert.Contains(logs.Entries, e => e.EventId == SetupOpenWithPresetCodeEvent && e.Level == LogLevel.Warning);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("2345-6789", StringComparison.Ordinal));
    }

    [Fact]
    public void APresetCodeShorterThanAGeneratedOne_FailsComposition()
    {
        using var host = Host(null, (FirstAdministratorSettings.SetupCodeKey, "ABCD-EFGH-JKLM"));

        var error = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains(FirstAdministratorSettings.SetupCodeKey, error.Message, StringComparison.Ordinal);
    }

    [GeneratedRegex("[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GeneratedCode();

    private WebApplicationFactory<Program> Host(LogCapture? logs, params (string Key, string Value)[] settings) =>
        _factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            if (logs is not null)
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
        });

    private static Task<HttpResponseMessage> PostSetupAsync(SignInClient client, string code, string username = "root") =>
        client.PostAsync(
            "/api/auth/setup",
            JsonSerializer.Serialize(new { username, displayName = "Root", password = SetupPassword, code }));

    /// <summary>An audit entry with this event id, action and actor whose message also names <paramref name="also"/>.</summary>
    private static bool IsAudit(LogEntry entry, int eventId, string action, string actor, string also) =>
        entry.EventId == eventId
        && entry.Message.Contains($" {action} ", StringComparison.Ordinal)
        && entry.Message.Contains($"Audit: {actor} ", StringComparison.Ordinal)
        && entry.Message.Contains(also, StringComparison.Ordinal);

    private static void AssertNoSecretLogged(LogCapture logs, params string[] secrets)
    {
        foreach (var secret in secrets)
            Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(secret, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task AssertSetupUnavailableAsync(HttpResponseMessage response, string detailContains)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await SignInClient.ReadJsonAsync(response);
        Assert.Equal(AuthProblems.SetupUnavailableCode, problem.GetProperty("code").GetString());
        Assert.Contains(detailContains, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    private static async Task AssertHealthyAsync(WebApplicationFactory<Program> host)
    {
        using var http = host.CreateClient();
        using var health = await http.GetAsync("/health");
        Assert.Equal("healthy", (await HealthResponse.ReadAsync(health)).Checks["identity"].Status);
    }

    private static async Task<DashboardUser?> FindUserAsync(WebApplicationFactory<Program> host, string username)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>().FindUserByNameAsync(username, CancellationToken.None);
    }

    /// <summary>Everything that could keep the seed user out, written straight to the store.</summary>
    private static async Task DemoteAsync(WebApplicationFactory<Program> host, string username)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<DashboardUser>>();
        await using var write = await store.BeginExclusiveAsync(CancellationToken.None);
        var user = (await store.FindUserByNameAsync(username, CancellationToken.None))!;
        await store.UpdateUserAsync(
            user with
            {
                PasswordHash = hasher.HashPassword(user, "a password nobody remembers"),
                IsEnabled = false,
                MustChangePassword = true,
                FailedSignInCount = 3,
                LockoutEndUtc = DateTimeOffset.UtcNow.AddHours(1),
                Grants = [ForTypes(BuiltInRoles.AdministratorId, "OrderSaga"), AllTypes(BuiltInRoles.ViewerId)],
            },
            CancellationToken.None);
        await write.CommitAsync(CancellationToken.None);
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    /// <summary>Every log entry of the host, formatted.</summary>
    private sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
            // Nothing to release; the entries outlive the host for the assertions.
        }

        private sealed class Logger(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner._entries.Enqueue(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, int EventId, string Message);
}
