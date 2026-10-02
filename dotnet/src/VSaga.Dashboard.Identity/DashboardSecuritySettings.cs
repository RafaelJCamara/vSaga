using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace VSaga.Dashboard.Identity;

/// <summary>
/// The dashboard's password and lockout settings, read once from configuration while composing and
/// validated there, then registered as a plain singleton. A value out of range fails composition, naming the
/// key and the value, instead of surfacing at the first sign-in.
/// </summary>
/// <param name="PasswordMinLength">The shortest password the policy accepts; at most <see cref="Services.PasswordPolicy.MaxLength"/>.</param>
/// <param name="LockoutMaxFailedAttempts">Consecutive failed sign-ins that lock an account; 0 never locks.</param>
/// <param name="LockoutDuration">How long a locked account refuses sign-in.</param>
public sealed record DashboardSecuritySettings(int PasswordMinLength, int LockoutMaxFailedAttempts, TimeSpan LockoutDuration)
{
    public const string PasswordMinLengthKey = "Dashboard:Password:MinLength";

    public const string LockoutMaxFailedAttemptsKey = "Dashboard:Lockout:MaxFailedAttempts";

    public const string LockoutMinutesKey = "Dashboard:Lockout:Minutes";

    public const int DefaultPasswordMinLength = 12;

    public const int DefaultLockoutMaxFailedAttempts = 5;

    public const int DefaultLockoutMinutes = 15;

    /// <summary>The settings with every key at its default.</summary>
    public static DashboardSecuritySettings Default { get; } =
        new(DefaultPasswordMinLength, DefaultLockoutMaxFailedAttempts, TimeSpan.FromMinutes(DefaultLockoutMinutes));

    /// <summary>
    /// Reads the keys; an empty or missing key takes its default. <c>Dashboard:Password:MinLength</c> must be
    /// 8 to 128, <c>Dashboard:Lockout:MaxFailedAttempts</c> 0 (never lock) to 100 and
    /// <c>Dashboard:Lockout:Minutes</c> 1 to 1440 (a day), or composition fails here.
    /// </summary>
    /// <exception cref="InvalidOperationException">A key holds something other than a whole number in its range.</exception>
    public static DashboardSecuritySettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var minLength = ReadInt(configuration, PasswordMinLengthKey, DefaultPasswordMinLength, 8, Services.PasswordPolicy.MaxLength);
        var maxAttempts = ReadInt(configuration, LockoutMaxFailedAttemptsKey, DefaultLockoutMaxFailedAttempts, 0, 100);
        var minutes = ReadInt(configuration, LockoutMinutesKey, DefaultLockoutMinutes, 1, 1440);
        return new DashboardSecuritySettings(minLength, maxAttempts, TimeSpan.FromMinutes(minutes));
    }

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue, int min, int max)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < min
            || parsed > max)
        {
            throw new InvalidOperationException(
                $"Invalid {key} '{value}': expected a whole number from {min} to {max}, or empty for the default of {defaultValue}.");
        }

        return parsed;
    }
}
