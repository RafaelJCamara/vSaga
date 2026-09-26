using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>
/// The Scoped staging buffer for outbox rows: one per DI scope, which <c>VSaga.Core</c> opens fresh per
/// message, timeout or retry. It holds staged rows and nothing else -- no connection, no script handle
/// -- exactly as the EF Core provider's change tracker holds Added rows until a <c>SaveChangesAsync</c>.
/// </summary>
/// <remarks>
/// The persist script is the sole committer: <c>RedisSagaSnapshotStore.InsertAsync</c>/<c>UpdateAsync</c>
/// read what is staged here, write it atomically with the snapshot, and remove exactly what they wrote --
/// peek, commit, clear, never take-then-write. A persist that throws leaves the rows staged; a scope
/// that ends with rows still staged drops them with itself, so nothing here ever becomes durable
/// without a committed snapshot (<c>ISagaOutboxStore.EnqueueAsync</c>'s staged-row lifecycle).
/// </remarks>
public sealed class RedisSagaUnitOfWork
{
    private readonly List<RedisStagedOutboxRow> _staged = [];

    internal IReadOnlyList<RedisStagedOutboxRow> Staged => _staged;

    internal void Stage(RedisStagedOutboxRow row) => _staged.Add(row);

    /// <summary>Drops the staged rows with these message ids. Rows an earlier commit already wrote are out of reach here, as they should be.</summary>
    internal void Discard(IReadOnlyCollection<string> messageIds)
    {
        if (messageIds.Count == 0)
            return;

        var ids = messageIds as HashSet<string> ?? new HashSet<string>(messageIds, StringComparer.Ordinal);
        _staged.RemoveAll(row => ids.Contains(row.MessageId));
    }

    /// <summary>Removes exactly the rows a persist script wrote -- by reference, so a row staged during the write stays staged.</summary>
    internal void Committed(IReadOnlyList<RedisStagedOutboxRow> rows)
    {
        foreach (var row in rows)
            _staged.Remove(row);
    }

    /// <summary>Drops everything staged, as ending the scope without a persist would.</summary>
    internal void Clear() => _staged.Clear();
}

/// <summary>One outbox row as staged: its headers already serialised, so a later mutation of the caller's dictionary reaches nothing.</summary>
internal sealed record RedisStagedOutboxRow(
    string MessageId,
    Guid CorrelationId,
    string SagaType,
    string MessageTypeName,
    byte[] Body,
    string? Destination,
    string HeadersJson,
    long CreatedMicroseconds)
{
    /// <summary>The row hash's fields, <c>destination</c> omitted when null so it reads back as a missing field -- which is what the dispatcher branches on.</summary>
    public RedisValue[] ToHashArguments()
    {
        var values = new List<RedisValue>(16)
        {
            RedisOutboxFields.CorrelationId, CorrelationId.ToString("D"),
            RedisOutboxFields.SagaType, SagaType,
            RedisOutboxFields.MessageId, MessageId,
            RedisOutboxFields.MessageTypeName, MessageTypeName,
            RedisOutboxFields.Body, Body,
            RedisOutboxFields.HeadersJson, HeadersJson,
            RedisOutboxFields.Status, 0,
            RedisOutboxFields.CreatedMicroseconds, CreatedMicroseconds,
        };

        if (Destination is not null)
        {
            values.Add(RedisOutboxFields.Destination);
            values.Add(Destination);
        }

        return values.ToArray();
    }
}

/// <summary>Field names of an outbox row hash, shared by the staging side and the claim side.</summary>
internal static class RedisOutboxFields
{
    public const string Id = "id";
    public const string CorrelationId = "correlationId";
    public const string SagaType = "sagaType";
    public const string MessageId = "messageId";
    public const string MessageTypeName = "messageTypeName";
    public const string Body = "body";
    public const string Destination = "destination";
    public const string HeadersJson = "headersJson";
    public const string Status = "status";
    public const string CreatedMicroseconds = "createdMicros";
}
