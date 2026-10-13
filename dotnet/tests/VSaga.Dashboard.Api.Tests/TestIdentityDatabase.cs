using Microsoft.Data.Sqlite;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// A directory of its own under the temp folder for one test host's identity database, so no test ever
/// writes the developer's real <c>{LocalApplicationData}/vSaga/dashboard/identity.db</c> and parallel test
/// classes never share a file.
/// </summary>
public sealed class TestIdentityDatabase
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vsaga-dashboard-tests", Guid.NewGuid().ToString("N"));

    /// <summary>The database file; the API creates it, and its directory, at start.</summary>
    public string FilePath => System.IO.Path.Combine(_directory, "identity.db");

    /// <summary>
    /// The connection string the API composes for <see cref="FilePath"/> (<c>SqliteDashboardIdentity</c>), which is
    /// also the key of the connection pool its host uses: <see cref="Delete"/> clears exactly that pool.
    /// </summary>
    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = FilePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true,
    }.ToString();

    /// <summary>
    /// Releases the pooled connections that would keep the file open (Windows refuses to delete an open
    /// file), then deletes the directory, best effort: a file still held by a host that is shutting down is
    /// left to the temp folder's own cleanup rather than failing the test.
    /// </summary>
    /// <remarks>
    /// Only this database's pool. <c>SqliteConnection.ClearAllPools()</c> clears the pool of every host in
    /// the process, and test classes run in parallel: it disposed a connection another test's host had just
    /// taken from its own pool (<c>ObjectDisposedException: SQLitePCL.sqlite3</c> out of its next statement),
    /// which also failed that host's start-up half way (no tables, no built-in roles) with nothing but a
    /// logged reason.
    /// </remarks>
    public void Delete()
    {
        using (var pooled = new SqliteConnection(ConnectionString))
            SqliteConnection.ClearPool(pooled);

        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort, see above.
        }
    }
}
