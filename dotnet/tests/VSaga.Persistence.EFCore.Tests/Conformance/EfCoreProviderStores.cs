using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Persistence.Conformance;
using Microsoft.EntityFrameworkCore;

namespace VSaga.Persistence.EFCore.Tests.Conformance;

/// <summary>One isolated database, whichever relational provider backs it; each unit of work is a fresh <see cref="VSagaDbContext"/>.</summary>
internal sealed class EfCoreProviderStores(DbContextOptions<VSagaDbContext> options, Func<ValueTask> release) : IProviderStores
{
    public Task<IStoreUnitOfWork> BeginAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IStoreUnitOfWork>(new EfCoreUnitOfWork(new VSagaDbContext(options)));

    public ValueTask DisposeAsync() => release();
}

/// <summary>
/// Every store over one shared <see cref="VSagaDbContext"/> — the same shape
/// <c>AddVSagaEfCore</c> registers, where the context is scoped per message and each store takes it by
/// constructor. That sharing is what makes this provider's unit of work atomic: a staged outbox row is
/// an Added entity any store's <c>SaveChangesAsync</c> flushes.
/// </summary>
internal sealed class EfCoreUnitOfWork : IStoreUnitOfWork
{
    private readonly VSagaDbContext _db;
    private readonly EfCoreSagaSummaryReader _summaryReader;

    public EfCoreUnitOfWork(VSagaDbContext db)
    {
        _db = db;
        _summaryReader = new EfCoreSagaSummaryReader(db);
        EventLog = new EfCoreSagaEventLogStore(db);
        Timeouts = new EfCoreSagaTimeoutStore(db);
        Outbox = new EfCoreSagaOutboxStore(db);
        Topology = new EfCoreServiceTopologyStore(db);
    }

    public ISagaSnapshotStore<TState> Snapshots<TState>() where TState : SagaState => new EfCoreSagaSnapshotStore<TState>(_db);

    public ISagaEventLogStore EventLog { get; }

    public ISagaTimeoutStore Timeouts { get; }

    public ISagaOutboxStore Outbox { get; }

    public ISagaSummaryReader Summaries => _summaryReader;

    public ISagaAdminStore Admin => _summaryReader;

    public IServiceTopologyStore Topology { get; }

    public Task CommitAsync(CancellationToken cancellationToken = default) => _db.SaveChangesAsync(cancellationToken);

    public Task AbandonAsync(CancellationToken cancellationToken = default)
    {
        _db.ChangeTracker.Clear();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
