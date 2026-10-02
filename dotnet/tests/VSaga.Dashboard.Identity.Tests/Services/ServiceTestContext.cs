using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;
using VSaga.Dashboard.Identity.Tests.EFCore;

namespace VSaga.Dashboard.Identity.Tests.Services;

/// <summary>
/// One SQLite identity database with the built-in roles, a fake clock, a fast password hasher, a recording
/// access-change observer and captured logs, for the service tests.
/// </summary>
internal sealed class ServiceTestContext : IAsyncDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    public const string StrongPassword = "correct horse battery";

    private ServiceTestContext(SqliteIdentityStoreHarness harness, DashboardSecuritySettings settings)
    {
        Harness = harness;
        Settings = settings;
        Observer = new RecordingAccessChangeObserver(harness);
    }

    public static CancellationToken None => CancellationToken.None;

    public static AuditContext Admin { get; } = new("dashboard:root", "203.0.113.7");

    public SqliteIdentityStoreHarness Harness { get; }

    public DashboardSecuritySettings Settings { get; }

    public FakeTimeProvider Time { get; } = new(T0);

    /// <summary>Few iterations, so hashing does not dominate the suite.</summary>
    public IPasswordHasher<DashboardUser> Hasher { get; } = NewHasher(1000);

    public RecordingAccessChangeObserver Observer { get; }

    public CapturingLoggerFactory Logs { get; } = new();

    public static async Task<ServiceTestContext> CreateAsync(DashboardSecuritySettings? settings = null)
    {
        var harness = await SqliteIdentityStoreHarness.CreateAsync();
        var store = harness.CreateStore();
        foreach (var role in BuiltInRoles.All)
            await store.CreateRoleAsync(role, None);
        return new ServiceTestContext(harness, settings ?? DashboardSecuritySettings.Default);
    }

    public static IPasswordHasher<DashboardUser> NewHasher(int iterations) =>
        new PasswordHasher<DashboardUser>(Options.Create(new PasswordHasherOptions { IterationCount = iterations }));

    public IDashboardIdentityStore NewStore() => Harness.CreateStore();

    public AccessAdministrationService NewAdministration(IPasswordHasher<DashboardUser>? hasher = null) =>
        new(NewStore(), hasher ?? Hasher, new PasswordPolicy(Settings), Observer, Settings, Time, Logs);

    public CredentialVerifier NewVerifier(IPasswordHasher<DashboardUser>? hasher = null)
    {
        var effective = hasher ?? Hasher;
        return new CredentialVerifier(NewStore(), effective, new DummyPasswordHash(effective), Settings, Time);
    }

    /// <summary>A user written straight to the store, past every service rule, for arranging a case.</summary>
    public async Task<DashboardUser> SeedUserAsync(
        string username, string password = StrongPassword, bool isEnabled = true, bool mustChangePassword = false, params AccessGrant[] grants)
    {
        var user = new DashboardUser(
            Guid.NewGuid(), username, username, Hasher.HashPassword(null!, password), SecurityStamps.New(), isEnabled, mustChangePassword,
            FailedSignInCount: 0, LockoutEndUtc: null, LastSignInAtUtc: null, T0, T0, grants);
        await NewStore().CreateUserAsync(user, None);
        return user;
    }

    /// <summary>An enabled user holding the built-in Administrator role for all saga types.</summary>
    public Task<DashboardUser> SeedAdministratorAsync(string username = "root") =>
        SeedUserAsync(username, grants: AllTypes(BuiltInRoles.AdministratorId));

    public async Task<DashboardTeam> SeedTeamAsync(string name, IReadOnlyList<Guid> memberIds, params AccessGrant[] grants)
    {
        var team = new DashboardTeam(Guid.NewGuid(), name, null, memberIds, grants);
        await NewStore().CreateTeamAsync(team, None);
        return team;
    }

    public async Task<DashboardRole> SeedRoleAsync(string name, params string[] permissions)
    {
        var role = new DashboardRole(Guid.NewGuid(), name, null, IsBuiltIn: false, permissions);
        await NewStore().CreateRoleAsync(role, None);
        return role;
    }

    public async Task<DashboardUser> ReadUserAsync(Guid id) =>
        await NewStore().FindUserAsync(id, None) ?? throw new InvalidOperationException($"User {id} is gone.");

    public static AccessGrant AllTypes(Guid roleId) => new(roleId, AllSagaTypes: true, []);

    public static AccessGrant Named(Guid roleId, params string[] sagaTypes) => new(roleId, AllSagaTypes: false, sagaTypes);

    public ValueTask DisposeAsync() => Harness.DisposeAsync();
}

/// <summary>
/// Records each notification and, at the moment it arrives, what a separate unit of work reads from the
/// store: a notification sent before the commit would see the old data (or the write lock).
/// </summary>
internal sealed class RecordingAccessChangeObserver(SqliteIdentityStoreHarness harness) : IAccessChangeObserver
{
    private readonly Lock _gate = new();

    public List<IReadOnlyCollection<Guid>> UserNotifications { get; } = [];

    /// <summary>Each user notification as <see cref="Ids"/> renders it, in the order they arrived.</summary>
    public IReadOnlyList<string> Notified
    {
        get
        {
            lock (_gate)
                return [.. UserNotifications.Select(n => Ids([.. n]))];
        }
    }

    /// <summary>A set of user ids in a stable, comparable form.</summary>
    public static string Ids(params Guid[] ids) => string.Join(",", ids.Order());

    public int AllUsersNotifications { get; private set; }

    /// <summary>What <see cref="Observe"/> saw, one entry per notification.</summary>
    public List<string> Observations { get; } = [];

    /// <summary>Runs against a fresh store while the notification is delivered; its result lands in <see cref="Observations"/>.</summary>
    public Func<IDashboardIdentityStore, Task<string>>? Observe { get; set; }

    public Task UsersChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        lock (_gate)
            UserNotifications.Add([.. userIds]);
        return ObserveAsync();
    }

    public Task AllUsersChangedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            AllUsersNotifications++;
        return ObserveAsync();
    }

    private async Task ObserveAsync()
    {
        if (Observe is null)
            return;

        string observation;
        try
        {
            observation = await Observe(harness.CreateStore());
        }
        catch (Exception ex)
        {
            observation = "error: " + ex.GetType().Name;
        }

        lock (_gate)
            Observations.Add(observation);
    }
}

/// <summary>
/// Counts the hashing work and, inside the first password verification, runs <c>interleave</c> once: a
/// change committed through another store between a service's unlocked check and its write. The hasher
/// interface is synchronous, so the change runs to completion on the thread pool before the verification
/// returns its (unchanged) result.
/// </summary>
internal sealed class InterleavingHasher(IPasswordHasher<DashboardUser> inner, Func<Task>? interleave = null) : IPasswordHasher<DashboardUser>
{
    private int _calls;
    private int _verifications;

    public int Calls => Volatile.Read(ref _calls);

    public string HashPassword(DashboardUser user, string password)
    {
        Interlocked.Increment(ref _calls);
        return inner.HashPassword(user, password);
    }

    public PasswordVerificationResult VerifyHashedPassword(DashboardUser user, string hashedPassword, string providedPassword)
    {
        Interlocked.Increment(ref _calls);
        var result = inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        if (interleave is not null && Interlocked.Increment(ref _verifications) == 1)
            Task.Run(interleave).GetAwaiter().GetResult();
        return result;
    }
}

/// <summary>Every log entry written through it, with its category, event id and structured state.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly Lock _gate = new();

    public List<CapturedLog> Entries { get; } = [];

    public IEnumerable<CapturedLog> Audit => Snapshot().Where(e => string.Equals(e.Category, DashboardAudit.CategoryName, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private List<CapturedLog> Snapshot()
    {
        lock (_gate)
            return [.. Entries];
    }

    private void Add(CapturedLog entry)
    {
        lock (_gate)
            Entries.Add(entry);
    }

    private sealed class CapturingLogger(CapturingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            owner.Add(new CapturedLog(category, logLevel, eventId, formatter(state, exception), properties));
        }
    }
}

internal sealed record CapturedLog(string Category, LogLevel Level, EventId EventId, string Message, IReadOnlyDictionary<string, object?> Properties)
{
    public string? Property(string name) => Properties.TryGetValue(name, out var value) ? value?.ToString() : null;
}
