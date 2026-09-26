using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.Conformance;

/// <summary>
/// Shared plumbing for the per-contract suites. Each derived abstract class covers one contract in
/// <c>VSaga.Abstractions.Persistence</c>; a provider runs the suite by deriving one concrete class per
/// contract in its own test project and handing each its <see cref="IProviderFixture"/>.
/// </summary>
/// <remarks>
/// Every case creates its own <see cref="IProviderStores"/>, so no case can observe another's rows —
/// which matters for the claim methods, whose contract is global rather than per-instance. All writes
/// go through the contracts themselves; no case reaches into a provider's storage.
/// </remarks>
public abstract class StoreConformanceTests(IProviderFixture fixture)
{
    /// <summary>
    /// A fixed origin every case offsets its timestamps from — microsecond-aligned, so offsets in whole
    /// ticks are the only thing that exercises sub-microsecond precision, and deliberately in the past, so
    /// a store that stamps its own clock instead of keeping the caller's value is caught by any case
    /// comparing against it.
    /// </summary>
    protected static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    protected IProviderFixture Fixture { get; } = fixture;

    protected static ConformanceSagaState NewState(
        string sagaType,
        Guid? correlationId = null,
        string currentState = "Started",
        SagaStatus status = SagaStatus.Running,
        DateTimeOffset? updatedAtUtc = null,
        string? businessKey = null) => new()
        {
            CorrelationId = correlationId ?? Guid.NewGuid(),
            SagaType = sagaType,
            Kind = SagaKind.Orchestrated,
            CurrentState = currentState,
            Status = status,
            BusinessKey = businessKey,
            CreatedAtUtc = T0,
            UpdatedAtUtc = updatedAtUtc ?? T0,
        };

    /// <summary>Inserts each state in a unit of work of its own, as separate messages would.</summary>
    protected static async Task InsertAsync(IProviderStores stores, params ConformanceSagaState[] states)
    {
        foreach (var state in states)
        {
            await using var uow = await stores.BeginAsync();
            await uow.Snapshots<ConformanceSagaState>().InsertAsync(state);
        }
    }

    protected static async Task<ConformanceSagaState?> FindAsync(IProviderStores stores, string sagaType, Guid correlationId)
    {
        await using var uow = await stores.BeginAsync();
        return await uow.Snapshots<ConformanceSagaState>().FindAsync(sagaType, correlationId);
    }

    protected static async Task<SagaSummary?> GetSummaryAsync(IProviderStores stores, string sagaType, Guid correlationId)
    {
        await using var uow = await stores.BeginAsync();
        return await uow.Summaries.GetAsync(sagaType, correlationId);
    }

    protected static IReadOnlyDictionary<string, string> NoHeaders() => new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Stages one outbox row with an empty JSON body, created at <paramref name="createdAtUtc"/>.</summary>
    protected static Task EnqueueAsync(IStoreUnitOfWork uow, string messageId, DateTimeOffset createdAtUtc, Guid? correlationId = null) =>
        uow.Outbox.EnqueueAsync("OrderSaga", correlationId ?? Guid.NewGuid(), messageId, "InventoryReserved", "{}"u8.ToArray(),
            destination: null, NoHeaders(), createdAtUtc);

    /// <summary>Claims every Pending row created at or before <paramref name="olderThan"/>, in a unit of work of its own.</summary>
    protected static async Task<IReadOnlyList<SagaOutboxMessage>> ClaimAllPendingAsync(IProviderStores stores, DateTimeOffset olderThan)
    {
        await using var uow = await stores.BeginAsync();
        return await uow.Outbox.ClaimPendingAsync(olderThan, batchSize: 1000);
    }

    /// <summary>
    /// Runs <paramref name="workers"/> copies of <paramref name="work"/> truly at once: each on a thread of
    /// its own, released together from a barrier. A provider whose calls complete synchronously would
    /// otherwise let the first worker finish before the next one starts, and the case would race nothing.
    /// </summary>
    protected static async Task<IReadOnlyList<T>> RunTogetherAsync<T>(int workers, Func<Task<IReadOnlyList<T>>> work)
    {
        using var barrier = new Barrier(workers);
        var running = Enumerable.Range(0, workers).Select(_ => Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            return work();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToList();

        return (await Task.WhenAll(running)).SelectMany(results => results).ToList();
    }

    /// <summary>
    /// Asserts two timestamps name the same instant at the provider's declared
    /// <see cref="IProviderFixture.TimestampResolution"/> — never exact equality, because a provider may
    /// legitimately store coarser than a tick (Postgres keeps microseconds).
    /// </summary>
    protected void AssertSameInstant(DateTimeOffset expected, DateTimeOffset actual) =>
        Assert.True((expected - actual).Duration() < Fixture.TimestampResolution,
            $"Expected {expected:O} within {Fixture.TimestampResolution} but got {actual:O}.");
}
