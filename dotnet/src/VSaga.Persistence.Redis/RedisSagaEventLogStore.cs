using System.Text.Json;
using VSaga.Abstractions.Persistence;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>
/// The timeline is a Redis list, one JSON element per entry, and <c>AppendAsync</c> is one <c>RPUSH</c>
/// whose reply -- the list's new length -- is the entry's per-instance sequence number by construction.
/// No counter key, no retry loop, and read-your-own-writes for free on a single-threaded server.
/// </summary>
/// <remarks>
/// Takes no dependency on <see cref="RedisSagaUnitOfWork"/>, structurally: an append must be durable
/// independently of any persist, because the engine's redelivery path relies on the
/// <c>MessageReceived</c> entry already being there when the redelivered copy is checked. It is also
/// the write most exposed to Redis's durability window -- see the provider's documentation.
/// </remarks>
public sealed class RedisSagaEventLogStore(RedisConnection connection, RedisKeySpace keys) : ISagaEventLogStore
{
    private static readonly RedisScript Append = new(RedisLuaScripts.AppendLogEntry);

    public async Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var db = await connection.GetDatabaseAsync(cancellationToken);

        // Only the two inbound entry types feed the dedupe set (clause 6): outbound entries and the
        // dead-letter entry carry message ids too, but none proves the message was processed.
        var dedupeId = entry is { EntryType: SagaEntryType.SagaStarted or SagaEntryType.MessageReceived, MessageId: { } messageId }
            ? messageId
            : string.Empty;

        var result = await Append.EvaluateAsync(db,
            [keys.Log(entry.CorrelationId, entry.SagaType), keys.Dedupe(entry.CorrelationId, entry.SagaType)],
            [JsonSerializer.Serialize(entry), dedupeId]);

        return (long)result;
    }

    /// <remarks>Ascending sequence order (clause 5) is the list's own order: position n holds sequence number n.</remarks>
    public async Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var elements = await db.ListRangeAsync(keys.Log(correlationId, sagaType));

        var timeline = new List<SagaLogEntry>(elements.Length);
        for (var i = 0; i < elements.Length; i++)
        {
            var entry = JsonSerializer.Deserialize<SagaLogEntry>(elements[i].ToString())
                        ?? throw new InvalidOperationException($"Timeline element {i + 1} of '{sagaType}' saga instance '{correlationId}' deserialised to null.");
            timeline.Add(entry with { SequenceNumber = i + 1 });
        }

        return timeline;
    }

    public async Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        return await db.SetContainsAsync(keys.Dedupe(correlationId, sagaType), messageId);
    }
}
