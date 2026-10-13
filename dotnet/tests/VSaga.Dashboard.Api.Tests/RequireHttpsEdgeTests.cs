using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Api.Hosting;
using VSaga.Dashboard.Identity;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// What the refusal of plain HTTP under <c>Dashboard:Session:RequireHttps</c> must leave alone, and what it must
/// still refuse: the hub's negotiate and WebSocket upgrade, a request a trusted proxy forwards as https, a CORS
/// preflight, the Development OpenAPI document. The refusal itself is in <see cref="SessionSettingsTests"/>.
/// </summary>
public sealed class RequireHttpsEdgeTests : IAsyncLifetime, IAsyncDisposable
{
    private const string Negotiate = "/hubs/saga/negotiate?negotiateVersion=1";
    private const string WebOrigin = "https://dashboard.example.com";

    private static readonly IPAddress TrustedProxy = IPAddress.Parse("10.1.2.3");

    private readonly DashboardApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task TheHub_OverHttps_NegotiatesAndUpgrades_AndOverPlainHttpIsRefusedBeforeEither()
    {
        using var host = RequireHttps();
        using var https = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://dashboard.example.com") });
        using var plain = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://dashboard.example.com") });

        using var negotiated = await https.SendAsync(Negotiation());
        using var refused = await plain.SendAsync(Negotiation());

        Assert.Equal(HttpStatusCode.OK, negotiated.StatusCode);
        await AdminApi.AssertProblemAsync(refused, HttpStatusCode.BadRequest, AuthProblems.HttpsRequiredCode);

        var token = (await SignInClient.ReadJsonAsync(negotiated)).GetProperty("connectionToken").GetString();
        using var socket = await ConnectAsync(host, $"wss://dashboard.example.com/hubs/saga?id={Uri.EscapeDataString(token!)}");
        Assert.Equal(WebSocketState.Open, socket.State);

        var plainUpgrade = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(host, $"ws://dashboard.example.com/hubs/saga?id={Uri.EscapeDataString(token!)}"));
        Assert.Contains("400", plainUpgrade.Message, StringComparison.Ordinal);
    }

    /// <summary>The origin guard runs before the refusal: a foreign origin is a 403 whether or not the request was plain HTTP.</summary>
    [Fact]
    public async Task AForeignOrigin_OnTheHub_IsStill403_BeforeThePlainHttpRefusal()
    {
        using var host = RequireHttps();
        using var plain = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://dashboard.example.com") });
        using var request = Negotiation();
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await plain.SendAsync(request);

        await AdminApi.AssertProblemAsync(response, HttpStatusCode.Forbidden, AuthProblems.ForbiddenCode);
    }

    /// <summary>
    /// The deployment this setting exists for: the API sees plain HTTP from its proxy, which says https in
    /// <c>X-Forwarded-Proto</c>. Only a trusted peer's header counts.
    /// </summary>
    [Fact]
    public async Task APlainRequest_ForwardedAsHttpsByATrustedProxy_IsServed_ButNotWhenAnyoneElseSaysSo()
    {
        using var viaProxy = RequireHttps(peer: TrustedProxy);
        using var viaStranger = RequireHttps(peer: IPAddress.Parse("192.0.2.10"));

        using var forwarded = await Send(viaProxy, Negotiation("https"));
        using var forwardedSession = await Send(viaProxy, new HttpRequestMessage(HttpMethod.Get, "/api/auth/session") { Headers = { { "X-Forwarded-Proto", "https" } } });
        using var notForwarded = await Send(viaProxy, Negotiation());
        using var forgedByAStranger = await Send(viaStranger, Negotiation("https"));

        Assert.Equal(HttpStatusCode.OK, forwarded.StatusCode);
        Assert.Equal(HttpStatusCode.OK, forwardedSession.StatusCode);
        await AdminApi.AssertProblemAsync(notForwarded, HttpStatusCode.BadRequest, AuthProblems.HttpsRequiredCode);
        await AdminApi.AssertProblemAsync(forgedByAStranger, HttpStatusCode.BadRequest, AuthProblems.HttpsRequiredCode);
    }

    /// <summary>
    /// The CORS middleware runs before the refusal and answers a preflight itself, so the refusal never sees it; a
    /// refused request still carries the CORS headers, so the browser can read the 400 instead of reporting a network error.
    /// </summary>
    [Fact]
    public async Task WithAWebOrigin_APreflightIsAnswered_AndARefusedRequestStillCarriesTheCorsHeaders()
    {
        using var host = RequireHttps(webOrigin: WebOrigin);
        using var plain = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://dashboard.example.com") });

        using var preflight = await plain.SendAsync(Preflight());
        using var refused = await plain.SendAsync(CrossOriginGet());

        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal(WebOrigin, Header(preflight, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(preflight, "Access-Control-Allow-Credentials"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(WebOrigin, Header(refused, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(refused, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task InDevelopment_TheOpenApiDocument_IsServedOverHttps_AndRefusedLikeEveryOtherPathOverPlainHttp()
    {
        using var host = RequireHttps();
        using var https = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://dashboard.example.com") });
        using var plain = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://dashboard.example.com") });

        using var secure = await https.GetAsync("/openapi/v1.json");
        using var refused = await plain.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
        await AdminApi.AssertProblemAsync(refused, HttpStatusCode.BadRequest, AuthProblems.HttpsRequiredCode);
    }

    private WebApplicationFactory<Program> RequireHttps(IPAddress? peer = null, string? webOrigin = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting(DashboardSecuritySettings.RequireHttpsKey, "true");
            builder.UseSetting(DashboardEdge.TrustedProxiesKey, "10.0.0.0/8");
            if (webOrigin is not null)
                builder.UseSetting(DashboardEdge.WebOriginKey, webOrigin);
            if (peer is not null)
                builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new PeerAddress(peer)));
        });

    private static async Task<HttpResponseMessage> Send(WebApplicationFactory<Program> host, HttpRequestMessage request)
    {
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://dashboard.example.com") });
        return await client.SendAsync(request);
    }

    private static Task<WebSocket> ConnectAsync(WebApplicationFactory<Program> host, string url)
    {
        var client = host.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers[ApiKeyAuthenticationDefaults.HeaderName] = DashboardApiFactory.TestApiKey;
        return client.ConnectAsync(new Uri(url), CancellationToken.None);
    }

    private static HttpRequestMessage Negotiation(string? forwardedProto = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Negotiate);
        request.Headers.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);
        if (forwardedProto is not null)
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);

        return request;
    }

    private static HttpRequestMessage Preflight()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/sagas");
        request.Headers.Add("Origin", WebOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "x-api-key");
        return request;
    }

    private static HttpRequestMessage CrossOriginGet()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/sagas");
        request.Headers.Add("Origin", WebOrigin);
        request.Headers.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

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
