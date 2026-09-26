using System.Text.Json;
using VSaga.Abstractions.Persistence;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// <see cref="EnqueueAsync"/> only stages into the scoped <see cref="MongoSagaUnitOfWork"/> -- zero
/// MongoDB operations, which is the contract's must-not-commit literally -- and takes no collection
/// handle for it, so committing a row early would need a deliberate API change. The persist writes the
/// rows inside its transaction. <see cref="ClaimPendingAsync"/> is a <c>findOneAndUpdate</c> loop.
/// </summary>
public sealed class MongoSagaOutboxStore(MongoCollections collections, MongoSagaUnitOfWork unitOfWork) : ISagaOutboxStore
{
    private static readonly SortDefinition<SagaOutboxDocument> EarliestCreatedFirst =
        Builders<SagaOutboxDocument>.Sort.Ascending(d => d.CreatedAtTicks).Ascending(d => d.Id);

    private static readonly UpdateDefinition<SagaOutboxDocument> MarkDispatched =
        Builders<SagaOutboxDocument>.Update.Set(d => d.Status, SagaOutboxStatus.Dispatched);

    /// <remarks>Stages only, per <see cref="ISagaOutboxStore.EnqueueAsync"/>. The headers are serialised now, so the row records them as they are at this call.</remarks>
    public Task EnqueueAsync(string sagaType, Guid correlationId, string messageId, string messageTypeName,
        ReadOnlyMemory<byte> body, string? destination, IReadOnlyDictionary<string, string> headers,
        DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
    {
        unitOfWork.Stage(new MongoStagedOutboxRow(messageId, correlationId, sagaType, messageTypeName, body.ToArray(), destination,
            JsonSerializer.Serialize(headers), createdAtUtc));

        return Task.CompletedTask;
    }

    public Task MarkDispatchedAsync(string messageId, CancellationToken cancellationToken = default) =>
        collections.Outbox.UpdateOneAsync(Builders<SagaOutboxDocument>.Filter.Eq(d => d.MessageId, messageId), MarkDispatched, cancellationToken: cancellationToken);

    /// <remarks>Drops staged rows only: a row an earlier commit wrote is durable and out of this unit of work's reach, as the contract's staged-row lifecycle requires.</remarks>
    public Task DiscardPendingAsync(IReadOnlyCollection<string> messageIds, CancellationToken cancellationToken = default)
    {
        unitOfWork.Discard(messageIds);
        return Task.CompletedTask;
    }

    /// <remarks>
    /// <c>findOneAndUpdate</c> claims exactly one document atomically -- it flips the status and returns the
    /// row in one operation, and is a retryable write -- so a batch is a loop, up to <paramref name="batchSize"/>
    /// round trips where Postgres costs one statement; an idle poll costs exactly one. The rejected
    /// alternative, a claim-token <c>updateMany</c>, is not retryable and strands the whole batch marked
    /// terminal on a crash between it and the follow-up read. This loop's own failure mode -- a crash at
    /// iteration k leaves k rows claimed in a list that dies with the process -- is exactly Postgres's,
    /// not tighter.
    /// </remarks>
    public async Task<IReadOnlyList<SagaOutboxMessage>> ClaimPendingAsync(DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken = default)
    {
        var filter = Builders<SagaOutboxDocument>.Filter.Eq(d => d.Status, SagaOutboxStatus.Pending)
                     & Builders<SagaOutboxDocument>.Filter.Lte(d => d.CreatedAtTicks, MongoTimestamps.ToTicks(olderThan));
        var options = new FindOneAndUpdateOptions<SagaOutboxDocument> { Sort = EarliestCreatedFirst, ReturnDocument = ReturnDocument.After };

        var claimed = new List<SagaOutboxMessage>();
        while (claimed.Count < batchSize)
        {
            var document = await collections.Outbox.FindOneAndUpdateAsync(filter, MarkDispatched, options, cancellationToken);
            if (document is null)
                break;

            claimed.Add(ToMessage(document));
        }

        return claimed;
    }

    private static SagaOutboxMessage ToMessage(SagaOutboxDocument document)
    {
        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(document.HeadersJson) ?? [];

        return new SagaOutboxMessage(document.Id, Guid.Parse(document.CorrelationId), document.SagaType, document.MessageId,
            document.MessageTypeName, document.Body, document.Destination, new Dictionary<string, string>(headers, StringComparer.Ordinal),
            document.Status, MongoTimestamps.FromTicks(document.CreatedAtTicks));
    }
}
