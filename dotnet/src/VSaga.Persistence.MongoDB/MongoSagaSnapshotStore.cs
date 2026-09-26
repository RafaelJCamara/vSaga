using System.Text.Json;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using MongoDB.Bson;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// The snapshot store, and through <see cref="MongoPersistWrites"/> the one committer of the unit of
/// work's staged outbox rows. An insert is one <c>insertOne</c>; an update is one version-guarded
/// <c>replaceOne</c>; each runs inside a transaction with the staged rows only when there are any.
/// </summary>
public sealed class MongoSagaSnapshotStore<TState>(MongoCollections collections, MongoPersistWrites writes, MongoSagaUnitOfWork unitOfWork)
    : ISagaSnapshotStore<TState>
    where TState : SagaState
{
    private static readonly ProjectionDefinition<SagaInstanceDocument, BlobOnly> Blob =
        Builders<SagaInstanceDocument>.Projection.Expression(d => new BlobOnly(d.SagaType, d.CorrelationId, d.DataJson));

    public async Task<TState?> FindAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var filter = Builders<SagaInstanceDocument>.Filter.Eq(d => d.Id, MongoIds.Instance(correlationId, sagaType));
        var blob = await collections.Instances.Find(filter).Project(Blob).FirstOrDefaultAsync(cancellationToken);

        return blob is null ? null : Deserialize(blob);
    }

    /// <remarks>
    /// The reservation only selects the instance; the state comes from its blob, exactly as
    /// <see cref="FindAsync"/>'s does (clause 2). The <c>$type: string</c> predicate is what makes the partial
    /// unique index eligible: the planner does not infer a string from an equality to one, and without the
    /// predicate it scans every instance of the saga type instead -- the explain case pins this.
    /// </remarks>
    public async Task<TState?> FindByBusinessKeyAsync(string sagaType, string businessKey, CancellationToken cancellationToken = default)
    {
        var filter = Builders<SagaInstanceDocument>.Filter.Eq(d => d.SagaType, sagaType)
                     & Builders<SagaInstanceDocument>.Filter.Type(d => d.BusinessKey, BsonType.String)
                     & Builders<SagaInstanceDocument>.Filter.Eq(d => d.BusinessKey, businessKey);
        var blob = await collections.Instances.Find(filter).Project(Blob).FirstOrDefaultAsync(cancellationToken);

        return blob is null ? null : Deserialize(blob);
    }

    // The document exists, so a blob that does not deserialise to a state is an error, never "no such
    // saga" (clause 11): a null here would have the orchestrator start a fresh instance over the live
    // row. Corrupt JSON already throws from the serializer; a blob that is JSON null yields null without one.
    private static TState Deserialize(BlobOnly blob) =>
        JsonSerializer.Deserialize<TState>(blob.DataJson)
        ?? throw new InvalidOperationException($"The stored state of '{blob.SagaType}' saga instance '{blob.CorrelationId}' deserialised to null; the document exists but its blob is not a {typeof(TState).Name}.");

    /// <remarks>A duplicate key on the instance collection -- on <c>_id</c> or on the business-key index -- is the collision the contract names; a duplicate anywhere else is an infrastructure failure.</remarks>
    public async Task InsertAsync(TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var document = SagaInstanceDocument.From(state, JsonSerializer.Serialize(state));

        try
        {
            await writes.PersistAsync(unitOfWork, async (session, ct) =>
            {
                if (session is null)
                    await collections.Instances.InsertOneAsync(document, cancellationToken: ct);
                else
                    await collections.Instances.InsertOneAsync(session, document, cancellationToken: ct);
                return true;
            }, cancellationToken);
        }
        catch (MongoException ex) when (MongoErrors.IsDuplicateKeyOn(ex, collections.Connection.DatabaseName, MongoCollections.InstancesName))
        {
            throw new SagaAlreadyExistsException(state.SagaType, state.CorrelationId, ex);
        }
    }

    /// <remarks>
    /// Clause 1: the bump happens before serialising, outside the transaction callback (which may run more
    /// than once), and is undone on every throw path. The guard is the filter itself -- <c>{_id, version:
    /// expected}</c> -- and <c>MatchedCount</c> (never <c>ModifiedCount</c>: an identical document modifies
    /// nothing without being a race) decides; zero matches is disambiguated by a follow-up existence read
    /// into a concurrency failure or a missing saga, because conflating them turns a genuinely missing saga
    /// into an unbounded optimistic-retry loop.
    /// </remarks>
    public async Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var id = MongoIds.Instance(state.CorrelationId, state.SagaType);
        var guarded = Builders<SagaInstanceDocument>.Filter.Eq(d => d.Id, id) & Builders<SagaInstanceDocument>.Filter.Eq(d => d.Version, expectedVersion);

        state.Version = expectedVersion + 1;
        bool matched;
        try
        {
            var document = SagaInstanceDocument.From(state, JsonSerializer.Serialize(state));
            matched = await writes.PersistAsync(unitOfWork, async (session, ct) =>
            {
                var result = session is null
                    ? await collections.Instances.ReplaceOneAsync(guarded, document, cancellationToken: ct)
                    : await collections.Instances.ReplaceOneAsync(session, guarded, document, cancellationToken: ct);
                return result.MatchedCount > 0;
            }, cancellationToken);
        }
        catch (MongoException ex) when (MongoErrors.IsDuplicateKeyOn(ex, collections.Connection.DatabaseName, MongoCollections.InstancesName))
        {
            // The replacement moved the business key onto one another instance of this saga type holds
            // (fix F7): the same collision InsertAsync reports, and nothing was written.
            state.Version = expectedVersion;
            throw new SagaAlreadyExistsException(state.SagaType, state.CorrelationId, ex);
        }
        catch
        {
            state.Version = expectedVersion;
            throw;
        }

        if (!matched)
        {
            state.Version = expectedVersion;
            throw await ResolveMismatchAsync(state, id, expectedVersion, cancellationToken);
        }
    }

    private async Task<Exception> ResolveMismatchAsync(TState state, string id, int expectedVersion, CancellationToken cancellationToken)
    {
        var exists = await collections.Instances.Find(Builders<SagaInstanceDocument>.Filter.Eq(d => d.Id, id))
            .Project(Builders<SagaInstanceDocument>.Projection.Include(d => d.Id))
            .AnyAsync(cancellationToken);

        return exists
            ? new SagaConcurrencyException(state.SagaType, state.CorrelationId, expectedVersion)
            : new SagaNotFoundException(state.SagaType, state.CorrelationId);
    }

    /// <summary>The three fields a state read needs; the rest of the document stays on the server.</summary>
    private sealed record BlobOnly(string SagaType, string CorrelationId, string DataJson);
}
