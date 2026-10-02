using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>
/// Brings the identity store to a usable state: creates the database's directory and file (owner-only on
/// Unix, since the file holds password hashes, security stamps and the Data Protection key ring), applies
/// the migrations, writes the built-in roles from code, so their permission sets always match
/// <see cref="BuiltInRoles"/>, and then lets <see cref="FirstAdministratorService"/> seed the first
/// administrator or open first-run setup, when one is registered. It never throws: an unusable store must not stop the API, whose saga views
/// do not depend on it. Until it succeeds, <see cref="IsReady"/> is false and <see cref="FailureReason"/>
/// says why in words an operator can act on.
/// </summary>
/// <remarks>
/// <para>
/// The host calls <see cref="EnsureReadyAsync(CancellationToken)"/> once before it starts; after that only
/// the <c>identity</c> health check calls it (the sign-in endpoints will too), which starts a new attempt
/// at most once per <see cref="RetryInterval"/> after the previous one ended. The Data Protection key ring
/// repository never retries: it reads <see cref="IsReady"/> and throws
/// <see cref="IdentityUnavailableException"/> while the store is not ready.
/// </para>
/// <para>
/// An attempt belongs to this class, not to the caller that started it: it ends when it succeeds, fails,
/// runs past <see cref="Timeout"/> or this instance is disposed, never because a caller stopped waiting. A
/// health probe that a prober abandons after 5 s therefore neither cancels a slow attempt nor makes the next
/// probe start another one.
/// </para>
/// </remarks>
public sealed partial class IdentityStartup : IIdentityReadiness, IDisposable
{
    /// <summary>How long one attempt may take before it is abandoned and reported.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The shortest gap between the end of a failed attempt and the start of the next.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    /// <summary>What <see cref="FailureReason"/> starts with while an attempt is running.</summary>
    public const string InitialisingReason = "The identity store is being initialised.";

    private static readonly Task<bool> Ready = Task.FromResult(true);
    private static readonly Task<bool> NotDue = Task.FromResult(false);

    private const string NotAttemptedReason = "The identity store has not been initialised yet.";

    private readonly IServiceScopeFactory _scopes;
    private readonly DashboardIdentitySettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<IdentityStartup> _logger;
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _disposed = new();
    private readonly CancellationToken _disposedToken;
    private volatile bool _ready;
    private volatile string _failureReason = NotAttemptedReason;
    private volatile Task<bool>? _attempt;
    private DateTimeOffset? _lastAttemptEndedAt;
    private string? _lastLoggedReason;
    private bool _pathLogged;
    private int _disposeCalled;

    public IdentityStartup(IServiceScopeFactory scopes, DashboardIdentitySettings settings, TimeProvider time, ILogger<IdentityStartup> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _settings = settings;
        _time = time;
        _logger = logger;
        _disposedToken = _disposed.Token;
    }

    /// <summary>How long one attempt may take; <see cref="DefaultTimeout"/> unless a test shortens it.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>True once an attempt has succeeded; it stays true for the life of the process.</summary>
    public bool IsReady => _ready;

    /// <summary>
    /// Why the store is not ready, or null when it is. While an attempt runs it starts with
    /// <see cref="InitialisingReason"/>, followed by the previous attempt's reason when there was one.
    /// </summary>
    public string? FailureReason
    {
        get
        {
            if (_ready)
                return null;

            var reason = _failureReason;
            if (_attempt is not { IsCompleted: false })
                return reason;

            return string.Equals(reason, NotAttemptedReason, StringComparison.Ordinal)
                ? InitialisingReason
                : $"{InitialisingReason} The previous attempt failed: {reason}";
        }
    }

    /// <summary>
    /// Returns true when the store is ready. Otherwise it joins the attempt that is running, or starts one
    /// when none has ended in the last <see cref="RetryInterval"/>, and waits for it until it ends or
    /// <paramref name="cancellationToken"/> is cancelled. Never throws: a cancelled wait answers false and
    /// leaves the attempt running, and counted.
    /// </summary>
    public Task<bool> EnsureReadyAsync(CancellationToken cancellationToken = default) =>
        EnsureReadyAsync(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>
    /// As <see cref="EnsureReadyAsync(CancellationToken)"/>, but waits for the attempt at most
    /// <paramref name="maxWait"/> and then answers false, leaving it to run on, so a caller with a deadline
    /// of its own (a health probe) is never held for a whole attempt.
    /// </summary>
    public async Task<bool> EnsureReadyAsync(TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        if (_ready)
            return true;

        var attempt = CurrentOrNewAttempt();
        try
        {
            return await attempt.WaitAsync(maxWait, cancellationToken);
        }
        catch (TimeoutException)
        {
            return _ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return _ready;
        }
    }

    /// <summary>
    /// Ends a running attempt; nothing is retried afterwards. Idempotent: the container disposes this once as
    /// itself and once as the <see cref="IIdentityReadiness"/> it is also registered as.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeCalled, 1) != 0)
            return;

        _disposed.Cancel();
        _disposed.Dispose();
    }

    /// <summary>The running attempt, a new one when one is due, or false when the last ended too recently.</summary>
    private Task<bool> CurrentOrNewAttempt()
    {
        lock (_sync)
        {
            if (_ready)
                return Ready;

            if (_attempt is { IsCompleted: false } running)
                return running;

            if (_disposedToken.IsCancellationRequested
                || (_lastAttemptEndedAt is { } ended && _time.GetUtcNow() - ended < RetryInterval))
                return NotDue;

            // Task.Run: the attempt starts with synchronous file work, which must not run under the lock or
            // on the caller's stack.
            var attempt = Task.Run(RunAttemptAsync, CancellationToken.None);
            _attempt = attempt;
            return attempt;
        }
    }

    private async Task<bool> RunAttemptAsync()
    {
        try
        {
            return await AttemptAsync();
        }
        finally
        {
            lock (_sync)
                _lastAttemptEndedAt = _time.GetUtcNow();
        }
    }

    // Every failure becomes a reason, never an exception: this runs before the host starts and from health
    // probes, and an unusable identity store must leave the rest of the API serving. The reason reaches the
    // anonymous /health response, so it names the setting to fix but not the path; the log has the path.
    private async Task<bool> AttemptAsync()
    {
        if (_settings.DatabasePath is not { } path)
            return Fail(_settings.PathProblem ?? $"{DashboardIdentitySettings.SqlitePathKey} could not be resolved.", exception: null);

        if (!_pathLogged)
        {
            _pathLogged = true;
            LogDatabasePath(_logger, path);
        }

        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_disposedToken);
            attempt.CancelAfter(Timeout);
            PrepareFile(path);
            await using var scope = _scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>();
            await store.InitializeAsync(attempt.Token);
            await UpsertBuiltInRolesAsync(store, attempt.Token);

            // The API registers it; a host without sign-in (the store's own tests) has no first administrator.
            if (scope.ServiceProvider.GetService<FirstAdministratorService>() is { } firstAdministrator)
                await firstAdministrator.ApplyAtStartAsync(attempt.Token);
        }
        catch (Exception) when (_disposedToken.IsCancellationRequested)
        {
            // The host is shutting down and disposed its services under the attempt: not a verdict on the store.
            return false;
        }
        catch (OperationCanceledException ex)
        {
            return Fail(
                $"Initialising the identity database timed out after {Timeout.TotalSeconds:0} s. A row left in "
                + "its __EFMigrationsLock table by a process that was killed while migrating blocks every later migration: "
                + "stop the API, delete that row (or the table) and start it again.",
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbException)
        {
            return Fail(
                $"The identity database file or its directory cannot be created, opened or written, or the file is not a SQLite "
                + $"database ({ex.GetType().Name}). Check {DashboardIdentitySettings.SqlitePathKey} (the API logs the resolved path "
                + "at start) and the volume's ownership and permissions.",
                ex);
        }
        catch (Exception ex)
        {
            return Fail($"Initialising the identity database failed ({ex.GetType().Name}); see the API log.", ex);
        }

        _ready = true;
        LogReady(_logger, path);
        return true;
    }

    private bool Fail(string reason, Exception? exception)
    {
        _failureReason = reason;
        if (string.Equals(reason, _lastLoggedReason, StringComparison.Ordinal))
        {
            LogStillNotReady(_logger, exception, reason);
        }
        else
        {
            _lastLoggedReason = reason;
            LogNotReady(_logger, exception, _settings.DatabasePath ?? "(unresolved)", reason, RetryInterval.TotalSeconds);
        }

        return false;
    }

    /// <summary>
    /// Creates the directory and the empty database file ahead of SQLite, so on Unix they are born
    /// owner-only (0700 and 0600; SQLite gives its -wal and -shm files the database file's mode). An existing
    /// file readable by others is narrowed to 0600 when this process may; Windows relies on the profile or
    /// volume ACLs.
    /// </summary>
    private void PrepareFile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (OperatingSystem.IsWindows())
        {
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            return;
        }

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (!File.Exists(path))
        {
            using (new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = ownerOnly }))
            {
                // An empty file is an empty SQLite database; the migrations fill it.
            }

            return;
        }

        if ((File.GetUnixFileMode(path) & ~(ownerOnly | UnixFileMode.UserExecute)) == UnixFileMode.None)
            return;

        try
        {
            File.SetUnixFileMode(path, ownerOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Owned by another account: the open below decides whether the file is usable at all.
            LogCouldNotRestrict(_logger, ex, path);
        }
    }

    /// <summary>Writes each built-in role as <see cref="BuiltInRoles"/> defines it, in one exclusive scope.</summary>
    private static async Task UpsertBuiltInRolesAsync(IDashboardIdentityStore store, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginExclusiveAsync(cancellationToken);
        foreach (var role in BuiltInRoles.All)
        {
            var stored = await store.FindRoleAsync(role.Id, cancellationToken);
            if (stored is null)
                await store.CreateRoleAsync(role, cancellationToken);
            else if (!SameRole(stored, role))
                await store.UpdateRoleAsync(role, cancellationToken);
        }

        await scope.CommitAsync(cancellationToken);
    }

    private static bool SameRole(DashboardRole stored, DashboardRole role) =>
        string.Equals(stored.Name, role.Name, StringComparison.Ordinal)
        && string.Equals(stored.Description, role.Description, StringComparison.Ordinal)
        && stored.IsBuiltIn == role.IsBuiltIn
        && stored.Permissions.SequenceEqual(role.Permissions, StringComparer.Ordinal);

    [LoggerMessage(EventId = 7200, EventName = "IdentityDatabasePath", Level = LogLevel.Information,
        Message = "Dashboard identity store: SQLite database at {Path}")]
    private static partial void LogDatabasePath(ILogger logger, string path);

    [LoggerMessage(EventId = 7201, EventName = "IdentityReady", Level = LogLevel.Information,
        Message = "Dashboard identity store ready at {Path}")]
    private static partial void LogReady(ILogger logger, string path);

    [LoggerMessage(EventId = 7202, EventName = "IdentityNotReady", Level = LogLevel.Error,
        Message = "Dashboard identity store at {Path} is not ready; sign-in is unavailable until it is. {Reason} Retrying when the identity health check asks, at most once every {RetrySeconds} s after an attempt ends")]
    private static partial void LogNotReady(ILogger logger, Exception? exception, string path, string reason, double retrySeconds);

    [LoggerMessage(EventId = 7203, EventName = "IdentityStillNotReady", Level = LogLevel.Debug,
        Message = "Dashboard identity store is still not ready: {Reason}")]
    private static partial void LogStillNotReady(ILogger logger, Exception? exception, string reason);

    [LoggerMessage(EventId = 7204, EventName = "IdentityFileModeNotRestricted", Level = LogLevel.Warning,
        Message = "Could not restrict the identity database at {Path} to its owner; anyone who can read it can read password hashes and forge sessions")]
    private static partial void LogCouldNotRestrict(ILogger logger, Exception exception, string path);
}
