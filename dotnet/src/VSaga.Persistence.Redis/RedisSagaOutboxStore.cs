using System.Text.Json;
using VSaga.Abstractions.Persistence;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>
/// Outbox rows are keyed on the message id -- both <see cref="MarkDispatchedAsync"/> and
/// <see cref="DiscardPendingAsync"/> are message-id-keyed, and no store-generated id exists at enqueue
/// time. <see cref="EnqueueAsync"/> only stages into the scoped <see cref="RedisSagaUnitOfWork"/>; the
/// persist script writes the rows with the snapshot, and only then adds them to the pending set.
/// </summary>
public sealed class RedisSagaOutboxStore(RedisConnection connection, RedisKeySpace keys, RedisSagaUnitOfWork unitOfWork) : ISagaOutboxStore
{
    private static readonly RedisScript Mark = new(RedisLuaScripts.MarkOutboxDispatched);
    private static readonly RedisScript Claim = new(RedisLuaScripts.ClaimPendingOutbox);

    /// <remarks>Stages only, per <see cref="ISagaOutboxStore.EnqueueAsync"/>: no connection is touched here. The headers are serialised now, so the row records them as they are at this call.</remarks>
    public Task EnqueueAsync(string sagaType, Guid correlationId, string messageId, string messageTypeName,
        ReadOnlyMemory<byte> body, string? destination, IReadOnlyDictionary<string, string> headers,
        DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
    {
        unitOfWork.Stage(new RedisStagedOutboxRow(messageId, correlationId, sagaType, messageTypeName, body.ToArray(), destination,
            JsonSerializer.Serialize(headers), RedisTimestamps.ToMicroseconds(createdAtUtc)));

        return Task.CompletedTask;
    }

    public async Task MarkDispatchedAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        await Mark.EvaluateAsync(db, [keys.OutboxRow(messageId), keys.OutboxPending], [messageId]);
    }

    /// <remarks>Drops staged rows only: a row an earlier commit wrote is durable and out of this unit of work's reach, as the contract's staged-row lifecycle requires.</remarks>
    public Task DiscardPendingAsync(IReadOnlyCollection<string> messageIds, CancellationToken cancellationToken = default)
    {
        unitOfWork.Discard(messageIds);
        return Task.CompletedTask;
    }

    /// <remarks>One script, one round trip, earliest-created first; each claimed row is marked Dispatched and leaves the pending set inside the same script, so two dispatchers can never both claim one.</remarks>
    public async Task<IReadOnlyList<SagaOutboxMessage>> ClaimPendingAsync(DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
            return [];

        var db = await connection.GetDatabaseAsync(cancellationToken);
        var result = await Claim.EvaluateAsync(db, [keys.OutboxPending], [keys.OutboxRowPrefix, RedisTimestamps.ToMicroseconds(olderThan), batchSize]);

        return (result.AsArray()).Select(ToMessage).ToList();
    }

    private static SagaOutboxMessage ToMessage(RedisResult row)
    {
        var flat = row.AsArray();
        var fields = new Dictionary<string, RedisValue>(flat.Length / 2, StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Length; i += 2)
            fields[flat[i].ToString()] = (RedisValue)flat[i + 1];

        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(fields[RedisOutboxFields.HeadersJson].ToString()) ?? [];

        return new SagaOutboxMessage(
            (long)fields[RedisOutboxFields.Id],
            Guid.Parse(fields[RedisOutboxFields.CorrelationId].ToString()),
            fields[RedisOutboxFields.SagaType].ToString(),
            fields[RedisOutboxFields.MessageId].ToString(),
            fields[RedisOutboxFields.MessageTypeName].ToString(),
            (byte[]?)fields[RedisOutboxFields.Body] ?? [],
            fields.TryGetValue(RedisOutboxFields.Destination, out var destination) ? (string?)destination : null,
            new Dictionary<string, string>(headers, StringComparer.Ordinal),
            SagaOutboxStatus.Dispatched,
            RedisTimestamps.FromMicroseconds((long)fields[RedisOutboxFields.CreatedMicroseconds]));
    }
}
