using Microsoft.Extensions.Diagnostics.HealthChecks;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Api.HealthChecks;

/// <summary>
/// The dashboard identity store. Registered with <see cref="HealthStatus.Degraded"/> as its failure status:
/// without the store nobody can sign in, but the saga views, the API key and the saga host do not depend on
/// it, so <c>/health</c> stays 200 and compose's <c>service_healthy</c> gate still opens. Each probe also
/// gives <see cref="IdentityStartup"/> its chance to retry, and the description carries its reason. A store
/// that works but whose configured first administrator could not be created, or whose <c>ResetOnStart</c>
/// could not be applied, is degraded too, with <see cref="FirstRunState.SeedProblem"/> as the reason.
/// </summary>
/// <remarks>
/// A probe waits for that retry at most <see cref="MaxWait"/>, well inside compose's 5 s probe timeout,
/// and then reports the store as it stands ("being initialised", with the previous attempt's reason). The
/// attempt runs on without the probe: a stuck migration lock takes its full 30 s and is reported by a later
/// probe, instead of every probe being killed by the prober and the service never turning healthy.
/// </remarks>
public sealed class IdentityHealthCheck(IdentityStartup startup, FirstRunState firstRun, IServiceScopeFactory scopeFactory) : IHealthCheck
{
    /// <summary>The longest a probe waits for an attempt that is running.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!await startup.EnsureReadyAsync(MaxWait, cancellationToken))
            return new HealthCheckResult(context.Registration.FailureStatus, startup.FailureReason);

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>();
        try
        {
            if (!await store.CanConnectAsync(cancellationToken))
                return new HealthCheckResult(context.Registration.FailureStatus, "The identity database is not answering.");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "The identity database is not answering.", ex);
        }

        return firstRun.SeedProblem is { } problem
            ? new HealthCheckResult(context.Registration.FailureStatus, problem)
            : HealthCheckResult.Healthy();
    }
}
