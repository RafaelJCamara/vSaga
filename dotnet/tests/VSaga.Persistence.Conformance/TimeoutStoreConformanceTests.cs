using VSaga.Abstractions.Persistence;

namespace VSaga.Persistence.Conformance;

/// <summary>Conformance cases for <see cref="ISagaTimeoutStore"/>.</summary>
public abstract class TimeoutStoreConformanceTests(IProviderFixture fixture) : StoreConformanceTests(fixture)
{
    private static async Task ScheduleAsync(IProviderStores stores, string sagaType, Guid correlationId, string forState, DateTimeOffset dueAtUtc)
    {
        await using var uow = await stores.BeginAsync();
        await uow.Timeouts.ScheduleAsync(sagaType, correlationId, forState, dueAtUtc);
    }

    private static async Task<IReadOnlyList<SagaTimeout>> ClaimDueAsync(IProviderStores stores, DateTimeOffset asOf, int batchSize = 1000)
    {
        await using var uow = await stores.BeginAsync();
        return await uow.Timeouts.ClaimDueAsync(asOf, batchSize);
    }

    [Fact]
    public async Task ClaimDue_ReturnsOnlyDueRows_MarkedFired_AndNeverTwice()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var due = Guid.NewGuid();
        var dueAtUtc = T0.AddTicks(1_234_567);
        await ScheduleAsync(stores, "OrderSaga", due, "AwaitingPayment", dueAtUtc);
        await ScheduleAsync(stores, "OrderSaga", Guid.NewGuid(), "AwaitingPayment", T0.AddMinutes(10));

        var claimed = Assert.Single(await ClaimDueAsync(stores, T0.AddMinutes(1)));

        Assert.Equal(due, claimed.CorrelationId);
        Assert.Equal("OrderSaga", claimed.SagaType);
        Assert.Equal("AwaitingPayment", claimed.ForState);
        AssertSameInstant(dueAtUtc, claimed.DueAtUtc);
        Assert.Equal(SagaTimeoutStatus.Fired, claimed.Status);
        Assert.Empty(await ClaimDueAsync(stores, T0.AddMinutes(1)));
    }

    [Fact]
    public async Task ClaimDue_IncludesARowDueExactlyAtTheCutoff()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        await ScheduleAsync(stores, "OrderSaga", Guid.NewGuid(), "AwaitingPayment", T0.AddSeconds(30));

        Assert.Empty(await ClaimDueAsync(stores, T0.AddSeconds(29)));
        Assert.Single(await ClaimDueAsync(stores, T0.AddSeconds(30)));
    }

    /// <summary>
    /// <c>batchSize</c> truncates rather than failing, and what it leaves behind is still Pending for the
    /// next claim — none of it lost, none of it returned twice.
    /// </summary>
    [Fact]
    public async Task ClaimDue_NeverReturnsMoreThanTheBatchSize_AndLeavesTheRestForTheNextClaim()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        for (var i = 0; i < 5; i++)
            await ScheduleAsync(stores, "OrderSaga", Guid.NewGuid(), "AwaitingPayment", T0.AddSeconds(i));

        var first = await ClaimDueAsync(stores, T0.AddMinutes(1), batchSize: 3);
        var second = await ClaimDueAsync(stores, T0.AddMinutes(1), batchSize: 3);
        var third = await ClaimDueAsync(stores, T0.AddMinutes(1), batchSize: 3);

        Assert.Equal(3, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Empty(third);
        Assert.Equal(5, first.Concat(second).Select(t => t.Id).Distinct().Count());
    }

    /// <summary>
    /// Clause 8 (fix F6): earliest-due first. Rows are scheduled latest-due first and named so their
    /// ordinal order also runs against due order, so neither insertion, row-id nor name order can pass for
    /// it; and the batch is smaller than the backlog, so which rows it takes matters as much as how it
    /// orders them — the most overdue must never wait behind newer ones.
    /// </summary>
    [Fact]
    public async Task ClaimDue_ReturnsTheEarliestDueRowsFirst()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        for (var i = 4; i >= 0; i--)
            await ScheduleAsync(stores, "OrderSaga", Guid.NewGuid(), $"State{4 - i}", T0.AddSeconds(i));

        var claimed = await ClaimDueAsync(stores, T0.AddMinutes(1), batchSize: 3);

        Assert.Equal(["State4", "State3", "State2"], claimed.Select(t => t.ForState), StringComparer.Ordinal);
    }

    /// <summary>
    /// Scoped per instance and state: state names are only unique within a saga type, so a cancel must
    /// not reach across into another saga type's same-named state.
    /// </summary>
    [Fact]
    public async Task Cancel_CancelsOnlyThatInstancesPendingTimeoutForThatState()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();
        var otherInstance = Guid.NewGuid();
        await ScheduleAsync(stores, "OrderSaga", correlationId, "Reserved", T0);
        await ScheduleAsync(stores, "ShippingChoreography", correlationId, "Reserved", T0);
        await ScheduleAsync(stores, "OrderSaga", correlationId, "Charged", T0);
        await ScheduleAsync(stores, "OrderSaga", otherInstance, "Reserved", T0);

        await using (var uow = await stores.BeginAsync())
            await uow.Timeouts.CancelAsync("OrderSaga", correlationId, "Reserved");

        var survivors = await ClaimDueAsync(stores, T0.AddMinutes(1));

        Assert.Equal(3, survivors.Count);
        Assert.DoesNotContain(survivors, t => string.Equals(t.SagaType, "OrderSaga", StringComparison.Ordinal)
                                              && t.CorrelationId == correlationId
                                              && string.Equals(t.ForState, "Reserved", StringComparison.Ordinal));
    }
}
