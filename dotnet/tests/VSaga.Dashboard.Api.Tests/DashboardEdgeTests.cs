using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using VSaga.Dashboard.Api.Hosting;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// DashboardEdge's settings parsing, and CORS through the real composition root: off unless
/// Dashboard:WebOrigin names an origin, and then granted to that origin only. The CORS tests set the key
/// with UseSetting, not ConfigureAppConfiguration, because Program.cs reads it while composing (see
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
