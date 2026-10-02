using System.Text.Json.Serialization;
using VSaga.Dashboard.Identity.Services;
using PermissionCatalog = VSaga.Dashboard.Identity.Model.Permissions;

namespace VSaga.Dashboard.Api.Endpoints;

/// <summary>
/// <c>GET /api/auth/session</c>, and the answer of every endpoint that signs in or out: who the caller is
/// and what they may do. Arrays are always present; <see cref="User"/> and <see cref="Access"/> are null
/// for an anonymous caller, and <see cref="User"/> is null for the API key.
/// </summary>
/// <param name="Authenticated">True for a signed-in user or the API key.</param>
/// <param name="SetupRequired">True while no user exists.</param>
/// <param name="SetupAvailable">True when first-run setup can be completed now.</param>
/// <param name="User">The signed-in user, or null.</param>
/// <param name="Access">What the caller may do, or null when anonymous.</param>
/// <param name="PasswordMinLength">The shortest password the policy accepts, for the forms.</param>
public sealed record SessionResponse(
    bool Authenticated,
    bool SetupRequired,
    bool SetupAvailable,
    SessionUser? User,
    SessionAccess? Access,
    int PasswordMinLength)
{
    /// <summary>The session of <paramref name="caller"/>, or an anonymous one when it is null.</summary>
    public static SessionResponse For(CallerAccess? caller, bool setupRequired, bool setupAvailable, int passwordMinLength)
    {
        if (caller is null)
            return new SessionResponse(false, setupRequired, setupAvailable, User: null, Access: null, passwordMinLength);

        var user = caller is { Kind: CallerKind.User, UserId: { } id }
            ? new SessionUser(id, caller.Username, caller.DisplayName, caller.MustChangePassword)
            : null;
        return new SessionResponse(true, setupRequired, setupAvailable, user, SessionAccess.From(caller.Access), passwordMinLength);
    }
}

/// <param name="Id">The user's id.</param>
/// <param name="Username">The username.</param>
/// <param name="DisplayName">What the dashboard shows.</param>
/// <param name="MustChangePassword">True while the user must change their password; access is then empty.</param>
public sealed record SessionUser(Guid Id, string Username, string DisplayName, bool MustChangePassword);

/// <summary>
/// The caller's effective access. <see cref="Permissions"/> lists the permissions held for every saga type;
/// <see cref="Scoped"/> lists, per saga type (ordinal order), the permissions held only for that type.
/// </summary>
public sealed record SessionAccess(IReadOnlyList<string> Permissions, IReadOnlyList<ScopedPermissions> Scoped)
{
    public static SessionAccess From(EffectiveAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        var unscoped = new List<string>();
        var scoped = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var permission in PermissionCatalog.All.Select(p => p.Key))
        {
            var scope = access.ScopeFor(permission);
            if (scope.IsAll)
            {
                unscoped.Add(permission);
                continue;
            }

            foreach (var sagaType in scope.SagaTypes)
            {
                if (!scoped.TryGetValue(sagaType, out var permissions))
                    scoped[sagaType] = permissions = [];
                permissions.Add(permission);
            }
        }

        return new SessionAccess(unscoped, [.. scoped.Select(s => new ScopedPermissions(s.Key, s.Value))]);
    }
}

/// <param name="SagaType">The exact saga type name.</param>
/// <param name="Permissions">The permissions held for it beyond <see cref="SessionAccess.Permissions"/>, in catalogue order.</param>
public sealed record ScopedPermissions(string SagaType, IReadOnlyList<string> Permissions);

/// <summary><c>POST /api/auth/login</c>. An unknown member is a 400, not silently ignored.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LoginRequest(string? Username, string? Password);

/// <summary><c>POST /api/auth/password</c>. An unknown member is a 400, not silently ignored.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
