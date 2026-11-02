using VSaga.Persistence.Conformance;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace VSaga.Persistence.EFCore.Tests.Conformance;

/// <summary>
/// The EF Core provider over a real SQL Server: one container for the whole collection, one database per
/// case, each built by <c>MigrateAsync</c> so every case runs against the migrated schema production gets —
/// not the model-built one SQLite uses. SQL Server has no <c>CREATE DATABASE ... TEMPLATE</c>, so unlike the
/// Postgres fixture the migration is applied afresh per case.
/// </summary>
public sealed class SqlServerProviderFixture : IProviderFixture, IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private int _databaseCount;

    /// <summary>Every store shares one <see cref="VSagaDbContext"/> per unit of work, so a staged row is flushed only by a commit on it.</summary>
    public bool SupportsAtomicUnitOfWork => true;

    /// <summary>SQL Server takes the atomic <c>UPDATE ... OUTPUT</c> over a <c>READPAST</c> read (ADR 0009).</summary>
    public bool SupportsConcurrentClaim => true;

    /// <summary>EF Core maps a <see cref="DateTime"/> to <c>datetime2</c>, whose default precision is 100 ns — one tick.</summary>
    public TimeSpan TimestampResolution => TimeSpan.FromTicks(1);

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<IProviderStores> CreateStoresAsync(CancellationToken cancellationToken = default)
    {
        var database = await CreateMigratedDatabaseAsync(cancellationToken);
        return new EfCoreProviderStores(database.Options, database.Release);
    }

    /// <summary>A fresh database with the migrations applied, for the cases that need its connection as well as its stores.</summary>
    internal async Task<MigratedDatabase> CreateMigratedDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var database = $"vsaga_conformance_{Interlocked.Increment(ref _databaseCount)}";
        await ExecuteAsync($"CREATE DATABASE [{database}]", cancellationToken);

        var connectionString = ConnectionString(database);
        var options = new DbContextOptionsBuilder<VSagaDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsAssembly("VSaga.Persistence.EFCore.SqlServer"))
            .Options;

        await using (var db = new VSagaDbContext(options))
            await db.Database.MigrateAsync(cancellationToken);

        return new MigratedDatabase(options, connectionString, async () =>
        {
            // The case's pooled connections would otherwise keep the database from being dropped.
            SqlConnection.ClearAllPools();
            await ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]", CancellationToken.None);
        });
    }

    private string ConnectionString(string database) =>
        new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = database }.ConnectionString;

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString("master"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerConformanceGroup : ICollectionFixture<SqlServerProviderFixture>
{
    public const string Name = "SQL Server conformance";
}

/// <summary>One migrated database: the options that open <see cref="VSagaDbContext"/> on it, its connection string and how to drop it.</summary>
internal sealed record MigratedDatabase(DbContextOptions<VSagaDbContext> Options, string ConnectionString, Func<ValueTask> Release);
