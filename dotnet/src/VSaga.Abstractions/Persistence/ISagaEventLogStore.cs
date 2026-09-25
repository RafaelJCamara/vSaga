namespace VSaga.Abstractions.Persistence;

/// <summary>Append-only audit trail: powers the dashboard timeline, precise manual-retry redrive, and compensation replay.</summary>
/// <remarks>
/// Reads are scoped to one saga instance — <c>(sagaType, correlationId)</c> — not to the correlation
/// id alone. Two saga types tracking the same correlation id each keep their own independent timeline;
/// merging them would corrupt both the dashboard view and, more seriously,
/// <c>SagaOrchestrator.GetVisitedStatesAsync</c>, which derives the compensation set from this log.
/// <see cref="AppendAsync"/> needs no separate parameter — every <see cref="SagaLogEntry"/> already
/// carries its own <see cref="SagaLogEntry.SagaType"/>.
/// </remarks>
public interface ISagaEventLogStore
{
    /// <summary>Appends the entry and returns its assigned, per-saga-instance-ordered sequence number.</summary>
    Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// The instance's full timeline, in ascending <see cref="SagaLogEntry.SequenceNumber"/> order —
    /// oldest first.
    /// </summary>
    /// <remarks>
    /// The ordering is contractual, not cosmetic: <c>SagaOrchestrator.GetVisitedStatesAsync</c> derives
    /// the visited-state sequence — which feeds compensation ordering — from this list exactly as
    /// returned, and the dashboard timeline renders it as-is.
    /// </remarks>
    Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True if a <see cref="SagaEntryType.SagaStarted"/> or <see cref="SagaEntryType.MessageReceived"/>
    /// entry with this (sagaType, correlationId, messageId) was already recorded — the
    /// idempotency/dedupe check for inbound messages.
    /// </summary>
    /// <remarks>
    /// Only those two entry types count, deliberately. Other entry types carry a
    /// <see cref="SagaLogEntry.MessageId"/> too — outbound <see cref="SagaEntryType.MessagePublished"/>
    /// and <see cref="SagaEntryType.MessageSent"/> entries, and the dead-letter path's
    /// <see cref="SagaEntryType.DeliveryExhausted"/> entry, which reuses the inbound message's own id —
    /// but none of them proves the message was processed. The infrastructure-failure redelivery path
    /// republishes with the original message id precisely so this check can recognise an
    /// already-recorded copy and skip it; matched more broadly, a redelivered message that never
    /// reached its <see cref="SagaEntryType.MessageReceived"/> log entry would be misread as a
    /// duplicate and silently dropped.
    /// </remarks>
    Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default);
}
