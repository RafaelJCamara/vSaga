using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace VSaga.Persistence.EFCore.Tests;

/// <summary>
/// What the Postgres-specific tests keep after the provider-neutral conformance suite
/// (<c>VSaga.Persistence.Conformance</c>, run against the same migrated schema by the <c>Postgres*ConformanceTests</c>)
/// took over the rest of what this class once re-ran against a real Postgres: the SQL translation of the ordered
/// lists, of <c>GetSagaTypesAsync</c> and of the topology read, the claim ordering, the <c>SKIP LOCKED</c> claims, the
/// business-key index, the <c>Status</c> and <c>CurrentState</c> columns an update writes, and the null parent columns
/// of a root saga. What is left guards the schema the migrations build: the per-type listing index.
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
}
