using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace VSaga.Persistence.Redis;

/// <summary>
/// Reports the provider's guarantees as verified right now, not as configured once: every check
/// re-runs <see cref="RedisServerProbe"/>, because <c>maxmemory-policy</c> or <c>appendonly</c> can be
/// changed live by a neighbouring operator. Unhealthy names the broken guarantee -- or says it could not
/// be verified, which is not the same as healthy. Register it under the provider-neutral name
/// <c>"persistence"</c>, as the dashboard does.
/// </summary>
public sealed class RedisPersistenceHealthCheck(RedisServerProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var report = await probe.ProbeAsync(cancellationToken);
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["durabilityTier"] = report.DurabilityTier,
            ["tornWrites"] = report.TornWrites,
        };
        if (report.MemoryRatio is { } ratio)
            data["memoryRatio"] = ratio;

        return report.IsHealthy
            ? HealthCheckResult.Healthy(report.Describe(), data)
            : HealthCheckResult.Unhealthy(report.Describe(), data: data);
    }
}
