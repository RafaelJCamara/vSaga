using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Api.Endpoints;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using static VSaga.Dashboard.Api.Tests.TestSessions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Signing in and out: the session's shapes, the cookie and the antiforgery token login issues, one uniform
/// and slow-enough 401 for every failed sign-in, antiforgery on unsafe requests (bound to the signed-in user,
/// and to this stack's key ring), sign-out, the self-service password change and the limits around both.
/// </summary>
public sealed class AuthEndpointsTests : IAsyncLifetime, IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string NewPassword = "staple battery horse";

    private readonly DashboardApiFactory _factory = new();
    private readonly RecordingObserver _observer = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Session_Anonymous_IsUnauthenticated_IssuesTheTokens_AndIsNotStored()
    {
        using var http = _factory.CreateClient();

        using var response = await http.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await SignInClient.ReadJsonAsync(response);
        Assert.False(session.GetProperty("authenticated").GetBoolean());
        Assert.True(session.GetProperty("setupRequired").GetBoolean());
        Assert.False(session.GetProperty("setupAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("user").ValueKind);
        Assert.Equal(JsonValueKind.Null, session.GetProperty("access").ValueKind);
        Assert.Equal(12, session.GetProperty("passwordMinLength").GetInt32());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var token = SignInClient.SetCookieHeader(response, AntiforgeryEnforcement.RequestTokenCookieName);
        Assert.NotNull(token);
        Assert.DoesNotContain("httponly", token, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", token, StringComparison.OrdinalIgnoreCase);
        var cookieToken = SignInClient.SetCookieHeader(response, "vsaga.session" + AntiforgeryEnforcement.CookieTokenSuffix);
        Assert.NotNull(cookieToken);
        Assert.Contains("httponly", cookieToken, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookieToken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Session_SignedIn_DescribesTheUserAndTheirAccess_UnscopedAndPerSagaType()
    {
        var alice = await CreateUserWithPasswordAsync(
            _factory.Services, "alice", Password, isEnabled: true, lockoutEndUtc: null, AllTypes(BuiltInRoles.ViewerId), ForTypes(BuiltInRoles.OperatorId, "OrderSaga"));
        using var client = await SignInClient.StartAsync(_factory);
        using var login = await client.LoginAsync("alice", Password);

        var session = await client.SessionAsync();

        Assert.True(session.GetProperty("authenticated").GetBoolean());
        Assert.False(session.GetProperty("setupRequired").GetBoolean());
        var user = session.GetProperty("user");
        Assert.Equal(alice.Id, user.GetProperty("id").GetGuid());
        Assert.Equal("alice", user.GetProperty("username").GetString());
        Assert.Equal("alice (display)", user.GetProperty("displayName").GetString());
        Assert.False(user.GetProperty("mustChangePassword").GetBoolean());
        var access = session.GetProperty("access");
        Assert.Equal([Permissions.SagasView, Permissions.SagasData], Strings(access.GetProperty("permissions")));
        var scoped = Assert.Single(access.GetProperty("scoped").EnumerateArray());
        Assert.Equal("OrderSaga", scoped.GetProperty("sagaType").GetString());
        Assert.Equal([Permissions.SagasRetry], Strings(scoped.GetProperty("permissions")));
    }

    [Fact]
    public async Task Session_WithTheApiKey_IsAuthenticatedWithNoUser()
    {
        using var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);

        using var response = await http.GetAsync("/api/auth/session");

        var session = await SignInClient.ReadJsonAsync(response);
        Assert.True(session.GetProperty("authenticated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("user").ValueKind);
        var access = session.GetProperty("access");
        Assert.Equal([Permissions.SagasView, Permissions.SagasData, Permissions.SagasRetry], Strings(access.GetProperty("permissions")));
        Assert.Empty(access.GetProperty("scoped").EnumerateArray());
    }

    [Fact]
    public async Task Session_PasswordMinLength_FollowsTheSetting()
    {
        await using var host = _factory.WithWebHostBuilder(b => b.UseSetting(DashboardSecuritySettings.PasswordMinLengthKey, "16"));
        using var client = await SignInClient.StartAsync(host);

        var session = await client.SessionAsync();

        Assert.Equal(16, session.GetProperty("passwordMinLength").GetInt32());
    }

    [Fact]
    public async Task Login_SetsTheSessionCookieAndANewToken_AndTheSessionWorks()
    {
        await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var client = await SignInClient.StartAsync(_factory);
        var anonymousToken = client.Token;

        using var login = await client.LoginAsync("alice", Password);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True((await SignInClient.ReadJsonAsync(login)).GetProperty("authenticated").GetBoolean());
        var cookie = SignInClient.SetCookieHeader(login, CookieName(_factory.Services));
        Assert.NotNull(cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(client.Token);
        Assert.NotEqual(anonymousToken, client.Token, StringComparer.Ordinal);
        using var sagas = await client.GetAsync("/api/sagas");
        Assert.Equal(HttpStatusCode.OK, sagas.StatusCode);
    }

    [Fact]
    public async Task Login_Failures_AreOneIdenticalBody_ForUnknownWrongLockedDisabledAndOverLong()
    {
        await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        await CreateUserWithPasswordAsync(_factory.Services, "locked", Password, lockoutEndUtc: DateTimeOffset.UtcNow.AddHours(1));
        await CreateUserWithPasswordAsync(_factory.Services, "disabled", Password, isEnabled: false);
        using var client = await SignInClient.StartAsync(_factory);

        var bodies = new List<string>();
        foreach (var (username, password) in new[]
        {
            ("nobody", Password), ("alice", "wrong password!"), ("locked", Password), ("disabled", Password),
            (new string('a', 65), Password), ("alice", new string('p', 129)),
        })
        {
            using var response = await client.LoginAsync(username, password);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Null(SignInClient.SetCookieHeader(response, CookieName(_factory.Services)));
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
        using var problem = JsonDocument.Parse(bodies[0]);
        Assert.Equal(AuthProblems.InvalidCredentialsCode, problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Login_Failures_TakeAtLeastTheFloor()
    {
        using var client = await SignInClient.StartAsync(_factory);

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.LoginAsync("nobody", Password);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(stopwatch.Elapsed >= AuthEndpoints.FailureFloor, $"A failed sign-in answered after {stopwatch.Elapsed.TotalMilliseconds} ms.");
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/API/AUTH/LOGIN")]
    [InlineData("/api/auth/logout")]
    public async Task AnUnsafeRequestWithoutAToken_Is400Antiforgery(string path)
    {
        await CreateUserWithPasswordAsync(_factory.Services, "alice", Password);
        using var client = await SignInClient.StartAsync(_factory);

        using var response = await client.PostAsync(path, """{"username":"alice","password":"correct horse battery"}""", token: "");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, AuthProblems.AntiforgeryCode);
        Assert.Null(SignInClient.SetCookieHeader(response, CookieName(_factory.Services)));
    }

    [Fact]
    public async Task APreLoginToken_IsRejectedAfterLogin_AndTheLoginIssuedOneAccepted()
    {
        await CreateUserWithPasswordAsync(_factory.Services, "alice", Password);
        using var client = await SignInClient.StartAsync(_factory);
        var preLogin = client.Token;
        using var login = await client.LoginAsync("alice", Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var stale = await client.PostAsync("/api/auth/logout", token: preLogin);
        using var current = await client.PostAsync("/api/auth/logout");

        await AssertProblemAsync(stale, HttpStatusCode.BadRequest, AuthProblems.AntiforgeryCode);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [Fact]
    public async Task ATokenFromAnotherStack_IsRejectedWithCodeAntiforgery()
    {
        await using var otherStack = new DashboardApiFactory();
        using var other = await SignInClient.StartAsync(otherStack);
        await CreateUserWithPasswordAsync(_factory.Services, "alice", Password);
        using var client = await SignInClient.StartAsync(_factory);

        // This stack's own antiforgery cookie, the other stack's request token: what one shared XSRF-TOKEN
        // cookie gives a browser with two stacks open on one host.
        using var response = await client.LoginAsync("alice", Password, token: other.Token);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, AuthProblems.AntiforgeryCode);
    }

    [Fact]
    public async Task Logout_EndsTheSession_AndTellsTheObserver()
    {
        await using var host = WithObserver();
        var alice = await CreateUserWithPasswordAsync(host.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var client = await SignInClient.StartAsync(host);
        using var login = await client.LoginAsync("alice", Password);

        using var logout = await client.PostAsync("/api/auth/logout");

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.False((await SignInClient.ReadJsonAsync(logout)).GetProperty("authenticated").GetBoolean());
        Assert.Contains("expires=Thu, 01 Jan 1970", SignInClient.SetCookieHeader(logout, CookieName(host.Services)), StringComparison.OrdinalIgnoreCase);
        Assert.Equal([alice.Id], _observer.Changed);
        using var sagas = await client.GetAsync("/api/sagas");
        Assert.Equal(HttpStatusCode.Unauthorized, sagas.StatusCode);
    }

    [Fact]
    public async Task PasswordChange_EndsTheOtherSession_ReissuesThisOne_AndTellsTheObserver()
    {
        await using var host = WithObserver();
        var alice = await CreateUserWithPasswordAsync(host.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var here = await SignInClient.StartAsync(host);
        using var elsewhere = await SignInClient.StartAsync(host);
        using var loginHere = await here.LoginAsync("alice", Password);
        using var loginElsewhere = await elsewhere.LoginAsync("alice", Password);

        using var change = await here.ChangePasswordAsync(Password, NewPassword);

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.NotNull(SignInClient.SetCookieHeader(change, CookieName(host.Services)));
        Assert.Equal([alice.Id], _observer.Changed);
        Assert.True((await here.SessionAsync()).GetProperty("authenticated").GetBoolean());
        Assert.False((await elsewhere.SessionAsync()).GetProperty("authenticated").GetBoolean());
        using var again = await SignInClient.StartAsync(host);
        using var withNew = await again.LoginAsync("alice", NewPassword);
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
    }

    [Fact]
    public async Task PasswordChange_ToTheCurrentPassword_IsAValidationProblemOnNewPassword()
    {
        await CreateUserWithPasswordAsync(_factory.Services, "alice", Password);
        using var client = await SignInClient.StartAsync(_factory);
        using var login = await client.LoginAsync("alice", Password);

        using var response = await client.ChangePasswordAsync(Password, Password);

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, AuthProblems.ValidationCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("newPassword", out _));
    }

    [Fact]
    public async Task PasswordChange_WrongCurrentPasswords_CountAndLock_AndEndTheSessionAtTheThreshold()
    {
        await using var host = WithObserver(b => b.UseSetting(DashboardSecuritySettings.LockoutMaxFailedAttemptsKey, "3"));
        var alice = await CreateUserWithPasswordAsync(host.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var client = await SignInClient.StartAsync(host);
        using var login = await client.LoginAsync("alice", Password);
        // A copy of the session cookie, as a thief holds it: it never sees the sign-out's deletion.
        var stolen = SignInClient.SetCookie(login, CookieName(host.Services));
        Assert.NotNull(stolen);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var wrong = await client.ChangePasswordAsync("not my password", NewPassword);
            var problem = await AssertProblemAsync(wrong, HttpStatusCode.BadRequest, AuthProblems.InvalidCredentialsCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("currentPassword", out _));
            Assert.True((await client.SessionAsync()).GetProperty("authenticated").GetBoolean());
        }

        using (var beforeLock = await GetWithCookieAsync(host, "/api/sagas", stolen))
            Assert.Equal(HttpStatusCode.OK, beforeLock.StatusCode);
        Assert.Empty(_observer.Changed);

        using var third = await client.ChangePasswordAsync("not my password", NewPassword);

        var locked = await AssertProblemAsync(third, HttpStatusCode.BadRequest, AuthProblems.InvalidCredentialsCode);
        Assert.Contains("not correct", Assert.Single(Strings(locked.GetProperty("errors").GetProperty("currentPassword"))), StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", SignInClient.SetCookieHeader(third, CookieName(host.Services)), StringComparison.OrdinalIgnoreCase);
        Assert.Equal([alice.Id], _observer.Changed);
        Assert.False((await client.SessionAsync()).GetProperty("authenticated").GetBoolean());
        using (var afterLock = await GetWithCookieAsync(host, "/api/sagas", stolen))
            Assert.Equal(HttpStatusCode.Unauthorized, afterLock.StatusCode);
        using var relogin = await client.LoginAsync("alice", Password);
        Assert.Equal(HttpStatusCode.Unauthorized, relogin.StatusCode);
    }

    [Fact]
    public async Task PasswordChange_OnAnAccountLockedByOthers_DoesNotCallThePasswordWrong_NorEndTheSession()
    {
        await using var host = WithObserver(b => b.UseSetting(DashboardSecuritySettings.LockoutMaxFailedAttemptsKey, "2"));
        await CreateUserWithPasswordAsync(host.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var owner = await SignInClient.StartAsync(host);
        using var login = await owner.LoginAsync("alice", Password);
        using var outsider = await SignInClient.StartAsync(host);
        using (var first = await outsider.LoginAsync("alice", "wrong password!"))
        using (var second = await outsider.LoginAsync("alice", "wrong password!"))
            Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);

        using var change = await owner.ChangePasswordAsync(Password, NewPassword);

        var problem = await AssertProblemAsync(change, HttpStatusCode.BadRequest, AuthProblems.InvalidCredentialsCode);
        var message = Assert.Single(Strings(problem.GetProperty("errors").GetProperty("currentPassword")));
        Assert.Contains("locked or disabled", message, StringComparison.Ordinal);
        Assert.DoesNotContain("not correct", message, StringComparison.Ordinal);
        Assert.Null(SignInClient.SetCookieHeader(change, CookieName(host.Services)));
        Assert.Empty(_observer.Changed);
        Assert.True((await owner.SessionAsync()).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task PasswordChange_WithTheApiKey_Is403()
    {
        using var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);

        using var response = await http.PostAsync(
            "/api/auth/password", new StringContent("""{"currentPassword":"a","newPassword":"b"}""", System.Text.Encoding.UTF8, "application/json"));

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, AuthProblems.ForbiddenCode);
    }

    [Fact]
    public async Task Lockout_RefusesTheRightPassword_AfterTheThreshold()
    {
        await using var host = _factory.WithWebHostBuilder(b => b.UseSetting(DashboardSecuritySettings.LockoutMaxFailedAttemptsKey, "2"));
        await CreateUserWithPasswordAsync(host.Services, "alice", Password);
        using var client = await SignInClient.StartAsync(host);

        using var first = await client.LoginAsync("alice", "wrong password!");
        using var second = await client.LoginAsync("alice", "wrong password!");
        using var right = await client.LoginAsync("alice", Password);

        await AssertProblemAsync(right, HttpStatusCode.Unauthorized, AuthProblems.InvalidCredentialsCode);
    }

    [Fact]
    public async Task TooManyAttempts_For_OneUsernameFromOneAddress_Are429WithRetryAfter()
    {
        await using var host = _factory.WithWebHostBuilder(b => b.UseSetting(DashboardSecuritySettings.AuthPerMinuteKey, "2"));
        using var client = await SignInClient.StartAsync(host);

        using var first = await client.LoginAsync("alice", "wrong password!");
        using var second = await client.LoginAsync(" ALICE ", "wrong password!");
        using var third = await client.LoginAsync("alice", "wrong password!");
        using var otherUser = await client.LoginAsync("bob", "wrong password!");

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        await AssertProblemAsync(third, HttpStatusCode.TooManyRequests, AuthProblems.RateLimitedCode);
        var retryAfter = third.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter.Value, TimeSpan.FromSeconds(1), AuthRateLimits.Window);
        Assert.Equal(HttpStatusCode.Unauthorized, otherUser.StatusCode);
    }

    [Fact]
    public async Task TooManyPasswordChanges_ShareTheSignInWindow_AndAre429WithRetryAfter()
    {
        await using var host = _factory.WithWebHostBuilder(b => b.UseSetting(DashboardSecuritySettings.AuthPerMinuteKey, "2"));
        await CreateUserWithPasswordAsync(host.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var client = await SignInClient.StartAsync(host);
        using var login = await client.LoginAsync("alice", Password);

        using var wrong = await client.ChangePasswordAsync("not my password", NewPassword);
        using var limited = await client.ChangePasswordAsync(Password, NewPassword);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await AssertProblemAsync(wrong, HttpStatusCode.BadRequest, AuthProblems.InvalidCredentialsCode);
        await AssertProblemAsync(limited, HttpStatusCode.TooManyRequests, AuthProblems.RateLimitedCode);
        var retryAfter = limited.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter.Value, TimeSpan.FromSeconds(1), AuthRateLimits.Window);
    }

    [Fact]
    public async Task WhileThePasswordHashingQueueIsFull_LoginIs429WithRetryAfter()
    {
        using var limits = new AuthRateLimits(perMinute: 20, hashingPermits: 1);
        await using var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(limits)));
        using var client = await SignInClient.StartAsync(host);
        var running = await limits.AcquireHashingAsync(CancellationToken.None);
        var queued = limits.AcquireHashingAsync(CancellationToken.None).AsTask();

        using (running)
        {
            Assert.True(running.IsAcquired);
            using var response = await client.LoginAsync("alice", Password);

            await AssertProblemAsync(response, HttpStatusCode.TooManyRequests, AuthProblems.RateLimitedCode);
            Assert.Equal(AuthRateLimits.BusyRetryAfter, response.Headers.RetryAfter?.Delta);
        }

        using var released = await queued;
        Assert.True(released.IsAcquired);
    }

    [Fact]
    public async Task WhileThePasswordHashingQueueIsFull_PasswordChangeIs429()
    {
        using var limits = new AuthRateLimits(perMinute: 20, hashingPermits: 1);
        await using var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(limits)));
        await CreateUserWithPasswordAsync(host.Services, "alice", Password);
        using var client = await SignInClient.StartAsync(host);
        using var login = await client.LoginAsync("alice", Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var running = await limits.AcquireHashingAsync(CancellationToken.None);
        var queued = limits.AcquireHashingAsync(CancellationToken.None).AsTask();

        using (running)
        {
            using var response = await client.ChangePasswordAsync(Password, NewPassword);

            await AssertProblemAsync(response, HttpStatusCode.TooManyRequests, AuthProblems.RateLimitedCode);
            Assert.Equal(AuthRateLimits.BusyRetryAfter, response.Headers.RetryAfter?.Delta);
        }

        using var released = await queued;
        Assert.True(released.IsAcquired);
    }

    [Fact]
    public async Task AnUnknownJsonMember_Is400Validation_NamingIt()
    {
        using var client = await SignInClient.StartAsync(_factory);

        using var response = await client.PostAsync("/api/auth/login", """{"username":"alice","password":"x","rememberMe":true}""");

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, AuthProblems.ValidationCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("rememberMe", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABodyOverTheCap_Is400Validation_AndNothingIsHashed(bool declaresItsLength)
    {
        using var limits = new AuthRateLimits(perMinute: 20, hashingPermits: 1);
        await using var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(limits)));
        await CreateUserWithPasswordAsync(host.Services, "alice", Password);
        using var client = await SignInClient.StartAsync(host);
        var json = JsonSerializer.Serialize(new { username = "alice", password = new string('x', AuthEndpoints.MaxRequestBodyBytes) });

        // Every hashing permit and queue slot is taken, so a body that reached the verifier would be 429.
        var running = await limits.AcquireHashingAsync(CancellationToken.None);
        var queued = limits.AcquireHashingAsync(CancellationToken.None).AsTask();
        using (running)
        {
            using var response = await client.PostContentAsync("/api/auth/login", Body(json, declaresItsLength));

            var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, AuthProblems.ValidationCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("request", out _));
        }

        using var released = await queued;
        Assert.True(released.IsAcquired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABodyOfExactlyTheCap_IsRead(bool declaresItsLength)
    {
        await CreateUserWithPasswordAsync(_factory.Services, "alice", Password);
        using var client = await SignInClient.StartAsync(_factory);
        var json = JsonSerializer.Serialize(new { username = "alice", password = Password }).PadRight(AuthEndpoints.MaxRequestBodyBytes);

        using var response = await client.PostContentAsync("/api/auth/login", Body(json, declaresItsLength));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WhileTheIdentityStoreIsNotReady_SessionAndLoginAre503()
    {
        await using var host = _factory.WithWebHostBuilder(b => b
            .UseSetting(DashboardIdentitySettings.RunningInContainerKey, "true")
            .UseSetting(DashboardIdentitySettings.SqlitePathKey, ""));
        using var http = host.CreateClient();

        using var session = await http.GetAsync("/api/auth/session");
        using var login = await http.PostAsync(
            "/api/auth/login", new StringContent("""{"username":"alice","password":"x"}""", System.Text.Encoding.UTF8, "application/json"));

        await AssertProblemAsync(session, HttpStatusCode.ServiceUnavailable, AuthProblems.IdentityUnavailableCode);
        await AssertProblemAsync(login, HttpStatusCode.ServiceUnavailable, AuthProblems.IdentityUnavailableCode);
    }

    private WebApplicationFactory<Program> WithObserver(Action<IWebHostBuilder>? configure = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IAccessChangeObserver>(_observer));
        });

    /// <summary>A GET from a client that keeps no cookies and sends only <paramref name="sessionCookie"/>.</summary>
    private static async Task<HttpResponseMessage> GetWithCookieAsync(WebApplicationFactory<Program> host, string path, string? sessionCookie)
    {
        using var http = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{CookieName(host.Services)}={sessionCookie}");
        return await http.SendAsync(request);
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await SignInClient.ReadJsonAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }

    private static List<string?> Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString())];

    /// <summary>A UTF-8 JSON body that declares its length, or one sent without a Content-Length (streamed).</summary>
    private static HttpContent Body(string json, bool declaresItsLength)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        HttpContent content = declaresItsLength ? new ByteArrayContent(bytes) : new UnsizedContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class RecordingObserver : IAccessChangeObserver
    {
        private readonly ConcurrentQueue<Guid> _changed = new();

        public IReadOnlyList<Guid> Changed => [.. _changed];

        public Task UsersChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
        {
            foreach (var id in userIds)
                _changed.Enqueue(id);
            return Task.CompletedTask;
        }

        public Task AllUsersChangedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
