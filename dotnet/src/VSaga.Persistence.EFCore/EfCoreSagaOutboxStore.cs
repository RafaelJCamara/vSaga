using System.Text.Json;
using VSaga.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace VSaga.Persistence.EFCore;

public sealed class EfCoreSagaOutboxStore(VSagaDbContext db) : ISagaOutboxStore
{
    /// <remarks>
    /// Adds to the change tracker without saving, per <see cref="ISagaOutboxStore.EnqueueAsync"/>'s
    /// contract: the next commit in this same shared <see cref="VSagaDbContext"/> — nominally the
    /// caller's own <c>PersistAsync</c>, atomically with the snapshot this row belongs to — is what
    /// makes it durable.
    /// </remarks>
    public Task EnqueueAsync(string sagaType, Guid correlationId, string messageId, string messageTypeName,
        ReadOnlyMemory<byte> body, string? destination, IReadOnlyDictionary<string, string> headers,
        DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
    {
        db.SagaOutboxMessages.Add(new SagaOutboxMessageEntity
        {
            CorrelationId = correlationId,
            SagaType = sagaType,
            MessageId = messageId,
            MessageTypeName = messageTypeName,
            Body = body.ToArray(),
            Destination = destination,
            HeadersJson = JsonSerializer.Serialize(headers),
            Status = SagaOutboxStatus.Pending,
            CreatedAtUtc = createdAtUtc,
        });

        return Task.CompletedTask;
    }

    public async Task MarkDispatchedAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var message = await db.SagaOutboxMessages.FirstOrDefaultAsync(x => x.MessageId == messageId, cancellationToken);
        if (message is null)
            return;

        message.Status = SagaOutboxStatus.Dispatched;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <remarks>
    /// Detaches rather than deletes: these rows were only ever staged in the change tracker by
    /// <see cref="EnqueueAsync"/> and never committed, so there is nothing in the database to remove —
    /// the entire point is to drop them before something else's <c>SaveChangesAsync</c> flushes them.
    /// Matching on the change tracker directly (not a query) for the same reason.
    /// </remarks>
    public Task DiscardPendingAsync(IReadOnlyCollection<string> messageIds, CancellationToken cancellationToken = default)
    {
        if (messageIds.Count == 0)
            return Task.CompletedTask;

        var ids = messageIds as HashSet<string> ?? new HashSet<string>(messageIds, StringComparer.Ordinal);

        var staged = db.ChangeTracker.Entries<SagaOutboxMessageEntity>()
            .Where(e => e.State == EntityState.Added && ids.Contains(e.Entity.MessageId))
            .ToList();

        foreach (var entry in staged)
            entry.State = EntityState.Detached;

        return Task.CompletedTask;
    }

    /// <remarks>
    /// On Postgres, claims via an atomic <c>UPDATE ... RETURNING</c> guarded by
    /// <c>FOR UPDATE SKIP LOCKED</c>; on SQL Server via an <c>UPDATE ... OUTPUT</c> over a <c>READPAST</c>
    /// read (ADR 0009). Both are safe for multiple concurrent VSaga.Dashboard.Api/worker instances racing on
    /// the same stale rows -- same shape as <see cref="EfCoreSagaTimeoutStore.ClaimDueAsync"/>. Any other
    /// provider (e.g. SQLite in tests) falls back to a plain load-then-update, which is only correct for a
    /// single dispatcher instance.
    /// </remarks>
    public Task<IReadOnlyList<SagaOutboxMessage>> ClaimPendingAsync(DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken = default)
    {
        var provider = db.Database.ProviderName;
        if (EfCoreProviderNames.IsNpgsql(provider))
            return ClaimPendingViaSkipLockedAsync(olderThan, batchSize, cancellationToken);

        return EfCoreProviderNames.IsSqlServer(provider)
            ? ClaimPendingViaReadPastAsync(olderThan, batchSize, cancellationToken)
            : ClaimPendingViaLoadAndUpdateAsync(olderThan, batchSize, cancellationToken);
    }

    /// <summary>
    /// Not safe for multiple concurrent dispatcher instances -- two dispatchers can both load the same
    /// stale row before either marks it Dispatched. Only reached for providers with no atomic claim of
    /// their own (neither Postgres nor SQL Server), where a portable one isn't available.
    /// </summary>
    private async Task<IReadOnlyList<SagaOutboxMessage>> ClaimPendingViaLoadAndUpdateAsync(DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        var stale = await db.SagaOutboxMessages
            .Where(x => x.Status == SagaOutboxStatus.Pending && x.CreatedAtUtc <= olderThan)
            .OrderBy(x => x.CreatedAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
            return [];

        foreach (var message in stale)
            message.Status = SagaOutboxStatus.Dispatched;

        await db.SaveChangesAsync(cancellationToken);

        return stale.Select(ToOutboxMessage).ToList();
    }

    /// <summary>
    /// A single statement claims and returns the stale rows atomically, so two dispatchers racing on the
    /// same row can never both claim it. <c>RETURNING</c> doesn't guarantee row order, so the caller's
    /// created-at ordering is reapplied in memory after materializing; no further LINQ can be composed
    /// onto the raw SQL itself (EF can't wrap a bare UPDATE...RETURNING in a subquery).
    /// </summary>
    private async Task<IReadOnlyList<SagaOutboxMessage>> ClaimPendingViaSkipLockedAsync(DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        var olderThanUtc = olderThan.UtcDateTime;
        var pending = (int)SagaOutboxStatus.Pending;
        var dispatched = (int)SagaOutboxStatus.Dispatched;

        var claimed = await db.SagaOutboxMessages.FromSqlInterpolated($"""
            UPDATE "SagaOutboxMessages" SET "Status" = {dispatched}
            WHERE "Id" IN (
                SELECT "Id" FROM "SagaOutboxMessages"
                WHERE "Status" = {pending} AND "CreatedAtUtc" <= {olderThanUtc}
                ORDER BY "CreatedAtUtc"
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            )
            RETURNING "Id", "CorrelationId", "SagaType", "MessageId", "MessageTypeName", "Body", "Destination", "HeadersJson", "Status", "CreatedAtUtc"
            """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return claimed
            .OrderBy(x => x.CreatedAtUtc)
            .Select(ToOutboxMessage)
            .ToList();
    }

    /// <summary>
    /// SQL Server's counterpart of <see cref="ClaimPendingViaSkipLockedAsync"/>: the stale rows are read through a
    /// CTE with <c>UPDLOCK, READPAST</c>, so a second dispatcher skips what the first has locked, and the same
    /// statement marks them Dispatched and returns them with <c>OUTPUT</c>. <c>OUTPUT</c> does not guarantee row
    /// order, so the created-at ordering is reapplied in memory.
    /// </summary>
    private async Task<IReadOnlyList<SagaOutboxMessage>> ClaimPendingViaReadPastAsync(DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        var olderThanUtc = olderThan.UtcDateTime;
        var pending = (int)SagaOutboxStatus.Pending;
        var dispatched = (int)SagaOutboxStatus.Dispatched;

        var claimed = await db.SagaOutboxMessages.FromSqlInterpolated($"""
            WITH stale AS (
                SELECT TOP ({batchSize}) * FROM [SagaOutboxMessages] WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE [Status] = {pending} AND [CreatedAtUtc] <= {olderThanUtc}
                ORDER BY [CreatedAtUtc], [Id]
            )
            UPDATE stale SET [Status] = {dispatched}
            OUTPUT inserted.[Id], inserted.[CorrelationId], inserted.[SagaType], inserted.[MessageId], inserted.[MessageTypeName], inserted.[Body], inserted.[Destination], inserted.[HeadersJson], inserted.[Status], inserted.[CreatedAtUtc]
            """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return claimed
            .OrderBy(x => x.CreatedAtUtc)
            .Select(ToOutboxMessage)
            .ToList();
    }

    private static SagaOutboxMessage ToOutboxMessage(SagaOutboxMessageEntity x) =>
        new(x.Id, x.CorrelationId, x.SagaType, x.MessageId, x.MessageTypeName, x.Body, x.Destination,
            JsonSerializer.Deserialize<Dictionary<string, string>>(x.HeadersJson) ?? new Dictionary<string, string>(StringComparer.Ordinal),
            x.Status, x.CreatedAtUtc);
}
