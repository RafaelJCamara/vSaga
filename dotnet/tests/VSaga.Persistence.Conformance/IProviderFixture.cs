using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.Conformance;

/// <summary>
/// What a persistence provider's test project implements to run this suite against its stores: a way
/// to get a fresh, isolated backing store, and what the provider declares about itself.
/// </summary>
/// <remarks>
/// The capability flags are declarations, not detections. A provider states them; cases that only mean
/// something with a capability live in their own suite (<see cref="ConcurrentClaimConformanceTests"/>,
/// <see cref="AtomicUnitOfWorkConformanceTests"/>) that a provider derives exactly when it declares it,
/// and the few assertions that legitimately differ by a flag branch on it. The provider's test project
/// pins the declared values in a test of its own, alongside
/// <see cref="ConformanceCoverage.AssertEverySuiteIsDerived"/> — so a flag can only ever change in a diff
/// a reviewer sees, never quietly to make a failing case go away.
/// </remarks>
public interface IProviderFixture
{
    /// <summary>
    /// True when a row staged by <see cref="ISagaOutboxStore.EnqueueAsync"/> stays invisible outside
    /// its unit of work until a commit inside it, and is dropped if the unit of work ends without one —
    /// <see cref="ISagaOutboxStore"/>'s staged-row lifecycle in full. False for a provider whose
    /// <c>EnqueueAsync</c> writes immediately because it has no unit of work to enlist in (the in-memory
    /// provider), which leaves open the crash window the outbox exists to close.
    /// </summary>
    bool SupportsAtomicUnitOfWork { get; }

    /// <summary>
    /// True when <see cref="ISagaTimeoutStore.ClaimDueAsync"/> and
    /// <see cref="ISagaOutboxStore.ClaimPendingAsync"/> are safe for several dispatcher instances
    /// racing on the same rows: no row is ever returned by two claims. False for a provider that is
    /// correct for a single dispatcher only (EF Core's load-then-update fallback on any database but
    /// Postgres — ADR 0004).
    /// </summary>
    bool SupportsConcurrentClaim { get; }

    /// <summary>
    /// The resolution at which this provider stores a <see cref="DateTimeOffset"/> in a projected or
    /// row field (Postgres: one microsecond; a provider that keeps full ticks: one tick). Such a
    /// timestamp read back is compared within this, never exactly. A state blob keeps whatever
    /// <c>System.Text.Json</c> writes, which is full precision everywhere.
    /// </summary>
    TimeSpan TimestampResolution { get; }

    /// <summary>A fresh, empty backing store: nothing written through one is ever visible through another.</summary>
    Task<IProviderStores> CreateStoresAsync(CancellationToken cancellationToken = default);
}

/// <summary>One isolated backing store. Disposing it releases whatever backs it.</summary>
public interface IProviderStores : IAsyncDisposable
{
    /// <summary>
    /// Begins one unit of work. Every store it hands out shares it, exactly as <c>VSaga.Core</c>
    /// resolves all of them from one DI scope per message, timeout or retry. Several may be open at
    /// once, as several messages may be in flight.
    /// </summary>
    Task<IStoreUnitOfWork> BeginAsync(CancellationToken cancellationToken = default);
}

/// <summary>The seven store contracts over one shared unit of work, plus the hooks that end it.</summary>
public interface IStoreUnitOfWork : IAsyncDisposable
{
    ISagaSnapshotStore<TState> Snapshots<TState>() where TState : SagaState;

    ISagaEventLogStore EventLog { get; }

    ISagaTimeoutStore Timeouts { get; }

    ISagaOutboxStore Outbox { get; }

    ISagaSummaryReader Summaries { get; }

    ISagaAdminStore Admin { get; }

    IServiceTopologyStore Topology { get; }

    /// <summary>
    /// Commits whatever this unit of work still holds staged — the suite's own way to make a staged
    /// outbox row durable without borrowing some store's commit. A no-op for a provider that never
    /// stages anything.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the unit of work without committing: anything still staged is dropped, as if processing
    /// the message had died at this point. The unit of work is not used again afterwards.
    /// </summary>
    Task AbandonAsync(CancellationToken cancellationToken = default);
}
