using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The 401 and 403 problem bodies, which the SPA and the user guide rely on (each ends with the
/// documentation pointer), and the permission policies the saga and administration endpoints will name.
/// </summary>
public sealed class AuthorizationResponseTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly DashboardApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData(null)]
    [InlineData("not-the-configured-key")]
    public async Task Unauthorized_EndsWithTheDocumentationPointer_AndNamesEveryWayIn(string? apiKey)
    {
        using var client = _factory.CreateClient();
        if (apiKey is not null)
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, apiKey);

        using var response = await client.GetAsync("/api/sagas");
        var problem = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.Equal("unauthenticated", problem.GetProperty("code").GetString());
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.EndsWith("See docs/dashboard.md#authentication.", detail, StringComparison.Ordinal);
        Assert.Contains("/login", detail, StringComparison.Ordinal);
        Assert.Contains("X-Api-Key", detail, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_NamesThePermissionAndTheSagaType_AndEndsWithTheDocumentationPointer()
    {
        var (status, contentType, problem) = await ForbidAsync(Caller(Named(BuiltInRoles.ViewerId, "OrderSaga")), Permissions.SagasRetry, "OrderSaga");

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("application/problem+json", contentType);
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
        Assert.Equal("forbidden", problem.GetProperty("code").GetString());
        Assert.Equal("sagas.retry", problem.GetProperty("permission").GetString());
        Assert.Equal("OrderSaga", problem.GetProperty("sagaType").GetString());
        Assert.Equal(
            "This needs the sagas.retry permission for saga type 'OrderSaga'. See docs/dashboard.md#authentication.",
            problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Forbidden_WithoutASagaType_CarriesANullSagaType()
    {
        var (_, _, problem) = await ForbidAsync(Caller(All(BuiltInRoles.OperatorId)), Permissions.AccessManage, sagaType: null);

        Assert.Equal("access.manage", problem.GetProperty("permission").GetString());
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("sagaType").ValueKind);
        Assert.EndsWith("See docs/dashboard.md#authentication.", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_ForAUserWhoMustChangeTheirPassword_SaysSo()
    {
        var caller = new CallerAccess(CallerKind.User, Guid.NewGuid(), "alice", "Alice", MustChangePassword: true, EffectiveAccess.None);

        var (_, _, problem) = await ForbidAsync(caller, Permissions.SagasView, "OrderSaga");

        Assert.Equal("password_change_required", problem.GetProperty("code").GetString());
        Assert.EndsWith("See docs/dashboard.md#authentication.", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_AccessManageForTheApiKey_ExplainsTheKeyNeverHoldsIt()
    {
        var apiKey = new CallerAccess(
            CallerKind.ApiKey, null, CallerAccess.ApiKeyUsername, "API key", false, AccessEvaluator.EvaluateGrants([All(BuiltInRoles.OperatorId)], BuiltInRoles.All));

        var (_, _, problem) = await ForbidAsync(apiKey, Permissions.AccessManage, sagaType: null);

        Assert.Contains("API key never holds access.manage", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DashboardPolicies.SagasView, "OrderSaga", true)]
    [InlineData(DashboardPolicies.SagasView, "PaymentSaga", false)]
    [InlineData(DashboardPolicies.SagasView, null, true)]
    [InlineData(DashboardPolicies.SagasRetry, "OrderSaga", true)]
    [InlineData(DashboardPolicies.SagasRetry, "PaymentSaga", false)]
    [InlineData(DashboardPolicies.AccessManage, null, false)]
    public async Task ThePermissionPolicies_CheckTheRoutesSagaType(string policy, string? sagaType, bool allowed)
    {
        var scopedOperator = Caller(Named(BuiltInRoles.OperatorId, "OrderSaga"));

        Assert.Equal(allowed, await AuthorizeAsync(scopedOperator, policy, sagaType));
    }

    [Fact]
    public async Task AccessManage_CountsOnlyForEverySagaType()
    {
        Assert.True(await AuthorizeAsync(Caller(All(BuiltInRoles.AdministratorId)), DashboardPolicies.AccessManage, sagaType: null));
        Assert.False(await AuthorizeAsync(Caller(Named(BuiltInRoles.AdministratorId, "OrderSaga")), DashboardPolicies.AccessManage, sagaType: null));
    }

    [Fact]
    public async Task APermissionPolicy_WithNoResolvedCaller_Fails()
    {
        Assert.False(await AuthorizeAsync(caller: null, DashboardPolicies.SagasView, "OrderSaga"));
    }

    private static AccessGrant All(Guid roleId) => new(roleId, AllSagaTypes: true, []);

    private static AccessGrant Named(Guid roleId, params string[] sagaTypes) => new(roleId, AllSagaTypes: false, sagaTypes);

    private static CallerAccess Caller(AccessGrant grant) =>
        new(CallerKind.User, Guid.NewGuid(), "alice", "Alice", MustChangePassword: false, AccessEvaluator.EvaluateGrants([grant], BuiltInRoles.All));

    private async Task<bool> AuthorizeAsync(CallerAccess? caller, string policy, string? sagaType)
    {
        using var client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var context = Context(scope.ServiceProvider, caller, sagaType);
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        return (await authorization.AuthorizeAsync(DashboardClaims.ForApiKey(), context, policy)).Succeeded;
    }

    /// <summary>The host's own 403 writer, given a failed requirement for <paramref name="permission"/>.</summary>
    private async Task<(int Status, string? ContentType, JsonElement Problem)> ForbidAsync(CallerAccess caller, string permission, string? sagaType)
    {
        using var client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var context = Context(scope.ServiceProvider, caller, sagaType);
        context.Response.Body = new MemoryStream();
        var handler = scope.ServiceProvider.GetRequiredService<IAuthorizationMiddlewareResultHandler>();
        var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        var forbid = PolicyAuthorizationResult.Forbid(AuthorizationFailure.Failed([new PermissionRequirement(permission)]));

        await handler.HandleAsync(_ => throw new InvalidOperationException("A forbidden request must not reach the endpoint."), context, policy, forbid);

        context.Response.Body.Position = 0;
        var problem = await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body);
        return (context.Response.StatusCode, context.Response.ContentType?.Split(';')[0], problem);
    }

    private static DefaultHttpContext Context(IServiceProvider services, CallerAccess? caller, string? sagaType)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/api/sagas/OrderSaga/00000000-0000-0000-0000-000000000001/retry";
        if (sagaType is not null)
            context.Request.RouteValues[PermissionAuthorizationHandler.SagaTypeRouteValue] = sagaType;
        if (caller is not null)
            context.SetCaller(caller);
        return context;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await JsonSerializer.DeserializeAsync<JsonElement>(await response.Content.ReadAsStreamAsync());
    }
}
