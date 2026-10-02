namespace VSaga.Dashboard.Identity.Services;

/// <summary>
/// What a new password must satisfy: <see cref="DashboardSecuritySettings.PasswordMinLength"/> to
/// <see cref="MaxLength"/> characters, not the username (ignoring case and surrounding whitespace) and, on a
/// change the user makes themselves, not the password it replaces. No composition rules: length is what
/// resists guessing, and the lockout and rate limits bound online attempts.
/// </summary>
public sealed class PasswordPolicy(DashboardSecuritySettings settings)
{
    /// <summary>
    /// The longest password accepted anywhere, sign-in included: a longer one is refused before it is
    /// hashed, so an oversized request cannot buy extra hashing work.
    /// </summary>
    public const int MaxLength = 128;

    public int MinLength => settings.PasswordMinLength;

    /// <summary>
    /// The reasons <paramref name="password"/> is not acceptable as a new password, or an empty list.
    /// </summary>
    /// <param name="username">The account's username, which the password may not equal.</param>
    /// <param name="password">The proposed password.</param>
    /// <param name="currentPassword">
    /// The password being replaced, when the user typed it (a self-service change); null when an
    /// administrator sets the password and the current one is unknown.
    /// </param>
    public IReadOnlyList<string> Validate(string? username, string? password, string? currentPassword = null)
    {
        if (string.IsNullOrEmpty(password))
            return ["Enter a password."];

        var errors = new List<string>();
        if (password.Length < MinLength || password.Length > MaxLength)
            errors.Add($"Use {MinLength} to {MaxLength} characters.");

        if (!string.IsNullOrWhiteSpace(username)
            && string.Equals(password.Trim(), username.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("The password may not be the username.");
        }

        if (currentPassword is not null && string.Equals(password, currentPassword, StringComparison.Ordinal))
            errors.Add("The new password must differ from the current one.");

        return errors;
    }
}
