using VSaga.Persistence.Conformance;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace VSaga.Persistence.EFCore.Tests.Conformance;

/// <summary>The EF Core provider over SQLite: the schema built from the model, one in-memory database per case.</summary>
public sealed class SqliteProviderFixture : IProviderFixture
{
    /// <summary>Every store shares one <see cref="VSagaDbContext"/> per unit of work, so a staged row is flushed only by a commit on it.</summary>
    public bool SupportsAtomicUnitOfWork => true;

    /// <summary>Anything but Npgsql takes the load-then-update claim fallback, correct for one dispatcher only (ADR 0004).</summary>
    public bool SupportsConcurrentClaim => false;

    /// <summary>EF Core's SQLite provider stores a <see cref="DateTime"/> as text with up to seven fractional digits (trailing zeros dropped) — full tick precision.</summary>
    public TimeSpan TimestampResolution => TimeSpan.FromTicks(1);

    public async Task<IProviderStores> CreateStoresAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"vsaga-conformance-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        // A named in-memory database lives exactly as long as some connection to it is open. This one
        // holds it for the stores' lifetime, while every unit of work opens its own connection, as it
        // would against a server rather than sharing one.
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<VSagaDbContext>().UseSqlite(connectionString).Options;
        await using (var db = new VSagaDbContext(options))
            await db.Database.EnsureCreatedAsync(cancellationToken);

        return new EfCoreProviderStores(options, async () =>
        {
            // Pooled connections would otherwise keep the database alive after the case ends.
            SqliteConnection.ClearPool(keepAlive);
            await keepAlive.DisposeAsync();
        });
    }
}
