using VSaga.Abstractions.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace VSaga.Observability;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wires VSaga's saga ActivitySource/Meter (defined once in VSaga.Abstractions so Core,
    /// Persistence, and Transport packages all emit against the same names) into the app's
    /// OpenTelemetry pipeline. Exporters (OTLP/Jaeger/Prometheus/console) are the app's own choice —
    /// this method only registers the sources; it never assumes a collector is present, since the
    /// dashboard reads the persisted event log rather than an OTel backend.
    /// <para>
    /// One-line OTLP wiring for a caller that does want to ship these spans/metrics somewhere: add the
    /// `OpenTelemetry.Exporter.OpenTelemetryProtocol` package and pass
    /// <c>configureTracing: t => t.AddOtlpExporter()</c> / <c>configureMetrics: m => m.AddOtlpExporter()</c>
    /// to this method. docs/observability.md ("Wiring it up") documents the same one-liner.
    /// </para>
    /// </summary>
    public static IServiceCollection AddVSagaOpenTelemetry(
        this IServiceCollection services,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        // production-readiness.md §6/§8.18: W3C is the wire format VSagaDiagnostics.Inject/
        // TryExtractActivityContext hand-roll, so make it the OTel SDK's default propagator too --
        // otherwise a host process that (or some other library) called SetDefaultTextMapPropagator
        // with something else first (e.g. B3) would silently disagree with what this library actually
        // puts on the wire. This is already the SDK's own out-of-the-box default; setting it explicitly
        // just stops that default from being an accident this library happens to depend on.
        Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator(
            new TextMapPropagator[] { new TraceContextPropagator(), new BaggagePropagator() }));

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing.AddSource(VSagaDiagnostics.ActivitySourceName);
                configureTracing?.Invoke(tracing);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(VSagaDiagnostics.MeterName);
                configureMetrics?.Invoke(metrics);
            });

        return services;
    }

    /// <summary>
    /// Turns on the <c>vsaga.saga.running</c> gauge (ADR 0013): how many saga instances are <c>Running</c> right now, by
    /// saga type, read from the store. A hosted service counts them through <c>ISagaSummaryReader.CountByTypeAsync</c>
    /// every <see cref="RunningSagasOptions.RefreshInterval"/> and the meter callback reports the last answer, so a
    /// restart or a second replica cannot skew it the way an up-down counter would be skewed. Opt-in because every
    /// refresh is a read the store did not do before: call it on one process per store, after the persistence
    /// registration, and aggregate across replicas with <c>max</c>, not <c>sum</c>. Needs <see cref="AddVSagaOpenTelemetry"/>
    /// (or any other subscription to the <c>VSaga.Saga</c> meter) to be seen anywhere.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The delegate set a <see cref="RunningSagasOptions.RefreshInterval"/> of zero, below, or over a day.</exception>
    public static IServiceCollection AddVSagaRunningSagasGauge(
        this IServiceCollection services,
        Action<RunningSagasOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new RunningSagasOptions();
        configure?.Invoke(options);
        options.Validate();

        // A second call replaces the options and does not add a second service.
        services.Replace(ServiceDescriptor.Singleton(options));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RunningSagasRefreshHostedService>());

        return services;
    }
}
