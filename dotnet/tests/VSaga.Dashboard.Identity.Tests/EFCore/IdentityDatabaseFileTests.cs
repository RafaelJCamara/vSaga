using Microsoft.Data.Sqlite;

namespace VSaga.Dashboard.Identity.Tests.EFCore;

/// <summary>
/// Test classes run in parallel in one process, each with an <see cref="IdentityDatabaseFile"/> of its own:
/// releasing one file's pooled connections must not reach another's (<c>SqliteConnection.ClearAllPools()</c> did,
/// and a connection it disposed under another test failed that test's next statement).
/// </summary>
public sealed class IdentityDatabaseFileTests
{
    private const string Marker = "pool_marker";

    [Fact]
    public async Task Dispose_LeavesTheConnectionsPooledForAnotherFileAlone()
    {
        using var kept = new IdentityDatabaseFile();
        var disposed = new IdentityDatabaseFile();
        try
        {
            // A temp table lives and dies with the connection that made it, so it is still there on the next
            // open only if the pool handed back the same connection: one that was cleared would be a new one.
            await using (var connection = await OpenAsync(kept))
                await ExecuteAsync(connection, $"CREATE TEMP TABLE {Marker} (Id INTEGER)");
            await using (var connection = await OpenAsync(disposed))
                await ExecuteAsync(connection, "SELECT 1");

            disposed.Dispose();

            await using var again = await OpenAsync(kept);
            Assert.Equal(1L, await ScalarAsync(again, $"SELECT COUNT(*) FROM sqlite_temp_master WHERE name = '{Marker}'"));
        }
        finally
        {
            disposed.Dispose();
        }
    }

    /// <summary>A path other than <see cref="IdentityDatabaseFile.DatabasePath"/> that a test builds services on is released too.</summary>
    [Fact]
    public async Task ClearPool_AlsoReleasesAnotherPathTheServicesWereBuiltOn()
    {
        using var file = new IdentityDatabaseFile();
        var other = Path.Combine(file.Directory, "other", "identity.db");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        await using var services = file.BuildServices(other);

        await using (var connection = new SqliteConnection(IdentityDatabaseFile.ConnectionString(other)))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"CREATE TEMP TABLE {Marker} (Id INTEGER)");
        }

        file.ClearPool();

        await using var again = new SqliteConnection(IdentityDatabaseFile.ConnectionString(other));
        await again.OpenAsync();
        Assert.Equal(0L, await ScalarAsync(again, $"SELECT COUNT(*) FROM sqlite_temp_master WHERE name = '{Marker}'"));
    }

    private static async Task<SqliteConnection> OpenAsync(IdentityDatabaseFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file.DatabasePath)!);
        var connection = new SqliteConnection(IdentityDatabaseFile.ConnectionString(file.DatabasePath));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
