using System.Globalization;
using System.Text;

namespace VSaga.Dashboard.Identity.Model;

/// <summary>
/// One role held by a user or a team, either for every saga type or for the named ones. Saga type names
/// are stored exactly as given: the administration service trims and validates them before they get here,
/// and the store never rewrites them, so what an administrator saw validated is what scopes the grant.
/// </summary>
/// <param name="RoleId">The role granted; it must exist when the grant is written.</param>
/// <param name="AllSagaTypes">True for every saga type, including types that have not run yet.</param>
/// <param name="SagaTypes">The exact saga type names the grant covers; empty when <paramref name="AllSagaTypes"/> is true.</param>
public sealed record AccessGrant(Guid RoleId, bool AllSagaTypes, IReadOnlyList<string> SagaTypes);

/// <summary>
/// A person who signs in to the dashboard. Store-neutral: nothing here depends on how the store keeps it.
/// </summary>
/// <param name="Id">Stable identity, also the cookie's <c>sub</c> claim.</param>
/// <param name="Username">As typed when the user was created; unique ignoring case and surrounding whitespace.</param>
/// <param name="DisplayName">Shown in the top bar and on the administration pages.</param>
/// <param name="PasswordHash">ASP.NET Core <c>PasswordHasher</c> output; never logged, and left out of <c>ToString</c>.</param>
/// <param name="SecurityStamp">Rotated when the credential or the account's standing changes, which ends existing sessions; left out of <c>ToString</c>.</param>
/// <param name="IsEnabled">A disabled user cannot sign in and holds no access.</param>
/// <param name="MustChangePassword">While true, the user holds no access until the password is changed.</param>
/// <param name="FailedSignInCount">Consecutive failed sign-ins since the last success or lockout.</param>
/// <param name="LockoutEndUtc">Sign-in is refused until this instant; null when not locked.</param>
/// <param name="LastSignInAtUtc">The last successful sign-in, or null if never.</param>
/// <param name="CreatedAtUtc">When the account was created.</param>
/// <param name="UpdatedAtUtc">When an administrator or the user last changed the account.</param>
/// <param name="Grants">The user's own grants; team grants are held by the teams.</param>
public sealed record DashboardUser(
    Guid Id,
    string Username,
    string DisplayName,
    string PasswordHash,
    string SecurityStamp,
    bool IsEnabled,
    bool MustChangePassword,
    int FailedSignInCount,
    DateTimeOffset? LockoutEndUtc,
    DateTimeOffset? LastSignInAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<AccessGrant> Grants)
{
    /// <summary>
    /// Replaces the generated member list of <see cref="object.ToString"/>, which would print every
    /// property: the password hash and the security stamp are left out, so a user that reaches a log
    /// line, an exception message or an audit event never carries either.
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            CultureInfo.InvariantCulture,
            $"Id = {Id}, Username = {Username}, DisplayName = {DisplayName}, IsEnabled = {IsEnabled}, MustChangePassword = {MustChangePassword}, ");
        builder.Append(
            CultureInfo.InvariantCulture,
            $"FailedSignInCount = {FailedSignInCount}, LockoutEndUtc = {LockoutEndUtc:O}, LastSignInAtUtc = {LastSignInAtUtc:O}, ");
        builder.Append(
            CultureInfo.InvariantCulture,
            $"CreatedAtUtc = {CreatedAtUtc:O}, UpdatedAtUtc = {UpdatedAtUtc:O}, GrantCount = {Grants.Count}");
        return true;
    }
}

/// <summary>A named group of users whose grants every member holds.</summary>
/// <param name="Id">Stable identity.</param>
/// <param name="Name">Unique ignoring case and surrounding whitespace.</param>
/// <param name="Description">Free text for administrators, or null.</param>
/// <param name="MemberIds">The users in the team. Membership is written only through the team.</param>
/// <param name="Grants">Grants every member holds while in the team.</param>
public sealed record DashboardTeam(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<Guid> MemberIds,
    IReadOnlyList<AccessGrant> Grants);

/// <summary>A named set of permission keys (see <see cref="Permissions"/>).</summary>
/// <param name="Id">Stable identity; the built-in roles use the fixed ids in <see cref="BuiltInRoles"/>.</param>
/// <param name="Name">Unique ignoring case and surrounding whitespace.</param>
/// <param name="Description">Free text for administrators, or null.</param>
/// <param name="IsBuiltIn">True for the three roles defined in code; those are never edited or deleted.</param>
/// <param name="Permissions">Permission keys from the catalogue.</param>
public sealed record DashboardRole(
    Guid Id,
    string Name,
    string? Description,
    bool IsBuiltIn,
    IReadOnlyList<string> Permissions);

/// <summary>How usernames, team names and role names are compared.</summary>
public static class IdentityNames
{
    /// <summary>
    /// The key uniqueness is enforced on: trimmed and upper-cased with the invariant culture. SQLite's
    /// own case folding (<c>NOCASE</c>) covers ASCII only, so uniqueness goes through a stored column
    /// holding this value rather than a collation, which a non-relational store can also use as its key.
    /// </summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim().ToUpperInvariant();
    }
}
