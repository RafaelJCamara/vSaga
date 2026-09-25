using VSaga.Abstractions.Persistence;

namespace VSaga.Persistence.Conformance;

/// <summary>Conformance cases for <see cref="IServiceTopologyStore"/>.</summary>
public abstract class ServiceTopologyStoreConformanceTests(IProviderFixture fixture) : StoreConformanceTests(fixture)
{
    private static async Task<IReadOnlyList<ServiceTopologyEntry>> GetAllAsync(IProviderStores stores)
    {
        await using var uow = await stores.BeginAsync();
        return await uow.Topology.GetAllAsync();
    }

    [Fact]
    public async Task Record_ThenGetAll_ReturnsEachBinding()
    {
        await using var stores = await Fixture.CreateStoresAsync();

        await using (var uow = await stores.BeginAsync())
        {
            await uow.Topology.RecordAsync("InventoryService", "ReserveInventory", "vsaga.participant.inventory", T0.AddTicks(1_234_567));
            await uow.Topology.RecordAsync("PaymentService", "ChargePayment", "vsaga.participant.payment", T0);
        }

        var entries = (await GetAllAsync(stores)).OrderBy(e => e.ServiceName, StringComparer.Ordinal).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Equal(("InventoryService", "ReserveInventory", "vsaga.participant.inventory"), (entries[0].ServiceName, entries[0].MessageType, entries[0].QueueName));
        AssertSameInstant(T0.AddTicks(1_234_567), entries[0].LastSeenAtUtc);
        Assert.Equal(("PaymentService", "ChargePayment", "vsaga.participant.payment"), (entries[1].ServiceName, entries[1].MessageType, entries[1].QueueName));
    }

    /// <summary>An upsert keyed on (ServiceName, MessageType): seeing a known binding again refreshes it rather than adding a second.</summary>
    [Fact]
    public async Task Record_ForAKnownBinding_RefreshesItInPlace()
    {
        await using var stores = await Fixture.CreateStoresAsync();

        await using (var uow = await stores.BeginAsync())
            await uow.Topology.RecordAsync("InventoryService", "ReserveInventory", "old-queue", T0);
        await using (var uow = await stores.BeginAsync())
            await uow.Topology.RecordAsync("InventoryService", "ReserveInventory", "new-queue", T0.AddMinutes(1));

        var entry = Assert.Single(await GetAllAsync(stores));
        Assert.Equal("new-queue", entry.QueueName);
        AssertSameInstant(T0.AddMinutes(1), entry.LastSeenAtUtc);
    }
}
