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
