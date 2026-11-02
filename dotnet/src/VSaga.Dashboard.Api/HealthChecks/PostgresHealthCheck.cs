using VSaga.Persistence.EFCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace VSaga.Dashboard.Api.HealthChecks;

/// <summary>
/// Resolves <see cref="VSagaDbContext"/> lazily from a fresh scope rather than taking it as a
/// constructor dependency: VSaga.Dashboard.Api.Tests' DashboardApiFactory removes it from DI entirely
/// (it swaps in the in-memory persistence provider), and health check instances are otherwise
/// constructed via the root provider, so a hard constructor dependency here would fail to resolve in
/// that composition. Serves every EF Core provider (Postgres, SQL Server), hence the generic wording of its failures.
/// </summary>
public sealed class PostgresHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetService<VSagaDbContext>();
        if (db is null)
            return HealthCheckResult.Healthy("No relational database configured.");

        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Could not connect to the relational database.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Could not connect to the relational database.", ex);
        }
    }
}
