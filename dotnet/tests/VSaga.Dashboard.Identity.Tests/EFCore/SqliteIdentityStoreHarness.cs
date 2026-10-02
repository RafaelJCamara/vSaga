using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VSaga.Dashboard.Identity.EFCore;
using VSaga.Dashboard.Identity.Stores;
using VSaga.Dashboard.Identity.Tests.Stores;

namespace VSaga.Dashboard.Identity.Tests.EFCore;

/// <summary>
/// The EF Core store over a named in-memory SQLite database, one per case. A named in-memory database
/// lives exactly as long as some connection to it is open: the keep-alive connection holds it, while every
/// store gets a context, and so a connection, of its own, as separate requests would.
/// </summary>
public sealed class SqliteIdentityStoreHarness : IIdentityStoreHarness
{
    private readonly Lock _gate = new();
    private readonly List<DashboardIdentityDbContext> _contexts = [];
    private readonly SqliteConnection _keepAlive;
    private readonly DbContextOptions<DashboardIdentityDbContext> _options;

    private SqliteIdentityStoreHarness(SqliteConnection keepAlive, DbContextOptions<DashboardIdentityDbContext> options)
    {
        _keepAlive = keepAlive;
        _options = options;
    }

    public static async Task<SqliteIdentityStoreHarness> CreateAsync()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"vsaga-identity-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();

        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var options = new DbContextOptionsBuilder<DashboardIdentityDbContext>().UseSqlite(connectionString).Options;

        // The schema straight from the model. The generated migrations, and a test that they build the
        // same schema, arrive with the VSaga.Dashboard.Identity.Sqlite project.
        await using (var db = new DashboardIdentityDbContext(options))
            await db.Database.EnsureCreatedAsync();

        return new SqliteIdentityStoreHarness(keepAlive, options);
    }

    public IDashboardIdentityStore CreateStore() => new EfCoreDashboardIdentityStore(NewContext());

    public IDashboardKeyRingStore CreateKeyRingStore() => new EfCoreKeyRingStore(NewContext());

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
            await context.DisposeAsync();

        // Pooled connections would otherwise keep the database alive after the case ends.
        SqliteConnection.ClearPool(_keepAlive);
        await _keepAlive.DisposeAsync();
    }

    private DashboardIdentityDbContext NewContext()
    {
        var context = new DashboardIdentityDbContext(_options);
        lock (_gate)
            _contexts.Add(context);
        return context;
    }
}
