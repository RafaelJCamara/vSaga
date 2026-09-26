using VSaga.Abstractions.Persistence;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// The Scoped staging buffer for outbox rows: one per DI scope, which <c>VSaga.Core</c> opens fresh per
/// message, timeout or retry. It holds staged rows and nothing else -- no session, no collection handle --
/// exactly as the EF Core provider's change tracker holds Added rows until a <c>SaveChangesAsync</c>. No
/// <c>IClientSessionHandle</c> lives here: a session held across a message would stay open across the
/// step's own I/O, and the redelivery net needs the event log's appends outside it anyway.
/// </summary>
/// <remarks>
/// The snapshot store is the sole committer: <c>InsertAsync</c>/<c>UpdateAsync</c> read what is staged
/// here, write it in one transaction with the snapshot, and remove exactly what they wrote -- peek,
/// commit, clear, never take-then-write. A persist that throws leaves the rows staged, so a later
/// <c>DiscardPendingAsync</c> finds them and a later persist in the same scope (the dead-letter path's
/// <c>Failed</c> snapshot) commits them atomically with it; a scope that ends with rows still staged drops
/// them with itself, so nothing here ever becomes durable without a committed snapshot.
/// </remarks>
public sealed class MongoSagaUnitOfWork
{
    private readonly List<MongoStagedOutboxRow> _staged = [];

    internal IReadOnlyList<MongoStagedOutboxRow> Staged => _staged;

    internal void Stage(MongoStagedOutboxRow row) => _staged.Add(row);

    /// <summary>Drops the staged rows with these message ids. Rows an earlier commit already wrote are out of reach here, as they should be.</summary>
    internal void Discard(IReadOnlyCollection<string> messageIds)
    {
        if (messageIds.Count == 0)
            return;

        var ids = messageIds as HashSet<string> ?? new HashSet<string>(messageIds, StringComparer.Ordinal);
        _staged.RemoveAll(row => ids.Contains(row.MessageId));
    }

    /// <summary>Removes exactly the rows a commit wrote -- by reference, so a row staged during the write stays staged.</summary>
    internal void Committed(IReadOnlyList<MongoStagedOutboxRow> rows)
    {
        foreach (var row in rows)
            _staged.Remove(row);
    }

    /// <summary>Drops everything staged, as ending the scope without a persist would.</summary>
    internal void Clear() => _staged.Clear();
}

/// <summary>One outbox row as staged: its headers already serialised, so a later mutation of the caller's dictionary reaches nothing.</summary>
internal sealed record MongoStagedOutboxRow(
    string MessageId,
    Guid CorrelationId,
    string SagaType,
    string MessageTypeName,
    byte[] Body,
    string? Destination,
    string HeadersJson,
    DateTimeOffset CreatedAtUtc)
{
    public SagaOutboxDocument ToDocument(long id) => new()
    {
        MessageId = MessageId,
        Id = id,
        CorrelationId = MongoIds.CorrelationText(CorrelationId),
        SagaType = SagaType,
        MessageTypeName = MessageTypeName,
        Body = Body,
        Destination = Destination,
        HeadersJson = HeadersJson,
        Status = SagaOutboxStatus.Pending,
        CreatedAt = MongoTimestamps.ToDate(CreatedAtUtc),
        CreatedAtTicks = MongoTimestamps.ToTicks(CreatedAtUtc),
    };
}
