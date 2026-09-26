using VSaga.Abstractions.Persistence;
using MongoDB.Bson;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// The provider's secondary indexes, each with an explicit name so a future layout change is a named
/// drop-and-create rather than a guess. Created by the bootstrapper on every tick (idempotent when the
/// definition matches; an error when it does not, which is the marker a migration is due) and listed by
/// the probe, which reports the database Unhealthy until every name is present -- the business-key
/// index in particular is the adjudicator of the concurrent-double-initiate race, and sagas must not run
/// without it. Creating the partial indexes is also the capability check: a server that does not
/// support them (Amazon DocumentDB) fails here, at bootstrap, rather than silently later.
/// </summary>
internal static class MongoIndexes
{
    public const string BusinessKey = "ux_sagaType_businessKey";
    public const string UpdatedTotal = "ix_updatedAtTicks_total";
    public const string StatusUpdatedDesc = "ix_status_updatedDesc";
    public const string StatusDescUpdatedDesc = "ix_statusDesc_updatedDesc";
    public const string SagaTypeUpdated = "ix_sagaType_updated";
    public const string KindUpdated = "ix_kind_updated";
    public const string Parent = "ix_parent";
    public const string CorrelationId = "ix_correlationId";
    public const string TimelineSeq = "ux_instance_seq";
    public const string Dedupe = "ix_instance_messageId_entryType";
    public const string TimeoutClaim = "ix_timeout_claim";
    public const string TimeoutCancel = "ix_timeout_cancel";
    public const string OutboxClaim = "ix_outbox_claim";

    /// <summary>Every index name, by collection, that the probe expects to find.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Expected = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [MongoCollections.InstancesName] = [BusinessKey, UpdatedTotal, StatusUpdatedDesc, StatusDescUpdatedDesc, SagaTypeUpdated, KindUpdated, Parent, CorrelationId],
        [MongoCollections.EventLogName] = [TimelineSeq, Dedupe],
        [MongoCollections.TimeoutsName] = [TimeoutClaim, TimeoutCancel],
        [MongoCollections.OutboxName] = [OutboxClaim],
    };

    public static async Task CreateAllAsync(MongoCollections collections, CancellationToken cancellationToken)
    {
        await collections.Instances.Indexes.CreateManyAsync(InstanceIndexes(), cancellationToken);
        await collections.EventLog.Indexes.CreateManyAsync(EventLogIndexes(), cancellationToken);
        await collections.Timeouts.Indexes.CreateManyAsync(TimeoutIndexes(), cancellationToken);
        await collections.Outbox.Indexes.CreateManyAsync(OutboxIndexes(), cancellationToken);
    }

    /// <summary>The index names present on each of the collections in <see cref="Expected"/>.</summary>
    public static async Task<Dictionary<string, HashSet<string>>> ListAsync(MongoConnection connection, CancellationToken cancellationToken)
    {
        var present = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var collectionName in Expected.Keys)
        {
            var collection = connection.Database.GetCollection<BsonDocument>(collectionName);
            using var cursor = await collection.Indexes.ListAsync(cancellationToken);
            var indexes = await cursor.ToListAsync(cancellationToken);
            present[collectionName] = indexes.Select(i => i["name"].AsString).ToHashSet(StringComparer.Ordinal);
        }

        return present;
    }

    private static List<CreateIndexModel<SagaInstanceDocument>> InstanceIndexes()
    {
        var keys = Builders<SagaInstanceDocument>.IndexKeys;
        return
        [
            // $type: "string", not $exists: true -- $exists matches an explicit null, and a saga that
            // declares no CorrelateOn leaves the key null, which is the common case. The document omits
            // the field entirely when null (BsonIgnoreIfNull), belt and braces.
            new(keys.Ascending(d => d.SagaType).Ascending(d => d.BusinessKey), new CreateIndexOptions<SagaInstanceDocument>
            {
                Name = BusinessKey,
                Unique = true,
                PartialFilterExpression = Builders<SagaInstanceDocument>.Filter.Type(d => d.BusinessKey, BsonType.String),
            }),
            // The total orders ListAsync sorts by, tiebreakers included: MongoDB serves a sort from an
            // index only when the sort pattern equals the index pattern or its exact inverse, so the
            // identity tiebreak follows the lead key's direction, and the two Status walks (both keeping
            // UpdatedAt descending inside a bucket, as EF Core does) need two indexes.
            new(keys.Ascending(d => d.UpdatedAtTicks).Ascending(d => d.SagaType).Ascending(d => d.CorrelationId), new CreateIndexOptions { Name = UpdatedTotal }),
            new(keys.Ascending(d => d.Status).Descending(d => d.UpdatedAtTicks).Descending(d => d.SagaType).Descending(d => d.CorrelationId), new CreateIndexOptions { Name = StatusUpdatedDesc }),
            new(keys.Descending(d => d.Status).Descending(d => d.UpdatedAtTicks).Descending(d => d.SagaType).Descending(d => d.CorrelationId), new CreateIndexOptions { Name = StatusDescUpdatedDesc }),
            new(keys.Ascending(d => d.SagaType).Ascending(d => d.UpdatedAtTicks).Ascending(d => d.CorrelationId), new CreateIndexOptions { Name = SagaTypeUpdated }),
            new(keys.Ascending(d => d.Kind).Ascending(d => d.UpdatedAtTicks).Ascending(d => d.SagaType).Ascending(d => d.CorrelationId), new CreateIndexOptions { Name = KindUpdated }),
            new(keys.Ascending(d => d.ParentSagaType).Ascending(d => d.ParentCorrelationId).Ascending(d => d.CreatedAtTicks), new CreateIndexOptions<SagaInstanceDocument>
            {
                Name = Parent,
                PartialFilterExpression = Builders<SagaInstanceDocument>.Filter.Type(d => d.ParentSagaType, BsonType.String),
            }),
            new(keys.Ascending(d => d.CorrelationId).Ascending(d => d.SagaType), new CreateIndexOptions { Name = CorrelationId }),
        ];
    }

    private static List<CreateIndexModel<SagaEventLogDocument>> EventLogIndexes()
    {
        var keys = Builders<SagaEventLogDocument>.IndexKeys;
        return
        [
            new(keys.Ascending(d => d.SagaType).Ascending(d => d.CorrelationId).Ascending(d => d.Seq), new CreateIndexOptions { Name = TimelineSeq, Unique = true }),
            new(keys.Ascending(d => d.SagaType).Ascending(d => d.CorrelationId).Ascending(d => d.MessageId).Ascending(d => d.EntryType), new CreateIndexOptions<SagaEventLogDocument>
            {
                Name = Dedupe,
                PartialFilterExpression = Builders<SagaEventLogDocument>.Filter.Type(d => d.MessageId, BsonType.String),
            }),
        ];
    }

    private static List<CreateIndexModel<SagaTimeoutDocument>> TimeoutIndexes()
    {
        var keys = Builders<SagaTimeoutDocument>.IndexKeys;
        return
        [
            new(keys.Ascending(d => d.Status).Ascending(d => d.DueAtTicks).Ascending(d => d.Id), new CreateIndexOptions<SagaTimeoutDocument>
            {
                Name = TimeoutClaim,
                PartialFilterExpression = Builders<SagaTimeoutDocument>.Filter.Eq(d => d.Status, SagaTimeoutStatus.Pending),
            }),
            new(keys.Ascending(d => d.SagaType).Ascending(d => d.CorrelationId).Ascending(d => d.ForState).Ascending(d => d.Status), new CreateIndexOptions { Name = TimeoutCancel }),
        ];
    }

    private static List<CreateIndexModel<SagaOutboxDocument>> OutboxIndexes()
    {
        var keys = Builders<SagaOutboxDocument>.IndexKeys;
        return
        [
            new(keys.Ascending(d => d.Status).Ascending(d => d.CreatedAtTicks).Ascending(d => d.Id), new CreateIndexOptions<SagaOutboxDocument>
            {
                Name = OutboxClaim,
                PartialFilterExpression = Builders<SagaOutboxDocument>.Filter.Eq(d => d.Status, SagaOutboxStatus.Pending),
            }),
        ];
    }
}
