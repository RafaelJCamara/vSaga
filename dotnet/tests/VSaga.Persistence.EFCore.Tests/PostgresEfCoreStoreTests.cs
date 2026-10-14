using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace VSaga.Persistence.EFCore.Tests;

/// <summary>
/// What the Postgres-specific tests keep after the provider-neutral conformance suite
/// (<c>VSaga.Persistence.Conformance</c>, run against the same migrated schema by the <c>Postgres*ConformanceTests</c>)
/// took over the rest of what this class once re-ran against a real Postgres: the SQL translation of the ordered
/// lists, the claim ordering, the <c>SKIP LOCKED</c> claims and the business-key index. What is left guards the
/// schema the migrations build (the per-type listing index), the columns an update writes
/// (which the suite reads back only through the blob), and what a root saga's nullable parent columns hold.
/// See persistence-contracts.md Q4.
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
}
