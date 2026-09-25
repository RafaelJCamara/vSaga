namespace VSaga.Abstractions.Persistence;

public enum SagaOutboxStatus
{
    Pending,
    Dispatched,
}

public sealed record SagaOutboxMessage(
    long Id,
    Guid CorrelationId,
    string SagaType,
    string MessageId,
    string MessageTypeName,
    ReadOnlyMemory<byte> Body,
    string? Destination,
    IReadOnlyDictionary<string, string> Headers,
    SagaOutboxStatus Status,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Durable crash-recovery backstop for <c>ctx.PublishAfterCommitAsync</c>, polled by the
/// SagaOutboxDispatcher hosted service. This is NOT the dispatch path — the inline drain right after
/// each step's own persist still sends every message synchronously, exactly as today. A row here only
/// ever gets (re)dispatched if a crash happens between that persist and the inline drain completing.
/// </summary>
/// <remarks>
/// Scoped per saga instance — <c>(sagaType, correlationId)</c> — the same precedent
/// <see cref="ISagaTimeoutStore"/> establishes.
/// </remarks>
public interface ISagaOutboxStore
{
    /// <summary>
    /// Stages one queued publish for durable recording. <paramref name="messageId"/> and
    /// <paramref name="createdAtUtc"/> are supplied by the caller rather than minted here — the row must
    /// describe the exact same message identity as the in-memory dispatch closure it backs, and the
    /// caller's own <c>TimeProvider</c> is what every other timestamp in a saga's timeline already goes
    /// through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Does not commit on its own.</b> A staged row becomes durable at the next successful commit
    /// in the same unit of work, <i>whichever store issues it</i> — not only at a persist.
    /// production-readiness.md §4.1 step 2 describes the intended pairing: the orchestrator calls this
    /// immediately before its own <c>PersistAsync</c>, and because every EF store shares one
    /// <c>VSagaDbContext</c> per message, that persist commits these rows and the snapshot together,
    /// in one implicit transaction. But the persist is not the unit of work's only commit — on EF,
    /// every <c>SaveChangesAsync</c> on the shared context flushes whatever is staged: the snapshot
    /// store's <c>InsertAsync</c> and <c>UpdateAsync</c>, the event log's <c>AppendAsync</c>, the
    /// timeout store's <c>ScheduleAsync</c> and <c>CancelAsync</c>, this store's own
    /// <see cref="MarkDispatchedAsync"/>, and <see cref="ISagaAdminStore"/>'s reset. <c>ScheduleAsync</c>
    /// runs on the ordinary success path, so "committed by the next persist" was never literally the
    /// rule.
    /// </para>
    /// <para>
    /// The rest of the lifecycle follows: a commit that throws leaves staged rows uncommitted and
    /// still staged, and a unit of work that ends with rows still staged must not leave them durable —
    /// they are dropped via <see cref="DiscardPendingAsync"/> or die with it. An implementation that
    /// commits here instead would reopen exactly the dual-write window the outbox exists to close — a
    /// crash, or a persist that loses its optimistic-concurrency race, would leave a durable Pending
    /// row describing a state transition that never committed, which
    /// <see cref="ClaimPendingAsync"/>'s poller would then faithfully publish.
    /// </para>
    /// </remarks>
    Task EnqueueAsync(string sagaType, Guid correlationId, string messageId, string messageTypeName,
        ReadOnlyMemory<byte> body, string? destination, IReadOnlyDictionary<string, string> headers,
        DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks one row Dispatched directly, by the message id it was enqueued under — the inline drain's
    /// path, called right after it sends the message synchronously. Never goes through
    /// <see cref="ClaimPendingAsync"/>, which is only ever reached by the recovery poller. Keyed on
    /// <c>messageId</c> rather than a row id because <see cref="EnqueueAsync"/> deliberately doesn't
    /// commit, so no database-generated id exists yet at enqueue time; every message id here is a
    /// freshly minted GUID from <c>MessageEnvelope.From</c>, so it identifies exactly one row.
    /// </summary>
    Task MarkDispatchedAsync(string messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops staged-but-uncommitted rows for <paramref name="messageIds"/>, for the paths that abandon a
    /// batch of deferred publishes instead of draining it (<c>SagaOrchestrator.DiscardDeferredPublishesAsync</c>
    /// — a timeout whose persist lost its concurrency race, or a step that threw).
    /// </summary>
    /// <remarks>
    /// Necessary because <see cref="EnqueueAsync"/> leaves its rows pending in the shared unit of work:
    /// left staged, the very next <c>ISagaEventLogStore.AppendAsync</c> on that same context — which the
    /// discard path itself performs, one entry per dropped publish — would flush them, resurrecting the
    /// exact publishes the discard exists to suppress. Discarding is not a status change: the rows must
    /// leave the unit of work entirely, since a Pending *or* Dispatched row would both be wrong for a
    /// transition that was never recorded.
    /// </remarks>
    Task DiscardPendingAsync(IReadOnlyCollection<string> messageIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims (marks Dispatched) and returns up to <paramref name="batchSize"/> rows still
    /// Pending and created at or before <paramref name="olderThan"/>, earliest-created first, for the
    /// recovery poller to republish. The grace period itself is the caller's concern
    /// (<c>now - DispatchGracePeriod</c>) — this store only ever compares against the absolute cutoff
    /// it's given, matching <see cref="ISagaTimeoutStore.ClaimDueAsync"/>'s own <c>asOf</c> shape.
    /// </summary>
    /// <remarks>
    /// Earliest-created first for the same reason <see cref="ISagaTimeoutStore.ClaimDueAsync"/> is
    /// earliest-due first: <paramref name="batchSize"/> truncates, and ordering decides which rows wait
    /// for a later poll — an arbitrary order could strand the oldest crash-recovered publish behind
    /// newer ones indefinitely.
    /// </remarks>
    Task<IReadOnlyList<SagaOutboxMessage>> ClaimPendingAsync(DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken = default);
}
