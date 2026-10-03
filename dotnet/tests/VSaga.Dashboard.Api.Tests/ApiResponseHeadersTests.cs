using System.Net;
using VSaga.Dashboard.Api.Auth;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Every <c>/api</c> response, 401s included, is <c>Cache-Control: no-store</c> and
/// <c>X-Content-Type-Options: nosniff</c>; the API port can be reached without nginx, so the API sets them.
/// X-Frame-Options is left to nginx: the API never sends it, not even when antiforgery issues tokens.
/// </summary>
public sealed class ApiResponseHeadersTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly DashboardApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData("/api/sagas", true, HttpStatusCode.OK)]
    [InlineData("/API/SAGAS", true, HttpStatusCode.OK)]
    [InlineData("/api/saga-types", true, HttpStatusCode.OK)]
    [InlineData("/api/no-such-endpoint", true, HttpStatusCode.NotFound)]
    [InlineData("/api/sagas", false, HttpStatusCode.Unauthorized)]
    [InlineData("/Api/Sagas", false, HttpStatusCode.Unauthorized)]
    public async Task EveryApiResponse_IsNoStoreAndNoSniff(string path, bool withKey, HttpStatusCode expected)
    {
        using var client = _factory.CreateClient();
        if (withKey)
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);

        using var response = await client.GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, $"Cache-Control was '{response.Headers.CacheControl}'.");
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"), StringComparer.Ordinal);
    }

    [Fact]
    public async Task IssuingAntiforgeryTokens_AddsNoXFrameOptions()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/auth/session");

        // The session read issues the tokens, which is when antiforgery would add X-Frame-Options: SAMEORIGIN.
        // Framing is nginx's policy (DENY and frame-ancestors 'none'); a second value from the API conflicts.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(SignInClient.SetCookieHeader(response, AntiforgeryEnforcement.RequestTokenCookieName));
        Assert.False(response.Headers.Contains("X-Frame-Options"));
    }

    [Fact]
    public async Task OtherPaths_AreLeftAlone()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health");

        // The health-check middleware sets its own Cache-Control; nosniff is the middleware's alone.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Content-Type-Options"));
    }
}
