using VSaga.Dashboard.Identity.Model;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>A user an administrator (or first-run setup) creates. Grants are validated against the stored roles.</summary>
/// <param name="Username">3 to 64 of <c>[A-Za-z0-9._@+-]</c>, starting with a letter or a digit; immutable once created.</param>
/// <param name="DisplayName">1 to 128 characters, trimmed.</param>
/// <param name="Password">Must satisfy the <see cref="PasswordPolicy"/>.</param>
/// <param name="MustChangePassword">True to make the user choose a new password before they hold any access.</param>
/// <param name="Grants">The user's own grants; null for none.</param>
public sealed record NewUser(
    string? Username,
    string? DisplayName,
    string? Password,
    bool MustChangePassword,
    IReadOnlyList<AccessGrant>? Grants);

/// <summary>Changes to a user; a null member leaves that field as it is. Team membership is changed through the team.</summary>
/// <param name="DisplayName">1 to 128 characters, trimmed.</param>
/// <param name="IsEnabled">False disables the account; either change rotates the security stamp.</param>
/// <param name="Grants">Replaces every grant the user holds directly.</param>
public sealed record UserChanges(string? DisplayName, bool? IsEnabled, IReadOnlyList<AccessGrant>? Grants);

/// <summary>A team as a whole: creating or replacing one writes every member. Null lists mean empty.</summary>
/// <param name="Name">1 to 64 characters, trimmed, unique ignoring case.</param>
/// <param name="Description">At most 256 characters; blank means none.</param>
/// <param name="MemberIds">The users in the team; this is the only way membership is written.</param>
/// <param name="Grants">Grants every member holds.</param>
public sealed record TeamDraft(string? Name, string? Description, IReadOnlyList<Guid>? MemberIds, IReadOnlyList<AccessGrant>? Grants);

/// <summary>A custom role as a whole.</summary>
/// <param name="Name">1 to 64 characters, trimmed, unique ignoring case.</param>
/// <param name="Description">At most 256 characters; blank means none.</param>
/// <param name="Permissions">A non-empty subset of the permission catalogue.</param>
public sealed record RoleDraft(string? Name, string? Description, IReadOnlyList<string>? Permissions);

/// <summary>How a self-service password change ended.</summary>
public enum PasswordChangeStatus
{
    /// <summary>The password was changed and the security stamp rotated.</summary>
    Changed,

    /// <summary>
    /// The current password was wrong; the failure was counted against the account. When
    /// <see cref="PasswordChangeResult.LockedUntilUtc"/> is set, this failure locked the account and the
    /// session should end.
    /// </summary>
    WrongCurrentPassword,

    /// <summary>The account is locked or disabled; nothing was checked or counted, and the session should end.</summary>
    Refused,
}

/// <summary>The outcome of <see cref="AccessAdministrationService.ChangeOwnPasswordAsync"/>.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="User">The updated user when <see cref="PasswordChangeStatus.Changed"/>, else null.</param>
/// <param name="LockedUntilUtc">The lockout a wrong current password caused, or null.</param>
public sealed record PasswordChangeResult(PasswordChangeStatus Status, DashboardUser? User, DateTimeOffset? LockedUntilUtc);
