using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using VSaga.Dashboard.Api.Hosting;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// DashboardEdge's settings parsing; CORS through the real composition root: off unless
/// Dashboard:WebOrigin names an origin, and then granted to that origin only; and forwarded headers through
/// a minimal app (<see cref="EdgeProbe"/>) that sets the connection's peer address itself: honoured only from
/// a trusted proxy, nearest hop only, with a rate-limited Warning for an untrusted peer. The CORS tests set
/// the key with UseSetting, not ConfigureAppConfiguration, because Program.cs reads it while composing (see
/// RedisHealthEndpointTests).
/// </summary>
public sealed class DashboardEdgeTests
{
    private const string ConfiguredOrigin = "https://dashboard.example.test";

    // The origin Program.cs used to fall back to when the key was unset, and the one compose used to set.
    private const string FormerDefaultOrigin = "http://localhost:4200";

    [Fact]
    public void Read_WithNoEdgeKeys_ReturnsNoOriginAndNoProxies()
    {
        var settings = DashboardEdge.Read(ConfigurationWithWebOrigin(webOrigin: null));

        Assert.Null(settings.WebOrigin);
        Assert.Empty(settings.TrustedProxies);
    }

    [Fact]
    public void Read_WithAnEmptyWebOrigin_ReturnsNoOrigin()
    {
        var settings = DashboardEdge.Read(ConfigurationWithWebOrigin(""));

        Assert.Null(settings.WebOrigin);
    }

    [Fact]
    public void Read_WithATrailingSlashOnWebOrigin_NormalisesIt()
    {
        var settings = DashboardEdge.Read(ConfigurationWithWebOrigin("http://localhost:4200/"));

        Assert.Equal("http://localhost:4200", settings.WebOrigin);
    }

    // A browser's Origin header is lower-case and omits a default port; the stored origin must match it
    // as a plain string, because the CORS policy (and later the hub origin check) compares it that way.
    [Theory]
    [InlineData("HTTP://LocalHost:80/", "http://localhost")]
    [InlineData("https://x.example:443", "https://x.example")]
    [InlineData("https://X.Example:8443", "https://x.example:8443")]
    public void Read_WithAnUpperCaseOrDefaultPortWebOrigin_NormalisesToTheBrowserOriginForm(string value, string expected)
    {
        var settings = DashboardEdge.Read(ConfigurationWithWebOrigin(value));

        Assert.Equal(expected, settings.WebOrigin);
    }

    [Theory]
    [InlineData("localhost:4200")]
    [InlineData("http://user@localhost:4200")]
    [InlineData("http://localhost:4200/app")]
    [InlineData("ftp://x")]
    [InlineData("http://localhost:4200/?a=1")]
    [InlineData("http://localhost:4200/#top")]
    [InlineData("*")]
    public void Read_WithAWebOriginThatIsNotAnOrigin_Throws(string value)
    {
        var configuration = ConfigurationWithWebOrigin(value);

        var exception = Assert.Throws<InvalidOperationException>(() => DashboardEdge.Read(configuration));

        Assert.Contains(DashboardEdge.WebOriginKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"'{value}'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_WithAddressesAndNetworks_ParsesBoth()
    {
        var settings = DashboardEdge.Read(ConfigurationWithTrustedProxies(" 10.0.0.5 , 172.16.0.0/12,,2001:db8::1, 2001:db8::/32 "));

        Assert.Equal(
            [
                new IPNetwork(IPAddress.Parse("10.0.0.5"), 32),
                new IPNetwork(IPAddress.Parse("172.16.0.0"), 12),
                new IPNetwork(IPAddress.Parse("2001:db8::1"), 128),
                new IPNetwork(IPAddress.Parse("2001:db8::"), 32),
            ],
            settings.TrustedProxies);
        Assert.Null(settings.WebOrigin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ")]
    public void Read_WithAnEmptyTrustedProxyList_TrustsNoProxy(string value)
    {
        var settings = DashboardEdge.Read(ConfigurationWithTrustedProxies(value));

        Assert.Empty(settings.TrustedProxies);
    }

    // "*" is not a grammar: trust is always an explicit list. "10" and "10.0/8" are address-parser
    // shorthand (0.0.0.10, 10.0.0.0) that an operator never means, and so are a part with leading zeros
    // (read as octal: 10.0.0.010 is 10.0.0.8) and a hex part; "10.0.0.1/8" has host bits set.
    [Theory]
    [InlineData("10.0.0.1,proxy.internal", "proxy.internal")]
    [InlineData("*", "*")]
    [InlineData("10.0.0.0/33", "10.0.0.0/33")]
    [InlineData("10.0.0.1/8", "10.0.0.1/8")]
    [InlineData("10.0.0.0/", "10.0.0.0/")]
    [InlineData("10", "10")]
    [InlineData("10.0/8", "10.0/8")]
    [InlineData("10.0.0.010", "10.0.0.010")]
    [InlineData("192.168.001.010", "192.168.001.010")]
    [InlineData("0x0A.0.0.1/32", "0x0A.0.0.1/32")]
    [InlineData("10.0.0.256", "10.0.0.256")]
    [InlineData("2001:db8::/129", "2001:db8::/129")]
    public void Read_WithAMalformedTrustedProxy_ThrowsNamingTheEntry(string value, string malformedEntry)
    {
        var configuration = ConfigurationWithTrustedProxies(value);

        var exception = Assert.Throws<InvalidOperationException>(() => DashboardEdge.Read(configuration));

        Assert.Contains(DashboardEdge.TrustedProxiesKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"'{malformedEntry}'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForwardedHeaders_FromATrustedProxy_ReplaceSchemeAndRemoteAddress()
    {
        await using var edge = await EdgeProbe.StartAsync(trustedProxies: "10.0.0.0/8");

        var probe = await edge.SendAsync(peer: "10.1.2.3", forwardedFor: "203.0.113.7", forwardedProto: "https");

        Assert.Equal("https 203.0.113.7", probe);
        Assert.Empty(edge.Warnings);
    }

    // A dual-mode Kestrel socket reports an IPv4 peer as IPv4-mapped IPv6; it is still the trusted proxy.
    [Fact]
    public async Task ForwardedHeaders_FromATrustedProxySeenAsIPv4MappedIPv6_ReplaceSchemeAndRemoteAddress()
    {
        await using var edge = await EdgeProbe.StartAsync(trustedProxies: "172.16.0.0/12");

        var probe = await edge.SendAsync(peer: "::ffff:172.18.0.4", forwardedFor: "203.0.113.7", forwardedProto: "https");

        Assert.Equal("https 203.0.113.7", probe);
        Assert.Empty(edge.Warnings);
    }

    // The framework trusts loopback by default: 127.0.0.0/8 through KnownIPNetworks and ::1 through
    // KnownProxies. Neither may be trusted unless configured, so both lists must be cleared.
    [Theory]
    [InlineData("10.0.0.0/8", "192.0.2.10")]
    [InlineData("10.0.0.0/8", "127.0.0.1")]
    [InlineData("10.0.0.0/8", "::1")]
    [InlineData("", "10.1.2.3")]
    public async Task ForwardedHeaders_FromAnUntrustedPeer_AreIgnored(string trustedProxies, string peer)
    {
        await using var edge = await EdgeProbe.StartAsync(trustedProxies);

        var probe = await edge.SendAsync(peer, forwardedFor: "203.0.113.7", forwardedProto: "https");

        Assert.Equal($"http {peer}", probe);
    }

    // Only the right-most value, the one the trusted proxy appended, is honoured; the client wrote the rest.
    // The last hop is itself inside a trusted network on purpose: a second hop would be followed if
    // ForwardLimit allowed it, so this pins the limit rather than the trust list.
    [Fact]
    public async Task ForwardedHeaders_WithSeveralHops_HonourOnlyTheLast()
    {
        await using var edge = await EdgeProbe.StartAsync(trustedProxies: "10.0.0.0/8,198.51.100.0/24");

        var probe = await edge.SendAsync(peer: "10.1.2.3", forwardedFor: "203.0.113.7, 198.51.100.9", forwardedProto: "https, http");

        Assert.Equal("http 198.51.100.9", probe);
    }

    // The second peer arrives first as IPv4-mapped IPv6, as Kestrel reports it, then as plain IPv4: one
    // peer, one warning, named in the IPv4 form an operator would put in the list.
    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("")]
    public async Task ForwardedHeaders_FromAnUntrustedPeer_LogOneWarningPerPeer(string trustedProxies)
    {
        await using var edge = await EdgeProbe.StartAsync(trustedProxies);

        await edge.SendAsync(peer: "192.0.2.10", forwardedFor: "203.0.113.7", forwardedProto: null);
        await edge.SendAsync(peer: "192.0.2.10", forwardedFor: null, forwardedProto: "https");
        await edge.SendAsync(peer: "::ffff:192.0.2.11", forwardedFor: "203.0.113.7", forwardedProto: null);
        await edge.SendAsync(peer: "192.0.2.11", forwardedFor: "203.0.113.7", forwardedProto: null);

        Assert.Collection(
            edge.Warnings,
            first => AssertUntrustedPeerWarning(first, "192.0.2.10"),
            second => AssertUntrustedPeerWarning(second, "192.0.2.11"));
    }

    // The first peer opens the overall window a minute earlier, so the second peer's own window outlives
    // it: the repeat is held back by the per-peer limit, not by the window rollover.
    [Fact]
    public async Task ForwardedHeaders_FromAnUntrustedPeer_WarnAgainOnceTheWindowHasPassed()
    {
        var clock = new FakeTimeProvider();
        await using var edge = await EdgeProbe.StartAsync(trustedProxies: "", clock);

        await edge.SendAsync(peer: "192.0.2.99", forwardedFor: "203.0.113.7", forwardedProto: null);
        clock.Advance(TimeSpan.FromMinutes(1));
        await edge.SendAsync(peer: "192.0.2.10", forwardedFor: "203.0.113.7", forwardedProto: null);
        clock.Advance(UntrustedForwardedHeadersWarning.Window - TimeSpan.FromSeconds(1));
        await edge.SendAsync(peer: "192.0.2.10", forwardedFor: "203.0.113.7", forwardedProto: null);
        clock.Advance(TimeSpan.FromSeconds(1));
        await edge.SendAsync(peer: "192.0.2.10", forwardedFor: "203.0.113.7", forwardedProto: null);

        Assert.Collection(
            edge.Warnings,
            opener => AssertUntrustedPeerWarning(opener, "192.0.2.99"),
            first => AssertUntrustedPeerWarning(first, "192.0.2.10"),
            second => AssertUntrustedPeerWarning(second, "192.0.2.10"));
    }

    // A client rotating its source address (easy within an IPv6 /64) must not get one warning per address:
    // past the overall cap a single line says the rest are suppressed, and warnings resume with the window.
    [Fact]
    public async Task ForwardedHeaders_FromManyUntrustedPeers_StopWarningAtTheCapUntilTheWindowEnds()
    {
        var clock = new FakeTimeProvider();
        await using var edge = await EdgeProbe.StartAsync(trustedProxies: "", clock);
        var cap = UntrustedForwardedHeadersWarning.MaxWarningsPerWindow;

        for (var i = 1; i <= cap + 5; i++)
            await edge.SendAsync(peer: $"2001:db8::{i:x}", forwardedFor: "203.0.113.7", forwardedProto: null);

        var warnings = edge.Warnings;
        Assert.Equal(cap + 1, warnings.Count);
        AssertUntrustedPeerWarning(warnings[cap - 1], $"2001:db8::{cap:x}");
        Assert.Contains("suppressed", warnings[cap], StringComparison.Ordinal);

        clock.Advance(UntrustedForwardedHeadersWarning.Window);
        await edge.SendAsync(peer: "2001:db8::1", forwardedFor: "203.0.113.7", forwardedProto: null);

        Assert.Equal(cap + 2, edge.Warnings.Count);
        AssertUntrustedPeerWarning(edge.Warnings[cap + 1], "2001:db8::1");
    }

    [Fact]
    public async Task Requests_WithoutForwardedHeaders_LogNoWarning()
    {
        await using var edge = await EdgeProbe.StartAsync(trustedProxies: "");

        var probe = await edge.SendAsync(peer: "192.0.2.10", forwardedFor: null, forwardedProto: null);

        Assert.Equal("http 192.0.2.10", probe);
        Assert.Empty(edge.Warnings);
    }

    [Fact]
    public async Task Cors_WhenWebOriginIsUnset_SendsNoCorsHeaders()
    {
        await using var factory = new DashboardApiFactory();
        using var client = factory.CreateClient();

        using var preflightRequest = Preflight(FormerDefaultOrigin);
        using var preflight = await client.SendAsync(preflightRequest);
        using var getRequest = AuthenticatedGet(FormerDefaultOrigin);
        using var get = await client.SendAsync(getRequest);

        AssertNoCorsHeaders(preflight);
        AssertNoCorsHeaders(get);
    }

    [Fact]
    public async Task Cors_ForTheConfiguredOrigin_AllowsItWithCredentials()
    {
        await using var factory = new DashboardApiFactory();
        await using var configured = WithWebOrigin(factory, ConfiguredOrigin + "/");
        using var client = configured.CreateClient();

        using var preflightRequest = Preflight(ConfiguredOrigin);
        using var preflight = await client.SendAsync(preflightRequest);
        using var getRequest = AuthenticatedGet(ConfiguredOrigin);
        using var get = await client.SendAsync(getRequest);

        Assert.Equal(ConfiguredOrigin, Header(preflight, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(preflight, "Access-Control-Allow-Credentials"));
        Assert.Contains("x-api-key", Header(preflight, "Access-Control-Allow-Headers"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConfiguredOrigin, Header(get, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(get, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Cors_ForAnotherOrigin_SendsNoCorsHeaders()
    {
        await using var factory = new DashboardApiFactory();
        await using var configured = WithWebOrigin(factory, ConfiguredOrigin);
        using var client = configured.CreateClient();

        using var preflightRequest = Preflight(FormerDefaultOrigin);
        using var preflight = await client.SendAsync(preflightRequest);
        using var getRequest = AuthenticatedGet(FormerDefaultOrigin);
        using var get = await client.SendAsync(getRequest);

        AssertNoCorsHeaders(preflight);
        AssertNoCorsHeaders(get);
    }

    /// <summary>A configuration holding only <c>Dashboard:WebOrigin</c>, or no key at all when <paramref name="webOrigin"/> is null.</summary>
    private static IConfiguration ConfigurationWithWebOrigin(string? webOrigin)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (webOrigin is not null)
            values[DashboardEdge.WebOriginKey] = webOrigin;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConfiguration ConfigurationWithTrustedProxies(string trustedProxies) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { [DashboardEdge.TrustedProxiesKey] = trustedProxies })
            .Build();

    private static void AssertUntrustedPeerWarning(string message, string peer)
    {
        Assert.Contains($"from {peer},", message, StringComparison.Ordinal);
        Assert.Contains(DashboardEdge.TrustedProxiesKey, message, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> WithWebOrigin(DashboardApiFactory factory, string origin) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting(DashboardEdge.WebOriginKey, origin));

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/sagas");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "x-api-key");
        return request;
    }

    private static HttpRequestMessage AuthenticatedGet(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/sagas");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("X-Api-Key", DashboardApiFactory.TestApiKey);
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static void AssertNoCorsHeaders(HttpResponseMessage response) =>
        Assert.DoesNotContain(response.Headers, header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
}
