using System.Data.Common;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VSaga.Dashboard.Api.Hosting;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Api.Auth;

/// <summary>
/// Authentication and authorization for the dashboard API (design §8.3 and §8.5). One policy scheme,
/// <see cref="PolicyScheme"/>, sends a request that presents an API key to the API-key handler and every
/// other request to the session cookie handler; both write the same 401 body. Every endpoint needs an
/// authenticated caller unless it opts out explicitly, so an endpoint mapped without thinking about access
/// is protected, not open.
/// </summary>
internal static partial class DashboardAuthExtensions
{
    /// <summary>The default scheme: forwards to <see cref="CookieScheme"/> or <see cref="ApiKeyAuthenticationDefaults.SchemeName"/>.</summary>
    internal const string PolicyScheme = "Dashboard";

    /// <summary>The session cookie scheme.</summary>
    internal const string CookieScheme = "Cookie";

    /// <summary>The shortest API key that does not draw a warning at start.</summary>
    internal const int MinimumApiKeyLength = 24;

    /// <summary>
    /// Reads and validates the security settings (composition fails on a bad value, or when
    /// <c>Dashboard:Session:RequireHttps</c> is true with no trusted proxy to say a request arrived over
    /// HTTPS), registers them as a singleton, and adds the schemes, the policies and the 403 writer.
    /// </summary>
    internal static IServiceCollection AddDashboardAuth(this IServiceCollection services, IConfiguration configuration, DashboardEdgeSettings edge)
    {
        var settings = DashboardSecuritySettings.Read(configuration);
        if (settings.RequireHttps && edge.TrustedProxies.Count == 0)
        {
            throw new InvalidOperationException(
                $"{DashboardSecuritySettings.RequireHttpsKey} is true but {DashboardEdge.TrustedProxiesKey} is empty: behind a TLS-terminating "
                + "proxy the API sees plain HTTP unless it trusts that proxy's X-Forwarded-Proto. Name the proxy's address or network in "
                + $"{DashboardEdge.TrustedProxiesKey}, or set {DashboardSecuritySettings.RequireHttpsKey} to false.");
        }

        services.AddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<CallerAccessResolver>();
        services.AddScoped<ICallerAccessResolver>(provider => provider.GetRequiredService<CallerAccessResolver>());
        services.AddScoped<DashboardCookieEvents>();
        AddSchemes(services, settings);

        var authorization = services.AddAuthorizationBuilder();
        var authenticated = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        authorization.SetFallbackPolicy(authenticated).SetDefaultPolicy(authenticated);
        DashboardPolicies.Add(authorization);
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, DashboardAuthorizationResultHandler>();

        if (settings.RequireHttps)
            services.AddHsts(_ => { });

        return services;
    }

    /// <summary>HSTS when HTTPS is required, then authentication and authorization.</summary>
    internal static WebApplication UseDashboardAuth(this WebApplication app)
    {
        if (app.Services.GetRequiredService<DashboardSecuritySettings>().RequireHttps)
            app.UseHsts();

        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    /// <summary>
    /// Warns, once at start and only when an API key is configured, about a key short enough to guess (key
    /// guesses are not rate limited), a <c>Dashboard:ApiKeyRole</c> that names no role (every request with
    /// the key then gets 401), and a role holding <c>access.manage</c>, which the key never gets.
    /// </summary>
    internal static async Task WarnAboutApiKeyAsync(this WebApplication app)
    {
        var key = app.Configuration["Dashboard:ApiKey"];
        if (string.IsNullOrEmpty(key))
            return;

        var logger = app.Services.GetRequiredService<ILogger<ApiKeyAuthenticationHandler>>();
        if (key.Length < MinimumApiKeyLength)
            LogShortApiKey(logger, MinimumApiKeyLength);

        var role = app.Services.GetRequiredService<DashboardSecuritySettings>().ApiKeyRole;
        await using var scope = app.Services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<CallerAccessResolver>();
        DashboardRole? found;
        try
        {
            found = await resolver.FindApiKeyRoleAsync(app.Lifetime.ApplicationStopping);
        }
        catch (Exception ex) when (ex is DbException or IdentityUnavailableException)
        {
            // A warning must never stop the API: an unreadable store reads as a role that cannot be found.
            found = null;
        }

        if (found is null)
            LogUnknownApiKeyRole(logger, role);
        else if (found.Permissions.Contains(Permissions.AccessManage, StringComparer.Ordinal))
            LogApiKeyRoleManagesAccess(logger, role);
    }

    private static void AddSchemes(IServiceCollection services, DashboardSecuritySettings settings)
    {
        services.AddAuthentication(PolicyScheme)
            .AddPolicyScheme(PolicyScheme, "Session cookie or API key", options =>
                options.ForwardDefaultSelector = context =>
                    ApiKeyCredentials.IsPresent(context) ? ApiKeyAuthenticationDefaults.SchemeName : CookieScheme)
            .AddCookie(CookieScheme, options => ConfigureCookie(options, settings))
            .AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationDefaults.SchemeName, configureOptions: null);
    }

    // HttpOnly and SameSite=Strict always; Secure on HTTPS requests, or always (with the __Host- prefix,
    // which needs Secure, path / and no domain) when HTTPS is required. Not persistent: the sign-in endpoint
    // issues it without an expiry, so it ends with the browser session or the sliding idle timeout, and the
    // absolute lifetime is checked on every request by DashboardCookieEvents.
    private static void ConfigureCookie(CookieAuthenticationOptions options, DashboardSecuritySettings settings)
    {
        options.Cookie.Name = settings.EffectiveSessionCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.Cookie.SecurePolicy = settings.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = settings.SessionIdleTimeout;
        options.SlidingExpiration = true;
        options.EventsType = typeof(DashboardCookieEvents);
    }

    [LoggerMessage(EventId = 7301, EventName = "ApiKeyShort", Level = LogLevel.Warning,
        Message = "Dashboard:ApiKey is shorter than {MinimumLength} characters; API key guesses are not rate limited, so use a long random key")]
    private static partial void LogShortApiKey(ILogger logger, int minimumLength);

    [LoggerMessage(EventId = 7302, EventName = "ApiKeyRoleUnknown", Level = LogLevel.Warning,
        Message = "Dashboard:ApiKeyRole '{Role}' names no built-in role and no custom role could be read with that name; every request with the API key gets 401 until it does")]
    private static partial void LogUnknownApiKeyRole(ILogger logger, string role);

    [LoggerMessage(EventId = 7303, EventName = "ApiKeyRoleManagesAccess", Level = LogLevel.Warning,
        Message = "Dashboard:ApiKeyRole '{Role}' includes access.manage, which the API key never holds; it gets the role's other permissions")]
    private static partial void LogApiKeyRoleManagesAccess(ILogger logger, string role);
}
