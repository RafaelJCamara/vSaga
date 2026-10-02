using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>Whether the identity store can be used; <see cref="IdentityStartup"/> answers it.</summary>
public interface IIdentityReadiness
{
    /// <summary>True once the store has been initialised.</summary>
    bool IsReady { get; }
}

/// <summary>
/// Resolves a principal to a <see cref="CallerAccess"/> on every request, so a disabled account, a rotated
/// security stamp or a revoked grant takes effect at once rather than when a cookie expires.
/// <list type="bullet">
/// <item>A session principal (subject is a user id): the user is read from the store and must exist, be
/// enabled and still have the stamp the session was issued under; access is the evaluation of the user's
/// grants and their teams' grants. While the store is not ready the answer is null.</item>
/// <item>The API key (subject <c>api-key</c>, authentication type <see cref="DashboardClaims.ApiKeyAuthenticationType"/>):
/// <see cref="DashboardSecuritySettings.ApiKeyRole"/> names a built-in role, resolved from code even while the
/// store is not ready, or a custom role, read from the store by name. Access is that role for every saga type,
/// without <c>access.manage</c>: a shared secret with no lockout and no named actor never manages access.</item>
/// </list>
/// </summary>
/// <remarks>
/// The store is taken from <paramref name="services"/> only once the store is ready: with no usable database
/// path, building the store itself throws, and an API key with a built-in role must keep working then.
/// </remarks>
public sealed class CallerAccessResolver(
    IServiceProvider services,
    IIdentityReadiness readiness,
    DashboardSecuritySettings settings) : ICallerAccessResolver
{
    /// <summary>What the dashboard shows as the API key's display name.</summary>
    public const string ApiKeyDisplayName = "API key";

    public async Task<CallerAccess?> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var subject = principal.FindFirst(DashboardClaims.Subject)?.Value;
        if (subject is null)
            return null;

        if (string.Equals(subject, CallerAccess.ApiKeyUsername, StringComparison.Ordinal))
        {
            return string.Equals(principal.Identity?.AuthenticationType, DashboardClaims.ApiKeyAuthenticationType, StringComparison.Ordinal)
                ? await ResolveApiKeyAsync(cancellationToken)
                : null;
        }

        return Guid.TryParse(subject, out var userId) && readiness.IsReady
            ? await ResolveUserAsync(userId, principal.FindFirst(DashboardClaims.SecurityStamp)?.Value, cancellationToken)
            : null;
    }

    /// <summary>
    /// The role <see cref="DashboardSecuritySettings.ApiKeyRole"/> names: a built-in from code, else a custom
    /// role from the store when it is ready; null when neither has that name.
    /// </summary>
    public async Task<DashboardRole?> FindApiKeyRoleAsync(CancellationToken cancellationToken)
    {
        if (BuiltInRoles.FindByName(settings.ApiKeyRole) is { } builtIn)
            return builtIn;

        return readiness.IsReady ? await Store.FindRoleByNameAsync(settings.ApiKeyRole, cancellationToken) : null;
    }

    private IDashboardIdentityStore Store => services.GetRequiredService<IDashboardIdentityStore>();

    private async Task<CallerAccess?> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        if (await FindApiKeyRoleAsync(cancellationToken) is not { } role)
            return null;

        var withoutManage = role with
        {
            Permissions = [.. role.Permissions.Where(p => !string.Equals(p, Permissions.AccessManage, StringComparison.Ordinal))],
        };
        var access = AccessEvaluator.EvaluateGrants([new AccessGrant(role.Id, AllSagaTypes: true, [])], [withoutManage]);
        return new CallerAccess(CallerKind.ApiKey, UserId: null, CallerAccess.ApiKeyUsername, ApiKeyDisplayName, MustChangePassword: false, access);
    }

    private async Task<CallerAccess?> ResolveUserAsync(Guid userId, string? stamp, CancellationToken cancellationToken)
    {
        var store = Store;
        var user = await store.FindUserAsync(userId, cancellationToken);
        if (user is not { IsEnabled: true } || !string.Equals(user.SecurityStamp, stamp, StringComparison.Ordinal))
            return null;

        var teams = await store.ListTeamsForUserAsync(userId, cancellationToken);
        var roles = await store.ListRolesAsync(cancellationToken);
        var access = AccessEvaluator.Evaluate(user, teams, roles);
        return new CallerAccess(CallerKind.User, user.Id, user.Username, user.DisplayName, user.MustChangePassword, access);
    }
}
