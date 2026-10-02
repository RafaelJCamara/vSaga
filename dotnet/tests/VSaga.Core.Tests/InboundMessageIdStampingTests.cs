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
/// The entries a step logs without a message of their own carry the step's inbound message id, so the
/// dashboard's timeline fold can attach them to the right step when handlers of one instance interleave
/// (docs/design/dashboard-usability-and-access.md §6.6): TimeoutScheduled and SagaCompleted as their
/// MessageId, entries logged through the SagaContext log sink (the Compensation entries here; the
/// .CallHttp request in VSaga.Http.Tests) as their CausationId. The timeout path has no inbound message
/// and leaves its entries as they were.
/// </summary>
public sealed class InboundMessageIdStampingTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;
    private readonly TestOrderSaga _saga;
    private readonly ISagaEventLogStore _eventLog;

    public InboundMessageIdStampingTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<TestOrderSaga, TestOrderSagaState>());

        _provider = services.BuildServiceProvider();
        _transport = _provider.GetRequiredService<InMemoryMessageTransport>();
        _saga = _provider.GetRequiredService<TestOrderSaga>();
        _eventLog = _provider.GetRequiredService<ISagaEventLogStore>();

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
    public async Task TimeoutScheduled_CarriesTheMessageIdOfTheStepThatScheduledIt()
    {
        var correlationId = Guid.NewGuid();
        var reserved = MessageEnvelope.New(correlationId);

        await _transport.PublishAsync(new OrderSubmitted("ORD-S1", 10m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), reserved);

        var timeline = await _eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        var scheduled = Assert.Single(timeline, e => e.EntryType == SagaEntryType.TimeoutScheduled);
        Assert.Equal(reserved.MessageId, scheduled.MessageId);
        Assert.Equal(_saga.AwaitingPayment.Name, scheduled.ToState);
    }

    [Fact]
    public async Task SagaCompleted_CarriesTheMessageIdOfTheStepThatFinalized()
    {
        var correlationId = Guid.NewGuid();
        var failed = MessageEnvelope.New(correlationId);

        await _transport.PublishAsync(new OrderSubmitted("ORD-S2", 10m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new PaymentFailed(), failed);

        var timeline = await _eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        var completed = Assert.Single(timeline, e => e.EntryType == SagaEntryType.SagaCompleted);
        Assert.Equal(failed.MessageId, completed.MessageId);
        Assert.Equal(_saga.Failed.Name, completed.ToState);
    }

    [Fact]
    public async Task CompensationEntries_LoggedThroughTheContext_CarryTheInboundMessageIdAsCausation()
    {
        var correlationId = Guid.NewGuid();
        var failed = MessageEnvelope.New(correlationId);

        await _transport.PublishAsync(new OrderSubmitted("ORD-S3", 10m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new PaymentFailed(), failed);

        var timeline = await _eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        var compensation = timeline.Where(e => e.EntryType is SagaEntryType.CompensationStarted
            or SagaEntryType.CompensationStepSucceeded or SagaEntryType.CompensationStepFailed).ToList();

        // Started, AwaitingPayment's throwing compensation, AwaitingInventory's release.
        Assert.Equal(3, compensation.Count);
        Assert.All(compensation, e => Assert.Equal(failed.MessageId, e.CausationId));
        Assert.All(compensation, e => Assert.Null(e.MessageId));
    }

    [Fact]
    public async Task TimeoutPath_HasNoInboundMessage_SoItsEntriesStayUnstamped()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-S4", 10m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));

        var timeoutStore = _provider.GetRequiredService<ISagaTimeoutStore>();
        var due = await timeoutStore.ClaimDueAsync(DateTimeOffset.UtcNow.AddHours(1), batchSize: 10);
        var timeout = Assert.Single(due, t => t.CorrelationId == correlationId);

        var orchestrator = _provider.GetRequiredService<SagaOrchestrator<TestOrderSagaState>>();
        await orchestrator.HandleTimeoutAsync(timeout, CancellationToken.None);

        var timeline = await _eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        var fired = Assert.Single(timeline, e => e.EntryType == SagaEntryType.TimeoutFired);
        var afterTimeout = timeline.Where(e => e.SequenceNumber > fired.SequenceNumber).ToList();

        var compensation = afterTimeout.Where(e => e.EntryType is SagaEntryType.CompensationStarted
            or SagaEntryType.CompensationStepSucceeded or SagaEntryType.CompensationStepFailed).ToList();
        Assert.Equal(3, compensation.Count);
        Assert.All(compensation, e => Assert.Null(e.CausationId));

        var outcome = Assert.Single(afterTimeout, e => e.EntryType == SagaEntryType.StepSucceeded);
        Assert.Null(outcome.MessageId);
    }
}
