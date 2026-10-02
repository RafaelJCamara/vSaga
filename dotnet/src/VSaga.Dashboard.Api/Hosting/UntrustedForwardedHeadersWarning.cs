using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace VSaga.Dashboard.Api.Hosting;

/// <summary>
/// Logs a Warning when a request carries <c>X-Forwarded-For</c> or <c>X-Forwarded-Proto</c> from a peer that
/// is not in <c>Dashboard:TrustedProxies</c>, including when no proxy is trusted at all. Those headers are
/// then ignored, so the API sees the proxy's address and scheme: every client shares one per-address
/// sign-in limit and cookies are not marked Secure behind a TLS terminator. That misconfiguration is
/// otherwise silent.
/// <para>
/// Two limits keep a stream of such requests from flooding the log, even from a client that rotates its
/// source address (easy within an IPv6 /64): each peer warns at most once per <see cref="Window"/>, and
/// all peers together at most <see cref="MaxWarningsPerWindow"/> times per window, after which one line
/// says further warnings are suppressed until the window ends. Only peers that were warned about are
/// remembered, and expired ones are dropped when a window ends, so memory stays bounded too.
/// </para>
/// </summary>
internal sealed class UntrustedForwardedHeadersWarning(
    DashboardEdgeSettings settings,
    TimeProvider timeProvider,
    ILogger<UntrustedForwardedHeadersWarning> logger)
{
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    internal const int MaxWarningsPerWindow = 20;

    private const string WarningTemplate =
        "Ignoring X-Forwarded-For and X-Forwarded-Proto from {Peer}, which is not listed in "
        + DashboardEdge.TrustedProxiesKey + ". Requests from this peer keep its own address and scheme, so "
        + "per-address sign-in limits key on it and cookies are not marked Secure behind a TLS terminator. "
        + "If this peer is your reverse proxy, add its address or network to " + DashboardEdge.TrustedProxiesKey
        + ". Logged at most once every {WindowMinutes} minutes per peer.";

    private const string SuppressedTemplate =
        "{MaxWarnings} peers not listed in " + DashboardEdge.TrustedProxiesKey + " sent X-Forwarded-For or "
        + "X-Forwarded-Proto within {WindowMinutes} minutes; further warnings about untrusted peers are "
        + "suppressed until {ResumesAt:O}.";

    private enum Outcome
    {
        Quiet,
        Warn,
        Suppress,
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, DateTimeOffset> _nextWarningAt = [];
    private DateTimeOffset _windowEndsAt = DateTimeOffset.MinValue;
    private int _warningsInWindow;
    private bool _suppressionLogged;

    /// <summary>Checks one request, before any forwarded headers are applied, and warns when it should.</summary>
    internal void Inspect(HttpContext context)
    {
        var headers = context.Request.Headers;
        if (!headers.ContainsKey(ForwardedHeadersDefaults.XForwardedForHeaderName)
            && !headers.ContainsKey(ForwardedHeadersDefaults.XForwardedProtoHeaderName))
        {
            return;
        }

        // No peer address (an in-process or Unix-socket server): the forwarded-headers middleware treats
        // that as trusted, so there is nothing to warn about.
        if (context.Connection.RemoteIpAddress is not { } peer || settings.TrustsPeer(peer))
            return;

        // Kestrel's dual-mode socket reports an IPv4 peer as ::ffff:a.b.c.d; name it the way an operator
        // would write it in Dashboard:TrustedProxies.
        var shown = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        switch (Decide(shown, out var windowEndsAt))
        {
            case Outcome.Warn:
                logger.LogWarning(WarningTemplate, shown, Window.TotalMinutes);
                break;
            case Outcome.Suppress:
                logger.LogWarning(SuppressedTemplate, MaxWarningsPerWindow, Window.TotalMinutes, windowEndsAt);
                break;
            default:
                break;
        }
    }

    // Only requests that carry forwarded headers from an untrusted peer get here, so one lock is cheap and
    // keeps the per-peer and overall counts consistent with each other.
    private Outcome Decide(IPAddress peer, out DateTimeOffset windowEndsAt)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (now >= _windowEndsAt)
                StartWindow(now);

            windowEndsAt = _windowEndsAt;
            if (_nextWarningAt.TryGetValue(peer, out var next) && now < next)
                return Outcome.Quiet;

            if (_warningsInWindow >= MaxWarningsPerWindow)
            {
                if (_suppressionLogged)
                    return Outcome.Quiet;

                _suppressionLogged = true;
                return Outcome.Suppress;
            }

            _warningsInWindow++;
            _nextWarningAt[peer] = now + Window;
            return Outcome.Warn;
        }
    }

    // A peer warned about in the window that just ended may still be inside its own window, so only expired
    // peers are forgotten; the map never holds more than two windows' worth of warned peers.
    private void StartWindow(DateTimeOffset now)
    {
        _windowEndsAt = now + Window;
        _warningsInWindow = 0;
        _suppressionLogged = false;

        foreach (var (peer, next) in _nextWarningAt.ToList())
        {
            if (now >= next)
                _nextWarningAt.Remove(peer);
        }
    }
}
