using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VSaga.Dashboard.Identity.EFCore;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Test classes run in parallel, each with a host of its own and a database file of its own, in one process:
/// one class's clean-up must never reach another's connections. <c>SqliteConnection.ClearAllPools()</c> did,
/// and a host whose connection it disposed mid-start-up came up without its tables or its built-in roles, or
/// failed a statement with <c>ObjectDisposedException</c>, in a different test each time.
/// </summary>
public sealed class TestIdentityDatabaseTests
{
    private const string Marker = "pool_marker";

    [Fact]
    public async Task Delete_LeavesTheConnectionsPooledForAnotherDatabaseAlone()
    {
        var kept = new TestIdentityDatabase();
        var deleted = new TestIdentityDatabase();
        try
        {
            // A temp table lives and dies with the connection that made it, so it is still there on the next
            // open only if the pool handed back the same connection: one that was cleared would be a new one.
            await MarkAsync(kept);
            await TouchAsync(deleted);

            deleted.Delete();

            Assert.True(await HasMarkerAsync(kept));
        }
        finally
        {
            kept.Delete();
            deleted.Delete();
        }
    }

    [Fact]
    public async Task Delete_ClearsThePoolItsOwnFileHas()
    {
        var database = new TestIdentityDatabase();
        try
        {
            await MarkAsync(database);

            database.Delete();

            Assert.False(await HasMarkerAsync(database));
        }
        finally
        {
            database.Delete();
        }
    }

    [Fact]
    public async Task TheConnectionString_IsTheOneTheApiUsesForThatFile()
    {
        await using var factory = new DashboardApiFactory();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardIdentityDbContext>();

        Assert.Equal(factory.IdentityConnectionString, db.Database.GetConnectionString());
    }

    private static async Task MarkAsync(TestIdentityDatabase database)
    {
        await using var connection = await OpenAsync(database);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TEMP TABLE {Marker} (Id INTEGER)";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> HasMarkerAsync(TestIdentityDatabase database)
    {
        await using var connection = await OpenAsync(database);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM sqlite_temp_master WHERE name = '{Marker}'";
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    private static async Task TouchAsync(TestIdentityDatabase database)
    {
        await using var connection = await OpenAsync(database);
    }

    private static async Task<SqliteConnection> OpenAsync(TestIdentityDatabase database)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(database.FilePath)!);
        var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
