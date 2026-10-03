using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using static VSaga.Dashboard.Api.Tests.TestSessions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The session cookie, checked against the store on every request: a revoked session (rotated stamp,
/// disabled or deleted user) and one past its absolute lifetime get the shared 401 and lose the cookie; while
/// the store is not ready the request gets 401 but the cookie is kept. Sliding renewal keeps the sign-in time
/// that bounds the session.
/// </summary>
public sealed class CookieSessionTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly DashboardApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ACurrentSession_IsAuthenticated()
    {
        using var client = _factory.CreateClient();
        var alice = await CreateUserAsync(_factory.Services, "alice", grants: AllTypes(BuiltInRoles.ViewerId));

        using var request = Get("/api/sagas", CookieHeader(_factory.Services, alice, DateTimeOffset.UtcNow));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task ASessionWhoseStampRotated_Gets401AndLosesTheCookie()
    {
        using var client = _factory.CreateClient();
        var alice = await CreateUserAsync(_factory.Services, "alice", grants: AllTypes(BuiltInRoles.ViewerId));

        using var request = Get("/api/sagas", CookieHeader(_factory.Services, alice, DateTimeOffset.UtcNow, stamp: SecurityStamps.New()));
        using var response = await client.SendAsync(request);

        await AssertUnauthorizedAsync(response);
        AssertCookieDeleted(response);
    }

    [Fact]
    public async Task ASessionOfADisabledUser_Gets401()
    {
        using var client = _factory.CreateClient();
        var alice = await CreateUserAsync(_factory.Services, "alice", isEnabled: false, grants: AllTypes(BuiltInRoles.ViewerId));

        using var request = Get("/api/sagas", CookieHeader(_factory.Services, alice, DateTimeOffset.UtcNow));
        using var response = await client.SendAsync(request);

        await AssertUnauthorizedAsync(response);
        AssertCookieDeleted(response);
    }

    [Fact]
    public async Task ASessionPastItsAbsoluteLifetime_Gets401AndLosesTheCookie_HoweverFreshItsTicket()
    {
        using var client = _factory.CreateClient();
        var alice = await CreateUserAsync(_factory.Services, "alice", grants: AllTypes(BuiltInRoles.ViewerId));
        var now = DateTimeOffset.UtcNow;

        using var inside = Get("/api/sagas", CookieHeader(_factory.Services, alice, now.AddHours(-23)));
        using var past = Get("/api/sagas", CookieHeader(_factory.Services, alice, now.AddHours(-24).AddMinutes(-1)));
        using var insideResponse = await client.SendAsync(inside);
        using var pastResponse = await client.SendAsync(past);

        Assert.Equal(HttpStatusCode.OK, insideResponse.StatusCode);
        await AssertUnauthorizedAsync(pastResponse);
        AssertCookieDeleted(pastResponse);
    }

    [Fact]
    public async Task SlidingRenewal_KeepsTheSignInTime()
    {
        using var client = _factory.CreateClient();
        var alice = await CreateUserAsync(_factory.Services, "alice", grants: AllTypes(BuiltInRoles.ViewerId));
        var now = DateTimeOffset.UtcNow;
        var signedInAt = now.AddHours(-5);
        var expected = new AuthenticationProperties();
        DashboardCookieEvents.SetSignedInAt(expected, signedInAt);

        // More than half of the 480-minute idle window has passed, so the handler renews the ticket.
        using var request = Get(
            "/api/sagas",
            CookieHeader(_factory.Services, alice, signedInAt, issuedUtc: now.AddMinutes(-300), expiresUtc: now.AddMinutes(180)));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var renewed = IssuedTicket(_factory.Services, response);
        Assert.NotNull(renewed);
        Assert.True(renewed.Properties.IssuedUtc > now.AddMinutes(-1), "The ticket was not renewed.");
        Assert.Equal(expected.Items[DashboardCookieEvents.SignedInAtItem], renewed.Properties.Items[DashboardCookieEvents.SignedInAtItem]);
    }

    [Fact]
    public async Task TheAbsoluteLifetime_FollowsTheSetting()
    {
        await using var host = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting(DashboardSecuritySettings.SessionAbsoluteTimeoutHoursKey, "1"));
        using var client = host.CreateClient();
        var alice = await CreateUserAsync(host.Services, "alice", grants: AllTypes(BuiltInRoles.ViewerId));

        using var request = Get("/api/sagas", CookieHeader(host.Services, alice, DateTimeOffset.UtcNow.AddMinutes(-61)));
        using var response = await client.SendAsync(request);

        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task ASessionWithoutASignInTime_Gets401()
    {
        using var client = _factory.CreateClient();
        var alice = await CreateUserAsync(_factory.Services, "alice", grants: AllTypes(BuiltInRoles.ViewerId));

        using var request = Get("/api/sagas", CookieHeader(_factory.Services, alice, signedInAt: null));
        using var response = await client.SendAsync(request);

        await AssertUnauthorizedAsync(response);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARejectedSession_IsSignedOutOnlyWhileTheStoreIsReady(bool storeReady)
    {
        var deleted = await ValidateRejectedSessionAsync(new NobodyResolver(), new Readiness(storeReady));

        Assert.Equal(storeReady, deleted);
    }

    [Fact]
    public async Task ARejectedSession_KeepsTheCookie_WhenTheStoreBecomesReadyWhileItIsResolved()
    {
        var store = new ReadyDuringResolution();

        var deleted = await ValidateRejectedSessionAsync(store, store);

        Assert.True(store.IsReady);
        Assert.False(deleted);
    }

    /// <summary>
    /// SignalR closes a hub socket when the ticket it opened under expires, reading the ticket's <c>ExpiresUtc</c>:
    /// the sliding idle expiry, which can be past the session's absolute lifetime. The expiry the request sees is
    /// capped at the sign-in time plus that lifetime, and nothing else about the ticket changes.
    /// </summary>
    [Fact]
    public async Task TheTicketsExpiry_IsCappedAtTheAbsoluteLifetime()
    {
        var signedInAt = WholeSeconds(DateTimeOffset.UtcNow.AddHours(-23));
        var idleExpiry = WholeSeconds(DateTimeOffset.UtcNow.AddHours(8));

        var validation = await ValidateSessionAsync(signedInAt, idleExpiry);

        Assert.NotNull(validation.Principal);
        Assert.Equal(signedInAt + DashboardSecuritySettings.Default.SessionAbsoluteTimeout, validation.Properties.ExpiresUtc);
        Assert.False(validation.ShouldRenew);
    }

    [Fact]
    public async Task AnExpiryBeforeTheAbsoluteLifetime_IsLeftAlone()
    {
        var idleExpiry = WholeSeconds(DateTimeOffset.UtcNow.AddHours(1));

        var validation = await ValidateSessionAsync(DateTimeOffset.UtcNow, idleExpiry);

        Assert.NotNull(validation.Principal);
        Assert.Equal(idleExpiry, validation.Properties.ExpiresUtc);
    }

    // AuthenticationProperties keeps its dates to the second, so a date compared after a round trip must have no fraction.
    private static DateTimeOffset WholeSeconds(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    /// <summary>Runs the cookie events on a ticket of a session that stands; the context holds the ticket as the events left it.</summary>
    private static async Task<CookieValidatePrincipalContext> ValidateSessionAsync(DateTimeOffset signedInAt, DateTimeOffset expiresUtc)
    {
        var properties = new AuthenticationProperties { IssuedUtc = signedInAt, ExpiresUtc = expiresUtc };
        DashboardCookieEvents.SetSignedInAt(properties, signedInAt);
        var ticket = new AuthenticationTicket(DashboardClaims.ForApiKey(), properties, DashboardAuthExtensions.CookieScheme);
        var scheme = new AuthenticationScheme(DashboardAuthExtensions.CookieScheme, null, typeof(CookieAuthenticationHandler));
        var validation = new CookieValidatePrincipalContext(new DefaultHttpContext(), scheme, new CookieAuthenticationOptions(), ticket);
        var events = new DashboardCookieEvents(
            StubCallerAccessResolver.FullAccess(), new Readiness(true), DashboardSecuritySettings.Default, TimeProvider.System,
            NullLogger<DashboardCookieEvents>.Instance);

        await events.ValidatePrincipal(validation);
        return validation;
    }

    /// <summary>Runs the cookie events on a rejected ticket; true when the session cookie was deleted.</summary>
    private async Task<bool> ValidateRejectedSessionAsync(ICallerAccessResolver resolver, IIdentityReadiness readiness)
    {
        using var client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(DashboardAuthExtensions.CookieScheme);
        var properties = new AuthenticationProperties();
        DashboardCookieEvents.SetSignedInAt(properties, DateTimeOffset.UtcNow);
        var ticket = new AuthenticationTicket(DashboardClaims.ForApiKey(), properties, DashboardAuthExtensions.CookieScheme);
        var scheme = new AuthenticationScheme(DashboardAuthExtensions.CookieScheme, null, typeof(CookieAuthenticationHandler));
        var validation = new CookieValidatePrincipalContext(context, scheme, options, ticket);
        var events = new DashboardCookieEvents(
            resolver, readiness, DashboardSecuritySettings.Default, TimeProvider.System, NullLogger<DashboardCookieEvents>.Instance);

        await events.ValidatePrincipal(validation);

        Assert.Null(validation.Principal);
        return context.Response.Headers.SetCookie.Any(c => c!.StartsWith(options.Cookie.Name + "=;", StringComparison.Ordinal));
    }

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await JsonSerializer.DeserializeAsync<JsonElement>(await response.Content.ReadAsStreamAsync());
        Assert.Equal(AuthProblems.UnauthenticatedCode, problem.GetProperty("code").GetString());
        Assert.Equal(AuthProblems.UnauthorizedDetail, problem.GetProperty("detail").GetString());
    }

    private void AssertCookieDeleted(HttpResponseMessage response)
    {
        var name = CookieName(_factory.Services);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies), "No Set-Cookie header.");
        var deletion = Assert.Single(cookies, c => c.StartsWith(name + "=;", StringComparison.Ordinal));
        Assert.Contains("expires=Thu, 01 Jan 1970", deletion, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Readiness(bool IsReady) : IIdentityReadiness;

    private sealed class NobodyResolver : ICallerAccessResolver
    {
        public Task<CallerAccess?> ResolveAsync(System.Security.Claims.ClaimsPrincipal principal, CancellationToken cancellationToken) =>
            Task.FromResult<CallerAccess?>(null);
    }

    /// <summary>
    /// A store that is not ready when the session is resolved (so the resolver answers null without checking
    /// anything) and becomes ready during that resolution, as start-up recovery can make it.
    /// </summary>
    private sealed class ReadyDuringResolution : ICallerAccessResolver, IIdentityReadiness
    {
        public bool IsReady { get; private set; }

        public Task<CallerAccess?> ResolveAsync(System.Security.Claims.ClaimsPrincipal principal, CancellationToken cancellationToken)
        {
            IsReady = true;
            return Task.FromResult<CallerAccess?>(null);
        }
    }
}
