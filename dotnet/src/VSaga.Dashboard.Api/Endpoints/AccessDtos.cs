using System.Text.Json.Serialization;
using VSaga.Dashboard.Identity.Model;
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
/// <param name="SetupAvailable">True when first-run setup can be completed now, with the one-time code.</param>
/// <param name="SetupProblem">
/// Why setup cannot be completed while it is required (a seed that could not be applied, say), with code
/// <c>setup_unavailable</c>; null unless <paramref name="SetupRequired"/> is true and
/// <paramref name="SetupAvailable"/> false.
/// </param>
/// <param name="User">The signed-in user, or null.</param>
/// <param name="Access">What the caller may do, or null when anonymous.</param>
/// <param name="PasswordMinLength">The shortest password the policy accepts, for the forms.</param>
public sealed record SessionResponse(
    bool Authenticated,
    bool SetupRequired,
    bool SetupAvailable,
    SetupProblem? SetupProblem,
    SessionUser? User,
    SessionAccess? Access,
    int PasswordMinLength)
{
    /// <summary>The session of <paramref name="caller"/>, or an anonymous one when it is null.</summary>
    public static SessionResponse For(CallerAccess? caller, SetupState setup, int passwordMinLength)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (caller is null)
            return new SessionResponse(false, setup.Required, setup.Available, setup.Problem, User: null, Access: null, passwordMinLength);

        var user = caller is { Kind: CallerKind.User, UserId: { } id }
            ? new SessionUser(id, caller.Username, caller.DisplayName, caller.MustChangePassword)
            : null;
        return new SessionResponse(true, setup.Required, setup.Available, setup.Problem, user, SessionAccess.From(caller.Access), passwordMinLength);
    }
}

/// <summary>Where first-run setup stands, as the session reports it.</summary>
/// <param name="Required">True while no user exists.</param>
/// <param name="Available">True when setup can be completed now.</param>
/// <param name="Problem">Why it cannot, while it is required; otherwise null.</param>
public sealed record SetupState(bool Required, bool Available, SetupProblem? Problem);

/// <param name="Code">A problem code from the API's list: <c>setup_unavailable</c>.</param>
/// <param name="Detail">What stands in the way and what to do about it, naming settings but never their values.</param>
public sealed record SetupProblem(string Code, string Detail);

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

/// <summary>
/// <c>POST /api/auth/setup</c>: the first administrator, and the one-time setup code the API logged at start
/// (or <c>Dashboard:Setup:Code</c>). An unknown member is a 400, not silently ignored.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetupRequest(string? Username, string? DisplayName, string? Password, string? Code);

/// <summary><c>POST /api/auth/password</c>. An unknown member is a 400, not silently ignored.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

// The administration contract (design §8.9). These records are canonical: the SPA's models use their names
// (isEnabled, isBuiltIn, lastSignInAtUtc), every array in a response is present and never null, and the golden
// fixtures under dashboard-web/src/app/testing/contracts/admin/ pin the JSON from both suites. Every request
// record refuses unknown members, so a payload that drifts from this contract is a 400 naming the member
// instead of a 200 that silently changed nothing.

/// <summary>One entry of <c>GET /api/admin/permissions</c>: the catalogue the role editor and the effective-access preview read.</summary>
/// <param name="Key">The stable permission key, such as <c>sagas.view</c>.</param>
/// <param name="Name">The label the dashboard shows.</param>
/// <param name="Description">What the permission allows.</param>
/// <param name="Scopable">True when a grant for named saga types confers it; false for <c>access.manage</c>.</param>
/// <param name="Implies">Keys held for the same scope whenever this one is.</param>
public sealed record PermissionResponse(string Key, string Name, string Description, bool Scopable, IReadOnlyList<string> Implies)
{
    public static PermissionResponse From(PermissionDefinition permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return new PermissionResponse(permission.Key, permission.Name, permission.Description, permission.Scopable, permission.Implies);
    }
}

/// <summary>
/// One role held for all saga types or for named ones, in a user's or a team's <c>grants</c>. In a response
/// <see cref="SagaTypes"/> is always an array (empty for all saga types); a request may leave it out when
/// <see cref="AllSagaTypes"/> is true.
/// </summary>
/// <param name="RoleId">The role granted.</param>
/// <param name="AllSagaTypes">True for every saga type, including types that have not run yet.</param>
/// <param name="SagaTypes">The exact saga type names, 1 to 100, when not for all saga types.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GrantDto(Guid RoleId, bool AllSagaTypes, IReadOnlyList<string>? SagaTypes)
{
    public static GrantDto From(AccessGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return new GrantDto(grant.RoleId, grant.AllSagaTypes, grant.SagaTypes);
    }

    /// <summary>
    /// The grants a request names, for the administration service to validate; null stays null (no change, or
    /// none). A null entry stays null too, so the service reports it at its path instead of the API failing.
    /// </summary>
    public static IReadOnlyList<AccessGrant>? ToGrants(IReadOnlyList<GrantDto?>? grants) =>
        grants is null ? null : [.. grants.Select(g => g is null ? null! : new AccessGrant(g.RoleId, g.AllSagaTypes, g.SagaTypes ?? []))];
}

/// <summary><c>GET /api/admin/roles</c> and the answer of every role change.</summary>
/// <param name="Id">The role's id; the built-in roles have fixed ones.</param>
/// <param name="Name">Unique ignoring case.</param>
/// <param name="Description">Free text, or null.</param>
/// <param name="IsBuiltIn">True for Administrator, Operator and Viewer, which cannot be changed or deleted.</param>
/// <param name="Permissions">Permission keys, in catalogue order.</param>
public sealed record RoleResponse(Guid Id, string Name, string? Description, bool IsBuiltIn, IReadOnlyList<string> Permissions)
{
    public static RoleResponse From(DashboardRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return new RoleResponse(role.Id, role.Name, role.Description, role.IsBuiltIn, role.Permissions);
    }
}

/// <summary>
/// <c>GET /api/admin/users</c> and the answer of every user change. <see cref="TeamIds"/> is read-only here:
/// membership is written only through the team.
/// </summary>
/// <param name="Id">The user's id.</param>
/// <param name="Username">Immutable once created.</param>
/// <param name="DisplayName">What the dashboard shows.</param>
/// <param name="IsEnabled">False for a disabled account, which cannot sign in and holds no access.</param>
/// <param name="MustChangePassword">True while the user must choose a new password before holding any access.</param>
/// <param name="LockedUntilUtc">When a lockout in force ends; null when the account is not locked now.</param>
/// <param name="LastSignInAtUtc">The last successful sign-in, or null if never.</param>
/// <param name="CreatedAtUtc">When the account was created.</param>
/// <param name="Grants">The user's own grants.</param>
/// <param name="TeamIds">The teams the user is a member of.</param>
public sealed record UserResponse(
    Guid Id,
    string Username,
    string DisplayName,
    bool IsEnabled,
    bool MustChangePassword,
    DateTimeOffset? LockedUntilUtc,
    DateTimeOffset? LastSignInAtUtc,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<GrantDto> Grants,
    IReadOnlyList<Guid> TeamIds)
{
    /// <summary>The user as <paramref name="now"/> sees it: a lockout that has ended reads as none.</summary>
    public static UserResponse From(DashboardUser user, IEnumerable<DashboardTeam> teams, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(teams);
        return new UserResponse(
            user.Id,
            user.Username,
            user.DisplayName,
            user.IsEnabled,
            user.MustChangePassword,
            user.LockoutEndUtc > now ? user.LockoutEndUtc : null,
            user.LastSignInAtUtc,
            user.CreatedAtUtc,
            [.. user.Grants.Select(GrantDto.From)],
            [.. teams.Where(t => t.MemberIds.Contains(user.Id)).Select(t => t.Id)]);
    }
}

/// <summary><c>GET /api/admin/teams</c> and the answer of every team change.</summary>
/// <param name="Id">The team's id.</param>
/// <param name="Name">Unique ignoring case.</param>
/// <param name="Description">Free text, or null.</param>
/// <param name="MemberIds">The users in the team.</param>
/// <param name="Grants">Grants every member holds.</param>
public sealed record TeamResponse(Guid Id, string Name, string? Description, IReadOnlyList<Guid> MemberIds, IReadOnlyList<GrantDto> Grants)
{
    public static TeamResponse From(DashboardTeam team)
    {
        ArgumentNullException.ThrowIfNull(team);
        return new TeamResponse(team.Id, team.Name, team.Description, team.MemberIds, [.. team.Grants.Select(GrantDto.From)]);
    }
}

/// <summary>
/// <c>POST /api/admin/users</c>. <see cref="MustChangePassword"/> defaults to true: a password an administrator
/// chose is a temporary one. There is no team member: membership is written through the team.
/// </summary>
/// <param name="Username">3 to 64 of <c>[A-Za-z0-9._@+-]</c>, starting with a letter or a digit; not <c>api-key</c>.</param>
/// <param name="DisplayName">1 to 128 characters.</param>
/// <param name="Password">Must satisfy the password policy.</param>
/// <param name="MustChangePassword">Null or true: the user must change it at first sign-in.</param>
/// <param name="Grants">The user's own grants; null or empty for none.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateUserRequest(
    string? Username,
    string? DisplayName,
    string? Password,
    bool? MustChangePassword,
    IReadOnlyList<GrantDto?>? Grants);

/// <summary><c>PUT /api/admin/users/{id}</c>: a member left out (or null) is left as it is.</summary>
/// <param name="DisplayName">1 to 128 characters.</param>
/// <param name="IsEnabled">False disables the account and ends its sessions; true enables it.</param>
/// <param name="Grants">Replaces every grant the user holds directly.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateUserRequest(string? DisplayName, bool? IsEnabled, IReadOnlyList<GrantDto?>? Grants);

/// <summary>
/// <c>POST /api/admin/users/{id}/password</c>: an administrator sets a user's password, which ends the user's
/// sessions. <see cref="MustChangePassword"/> defaults to true.
/// </summary>
/// <param name="NewPassword">Must satisfy the password policy.</param>
/// <param name="MustChangePassword">Null or true: the user must change it at next sign-in.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResetPasswordRequest(string? NewPassword, bool? MustChangePassword);

/// <summary><c>POST /api/admin/teams</c> and <c>PUT /api/admin/teams/{id}</c>: the whole team, members included.</summary>
/// <param name="Name">1 to 64 characters, unique ignoring case.</param>
/// <param name="Description">At most 256 characters; blank or null for none.</param>
/// <param name="MemberIds">The users in the team; the only way membership is written. Null or empty for none.</param>
/// <param name="Grants">Grants every member holds; null or empty for none.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TeamRequest(string? Name, string? Description, IReadOnlyList<Guid>? MemberIds, IReadOnlyList<GrantDto?>? Grants);

/// <summary><c>POST /api/admin/roles</c> and <c>PUT /api/admin/roles/{id}</c>: a custom role as a whole.</summary>
/// <param name="Name">1 to 64 characters, unique ignoring case, the built-in names included.</param>
/// <param name="Description">At most 256 characters; blank or null for none.</param>
/// <param name="Permissions">A non-empty subset of the catalogue.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoleRequest(string? Name, string? Description, IReadOnlyList<string>? Permissions);
