using Microsoft.AspNetCore.Antiforgery;
using VSaga.Dashboard.Api.Endpoints;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Auth;

/// <summary>
/// Endpoint metadata exempting an endpoint from <see cref="AntiforgeryEnforcement"/>. Only the SignalR hub
/// carries it: a browser cannot add a header to a WebSocket upgrade, so the hub is guarded by its own
/// origin check instead. Anything else that changes state is validated, whatever its path.
/// </summary>
/// <param name="Reason">Why the endpoint is exempt, for whoever reads the endpoint's metadata.</param>
public sealed record AntiforgeryExemption(string Reason)
{
    /// <summary>The marker on the hub's endpoints (negotiate and connect).</summary>
    public static AntiforgeryExemption Hub { get; } =
        new("SignalR hub: a WebSocket upgrade cannot carry the antiforgery header; the hub's origin check guards it.");
}

/// <summary>
/// Cross-site request forgery protection (design §8.3). The session cookie is <c>SameSite=Strict</c>, but
/// cookies are not scoped by port, so another site on the same host could still ride the session; every
/// unsafe request therefore needs the antiforgery request token in <see cref="HeaderName"/>, which only
/// script on the dashboard's own origin can read (from the <see cref="RequestTokenCookieName"/> cookie).
/// <list type="bullet">
/// <item>Decided per endpoint, after routing and authentication: every method other than GET, HEAD, OPTIONS
/// and TRACE on a matched endpoint is validated unless the endpoint carries <see cref="AntiforgeryExemption"/>.
/// No path prefix is involved, so <c>/API/...</c> is checked like <c>/api/...</c>.</item>
/// <item>A request authenticated by the API key is exempt only when the key arrived in a header
/// (<c>X-Api-Key</c> or <c>Authorization</c>), which a cross-site form cannot set.</item>
/// <item>The token is bound to the signed-in user, so it is (re)issued by
/// <see cref="IssueRequestToken"/> after <c>HttpContext.User</c> is set: by the session, login, logout and
/// password endpoints.</item>
/// </list>
/// The framework's <c>UseAntiforgery</c> is not enough on its own: it validates form-bound endpoints only.
/// </summary>
public static partial class AntiforgeryEnforcement
{
    /// <summary>The request header the token is sent in; Angular's default.</summary>
    public const string HeaderName = "X-XSRF-TOKEN";

    /// <summary>
    /// The readable cookie the request token is issued in; Angular's default. One name across stacks: a token
    /// from another stack fails validation, and the SPA then reads the session again and retries once.
    /// </summary>
    public const string RequestTokenCookieName = "XSRF-TOKEN";

    /// <summary>Appended to the session cookie's name to name the antiforgery cookie token's cookie.</summary>
    public const string CookieTokenSuffix = ".af";

    /// <summary>
    /// Configures the antiforgery services: the header only (never a form field), and the cookie token in an
    /// HttpOnly, <c>SameSite=Strict</c> cookie named after the session cookie, Secure as the session cookie is.
    /// </summary>
    internal static IServiceCollection AddDashboardAntiforgery(this IServiceCollection services, DashboardSecuritySettings settings)
    {
        services.AddAntiforgery(options =>
        {
            options.HeaderName = HeaderName;
            options.SuppressReadingTokenFromFormBody = true;
            options.Cookie.Name = settings.EffectiveSessionCookieName + CookieTokenSuffix;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.Cookie.SecurePolicy = settings.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            // Antiforgery adds X-Frame-Options: SAMEORIGIN whenever it issues tokens. The API answers only JSON,
            // and framing is the edge's policy: nginx sends X-Frame-Options: DENY and frame-ancestors 'none' on
            // every response, so a second, conflicting X-Frame-Options would reach the browser, which may then
            // treat the header as invalid and ignore it.
            options.SuppressXFrameOptionsHeader = true;
        });
        return services;
    }

    /// <summary>The enforcement middleware; call it after authentication and authorization.</summary>
    internal static WebApplication UseAntiforgeryEnforcement(this WebApplication app)
    {
        app.Use(EnforceAsync);
        return app;
    }

    /// <summary>
    /// Issues a request token for the current <c>HttpContext.User</c> as the readable
    /// <see cref="RequestTokenCookieName"/> cookie (and the cookie token, when the request has no valid one).
    /// Call only once the identity store is ready: the tokens are protected with its key ring.
    /// </summary>
    public static void IssueRequestToken(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
        var settings = context.RequestServices.GetRequiredService<DashboardSecuritySettings>();
        // Not HttpOnly on purpose: the SPA reads it and echoes it in X-XSRF-TOKEN, and that echo is the check.
        // Secure whenever the request is HTTPS, and always when HTTPS is required, like the session cookie.
#pragma warning disable S3330, S2092 // see above
        var options = new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            Secure = settings.RequireHttps || context.Request.IsHttps,
        };
#pragma warning restore S3330, S2092
        context.Response.Cookies.Append(RequestTokenCookieName, tokens.RequestToken!, options);
    }

    /// <summary>True when this request must carry a valid antiforgery token.</summary>
    public static bool RequiresValidation(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method))
            return false;

        return context.GetEndpoint() is { } endpoint
            && endpoint.Metadata.GetMetadata<AntiforgeryExemption>() is null
            && !IsApiKeyFromHeader(context);
    }

    private static async Task EnforceAsync(HttpContext context, RequestDelegate next)
    {
        if (!RequiresValidation(context))
        {
            await next(context);
            return;
        }

        // The tokens are protected with the store's key ring: never validate (or mint a key) without it.
        var startup = context.RequestServices.GetRequiredService<IdentityStartup>();
        if (!await startup.EnsureReadyAsync(AuthEndpoints.IdentityReadyWait, context.RequestAborted))
        {
            await AuthProblems.IdentityUnavailable().ExecuteAsync(context);
            return;
        }

        if (!await context.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(context))
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AntiforgeryEnforcement).FullName!);
            LogRejected(logger, context.Request.Method, context.Request.Path);
            await AuthProblems.Antiforgery().ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static bool IsApiKeyFromHeader(HttpContext context) =>
        context.GetCaller() is { Kind: CallerKind.ApiKey }
        && (context.Request.Headers.ContainsKey(ApiKeyAuthenticationDefaults.HeaderName)
            || context.Request.Headers.Authorization.Count > 0);

    [LoggerMessage(EventId = 7310, EventName = "AntiforgeryRejected", Level = LogLevel.Debug,
        Message = "Antiforgery check failed for {Method} {Path}")]
    private static partial void LogRejected(ILogger logger, string method, PathString path);
}
