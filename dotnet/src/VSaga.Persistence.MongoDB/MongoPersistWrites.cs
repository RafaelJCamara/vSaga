using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// The commit: a snapshot write and whatever the unit of work has staged, together. When nothing is
/// staged -- the common case, a saga that deferred no publish -- the snapshot write runs bare, with no
/// session, costing nothing extra. When rows are staged, one short multi-document transaction inserts
/// them and runs the snapshot write, and only a committed transaction clears them from the buffer.
/// </summary>
/// <remarks>
/// <para>
/// Opened at the moment EF Core would call <c>SaveChangesAsync</c>, never held across a message: the
/// staging window is tight (every staging site is immediately followed by a persist) and no session ever
/// spans user step I/O, the 60 s transaction lifetime, or the 5 ms lock-request timeout that would turn
/// same-instance contention into an abort of the whole message.
/// </para>
/// <para>
/// The callback API, not <c>StartTransaction</c>/<c>CommitTransaction</c>: it runs the
/// <c>TransientTransactionError</c> and <c>UnknownTransactionCommitResult</c> retry loops the core API
/// makes the caller write. The consequence is that the callback may run more than once, so nothing that
/// must happen once -- the live object's version bump, the serialisation, the row-id allocation -- happens
/// inside it. Row ids are allocated before the transaction; ids skipped by an abort are harmless.
/// </para>
/// </remarks>
public sealed class MongoPersistWrites(MongoCollections collections)
{
    private static readonly TransactionOptions Transaction = new(ReadConcern.Local, ReadPreference.Primary, WriteConcern.WMajority);

    /// <summary>
    /// Runs <paramref name="snapshotWrite"/> with the staged rows: bare when nothing is staged, else inside
    /// one transaction with their insert. The session handed to the write is null on the bare path. The
    /// write returns whether it applied; a write that matched nothing (a version guard that failed) aborts
    /// the transaction from inside the callback, so the staged rows are neither written nor cleared, and
    /// the caller decides what the miss means. Returns what the write returned.
    /// </summary>
    internal async Task<bool> PersistAsync(MongoSagaUnitOfWork? unitOfWork, Func<IClientSessionHandle?, CancellationToken, Task<bool>> snapshotWrite, CancellationToken cancellationToken)
    {
        var staged = unitOfWork is { Staged.Count: > 0 } ? unitOfWork.Staged.ToList() : null;
        if (staged is null)
            return await snapshotWrite(null, cancellationToken);

        var lastId = await MongoCounters.IncrementAsync(collections.Counters, MongoCollections.OutboxName, staged.Count, cancellationToken);
        var documents = staged.Select((row, i) => row.ToDocument(lastId - staged.Count + 1 + i)).ToList();

        using var session = await collections.Connection.Client.StartSessionAsync(cancellationToken: cancellationToken);
        var applied = await session.WithTransactionAsync(async (s, ct) =>
        {
            await collections.Outbox.InsertManyAsync(s, documents, cancellationToken: ct);
            var written = await snapshotWrite(s, ct);
            if (!written)
                await s.AbortTransactionAsync(ct);
            return written;
        }, Transaction, cancellationToken);

        if (applied)
            unitOfWork!.Committed(staged);

        return applied;
    }

    /// <summary>
    /// Commits the staged rows alone, with no snapshot write. The engine never does this -- the persist is
    /// the sole committer -- but the conformance fixture needs a way to make a staged row durable without
    /// borrowing a store's commit. Internal for that reason.
    /// </summary>
    internal Task CommitStagedAsync(MongoSagaUnitOfWork unitOfWork, CancellationToken cancellationToken) =>
        PersistAsync(unitOfWork, static (_, _) => Task.FromResult(true), cancellationToken);
}
