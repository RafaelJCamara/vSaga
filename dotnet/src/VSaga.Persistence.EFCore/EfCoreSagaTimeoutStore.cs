using System.Text.Json;
using VSaga.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace VSaga.Persistence.EFCore;

public sealed class EfCoreSagaTimeoutStore(VSagaDbContext db) : ISagaTimeoutStore
{
    public Task ScheduleAsync(string sagaType, Guid correlationId, string forState, DateTimeOffset dueAtUtc, CancellationToken cancellationToken = default)
    {
        db.SagaTimeouts.Add(new SagaTimeoutEntity
        {
            CorrelationId = correlationId,
            SagaType = sagaType,
            ForState = forState,
            DueAtUtc = dueAtUtc,
            Status = SagaTimeoutStatus.Pending,
        });

        return db.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelAsync(string sagaType, Guid correlationId, string forState, CancellationToken cancellationToken = default)
    {
        // SagaType is part of the filter, not incidental: state names are only unique within a saga
        // type, so without it one saga would cancel another's timeout for a same-named state.
        var pending = await db.SagaTimeouts
            .Where(x => x.SagaType == sagaType && x.CorrelationId == correlationId && x.ForState == forState && x.Status == SagaTimeoutStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var timeout in pending)
            timeout.Status = SagaTimeoutStatus.Cancelled;

        if (pending.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

    /// <remarks>
    /// On Postgres, claims via an atomic <c>UPDATE ... RETURNING</c> guarded by
    /// <c>FOR UPDATE SKIP LOCKED</c>; on SQL Server via an <c>UPDATE ... OUTPUT</c> over a <c>READPAST</c>
    /// read (ADR 0009). Both are safe for multiple concurrent VSaga.Dashboard.Api/worker instances racing on
    /// the same due rows. Any other provider (e.g. SQLite in tests) falls back to a plain load-then-update,
    /// which is only correct for a single dispatcher instance — that fallback exists for provider
    /// portability, not as a v1 shortcut.
    /// </remarks>
    public Task<IReadOnlyList<SagaTimeout>> ClaimDueAsync(DateTimeOffset asOf, int batchSize, IReadOnlyCollection<string>? sagaTypes = null, CancellationToken cancellationToken = default)
    {
        // Null claims every type; a set claims only its members (ISagaTimeoutStore.ClaimDueAsync), so a
        // process hosting some of the saga types sharing this database never fires the others' rows.
        // Materialised once here: both paths hand it to the database as a parameter.
        var wanted = sagaTypes?.ToArray();

        var provider = db.Database.ProviderName;
        if (EfCoreProviderNames.IsNpgsql(provider))
            return ClaimDueViaSkipLockedAsync(asOf, batchSize, wanted, cancellationToken);

        return EfCoreProviderNames.IsSqlServer(provider)
            ? ClaimDueViaReadPastAsync(asOf, batchSize, wanted, cancellationToken)
            : ClaimDueViaLoadAndUpdateAsync(asOf, batchSize, wanted, cancellationToken);
    }

    /// <summary>
    /// Not safe for multiple concurrent dispatcher instances — two dispatchers can both load the same
    /// due row before either marks it Fired. Only reached for providers with no atomic claim of their own
    /// (neither Postgres nor SQL Server), where a portable one isn't available.
    /// </summary>
    private async Task<IReadOnlyList<SagaTimeout>> ClaimDueViaLoadAndUpdateAsync(DateTimeOffset asOf, int batchSize, string[]? sagaTypes, CancellationToken cancellationToken)
    {
        var query = db.SagaTimeouts.Where(x => x.Status == SagaTimeoutStatus.Pending && x.DueAtUtc <= asOf);
        if (sagaTypes is not null)
            query = query.Where(x => sagaTypes.Contains(x.SagaType));

        var due = await query
            .OrderBy(x => x.DueAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
            return [];

        foreach (var timeout in due)
            timeout.Status = SagaTimeoutStatus.Fired;

        await db.SaveChangesAsync(cancellationToken);

        return due.Select(x => new SagaTimeout(x.Id, x.CorrelationId, x.SagaType, x.ForState, x.DueAtUtc, x.Status)).ToList();
    }

    /// <summary>
    /// A single statement claims and returns the due rows atomically, so two dispatchers racing on the
    /// same due row can never both claim it. <c>RETURNING</c> doesn't guarantee row order, so the
    /// caller's due-date ordering is reapplied in memory after materializing; no further LINQ can be
    /// composed onto the raw SQL itself (EF can't wrap a bare UPDATE...RETURNING in a subquery).
    /// </summary>
    private async Task<IReadOnlyList<SagaTimeout>> ClaimDueViaSkipLockedAsync(DateTimeOffset asOf, int batchSize, string[]? sagaTypes, CancellationToken cancellationToken)
    {
        var asOfUtc = asOf.UtcDateTime;
        var pending = (int)SagaTimeoutStatus.Pending;
        var fired = (int)SagaTimeoutStatus.Fired;

        // Two statements rather than one with a nullable array parameter: Npgsql cannot infer a type
        // for a null array in "{p} IS NULL OR ...", and the filter belongs inside the locking subquery
        // so that SKIP LOCKED only ever locks rows this claim will take. A string[] binds as text[].
        FormattableString sql = sagaTypes is null
            ? (FormattableString)$"""
                UPDATE "SagaTimeouts" SET "Status" = {fired}
                WHERE "Id" IN (
                    SELECT "Id" FROM "SagaTimeouts"
                    WHERE "Status" = {pending} AND "DueAtUtc" <= {asOfUtc}
                    ORDER BY "DueAtUtc"
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING "Id", "CorrelationId", "SagaType", "ForState", "DueAtUtc", "Status"
                """
            : (FormattableString)$"""
                UPDATE "SagaTimeouts" SET "Status" = {fired}
                WHERE "Id" IN (
                    SELECT "Id" FROM "SagaTimeouts"
                    WHERE "Status" = {pending} AND "DueAtUtc" <= {asOfUtc} AND "SagaType" = ANY({sagaTypes})
                    ORDER BY "DueAtUtc"
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING "Id", "CorrelationId", "SagaType", "ForState", "DueAtUtc", "Status"
                """;

        var claimed = await db.SagaTimeouts.FromSqlInterpolated(sql)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return claimed
            .OrderBy(x => x.DueAtUtc)
            .Select(x => new SagaTimeout(x.Id, x.CorrelationId, x.SagaType, x.ForState, x.DueAtUtc, x.Status))
            .ToList();
    }

    /// <summary>
    /// SQL Server's counterpart of <see cref="ClaimDueViaSkipLockedAsync"/>: the due rows are read through a
    /// CTE with <c>UPDLOCK, READPAST</c>, so a second dispatcher skips the rows the first has locked instead of
    /// waiting on or re-taking them, and the same statement marks them Fired and returns them with
    /// <c>OUTPUT</c>. <c>TOP</c> cannot be ordered inside an <c>UPDATE</c> itself, which is why the CTE exists.
    /// <c>OUTPUT</c> does not guarantee row order, so the due-date ordering is reapplied in memory.
    /// </summary>
    private async Task<IReadOnlyList<SagaTimeout>> ClaimDueViaReadPastAsync(DateTimeOffset asOf, int batchSize, string[]? sagaTypes, CancellationToken cancellationToken)
    {
        var asOfUtc = asOf.UtcDateTime;
        var pending = (int)SagaTimeoutStatus.Pending;
        var fired = (int)SagaTimeoutStatus.Fired;

        // The set binds as one JSON array read by OPENJSON, SQL Server's counterpart of Postgres's = ANY(array),
        // so the statement text does not change with the number of saga types. Two statements, as on Postgres,
        // so the filter sits inside the locking read and READPAST only ever skips rows this claim would take.
        // The names match exactly (ISagaTimeoutStore.ClaimDueAsync): a binary collation, because the database's
        // is case-insensitive by default, and a length check, because SQL Server's = ignores trailing spaces.
        FormattableString sql = sagaTypes is null
            ? (FormattableString)$"""
                WITH due AS (
                    SELECT TOP ({batchSize}) * FROM [SagaTimeouts] WITH (ROWLOCK, UPDLOCK, READPAST)
                    WHERE [Status] = {pending} AND [DueAtUtc] <= {asOfUtc}
                    ORDER BY [DueAtUtc], [Id]
                )
                UPDATE due SET [Status] = {fired}
                OUTPUT inserted.[Id], inserted.[CorrelationId], inserted.[SagaType], inserted.[ForState], inserted.[DueAtUtc], inserted.[Status]
                """
            : (FormattableString)$"""
                WITH due AS (
                    SELECT TOP ({batchSize}) * FROM [SagaTimeouts] WITH (ROWLOCK, UPDLOCK, READPAST)
                    WHERE [Status] = {pending} AND [DueAtUtc] <= {asOfUtc}
                        AND EXISTS (
                            SELECT 1 FROM OPENJSON({JsonSerializer.Serialize(sagaTypes)}) AS wanted
                            WHERE wanted.[value] COLLATE Latin1_General_100_BIN2 = [SagaType] COLLATE Latin1_General_100_BIN2
                                AND DATALENGTH(wanted.[value]) = DATALENGTH([SagaType]))
                    ORDER BY [DueAtUtc], [Id]
                )
                UPDATE due SET [Status] = {fired}
                OUTPUT inserted.[Id], inserted.[CorrelationId], inserted.[SagaType], inserted.[ForState], inserted.[DueAtUtc], inserted.[Status]
                """;

        var claimed = await db.SagaTimeouts.FromSqlInterpolated(sql)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return claimed
            .OrderBy(x => x.DueAtUtc)
            .Select(x => new SagaTimeout(x.Id, x.CorrelationId, x.SagaType, x.ForState, x.DueAtUtc, x.Status))
            .ToList();
    }
}
