using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace VSaga.Persistence.EFCore.Tests.Conformance;

/// <summary>
/// What the cross-provider concurrent-claim suite cannot show about SQL Server's claim. That suite proves
/// safety — every row claimed exactly once — and an <c>UPDATE</c> takes update locks on the rows it reads, so
/// it holds without any table hint. <c>READPAST</c> is the liveness half: a dispatcher skips a row another
/// transaction holds instead of waiting behind it. These cases leave a claim of one row open on a second connection and need
/// the claim to return the others without blocking, which fails if the hint is removed.
/// </summary>
[Collection(SqlServerConformanceGroup.Name)]
public sealed class SqlServerClaimTests(SqlServerProviderFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Long enough for a loaded CI machine to claim two rows, short enough that a claim blocked behind the
    // held lock fails the case quickly: it would otherwise wait on the lock until the case timed out.
    private static readonly TimeSpan ClaimDeadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ClaimDue_SkipsARowAnotherTransactionHoldsInsteadOfWaitingBehindIt()
    {
        var database = await fixture.CreateMigratedDatabaseAsync();
        await using var _ = new AsyncDisposer(database.Release);

        await using (var seed = new VSagaDbContext(database.Options))
        {
            var timeouts = new EfCoreSagaTimeoutStore(seed);
            for (var i = 0; i < 3; i++)
                await timeouts.ScheduleAsync("OrderSaga", Guid.NewGuid(), "AwaitingPayment", T0.AddSeconds(i));
        }

        await using var holder = await HoldAClaimOfTheFirstRowAsync(database.ConnectionString, "SagaTimeouts", "DueAtUtc");

        await using var db = new VSagaDbContext(database.Options);
        using var deadline = new CancellationTokenSource(ClaimDeadline);
        var claimed = await new EfCoreSagaTimeoutStore(db).ClaimDueAsync(T0.AddMinutes(1), batchSize: 10, cancellationToken: deadline.Token);

        Assert.Equal(2, claimed.Count);
        Assert.All(claimed, t => Assert.NotEqual(T0, t.DueAtUtc));

        await holder.ReleaseAsync();
        var afterwards = await new EfCoreSagaTimeoutStore(db).ClaimDueAsync(T0.AddMinutes(1), batchSize: 10);
        Assert.Equal(T0, Assert.Single(afterwards).DueAtUtc);
    }

    [Fact]
    public async Task ClaimPending_SkipsARowAnotherTransactionHoldsInsteadOfWaitingBehindIt()
    {
        var database = await fixture.CreateMigratedDatabaseAsync();
        await using var _ = new AsyncDisposer(database.Release);

        await using (var seed = new VSagaDbContext(database.Options))
        {
            var outbox = new EfCoreSagaOutboxStore(seed);
            for (var i = 0; i < 3; i++)
                await outbox.EnqueueAsync("OrderSaga", Guid.NewGuid(), $"m{i}", "InventoryReserved", "{}"u8.ToArray(), null, new Dictionary<string, string>(StringComparer.Ordinal), T0.AddSeconds(i));
            await seed.SaveChangesAsync();
        }

        await using var holder = await HoldAClaimOfTheFirstRowAsync(database.ConnectionString, "SagaOutboxMessages", "CreatedAtUtc");

        await using var db = new VSagaDbContext(database.Options);
        using var deadline = new CancellationTokenSource(ClaimDeadline);
        var claimed = await new EfCoreSagaOutboxStore(db).ClaimPendingAsync(T0.AddMinutes(1), batchSize: 10, deadline.Token);

        Assert.Equal(["m1", "m2"], claimed.Select(m => m.MessageId).Order(StringComparer.Ordinal), StringComparer.Ordinal);

        await holder.ReleaseAsync();
        var afterwards = await new EfCoreSagaOutboxStore(db).ClaimPendingAsync(T0.AddMinutes(1), batchSize: 10);
        Assert.Equal("m0", Assert.Single(afterwards).MessageId, StringComparer.Ordinal);
    }

    /// <summary>
    /// Starts a claim of one row on its own connection and leaves its transaction open, as a second dispatcher
    /// mid-claim would be. It has to be a claim and not a bare lock on the row: <c>READPAST</c> skips what the
    /// index it reads is locked on, and only another claim holds its locks where this one looks for them.
    /// </summary>
    private static async Task<RowLock> HoldAClaimOfTheFirstRowAsync(string connectionString, string table, string orderColumn)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = new SqlCommand(
            $"WITH held AS (SELECT TOP (1) * FROM [{table}] WITH (ROWLOCK, UPDLOCK, READPAST) WHERE [Status] = 0 ORDER BY [{orderColumn}], [Id]) UPDATE held SET [Status] = 1",
            connection, transaction);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        return new RowLock(connection, transaction);
    }

    private sealed class RowLock(SqlConnection connection, SqlTransaction transaction) : IAsyncDisposable
    {
        private bool _released;

        public async Task ReleaseAsync()
        {
            if (_released)
                return;

            _released = true;
            await transaction.RollbackAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class AsyncDisposer(Func<ValueTask> release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => release();
    }
}
