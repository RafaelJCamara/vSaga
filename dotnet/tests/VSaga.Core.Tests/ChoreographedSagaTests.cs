using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Core.Runtime;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

/// <summary>
/// Exercises <see cref="Dsl.ChoreographedSagaDefinition{TState}"/> through the real
/// <see cref="SagaOrchestrator{TState}"/> — the same engine orchestrated sagas run through, registered
/// via the exact same <c>AddSaga&lt;TDefinition, TState&gt;()</c> call, confirming the runtime needed no
/// changes to drive a choreographed definition.
/// </summary>
public sealed class ChoreographedSagaTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;
    private readonly TestShippingChoreography _saga;

    public ChoreographedSagaTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<TestShippingChoreography, ChoreoShippingState>());

        _provider = services.BuildServiceProvider();
        _transport = _provider.GetRequiredService<InMemoryMessageTransport>();
        _saga = _provider.GetRequiredService<TestShippingChoreography>();

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
    public async Task HappyPath_InventoryObservedBeforePayment_CompletesAndPersistsChoreographedKind()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new ChoreoOrderPlaced("ORD-1"), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new ChoreoInventoryReserved("ORD-1"), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new ChoreoPaymentCharged("ORD-1"), MessageEnvelope.New(correlationId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<ChoreoShippingState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);

        Assert.NotNull(state);
        Assert.Equal(_saga.Charged.Name, state.CurrentState);
        Assert.Equal(SagaStatus.Completed, state.Status);
        Assert.Equal(SagaKind.Choreographed, state.Kind);
        Assert.True(state.InventoryReady);
        Assert.True(state.PaymentReady);

        var summaryReader = _provider.GetRequiredService<ISagaSummaryReader>();
        var summary = await summaryReader.GetAsync(_saga.SagaType, correlationId);
        Assert.NotNull(summary);
        Assert.Equal(SagaKind.Choreographed, summary.Kind);
    }

    [Fact]
    public async Task ReversedEventOrder_BothEventsStillHandled_BecauseDispatchIsNotGatedByCurrentState()
    {
        // The whole point of choreography: unlike OrchestratedSagaDefinition.During(state).When<T>(),
        // On<T>() is registered once, globally — not per current-state. PaymentCharged normally arrives
        // after InventoryReserved, but nothing about this saga's dispatch depends on that order; three
        // independent services racing over a real broker have no reason to agree on one.
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new ChoreoPaymentCharged("ORD-2"), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new ChoreoInventoryReserved("ORD-2"), MessageEnvelope.New(correlationId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<ChoreoShippingState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);

        Assert.NotNull(state);
        Assert.True(state.PaymentReady);
        Assert.True(state.InventoryReady); // still applied even though the saga was already Completed from PaymentCharged
        Assert.Equal(_saga.Reserved.Name, state.CurrentState); // InventoryReserved ran last and recorded its own state label
    }

    [Fact]
    public async Task MultipleEventTypesCanIndependentlyStartANewTrackedInstance()
    {
        // No designated first step like orchestration's single InitialState: any event marked
        // StartsNewInstance() can be the one that creates the instance. Here InventoryReserved arrives
        // with no OrderPlaced ever observed at all, e.g. because it was published before this tracker
        // subscribed, or simply raced ahead over the broker.
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new ChoreoInventoryReserved("ORD-3"), MessageEnvelope.New(correlationId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<ChoreoShippingState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);

        Assert.NotNull(state);
        Assert.Equal(_saga.Reserved.Name, state.CurrentState);
        Assert.True(state.InventoryReady);
        Assert.Equal("ORD-3", state.OrderId);
    }

    [Fact]
    public async Task EventNotMarkedStartsNewInstance_ForUnknownSaga_IsIgnoredWithoutCreatingState()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new ChoreoPaymentDeclined("ORD-4"), MessageEnvelope.New(correlationId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<ChoreoShippingState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.Null(state);

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.UnexpectedEvent);
    }

    [Fact]
    public async Task FailureEvent_CompensatesAndFinalizesFailed()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new ChoreoInventoryReserved("ORD-5"), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new ChoreoPaymentDeclined("ORD-5"), MessageEnvelope.New(correlationId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<ChoreoShippingState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.NotNull(state);
        Assert.Equal(_saga.Failed.Name, state.CurrentState);
        Assert.Equal(SagaStatus.Failed, state.Status);

        Assert.Contains(_transport.GetPublished(), p => p.Message is ChoreoReleaseInventory);

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.CompensationStarted);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.CompensationStepSucceeded);
    }

    [Fact]
    public async Task Timeout_FiresAndCompensates()
    {
        var correlationId = Guid.NewGuid();
        await _transport.PublishAsync(new ChoreoInventoryReserved("ORD-6"), MessageEnvelope.New(correlationId));

        var timeoutStore = _provider.GetRequiredService<ISagaTimeoutStore>();
        var due = await timeoutStore.ClaimDueAsync(DateTimeOffset.UtcNow.AddHours(1), batchSize: 10);
        var timeout = Assert.Single(due, t => t.CorrelationId == correlationId);
        Assert.Equal(_saga.Reserved.Name, timeout.ForState);

        var orchestrator = _provider.GetRequiredService<SagaOrchestrator<ChoreoShippingState>>();
        await orchestrator.HandleTimeoutAsync(timeout, CancellationToken.None);

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<ChoreoShippingState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.NotNull(state);
        Assert.Equal(_saga.Failed.Name, state.CurrentState);
        Assert.Equal(SagaStatus.TimedOut, state.Status);
        Assert.Contains(_transport.GetPublished(), p => p.Message is ChoreoReleaseInventory);
    }

    [Fact]
    public async Task StepLevelRetryPolicy_RetriesInProcessUntilItSucceeds()
    {
        var correlationId = Guid.NewGuid();
        await _transport.PublishAsync(new ChoreoOrderPlaced("ORD-7"), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new ChoreoFlakyEvent("ORD-7"), MessageEnvelope.New(correlationId));

        Assert.Equal(2, _saga.FlakyAttempts);

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<ChoreoShippingState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.NotNull(state);
        Assert.Equal(_saga.Reserved.Name, state.CurrentState);
        Assert.Equal(SagaStatus.Running, state.Status);
    }
}
