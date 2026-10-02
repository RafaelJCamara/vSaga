using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VSaga.Dashboard.Identity.EFCore;

namespace VSaga.Dashboard.Identity.Tests.EFCore;

/// <summary>
/// An identity database as a file in a temp directory of its own (the API's shape, not the in-memory
/// harness), with the service collection the API composes over it. Disposing releases pooled connections
/// and deletes the directory.
/// </summary>
public sealed class IdentityDatabaseFile : IDisposable
{
    public IdentityDatabaseFile()
    {
        Directory = Path.Combine(Path.GetTempPath(), "vsaga-identity-tests", Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(Directory, "data", "identity.db");
    }

    /// <summary>The scratch directory; the database's own directory (<c>data</c>) does not exist yet.</summary>
    public string Directory { get; }

    public string DatabasePath { get; }

    public static string ConnectionString(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString();

    public DbContextOptions<DashboardIdentityDbContext> ContextOptions() =>
        new DbContextOptionsBuilder<DashboardIdentityDbContext>()
            .UseSqlite(ConnectionString(DatabasePath), sqlite => sqlite.MigrationsAssembly(SqliteIdentityStoreHarness.MigrationsAssembly))
            .Options;

    /// <summary>
    /// The identity services on <paramref name="path"/> (this file by default), as the API registers them;
    /// <paramref name="configure"/> adds or replaces services first (a fake clock, say).
    /// </summary>
    public ServiceProvider BuildServices(string? path = null, Action<IServiceCollection>? configure = null)
    {
        var resolved = path ?? DatabasePath;
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug));
        configure?.Invoke(services);
        services.AddDashboardIdentity(
            new DashboardIdentitySettings(resolved, PathProblem: null),
            db => db.UseSqlite(ConnectionString(resolved), sqlite => sqlite.MigrationsAssembly(SqliteIdentityStoreHarness.MigrationsAssembly)));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a file still held open is left to the temp folder's own cleanup.
        }
    }
}
