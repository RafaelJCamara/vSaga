using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;
using VSaga.Dashboard.Identity.Tests.EFCore;

namespace VSaga.Dashboard.Identity.Tests.Services;

public sealed class IdentityStartupTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly IdentityDatabaseFile _file = new();
    private readonly FakeTimeProvider _time = new(ServiceTestContext.T0);

    public void Dispose() => _file.Dispose();

    [Fact]
    public async Task EnsureReady_CreatesTheDirectoryAndDatabase_AndWritesTheBuiltInRoles()
    {
        await using var services = _file.BuildServices();
        var startup = services.GetRequiredService<IdentityStartup>();

        Assert.False(startup.IsReady);
        Assert.True(await startup.EnsureReadyAsync(None));

        Assert.True(startup.IsReady);
        Assert.Null(startup.FailureReason);
        Assert.True(File.Exists(_file.DatabasePath));
        var roles = await RolesAsync(services);
        Assert.Equal(BuiltInRoles.All.Count, roles.Count);
        foreach (var builtIn in BuiltInRoles.All)
            Assert.Equal(builtIn.Permissions, roles.Single(r => r.Id == builtIn.Id).Permissions);
    }

    [Fact]
    public async Task EnsureReady_OnAnExistingDatabase_RestoresABuiltInRoleToItsCodeDefinition()
    {
        await using (var first = _file.BuildServices())
        {
            Assert.True(await first.GetRequiredService<IdentityStartup>().EnsureReadyAsync(None));
            await using var scope = first.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>();
            await using var write = await store.BeginExclusiveAsync(None);
            await store.UpdateRoleAsync(BuiltInRoles.Viewer with { Description = "edited", Permissions = [Permissions.SagasView] }, None);
            await write.CommitAsync(None);
        }

        await using var restarted = _file.BuildServices();
        Assert.True(await restarted.GetRequiredService<IdentityStartup>().EnsureReadyAsync(None));

        var viewer = (await RolesAsync(restarted)).Single(r => r.Id == BuiltInRoles.ViewerId);
        Assert.Equal(BuiltInRoles.Viewer.Description, viewer.Description);
        Assert.Equal(BuiltInRoles.Viewer.Permissions, viewer.Permissions);
    }

    [Fact]
    public async Task EnsureReady_WithAnUnusablePath_NeverThrows_AndNamesTheSetting()
    {
        var unusable = BlockedPath();
        await using var services = _file.BuildServices(unusable);
        var startup = services.GetRequiredService<IdentityStartup>();

        Assert.False(await startup.EnsureReadyAsync(None));

        Assert.False(startup.IsReady);
        Assert.Contains("cannot be created, opened or written", startup.FailureReason, StringComparison.Ordinal);
        Assert.Contains(DashboardIdentitySettings.SqlitePathKey, startup.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureReady_WithNoResolvedPath_ReportsWhyWithoutTouchingADatabase()
    {
        var settings = new DashboardIdentitySettings(DatabasePath: null, "Dashboard:Identity:Sqlite:Path is not set. In a container it must name it.");
        using var startup = new IdentityStartup(new ThrowingScopeFactory(), settings, _time, NullLogger<IdentityStartup>.Instance);

        Assert.False(await startup.EnsureReadyAsync(None));

        Assert.Equal(settings.PathProblem, startup.FailureReason);
    }

    [Fact]
    public async Task EnsureReady_AfterAFailure_RetriesOnlyOnceTheRetryIntervalHasPassed()
    {
        var blocked = BlockedPath();
        await using var services = _file.BuildServices(blocked, s => s.AddSingleton<TimeProvider>(_time));
        var startup = services.GetRequiredService<IdentityStartup>();
        Assert.False(await startup.EnsureReadyAsync(None));

        // The volume is fixed, but the next attempt is not due yet.
        File.Delete(Path.GetDirectoryName(blocked)!);
        Assert.False(await startup.EnsureReadyAsync(None));
        _time.Advance(IdentityStartup.RetryInterval - TimeSpan.FromSeconds(1));
        Assert.False(await startup.EnsureReadyAsync(None));
        Assert.False(File.Exists(blocked));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await startup.EnsureReadyAsync(None));
        Assert.True(startup.IsReady);
        Assert.True(File.Exists(blocked));
    }

    [Fact]
    public async Task EnsureReady_WhenTheCallerStopsWaiting_TheAttemptRunsOnToTheEnd()
    {
        await using var stale = await StartupOverAStaleLockAsync(TimeSpan.FromSeconds(30));
        var startup = stale.Startup;
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(await startup.EnsureReadyAsync(caller.Token));

        // The caller got its answer long before the attempt's timeout, and the attempt is still running.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"The caller waited {clock.Elapsed}.");
        Assert.Equal(IdentityStartup.InitialisingReason, startup.FailureReason);

        // The operator clears the lock: the same attempt, which nobody waits for any more, gets through.
        await ExecuteAsync("DELETE FROM __EFMigrationsLock");
        Assert.True(await EventuallyAsync(() => startup.IsReady, TimeSpan.FromSeconds(25)), startup.FailureReason);
    }

    [Fact]
    public async Task EnsureReady_ACallerThatStopsWaiting_StillCountsTheAttemptForTheRetryInterval()
    {
        await using var stale = await StartupOverAStaleLockAsync(TimeSpan.FromSeconds(1));
        var startup = stale.Startup;
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Assert.False(await startup.EnsureReadyAsync(caller.Token));

        // A second caller joins the attempt that is running instead of starting one, and sees it time out.
        Assert.False(await startup.EnsureReadyAsync(None));
        Assert.StartsWith("Initialising the identity database timed out after 1 s", startup.FailureReason, StringComparison.Ordinal);

        // The lock is cleared, but the abandoned-by-its-caller attempt counted: the next is not due yet.
        await ExecuteAsync("DELETE FROM __EFMigrationsLock");
        Assert.False(await startup.EnsureReadyAsync(None));
        _time.Advance(IdentityStartup.RetryInterval);
        Assert.True(await startup.EnsureReadyAsync(None));
    }

    [Fact]
    public async Task EnsureReady_WithAMaxWait_AnswersBeforeTheAttemptEnds_AndReportsItInProgress()
    {
        await using var stale = await StartupOverAStaleLockAsync(TimeSpan.FromSeconds(30));
        var startup = stale.Startup;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(await startup.EnsureReadyAsync(TimeSpan.FromMilliseconds(200), None));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"The caller waited {clock.Elapsed}.");
        Assert.Equal(IdentityStartup.InitialisingReason, startup.FailureReason);
    }

    [Fact]
    public async Task EnsureReady_WithAStaleMigrationLock_TimesOutNamingTheLockTable()
    {
        await using var stale = await StartupOverAStaleLockAsync(TimeSpan.FromSeconds(1));
        var startup = stale.Startup;

        Assert.False(await startup.EnsureReadyAsync(None));

        Assert.Contains("timed out after 1 s", startup.FailureReason, StringComparison.Ordinal);
        Assert.Contains("__EFMigrationsLock", startup.FailureReason, StringComparison.Ordinal);

        // While the retry runs, the reason says so and keeps the previous attempt's.
        _time.Advance(IdentityStartup.RetryInterval);
        Assert.False(await startup.EnsureReadyAsync(TimeSpan.Zero, None));
        Assert.StartsWith($"{IdentityStartup.InitialisingReason} The previous attempt failed: Initialising the identity database timed out", startup.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureReady_OnUnix_CreatesTheDirectoryAndFileForTheOwnerOnly()
    {
        // File modes exist on Unix only; CI's Linux runner exercises this, Windows passes it trivially.
        if (OperatingSystem.IsWindows())
            return;

        await using var services = _file.BuildServices();
        Assert.True(await services.GetRequiredService<IdentityStartup>().EnsureReadyAsync(None));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(_file.DatabasePath)!));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_file.DatabasePath));
    }

    [Fact]
    public async Task EnsureReady_OnUnix_NarrowsAnExistingWorldReadableFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using (var first = _file.BuildServices())
            Assert.True(await first.GetRequiredService<IdentityStartup>().EnsureReadyAsync(None));
        SqliteConnection.ClearAllPools();
        File.SetUnixFileMode(_file.DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await using var restarted = _file.BuildServices();
        Assert.True(await restarted.GetRequiredService<IdentityStartup>().EnsureReadyAsync(None));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_file.DatabasePath));
    }

    /// <summary>A database path whose directory cannot be created, because a file sits where it would go.</summary>
    private string BlockedPath()
    {
        Directory.CreateDirectory(_file.Directory);
        var blocker = Path.Combine(_file.Directory, "not-a-directory");
        File.WriteAllText(blocker, "");
        return Path.Combine(blocker, "identity.db");
    }

    /// <summary>
    /// A migrated database holding what a process killed mid-migration leaves behind (a row in
    /// __EFMigrationsLock), and a fresh <see cref="IdentityStartup"/> over it on the fake clock.
    /// </summary>
    private async Task<StaleLock> StartupOverAStaleLockAsync(TimeSpan timeout)
    {
        await using (var first = _file.BuildServices())
            Assert.True(await first.GetRequiredService<IdentityStartup>().EnsureReadyAsync(None));

        await ExecuteAsync("INSERT INTO __EFMigrationsLock (Id, Timestamp) VALUES (1, '2026-10-02 09:00:00')");
        var services = _file.BuildServices();
        var startup = new IdentityStartup(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<DashboardIdentitySettings>(),
            _time,
            NullLogger<IdentityStartup>.Instance) { Timeout = timeout };
        return new StaleLock(services, startup);
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                return false;
            await Task.Delay(50, None);
        }

        return true;
    }

    private static async Task<IReadOnlyList<DashboardRole>> RolesAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>().ListRolesAsync(None);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(IdentityDatabaseFile.ConnectionString(_file.DatabasePath));
        await connection.OpenAsync(None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(None);
    }

    private sealed class StaleLock(ServiceProvider services, IdentityStartup startup) : IAsyncDisposable
    {
        public IdentityStartup Startup => startup;

        public async ValueTask DisposeAsync()
        {
            // Disposing ends a running attempt; waiting for it to end releases its connection before the
            // provider and the file go.
            startup.Dispose();
            await startup.EnsureReadyAsync(None);
            await services.DisposeAsync();
        }
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("No database should be opened without a path.");
    }
}
