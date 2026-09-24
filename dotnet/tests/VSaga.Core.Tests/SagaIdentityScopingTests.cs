using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

/// <summary>
/// A saga instance is identified by <c>(SagaType, CorrelationId)</c>, not by correlation id alone.
/// These tests pin the behaviour that change exists for: two saga types may track the same business
/// correlation id without colliding, and every per-instance lookup — snapshot, timeline, dedupe,
/// timeout — must stay on its own side of that boundary.
///
/// Before the composite key, each of these was a real defect and not merely a missing feature: the
/// second saga's <c>InsertAsync</c> threw <see cref="SagaAlreadyExistsException"/>; its timeline
/// merged into the first's (which is what <c>SagaOrchestrator.GetVisitedStatesAsync</c> derives the
/// compensation set from, so compensation would run for states the saga never visited); its inbound
/// messages were discarded as duplicates of the other saga's; and cancelling a timeout for a
/// same-named state reached across into the other saga's pending timeout.
/// </summary>
public sealed class SagaIdentityScopingTests : IAsyncDisposable
{
    private const string OrderSagaType = nameof(TestOrderSaga);
    private const string ChoreographyType = nameof(TestShippingChoreography);

    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;

    public SagaIdentityScopingTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();

        // Both saga types in one engine, sharing one persistence store and one transport — the
        // arrangement the composite key has to hold up under.
        services.AddVSagaEngine(o => o
            .AddSaga<TestOrderSaga, TestOrderSagaState>()
            .AddSaga<TestShippingChoreography, ChoreoShippingState>());

        _provider = services.BuildServiceProvider();
        _transport = _provider.GetRequiredService<InMemoryMessageTransport>();

        foreach (var hosted in _provider.GetServices<IHostedService>())
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);

        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task TwoSagaTypesTrackTheSameCorrelationIdIndependently()
    {
        var correlationId = Guid.NewGuid();

        // An orchestrated saga and a choreographed one both observing the same business transaction.
        await _transport.PublishAsync(new OrderSubmitted("ORD-1", 42m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new ChoreoOrderPlaced("ORD-1"), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new ChoreoInventoryReserved("ORD-1"), MessageEnvelope.New(correlationId));

        var orderState = await _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>()
            .FindAsync(OrderSagaType, correlationId);
        var choreoState = await _provider.GetRequiredService<ISagaSnapshotStore<ChoreoShippingState>>()
            .FindAsync(ChoreographyType, correlationId);

        Assert.NotNull(orderState);
        Assert.NotNull(choreoState);

        // Same correlation id, two genuinely separate instances with their own state and kind.
        Assert.Equal(correlationId, orderState.CorrelationId);
        Assert.Equal(correlationId, choreoState.CorrelationId);
        Assert.Equal(SagaKind.Orchestrated, orderState.Kind);
        Assert.Equal(SagaKind.Choreographed, choreoState.Kind);
        Assert.Equal(42m, orderState.Amount);
        Assert.True(choreoState.InventoryReady);
    }

    [Fact]
    public async Task EachSagaTypesTimelineExcludesTheOthers()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-2", 10m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new ChoreoOrderPlaced("ORD-2"), MessageEnvelope.New(correlationId));

        var log = _provider.GetRequiredService<ISagaEventLogStore>();
        var orderTimeline = await log.GetTimelineAsync(OrderSagaType, correlationId);
        var choreoTimeline = await log.GetTimelineAsync(ChoreographyType, correlationId);

        Assert.NotEmpty(orderTimeline);
        Assert.NotEmpty(choreoTimeline);

        // The load-bearing assertion: neither timeline contains a single entry belonging to the
        // other saga. GetVisitedStatesAsync reads exactly this, so a leak here would drive
        // compensation for states the saga never visited.
        Assert.All(orderTimeline, e => Assert.Equal(OrderSagaType, e.SagaType));
        Assert.All(choreoTimeline, e => Assert.Equal(ChoreographyType, e.SagaType));
    }

    [Fact]
    public async Task FindByCorrelationIdReturnsEverySagaTypeTrackingIt()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-3", 7m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new ChoreoOrderPlaced("ORD-3"), MessageEnvelope.New(correlationId));

        var matches = await _provider.GetRequiredService<ISagaSummaryReader>().FindByCorrelationIdAsync(correlationId);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.Equal(correlationId, m.CorrelationId));
        Assert.Contains(matches, m => string.Equals(m.SagaType, OrderSagaType, StringComparison.Ordinal));
        Assert.Contains(matches, m => string.Equals(m.SagaType, ChoreographyType, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateCheckIsScopedToOneSagaType()
    {
        var correlationId = Guid.NewGuid();
        var log = _provider.GetRequiredService<ISagaEventLogStore>();
        const string messageId = "shared-message-id";

        await log.AppendAsync(SagaLogEntry.Create(correlationId, OrderSagaType, SagaEntryType.MessageReceived, messageId: messageId));

        Assert.True(await log.IsDuplicateAsync(OrderSagaType, correlationId, messageId));

        // The same broadcast message legitimately reaches both saga types. Were this check keyed on
        // correlation id alone, the second saga would silently discard its own first delivery.
        Assert.False(await log.IsDuplicateAsync(ChoreographyType, correlationId, messageId));
    }

    [Fact]
    public async Task CancellingATimeoutDoesNotCancelAnotherSagaTypesSameNamedState()
    {
        var correlationId = Guid.NewGuid();
        var timeouts = _provider.GetRequiredService<ISagaTimeoutStore>();
        var dueAt = DateTimeOffset.UtcNow.AddMinutes(5);

        // State names are only unique within a saga type, so "Reserved" here means two different
        // things — exactly the collision an unscoped cancel would conflate.
        await timeouts.ScheduleAsync(OrderSagaType, correlationId, "Reserved", dueAt);
        await timeouts.ScheduleAsync(ChoreographyType, correlationId, "Reserved", dueAt);

        await timeouts.CancelAsync(OrderSagaType, correlationId, "Reserved");

        var due = await timeouts.ClaimDueAsync(dueAt.AddMinutes(1), batchSize: 10);
        var stillPending = due.Where(t => t.CorrelationId == correlationId).ToList();

        var survivor = Assert.Single(stillPending);
        Assert.Equal(ChoreographyType, survivor.SagaType);
    }

    [Fact]
    public async Task InsertingTheSameCorrelationIdUnderADifferentSagaTypeDoesNotCollide()
    {
        var correlationId = Guid.NewGuid();
        var orderStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();

        var first = new TestOrderSagaState
        {
            CorrelationId = correlationId,
            SagaType = OrderSagaType,
            CurrentState = "Submitted",
            Status = SagaStatus.Running,
        };
        await orderStore.InsertAsync(first);

        // Same correlation id, different saga type — allowed.
        var second = new TestOrderSagaState
        {
            CorrelationId = correlationId,
            SagaType = "SomeOtherSaga",
            CurrentState = "Started",
            Status = SagaStatus.Running,
        };
        await orderStore.InsertAsync(second);

        // Same correlation id AND same saga type — still rejected, as it must be.
        var duplicate = new TestOrderSagaState
        {
            CorrelationId = correlationId,
            SagaType = OrderSagaType,
            CurrentState = "Submitted",
            Status = SagaStatus.Running,
        };
        var ex = await Assert.ThrowsAsync<SagaAlreadyExistsException>(() => orderStore.InsertAsync(duplicate));
        Assert.Equal(OrderSagaType, ex.SagaType);
        Assert.Equal(correlationId, ex.CorrelationId);
    }
}
