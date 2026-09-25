using VSaga.Abstractions.Sagas;

namespace VSaga.Abstractions.Persistence;

/// <summary>
/// Current-state snapshot per saga instance, typed to the saga's own state class. Used by the
/// orchestrator for fast load/save with optimistic concurrency. Separate from the append-only
/// event log (<see cref="ISagaEventLogStore"/>), which is the audit/timeline/redrive source of truth.
/// </summary>
/// <remarks>
/// <para>
/// One of seven deliberately narrow persistence contracts rather than a single repository. The
/// carve-up follows what each caller can know: this store alone is generic — only the engine holds the
/// concrete <typeparamref name="TState"/> — while <see cref="ISagaEventLogStore"/> (the append-only
/// audit trail), <see cref="ISagaTimeoutStore"/> (the durable timeout schedule),
/// <see cref="ISagaOutboxStore"/> (the crash-recovery publish backstop),
/// <see cref="ISagaSummaryReader"/> (cross-saga-type reads), <see cref="ISagaAdminStore"/> (the one
/// administrative write) and <see cref="IServiceTopologyStore"/> (the observability service map) are
/// all non-generic, which is what lets the dashboard and the dispatcher hosted services operate on
/// sagas they hold no definitions for. A provider implements the seven together, and the doc comments
/// on these interfaces are the contract it is written against — not a description of any one
/// implementation.
/// </para>
/// <para>
/// A saga instance is identified by <c>(sagaType, correlationId)</c>, not by correlation id alone:
/// two different saga types may legitimately track the same business correlation id — that is exactly
/// what lets a choreographed saga observe messages already flowing under an orchestrated saga's id.
/// <typeparamref name="TState"/> does not imply the saga type (two saga definitions can share a state
/// class), so the type is always passed explicitly rather than inferred.
/// </para>
/// <para>
/// Timestamps belong to the caller. The orchestrator stamps <c>state.UpdatedAtUtc</c> from its own
/// <c>TimeProvider</c> immediately before every persist; implementations write the value they are
/// given and never restamp it from their own clock. A store-side <c>DateTimeOffset.UtcNow</c> would
/// replace the engine's clock with the store's — the recorded timestamp would no longer say when the
/// engine committed the transition, and a test driving a fake <c>TimeProvider</c> could never hold
/// the value still.
/// </para>
/// </remarks>
public interface ISagaSnapshotStore<TState> where TState : SagaState
{
    /// <summary>Loads the saga's current state, or null if no snapshot exists for this (sagaType, correlationId) instance.</summary>
    /// <remarks>
    /// The returned object is deserialised from the stored state blob, not reassembled from projected
    /// fields — the blob is the authoritative copy of the state, and the queryable columns
    /// <see cref="ISagaSummaryReader"/> reads are a projection derived from it. A store that patched
    /// fields like <c>Version</c> back in from its projection would paper over exactly the
    /// blob/projection drift the write-side contracts exist to prevent.
    /// </remarks>
    Task<TState?> FindAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="SagaAlreadyExistsException"/> if a snapshot already exists for this state's own (SagaType, CorrelationId) pair.</summary>
    Task InsertAsync(TState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists <paramref name="state"/> over the existing snapshot. Throws
    /// <see cref="SagaConcurrencyException"/> if the stored version does not equal
    /// <paramref name="expectedVersion"/>.
    /// </summary>
    /// <remarks>
    /// Mutates <c>state.Version</c> in place to <paramref name="expectedVersion"/> + 1 <b>before</b>
    /// serialising the blob — so the stored copy embeds the new version — and restores it to
    /// <paramref name="expectedVersion"/> on <b>every</b> throw path. The invariant is that the live
    /// object never claims a version the store did not durably record. The orchestrator depends on the
    /// bump half directly: a timeout persists the same live object twice (an up-front claim, then the
    /// post-side-effect commit), passing <c>state.Version</c> each time, so a store that writes version
    /// n+1 durably but leaves the object at n makes every timeout's final persist report a concurrency
    /// race it did not lose — a branch that only logs, because its side effects are already sent, so
    /// the saga stalls silently. The restore half is the same invariant after a failure: a caller that
    /// retries or abandons after a throw must still be holding <paramref name="expectedVersion"/>, not
    /// a version that exists nowhere.
    /// </remarks>
    Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up the instance that reserved this business key, or null if none has. At most one instance
    /// can exist for a given (sagaType, businessKey) pair -- enforced at InsertAsync time by a unique
    /// constraint, not by this method.
    /// </summary>
    /// <remarks>
    /// Deserialises from the stored state blob, exactly as <see cref="FindAsync"/> does — the business
    /// key only selects the row.
    /// </remarks>
    Task<TState?> FindByBusinessKeyAsync(string sagaType, string businessKey, CancellationToken cancellationToken = default);
}
