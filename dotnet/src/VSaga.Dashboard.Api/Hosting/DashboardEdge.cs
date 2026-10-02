using System.Net;

namespace VSaga.Dashboard.Api.Hosting;

/// <summary>
/// The dashboard API's edge settings, read once from configuration while composing and validated there.
/// <paramref name="WebOrigin"/> is a normalised origin (<c>scheme://host[:port]</c>, no trailing slash), or
/// null when no cross-origin browser client is allowed. <paramref name="TrustedProxies"/> lists the
/// networks whose forwarded headers are honoured; it is always empty until forwarded-header support lands.
/// </summary>
internal sealed record DashboardEdgeSettings(string? WebOrigin, IReadOnlyList<IPNetwork> TrustedProxies);

/// <summary>
/// The only code that reads <c>Dashboard:WebOrigin</c> (and, later, <c>Dashboard:TrustedProxies</c>), and
/// the only place that wires CORS. The bundled UI reaches the API through its own origin (the dev server
/// proxies <c>/api</c> and <c>/hubs</c>, and so will the web image's nginx), so it needs no CORS at all:
/// the policy exists only when an operator opts in by naming one origin. The old fallback to
/// <c>http://localhost:4200</c> is gone on purpose. Browsers scope cookies by host, not port, so a
/// credentialed policy for a fixed localhost port would let whatever runs there (another stack's UI, any
/// dev server) read this API with the user's session.
/// </summary>
internal static class DashboardEdge
{
    internal const string CorsPolicy = "Dashboard";
    internal const string WebOriginKey = "Dashboard:WebOrigin";

    /// <summary>
    /// Reads and validates the edge keys. An empty <c>Dashboard:WebOrigin</c> means none; anything else
    /// must be an absolute http or https URI with no user info, path, query or fragment, or composition
    /// fails here, naming the key and the value, rather than at the first cross-origin request. The stored
    /// origin is lower-cased, without a default port or a trailing slash: the form a browser sends.
    /// </summary>
    internal static DashboardEdgeSettings Read(IConfiguration configuration) =>
        new(ReadWebOrigin(configuration[WebOriginKey]), []);

    /// <summary>Registers the settings singleton, and the credentialed CORS policy only when an origin is set.</summary>
    internal static IServiceCollection AddDashboardEdge(this IServiceCollection services, DashboardEdgeSettings settings)
    {
        services.AddSingleton(settings);

        if (settings.WebOrigin is { } origin)
        {
            services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
                policy.WithOrigins(origin).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        }

        return services;
    }

    /// <summary>The edge middleware; call it first, before authentication. Adds CORS only when an origin is set.</summary>
    internal static WebApplication UseDashboardEdge(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<DashboardEdgeSettings>();

        if (settings.WebOrigin is not null)
            app.UseCors(CorsPolicy);

        return app;
    }

    private static string? ReadWebOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            || uri.UserInfo.Length > 0
            || !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal)
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"Invalid {WebOriginKey} '{value}': expected an origin such as https://dashboard.example.com "
                + "(an absolute http or https URI with no user info, path, query or fragment), or empty to turn CORS off.");
        }

        // scheme://host[:port], lower-cased by Uri and without a default port, which is the form a browser
        // sends in its Origin header; this also drops the trailing slash.
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
