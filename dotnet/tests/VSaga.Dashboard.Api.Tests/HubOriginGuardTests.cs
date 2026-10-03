using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Api.Hosting;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The hub's origin guard through the real composition (design §8.6): a hub request is allowed with no
/// <c>Origin</c>, with the request's own <c>{scheme}://{Host}</c> (TestServer's is <c>http://localhost</c>), or
/// with <c>Dashboard:WebOrigin</c> when that is set, the scheme being the one a trusted proxy forwarded;
/// anything else, <c>Origin: null</c> included, is a 403 logged at Warning with what was received and what was
/// expected. The hub is recognised by its endpoint metadata, so the path's case does not matter, and nothing
/// but the hub is guarded. Each request presents the API key in a header, so it would otherwise succeed.
/// </summary>
public sealed class HubOriginGuardTests : IAsyncLifetime, IAsyncDisposable
{
    private const string Negotiate = "/hubs/saga/negotiate?negotiateVersion=1";
    private const string OwnOrigin = "http://localhost";
    private const string AnotherLocalPort = "http://localhost:4300";
    private const string ConfiguredWebOrigin = "http://localhost:4200";
    private const string GuardCategory = "VSaga.Dashboard.Api.Auth.HubOriginGuard";
    private const int RejectedEventId = 7311;

    private readonly DashboardApiFactory _factory = new();
    private readonly AuditLogCapture _logs = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task WithoutAnOrigin_TheHubAnswers()
    {
        await using var host = Host();

        using var response = await NegotiateAsync(host, origin: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FromTheRequestsOwnOrigin_TheHubAnswers()
    {
        await using var host = Host();

        using var response = await NegotiateAsync(host, OwnOrigin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_logs.Logged(GuardCategory, RejectedEventId));
    }

    /// <summary>Cookies are not scoped by port, so whatever runs on another port of this host is another site to the hub.</summary>
    [Fact]
    public async Task FromAnotherLocalhostPort_Is403_AndLogsTheReceivedAndExpectedOriginsAtWarning()
    {
        await using var host = Host();

        using var response = await NegotiateAsync(host, AnotherLocalPort);

        var problem = await AdminApi.AssertProblemAsync(response, HttpStatusCode.Forbidden, AuthProblems.ForbiddenCode);
        var detail = problem.GetProperty("detail").GetString();
        Assert.Contains("Dashboard:WebOrigin", detail, StringComparison.Ordinal);
        Assert.EndsWith(AuthProblems.DocumentationPointer, detail, StringComparison.Ordinal);
        var (level, message) = Assert.Single(_logs.Logged(GuardCategory, RejectedEventId));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains($"'{AnotherLocalPort}'", message, StringComparison.Ordinal);
        Assert.Contains($"expected no Origin or {OwnOrigin}.", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The received Origin is the caller's, with no authentication behind it: a refusal logs at most its first
    /// 256 characters, ending in an ellipsis, while the expected value is logged whole.
    /// </summary>
    [Fact]
    public async Task ALongOrigin_IsRefused_AndLoggedCutAt256Characters()
    {
        await using var host = Host();
        var origin = "http://" + new string('a', 1024) + ".example";

        using var response = await NegotiateAsync(host, origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var (_, message) = Assert.Single(_logs.Logged(GuardCategory, RejectedEventId));
        Assert.Contains($"'{origin[..HubOriginGuard.MaxLoggedOriginLength]}\u2026'", message, StringComparison.Ordinal);
        Assert.DoesNotContain(origin[..(HubOriginGuard.MaxLoggedOriginLength + 1)], message, StringComparison.Ordinal);
        Assert.Contains($"expected no Origin or {OwnOrigin}.", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOriginAtTheLimit_IsLoggedWhole()
    {
        await using var host = Host();
        var origin = "http://" + new string('a', HubOriginGuard.MaxLoggedOriginLength - "http://".Length);

        using var response = await NegotiateAsync(host, origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains($"'{origin}'", Assert.Single(_logs.Logged(GuardCategory, RejectedEventId)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FromTheConfiguredWebOrigin_TheHubAnswers_AndAnyOtherOriginIsStill403NamingBoth()
    {
        await using var host = Host(builder => builder.UseSetting(DashboardEdge.WebOriginKey, AnotherLocalPort + "/"));

        using var configured = await NegotiateAsync(host, AnotherLocalPort);
        using var other = await NegotiateAsync(host, "http://localhost:4400");

        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        var (_, message) = Assert.Single(_logs.Logged(GuardCategory, RejectedEventId));
        Assert.Contains($"expected no Origin or {OwnOrigin} or {AnotherLocalPort}.", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Behind a TLS-terminating proxy the browser's origin is https while the API is reached over http: the
    /// scheme a trusted proxy forwards is the one compared, and only a trusted proxy's is believed.
    /// </summary>
    [Fact]
    public async Task HttpsForwardedByATrustedProxy_IsTheSchemeCompared()
    {
        await using var host = Host(builder => builder
            .UseSetting(DashboardEdge.TrustedProxiesKey, "10.0.0.0/8")
            .ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new PeerAddress(IPAddress.Parse("10.1.2.3")))));

        using var https = await NegotiateAsync(host, "https://localhost", forwardedProto: "https");
        using var http = await NegotiateAsync(host, OwnOrigin, forwardedProto: "https");

        Assert.Equal(HttpStatusCode.OK, https.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, http.StatusCode);
    }

    [Fact]
    public async Task HttpsForwardedByAnUntrustedPeer_IsIgnored()
    {
        await using var host = Host(builder => builder
            .UseSetting(DashboardEdge.TrustedProxiesKey, "10.0.0.0/8")
            .ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new PeerAddress(IPAddress.Parse("192.0.2.10")))));

        using var https = await NegotiateAsync(host, "https://localhost", forwardedProto: "https");

        Assert.Equal(HttpStatusCode.Forbidden, https.StatusCode);
    }

    /// <summary>Sandboxed frames and some redirects send the literal origin "null", which matches no page of the dashboard.</summary>
    [Fact]
    public async Task OriginNull_Is403()
    {
        await using var host = Host(builder => builder.UseSetting(DashboardEdge.WebOriginKey, AnotherLocalPort));

        using var response = await NegotiateAsync(host, "null");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("'null'", Assert.Single(_logs.Logged(GuardCategory, RejectedEventId)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/HUBS/SAGA/NEGOTIATE?negotiateVersion=1", AnotherLocalPort, HttpStatusCode.Forbidden)]
    [InlineData("/Hubs/Saga/negotiate?negotiateVersion=1", AnotherLocalPort, HttpStatusCode.Forbidden)]
    [InlineData("/HUBS/SAGA/NEGOTIATE?negotiateVersion=1", OwnOrigin, HttpStatusCode.OK)]
    public async Task AnUpperCasePath_IsGuardedTheSame(string path, string origin, HttpStatusCode expected)
    {
        await using var host = Host();

        using var response = await NegotiateAsync(host, origin, path: path);

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>
    /// A browser opens the WebSocket itself, skipping negotiate (the SPA's client may be configured so), and
    /// always sends its page's Origin on the upgrade: the guard refuses that request too, before the socket
    /// exists. Under TestServer a refused upgrade surfaces as an incomplete handshake naming the status.
    /// </summary>
    [Fact]
    public async Task ADirectWebSocketUpgrade_FromAnotherOrigin_Is403_AndLogsOneWarning()
    {
        await using var host = Host();
        var client = host.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["Origin"] = AnotherLocalPort;
            request.Headers[ApiKeyAuthenticationDefaults.HeaderName] = DashboardApiFactory.TestApiKey;
        };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync(new Uri("ws://localhost/hubs/saga"), CancellationToken.None));

        Assert.Contains("403", refused.Message, StringComparison.Ordinal);
        var (level, message) = Assert.Single(_logs.Logged(GuardCategory, RejectedEventId));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains($"'{AnotherLocalPort}'", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The expected origin is built from the request's <c>Host</c>, which is the caller's as much as the <c>Origin</c>
    /// is: a long one is cut in the log like the received origin, while <c>Dashboard:WebOrigin</c>, the operator's own,
    /// is logged whole next to it.
    /// </summary>
    [Fact]
    public async Task ALongHost_IsLoggedCutAt256Characters_NextToTheWholeConfiguredWebOrigin()
    {
        await using var host = Host(builder => builder.UseSetting(DashboardEdge.WebOriginKey, ConfiguredWebOrigin));
        var ownOrigin = "http://" + new string('h', 1024) + ".example";
        var client = host.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Host = new HostString(ownOrigin["http://".Length..]);
            request.Headers["Origin"] = AnotherLocalPort;
            request.Headers[ApiKeyAuthenticationDefaults.HeaderName] = DashboardApiFactory.TestApiKey;
        };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync(new Uri("ws://localhost/hubs/saga"), CancellationToken.None));

        Assert.Contains("403", refused.Message, StringComparison.Ordinal);
        var (_, message) = Assert.Single(_logs.Logged(GuardCategory, RejectedEventId));
        Assert.Contains($"expected no Origin or {ownOrigin[..HubOriginGuard.MaxLoggedOriginLength]}\u2026 or {ConfiguredWebOrigin}.", message, StringComparison.Ordinal);
        Assert.DoesNotContain(ownOrigin[..(HubOriginGuard.MaxLoggedOriginLength + 1)], message, StringComparison.Ordinal);
    }

    /// <summary>The guard runs before authentication: a page on another origin learns nothing about the session.</summary>
    [Fact]
    public async Task FromAnotherOrigin_WithoutCredentials_Is403NotAChallenge()
    {
        await using var host = Host();

        using var response = await NegotiateAsync(host, AnotherLocalPort, withKey: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Everything but the hub is left to antiforgery and CORS: the guard keys on the hub's endpoint metadata.</summary>
    [Fact]
    public async Task AnEndpointThatIsNotTheHub_IsNotGuarded()
    {
        await using var host = Host();
        using var http = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/sagas");
        request.Headers.Add("Origin", AnotherLocalPort);
        request.Headers.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_logs.Logged(GuardCategory, RejectedEventId));
    }

    [Theory]
    [InlineData("HTTP://LOCALHOST", true)]
    [InlineData("http://localhost/", false)]
    [InlineData("http://localhost:80", false)]
    [InlineData("", false)]
    public void IsAllowed_ComparesTheWholeOriginIgnoringCaseOnly(string origin, bool expected) =>
        Assert.Equal(expected, HubOriginGuard.IsAllowed(new StringValues(origin), OwnOrigin, webOrigin: null));

    [Fact]
    public void IsAllowed_RefusesSeveralOriginHeaders_EvenWhenEachWouldPass() =>
        Assert.False(HubOriginGuard.IsAllowed(new StringValues([OwnOrigin, OwnOrigin]), OwnOrigin, webOrigin: null));

    private WebApplicationFactory<Program> Host(Action<IWebHostBuilder>? configure = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
            configure?.Invoke(builder);
        });

    private static async Task<HttpResponseMessage> NegotiateAsync(
        WebApplicationFactory<Program> host, string? origin, string path = Negotiate, string? forwardedProto = null, bool withKey = true)
    {
        using var http = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (withKey)
            request.Headers.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        if (forwardedProto is not null)
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);

        return await http.SendAsync(request);
    }

    /// <summary>Sets the connection's peer address before anything else runs: TestServer has no socket to report one.</summary>
    private sealed class PeerAddress(IPAddress peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = peer;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
