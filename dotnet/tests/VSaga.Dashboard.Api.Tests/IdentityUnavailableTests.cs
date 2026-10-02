using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using VSaga.Dashboard.Api.HealthChecks;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The identity store is the API's, but the saga views must not depend on it: an unusable database starts
/// the API anyway, keeps the API key working, and shows up only as a Degraded <c>identity</c> check, so
/// <c>/health</c> stays 200 and compose's <c>service_healthy</c> gate still opens.
/// </summary>
public sealed class IdentityUnavailableTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly DashboardApiFactory _factory = new();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "vsaga-dashboard-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        if (Directory.Exists(_scratch))
            Directory.Delete(_scratch, recursive: true);
    }

    [Fact]
    public async Task AtStartUp_TheDatabaseIsCreatedAndReady_AndHealthReportsIdentityHealthy()
    {
        using var client = _factory.CreateClient();

        // Before anything asks: start-up itself prepared the store, not the first probe.
        Assert.True(_factory.Services.GetRequiredService<IdentityStartup>().IsReady);
        Assert.True(File.Exists(_factory.IdentityDatabasePath));

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = await HealthResponse.ReadAsync(response);
        Assert.Equal("healthy", health.Checks["identity"].Status);
    }

    [Fact]
    public async Task AnUnusablePath_StillStarts_ServesTheApiKey_AndReportsIdentityDegraded()
    {
        // A directory cannot be created where a file already is, on every OS and for every account.
        Directory.CreateDirectory(_scratch);
        var blocker = Path.Combine(_scratch, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "");
        var unusable = Path.Combine(blocker, "identity.db");
        await using var host = _factory.WithWebHostBuilder(builder => builder.UseSetting(DashboardIdentitySettings.SqlitePathKey, unusable));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", DashboardApiFactory.TestApiKey);

        var sagas = await client.GetAsync("/api/sagas");
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, sagas.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = await HealthResponse.ReadAsync(response);
        Assert.Equal("degraded", health.Status);
        var identity = health.Checks["identity"];
        Assert.Equal("degraded", identity.Status);
        Assert.Contains("cannot be created, opened or written", identity.Description, StringComparison.Ordinal);
        Assert.Contains(DashboardIdentitySettings.SqlitePathKey, identity.Description, StringComparison.Ordinal);
        // /health is anonymous: the reason names the setting, never the server's file layout.
        Assert.DoesNotContain(_scratch, identity.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InAContainer_WithoutAPath_ReportsIdentityDegradedNamingTheKey()
    {
        await using var host = _factory.WithWebHostBuilder(builder => builder
            .UseSetting(DashboardIdentitySettings.RunningInContainerKey, "true")
            .UseSetting(DashboardIdentitySettings.SqlitePathKey, ""));
        using var client = host.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var identity = (await HealthResponse.ReadAsync(response)).Checks["identity"];
        Assert.Equal("degraded", identity.Status);
        Assert.StartsWith($"{DashboardIdentitySettings.SqlitePathKey} is not set. In a container", identity.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleMigrationLock_NeverHoldsAProbe_AndIsReportedOnceTheAttemptEnds()
    {
        // A ready database, then what a process killed mid-migration leaves behind.
        using (_factory.CreateClient())
            await InsertStaleMigrationLockAsync(_factory.IdentityDatabasePath);
        var attemptTimeout = TimeSpan.FromSeconds(6);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(provider => new IdentityStartup(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<DashboardIdentitySettings>(),
                clock,
                provider.GetRequiredService<ILogger<IdentityStartup>>()) { Timeout = attemptTimeout })));

        // Start-up's own attempt runs into the lock and times out; the API starts regardless.
        using var client = host.CreateClient();

        // Inside the retry interval: no new attempt, the reason at once.
        var (atStart, _) = await ProbeIdentityAsync(client);
        Assert.Equal("degraded", atStart.Status);
        Assert.StartsWith("Initialising the identity database timed out after 6 s", atStart.Description, StringComparison.Ordinal);
        Assert.Contains("__EFMigrationsLock", atStart.Description, StringComparison.Ordinal);

        // A probe that starts the retry waits for it only briefly, then reports it in progress; so does the
        // next one, which joins the same attempt. Neither is held for the attempt's 6 s.
        clock.Advance(IdentityStartup.RetryInterval);
        for (var probe = 0; probe < 2; probe++)
        {
            var (retrying, elapsed) = await ProbeIdentityAsync(client);
            Assert.True(elapsed < attemptTimeout - TimeSpan.FromSeconds(2), $"The probe took {elapsed}.");
            Assert.Equal("degraded", retrying.Status);
            Assert.StartsWith(IdentityStartup.InitialisingReason, retrying.Description, StringComparison.Ordinal);
            Assert.Contains("__EFMigrationsLock", retrying.Description, StringComparison.Ordinal);
        }

        // A prober that gives up after 200 ms gets its answer, and neither cancels nor restarts the attempt.
        var check = ActivatorUtilities.CreateInstance<IdentityHealthCheck>(host.Services);
        var context = new HealthCheckContext { Registration = new HealthCheckRegistration("identity", check, HealthStatus.Degraded, tags: null) };
        using (var prober = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            var watch = Stopwatch.StartNew();
            var result = await check.CheckHealthAsync(context, prober.Token);
            Assert.True(watch.Elapsed < IdentityHealthCheck.MaxWait + TimeSpan.FromSeconds(1), $"The check took {watch.Elapsed}.");
            Assert.Equal(HealthStatus.Degraded, result.Status);
            Assert.StartsWith(IdentityStartup.InitialisingReason, result.Description, StringComparison.Ordinal);
        }

        // The attempt ends on its own timeout and counts: the probes after it report its reason at once.
        var deadline = DateTime.UtcNow + attemptTimeout + TimeSpan.FromSeconds(10);
        HealthCheckEntry ended;
        do
        {
            (ended, _) = await ProbeIdentityAsync(client);
        }
        while (ended.Description!.StartsWith(IdentityStartup.InitialisingReason, StringComparison.Ordinal) && DateTime.UtcNow < deadline);

        Assert.StartsWith("Initialising the identity database timed out after 6 s", ended.Description, StringComparison.Ordinal);
        var (afterwards, quickly) = await ProbeIdentityAsync(client);
        Assert.Equal(ended.Description, afterwards.Description);
        Assert.True(quickly < IdentityHealthCheck.MaxWait, $"The probe took {quickly}.");
    }

    [Fact]
    public void AnUnknownProvider_FailsAtStartUp()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.UseSetting(DashboardIdentitySettings.ProviderKey, "Postgres"));

        var exception = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Equal("Unknown Dashboard:Identity:Provider 'Postgres'.", exception.Message);
    }

    /// <summary>One <c>/health</c> call, which must answer 200, and its <c>identity</c> entry.</summary>
    private static async Task<(HealthCheckEntry Identity, TimeSpan Elapsed)> ProbeIdentityAsync(HttpClient client)
    {
        var watch = Stopwatch.StartNew();
        var response = await client.GetAsync("/health");
        watch.Stop();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ((await HealthResponse.ReadAsync(response)).Checks["identity"], watch.Elapsed);
    }

    private static async Task InsertStaleMigrationLockAsync(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO __EFMigrationsLock (Id, Timestamp) VALUES (1, '2026-10-02 09:00:00')";
        await command.ExecuteNonQueryAsync();
    }
}
