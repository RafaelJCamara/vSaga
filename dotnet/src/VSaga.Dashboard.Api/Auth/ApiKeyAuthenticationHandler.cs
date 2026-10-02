using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Auth;

public static class ApiKeyAuthenticationDefaults
{
    public const string SchemeName = DashboardClaims.ApiKeyAuthenticationType;
    public const string HeaderName = "X-Api-Key";

    /// <summary>SignalR's JS client sends the <c>accessTokenFactory</c> token as this header
    /// (<c>Bearer &lt;token&gt;</c>) on plain HTTP calls — notably the negotiate POST.</summary>
    public const string BearerPrefix = "Bearer ";

    /// <summary>SignalR clients can't set custom headers (including Authorization) on the WebSocket
    /// upgrade itself — only on the initial negotiate call — so the JS client falls back to sending
    /// the <c>accessTokenFactory</c> token via this query string parameter for the upgrade request.
    /// Accepted on hub endpoints only: anywhere else a cross-site form or link could supply it.</summary>
    public const string QueryStringParameterName = "access_token";
}

/// <summary>Where a request carries an API key, if it carries one.</summary>
public static class ApiKeyCredentials
{
    /// <summary>
    /// True when the request presents an API key in a form this endpoint accepts, so the dashboard scheme
    /// sends it to the API-key handler rather than the cookie handler.
    /// </summary>
    public static bool IsPresent(HttpContext context) => Find(context) is not null;

    /// <summary>
    /// The presented key: the <c>X-Api-Key</c> header, else an <c>Authorization: Bearer</c> header, else, on a
    /// hub endpoint only, the <c>access_token</c> query string. Hub endpoints are recognised by their
    /// metadata after routing, so the check holds whatever the path's case.
    /// </summary>
    public static string? Find(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        var key = request.Headers[ApiKeyAuthenticationDefaults.HeaderName].FirstOrDefault()
            ?? GetBearerToken(request.Headers.Authorization.FirstOrDefault());
        if (key is null && IsHubEndpoint(context))
            key = request.Query[ApiKeyAuthenticationDefaults.QueryStringParameterName].FirstOrDefault();

        return string.IsNullOrEmpty(key) ? null : key;
    }

    /// <summary>True when the request was routed to a SignalR hub endpoint (negotiate or connect).</summary>
    public static bool IsHubEndpoint(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<HubMetadata>() is not null;

    private static string? GetBearerToken(string? authorizationHeader) =>
        authorizationHeader?.StartsWith(ApiKeyAuthenticationDefaults.BearerPrefix, StringComparison.OrdinalIgnoreCase) == true
            ? authorizationHeader[ApiKeyAuthenticationDefaults.BearerPrefix.Length..]
            : null;
}

#pragma warning disable S2094 // no options of our own to add — required as a distinct type by AddScheme<TOptions, THandler>
public sealed class ApiKeyAuthenticationSchemeOptions : AuthenticationSchemeOptions;
#pragma warning restore S2094

/// <summary>
/// Validates a single shared secret (<c>Dashboard:ApiKey</c> in configuration) presented as described by
/// <see cref="ApiKeyCredentials.Find"/>. Fails closed: an unconfigured key denies every request rather than
/// silently disabling auth. A matching key acts as the role <c>Dashboard:ApiKeyRole</c> names (Viewer by
/// default) for every saga type, never with <c>access.manage</c>; a role that cannot be found fails
/// authentication rather than granting anything.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration,
    ICallerAccessResolver resolver,
    DashboardSecuritySettings settings)
    : AuthenticationHandler<ApiKeyAuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configuredKey = configuration["Dashboard:ApiKey"];
        if (string.IsNullOrEmpty(configuredKey))
            return AuthenticateResult.Fail("Dashboard:ApiKey is not configured.");

        var providedKey = ApiKeyCredentials.Find(Context);
        if (providedKey is null || !FixedTimeEquals(providedKey, configuredKey))
            return AuthenticateResult.Fail("Missing or invalid API key.");

        var principal = DashboardClaims.ForApiKey();
        var caller = await resolver.ResolveAsync(principal, Context.RequestAborted);
        if (caller is null)
        {
            return AuthenticateResult.Fail(
                $"{DashboardSecuritySettings.ApiKeyRoleKey} '{settings.ApiKeyRole}' names no built-in role, and no custom role "
                + "of that name could be read from the identity store.");
        }

        Context.SetCaller(caller);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, ApiKeyAuthenticationDefaults.SchemeName));
    }

    /// <summary>
    /// The default challenge writes a bare 401 with no body, which leaves the most common setup mistake —
    /// a caller who simply forgot the key — with nothing to go on but a status code. This writes the body
    /// the cookie scheme writes too (<see cref="AuthProblems.WriteUnauthorizedAsync"/>).
    /// </summary>
    /// <remarks>
    /// Deliberately identical for a missing key, a wrong key, an unknown role and an unconfigured server:
    /// the reason is logged server-side (<see cref="AuthenticateResult.Fail(string)"/>) but never echoed, so
    /// a response can't be used to probe whether a particular key was close to correct.
    /// </remarks>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        AuthProblems.WriteUnauthorizedAsync(Context);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        AuthProblems.WriteForbiddenAsync(Context, Context.GetCaller(), permission: null, sagaType: null);

    private static bool FixedTimeEquals(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        return bytesA.Length == bytesB.Length && CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}
