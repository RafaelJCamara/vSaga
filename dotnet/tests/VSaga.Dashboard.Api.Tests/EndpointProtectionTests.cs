using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using VSaga.Dashboard.Identity.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VSaga.Dashboard.Api.Auth;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Every endpoint states its access, and only the ones that must be reachable before signing in are
/// anonymous: <c>/health</c> for infrastructure probes, the session, login, logout and first-run setup endpoints, and, in
/// Development only, the OpenAPI document. An
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
    public void TheAnonymousEndpoints_AreExactlyHealthSignInSetupAndTheDevelopmentOpenApiDocument()
    {
        var anonymous = Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText)
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            ["/api/auth/login", "/api/auth/logout", "/api/auth/session", "/api/auth/setup", "/health", "/openapi/{documentName}.json"],
            anonymous,
            StringComparer.Ordinal);
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
    [InlineData("/api/auth/session")]
    [InlineData("/API/AUTH/SESSION")]
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

    [Fact]
    public void EveryEndpointAcceptingAnUnsafeMethod_IsAntiforgeryEnforced_OrIsTheHub()
    {
        var unsafeEndpoints = Endpoints().Where(AcceptsAnUnsafeMethod).ToList();

        var exemptButNotHub = unsafeEndpoints
            .Where(e => e.Metadata.GetMetadata<AntiforgeryExemption>() is not null && e.Metadata.GetMetadata<HubMetadata>() is null)
            .Select(e => e.RoutePattern.RawText);
        var hubNotExempt = Endpoints()
            .Where(e => e.Metadata.GetMetadata<HubMetadata>() is not null && e.Metadata.GetMetadata<AntiforgeryExemption>() is null)
            .Select(e => e.RoutePattern.RawText);

        Assert.Empty(exemptButNotHub);
        Assert.Empty(hubNotExempt);
        Assert.Contains(unsafeEndpoints, e => string.Equals(e.RoutePattern.RawText, "/api/auth/login", StringComparison.Ordinal));
        Assert.Contains(unsafeEndpoints, e => e.Metadata.GetMetadata<HubMetadata>() is not null);
    }

    [Theory]
    [InlineData("/api/sagas/OrderSaga/{0}/retry")]
    [InlineData("/API/SAGAS/OrderSaga/{0}/RETRY")]
    public async Task ASignedInUnsafeRequest_NeedsTheToken_WhateverThePathsCase(string pathFormat)
    {
        const string password = "correct horse battery";
        await TestSessions.CreateUserWithPasswordAsync(
            _factory.Services, "operator", password, grants: TestSessions.AllTypes(BuiltInRoles.OperatorId));
        using var client = await SignInClient.StartAsync(_factory);
        using var login = await client.LoginAsync("operator", password);
        var path = string.Format(CultureInfo.InvariantCulture, pathFormat, Guid.NewGuid());

        using var withoutToken = await client.PostAsync(path, token: "");
        using var withToken = await client.PostAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
        Assert.Equal(AuthProblems.AntiforgeryCode, (await SignInClient.ReadJsonAsync(withoutToken)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, withToken.StatusCode);
    }

    [Fact]
    public async Task TheApiKeyInAHeader_NeedsNoToken()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);

        using var response = await client.PostAsync($"/api/sagas/OrderSaga/{Guid.NewGuid()}/retry", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static bool AcceptsAnUnsafeMethod(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is not { Count: > 0 } methods
        || methods.Any(m => !HttpMethods.IsGet(m) && !HttpMethods.IsHead(m) && !HttpMethods.IsOptions(m) && !HttpMethods.IsTrace(m));

    private List<RouteEndpoint> Endpoints()
    {
        using var client = _factory.CreateClient();
        return [.. _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];
    }
}
