namespace VSaga.Observability;

/// <summary>
/// Tunables for the <c>vsaga.saga.running</c> gauge (ADR 0013), set through
/// <see cref="ServiceCollectionExtensions.AddVSagaRunningSagasGauge"/>, which also validates them.
/// </summary>
public sealed class RunningSagasOptions
{
    private static readonly TimeSpan LongestInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// Time between reads of the store (<c>ISagaSummaryReader.CountByTypeAsync</c>), 30 seconds by default, and the
    /// deadline of each read. The gauge reports the last reading, so it is up to this old, plus the exporter's own
    /// interval; the store's cost is one read per interval however often the metric is scraped
    /// (docs/observability.md, "Running sagas").
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for a value the gauge cannot work with.</summary>
    internal void Validate() => RequireWithin(RefreshInterval, nameof(RefreshInterval));

    private static void RequireWithin(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value > LongestInterval)
            throw new ArgumentOutOfRangeException(name, value, "Must be greater than zero and at most one day.");
    }
}
