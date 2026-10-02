using Microsoft.Extensions.Configuration;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Identity;

/// <summary>
/// How the first administrator comes to exist (design §8.8), read once from configuration while composing:
/// seeded from <see cref="UsernameKey"/> and <see cref="PasswordKey"/>, or claimed through first-run setup
/// with a one-time code, which <see cref="SetupCodeKey"/> may preset. A class, not a record, so the password
/// and the code never reach a generated <c>ToString</c>.
/// </summary>
public sealed class FirstAdministratorSettings
{
    public const string UsernameKey = "Dashboard:Admin:Username";

    public const string PasswordKey = "Dashboard:Admin:Password";

    public const string ResetOnStartKey = "Dashboard:Admin:ResetOnStart";

    public const string SetupCodeKey = "Dashboard:Setup:Code";

    public FirstAdministratorSettings(string? username, string? password, bool resetOnStart, string? setupCode)
    {
        Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        Password = string.IsNullOrEmpty(password) ? null : password;
        ResetOnStart = resetOnStart;
        SetupCode = string.IsNullOrWhiteSpace(setupCode) ? null : setupCode.Trim();
    }

    /// <summary>Nothing configured: setup with a generated code when the store has no users.</summary>
    public static FirstAdministratorSettings None { get; } = new(null, null, resetOnStart: false, setupCode: null);

    /// <summary>The seed administrator's username, trimmed; null when not set.</summary>
    public string? Username { get; }

    /// <summary>The seed administrator's password; null when not set. Never logged.</summary>
    public string? Password { get; }

    /// <summary>
    /// True to reset the seed user at every start: its password, its standing (enabled, unlocked, no forced
    /// change) and an Administrator grant for all saga types, creating the user when it is missing.
    /// </summary>
    public bool ResetOnStart { get; }

    /// <summary>The preset one-time setup code, trimmed; null to have one generated. Never logged.</summary>
    public string? SetupCode { get; }

    /// <summary>
    /// True when either seed key is set. First-run setup is then never offered, even when the seed cannot be
    /// applied: an operator who configured a seed never gets an open claim instead.
    /// </summary>
    public bool SeedConfigured => Username is not null || Password is not null;

    /// <summary>
    /// Reads the keys. <c>Dashboard:Admin:ResetOnStart</c> must be true or false (empty means false) and needs
    /// at least one seed key; <c>Dashboard:Setup:Code</c> must hold <see cref="SetupCodes.MinPresetLength"/> to
    /// <see cref="SetupCodes.MaxPresetLength"/> characters once spaces and hyphens are left out, or composition
    /// fails here. Whether the seed itself is usable (a valid username, a password the policy accepts) is
    /// decided at start, where a bad seed leaves sign-in closed and says why instead of stopping the API.
    /// </summary>
    /// <exception cref="InvalidOperationException">A key holds something it cannot.</exception>
    public static FirstAdministratorSettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = new FirstAdministratorSettings(
            configuration[UsernameKey], configuration[PasswordKey], ReadResetOnStart(configuration[ResetOnStartKey]), configuration[SetupCodeKey]);

        if (settings.ResetOnStart && !settings.SeedConfigured)
        {
            throw new InvalidOperationException(
                $"{ResetOnStartKey} is true but neither {UsernameKey} nor {PasswordKey} is set: it resets the account those two name, "
                + "so set both, or set it back to false.");
        }

        if (settings.SetupCode is { } code && SetupCodes.Canonical(code).Length is < SetupCodes.MinPresetLength or > SetupCodes.MaxPresetLength)
        {
            throw new InvalidOperationException(
                $"Invalid {SetupCodeKey}: use {SetupCodes.MinPresetLength} to {SetupCodes.MaxPresetLength} characters, not counting "
                + "spaces and hyphens, or leave it empty to have a code generated and logged at start.");
        }

        return settings;
    }

    private static bool ReadResetOnStart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return bool.TryParse(value.Trim(), out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Invalid {ResetOnStartKey} '{value}': expected true or false, or empty for the default of false.");
    }
}
