using System.Globalization;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Persistence.Conformance;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace VSaga.Persistence.MongoDB.Tests;

/// <summary>
/// The MongoDB provider over a real MongoDB 8 single-member replica set in a container -- the plan's
/// hard prerequisite, and the one thing an in-process fake could not give: one container and one client
/// for the whole collection, one database per case with the provider's indexes created up front, dropped
/// when the case's stores are disposed.
/// </summary>
public sealed class MongoProviderFixture : IProviderFixture, IAsyncLifetime, IAsyncDisposable
{
    public const string Image = "mongo:8.0";

    private readonly MongoDbContainer _container = new MongoDbBuilder(Image).WithReplicaSet("rs0").Build();
    private IMongoClient? _client;
    private int _databaseCount;

    /// <summary>Staged outbox rows live in the Scoped unit of work until the persist writes them in one transaction with the snapshot.</summary>
    public bool SupportsAtomicUnitOfWork => true;

    /// <summary>Each claim is one <c>findOneAndUpdate</c>: the row is marked terminal and returned in one atomic operation.</summary>
    public bool SupportsConcurrentClaim => true;

    /// <summary>Projected timestamps keep their exact ticks in an <c>Int64</c> beside the BSON Date.</summary>
    public TimeSpan TimestampResolution => TimeSpan.FromTicks(1);

    public string ConnectionString => _container.GetConnectionString();

    public IMongoClient Client => _client ?? throw new InvalidOperationException("The fixture has not been initialised.");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _client = new MongoClient(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _container.DisposeAsync();
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    public async Task<IProviderStores> CreateStoresAsync(CancellationToken cancellationToken = default) =>
        await CreateDatabaseAsync(options: null, cancellationToken);

    /// <summary>A fresh database on the shared server, indexed, with the given options (the connection string is always the fixture's).</summary>
    public async Task<MongoProviderStores> CreateDatabaseAsync(VSagaMongoOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new VSagaMongoOptions();
        options.ConnectionString = ConnectionString;
        options.DatabaseName = "conformance-" + Interlocked.Increment(ref _databaseCount).ToString(CultureInfo.InvariantCulture);

        var connection = new MongoConnection(Client, options.DatabaseName);
        var stores = new MongoProviderStores(connection, options, () => new ValueTask(Client.DropDatabaseAsync(options.DatabaseName, CancellationToken.None)));
        await MongoIndexes.CreateAllAsync(stores.Collections, cancellationToken);
        return stores;
    }
}

/// <summary>One database on the fixture's server; each unit of work is a fresh <see cref="MongoSagaUnitOfWork"/>, as a DI scope would give it.</summary>
public sealed class MongoProviderStores : IProviderStores
{
    private readonly Func<ValueTask> _release;

    public MongoProviderStores(MongoConnection connection, VSagaMongoOptions options, Func<ValueTask> release)
    {
        Connection = connection;
        Options = options;
        Collections = new MongoCollections(connection);
        Writes = new MongoPersistWrites(Collections);
        Probe = new MongoServerProbe(connection, Collections, options);
        _release = release;
    }

    public MongoConnection Connection { get; }

    public VSagaMongoOptions Options { get; }

    public MongoCollections Collections { get; }

    public MongoPersistWrites Writes { get; }

    public MongoServerProbe Probe { get; }

    public Task<IStoreUnitOfWork> BeginAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IStoreUnitOfWork>(new MongoConformanceUnitOfWork(this));

    public ValueTask DisposeAsync() => _release();
}

/// <summary>
/// The seven stores over one <see cref="MongoSagaUnitOfWork"/> -- the shape <c>AddVSagaMongoDb</c> registers
/// per scope. <see cref="CommitAsync"/> commits the staged rows alone, the one way to make a staged row
/// durable without a snapshot write; <see cref="AbandonAsync"/> drops what is staged.
/// </summary>
internal sealed class MongoConformanceUnitOfWork(MongoProviderStores stores) : IStoreUnitOfWork
{
    private readonly MongoSagaUnitOfWork _unitOfWork = new();
    private readonly MongoSagaSummaryReader _summaryReader = new(stores.Collections);

    public ISagaSnapshotStore<TState> Snapshots<TState>() where TState : SagaState =>
        new MongoSagaSnapshotStore<TState>(stores.Collections, stores.Writes, _unitOfWork);

    public ISagaEventLogStore EventLog { get; } = new MongoSagaEventLogStore(stores.Collections, stores.Options, NullLogger<MongoSagaEventLogStore>.Instance);

    public ISagaTimeoutStore Timeouts { get; } = new MongoSagaTimeoutStore(stores.Collections);

    public ISagaOutboxStore Outbox => new MongoSagaOutboxStore(stores.Collections, _unitOfWork);

    public ISagaSummaryReader Summaries => _summaryReader;

    public ISagaAdminStore Admin => _summaryReader;

    public IServiceTopologyStore Topology { get; } = new MongoServiceTopologyStore(stores.Collections);

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        _unitOfWork.Staged.Count == 0 ? Task.CompletedTask : stores.Writes.CommitStagedAsync(_unitOfWork, cancellationToken);

    public Task AbandonAsync(CancellationToken cancellationToken = default)
    {
        _unitOfWork.Clear();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[CollectionDefinition(Name)]
public sealed class MongoConformanceGroup : ICollectionFixture<MongoProviderFixture>
{
    public const string Name = "MongoDB conformance";
}
