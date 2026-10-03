using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Auth;

/// <summary>
/// The session cookie's events. Every request re-checks the session against the store: the user must exist,
/// be enabled and still hold the security stamp the cookie was issued under, and the session must be
/// younger than <see cref="DashboardSecuritySettings.SessionAbsoluteTimeout"/> however often it slid; the
/// ticket's expiry is capped at that point too, so a hub socket opened under it closes by then.
/// Failures answer the shared problem bodies instead of the cookie handler's redirects, which an API has no
/// use for.
/// </summary>
/// <remarks>
/// A session the store rejects is signed out (its cookie deleted) only when the store was ready before the
/// session was resolved. While it is not, nothing can be checked, so the request gets 401 but the cookie is
/// kept for when the store returns.
/// </remarks>
public sealed partial class DashboardCookieEvents(
    ICallerAccessResolver resolver,
    IIdentityReadiness readiness,
    DashboardSecuritySettings settings,
    TimeProvider timeProvider,
    ILogger<DashboardCookieEvents> logger) : CookieAuthenticationEvents
{
    /// <summary>
    /// The authentication property holding when the user signed in (round-trip format). It survives sliding
    /// renewal, so it bounds the session's whole life; a ticket without it is rejected.
    /// </summary>
    public const string SignedInAtItem = ".vsaga.signedInAt";

    /// <summary>Records the sign-in time on a new session's properties.</summary>
    public static void SetSignedInAt(AuthenticationProperties properties, DateTimeOffset signedInAt)
    {
        ArgumentNullException.ThrowIfNull(properties);
        properties.Items[SignedInAtItem] = signedInAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Principal is null || SignedInAt(context.Properties) is not { } signedInAt
            || timeProvider.GetUtcNow() - signedInAt > settings.SessionAbsoluteTimeout)
        {
            await RejectAsync(context, signOut: true, "the session is past its absolute lifetime or has no sign-in time");
            return;
        }

        CapExpiryAtAbsoluteLifetime(context.Properties, signedInAt + settings.SessionAbsoluteTimeout);

        // Readiness is read before resolving: it only ever goes from false to true, so a store that became
        // ready mid-resolution must not turn the resolver's "not ready" answer into a sign-out.
        var ready = readiness.IsReady;
        var caller = await resolver.ResolveAsync(context.Principal, context.HttpContext.RequestAborted);
        if (caller is null)
        {
            await RejectAsync(
                context,
                signOut: ready,
                ready ? "the user is missing or disabled, or the security stamp changed" : "the identity store is not ready");
            return;
        }

        context.HttpContext.SetCaller(caller);
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return AuthProblems.WriteUnauthorizedAsync(context.HttpContext);
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return AuthProblems.WriteForbiddenAsync(context.HttpContext, context.HttpContext.GetCaller(), permission: null, sagaType: null);
    }

    private static DateTimeOffset? SignedInAt(AuthenticationProperties properties) =>
        properties.Items.TryGetValue(SignedInAtItem, out var value)
        && DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var signedInAt)
            ? signedInAt
            : null;

    /// <summary>
    /// SignalR reads the ticket's <c>ExpiresUtc</c> when a hub connection opens and closes the socket then
    /// (<c>CloseOnAuthenticationExpiration</c>). That is the sliding idle expiry, so left alone an open socket
    /// would outlive the absolute lifetime by up to the idle timeout. This caps the expiry on this request's view
    /// of the ticket only: the cookie's own renewal works from a copy of the ticket taken before this event
    /// (the handler checks for renewal, and clones the ticket for it, before it calls <c>ValidatePrincipal</c>), provided
    /// <c>ValidatePrincipal</c> never sets <c>ShouldRenew</c>. The renewed cookie therefore keeps its full sliding
    /// expiry, which may end after the absolute lifetime: the check in <c>ValidatePrincipal</c> is what ends the session.
    /// </summary>
    private static void CapExpiryAtAbsoluteLifetime(AuthenticationProperties properties, DateTimeOffset absoluteEnd)
    {
        if (properties.ExpiresUtc is not { } expiresUtc || expiresUtc > absoluteEnd)
            properties.ExpiresUtc = absoluteEnd;
    }

    private async Task RejectAsync(CookieValidatePrincipalContext context, bool signOut, string reason)
    {
        LogSessionRejected(logger, reason);
        context.RejectPrincipal();
        if (signOut)
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
    }

    [LoggerMessage(EventId = 7300, EventName = "SessionRejected", Level = LogLevel.Debug,
        Message = "Dashboard session rejected: {Reason}")]
    private static partial void LogSessionRejected(ILogger logger, string reason);
}
