using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IPNetwork = System.Net.IPNetwork;

namespace VSaga.Dashboard.Api.Hosting;

/// <summary>
/// The dashboard API's edge settings, read once from configuration while composing and validated there.
/// <paramref name="WebOrigin"/> is a normalised origin (<c>scheme://host[:port]</c>, no trailing slash), or
/// null when no cross-origin browser client is allowed. <paramref name="TrustedProxies"/> lists the
/// networks whose forwarded headers are honoured (a single address is a /32 or /128 network); empty means
/// forwarded headers are ignored from every peer.
/// </summary>
internal sealed record DashboardEdgeSettings(string? WebOrigin, IReadOnlyList<IPNetwork> TrustedProxies)
{
    /// <summary>
    /// Whether forwarded headers from <paramref name="peer"/> are honoured: the same test the
    /// forwarded-headers middleware applies. <see cref="IPNetwork.Contains"/> also matches an IPv4 peer that a
    /// dual-mode socket reports as IPv4-mapped IPv6.
    /// </summary>
    internal bool TrustsPeer(IPAddress peer) => TrustedProxies.Any(network => network.Contains(peer));
}

/// <summary>
/// The only code that reads <c>Dashboard:WebOrigin</c> and <c>Dashboard:TrustedProxies</c>, and the only
/// place that wires CORS and forwarded headers. The bundled UI reaches the API through its own origin (the
/// dev server proxies <c>/api</c> and <c>/hubs</c>, and so does the web image's nginx), so it needs no CORS
/// at all: the policy exists only when an operator opts in by naming one origin. The old fallback to
/// <c>http://localhost:4200</c> is gone on purpose. Browsers scope cookies by host, not port, so a
/// credentialed policy for a fixed localhost port would let whatever runs there (another stack's UI, any
/// dev server) read this API with the user's session.
/// <para>
/// Forwarded headers matter for the scheme (Secure cookies) and the client address (per-address sign-in
/// limits): behind nginx every browser arrives over HTTP from the proxy's address. Trust is explicit and
/// off by default, there is no "trust everyone" value, and only the nearest hop is honoured.
/// </para>
/// </summary>
internal static class DashboardEdge
{
    internal const string CorsPolicy = "Dashboard";
    internal const string WebOriginKey = "Dashboard:WebOrigin";
    internal const string TrustedProxiesKey = "Dashboard:TrustedProxies";

    /// <summary>
    /// Reads and validates the edge keys. An empty <c>Dashboard:WebOrigin</c> means none; anything else
    /// must be an absolute http or https URI with no user info, path, query or fragment, or composition
    /// fails here, naming the key and the value, rather than at the first cross-origin request. The stored
    /// origin is lower-cased, without a default port or a trailing slash: the form a browser sends.
    /// <c>Dashboard:TrustedProxies</c> is a comma-separated list of IP addresses or CIDR networks
    /// (whitespace around entries is ignored; empty means none); a malformed entry fails composition and
    /// is named in the message.
    /// </summary>
    internal static DashboardEdgeSettings Read(IConfiguration configuration) =>
        new(ReadWebOrigin(configuration[WebOriginKey]), ReadTrustedProxies(configuration[TrustedProxiesKey]));

    /// <summary>
    /// Registers the settings singleton, the credentialed CORS policy only when an origin is set, and the
    /// forwarded-headers options only when a proxy is trusted.
    /// </summary>
    internal static IServiceCollection AddDashboardEdge(this IServiceCollection services, DashboardEdgeSettings settings)
    {
        services.AddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<UntrustedForwardedHeadersWarning>();

        if (settings.WebOrigin is { } origin)
        {
            services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
                policy.WithOrigins(origin).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        }

        if (settings.TrustedProxies.Count > 0)
            services.Configure<ForwardedHeadersOptions>(options => ConfigureForwardedHeaders(options, settings.TrustedProxies));

        return services;
    }

    /// <summary>
    /// The edge middleware; call it first, before authentication. Warns about forwarded headers from an
    /// untrusted peer (it must see the connection's own address, so it runs before they are applied), then
    /// applies them when a proxy is trusted, then CORS when an origin is set.
    /// </summary>
    internal static WebApplication UseDashboardEdge(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<DashboardEdgeSettings>();
        var warning = app.Services.GetRequiredService<UntrustedForwardedHeadersWarning>();

        app.Use((context, next) =>
        {
            warning.Inspect(context);
            return next(context);
        });

        if (settings.TrustedProxies.Count > 0)
            app.UseForwardedHeaders();

        if (settings.WebOrigin is not null)
            app.UseCors(CorsPolicy);

        return app;
    }

    // Scheme and client address only: Host is passed through by the proxy unchanged. ForwardLimit 1 takes
    // the right-most value, the one the trusted proxy itself appended; anything to its left was written by
    // the client and could be forged. Both default lists (loopback) are cleared so only the configured
    // networks are trusted.
    private static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IReadOnlyList<IPNetwork> trustedProxies)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var network in trustedProxies)
            options.KnownIPNetworks.Add(network);
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

    private static IReadOnlyList<IPNetwork> ReadTrustedProxies(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var entries = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var networks = new List<IPNetwork>(entries.Length);
        foreach (var entry in entries)
        {
            networks.Add(ParseTrustedProxy(entry)
                ?? throw new InvalidOperationException(
                    $"Invalid {TrustedProxiesKey} entry '{entry}': expected an IP address such as 10.0.0.5 or a CIDR "
                    + "network such as 10.0.0.0/8 with no host bits set, in a comma-separated list, or empty to trust no proxy."));
        }

        return networks;
    }

    /// <summary>
    /// One entry: an address (a /32 or /128 network) or a CIDR network, or null when it is neither. IPv4
    /// must be written in canonical dotted-decimal form, the form it prints back as, because the address
    /// parser also reads forms an operator never means: shorthand such as <c>10</c> for 0.0.0.10, a part
    /// with leading zeros as octal (<c>10.0.0.010</c> is 10.0.0.8) and hex parts (<c>0x0A.0.0.1</c>).
    /// Scoped IPv6 addresses are refused for the same reason.
    /// </summary>
    private static IPNetwork? ParseTrustedProxy(string entry)
    {
        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        var addressPart = slash < 0 ? entry : entry[..slash];
        if (!IPAddress.TryParse(addressPart, out var address)
            || (address.AddressFamily == AddressFamily.InterNetwork
                && !string.Equals(address.ToString(), addressPart, StringComparison.Ordinal))
            || (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0))
        {
            return null;
        }

        if (slash < 0)
            return new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);

        // The parser accepts host bits after the prefix (10.0.0.1/8); refuse them rather than guess whether
        // the operator meant the network or the one address.
        return IPNetwork.TryParse(entry, out var network) && network.BaseAddress.Equals(address) ? network : null;
    }
}
