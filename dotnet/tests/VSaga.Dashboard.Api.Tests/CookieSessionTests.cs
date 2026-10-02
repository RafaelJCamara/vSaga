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
/// the store is not ready the request gets 401 but the cookie is kept.
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
            new NobodyResolver(), new Readiness(storeReady), DashboardSecuritySettings.Default, TimeProvider.System,
            NullLogger<DashboardCookieEvents>.Instance);

        await events.ValidatePrincipal(validation);

        Assert.Null(validation.Principal);
        Assert.Equal(storeReady, context.Response.Headers.SetCookie.Any(c => c!.StartsWith(options.Cookie.Name + "=;", StringComparison.Ordinal)));
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
}
