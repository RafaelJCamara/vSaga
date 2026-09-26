using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>
/// A retrying hosted service, never fail-fast: it writes the key space's schema marker once, then
/// probes the server on <see cref="VSagaRedisOptions.ProbeInterval"/> for as long as the host runs,
/// logging each change in the probe's verdict. The host starts whether or not Redis is reachable --
/// stores simply fail until it is -- but <see cref="RedisPersistenceHealthCheck"/> stays Unhealthy until a
/// probe passes, which is what a compose file's <c>depends_on: service_healthy</c> gates on.
/// </summary>
public sealed class RedisPersistenceBootstrapper(RedisConnection connection, RedisKeySpace keys, RedisServerProbe probe, VSagaRedisOptions options, ILogger<RedisPersistenceBootstrapper> logger)
    : BackgroundService
{
    /// <summary>The key-space layout version this provider writes and expects. Bumped only with a documented migration.</summary>
    public const string SchemaVersion = "1";

    public const string SchemaVersionField = "sv";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool? wasHealthy = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureSchemaMarkerAsync(stoppingToken);
                var report = await probe.ProbeAsync(stoppingToken);
                if (report.IsHealthy != wasHealthy)
                {
                    if (report.IsHealthy)
                        logger.LogInformation("Redis persistence is ready: {Report}", report.Describe());
                    else
                        logger.LogError("Redis persistence is not ready: {Report}", report.Describe());
                }

                wasHealthy = report.IsHealthy;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A transient failure here, such as Redis still starting, is exactly what the retry is
                // for. The health check meanwhile reports whatever the last probe found.
                logger.LogWarning(ex, "Redis persistence bootstrap attempt failed; retrying in {Interval}.", options.ProbeInterval);
                wasHealthy = null;
            }

            try
            {
                await Task.Delay(options.ProbeInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// <c>HSETNX</c> the marker: idempotent, so a restart or a flushed key space simply writes it again.
    /// A marker of another version is left alone and reported by the probe -- "no migrations" would be a
    /// liability transfer, not a free benefit, and the marker is what makes a future layout change loud.
    /// </summary>
    private async Task EnsureSchemaMarkerAsync(CancellationToken cancellationToken)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        await db.HashSetAsync(keys.Meta, SchemaVersionField, SchemaVersion, When.NotExists);
    }
}
