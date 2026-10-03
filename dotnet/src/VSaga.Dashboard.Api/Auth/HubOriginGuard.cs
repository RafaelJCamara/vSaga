using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using VSaga.Dashboard.Api.Hosting;

namespace VSaga.Dashboard.Api.Auth;

/// <summary>
/// The hub's cross-site guard (design §8.6). A browser cannot add the antiforgery header to a WebSocket
/// upgrade, so the hub is exempt from antiforgery (<see cref="AntiforgeryExemption.Hub"/>) and checked here
/// instead: a page on another origin (another site, or anything else on another port of this host, which
/// the session cookie does not keep apart) must not open a connection that rides the user's session.
/// <list type="bullet">
/// <item>Applies to endpoints carrying SignalR's hub metadata, after routing, so <c>/HUBS/SAGA</c> is checked
/// like <c>/hubs/saga</c>; every other endpoint is left to antiforgery.</item>
/// <item>Allowed: no <c>Origin</c> header (not a browser, which always sends one on a WebSocket or a POST);
/// <c>Origin</c> equal to the request's own <c>{scheme}://{Host}</c>, the scheme being the one a trusted
/// proxy forwarded; or equal to <c>Dashboard:WebOrigin</c> when that is set.</item>
/// <item>Everything else is refused with 403, <c>Origin: null</c> (sandboxed frames, some redirects) and
/// several <c>Origin</c> headers included, and logged at Warning with the received and expected values, so
/// an operator whose proxy rewrites the scheme or host can see why.</item>
/// </list>
/// Scheme and host compare ignoring case, as they are defined; the configured origin is already normalised.
/// </summary>
internal static partial class HubOriginGuard
{
    /// <summary>The guard middleware; call it after <c>UseDashboardEdge</c> (so the forwarded scheme applies) and before authentication.</summary>
    internal static WebApplication UseHubOriginGuard(this WebApplication app)
    {
        var webOrigin = app.Services.GetRequiredService<DashboardEdgeSettings>().WebOrigin;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HubOriginGuard).FullName!);
        app.Use((context, next) => GuardAsync(context, next, webOrigin, logger));
        return app;
    }

    /// <summary>The origin a same-origin page sends for this request: <c>{scheme}://{Host}</c>.</summary>
    internal static string OwnOrigin(HttpRequest request) => $"{request.Scheme}://{request.Host.Value}";

    /// <summary>True when a request with these <c>Origin</c> headers may reach the hub.</summary>
    internal static bool IsAllowed(StringValues origins, string ownOrigin, string? webOrigin) =>
        origins.Count == 0
        || (origins.Count == 1
            && origins[0] is { } origin
            && (string.Equals(origin, ownOrigin, StringComparison.OrdinalIgnoreCase)
                || (webOrigin is not null && string.Equals(origin, webOrigin, StringComparison.OrdinalIgnoreCase))));

    private static async Task GuardAsync(HttpContext context, RequestDelegate next, string? webOrigin, ILogger logger)
    {
        if (!ApiKeyCredentials.IsHubEndpoint(context))
        {
            await next(context);
            return;
        }

        var origins = context.Request.Headers[HeaderNames.Origin];
        var ownOrigin = OwnOrigin(context.Request);
        if (IsAllowed(origins, ownOrigin, webOrigin))
        {
            await next(context);
            return;
        }

        LogRejected(logger, context.Request.Path, origins.ToString(), webOrigin is null ? ownOrigin : $"{ownOrigin} or {webOrigin}");
        await AuthProblems.ForeignHubOrigin().ExecuteAsync(context);
    }

    [LoggerMessage(EventId = 7311, EventName = "HubOriginRejected", Level = LogLevel.Warning,
        Message = "Refused a hub request to {Path} from origin '{Origin}'; expected no Origin or {Expected}. Behind a proxy, check that it passes the browser's Host and, from a trusted proxy (Dashboard:TrustedProxies), X-Forwarded-Proto")]
    private static partial void LogRejected(ILogger logger, PathString path, string origin, string expected);
}
