using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VSaga.Dashboard.Identity.EFCore;

namespace VSaga.Dashboard.Identity.Tests.EFCore;

/// <summary>
/// The generated SQLite migrations against the model. A model change without a new migration would make
/// MigrateAsync throw at start, which IdentityStartup turns into a degraded, sign-in-less dashboard rather
/// than a crash, so this is where it has to be caught.
/// </summary>
public sealed class IdentityMigrationsTests : IDisposable
{
    private readonly IdentityDatabaseFile _file = new();

    public void Dispose() => _file.Dispose();

    [Fact]
    public async Task Migrations_BuildEveryTableOnAFile_AndLeaveNothingPending()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file.DatabasePath)!);
        await using var db = new DashboardIdentityDbContext(_file.ContextOptions());

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var tables = await TablesAsync(_file.DatabasePath);
        foreach (var table in new[] { "Users", "Teams", "TeamMembers", "Roles", "UserGrants", "TeamGrants", "DataProtectionKeys" })
            Assert.Contains(table, tables, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Model_HasNoChangesMissingAMigration()
    {
        await using var db = new DashboardIdentityDbContext(_file.ContextOptions());

        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task<List<string>> TablesAsync(string path)
    {
        await using var connection = new SqliteConnection(IdentityDatabaseFile.ConnectionString(path));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }
}
