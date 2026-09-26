using System.Text.Json.Nodes;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using MongoDB.Bson;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// The read side, over the projected fields of <c>sagaInstances</c>: a list is one <c>countDocuments</c>
/// and one <c>find</c> with a total-order sort, offset paging and an exact count over the same filtered
/// set, which is what the dashboard's change poller needs (a short page reads as "drained").
/// </summary>
public sealed class MongoSagaSummaryReader(MongoCollections collections) : ISagaSummaryReader, ISagaAdminStore
{
    private static readonly FilterDefinitionBuilder<SagaInstanceDocument> Filter = Builders<SagaInstanceDocument>.Filter;

    private static readonly ProjectionDefinition<SagaInstanceDocument, string?> DataJsonOnly =
        Builders<SagaInstanceDocument>.Projection.Expression<string?>(d => d.DataJson);

    public async Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Max(filter.PageSize, 1);
        var predicate = MongoListQuery.ToFilter(filter);

        var totalCount = (int)await collections.Instances.CountDocumentsAsync(predicate, cancellationToken: cancellationToken);
        var documents = await collections.Instances.Find(predicate)
            .Sort(MongoListQuery.ToSort(filter))
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<SagaSummary>(documents.Select(d => d.ToSummary()).ToList(), page, pageSize, totalCount);
    }

    public async Task<SagaSummary?> GetAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var document = await collections.Instances.Find(ById(sagaType, correlationId)).FirstOrDefaultAsync(cancellationToken);
        return document?.ToSummary();
    }

    public Task<string?> GetDataJsonAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
        collections.Instances.Find(ById(sagaType, correlationId)).Project(DataJsonOnly).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SagaSummary>> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        var documents = await collections.Instances.Find(Filter.Eq(d => d.CorrelationId, MongoIds.CorrelationText(correlationId)))
            .Sort(Builders<SagaInstanceDocument>.Sort.Ascending(d => d.SagaType))
            .ToListAsync(cancellationToken);

        return documents.Select(d => d.ToSummary()).ToList();
    }

    public async Task<IReadOnlyList<SagaSummary>> FindChildrenAsync(string parentSagaType, Guid parentCorrelationId, CancellationToken cancellationToken = default)
    {
        var filter = Filter.Eq(d => d.ParentSagaType, parentSagaType) & Filter.Eq(d => d.ParentCorrelationId, MongoIds.CorrelationText(parentCorrelationId));
        var documents = await collections.Instances.Find(filter)
            .Sort(Builders<SagaInstanceDocument>.Sort.Ascending(d => d.CreatedAtTicks).Ascending(d => d.SagaType))
            .ToListAsync(cancellationToken);

        return documents.Select(d => d.ToSummary()).ToList();
    }

    /// <remarks>A <c>$group</c> on the (sagaType, kind) pair -- the same distinct projection EF Core runs, without the anonymous-type detour.</remarks>
    public async Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default)
    {
        var group = new BsonDocument("$group", new BsonDocument("_id", new BsonDocument { { "sagaType", "$sagaType" }, { "kind", "$kind" } }));
        var groups = await collections.Instances.Aggregate().AppendStage<BsonDocument>(group).ToListAsync(cancellationToken);

        return groups
            .Select(g => g["_id"].AsBsonDocument)
            .Select(key => new SagaTypeInfo(key["sagaType"].AsString, (SagaKind)key["kind"].AsInt32))
            .OrderBy(t => t.SagaType, StringComparer.Ordinal)
            .ToList();
    }

    /// <remarks>
    /// A single-shot read, patch, version-guarded replace: <c>dataJson</c> is an opaque JSON string, and
    /// MongoDB's update language cannot patch properties inside a string, so the four engine-owned fields
    /// are patched here by exact property name -- the sibling of <c>EfCoreSagaSummaryReader.ResetStateAsync</c>,
    /// which must stay byte-identical in what it patches. Never retried: a lost race, at the read or at the
    /// write, is reported as <see cref="SagaConcurrencyException"/> (clause 7).
    /// </remarks>
    public async Task ResetStateAsync(string sagaType, Guid correlationId, string currentState, SagaStatus status, int expectedVersion, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        var document = await collections.Instances.Find(ById(sagaType, correlationId)).FirstOrDefaultAsync(cancellationToken)
                       ?? throw new SagaNotFoundException(sagaType, correlationId);

        if (document.Version != expectedVersion)
            throw new SagaConcurrencyException(sagaType, correlationId, expectedVersion);

        var newVersion = expectedVersion + 1;
        var node = JsonNode.Parse(document.DataJson)!.AsObject();
        node["CurrentState"] = currentState;
        node["Status"] = (int)status;
        node["Version"] = newVersion;
        node["UpdatedAtUtc"] = updatedAtUtc;

        document.CurrentState = currentState;
        document.Status = status;
        document.Version = newVersion;
        document.DataJson = node.ToJsonString();
        document.UpdatedAt = MongoTimestamps.ToDate(updatedAtUtc);
        document.UpdatedAtTicks = MongoTimestamps.ToTicks(updatedAtUtc);

        var guarded = ById(sagaType, correlationId) & Filter.Eq(d => d.Version, expectedVersion);
        var result = await collections.Instances.ReplaceOneAsync(guarded, document, cancellationToken: cancellationToken);
        if (result.MatchedCount == 0)
            throw new SagaConcurrencyException(sagaType, correlationId, expectedVersion);
    }

    private static FilterDefinition<SagaInstanceDocument> ById(string sagaType, Guid correlationId) =>
        Filter.Eq(d => d.Id, MongoIds.Instance(correlationId, sagaType));
}
