using VSaga.Abstractions.Persistence;

namespace VSaga.Persistence.Conformance;

/// <summary>Conformance cases for <see cref="ISagaEventLogStore"/>.</summary>
public abstract class EventLogStoreConformanceTests(IProviderFixture fixture) : StoreConformanceTests(fixture)
{
    private static SagaLogEntry Entry(Guid correlationId, SagaEntryType entryType = SagaEntryType.StateEntered,
        string sagaType = "OrderSaga", string? messageId = null) =>
        SagaLogEntry.Create(correlationId, sagaType, entryType, messageId: messageId, occurredAtUtc: T0);

    [Fact]
    public async Task Append_ReturnsAscendingSequenceNumbers()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();

        await using var uow = await stores.BeginAsync();
        var first = await uow.EventLog.AppendAsync(Entry(correlationId));
        var second = await uow.EventLog.AppendAsync(Entry(correlationId));
        var third = await uow.EventLog.AppendAsync(Entry(correlationId));

        Assert.True(first < second && second < third, $"Expected ascending sequence numbers, got {first}, {second}, {third}.");
    }

    /// <summary>
    /// Clause 5: ascending <see cref="SagaLogEntry.SequenceNumber"/> order, oldest first — the order
    /// compensation is derived from. Appends to another instance are interleaved so that a store
    /// returning a global append order, rather than this instance's, cannot pass by accident.
    /// </summary>
    [Fact]
    public async Task GetTimeline_ReturnsTheInstancesEntriesInAscendingSequenceOrder()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var appended = new List<long>();

        foreach (var state in new[] { "A", "B", "C", "D" })
        {
            await using var uow = await stores.BeginAsync();
            appended.Add(await uow.EventLog.AppendAsync(
                SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StateEntered, toState: state, occurredAtUtc: T0)));
            await uow.EventLog.AppendAsync(Entry(other));
        }

        await using var reader = await stores.BeginAsync();
        var timeline = await reader.EventLog.GetTimelineAsync("OrderSaga", correlationId);

        Assert.Equal(appended, timeline.Select(e => e.SequenceNumber));
        Assert.Equal(["A", "B", "C", "D"], timeline.Select(e => e.ToState), StringComparer.Ordinal);
    }

    [Fact]
    public async Task GetTimeline_RoundTripsEveryField()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();
        var occurredAtUtc = T0.AddTicks(1_234_567);
        var written = new SagaLogEntry(0, correlationId, "OrderSaga", SagaEntryType.StepFailed, "Reserving", "Failed",
            "ReserveInventory", "m-1", "{\"Sku\":\"A\"}", "out of stock", "trace-1", "span-1", occurredAtUtc,
            SourceService: "OrderService", DestinationService: "InventoryService", CausationId: "m-0");

        long sequenceNumber;
        await using (var uow = await stores.BeginAsync())
            sequenceNumber = await uow.EventLog.AppendAsync(written);

        await using var reader = await stores.BeginAsync();
        var read = Assert.Single(await reader.EventLog.GetTimelineAsync("OrderSaga", correlationId));

        AssertSameInstant(occurredAtUtc, read.OccurredAtUtc);
        Assert.Equal(written with { SequenceNumber = sequenceNumber, OccurredAtUtc = read.OccurredAtUtc }, read);
    }

    /// <summary>
    /// Two saga types tracking one correlation id keep independent timelines: merging them would corrupt
    /// the compensation set <c>GetVisitedStatesAsync</c> derives from this log.
    /// </summary>
    [Fact]
    public async Task GetTimeline_IsScopedToOneSagaInstance()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();

        await using var uow = await stores.BeginAsync();
        await uow.EventLog.AppendAsync(Entry(correlationId, sagaType: "OrderSaga", messageId: "m1"));
        await uow.EventLog.AppendAsync(Entry(correlationId, sagaType: "ShippingChoreography", messageId: "m2"));
        await uow.EventLog.AppendAsync(Entry(Guid.NewGuid(), sagaType: "OrderSaga", messageId: "m3"));

        Assert.Equal("m1", Assert.Single(await uow.EventLog.GetTimelineAsync("OrderSaga", correlationId)).MessageId);
        Assert.Equal("m2", Assert.Single(await uow.EventLog.GetTimelineAsync("ShippingChoreography", correlationId)).MessageId);
        Assert.Empty(await uow.EventLog.GetTimelineAsync("OrderSaga", Guid.NewGuid()));
    }

    /// <summary>Clause 6: only the two inbound entry types make a message id a duplicate.</summary>
    [Theory]
    [InlineData(SagaEntryType.SagaStarted)]
    [InlineData(SagaEntryType.MessageReceived)]
    public async Task IsDuplicate_ForAnInboundEntryWithThatMessageId_IsTrue(SagaEntryType entryType)
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();

        await using var uow = await stores.BeginAsync();
        await uow.EventLog.AppendAsync(Entry(correlationId, entryType, messageId: "m1"));

        Assert.True(await uow.EventLog.IsDuplicateAsync("OrderSaga", correlationId, "m1"));
        Assert.False(await uow.EventLog.IsDuplicateAsync("OrderSaga", correlationId, "m2"));
    }

    /// <summary>
    /// Clause 6, the other half: outbound entries and the dead-letter path's <c>DeliveryExhausted</c> carry
    /// message ids too — <c>DeliveryExhausted</c> reuses the inbound id outright — but none proves the
    /// message was processed. Matching them would drop a redelivered, never-processed message as a
    /// duplicate.
    /// </summary>
    [Theory]
    [InlineData(SagaEntryType.MessagePublished)]
    [InlineData(SagaEntryType.MessageSent)]
    [InlineData(SagaEntryType.DeliveryExhausted)]
    [InlineData(SagaEntryType.StepFailed)]
    public async Task IsDuplicate_IgnoresOtherEntryTypesCarryingThatMessageId(SagaEntryType entryType)
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();

        await using var uow = await stores.BeginAsync();
        await uow.EventLog.AppendAsync(Entry(correlationId, entryType, messageId: "m1"));

        Assert.False(await uow.EventLog.IsDuplicateAsync("OrderSaga", correlationId, "m1"));
    }

    /// <summary>
    /// Scoped to the instance: the same broadcast legitimately reaches several saga types, and each must
    /// process its own copy rather than the second being discarded as the first's duplicate.
    /// </summary>
    [Fact]
    public async Task IsDuplicate_IsScopedToOneSagaInstance()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();

        await using var uow = await stores.BeginAsync();
        await uow.EventLog.AppendAsync(Entry(correlationId, SagaEntryType.MessageReceived, "OrderSaga", "m1"));

        Assert.True(await uow.EventLog.IsDuplicateAsync("OrderSaga", correlationId, "m1"));
        Assert.False(await uow.EventLog.IsDuplicateAsync("ShippingChoreography", correlationId, "m1"));
        Assert.False(await uow.EventLog.IsDuplicateAsync("OrderSaga", Guid.NewGuid(), "m1"));
    }
}
