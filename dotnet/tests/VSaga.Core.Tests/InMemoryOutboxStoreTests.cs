using VSaga.Abstractions.Persistence;
using VSaga.Persistence.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Core.Tests;

/// <summary>
/// What the in-memory outbox store keeps of its own tests after the provider-neutral conformance suite
/// (<c>OutboxStoreConformanceTests</c>, run for this provider by <c>InMemoryOutboxStoreConformanceTests</c>)
/// took over the rest: a case the suite does not make. Its round-trip enqueues an addressed row, so a store
/// that returned a null destination as empty text would pass it; this one enqueues a broadcast and checks it
/// comes back null, which is what the dispatcher branches on. See persistence-contracts.md Q4.
/// </summary>
public sealed class InMemoryOutboxStoreTests
{
    private static ISagaOutboxStore NewStore()
    {
        var services = new ServiceCollection();
        services.AddVSagaInMemoryPersistence();
        return services.BuildServiceProvider().GetRequiredService<ISagaOutboxStore>();
    }

    [Fact]
    public async Task EnqueueRoundTripsEveryFieldAndClaimNeverReturnsItTwice()
    {
        var store = NewStore();
        var correlationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["x-vsaga-source-service"] = "OrderSaga" };
        var body = "{\"OrderId\":\"ORD-1\"}"u8.ToArray();

        await store.EnqueueAsync("OrderSaga", correlationId, "m1", "InventoryReserved", body, destination: null, headers, now.AddMinutes(-1));

        var claimed = Assert.Single(await store.ClaimPendingAsync(now, batchSize: 10));
        Assert.Equal(correlationId, claimed.CorrelationId);
        Assert.Equal("OrderSaga", claimed.SagaType);
        Assert.Equal("m1", claimed.MessageId);
        Assert.Equal("InventoryReserved", claimed.MessageTypeName);
        Assert.Equal(body, claimed.Body.ToArray());
        Assert.Null(claimed.Destination);
        Assert.Equal("OrderSaga", claimed.Headers["x-vsaga-source-service"]);
        Assert.Equal(SagaOutboxStatus.Dispatched, claimed.Status);

        Assert.Empty(await store.ClaimPendingAsync(now, batchSize: 10)); // already dispatched, shouldn't be claimed twice
    }
}
