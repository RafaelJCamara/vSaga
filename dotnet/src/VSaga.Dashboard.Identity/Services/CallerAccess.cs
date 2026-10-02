using System.Security.Claims;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>How a request was authenticated.</summary>
public enum CallerKind
{
    /// <summary>A signed-in dashboard user (the session cookie).</summary>
    User,

    /// <summary>The shared API key; never holds <c>access.manage</c>.</summary>
    ApiKey,
}

/// <summary>
/// Who is calling and what they may do, resolved afresh from the identity store for each request, so a
/// revoked grant or a disabled account takes effect at once.
/// </summary>
/// <param name="Kind">How the caller authenticated.</param>
/// <param name="UserId">The user's id; null for the API key.</param>
/// <param name="Username">The username, or <c>api-key</c>, which no user may take.</param>
/// <param name="DisplayName">What the dashboard shows for the caller.</param>
/// <param name="MustChangePassword">True while the user must change their password; <paramref name="Access"/> is then empty.</param>
/// <param name="Access">The caller's effective access.</param>
public sealed record CallerAccess(
    CallerKind Kind,
    Guid? UserId,
    string Username,
    string DisplayName,
    bool MustChangePassword,
    EffectiveAccess Access)
{
    /// <summary>The username the API key acts under; reserved, so no user can be recorded the same way.</summary>
    public const string ApiKeyUsername = "api-key";

    /// <summary>
    /// The name written as the actor of a retry and of audit events: <c>dashboard:&lt;username&gt;</c>, or
    /// <c>dashboard:api-key</c> for the key.
    /// </summary>
    public string AuditActor => Kind == CallerKind.ApiKey ? "dashboard:" + ApiKeyUsername : "dashboard:" + Username;
}

/// <summary>
/// Resolves the caller behind an authenticated principal. The implementation reads the claims, loads the
/// user, their teams and the roles, and evaluates access; it arrives with the authentication wiring.
/// </summary>
public interface ICallerAccessResolver
{
    /// <summary>
    /// The caller, or null when the principal no longer stands: the user is missing or disabled, the
    /// security stamp differs, the API key's role is unknown, or the store is not ready.
    /// </summary>
    Task<CallerAccess?> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}

/// <summary>
/// Told after an access change is committed, so live connections that were authorised under the old
/// access can be dropped and re-established under the new one (the hub's connection registry).
/// </summary>
public interface IAccessChangeObserver
{
    /// <summary>The access, credentials or standing of these users changed.</summary>
    Task UsersChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>A change that can affect anyone (a role's permissions) was committed.</summary>
    Task AllUsersChangedAsync(CancellationToken cancellationToken);
}
