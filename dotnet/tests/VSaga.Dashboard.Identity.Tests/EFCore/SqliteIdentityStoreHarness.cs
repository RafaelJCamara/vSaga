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
    /// <summary>The assembly the API takes the identity migrations from.</summary>
    public const string MigrationsAssembly = "VSaga.Dashboard.Identity.Sqlite";

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

        var options = new DbContextOptionsBuilder<DashboardIdentityDbContext>()
            .UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly(MigrationsAssembly))
            .Options;

        // The generated migrations, as the API applies them, so the store is tested on the schema it ships
        // with; IdentityMigrationsTests checks that they match the model.
        await using (var db = new DashboardIdentityDbContext(options))
            await db.Database.MigrateAsync();

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
