using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// Reports the provider's prerequisites as verified right now, not as configured once: every check
/// re-runs <see cref="MongoServerProbe"/>, because a replica set can lose its primary and an index can be
/// dropped under a running host. Unhealthy names the missing prerequisite -- or says it could not be
/// verified, which is not the same as healthy. Register it under the provider-neutral name
/// <c>"persistence"</c>, as the dashboard does.
/// </summary>
public sealed class MongoPersistenceHealthCheck(MongoServerProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var report = await probe.ProbeAsync(cancellationToken);
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["topology"] = report.Topology,
            ["indexesInPlace"] = report.IndexesInPlace,
            ["strandedOutboxRows"] = report.StrandedOutboxRows,
        };
        if (report.ServerVersion is { } version)
            data["serverVersion"] = version;

        return report.IsHealthy
            ? HealthCheckResult.Healthy(report.Describe(), data)
            : HealthCheckResult.Unhealthy(report.Describe(), data: data);
    }
}
