using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Api.Hubs;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The API key acts as the role <c>Dashboard:ApiKeyRole</c> names (Viewer by default) for every saga type,
/// never with <c>access.manage</c>; an unknown role authenticates nobody. The key is read from a header,
/// and from the <c>access_token</c> query string only on a hub endpoint.
/// </summary>
public sealed class ApiKeyRoleTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly DashboardApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ByDefault_TheKeyIsAViewerForEverySagaType()
    {
        await using var host = WithApiKeyRole("");
        using var client = host.CreateClient();

        var caller = await AuthenticateWithKeyAsync(host.Services);

        Assert.Equal(CallerKind.ApiKey, caller.Kind);
        Assert.Equal("dashboard:api-key", caller.AuditActor);
        Assert.Equal([Permissions.SagasView, Permissions.SagasData], caller.Access.Permissions);
        Assert.True(caller.Access.HasUnscoped(Permissions.SagasView));
        Assert.False(caller.Access.HasAny(Permissions.SagasRetry));
    }

    [Fact]
    public async Task Operator_ViaTheSetting_CanRetryEverySagaType()
    {
        using var client = _factory.CreateClient();

        var caller = await AuthenticateWithKeyAsync(_factory.Services);

        Assert.True(caller.Access.HasUnscoped(Permissions.SagasRetry));
        Assert.False(caller.Access.HasAny(Permissions.AccessManage));
    }

    [Fact]
    public async Task Administrator_IsStrippedOfAccessManage()
    {
        await using var host = WithApiKeyRole("Administrator");
        using var client = host.CreateClient();

        var caller = await AuthenticateWithKeyAsync(host.Services);

        Assert.Equal([Permissions.SagasView, Permissions.SagasData, Permissions.SagasRetry], caller.Access.Permissions);
        Assert.False(caller.Access.HasAny(Permissions.AccessManage));
    }

    [Fact]
    public async Task ACustomRole_IsReadFromTheStore()
    {
        await using var host = WithApiKeyRole("Auditors");
        using var client = host.CreateClient();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>().CreateRoleAsync(
                new DashboardRole(Guid.NewGuid(), "Auditors", null, IsBuiltIn: false, [Permissions.SagasView]), CancellationToken.None);
        }

        var caller = await AuthenticateWithKeyAsync(host.Services);

        Assert.Equal([Permissions.SagasView], caller.Access.Permissions);
        Assert.True(caller.Access.HasUnscoped(Permissions.SagasView));
    }

    [Fact]
    public async Task AnUnknownRole_Gets401WithTheSharedBody()
    {
        await using var host = WithApiKeyRole("Nobody");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);
        using var anonymous = host.CreateClient();

        using var response = await client.GetAsync("/api/sagas");
        using var withoutKey = await anonymous.GetAsync("/api/sagas");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(await withoutKey.Content.ReadAsStringAsync(), await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/sagas")]
    [InlineData("/API/SAGAS")]
    [InlineData("/api/saga-types")]
    public async Task TheQueryStringKey_IsRefusedOutsideTheHub(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"{path}?access_token={DashboardApiFactory.TestApiKey}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/hubs/saga/negotiate")]
    [InlineData("/HUBS/SAGA/negotiate")]
    public async Task TheQueryStringKey_IsAcceptedOnTheHub_WhateverThePathsCase(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync($"{path}?negotiateVersion=1&access_token={DashboardApiFactory.TestApiKey}", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheHandler_ReadsTheQueryStringOnlyOnAHubEndpoint()
    {
        using var client = _factory.CreateClient();
        var hub = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new HubMetadata(typeof(SagaHub))), "hub");

        var elsewhere = await AuthenticateAsync(_factory.Services, request => request.QueryString = new QueryString("?access_token=" + DashboardApiFactory.TestApiKey));
        var onTheHub = await AuthenticateAsync(
            _factory.Services, request => request.QueryString = new QueryString("?access_token=" + DashboardApiFactory.TestApiKey), hub);

        Assert.False(elsewhere.Result.Succeeded);
        Assert.True(onTheHub.Result.Succeeded);
        Assert.Equal(CallerKind.ApiKey, onTheHub.Caller?.Kind);
    }

    private WebApplicationFactory<Program> WithApiKeyRole(string role) =>
        _factory.WithWebHostBuilder(builder => builder.UseSetting(DashboardSecuritySettings.ApiKeyRoleKey, role));

    private static async Task<CallerAccess> AuthenticateWithKeyAsync(IServiceProvider services)
    {
        var (result, caller) = await AuthenticateAsync(services, request => request.Headers[ApiKeyAuthenticationDefaults.HeaderName] = DashboardApiFactory.TestApiKey);
        Assert.True(result.Succeeded, result.Failure?.Message);
        return Assert.IsType<CallerAccess>(caller);
    }

    /// <summary>
    /// Runs the host's default authentication (the policy scheme) for a request arranged by
    /// <paramref name="arrange"/>, routed to <paramref name="endpoint"/> when given, and returns the outcome
    /// and the caller the scheme resolved.
    /// </summary>
    private static async Task<(AuthenticateResult Result, CallerAccess? Caller)> AuthenticateAsync(
        IServiceProvider services, Action<HttpRequest> arrange, Endpoint? endpoint = null)
    {
        await using var scope = services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        if (endpoint is not null)
            context.SetEndpoint(endpoint);
        arrange(context.Request);

        var result = await context.AuthenticateAsync();
        return (result, context.GetCaller());
    }
}
