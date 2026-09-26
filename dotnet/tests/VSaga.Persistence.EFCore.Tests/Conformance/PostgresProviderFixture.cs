using VSaga.Persistence.Conformance;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace VSaga.Persistence.EFCore.Tests.Conformance;

/// <summary>
/// The EF Core provider over a real Postgres: one container for the whole collection, one database per
/// case. The schema is built once, by <c>MigrateAsync</c> into a template, so every case runs against the
/// migrated schema production gets — not the model-built one SQLite uses — and each case's database is a
/// cheap <c>CREATE DATABASE ... TEMPLATE</c> copy of it.
/// </summary>
public sealed class PostgresProviderFixture : IProviderFixture, IAsyncLifetime
{
    private const string TemplateDatabase = "vsaga_conformance_template";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private int _databaseCount;

    /// <summary>Every store shares one <see cref="VSagaDbContext"/> per unit of work, so a staged row is flushed only by a commit on it.</summary>
    public bool SupportsAtomicUnitOfWork => true;

    /// <summary>Npgsql takes the atomic <c>UPDATE ... FOR UPDATE SKIP LOCKED ... RETURNING</c> claim (ADR 0004).</summary>
    public bool SupportsConcurrentClaim => true;

    /// <summary>Postgres <c>timestamp</c> columns keep microseconds; the rest is truncated on write.</summary>
    public TimeSpan TimestampResolution => TimeSpan.FromMicroseconds(1);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Pooling off for the template only: a pooled connection left idling on it by MigrateAsync would make
        // CREATE DATABASE ... TEMPLATE refuse to copy it.
        await using var db = new VSagaDbContext(Options(TemplateDatabase, pooling: false));
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<IProviderStores> CreateStoresAsync(CancellationToken cancellationToken = default)
    {
        var database = $"vsaga_conformance_{Interlocked.Increment(ref _databaseCount)}";
        await ExecuteAsync($"CREATE DATABASE \"{database}\" TEMPLATE \"{TemplateDatabase}\"", cancellationToken);

        // A case's own database is pooled — several of them open a connection per write, hundreds of times —
        // and its pool is emptied before the drop, which WITH (FORCE) would otherwise have to break into.
        return new EfCoreProviderStores(Options(database, pooling: true), async () =>
        {
            await using (var pooled = new NpgsqlConnection(ConnectionString(database, pooling: true)))
                NpgsqlConnection.ClearPool(pooled);
            await ExecuteAsync($"DROP DATABASE \"{database}\" WITH (FORCE)", CancellationToken.None);
        });
    }

    private DbContextOptions<VSagaDbContext> Options(string database, bool pooling) =>
        new DbContextOptionsBuilder<VSagaDbContext>()
            .UseNpgsql(ConnectionString(database, pooling), npgsql => npgsql.MigrationsAssembly("VSaga.Persistence.EFCore.Postgres"))
            .Options;

    private string ConnectionString(string database, bool pooling) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database, Pooling = pooling }.ConnectionString;

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString("postgres", pooling: false));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresConformanceGroup : ICollectionFixture<PostgresProviderFixture>
{
    public const string Name = "Postgres conformance";
}
