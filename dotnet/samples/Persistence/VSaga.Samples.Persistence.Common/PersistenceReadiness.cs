using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace VSaga.Samples.Persistence.Common;

/// <summary>
/// Waits for a persistence provider's own health check to report Healthy. The MongoDB and Redis
/// providers bootstrap in a retrying hosted service instead of failing fast: the host starts without
/// its store, and the stores fail until the bootstrapper has reached the server, verified its
/// configuration and written the schema marker. A web host gates traffic on <c>/health</c>; a console
/// sample has to wait for the same signal itself before submitting anything.
/// </summary>
public static class PersistenceReadiness
{
    public static async Task WaitUntilHealthyAsync(IHealthCheck healthCheck, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var context = new HealthCheckContext();

        while (true)
        {
            var result = await healthCheck.CheckHealthAsync(context, cancellationToken);
            if (result.Status == HealthStatus.Healthy)
            {
                Console.WriteLine($"Persistence healthy: {result.Description}");
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Persistence was not healthy after {timeout.TotalSeconds:0} s. Last report: {result.Status}: {result.Description}", result.Exception);
            }
        }
    }
}
