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
    /// Releases the pooled connections that would keep the file open (Windows refuses to delete an open
    /// file), then deletes the directory, best effort: a file still held by a host that is shutting down is
    /// left to the temp folder's own cleanup rather than failing the test.
    /// </summary>
    public void Delete()
    {
        SqliteConnection.ClearAllPools();
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
