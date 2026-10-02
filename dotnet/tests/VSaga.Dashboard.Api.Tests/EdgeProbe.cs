using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VSaga.Dashboard.Api.Hosting;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// A minimal app on TestServer for the forwarded-header tests: its first middleware sets the connection's
/// peer address from a test-only header (TestServer has no socket), then <c>UseDashboardEdge</c> runs as in
/// Program.cs, then a probe endpoint answers "<c>scheme remote-address</c>". Warnings logged by the edge are
/// captured in <see cref="Warnings"/>.
/// </summary>
internal sealed class EdgeProbe : IAsyncDisposable
{
    private const string PeerHeader = "X-Test-Peer";

    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly WarningCapture _warnings;

    private EdgeProbe(WebApplication app, WarningCapture warnings)
    {
        _app = app;
        _client = app.GetTestClient();
        _warnings = warnings;
    }

    /// <summary>The formatted Warning messages logged under the Hosting namespace, in order.</summary>
    public IReadOnlyList<string> Warnings => [.. _warnings.Messages];

    /// <summary>Starts the probe app; <paramref name="timeProvider"/> replaces the system clock the warning's limits read.</summary>
    public static async Task<EdgeProbe> StartAsync(string trustedProxies, TimeProvider? timeProvider = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        if (timeProvider is not null)
            builder.Services.AddSingleton(timeProvider);

        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DashboardEdge.TrustedProxiesKey] = trustedProxies,
        });

        var warnings = new WarningCapture();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(warnings);

        builder.Services.AddDashboardEdge(DashboardEdge.Read(builder.Configuration));

        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[PeerHeader].ToString());
            return next(context);
        });
        app.UseDashboardEdge();
        app.MapGet("/probe", (HttpContext context) => $"{context.Request.Scheme} {context.Connection.RemoteIpAddress}");

        await app.StartAsync();
        return new EdgeProbe(app, warnings);
    }

    public async Task<string> SendAsync(string peer, string? forwardedFor, string? forwardedProto)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.Add(PeerHeader, peer);
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (forwardedProto is not null)
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);

        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return _app.DisposeAsync();
    }

    private sealed class WarningCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
            // Nothing to release; the messages outlive the app for the assertions.
        }

        private sealed class CapturingLogger(WarningCapture owner, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning
                    && categoryName.StartsWith(typeof(DashboardEdge).Namespace!, StringComparison.Ordinal))
                {
                    owner.Messages.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
