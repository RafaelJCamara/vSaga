using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Api.Hosting;

/// <summary>The SQLite arm of <c>Dashboard:Identity:Provider</c>.</summary>
public static class SqliteDashboardIdentity
{
    /// <summary>The assembly holding the generated identity migrations for SQLite.</summary>
    public const string MigrationsAssembly = "VSaga.Dashboard.Identity.Sqlite";

    /// <summary>
    /// Registers the identity store on the SQLite file <paramref name="settings"/> resolved. With no
    /// resolved path the registration still succeeds, so the API starts; <c>IdentityStartup</c> reports the
    /// reason and never opens a context, and anything that tried to would get
    /// <see cref="IdentityUnavailableException"/>.
    /// </summary>
    public static IServiceCollection AddSqliteDashboardIdentity(this IServiceCollection services, DashboardIdentitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return services.AddDashboardIdentity(settings, db => db.UseSqlite(
            ConnectionString(settings),
            sqlite => sqlite.MigrationsAssembly(MigrationsAssembly)));
    }

    private static string ConnectionString(DashboardIdentitySettings settings)
    {
        if (settings.DatabasePath is not { } path)
            throw new IdentityUnavailableException(settings.PathProblem ?? $"{DashboardIdentitySettings.SqlitePathKey} could not be resolved.");

        return new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
    }
}
