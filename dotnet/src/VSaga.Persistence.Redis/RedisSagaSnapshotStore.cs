using System.Text.Json;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.Redis;

/// <summary>
/// The snapshot store, and through <see cref="RedisPersistScripts"/> the one committer of the unit of
/// work's staged outbox rows: a persist writes the snapshot, its staged rows and every summary index in
/// one Lua script, on one shard, atomically.
/// </summary>
public sealed class RedisSagaSnapshotStore<TState>(RedisConnection connection, RedisKeySpace keys, RedisPersistScripts scripts, RedisSagaUnitOfWork unitOfWork)
    : ISagaSnapshotStore<TState>
    where TState : SagaState
{
    public async Task<TState?> FindAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var dataJson = await db.HashGetAsync(keys.Saga(correlationId, sagaType), RedisSnapshotRow.DataJsonField);

        return dataJson.IsNull ? null : Deserialize(sagaType, correlationId, dataJson.ToString());
    }

    /// <remarks>The reservation only selects the instance; the state comes from its blob, exactly as <see cref="FindAsync"/>'s does (clause 2).</remarks>
    public async Task<TState?> FindByBusinessKeyAsync(string sagaType, string businessKey, CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var reserved = await db.StringGetAsync(keys.BusinessKey(sagaType, businessKey));

        return reserved.IsNull ? null : await FindAsync(sagaType, Guid.Parse(reserved.ToString()), cancellationToken);
    }

    // The hash exists, so a blob that does not deserialise to a state is an error, never "no such saga"
    // (clause 11): a null here would have the orchestrator start a fresh instance over the live row.
    // Corrupt JSON already throws from the serializer; a blob that is JSON null yields null without one.
    private static TState Deserialize(string sagaType, Guid correlationId, string dataJson) =>
        JsonSerializer.Deserialize<TState>(dataJson)
        ?? throw new InvalidOperationException($"The stored state of '{sagaType}' saga instance '{correlationId}' deserialised to null; the hash exists but its blob is not a {typeof(TState).Name}.");

    public Task InsertAsync(TState state, CancellationToken cancellationToken = default) =>
        scripts.InsertAsync(RedisSnapshotRow.FromState(state, JsonSerializer.Serialize(state)), unitOfWork, cancellationToken);

    /// <remarks>
    /// The version is checked twice: once here, against a fresh read, so a stale caller is told before the
    /// live object is bumped or anything serialised; and again inside the script, as a compare-and-set,
    /// which is what makes the write correct against a rival landing between the two. Clause 1 holds on
    /// both paths: the bump happens before serialising and is undone on every throw after it.
    /// </remarks>
    public async Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default)
    {
        var current = await scripts.ReadHeadAsync(state.SagaType, state.CorrelationId, cancellationToken)
                      ?? throw new SagaNotFoundException(state.SagaType, state.CorrelationId);

        if (current.Version != expectedVersion)
            throw new SagaConcurrencyException(state.SagaType, state.CorrelationId, expectedVersion);

        state.Version = expectedVersion + 1;
        try
        {
            var row = RedisSnapshotRow.FromState(state, JsonSerializer.Serialize(state));
            await scripts.UpdateAsync(current, row, expectedVersion, unitOfWork, cancellationToken);
        }
        catch
        {
            state.Version = expectedVersion;
            throw;
        }
    }
}
