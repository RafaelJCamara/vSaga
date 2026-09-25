using VSaga.Abstractions.Sagas;

namespace VSaga.Abstractions.Persistence;

/// <summary>
/// Narrow administrative write, separate from <see cref="ISagaSnapshotStore{TState}"/>: resets a
/// saga's CurrentState/Status without needing to know the concrete TState type. Used by the
/// dashboard's whole-saga retry when a Failed saga has no specific technical step failure to
/// redrive (e.g. it reached Failed via a normal business transition or a timeout) — the saga is
/// reset to an earlier state and the message that produced that state is replayed.
/// </summary>
public interface ISagaAdminStore
{
    /// <summary>
    /// Resets the instance to <paramref name="currentState"/>/<paramref name="status"/>, bumping its
    /// version. The write is version-guarded: it throws <see cref="SagaConcurrencyException"/> if the
    /// stored version does not equal <paramref name="expectedVersion"/>, rather than retrying until
    /// it wins — an operator resetting a saga that is being actively processed should be told, not
    /// have the reset silently clobber a concurrent step's committed transition. Callers pass the
    /// version they read and validated — not a fresh re-read taken here — which is what makes the
    /// resulting conflict mean "the saga changed since you looked".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reset patches <c>CurrentState</c>, <c>Status</c>, <c>Version</c> and <c>UpdatedAtUtc</c>
    /// inside the state blob, in lockstep with the projected fields. Contrary to what this contract
    /// once claimed, it must touch <c>DataJson</c>: snapshot reads deserialise from the blob
    /// (<see cref="ISagaSnapshotStore{TState}.FindAsync"/>), so a projection-only reset would be
    /// invisible to the engine, which would resume the saga from the very state the operator reset it
    /// away from. The saga's business fields inside the blob are untouched — only those four
    /// engine-owned fields change.
    /// </para>
    /// <para>
    /// <paramref name="updatedAtUtc"/> is supplied by the caller rather than minted here, from the
    /// caller's own <c>TimeProvider</c> — the same reasoning as
    /// <see cref="ISagaOutboxStore.EnqueueAsync"/>'s <c>createdAtUtc</c>: a store-side
    /// <c>DateTimeOffset.UtcNow</c> would bypass the clock every engine write goes through.
    /// </para>
    /// </remarks>
    Task ResetStateAsync(string sagaType, Guid correlationId, string currentState, SagaStatus status, int expectedVersion, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
}
