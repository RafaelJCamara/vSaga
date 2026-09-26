using VSaga.Abstractions.Sagas;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>
/// Runs the persist script (<see cref="RedisLuaScripts.Persist"/>) -- the only code that writes a
/// snapshot, and so the only committer of staged outbox rows. Shared by the snapshot store and the admin
/// store's reset, which is an update of the projected fields with no outbox rows.
/// </summary>
/// <remarks>
/// Maps the script's numeric sentinels, and only those, to the domain exceptions: a Redis error string
/// is always an infrastructure failure. Mapping an infrastructure error to
/// <see cref="SagaConcurrencyException"/> would make a timeout vanish with its side effects already
/// sent; mapping a real version mismatch to an infrastructure error would redeliver forever.
/// </remarks>
public sealed class RedisPersistScripts(RedisConnection connection, RedisKeySpace keys, VSagaRedisOptions options, RedisServerProbe probe)
{
    private const int FixedKeyCount = 16;
    private static readonly RedisScript Persist = new(RedisLuaScripts.Persist);

    /// <summary>
    /// Test hook: makes the next persist script abort after the snapshot write and before the indexes,
    /// the tear the torn-write sentinel exists to make loud. Internal, and never set by the provider.
    /// </summary>
    internal bool FailAfterSnapshotWrite { get; set; }

    /// <summary>What an update needs of the row it replaces: the version to compare and the index keys to move off.</summary>
    internal sealed record Head(int Version, SagaStatus Status, SagaKind Kind, string? BusinessKey)
    {
        public static Head From(RedisSnapshotRow row) => new(row.Version, row.Status, row.Kind, row.BusinessKey);
    }

    /// <summary>The current row's head, or null when no such instance exists.</summary>
    internal async Task<Head?> ReadHeadAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var values = await db.HashGetAsync(keys.Saga(correlationId, sagaType), [RedisSnapshotRow.VersionField, "status", "kind", "businessKey"]);
        if (values[0].IsNull)
            return null;

        return new Head((int)values[0], (SagaStatus)(int)values[1], (SagaKind)(int)values[2], (string?)values[3]);
    }

    internal Task InsertAsync(RedisSnapshotRow row, RedisSagaUnitOfWork? unitOfWork, CancellationToken cancellationToken) =>
        RunAsync(mode: 0, current: null, row, expectedVersion: 0, unitOfWork, cancellationToken);

    internal Task UpdateAsync(Head current, RedisSnapshotRow row, int expectedVersion, RedisSagaUnitOfWork? unitOfWork, CancellationToken cancellationToken) =>
        RunAsync(mode: 1, current, row, expectedVersion, unitOfWork, cancellationToken);

    /// <summary>Commits the unit of work's staged rows with no snapshot -- the conformance suite's <c>CommitAsync</c> hook, never the engine's path.</summary>
    internal Task CommitStagedOutboxAsync(RedisSagaUnitOfWork unitOfWork, CancellationToken cancellationToken) =>
        RunAsync(mode: 2, current: null, row: null, expectedVersion: 0, unitOfWork, cancellationToken);

    private async Task RunAsync(int mode, Head? current, RedisSnapshotRow? row, int expectedVersion, RedisSagaUnitOfWork? unitOfWork, CancellationToken cancellationToken)
    {
        RefuseUnderMemoryPressure();

        var staged = unitOfWork?.Staged.ToArray() ?? [];
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var result = await Persist.EvaluateAsync(db, BuildKeys(mode, current, row, staged), BuildArguments(mode, current, row, expectedVersion, staged));

        if (row is not null)
            ThrowForSentinel((long)result, row, expectedVersion);

        unitOfWork?.Committed(staged);
    }

    /// <summary>
    /// The pre-flight memory gate: a Lua script has no rollback, so a persist is refused before any write
    /// while the last probe put the server above the threshold, rather than risking an out-of-memory
    /// abort midway. The ratio is the probe's, read on its interval, not fetched per write.
    /// </summary>
    private void RefuseUnderMemoryPressure()
    {
        if (probe.Latest is { MemoryRatio: { } ratio } && ratio > options.WriteMemoryThreshold)
            throw new RedisMemoryPressureException(ratio, options.WriteMemoryThreshold);
    }

    private RedisKey[] BuildKeys(int mode, Head? current, RedisSnapshotRow? row, RedisStagedOutboxRow[] staged)
    {
        var result = new RedisKey[FixedKeyCount + staged.Length];
        Array.Fill(result, keys.Nil);
        result[0] = keys.Torn;
        result[14] = keys.OutboxPending;
        result[15] = keys.OutboxSequence;

        if (mode != 2 && row is not null)
        {
            result[1] = keys.Saga(row.CorrelationId, row.SagaType);
            if (current?.BusinessKey is { } oldKey)
                result[2] = keys.BusinessKey(row.SagaType, oldKey);
            if (row.BusinessKey is { } newKey)
                result[3] = keys.BusinessKey(row.SagaType, newKey);
            result[4] = keys.IndexUpdated;
            result[5] = current is null ? keys.Nil : keys.IndexStatus(current.Status);
            result[6] = keys.IndexStatus(row.Status);
            result[7] = current is null ? keys.Nil : keys.IndexKind(current.Kind);
            result[8] = keys.IndexKind(row.Kind);
            result[9] = keys.IndexType(row.SagaType);
            if (row.ParentSagaType is { } parentType && row.ParentCorrelationId is { } parentId)
                result[10] = keys.IndexParent(parentType, parentId);
            result[11] = keys.IndexCorrelation(row.CorrelationId);
            result[12] = keys.IndexTypes;
            result[13] = keys.IndexTypeCounts;
        }

        for (var i = 0; i < staged.Length; i++)
            result[FixedKeyCount + i] = keys.OutboxRow(staged[i].MessageId);

        return result;
    }

    private RedisValue[] BuildArguments(int mode, Head? current, RedisSnapshotRow? row, int expectedVersion, RedisStagedOutboxRow[] staged)
    {
        var businessKeyChanged = !string.Equals(current?.BusinessKey, row?.BusinessKey, StringComparison.Ordinal);
        var snapshot = row?.ToHashArguments() ?? [];
        var hasParent = row is { ParentSagaType: not null, ParentCorrelationId: not null };

        var values = new List<RedisValue>(16 + snapshot.Length + (staged.Length * 24))
        {
            mode,
            expectedVersion,
            row?.Member ?? string.Empty,
            RedisTimestamps.ToMicroseconds(DateTimeOffset.UtcNow),
            businessKeyChanged && row?.BusinessKey is not null ? 1 : 0,
            businessKeyChanged && current?.BusinessKey is not null ? 1 : 0,
            row?.CorrelationId.ToString("D") ?? string.Empty,
            row?.Member ?? string.Empty,
            row?.UpdatedMicroseconds ?? 0,
            row?.CreatedMicroseconds ?? 0,
            hasParent ? 1 : 0,
            row?.SagaType ?? string.Empty,
            row is null ? 0 : (int)row.Kind,
            FailAfterSnapshotWrite ? 1 : 0,
            snapshot.Length / 2,
        };
        values.AddRange(snapshot);

        foreach (var stagedRow in staged)
        {
            var fields = stagedRow.ToHashArguments();
            values.Add(stagedRow.CreatedMicroseconds);
            values.Add(stagedRow.MessageId);
            values.Add(fields.Length / 2);
            values.AddRange(fields);
        }

        return values.ToArray();
    }

    private static void ThrowForSentinel(long code, RedisSnapshotRow row, int expectedVersion)
    {
        switch (code)
        {
            case 1:
                return;
            case -1:
                throw new SagaConcurrencyException(row.SagaType, row.CorrelationId, expectedVersion);
            case -2:
                throw new SagaNotFoundException(row.SagaType, row.CorrelationId);
            case -3:
            case -4:
                throw new SagaAlreadyExistsException(row.SagaType, row.CorrelationId);
            default:
                throw new RedisPersistenceProtocolException($"The persist script returned {code}, which is not a sentinel this provider defines; the Lua and the C# disagree.");
        }
    }
}
