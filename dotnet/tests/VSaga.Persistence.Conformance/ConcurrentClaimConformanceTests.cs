using VSaga.Abstractions.Persistence;

namespace VSaga.Persistence.Conformance;

/// <summary>
/// Cases only a provider declaring <see cref="IProviderFixture.SupportsConcurrentClaim"/> runs: dispatcher
/// replicas racing on the same rows. Derive it for such a provider only — the constructor refuses any
/// other, and <see cref="ConformanceCoverage.AssertEverySuiteIsDerived"/> fails a provider that declares
/// the capability without deriving it, so the declaration and the coverage cannot drift apart.
/// </summary>
/// <remarks>
/// Each claim marks its rows terminal, so a row two replicas both claim is a timeout fired twice or a
/// message published twice. The race is arranged rather than hoped for: every worker runs on a thread of
/// its own and waits at a barrier until all of them are there, then keeps claiming small batches until
/// one comes back empty. A provider whose claims complete synchronously therefore still has several
/// claims executing at the same moment, instead of the first worker quietly draining every row before
/// the others start.
/// </remarks>
public abstract class ConcurrentClaimConformanceTests(IProviderFixture fixture) : StoreConformanceTests(Require(fixture))
{
    private const int Rows = 120;
    private const int Workers = 8;
    private const int BatchSize = 5;

    private static IProviderFixture Require(IProviderFixture fixture) =>
        fixture.SupportsConcurrentClaim
            ? fixture
            : throw new InvalidOperationException($"{fixture.GetType().Name} does not declare SupportsConcurrentClaim, so it must not derive {nameof(ConcurrentClaimConformanceTests)}.");

    [Fact]
    public async Task ClaimDue_RacingDispatchers_ClaimEveryDueRowExactlyOnce()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        await using (var uow = await stores.BeginAsync())
        {
            for (var i = 0; i < Rows; i++)
                await uow.Timeouts.ScheduleAsync("OrderSaga", Guid.NewGuid(), "AwaitingPayment", T0);
        }

        var claimed = await RaceAsync(stores, async uow =>
            (await uow.Timeouts.ClaimDueAsync(T0, BatchSize)).Select(t => t.Id).ToList());

        Assert.Equal(Rows, claimed.Count);
        Assert.Equal(Rows, claimed.Distinct().Count());
    }

    [Fact]
    public async Task ClaimPending_RacingDispatchers_ClaimEveryPendingRowExactlyOnce()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var seeded = Enumerable.Range(0, Rows).Select(i => $"m{i}").ToList();
        await using (var uow = await stores.BeginAsync())
        {
            foreach (var messageId in seeded)
                await EnqueueAsync(uow, messageId, T0);
            await uow.CommitAsync();
        }

        var claimed = await RaceAsync(stores, async uow =>
            (await uow.Outbox.ClaimPendingAsync(T0, BatchSize)).Select(m => m.MessageId).ToList());

        Assert.Equal(seeded.Order(StringComparer.Ordinal), claimed.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    /// <summary>
    /// Starts every worker on a dedicated thread, releases them together from a barrier, and has each claim
    /// in its own unit of work until a claim comes back empty. A worker stops after <see cref="Rows"/>
    /// rounds regardless: needing more non-empty batches than there are rows means rows were handed out
    /// twice, and the assertions should say so rather than the run hang on a claim that never marks rows.
    /// </summary>
    private static async Task<List<T>> RaceAsync<T>(IProviderStores stores, Func<IStoreUnitOfWork, Task<List<T>>> claimBatch)
    {
        using var barrier = new Barrier(Workers);
        var workers = Enumerable.Range(0, Workers).Select(_ => Task.Factory.StartNew(async () =>
        {
            var mine = new List<T>();
            barrier.SignalAndWait();
            for (var round = 0; round < Rows; round++)
            {
                await using var uow = await stores.BeginAsync();
                var batch = await claimBatch(uow);
                if (batch.Count == 0)
                    break;
                mine.AddRange(batch);
            }

            return mine;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToList();

        return (await Task.WhenAll(workers)).SelectMany(claimed => claimed).ToList();
    }
}
