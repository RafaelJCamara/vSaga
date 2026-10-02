using VSaga.Abstractions.Persistence;

namespace VSaga.Core.Runtime;

/// <summary>
/// Tunables for <see cref="SagaOrchestrator{TState}"/>: how often an infrastructure failure redelivers
/// before dead-lettering, and whether and how much of the saga's state it records as a
/// <see cref="SagaEntryType.StatePersisted"/> snapshot after each committed transition. None is
/// validated; set them through <c>SagaEngineBuilder.ConfigureOrchestrator</c>.
/// </summary>
public sealed class SagaOrchestratorOptions
{
    /// <summary>
    /// How many times an infrastructure-level failure — a deserialize error, a persistence-store
    /// exception, anything outside the saga definition's own step logic (which
    /// <see cref="SagaOrchestrator{TState}"/>'s HandleStepFailureAsync already handles by marking the
    /// saga Failed) — redelivers the same message before it's dead-lettered instead of requeued
    /// forever.
    /// </summary>
    public int MaxDeliveryAttempts { get; set; } = 5;

    /// <summary>
    /// Whether the engine appends a <see cref="SagaEntryType.StatePersisted"/> entry, carrying the state
    /// exactly as the snapshot store wrote it, after each committed step, step failure, timeout and
    /// delivery exhaustion. Best effort: a failed append is logged and the step carries on. Off records
    /// nothing; the dashboard then shows no per-step data.
    /// </summary>
    public bool RecordStateSnapshots { get; set; } = true;

    /// <summary>
    /// The largest state, in UTF-8 bytes of its JSON, recorded in full. A larger state is recorded as
    /// <c>{"$vsagaStateOmitted":true,"bytes":N,"limit":L}</c>. 0 records size-only markers.
    /// </summary>
    public int MaxStateSnapshotBytes { get; set; } = SagaStateSnapshot.DefaultMaxBytes;

    /// <summary>
    /// The per-instance budget, in UTF-8 bytes, for the snapshots one saga's timeline holds, because the
    /// engine reads them all back before every step. Once the snapshots already recorded plus the next
    /// one would pass it, the snapshots after a successful step or a timeout become
    /// <c>{"$vsagaStateOmitted":true,"bytes":N,"budget":B}</c>; the ones after a step failure or a
    /// delivery exhaustion are still recorded in full (up to <see cref="MaxStateSnapshotBytes"/>), since
    /// those are what an investigation needs. 0 (or less) means unlimited. Defaults to 1 MiB.
    /// </summary>
    public int MaxStateSnapshotBytesPerSaga { get; set; } = 1_048_576;

    /// <summary>
    /// How long one snapshot append may take before it is abandoned. The append sits between the commit
    /// and the step's deferred publishes and acknowledgement, so a stalled event-log write must not hold
    /// them back; a timeout is logged as a warning like any other failed append. Defaults to 5 seconds,
    /// well under the outbox's 30-second dispatch grace period.
    /// </summary>
    public TimeSpan StateSnapshotTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
