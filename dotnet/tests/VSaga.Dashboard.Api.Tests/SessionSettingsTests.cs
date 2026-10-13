using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Identity;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The session cookie's options as the settings shape them, <c>Dashboard:Session:RequireHttps</c> (Secure
/// always, the <c>__Host-</c> prefix and HSTS, and only behind a trusted proxy), and the API-key warnings
/// logged at start.
/// </summary>
public sealed class SessionSettingsTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly DashboardApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public void ByDefault_TheCookieIsHttpOnlyStrictSlidingAndSecureOnlyOverHttps()
    {
        using var client = _factory.CreateClient();
        var options = CookieOptions(_factory.Services);

        Assert.Equal("vsaga.session", options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Strict, options.Cookie.SameSite);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, options.Cookie.SecurePolicy);
        Assert.Equal("/", options.Cookie.Path);
        Assert.Equal(TimeSpan.FromMinutes(480), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.Equal(typeof(DashboardCookieEvents), options.EventsType);
    }

    [Fact]
    public void TheCookieNameAndIdleTimeout_FollowTheSettings()
    {
        using var host = With(
            (DashboardSecuritySettings.SessionCookieNameKey, "vsaga.session.overlay"),
            (DashboardSecuritySettings.SessionIdleTimeoutMinutesKey, "30"));
        using var client = host.CreateClient();
        var options = CookieOptions(host.Services);

        Assert.Equal("vsaga.session.overlay", options.Cookie.Name);
        Assert.Equal(TimeSpan.FromMinutes(30), options.ExpireTimeSpan);
    }

    [Fact]
    public void RequireHttps_WithoutATrustedProxy_FailsComposition()
    {
        using var host = With((DashboardSecuritySettings.RequireHttpsKey, "true"));

        var error = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains(DashboardSecuritySettings.RequireHttpsKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("Dashboard:TrustedProxies", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequireHttps_BehindATrustedProxy_MakesTheCookieSecureAndHostOnly_AndSendsHsts()
    {
        using var host = With((DashboardSecuritySettings.RequireHttpsKey, "true"), ("Dashboard:TrustedProxies", "10.0.0.0/8"));
        using var https = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://dashboard.example.com") });
        var options = CookieOptions(host.Services);

        using var response = await https.GetAsync("/health");

        Assert.Equal("__Host-vsaga.session", options.Cookie.Name);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
        Assert.Equal("/", options.Cookie.Path);
        Assert.Null(options.Cookie.Domain);
        Assert.True(response.Headers.Contains("Strict-Transport-Security"));
    }

    /// <summary>
    /// Found on a live run behind a TLS proxy: a request that reached the API over plain HTTP (its own published
    /// port, or a proxy missing from <c>Dashboard:TrustedProxies</c>) threw out of the antiforgery system, which
    /// refuses to issue Secure cookies on a non-SSL request, and the caller got an empty 500 and an error log.
    /// </summary>
    [Fact]
    public async Task RequireHttps_APlainHttpRequest_IsRefusedWith400AndTheHealthProbeStillAnswers()
    {
        using var host = With((DashboardSecuritySettings.RequireHttpsKey, "true"), ("Dashboard:TrustedProxies", "10.0.0.0/8"));
        using var plain = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://dashboard.example.com") });
        using var https = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://dashboard.example.com") });

        using var refused = await plain.GetAsync("/api/auth/session");
        using var health = await plain.GetAsync("/health");
        using var secure = await https.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(AuthProblems.HttpsRequiredCode, body.RootElement.GetProperty("code").GetString());
        Assert.Contains("Dashboard:TrustedProxies", body.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(refused.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
        Assert.Contains(secure.Headers.GetValues("Set-Cookie"), c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal) && c.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WithoutRequireHttps_NoHstsIsSent()
    {
        using var https = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://dashboard.example.com") });

        using var response = await https.GetAsync("/health");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public void AtStart_AShortKeyAndARoleHoldingAccessManage_AreWarnedAbout()
    {
        var warnings = new WarningCapture();
        using var host = _factory.WithWebHostBuilder(builder => builder
            .UseSetting(DashboardSecuritySettings.ApiKeyRoleKey, "Administrator")
            .ConfigureLogging(logging => logging.AddProvider(warnings)));

        using var client = host.CreateClient();

        // The factory's key, "test-api-key", is 12 characters.
        Assert.Contains(warnings.EventIds, id => id == 7301);
        Assert.Contains(warnings.EventIds, id => id == 7303);
        Assert.DoesNotContain(warnings.EventIds, id => id == 7302);
    }

    [Fact]
    public void AtStart_ARoleThatCannotBeFound_IsWarnedAbout()
    {
        var warnings = new WarningCapture();
        using var host = _factory.WithWebHostBuilder(builder => builder
            .UseSetting(DashboardSecuritySettings.ApiKeyRoleKey, "Nobody")
            .ConfigureLogging(logging => logging.AddProvider(warnings)));

        using var client = host.CreateClient();

        Assert.Contains(warnings.EventIds, id => id == 7302);
        Assert.DoesNotContain(warnings.EventIds, id => id == 7303);
    }

    [Fact]
    public void AtStart_ALongKeyAndAViewerRole_DrawNoWarning()
    {
        var warnings = new WarningCapture();
        using var host = _factory.WithWebHostBuilder(builder => builder
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Dashboard:ApiKey"] = new string('k', DashboardAuthExtensions.MinimumApiKeyLength),
            }))
            .UseSetting(DashboardSecuritySettings.ApiKeyRoleKey, "Viewer")
            .ConfigureLogging(logging => logging.AddProvider(warnings)));

        using var client = host.CreateClient();

        Assert.DoesNotContain(warnings.EventIds, id => id is >= 7301 and <= 7303);
    }

    private WebApplicationFactory<Program> With(params (string Key, string Value)[] settings) =>
        _factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        });

    private static CookieAuthenticationOptions CookieOptions(IServiceProvider services) =>
        services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(DashboardAuthExtensions.CookieScheme);

    /// <summary>The event ids of the Warning entries logged by the API-key handler's category.</summary>
    private sealed class WarningCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<int> _eventIds = new();

        public IReadOnlyList<int> EventIds => [.. _eventIds];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
            // Nothing to release; the entries outlive the host for the assertions.
        }

        private sealed class Logger(WarningCapture owner, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning && string.Equals(categoryName, typeof(ApiKeyAuthenticationHandler).FullName, StringComparison.Ordinal))
                    owner._eventIds.Enqueue(eventId.Id);
            }
        }
    }
}
