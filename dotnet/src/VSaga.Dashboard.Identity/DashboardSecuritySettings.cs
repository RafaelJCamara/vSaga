using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace VSaga.Dashboard.Identity;

/// <summary>
/// The dashboard's password, lockout, session and API-key settings, read once from configuration while
/// composing and validated there, then registered as a plain singleton. A value out of range fails
/// composition, naming the key and the value, instead of surfacing at the first sign-in.
/// </summary>
/// <param name="PasswordMinLength">The shortest password the policy accepts; at most <see cref="Services.PasswordPolicy.MaxLength"/>.</param>
/// <param name="LockoutMaxFailedAttempts">Consecutive failed sign-ins that lock an account; 0 never locks.</param>
/// <param name="LockoutDuration">How long a locked account refuses sign-in.</param>
public sealed record DashboardSecuritySettings(int PasswordMinLength, int LockoutMaxFailedAttempts, TimeSpan LockoutDuration)
{
    public const string PasswordMinLengthKey = "Dashboard:Password:MinLength";

    public const string LockoutMaxFailedAttemptsKey = "Dashboard:Lockout:MaxFailedAttempts";

    public const string LockoutMinutesKey = "Dashboard:Lockout:Minutes";

    public const string SessionCookieNameKey = "Dashboard:Session:CookieName";

    public const string SessionIdleTimeoutMinutesKey = "Dashboard:Session:IdleTimeoutMinutes";

    public const string SessionAbsoluteTimeoutHoursKey = "Dashboard:Session:AbsoluteTimeoutHours";

    public const string RequireHttpsKey = "Dashboard:Session:RequireHttps";

    public const string ApiKeyRoleKey = "Dashboard:ApiKeyRole";

    public const string AuthPerMinuteKey = "Dashboard:RateLimit:AuthPerMinute";

    public const int DefaultPasswordMinLength = 12;

    public const int DefaultAuthPerMinute = 20;

    public const int DefaultLockoutMaxFailedAttempts = 5;

    public const int DefaultLockoutMinutes = 15;

    public const string DefaultSessionCookieName = "vsaga.session";

    public const int DefaultSessionIdleTimeoutMinutes = 480;

    public const int DefaultSessionAbsoluteTimeoutHours = 24;

    /// <summary>The built-in role the API key acts as unless <see cref="ApiKeyRoleKey"/> names another.</summary>
    public const string DefaultApiKeyRole = "Viewer";

    /// <summary>The prefix browsers accept only on a Secure, host-only cookie with path <c>/</c>; added when <see cref="RequireHttps"/> is true.</summary>
    public const string HostCookiePrefix = "__Host-";

    private const int MaxCookieNameLength = 128;

    private const int MaxRoleNameLength = 64;

    /// <summary>The settings with every key at its default.</summary>
    public static DashboardSecuritySettings Default { get; } =
        new(DefaultPasswordMinLength, DefaultLockoutMaxFailedAttempts, TimeSpan.FromMinutes(DefaultLockoutMinutes));

    /// <summary>The session cookie's configured name, without the <see cref="HostCookiePrefix"/>.</summary>
    public string SessionCookieName { get; init; } = DefaultSessionCookieName;

    /// <summary>How long a session lasts without a request; each request slides it.</summary>
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromMinutes(DefaultSessionIdleTimeoutMinutes);

    /// <summary>How long a session lasts after sign-in, however active it is.</summary>
    public TimeSpan SessionAbsoluteTimeout { get; init; } = TimeSpan.FromHours(DefaultSessionAbsoluteTimeoutHours);

    /// <summary>
    /// True when the dashboard is served over HTTPS only: the session cookie is always Secure and carries the
    /// <see cref="HostCookiePrefix"/>, and HSTS is sent.
    /// </summary>
    public bool RequireHttps { get; init; }

    /// <summary>The name of the built-in or custom role the API key acts as; <c>access.manage</c> is never held.</summary>
    public string ApiKeyRole { get; init; } = DefaultApiKeyRole;

    /// <summary>
    /// Sign-in and password-change attempts allowed per minute for one client address and one username;
    /// first-run setup gets the same number per client address.
    /// </summary>
    public int AuthPerMinute { get; init; } = DefaultAuthPerMinute;

    /// <summary>The name the session cookie is written under: <see cref="SessionCookieName"/>, prefixed when <see cref="RequireHttps"/> is true.</summary>
    public string EffectiveSessionCookieName => RequireHttps ? HostCookiePrefix + SessionCookieName : SessionCookieName;

    /// <summary>
    /// Reads the keys; an empty or missing key takes its default. <c>Dashboard:Password:MinLength</c> must be
    /// 8 to 128, <c>Dashboard:Lockout:MaxFailedAttempts</c> 0 (never lock) to 100,
    /// <c>Dashboard:Lockout:Minutes</c> 1 to 1440 (a day), <c>Dashboard:Session:IdleTimeoutMinutes</c> 1 to
    /// 10080 (a week), <c>Dashboard:Session:AbsoluteTimeoutHours</c> 1 to 720 (30 days),
    /// <c>Dashboard:Session:RequireHttps</c> true or false, <c>Dashboard:Session:CookieName</c> 1 to 128 letters,
    /// digits, dots, hyphens and underscores not starting with <c>__</c> (the prefix is the setting's job),
    /// <c>Dashboard:ApiKeyRole</c> at most 64 characters, and <c>Dashboard:RateLimit:AuthPerMinute</c> 1 to
    /// 1000, or composition fails here.
    /// </summary>
    /// <exception cref="InvalidOperationException">A key holds something outside its range.</exception>
    public static DashboardSecuritySettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var minLength = ReadInt(configuration, PasswordMinLengthKey, DefaultPasswordMinLength, 8, Services.PasswordPolicy.MaxLength);
        var maxAttempts = ReadInt(configuration, LockoutMaxFailedAttemptsKey, DefaultLockoutMaxFailedAttempts, 0, 100);
        var minutes = ReadInt(configuration, LockoutMinutesKey, DefaultLockoutMinutes, 1, 1440);
        return new DashboardSecuritySettings(minLength, maxAttempts, TimeSpan.FromMinutes(minutes))
        {
            SessionCookieName = ReadCookieName(configuration[SessionCookieNameKey]),
            SessionIdleTimeout = TimeSpan.FromMinutes(
                ReadInt(configuration, SessionIdleTimeoutMinutesKey, DefaultSessionIdleTimeoutMinutes, 1, 10080)),
            SessionAbsoluteTimeout = TimeSpan.FromHours(
                ReadInt(configuration, SessionAbsoluteTimeoutHoursKey, DefaultSessionAbsoluteTimeoutHours, 1, 720)),
            RequireHttps = ReadBool(configuration, RequireHttpsKey),
            ApiKeyRole = ReadApiKeyRole(configuration[ApiKeyRoleKey]),
            AuthPerMinute = ReadInt(configuration, AuthPerMinuteKey, DefaultAuthPerMinute, 1, 1000),
        };
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

    private static bool ReadBool(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return bool.TryParse(value.Trim(), out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Invalid {key} '{value}': expected true or false, or empty for the default of false.");
    }

    private static string ReadCookieName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DefaultSessionCookieName;

        var name = value.Trim();
        if (name.Length > MaxCookieNameLength
            || name.StartsWith("__", StringComparison.Ordinal)
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            throw new InvalidOperationException(
                $"Invalid {SessionCookieNameKey} '{value}': expected 1 to {MaxCookieNameLength} letters, digits, dots, hyphens or "
                + $"underscores, not starting with '__' ({RequireHttpsKey} adds the {HostCookiePrefix} prefix), or empty for the "
                + $"default of {DefaultSessionCookieName}.");
        }

        return name;
    }

    private static string ReadApiKeyRole(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DefaultApiKeyRole;

        var role = value.Trim();
        return role.Length <= MaxRoleNameLength
            ? role
            : throw new InvalidOperationException(
                $"Invalid {ApiKeyRoleKey} '{value}': a role name is at most {MaxRoleNameLength} characters; empty means {DefaultApiKeyRole}.");
    }
}
