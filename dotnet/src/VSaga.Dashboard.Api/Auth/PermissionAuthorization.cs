using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Auth;

/// <summary>The permission policies endpoints name in <c>RequireAuthorization</c>; each is named after its permission key.</summary>
public static class DashboardPolicies
{
    /// <summary><c>sagas.view</c>, for the route's saga type when it has one, else for any saga type.</summary>
    public const string SagasView = Permissions.SagasView;

    /// <summary><c>sagas.retry</c>, for the route's saga type.</summary>
    public const string SagasRetry = Permissions.SagasRetry;

    /// <summary><c>access.manage</c>, which counts only for every saga type.</summary>
    public const string AccessManage = Permissions.AccessManage;

    internal static void Add(AuthorizationBuilder authorization)
    {
        authorization
            .AddPolicy(SagasView, policy => policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(Permissions.SagasView)))
            .AddPolicy(SagasRetry, policy => policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(Permissions.SagasRetry)))
            .AddPolicy(AccessManage, policy => policy.RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(Permissions.AccessManage, RequireUnscoped: true)));
    }
}

/// <summary>
/// The caller must hold <paramref name="Permission"/>: for every saga type when
/// <paramref name="RequireUnscoped"/> is true; otherwise for the route's <c>sagaType</c> when the endpoint
/// has one, or for at least one saga type when it does not.
/// </summary>
public sealed record PermissionRequirement(string Permission, bool RequireUnscoped = false) : IAuthorizationRequirement;

/// <summary>
/// Decides <see cref="PermissionRequirement"/> from the caller the authenticating scheme resolved for this
/// request (<see cref="ICallerAccessFeature"/>). No caller, or a resource that is not an HTTP request, never
/// succeeds.
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    /// <summary>The route value a per-saga-type endpoint names its saga type with.</summary>
    public const string SagaTypeRouteValue = "sagaType";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        if (context.Resource is HttpContext http && http.GetCaller() is { } caller && Holds(caller.Access, requirement, http.Request.RouteValues))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }

    /// <summary>
    /// The route's saga type for the 403 body exactly as the request gave it, a blank one included (that is the
    /// value the decision was made for), or null only when the endpoint has no saga type route value.
    /// </summary>
    public static string? SagaTypeOf(HttpContext context) =>
        context.GetRouteValue(SagaTypeRouteValue) as string;

    /// <summary>
    /// An endpoint whose route has a saga type is decided for that value exactly as the request gave it, a
    /// blank one included: only a grant naming that very value covers it, so a blank segment such as
    /// <c>/api/sagas/%20/{id}</c> cannot fall back to "any type". Only endpoints without the route value ask
    /// whether the permission is held for any type.
    /// </summary>
    private static bool Holds(EffectiveAccess access, PermissionRequirement requirement, RouteValueDictionary routeValues)
    {
        if (requirement.RequireUnscoped)
            return access.HasUnscoped(requirement.Permission);

        return routeValues.TryGetValue(SagaTypeRouteValue, out var sagaType)
            ? access.Has(requirement.Permission, sagaType as string ?? "")
            : access.HasAny(requirement.Permission);
    }
}

/// <summary>
/// Writes the 403 problem (<see cref="AuthProblems.WriteForbiddenAsync"/>) naming the permission and saga
/// type a failed <see cref="PermissionRequirement"/> asked for, instead of the scheme's own forbid. Every
/// other outcome is the framework's.
/// </summary>
public sealed class DashboardAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (!authorizeResult.Forbidden)
            return _default.HandleAsync(next, context, policy, authorizeResult);

        var permission = authorizeResult.AuthorizationFailure?.FailedRequirements
            .OfType<PermissionRequirement>()
            .Select(r => r.Permission)
            .FirstOrDefault();
        return AuthProblems.WriteForbiddenAsync(context, context.GetCaller(), permission, PermissionAuthorizationHandler.SagaTypeOf(context));
    }
}
