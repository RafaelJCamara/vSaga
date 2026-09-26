using System.Globalization;
using System.Text;
using VSaga.Abstractions.Persistence;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// The timeline: one document per entry, ordered by a per-instance <c>seq</c> allocated from
/// <c>sagaSequences</c>. An append is two round trips -- the counter increment, then the insert -- and
/// both commit on their own, outside any transaction, at majority write concern.
/// </summary>
/// <remarks>
/// Takes no dependency on <see cref="MongoSagaUnitOfWork"/>, structurally: an append must be durable
/// independently of any persist, because the engine's redelivery path relies on the
/// <c>MessageReceived</c> entry already being there when the redelivered copy is checked. A provider that
/// wrapped a whole message in one transaction would take the entry down with the aborted persist and
/// reprocess every redelivered message. Folding appends into the persist's transaction would therefore
/// need a deliberate API change here, not an omission.
/// </remarks>
public sealed class MongoSagaEventLogStore(MongoCollections collections, VSagaMongoOptions options, ILogger<MongoSagaEventLogStore> logger) : ISagaEventLogStore
{
    private static readonly SagaEntryType[] InboundEntryTypes = [SagaEntryType.SagaStarted, SagaEntryType.MessageReceived];

    public async Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var seq = await MongoCounters.IncrementAsync(collections.Sequences, MongoIds.Instance(entry.CorrelationId, entry.SagaType), 1, cancellationToken);
        await collections.EventLog.InsertOneAsync(SagaEventLogDocument.From(entry, seq, GuardPayload(entry)), cancellationToken: cancellationToken);
        return seq;
    }

    /// <summary>
    /// A payload above <see cref="VSagaMongoOptions.MaxPayloadJsonBytes"/> is replaced by a small JSON
    /// marker, loudly: the engine records the full inbound body before the step runs, so a document too
    /// large for MongoDB would otherwise make the saga permanently unstartable.
    /// </summary>
    private string? GuardPayload(SagaLogEntry entry)
    {
        if (entry.PayloadJson is not { } payload)
            return null;

        var bytes = Encoding.UTF8.GetByteCount(payload);
        if (bytes <= options.MaxPayloadJsonBytes)
            return payload;

        logger.LogWarning("Omitted a {Bytes}-byte payload from the {EntryType} entry of saga {SagaType}/{CorrelationId}: above MaxPayloadJsonBytes ({Limit}), and MongoDB caps a document at 16 MB.",
            bytes, entry.EntryType, entry.SagaType, entry.CorrelationId, options.MaxPayloadJsonBytes);
        return string.Create(CultureInfo.InvariantCulture, $"{{\"$vsagaPayloadOmitted\":true,\"bytes\":{bytes},\"limit\":{options.MaxPayloadJsonBytes}}}");
    }

    /// <remarks>Ascending <c>seq</c> (clause 5), served by the unique <c>(sagaType, correlationId, seq)</c> index.</remarks>
    public async Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var documents = await collections.EventLog.Find(Instance(sagaType, correlationId))
            .Sort(Builders<SagaEventLogDocument>.Sort.Ascending(d => d.Seq))
            .ToListAsync(cancellationToken);

        return documents.Select(d => d.ToEntry()).ToList();
    }

    /// <remarks>Only the two inbound entry types count (clause 6): outbound entries and the dead-letter entry carry message ids too, but none proves the message was processed.</remarks>
    public Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default)
    {
        var filter = Instance(sagaType, correlationId)
                     & Builders<SagaEventLogDocument>.Filter.Eq(d => d.MessageId, messageId)
                     & Builders<SagaEventLogDocument>.Filter.In(d => d.EntryType, InboundEntryTypes);

        return collections.EventLog.Find(filter).Limit(1).AnyAsync(cancellationToken);
    }

    private static FilterDefinition<SagaEventLogDocument> Instance(string sagaType, Guid correlationId) =>
        Builders<SagaEventLogDocument>.Filter.Eq(d => d.SagaType, sagaType)
        & Builders<SagaEventLogDocument>.Filter.Eq(d => d.CorrelationId, MongoIds.CorrelationText(correlationId));
}
