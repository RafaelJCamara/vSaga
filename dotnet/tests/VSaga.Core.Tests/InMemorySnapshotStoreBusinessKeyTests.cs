using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Persistence.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Core.Tests;

/// <summary>
/// What the in-memory snapshot store keeps of its BusinessKey tests (production-readiness §5.2, item 12)
/// after the provider-neutral conformance suite (<c>SnapshotStoreConformanceTests</c>, run for this provider
/// by <c>InMemorySnapshotStoreConformanceTests</c>) took over the rest: a case the suite does not make. The
/// reservation index is a second dictionary in this provider, and an update that leaves the key alone must
/// not try to reserve it a second time against its own holder. Every suite case that updates a keyed saga
/// changes the key, so none of them would notice. See persistence-contracts.md Q4.
/// </summary>
public sealed class InMemorySnapshotStoreBusinessKeyTests
{
    private sealed class TestState : SagaState
    {
        public string? OrderId { get; set; }
    }

    private static ISagaSnapshotStore<TestState> NewStore()
    {
        var services = new ServiceCollection();
        services.AddVSagaInMemoryPersistence();
        return services.BuildServiceProvider().GetRequiredService<ISagaSnapshotStore<TestState>>();
    }

    [Fact]
    public async Task Update_KeepsBusinessKeyResolvableAfterAnUnrelatedFieldChanges()
    {
        var store = NewStore();
        var correlationId = Guid.NewGuid();

        await store.InsertAsync(new TestState
        {
            CorrelationId = correlationId,
            SagaType = "OrderSaga",
            CurrentState = "Submitted",
            OrderId = "ORD-BK-5",
            BusinessKey = "ORD-BK-5",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        var state = await store.FindByBusinessKeyAsync("OrderSaga", "ORD-BK-5");
        state!.CurrentState = "Completed";
        await store.UpdateAsync(state, expectedVersion: 0);

        var reloaded = await store.FindByBusinessKeyAsync("OrderSaga", "ORD-BK-5");
        Assert.NotNull(reloaded);
        Assert.Equal(correlationId, reloaded.CorrelationId);
        Assert.Equal("Completed", reloaded.CurrentState);
        Assert.Equal(1, reloaded.Version);
    }
}
