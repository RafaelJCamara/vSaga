using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VSaga.Dashboard.Api.Auth;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Every endpoint states its access, and only the ones that must be reachable before signing in are
/// anonymous: <c>/health</c> for infrastructure probes and, in Development only, the OpenAPI document. An
/// endpoint mapped without saying anything still needs an authenticated caller (the fallback policy).
/// Routing is case-insensitive, so upper-case paths must get the same answers.
/// </summary>
public sealed class EndpointProtectionTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly DashboardApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public void EveryMappedEndpoint_HasExplicitAuthorizationMetadata()
    {
        var unstated = Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null && !e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any())
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.Empty(unstated);
    }

    [Fact]
    public void TheAnonymousEndpoints_AreExactlyHealthAndTheDevelopmentOpenApiDocument()
    {
        var anonymous = Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["/health", "/openapi/{documentName}.json"], anonymous, StringComparer.Ordinal);
    }

    [Fact]
    public void TheFallbackAndDefaultPolicies_RequireAnAuthenticatedCaller()
    {
        using var client = _factory.CreateClient();
        var options = _factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        Assert.IsType<DenyAnonymousAuthorizationRequirement>(Assert.Single(options.FallbackPolicy!.Requirements));
        Assert.IsType<DenyAnonymousAuthorizationRequirement>(Assert.Single(options.DefaultPolicy.Requirements));
    }

    [Theory]
    [InlineData("/api/sagas")]
    [InlineData("/API/SAGAS")]
    [InlineData("/Api/Saga-Types")]
    [InlineData("/api/no-such-endpoint")]
    [InlineData("/no-such-endpoint")]
    public async Task WithoutCredentials_AnyPathButTheAnonymousOnes_Gets401(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/HEALTH")]
    [InlineData("/openapi/v1.json")]
    public async Task WithoutCredentials_TheAnonymousEndpointsAnswer(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/API/SAGAS", HttpStatusCode.OK)]
    [InlineData("/api/no-such-endpoint", HttpStatusCode.NotFound)]
    public async Task WithTheKey_UpperCaseAndUnknownPathsBehaveAsUsual(string path, HttpStatusCode expected)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);

        using var response = await client.GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
    }

    private List<RouteEndpoint> Endpoints()
    {
        using var client = _factory.CreateClient();
        return [.. _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];
    }
}
