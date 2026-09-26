namespace VSaga.Abstractions.Persistence;

public enum SagaTimeoutStatus
{
    Pending,
    Fired,
    Cancelled,
}

public sealed record SagaTimeout(
    long Id,
    Guid CorrelationId,
    string SagaType,
    string ForState,
    DateTimeOffset DueAtUtc,
    SagaTimeoutStatus Status);

/// <summary>Durable schedule for state-timeout transitions, polled by the SagaTimeoutDispatcher hosted service.</summary>
/// <remarks>
/// Scoped per saga instance — <c>(sagaType, correlationId)</c>. State names are only unique within a
/// saga type, so two saga types sharing a correlation id can each have a pending timeout for a
/// same-named state; cancelling must not reach across into the other's.
/// </remarks>
public interface ISagaTimeoutStore
{
    Task ScheduleAsync(string sagaType, Guid correlationId, string forState, DateTimeOffset dueAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Cancels any pending timeout for this saga instance/state pair (called when the saga transitions away before it fires).</summary>
    Task CancelAsync(string sagaType, Guid correlationId, string forState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims (marks Fired) and returns up to <paramref name="batchSize"/> due timeouts,
    /// earliest-due first, for the dispatcher to act on — only those of the saga types in
    /// <paramref name="sagaTypes"/> when it is given, every saga type's when it is null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ordering is part of the contract because <paramref name="batchSize"/> truncates: when more
    /// rows are due than one claim returns, ordering decides which of them wait for a later poll.
    /// Earliest-due first makes that wait bounded — a backlog drains oldest-first — where an arbitrary
    /// order could leave the most overdue timeout unclaimed indefinitely under sustained load.
    /// </para>
    /// <para>
    /// <paramref name="sagaTypes"/> exists because a claim is destructive: a claimed row is Fired and
    /// will never be claimed again. A process that hosts only some of the saga types sharing a store —
    /// one service per saga, one database for all — must therefore claim only what it can dispatch;
    /// claiming everything and dropping the rest would fire, and so lose for good, every other
    /// service's timeouts. The dispatcher passes the saga types it has runtimes for. A row whose type
    /// is not in the set stays Pending, untouched, for whichever process owns it. An empty set claims
    /// nothing; null means unfiltered, which is right only for a caller that can act on every type.
    /// Types compare ordinally, as the saga type is written. The outbox's
    /// <see cref="ISagaOutboxStore.ClaimPendingAsync"/> deliberately has no such parameter: its
    /// dispatcher republishes raw bytes by type name and needs no runtime, so a filter there would
    /// only strand rows.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<SagaTimeout>> ClaimDueAsync(DateTimeOffset asOf, int batchSize, IReadOnlyCollection<string>? sagaTypes = null, CancellationToken cancellationToken = default);
}
