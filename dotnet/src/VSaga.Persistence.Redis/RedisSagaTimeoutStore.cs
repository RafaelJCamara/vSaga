using System.Globalization;
using VSaga.Abstractions.Persistence;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>
/// Timeouts are a row hash each, a due-ordered sorted set of the Pending ones, and a per-scope set for
/// cancellation. <see cref="ClaimDueAsync"/> is one Lua script and one round trip: atomic, earliest-due
/// first by construction, and safe for any number of dispatcher replicas -- the shape the plan finds
/// Redis best at.
/// </summary>
public sealed class RedisSagaTimeoutStore(RedisConnection connection, RedisKeySpace keys) : ISagaTimeoutStore
{
    private static readonly RedisScript Schedule = new(RedisLuaScripts.ScheduleTimeout);
    private static readonly RedisScript Cancel = new(RedisLuaScripts.CancelTimeouts);
    private static readonly RedisScript Claim = new(RedisLuaScripts.ClaimDueTimeouts);

    /// <remarks>The id is taken from a counter before the script so the row key is computed in C# like every other key; an id skipped by a crash in between is harmless.</remarks>
    public async Task ScheduleAsync(string sagaType, Guid correlationId, string forState, DateTimeOffset dueAtUtc, CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var id = await db.StringIncrementAsync(keys.TimeoutSequence);
        var dueMicroseconds = RedisTimestamps.ToMicroseconds(dueAtUtc);
        var scope = keys.TimeoutFor(sagaType, correlationId, forState);

        await Schedule.EvaluateAsync(db,
            [keys.TimeoutRow(id), keys.TimeoutDue, scope],
            [RedisKeySpace.PadId(id), dueMicroseconds, correlationId.ToString("D"), sagaType, forState, dueMicroseconds, (string?)scope ?? string.Empty]);
    }

    /// <remarks>Scoped to the (sagaType, correlationId, forState) set, so it is a lookup rather than a scan and never reaches another saga type's same-named state.</remarks>
    public async Task CancelAsync(string sagaType, Guid correlationId, string forState, CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        await Cancel.EvaluateAsync(db, [keys.TimeoutFor(sagaType, correlationId, forState), keys.TimeoutDue], [keys.TimeoutRowPrefix]);
    }

    public async Task<IReadOnlyList<SagaTimeout>> ClaimDueAsync(DateTimeOffset asOf, int batchSize, IReadOnlyCollection<string>? sagaTypes = null, CancellationToken cancellationToken = default)
    {
        // An empty set claims nothing (ISagaTimeoutStore.ClaimDueAsync); null claims every type.
        if (batchSize <= 0 || sagaTypes is { Count: 0 })
            return [];

        var values = new List<RedisValue>(4 + (sagaTypes?.Count ?? 0))
        {
            keys.TimeoutRowPrefix,
            RedisTimestamps.ToMicroseconds(asOf),
            batchSize,
            sagaTypes is null ? 0 : 1,
        };
        if (sagaTypes is not null)
            values.AddRange(sagaTypes.Select(t => (RedisValue)t));

        var db = await connection.GetDatabaseAsync(cancellationToken);
        var result = await Claim.EvaluateAsync(db, [keys.TimeoutDue], values.ToArray());

        return (result.AsArray()).Select(ToTimeout).ToList();
    }

    private static SagaTimeout ToTimeout(RedisResult row)
    {
        var fields = row.AsArray();
        return new SagaTimeout(
            long.Parse(fields[0].AsString(), CultureInfo.InvariantCulture),
            Guid.Parse(fields[1].AsString()),
            fields[2].AsString(),
            fields[3].AsString(),
            RedisTimestamps.FromMicroseconds((long)fields[4]),
            SagaTimeoutStatus.Fired);
    }
}
