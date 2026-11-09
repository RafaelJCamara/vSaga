using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using VSaga.Abstractions.Diagnostics;

namespace VSaga.Observability;

/// <summary>
/// <c>vsaga.saga.running</c>: how many instances were in status <c>Running</c> at the last refresh of the store, by
/// saga type (ADR 0013). An <see cref="ObservableGauge{T}"/> over a cached reading, not an <c>UpDownCounter</c>: a
/// counter is process-local and non-idempotent, so a restart, a redelivery or a second replica would desynchronise it
/// for good (see <see cref="VSagaDiagnostics"/>), where a reading of the store corrects itself on the next refresh. A
/// meter callback cannot await a scoped store, which is why <see cref="RunningSagasRefreshHostedService"/> reads the
/// store and the callback only returns what it left. The same shape as <c>vsaga.saga.stuck</c>.
/// </summary>
internal static class RunningSagasMetrics
{
    private static readonly ConcurrentDictionary<object, IReadOnlyList<Measurement<long>>> Readings = new();

    private static readonly ObservableGauge<long> Gauge = VSagaDiagnostics.Meter.CreateObservableGauge(
        "vsaga.saga.running",
        Observe,
        unit: "{saga}",
        description: "Saga instances in status Running at the last read of the store, by saga type.");

    /// <summary>Replaces <paramref name="source"/>'s reading with one measurement per saga type in <paramref name="countsBySagaType"/>, zeros included.</summary>
    public static void Publish(object source, IReadOnlyDictionary<string, long> countsBySagaType)
    {
        GC.KeepAlive(Gauge);
        Readings[source] =
        [
            .. countsBySagaType.Select(type => new Measurement<long>(type.Value, new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, type.Key))),
        ];
    }

    /// <summary>Stops reporting <paramref name="source"/>'s reading: its service stopped, or has failed too often for the last reading to be believed.</summary>
    public static void Withdraw(object source) => Readings.TryRemove(source, out _);

    /// <summary>How many services currently have a reading published; for tests, which must see every host's reading gone once it is disposed.</summary>
    internal static int PublishedReadings => Readings.Count;

    private static IEnumerable<Measurement<long>> Observe() => Readings.Values.SelectMany(reading => reading);
}
