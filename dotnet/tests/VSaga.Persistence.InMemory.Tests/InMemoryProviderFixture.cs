using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Persistence.Conformance;
using VSaga.Persistence.InMemory;

namespace VSaga.Persistence.InMemory.Tests;

/// <summary>The in-memory provider: a fresh <see cref="InMemorySagaStore"/> per case.</summary>
public sealed class InMemoryProviderFixture : IProviderFixture
{
    /// <summary>
    /// <c>EnqueueAsync</c> writes immediately — a <c>ConcurrentDictionary</c> has no unit of work to enlist
    /// in, so the crash window production-readiness.md §4.4 documents stays open on this provider.
    /// </summary>
    public bool SupportsAtomicUnitOfWork => false;

    /// <summary>Each row is claimed by a compare-and-swap on its own entry, so racing claims cannot both win one.</summary>
    public bool SupportsConcurrentClaim => true;

    /// <summary>Nothing is converted on the way in: a <see cref="DateTimeOffset"/> is kept as given.</summary>
    public TimeSpan TimestampResolution => TimeSpan.FromTicks(1);

    public Task<IProviderStores> CreateStoresAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IProviderStores>(new InMemoryProviderStores(new InMemorySagaStore()));
}

internal sealed class InMemoryProviderStores(InMemorySagaStore store) : IProviderStores
{
    public Task<IStoreUnitOfWork> BeginAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IStoreUnitOfWork>(new InMemoryUnitOfWork(store));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Every unit of work is a view over the same singleton, exactly as <c>AddVSagaInMemoryPersistence</c>
/// registers it: this provider never stages anything, so there is nothing for a unit of work to hold,
/// commit or drop.
/// </summary>
internal sealed class InMemoryUnitOfWork(InMemorySagaStore store) : IStoreUnitOfWork
{
    public ISagaSnapshotStore<TState> Snapshots<TState>() where TState : SagaState => new InMemorySagaSnapshotStore<TState>(store);

    public ISagaEventLogStore EventLog => store;

    public ISagaTimeoutStore Timeouts => store;

    public ISagaOutboxStore Outbox => store;

    public ISagaSummaryReader Summaries => store;

    public ISagaAdminStore Admin => store;

    public IServiceTopologyStore Topology => store;

    public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AbandonAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
