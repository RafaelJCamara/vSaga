using System.Globalization;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Persistence.Conformance;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace VSaga.Persistence.Redis.Tests;

/// <summary>
/// The Redis provider over a real Redis 7 in a container, configured at the plan's Tier A
/// (<c>appendonly yes</c>, <c>appendfsync everysec</c>, <c>maxmemory-policy noeviction</c>): one container
/// and one multiplexer for the whole collection, one key-space namespace per case, deleted when the
/// case's stores are disposed.
/// </summary>
public sealed class RedisProviderFixture : IProviderFixture, IAsyncLifetime, IAsyncDisposable
{
    public const string Image = "redis:7.4-alpine";

    private readonly RedisContainer _container = TierA(new RedisBuilder(Image)).Build();
    private RedisConnection? _connection;
    private int _namespaceCount;

    /// <summary>Staged outbox rows live in the Scoped unit of work until the persist script writes them with the snapshot.</summary>
    public bool SupportsAtomicUnitOfWork => true;

    /// <summary>Each claim is one Lua script: rows are marked terminal and removed from the due/pending set inside it.</summary>
    public bool SupportsConcurrentClaim => true;

    /// <summary>Projected timestamps are stored as Unix microseconds -- the same resolution as Postgres's timestamp columns.</summary>
    public TimeSpan TimestampResolution => TimeSpan.FromMicroseconds(1);

    public string ConnectionString => _container.GetConnectionString();

    public RedisConnection Connection => _connection ?? throw new InvalidOperationException("The fixture has not been initialised.");

    /// <summary>A Redis server started with the durability and eviction settings the provider's Tier A requires.</summary>
    public static RedisBuilder TierA(RedisBuilder builder) =>
        builder.WithCommand("redis-server", "--appendonly", "yes", "--appendfsync", "everysec", "--maxmemory-policy", "noeviction");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _connection = new RedisConnection(new VSagaRedisOptions { ConnectionString = ConnectionString });
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
        await _container.DisposeAsync();
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    public async Task<IProviderStores> CreateStoresAsync(CancellationToken cancellationToken = default) =>
        await CreateNamespaceAsync(options: null, cancellationToken);

    /// <summary>A fresh namespace on the shared server, with the given options (connection string and namespace are always the fixture's).</summary>
    public Task<RedisProviderStores> CreateNamespaceAsync(VSagaRedisOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new VSagaRedisOptions();
        options.ConnectionString = ConnectionString;
        options.Namespace = "conformance-" + Interlocked.Increment(ref _namespaceCount).ToString(CultureInfo.InvariantCulture);

        var stores = new RedisProviderStores(Connection, options, () => DeleteNamespaceAsync(options.Namespace));
        return Task.FromResult(stores);
    }

    private async ValueTask DeleteNamespaceAsync(string ns)
    {
        var multiplexer = await Connection.GetMultiplexerAsync();
        var server = multiplexer.GetServers().First(s => s.IsConnected);
        var keys = new List<RedisKey>();
        await foreach (var key in server.KeysAsync(pattern: new RedisKeySpace(ns).Prefix + "*", pageSize: 1000))
            keys.Add(key);

        if (keys.Count > 0)
            await multiplexer.GetDatabase().KeyDeleteAsync(keys.ToArray());
    }
}

/// <summary>One namespace on the fixture's server; each unit of work is a fresh <see cref="RedisSagaUnitOfWork"/>, as a DI scope would give it.</summary>
public sealed class RedisProviderStores : IProviderStores
{
    private readonly Func<ValueTask> _release;

    public RedisProviderStores(RedisConnection connection, VSagaRedisOptions options, Func<ValueTask> release)
    {
        Connection = connection;
        Options = options;
        Keys = new RedisKeySpace(options.Namespace);
        Probe = new RedisServerProbe(connection, Keys, options);
        Scripts = new RedisPersistScripts(connection, Keys, options, Probe);
        _release = release;
    }

    public RedisConnection Connection { get; }

    public VSagaRedisOptions Options { get; }

    public RedisKeySpace Keys { get; }

    public RedisServerProbe Probe { get; }

    public RedisPersistScripts Scripts { get; }

    public Task<IStoreUnitOfWork> BeginAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IStoreUnitOfWork>(new RedisConformanceUnitOfWork(this));

    public ValueTask DisposeAsync() => _release();
}

/// <summary>
/// The seven stores over one <see cref="RedisSagaUnitOfWork"/> -- the shape <c>AddVSagaRedis</c> registers
/// per scope. <see cref="CommitAsync"/> runs the persist script's outbox-only mode, the one way to make
/// a staged row durable without a snapshot write; <see cref="AbandonAsync"/> drops what is staged.
/// </summary>
internal sealed class RedisConformanceUnitOfWork(RedisProviderStores stores) : IStoreUnitOfWork
{
    private readonly RedisSagaUnitOfWork _unitOfWork = new();
    private readonly RedisSagaSummaryReader _summaryReader = new(stores.Connection, stores.Keys, stores.Options, stores.Scripts);

    public ISagaSnapshotStore<TState> Snapshots<TState>() where TState : SagaState =>
        new RedisSagaSnapshotStore<TState>(stores.Connection, stores.Keys, stores.Scripts, _unitOfWork);

    public ISagaEventLogStore EventLog { get; } = new RedisSagaEventLogStore(stores.Connection, stores.Keys);

    public ISagaTimeoutStore Timeouts { get; } = new RedisSagaTimeoutStore(stores.Connection, stores.Keys);

    public ISagaOutboxStore Outbox => new RedisSagaOutboxStore(stores.Connection, stores.Keys, _unitOfWork);

    public ISagaSummaryReader Summaries => _summaryReader;

    public ISagaAdminStore Admin => _summaryReader;

    public IServiceTopologyStore Topology { get; } = new RedisServiceTopologyStore(stores.Connection, stores.Keys);

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        _unitOfWork.Staged.Count == 0 ? Task.CompletedTask : stores.Scripts.CommitStagedOutboxAsync(_unitOfWork, cancellationToken);

    public Task AbandonAsync(CancellationToken cancellationToken = default)
    {
        _unitOfWork.Clear();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[CollectionDefinition(Name)]
public sealed class RedisConformanceGroup : ICollectionFixture<RedisProviderFixture>
{
    public const string Name = "Redis conformance";
}
