using VSaga.Abstractions.Persistence;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// Timeouts are one document each, with a numeric id from the <c>sagaCounters</c> collection because
/// <c>SagaTimeout.Id</c> is a <c>long</c>. Schedule and cancel commit on their own, outside any
/// transaction, as EF Core's do; the claim is the same <c>findOneAndUpdate</c> loop as the outbox's.
/// </summary>
public sealed class MongoSagaTimeoutStore(MongoCollections collections) : ISagaTimeoutStore
{
    private static readonly SortDefinition<SagaTimeoutDocument> EarliestDueFirst =
        Builders<SagaTimeoutDocument>.Sort.Ascending(d => d.DueAtTicks).Ascending(d => d.Id);

    private static readonly UpdateDefinition<SagaTimeoutDocument> MarkFired =
        Builders<SagaTimeoutDocument>.Update.Set(d => d.Status, SagaTimeoutStatus.Fired);

    private static readonly UpdateDefinition<SagaTimeoutDocument> MarkCancelled =
        Builders<SagaTimeoutDocument>.Update.Set(d => d.Status, SagaTimeoutStatus.Cancelled);

    /// <remarks>The id is taken from the counter before the insert; an id skipped by a crash in between is harmless.</remarks>
    public async Task ScheduleAsync(string sagaType, Guid correlationId, string forState, DateTimeOffset dueAtUtc, CancellationToken cancellationToken = default)
    {
        var id = await MongoCounters.IncrementAsync(collections.Counters, MongoCollections.TimeoutsName, 1, cancellationToken);
        await collections.Timeouts.InsertOneAsync(new SagaTimeoutDocument
        {
            Id = id,
            SagaType = sagaType,
            CorrelationId = MongoIds.CorrelationText(correlationId),
            ForState = forState,
            DueAt = MongoTimestamps.ToDate(dueAtUtc),
            DueAtTicks = MongoTimestamps.ToTicks(dueAtUtc),
            Status = SagaTimeoutStatus.Pending,
        }, cancellationToken: cancellationToken);
    }

    /// <remarks>All four predicates: state names are only unique within a saga type, so without the type one saga would cancel another's timeout for a same-named state.</remarks>
    public Task CancelAsync(string sagaType, Guid correlationId, string forState, CancellationToken cancellationToken = default)
    {
        var filter = Builders<SagaTimeoutDocument>.Filter.Eq(d => d.SagaType, sagaType)
                     & Builders<SagaTimeoutDocument>.Filter.Eq(d => d.CorrelationId, MongoIds.CorrelationText(correlationId))
                     & Builders<SagaTimeoutDocument>.Filter.Eq(d => d.ForState, forState)
                     & Builders<SagaTimeoutDocument>.Filter.Eq(d => d.Status, SagaTimeoutStatus.Pending);

        return collections.Timeouts.UpdateManyAsync(filter, MarkCancelled, cancellationToken: cancellationToken);
    }

    /// <remarks>Earliest-due first, one atomic claim per round trip, the saga-type filter inside the claim's own predicate so the truncation applies after it (clause 10).</remarks>
    public async Task<IReadOnlyList<SagaTimeout>> ClaimDueAsync(DateTimeOffset asOf, int batchSize, IReadOnlyCollection<string>? sagaTypes = null, CancellationToken cancellationToken = default)
    {
        // An empty set claims nothing (ISagaTimeoutStore.ClaimDueAsync); null claims every type.
        if (batchSize <= 0 || sagaTypes is { Count: 0 })
            return [];

        var filter = Builders<SagaTimeoutDocument>.Filter.Eq(d => d.Status, SagaTimeoutStatus.Pending)
                     & Builders<SagaTimeoutDocument>.Filter.Lte(d => d.DueAtTicks, MongoTimestamps.ToTicks(asOf));
        if (sagaTypes is not null)
            filter &= Builders<SagaTimeoutDocument>.Filter.In(d => d.SagaType, sagaTypes);

        var options = new FindOneAndUpdateOptions<SagaTimeoutDocument> { Sort = EarliestDueFirst, ReturnDocument = ReturnDocument.After };
        var claimed = new List<SagaTimeout>();
        while (claimed.Count < batchSize)
        {
            var document = await collections.Timeouts.FindOneAndUpdateAsync(filter, MarkFired, options, cancellationToken);
            if (document is null)
                break;

            claimed.Add(document.ToTimeout());
        }

        return claimed;
    }
}
