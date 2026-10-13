using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace VSaga.Persistence.EFCore.Tests;

/// <summary>
/// Re-runs the provider-sensitive parts of the store suite against a real Postgres via
/// Testcontainers, not just SQLite. This is deliberately not a full duplicate of
/// <see cref="EfCoreStoreTests"/> — SQLite already covers general CRUD/concurrency logic; this class
/// exists specifically because SQLite's EF Core provider is more lenient than Npgsql about certain
/// LINQ shapes (GetSagaTypesAsync's Distinct-over-a-record-projection query translated fine on SQLite
/// but threw on Npgsql in production — see EfCoreSagaSummaryReader.GetSagaTypesAsync for the fix).
/// </summary>
public sealed class PostgresEfCoreStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private DbContextOptions<VSagaDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _options = new DbContextOptionsBuilder<VSagaDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly("VSaga.Persistence.EFCore.Postgres"))
            .Options;

        await using var db = new VSagaDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private VSagaDbContext NewContext() => new(_options);

    /// <summary>
    /// The migrations leave an index on (SagaType, UpdatedAtUtc), which serves a list of one saga type in its
    /// UpdatedAt order at any depth. Without it Postgres walks the UpdatedAtUtc index in time order and discards
    /// every row of another type, and an offset page does so for every row before it. Measured on 400,400 rows
    /// in random physical order: the hundredth page of 25 for one type read 42,860 buffers and took 55 ms, and
    /// with the index 2,533 buffers and 4 ms. The planner's own choice depends on the table's size and cache
    /// settings, so this pins the index, not a plan.
    /// </summary>
    [Fact]
    public async Task TheMigrations_LeaveAnIndexOnSagaTypeThenUpdatedAtUtc_ForPerTypeListings()
    {
        await using var db = NewContext();

        var definitions = await db.Database
            .SqlQueryRaw<string>("""SELECT indexdef AS "Value" FROM pg_indexes WHERE tablename = 'SagaInstances' AND indexname = 'IX_SagaInstances_SagaType_UpdatedAtUtc' """)
            .ToListAsync();

        var definition = Assert.Single(definitions);
        Assert.Contains("(\"SagaType\", \"UpdatedAtUtc\")", definition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSagaTypesAsync_TranslatesAndReturnsDistinctTypes()
    {
        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", Kind = SagaKind.Orchestrated, CurrentState = "A", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", Kind = SagaKind.Orchestrated, CurrentState = "B", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "ShippingSaga", Kind = SagaKind.Choreographed, CurrentState = "A", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        }

        await using var db2 = NewContext();
        var types = await new EfCoreSagaSummaryReader(db2).GetSagaTypesAsync();

        Assert.Equal(2, types.Count);
        Assert.Contains(types, t => string.Equals(t.SagaType, "OrderSaga", StringComparison.Ordinal) && t.Kind == SagaKind.Orchestrated);
        Assert.Contains(types, t => string.Equals(t.SagaType, "ShippingSaga", StringComparison.Ordinal) && t.Kind == SagaKind.Choreographed);
    }

    [Fact]
    public async Task InsertUpdateAndList_RoundTripAgainstRealPostgres()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
            {
                CorrelationId = correlationId,
                SagaType = "OrderSaga",
                CurrentState = "Submitted",
                Status = SagaStatus.Running,
                OrderId = "ORD-1",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        TestState state;
        await using (var db2 = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db2);
            state = (await store.FindAsync("OrderSaga", correlationId))!;
            state.CurrentState = "Completed";
            state.Status = SagaStatus.Completed;
            await store.UpdateAsync(state, expectedVersion: 0);
        }

        await using var db3 = NewContext();
        var reader = new EfCoreSagaSummaryReader(db3);
        var summary = await reader.GetAsync("OrderSaga", correlationId);

        Assert.NotNull(summary);
        Assert.Equal("Completed", summary.CurrentState);
        Assert.Equal(SagaStatus.Completed, summary.Status);
        Assert.Equal(1, summary.Version);

        var page = await reader.ListAsync(new SagaListFilter { Status = SagaStatus.Completed });
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ListAsync_SortsByStatusAcrossTheWholeResultSet_AgainstRealPostgres()
    {
        var sagaType = $"SortSaga-{Guid.NewGuid():N}";
        var running = Guid.NewGuid();
        var completed = Guid.NewGuid();
        var failed = Guid.NewGuid();

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            // Inserted out of domain order deliberately, so a passing test can't be explained by insertion order.
            await store.InsertAsync(new TestState { CorrelationId = failed, SagaType = sagaType, CurrentState = "X", Status = SagaStatus.Failed, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await store.InsertAsync(new TestState { CorrelationId = running, SagaType = sagaType, CurrentState = "X", Status = SagaStatus.Running, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await store.InsertAsync(new TestState { CorrelationId = completed, SagaType = sagaType, CurrentState = "X", Status = SagaStatus.Completed, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        }

        await using var db2 = NewContext();
        var reader = new EfCoreSagaSummaryReader(db2);

        // Split across two pages of the same sort to prove the ORDER BY is translated and applied to the
        // whole query (before Skip/Take), not just re-sorting whatever page happened to be requested.
        var page1 = await reader.ListAsync(new SagaListFilter { SagaType = sagaType, SortBy = SagaSortColumn.Status, Page = 1, PageSize = 2 });
        var page2 = await reader.ListAsync(new SagaListFilter { SagaType = sagaType, SortBy = SagaSortColumn.Status, Page = 2, PageSize = 2 });

        Assert.Equal(new[] { running, completed }, page1.Items.Select(s => s.CorrelationId));
        Assert.Equal(new[] { failed }, page2.Items.Select(s => s.CorrelationId));
    }

    [Fact]
    public async Task ClaimDueAsync_OrdersByDueDateAgainstRealPostgres()
    {
        var early = Guid.NewGuid();
        var late = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaTimeoutStore(db);
            await store.ScheduleAsync("OrderSaga", late, "AwaitingPayment", now.AddSeconds(-1), CancellationToken.None);
            await store.ScheduleAsync("OrderSaga", early, "AwaitingInventory", now.AddSeconds(-5), CancellationToken.None);
        }

        await using var db2 = NewContext();
        var due = await new EfCoreSagaTimeoutStore(db2).ClaimDueAsync(now, batchSize: 10);

        Assert.Equal(2, due.Count);
        Assert.Equal(early, due[0].CorrelationId); // earliest due date claimed first
        Assert.Equal(late, due[1].CorrelationId);
    }

    [Fact]
    public async Task ClaimDueAsync_ConcurrentCallsNeverClaimTheSameTimeoutTwice()
    {
        var now = DateTimeOffset.UtcNow;

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaTimeoutStore(db);
            for (var i = 0; i < 20; i++)
                await store.ScheduleAsync("OrderSaga", Guid.NewGuid(), "AwaitingPayment", now.AddSeconds(-1), CancellationToken.None);
        }

        // Two dispatcher instances (separate DbContexts/connections) racing on the same 20 due rows —
        // FOR UPDATE SKIP LOCKED must partition them with no overlap, proving the atomic claim actually
        // works under concurrency rather than just compiling.
        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var claimA = new EfCoreSagaTimeoutStore(dbA).ClaimDueAsync(now, batchSize: 20);
        var claimB = new EfCoreSagaTimeoutStore(dbB).ClaimDueAsync(now, batchSize: 20);
        var results = await Task.WhenAll(claimA, claimB);

        var claimedIds = results.SelectMany(r => r).Select(t => t.Id).ToList();
        Assert.Equal(20, claimedIds.Count);
        Assert.Equal(claimedIds.Count, claimedIds.Distinct().Count());
    }

    [Fact]
    public async Task ClaimPendingAsync_OrdersByCreatedAtAgainstRealPostgres()
    {
        var early = Guid.NewGuid();
        var late = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaOutboxStore(db);
            await store.EnqueueAsync("OrderSaga", late, "m-late", "InventoryReserved", "{}"u8.ToArray(), null,
                new Dictionary<string, string>(StringComparer.Ordinal), now.AddSeconds(-1));
            await store.EnqueueAsync("OrderSaga", early, "m-early", "InventoryReserved", "{}"u8.ToArray(), null,
                new Dictionary<string, string>(StringComparer.Ordinal), now.AddSeconds(-5));
            // EnqueueAsync stages only — in production the snapshot store's PersistAsync commits these.
            await db.SaveChangesAsync();
        }

        await using var db2 = NewContext();
        var claimed = await new EfCoreSagaOutboxStore(db2).ClaimPendingAsync(now, batchSize: 10);

        Assert.Equal(2, claimed.Count);
        Assert.Equal(early, claimed[0].CorrelationId); // earliest created first
        Assert.Equal(late, claimed[1].CorrelationId);
    }

    [Fact]
    public async Task ClaimPendingAsync_ConcurrentCallsNeverClaimTheSameMessageTwice()
    {
        var now = DateTimeOffset.UtcNow;

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaOutboxStore(db);
            for (var i = 0; i < 20; i++)
            {
                await store.EnqueueAsync("OrderSaga", Guid.NewGuid(), $"m{i}", "InventoryReserved", "{}"u8.ToArray(),
                    null, new Dictionary<string, string>(StringComparer.Ordinal), now.AddSeconds(-1));
            }

            await db.SaveChangesAsync();
        }

        // Two dispatcher instances (separate DbContexts/connections) racing on the same 20 stale rows --
        // FOR UPDATE SKIP LOCKED must partition them with no overlap, proving the atomic claim actually
        // works under concurrency rather than just compiling.
        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var claimA = new EfCoreSagaOutboxStore(dbA).ClaimPendingAsync(now, batchSize: 20);
        var claimB = new EfCoreSagaOutboxStore(dbB).ClaimPendingAsync(now, batchSize: 20);
        var results = await Task.WhenAll(claimA, claimB);

        var claimedIds = results.SelectMany(r => r).Select(m => m.Id).ToList();
        Assert.Equal(20, claimedIds.Count);
        Assert.Equal(claimedIds.Count, claimedIds.Distinct().Count());
    }

    [Fact]
    public async Task ServiceTopologyStore_GetAllAsync_TranslatesAgainstRealPostgres()
    {
        await using (var db = NewContext())
        {
            var store = new EfCoreServiceTopologyStore(db);
            await store.RecordAsync("InventoryService", "ReserveInventory", "vsaga.participant.inventory", DateTimeOffset.UtcNow);
            await store.RecordAsync("PaymentService", "ChargePayment", "vsaga.participant.payment", DateTimeOffset.UtcNow);
        }

        await using var db2 = NewContext();
        var entries = await new EfCoreServiceTopologyStore(db2).GetAllAsync();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => string.Equals(e.ServiceName, "InventoryService", StringComparison.Ordinal) && string.Equals(e.MessageType, "ReserveInventory", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.ServiceName, "PaymentService", StringComparison.Ordinal) && string.Equals(e.MessageType, "ChargePayment", StringComparison.Ordinal));
    }

    /// <summary>
    /// Runs against a schema built by <c>MigrateAsync</c>, so this is as much a test of the
    /// AddSagaParentLinkage migration as of the query: if the migration failed to add the columns, the
    /// SQLite copy of this test would still pass (it builds its schema from the model) and only this one
    /// would catch it. That is the same gap that let a Distinct-over-record-projection query ship
    /// working on SQLite and throwing on Npgsql.
    /// </summary>
    [Fact]
    public async Task FindChildrenAsync_TranslatesAndFiltersAgainstTheMigratedSchema()
    {
        var parentId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            await store.InsertAsync(Child("InvoiceDeliverySaga", "PostShipmentChoreography", parentId));
            await store.InsertAsync(Child("InvoiceDeliverySaga", "PostShipmentChoreography", strangerId));
            await store.InsertAsync(Child("OrderSaga", parentSagaType: null, parentCorrelationId: null));
        }

        await using var db2 = NewContext();
        var children = await new EfCoreSagaSummaryReader(db2).FindChildrenAsync("PostShipmentChoreography", parentId);

        var child = Assert.Single(children);
        Assert.Equal("InvoiceDeliverySaga", child.SagaType);
        Assert.Equal(parentId, child.ParentCorrelationId);

        // Nullable columns really are null for a root saga rather than defaulting to an empty string,
        // which would otherwise make every root look like a child of a saga named "".
        var root = await new EfCoreSagaSummaryReader(db2).ListAsync(new SagaListFilter { SagaType = "OrderSaga" });
        Assert.Null(Assert.Single(root.Items).ParentSagaType);
    }

    private static TestState Child(string sagaType, string? parentSagaType, Guid? parentCorrelationId) => new()
    {
        CorrelationId = Guid.NewGuid(),
        SagaType = sagaType,
        Kind = SagaKind.Orchestrated,
        CurrentState = "Requested",
        Status = SagaStatus.Running,
        ParentSagaType = parentSagaType,
        ParentCorrelationId = parentCorrelationId,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// This is the first filtered/partial index in the whole codebase, so -- like
    /// GetSagaTypesAsync_TranslatesAndReturnsDistinctTypes above -- it deserves its own direct check
    /// against a real Postgres rather than trusting SQLite's (EfCoreStoreTests) result to generalize.
    /// Runs against a schema built by MigrateAsync, so this also confirms the AddSagaBusinessKey
    /// migration's partial-unique filter is valid Postgres DDL, not just a model that compiles.
    /// </summary>
    [Fact]
    public async Task BusinessKey_PartialUniqueIndexSurvivesTheMigration_AndRejectsADuplicateAgainstRealPostgres()
    {
        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);

            // Two null-BusinessKey rows under the same SagaType must still both succeed -- the whole
            // point of the filter being partial rather than a plain unique index.
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", CurrentState = "A", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", CurrentState = "B", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });

            await store.InsertAsync(new TestState
            {
                CorrelationId = Guid.NewGuid(),
                SagaType = "OrderSaga",
                CurrentState = "Submitted",
                BusinessKey = "ORD-PG-1",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        await using var db2 = NewContext();
        var store2 = new EfCoreSagaSnapshotStore<TestState>(db2);

        var found = await store2.FindByBusinessKeyAsync("OrderSaga", "ORD-PG-1");
        Assert.NotNull(found);
        Assert.Equal("Submitted", found.CurrentState);

        await Assert.ThrowsAsync<SagaAlreadyExistsException>(() => store2.InsertAsync(new TestState
        {
            CorrelationId = Guid.NewGuid(),
            SagaType = "OrderSaga",
            CurrentState = "Submitted",
            BusinessKey = "ORD-PG-1",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }));
    }
}
