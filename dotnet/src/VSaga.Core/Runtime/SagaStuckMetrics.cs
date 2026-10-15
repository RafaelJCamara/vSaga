using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Notifications;

namespace VSaga.Core.Runtime;

/// <summary>
/// <c>vsaga.saga.stuck</c>: how many instances were idle past their threshold at each sweep's last reading, by saga
/// type and cause (docs/design/stuck-saga-detection.md §5.1). An <see cref="ObservableGauge{T}"/> over the sweep's
/// cached result, not an <c>UpDownCounter</c>: a counter is process-local and non-idempotent, so a restart, a
/// redelivery or a second replica would desynchronise it for good (see <see cref="VSagaDiagnostics"/>), where a
/// reading of the store corrects itself on the next sweep. A meter callback cannot await a scoped store, which is why
/// the sweep reads the store and the callback only reads what it left.
/// </summary>
internal static class SagaStuckMetrics
{
    private static readonly ConcurrentDictionary<object, IReadOnlyList<Measurement<long>>> Readings = new();

    private static readonly ObservableGauge<long> Gauge = VSagaDiagnostics.Meter.CreateObservableGauge(
        "vsaga.saga.stuck",
        Observe,
        unit: "{saga}",
        description: "Saga instances idle past their threshold at the last sweep, by saga type and cause.");

    /// <summary>Replaces <paramref name="source"/>'s reading: one measurement per saga type and cause, zeros included, so an alert rule sees a recovery as a value.</summary>
    public static void Publish(object source, IReadOnlyDictionary<string, IReadOnlyDictionary<StuckCause, int>> countsBySagaType)
    {
        GC.KeepAlive(Gauge);
        Readings[source] =
        [
            .. countsBySagaType.SelectMany(type => type.Value.Select(cause => new Measurement<long>(
                cause.Value,
                new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, type.Key),
                new KeyValuePair<string, object?>(VSagaDiagnostics.TagStuckCause, cause.Key.ToString())))),
        ];
    }

    /// <summary>Stops reporting <paramref name="source"/>'s reading: its sweep stopped, or has failed too often for the last reading to be believed.</summary>
    public static void Withdraw(object source) => Readings.TryRemove(source, out _);

    /// <summary>How many sweeps currently have a reading published; for tests, which must see every host's reading gone once it is disposed.</summary>
    internal static int PublishedReadings => Readings.Count;

    private static IEnumerable<Measurement<long>> Observe() => Readings.Values.SelectMany(reading => reading);
}
