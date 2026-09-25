using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.Conformance;

/// <summary>
/// The half of clause 4 only a provider declaring <see cref="IProviderFixture.SupportsAtomicUnitOfWork"/>
/// can honour: a staged outbox row that never meets a successful commit never becomes durable. Derive it
/// for such a provider only — the constructor refuses any other, and
/// <see cref="ConformanceCoverage.AssertEverySuiteIsDerived"/> fails a provider that declares the capability
/// without deriving it.
/// </summary>
public abstract class AtomicUnitOfWorkConformanceTests(IProviderFixture fixture) : StoreConformanceTests(Require(fixture))
{
    private static IProviderFixture Require(IProviderFixture fixture) =>
        fixture.SupportsAtomicUnitOfWork
            ? fixture
            : throw new InvalidOperationException($"{fixture.GetType().Name} does not declare SupportsAtomicUnitOfWork, so it must not derive {nameof(AtomicUnitOfWorkConformanceTests)}.");

    /// <summary>A unit of work that ends with rows still staged must not leave them durable.</summary>
    [Fact]
    public async Task StagedRow_InAnAbandonedUnitOfWork_NeverBecomesDurable()
    {
        await using var stores = await Fixture.CreateStoresAsync();

        await using (var uow = await stores.BeginAsync())
        {
            await EnqueueAsync(uow, "m1", T0);
            Assert.Empty(await ClaimAllPendingAsync(stores, T0.AddMinutes(1)));
            await uow.AbandonAsync();
        }

        Assert.Empty(await ClaimAllPendingAsync(stores, T0.AddMinutes(1)));
    }

    /// <summary>
    /// A persist that throws leaves staged rows uncommitted — the failure a staged row exists to survive.
    /// The live object already persisted once in this unit of work (the timeout's claim-then-commit shape)
    /// and another unit of work wrote in between. A store that keeps the row it already wrote (EF Core's
    /// identity map) only discovers that at the commit itself; one that re-reads the row rejects it up
    /// front. Either way the row must not be out, and ending the unit of work drops it.
    /// </summary>
    [Fact]
    public async Task StagedRow_IsNotCommittedByAPersistThatLosesItsRace()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga");
        await InsertAsync(stores, state);

        await using (var uow = await stores.BeginAsync())
        {
            var store = uow.Snapshots<ConformanceSagaState>();
            var live = (await store.FindAsync("OrderSaga", state.CorrelationId))!;
            await store.UpdateAsync(live, live.Version);

            await using (var rival = await stores.BeginAsync())
            {
                var other = (await rival.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
                await rival.Snapshots<ConformanceSagaState>().UpdateAsync(other, other.Version);
            }

            await EnqueueAsync(uow, "m1", T0, state.CorrelationId);
            await Assert.ThrowsAsync<SagaConcurrencyException>(() => store.UpdateAsync(live, live.Version));
            Assert.Empty(await ClaimAllPendingAsync(stores, T0.AddMinutes(1)));

            await uow.AbandonAsync();
        }

        Assert.Empty(await ClaimAllPendingAsync(stores, T0.AddMinutes(1)));
    }
}
