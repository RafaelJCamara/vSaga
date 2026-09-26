using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.Conformance;

/// <summary>Conformance cases for <see cref="ISagaAdminStore"/>.</summary>
public abstract class AdminStoreConformanceTests(IProviderFixture fixture) : StoreConformanceTests(fixture)
{
    /// <summary>
    /// Clause 7: the reset reaches the blob, not only the projection — snapshot reads deserialise the blob,
    /// so a projection-only reset would have the engine resume from the very state the operator reset it
    /// away from — while the saga's business fields stay exactly as they were.
    /// </summary>
    [Fact]
    public async Task Reset_PatchesStateAndStatusInBothBlobAndProjection_LeavingBusinessFieldsUntouched()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var parentId = Guid.NewGuid();
        var state = NewState("OrderSaga", currentState: "PaymentFailed", status: SagaStatus.Failed, businessKey: "ORD-1");
        state.ParentSagaType = "FulfilmentSaga";
        state.ParentCorrelationId = parentId;
        state.OrderId = "ORD-1";
        state.Amount = 12.50m;
        await InsertAsync(stores, state);

        await using (var uow = await stores.BeginAsync())
            await uow.Admin.ResetStateAsync("OrderSaga", state.CorrelationId, "AwaitingPayment", SagaStatus.Running, expectedVersion: 0, T0.AddMinutes(5));

        var blob = await FindAsync(stores, "OrderSaga", state.CorrelationId);
        var summary = await GetSummaryAsync(stores, "OrderSaga", state.CorrelationId);

        Assert.Equal("AwaitingPayment", blob!.CurrentState);
        Assert.Equal(SagaStatus.Running, blob.Status);
        Assert.Equal("AwaitingPayment", summary!.CurrentState);
        Assert.Equal(SagaStatus.Running, summary.Status);
        Assert.Equal(1, summary.Version);

        Assert.Equal("ORD-1", blob.OrderId);
        Assert.Equal(12.50m, blob.Amount);
        Assert.Equal("ORD-1", blob.BusinessKey);
        Assert.Equal("FulfilmentSaga", blob.ParentSagaType);
        Assert.Equal(parentId, blob.ParentCorrelationId);
        Assert.Equal(T0, blob.CreatedAtUtc);
    }

    /// <summary>
    /// Clause 7 (fixes F3, F4): the reset's timestamp is the caller's — from its <c>TimeProvider</c> — not
    /// the store's own clock. Sub-microsecond ticks on purpose, compared at storage resolution.
    /// </summary>
    [Fact]
    public async Task Reset_StampsTheCallersUpdatedAtUtc()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga", currentState: "PaymentFailed", status: SagaStatus.Failed);
        await InsertAsync(stores, state);
        var resetAt = T0.AddMinutes(5).AddTicks(1_234_567);

        await using (var uow = await stores.BeginAsync())
            await uow.Admin.ResetStateAsync("OrderSaga", state.CorrelationId, "AwaitingPayment", SagaStatus.Running, expectedVersion: 0, resetAt);

        AssertSameInstant(resetAt, (await GetSummaryAsync(stores, "OrderSaga", state.CorrelationId))!.UpdatedAtUtc);
    }

    /// <summary>
    /// Clause 7 (fixes F3, F4): <c>CurrentState</c>, <c>Status</c>, <c>Version</c> and <c>UpdatedAtUtc</c>
    /// are patched inside the blob in lockstep with the projection, so what the engine reads back through
    /// Find is what the dashboard shows. The timestamps are compared at storage resolution, since a
    /// projected column may be coarser than the blob's full ticks.
    /// </summary>
    [Fact]
    public async Task Reset_KeepsTheBlobInLockstepWithTheProjection()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga", currentState: "PaymentFailed", status: SagaStatus.Failed);
        await InsertAsync(stores, state);

        await using (var uow = await stores.BeginAsync())
        {
            await uow.Admin.ResetStateAsync("OrderSaga", state.CorrelationId, "AwaitingPayment", SagaStatus.Running,
                expectedVersion: 0, T0.AddMinutes(5).AddTicks(1_234_567));
        }

        var blob = await FindAsync(stores, "OrderSaga", state.CorrelationId);
        var summary = await GetSummaryAsync(stores, "OrderSaga", state.CorrelationId);

        Assert.Equal(summary!.CurrentState, blob!.CurrentState);
        Assert.Equal(summary.Status, blob.Status);
        Assert.Equal(1, summary.Version);
        Assert.Equal(summary.Version, blob.Version);
        AssertSameInstant(blob.UpdatedAtUtc, summary.UpdatedAtUtc);
    }

    /// <summary>
    /// Clause 7 (fixes F3, F4): version-guarded. An operator resetting a saga that moved on since the
    /// dashboard rendered it is told, rather than having the reset silently clobber that progress.
    /// </summary>
    [Fact]
    public async Task Reset_AgainstAVersionTheSagaHasMovedPast_ThrowsAndChangesNothing()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga", currentState: "PaymentFailed", status: SagaStatus.Failed);
        await InsertAsync(stores, state);

        await using (var uow = await stores.BeginAsync())
        {
            var live = (await uow.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
            live.CurrentState = "Moved";
            await uow.Snapshots<ConformanceSagaState>().UpdateAsync(live, live.Version);
        }

        await using (var uow = await stores.BeginAsync())
        {
            var thrown = await Assert.ThrowsAsync<SagaConcurrencyException>(() => uow.Admin.ResetStateAsync(
                "OrderSaga", state.CorrelationId, "AwaitingPayment", SagaStatus.Running, expectedVersion: 0, T0.AddMinutes(5)));
            Assert.Equal(0, thrown.ExpectedVersion);
        }

        var summary = await GetSummaryAsync(stores, "OrderSaga", state.CorrelationId);
        Assert.Equal("Moved", summary!.CurrentState);
        Assert.Equal(1, summary.Version);
        Assert.Equal("Moved", (await FindAsync(stores, "OrderSaga", state.CorrelationId))!.CurrentState);
    }

    /// <summary>
    /// Clause 7 (fixes F3, F4), the same guard where the race only shows at the write: the reset's unit of
    /// work has already persisted the instance, and another unit of work wrote in between. A store that
    /// keeps the row it already holds (EF Core's identity map) cannot see the race until it writes — and
    /// must still report it as <see cref="SagaConcurrencyException"/>, the one type the dashboard maps to a
    /// 409, not as a provider exception.
    /// </summary>
    [Fact]
    public async Task Reset_ThatLosesARaceAtTheWrite_ThrowsSagaConcurrency()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga", currentState: "PaymentFailed", status: SagaStatus.Failed);
        await InsertAsync(stores, state);

        await using (var uow = await stores.BeginAsync())
        {
            var live = (await uow.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
            await uow.Snapshots<ConformanceSagaState>().UpdateAsync(live, live.Version);

            await using (var rival = await stores.BeginAsync())
            {
                var other = (await rival.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
                other.CurrentState = "Moved";
                await rival.Snapshots<ConformanceSagaState>().UpdateAsync(other, other.Version);
            }

            await Assert.ThrowsAsync<SagaConcurrencyException>(() => uow.Admin.ResetStateAsync(
                "OrderSaga", state.CorrelationId, "AwaitingPayment", SagaStatus.Running, expectedVersion: 1, T0.AddMinutes(5)));
        }

        var summary = await GetSummaryAsync(stores, "OrderSaga", state.CorrelationId);
        Assert.Equal("Moved", summary!.CurrentState);
        Assert.Equal(2, summary.Version);
    }

    /// <summary>
    /// Why the blob's version matters (fix F3): after a retry the engine reads the state back through Find
    /// and persists with the version it finds there. A stale one turns the saga's first step after the
    /// reset into a concurrency failure against a write nobody raced.
    /// </summary>
    [Fact]
    public async Task Reset_ThenAPersistFromTheReadBackState_Succeeds()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga", currentState: "PaymentFailed", status: SagaStatus.Failed);
        await InsertAsync(stores, state);

        await using (var uow = await stores.BeginAsync())
            await uow.Admin.ResetStateAsync("OrderSaga", state.CorrelationId, "AwaitingPayment", SagaStatus.Running, expectedVersion: 0, T0.AddMinutes(5));

        await using (var uow = await stores.BeginAsync())
        {
            var live = (await uow.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
            live.CurrentState = "Paid";
            await uow.Snapshots<ConformanceSagaState>().UpdateAsync(live, live.Version);
        }

        var reloaded = await FindAsync(stores, "OrderSaga", state.CorrelationId);
        Assert.Equal("Paid", reloaded!.CurrentState);
        Assert.Equal(2, reloaded.Version);
    }

    [Fact]
    public async Task Reset_OnlyTouchesItsOwnSagaTypesInstance()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();
        await InsertAsync(stores,
            NewState("OrderSaga", correlationId, currentState: "PaymentFailed", status: SagaStatus.Failed),
            NewState("ShippingChoreography", correlationId, currentState: "Tracking"));

        await using (var uow = await stores.BeginAsync())
            await uow.Admin.ResetStateAsync("OrderSaga", correlationId, "AwaitingPayment", SagaStatus.Running, expectedVersion: 0, T0.AddMinutes(5));

        var untouched = await GetSummaryAsync(stores, "ShippingChoreography", correlationId);
        Assert.Equal("Tracking", untouched!.CurrentState);
        Assert.Equal(0, untouched.Version);
        Assert.Equal("Tracking", (await FindAsync(stores, "ShippingChoreography", correlationId))!.CurrentState);
    }
}
