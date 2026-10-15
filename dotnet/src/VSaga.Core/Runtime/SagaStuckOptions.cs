namespace VSaga.Core.Runtime;

/// <summary>
/// Tunables for the stuck-saga sweep (<see cref="SagaStuckSweepHostedService"/>,
/// docs/design/stuck-saga-detection.md §4): which instances count as idle for too long, how often the store is
/// read, and how much of the answer is logged. Set through <c>SagaEngineBuilder.ConfigureStuckDetection</c>, which
/// also validates them.
/// </summary>
public sealed class SagaStuckOptions
{
    private static readonly TimeSpan LongestThreshold = TimeSpan.FromDays(3650);

    /// <summary>
    /// Whether the sweep runs. False by default, so an upgrade adds no polling query and no log line on its own;
    /// <c>ConfigureStuckDetection</c> sets it true before running the delegate, so calling it is the opt-in and a
    /// bound configuration can still switch it off.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Time between sweeps.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Idle time that counts as stuck where no timeout applies: a <c>Running</c> saga whose state declares none, and
    /// every <c>Compensating</c> saga. Deliberately long: a human approval is not stuck.
    /// </summary>
    public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Per-saga-type overrides of <see cref="IdleThreshold"/>, by the saga type's name, compared ordinally.</summary>
    public IDictionary<string, TimeSpan> IdleThresholdBySagaType { get; } = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

    /// <summary>
    /// Added to a state's timeout before a saga idle in it counts as stuck: the timeout dispatcher's own 5 s poll,
    /// the timeout step's retries and a clock difference between replicas all fit inside it.
    /// </summary>
    public TimeSpan TimeoutGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Saga types the sweep never reads, for a saga that waits for days by design, by name, compared ordinally.</summary>
    public ISet<string> ExcludedSagaTypes { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The most instances of one saga type the sweep logs individually each time it runs; the rest are counted in one
    /// further line and in the gauge.
    /// </summary>
    public int MaxReportedPerSweep { get; set; } = 200;

    /// <summary>
    /// The most instances of one saga type the sweep reads each time it runs, oldest first. Past it the gauge counts a
    /// lower bound and a warning says so; a store with that many idle instances has a larger problem than the count.
    /// </summary>
    public int MaxScannedPerType { get; set; } = 10_000;

    /// <summary>The idle threshold for <paramref name="sagaType"/>: its override, or <see cref="IdleThreshold"/>.</summary>
    public TimeSpan IdleThresholdFor(string sagaType) =>
        IdleThresholdBySagaType.TryGetValue(sagaType, out var threshold) ? threshold : IdleThreshold;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for a value the sweep cannot work with.</summary>
    internal void Validate()
    {
        RequireWithin(SweepInterval, nameof(SweepInterval), TimeSpan.Zero, exclusiveMinimum: true);
        RequireWithin(IdleThreshold, nameof(IdleThreshold), TimeSpan.Zero, exclusiveMinimum: true);
        foreach (var (sagaType, threshold) in IdleThresholdBySagaType)
            RequireWithin(threshold, $"{nameof(IdleThresholdBySagaType)}[{sagaType}]", TimeSpan.Zero, exclusiveMinimum: true);

        RequireWithin(TimeoutGrace, nameof(TimeoutGrace), TimeSpan.Zero);
        RequireAtLeastOne(MaxReportedPerSweep, nameof(MaxReportedPerSweep));
        RequireAtLeastOne(MaxScannedPerType, nameof(MaxScannedPerType));
    }

    private static void RequireWithin(TimeSpan value, string name, TimeSpan minimum, bool exclusiveMinimum = false)
    {
        if (value < minimum || (exclusiveMinimum && value == minimum) || value > LongestThreshold)
            throw new ArgumentOutOfRangeException(name, value, exclusiveMinimum ? "Must be greater than zero and at most ten years." : "Must be between zero and ten years.");
    }

    private static void RequireAtLeastOne(int value, string name)
    {
        if (value < 1)
            throw new ArgumentOutOfRangeException(name, value, "Must be at least one.");
    }
}
