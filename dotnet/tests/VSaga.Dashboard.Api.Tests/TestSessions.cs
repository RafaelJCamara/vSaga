using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Users written straight to a test host's identity store, and session cookies for them protected with that
/// host's own cookie options and Data Protection keys: exactly what the cookie handler reads, without a
/// sign-in endpoint.
/// </summary>
internal static class TestSessions
{
    public static async Task<DashboardUser> CreateUserAsync(
        IServiceProvider services, string username, bool isEnabled = true, bool mustChangePassword = false, params AccessGrant[] grants)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new DashboardUser(
            Guid.NewGuid(), username, username, "not-a-password-hash", SecurityStamps.New(), isEnabled, mustChangePassword,
            FailedSignInCount: 0, LockoutEndUtc: null, LastSignInAtUtc: null, now, now, grants);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>().CreateUserAsync(user, CancellationToken.None);
        return user;
    }

    /// <summary>A user who can sign in with <paramref name="password"/>, hashed with the host's own hasher.</summary>
    public static async Task<DashboardUser> CreateUserWithPasswordAsync(
        IServiceProvider services,
        string username,
        string password,
        bool isEnabled = true,
        DateTimeOffset? lockoutEndUtc = null,
        params AccessGrant[] grants)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new DashboardUser(
            Guid.NewGuid(), username, username + " (display)", PasswordHash: "", SecurityStamps.New(), isEnabled, MustChangePassword: false,
            FailedSignInCount: 0, lockoutEndUtc, LastSignInAtUtc: null, now, now, grants);
        user = user with { PasswordHash = services.GetRequiredService<IPasswordHasher<DashboardUser>>().HashPassword(user, password) };
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>().CreateUserAsync(user, CancellationToken.None);
        return user;
    }

    public static AccessGrant AllTypes(Guid roleId) => new(roleId, AllSagaTypes: true, []);

    public static AccessGrant ForTypes(Guid roleId, params string[] sagaTypes) => new(roleId, AllSagaTypes: false, sagaTypes);

    /// <summary>The session cookie's name in this host.</summary>
    public static string CookieName(IServiceProvider services) => Options(services).Cookie.Name!;

    /// <summary>
    /// A <c>Cookie</c> header value carrying a session for <paramref name="user"/> as it stands (or with
    /// <paramref name="stamp"/>), signed in at <paramref name="signedInAt"/>; null leaves the sign-in time out.
    /// The ticket itself is fresh unless <paramref name="issuedUtc"/> and <paramref name="expiresUtc"/> say
    /// otherwise: by default issued now, expiring in an hour.
    /// </summary>
    public static string CookieHeader(
        IServiceProvider services,
        DashboardUser user,
        DateTimeOffset? signedInAt,
        string? stamp = null,
        DateTimeOffset? issuedUtc = null,
        DateTimeOffset? expiresUtc = null)
    {
        var options = Options(services);
        var principal = DashboardClaims.ForUser(stamp is null ? user : user with { SecurityStamp = stamp }, DashboardAuthExtensions.CookieScheme);
        var now = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties { IssuedUtc = issuedUtc ?? now, ExpiresUtc = expiresUtc ?? now.AddHours(1) };
        if (signedInAt is { } at)
            DashboardCookieEvents.SetSignedInAt(properties, at);

        var ticket = new AuthenticationTicket(principal, properties, DashboardAuthExtensions.CookieScheme);
        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }

    /// <summary>A GET carrying <paramref name="cookieHeader"/>.</summary>
    public static HttpRequestMessage Get(string path, string cookieHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookieHeader);
        return request;
    }

    /// <summary>The ticket in the session cookie this response sets, or null when it sets none.</summary>
    public static AuthenticationTicket? IssuedTicket(IServiceProvider services, HttpResponseMessage response)
    {
        var options = Options(services);
        var prefix = options.Cookie.Name + "=";
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
            return null;

        var cookie = cookies.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal) && !c.StartsWith(prefix + ";", StringComparison.Ordinal));
        if (cookie is null)
            return null;

        var value = cookie[prefix.Length..].Split(';', 2)[0];
        return options.TicketDataFormat.Unprotect(value);
    }

    private static CookieAuthenticationOptions Options(IServiceProvider services) =>
        services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(DashboardAuthExtensions.CookieScheme);
}
