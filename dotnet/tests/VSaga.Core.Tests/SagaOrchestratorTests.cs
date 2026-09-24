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

public sealed class SagaOrchestratorTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;
    private readonly TestOrderSaga _saga;

    public SagaOrchestratorTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<TestOrderSaga, TestOrderSagaState>());

        _provider = services.BuildServiceProvider();
        _transport = _provider.GetRequiredService<InMemoryMessageTransport>();
        _saga = _provider.GetRequiredService<TestOrderSaga>();

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
    public async Task HappyPath_RunsThroughToCompleted()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-1", 42m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new PaymentCharged(), MessageEnvelope.New(correlationId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);

        Assert.NotNull(state);
        Assert.Equal(_saga.Completed.Name, state.CurrentState);
        Assert.Equal(SagaStatus.Completed, state.Status);
        Assert.Equal("ORD-1", state.OrderId);
        Assert.Equal(42m, state.Amount);

        var published = _transport.GetPublished().Select(p => p.Message).ToList();
        Assert.Contains(published, m => m is ReserveInventory rq && string.Equals(rq.OrderId, "ORD-1", StringComparison.Ordinal));
        Assert.Contains(published, m => m is ChargePayment cp && cp.Amount == 42m);

        var summaryReader = _provider.GetRequiredService<ISagaSummaryReader>();
        var summary = await summaryReader.GetAsync(_saga.SagaType, correlationId);
        Assert.NotNull(summary);
        Assert.Equal(SagaStatus.Completed, summary.Status);
    }

    [Fact]
    public async Task FailurePath_CompensatesAndMarksFailed()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-2", 10m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new PaymentFailed(), MessageEnvelope.New(correlationId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);

        Assert.NotNull(state);
        Assert.Equal(_saga.Failed.Name, state.CurrentState);
        Assert.Equal(SagaStatus.Failed, state.Status);

        var published = _transport.GetPublished().Select(p => p.Message).ToList();
        Assert.Contains(published, m => m is ReleaseInventory);

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.SagaStarted);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.SagaCompleted); // logged for any terminal Finalize, including Failed
    }

    [Fact]
    public async Task NonInitiatingMessageForUnknownSaga_IsIgnoredWithoutCreatingState()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.Null(state);

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.UnexpectedEvent);
    }

    [Fact]
    public async Task ManualRetry_ReplaysFailedStepAndRecovers()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-3", 5m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new FlakyStep(), MessageEnvelope.New(correlationId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var afterFailure = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.NotNull(afterFailure);
        Assert.Equal(SagaStatus.Failed, afterFailure.Status);
        Assert.Equal(_saga.AwaitingInventory.Name, afterFailure.CurrentState); // failure keeps the saga parked in the state it failed in

        var retryDispatcher = _provider.GetRequiredService<ISagaRetryDispatcher>();
        await retryDispatcher.RetryAsync(_saga.SagaType, correlationId);

        var afterRetry = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.NotNull(afterRetry);
        Assert.Equal(SagaStatus.Running, afterRetry.Status);
        Assert.Equal(_saga.AwaitingPayment.Name, afterRetry.CurrentState);
        Assert.Equal(2, _saga.FlakyStepAttempts);
    }

    [Fact]
    public async Task Timeout_FiresAndTransitionsSaga()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-4", 7m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));

        var timeoutStore = _provider.GetRequiredService<ISagaTimeoutStore>();
        var due = await timeoutStore.ClaimDueAsync(DateTimeOffset.UtcNow.AddHours(1), batchSize: 10);
        var timeout = Assert.Single(due, t => t.CorrelationId == correlationId);
        Assert.Equal(_saga.AwaitingPayment.Name, timeout.ForState);

        var orchestrator = _provider.GetRequiredService<SagaOrchestrator<TestOrderSagaState>>();
        await orchestrator.HandleTimeoutAsync(timeout, CancellationToken.None);

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.NotNull(state);
        Assert.Equal(_saga.Failed.Name, state.CurrentState);
        Assert.Equal(SagaStatus.TimedOut, state.Status);

        // The saga had already reserved inventory before this timeout fired — that hold must not be
        // left dangling, so the timeout's .Compensate() releases it just like a PaymentFailed would.
        Assert.Contains(_transport.GetPublished(), p => p.Message is ReleaseInventory);

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.CompensationStarted);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.CompensationStepSucceeded);
    }

    [Fact]
    public async Task StepLevelRetryPolicy_RetriesInProcessUntilItSucceeds()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-5", 12m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new FlakyWithPolicy(), MessageEnvelope.New(correlationId));

        // The whole point of a step-level RetryPolicy is that all attempts happen inside ONE message
        // handling call — no manual intervention, no separate delivery, no Failed status in between.
        Assert.Equal(3, _saga.FlakyWithPolicyAttempts);

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.NotNull(state);
        Assert.Equal(_saga.AwaitingPayment.Name, state.CurrentState);
        Assert.Equal(SagaStatus.Running, state.Status);
    }

    [Fact]
    public async Task StepLevelRetryPolicy_MarksFailedOnceAttemptsAreExhausted()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-6", 8m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new AlwaysFailsWithPolicy(), MessageEnvelope.New(correlationId));

        Assert.Equal(2, _saga.AlwaysFailsWithPolicyAttempts); // MaxAttempts: 2, and it never succeeds

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.NotNull(state);
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.Equal(_saga.AwaitingInventory.Name, state.CurrentState); // parked where it failed, no transition ran

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.StepFailed && string.Equals(e.MessageType, nameof(AlwaysFailsWithPolicy), StringComparison.Ordinal));
    }

    /// <summary>
    /// §4.5: before this fix, a deferred publish queued via ctx.PublishAfterCommitAsync before a step
    /// throws was silently abandoned -- never sent, but also no timeline entry recorded why. It must
    /// still never be sent (the transition that queued it was never reached), but now discarding it is
    /// visible on the timeline, the same DeliveryExhausted entry the timeout-race discard path records.
    /// </summary>
    [Fact]
    public async Task StepThatThrows_DiscardsItsQueuedDeferredPublishInsteadOfAbandoningItSilently()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-13", 12m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new AlwaysFailsWithLoopback(), MessageEnvelope.New(correlationId));

        Assert.DoesNotContain(_transport.GetPublished(), p => p.Message is LoopbackAck);

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.NotNull(state);
        Assert.Equal(SagaStatus.Failed, state.Status);

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        var exhausted = Assert.Single(timeline, e => e.EntryType == SagaEntryType.DeliveryExhausted);
        Assert.Equal(nameof(LoopbackAck), exhausted.MessageType);
    }

    /// <summary>
    /// docs/design/mixed-sagas.md §3.2: a step queues a loopback (ctx.PublishAfterCommitAsync) then throws on
    /// its first attempt. Without StepExecutor clearing the queue before the retry replays the step from
    /// index 0, the second attempt would queue a second LoopbackAck with a fresh MessageId -- invisible
    /// to IsDuplicateAsync's MessageId-keyed dedupe -- and the drain would publish both.
    /// </summary>
    [Fact]
    public async Task StepLevelRetryPolicy_QueuedLoopbackPublishesExactlyOnceDespiteARetriedAttempt()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-9", 15m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new FlakyWithLoopback(), MessageEnvelope.New(correlationId));

        Assert.Equal(2, _saga.FlakyWithLoopbackAttempts); // failed once, succeeded on retry

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.NotNull(state);
        Assert.Equal(_saga.AwaitingPayment.Name, state.CurrentState);
        Assert.Equal(SagaStatus.Running, state.Status);

        var loopbackCount = _transport.GetPublished().Count(p => p.Message is LoopbackAck);
        Assert.Equal(1, loopbackCount); // not 2 -- the first attempt's queued publish was cleared, not carried into the drain
    }

    [Fact]
    public async Task DuplicateMessageId_IsSkippedAndDoesNotReprocess()
    {
        var correlationId = Guid.NewGuid();
        await _transport.PublishAsync(new OrderSubmitted("ORD-7", 20m), MessageEnvelope.New(correlationId));

        var duplicateEnvelope = new MessageEnvelope(correlationId, "fixed-message-id-123");
        await _transport.PublishAsync(new InventoryReserved(), duplicateEnvelope);
        await _transport.PublishAsync(new InventoryReserved(), duplicateEnvelope); // same correlation + same message id: a genuine redelivery

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.NotNull(state);
        Assert.Equal(_saga.AwaitingPayment.Name, state.CurrentState); // advanced exactly once, not twice

        var chargePaymentCount = _transport.GetPublished().Count(p => p.Message is ChargePayment);
        Assert.Equal(1, chargePaymentCount); // the ChargePayment side effect only fired once
    }

    [Fact]
    public async Task OutboundMessage_IsLoggedWithSourceServiceAndCausationId()
    {
        var correlationId = Guid.NewGuid();
        var inboundEnvelope = MessageEnvelope.New(correlationId);

        await _transport.PublishAsync(new OrderSubmitted("ORD-8", 30m), inboundEnvelope);

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);

        var published = Assert.Single(timeline, e => e.EntryType == SagaEntryType.MessagePublished &&
            string.Equals(e.MessageType, nameof(ReserveInventory), StringComparison.Ordinal));
        Assert.Equal(_saga.SagaType, published.SourceService);
        Assert.Equal(inboundEnvelope.MessageId, published.CausationId);
        Assert.NotNull(published.MessageId);
    }

    [Fact]
    public async Task InboundMessage_RecordsTheStampedSourceServiceHeader()
    {
        var correlationId = Guid.NewGuid();
        await _transport.PublishAsync(new OrderSubmitted("ORD-9", 15m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.From("InventoryService", correlationId));

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);

        var received = Assert.Single(timeline, e => e.EntryType == SagaEntryType.MessageReceived &&
            string.Equals(e.MessageType, nameof(InventoryReserved), StringComparison.Ordinal));
        Assert.Equal("InventoryService", received.SourceService);
    }

    [Fact]
    public async Task ReplyCausationId_StitchesBackToTheOutboundMessageThatCausedIt()
    {
        // End-to-end version of the causation-stitching contract SagaMapBuilder relies on: a
        // participant replying with MessageEnvelope.From(..., causationId: received.MessageId) must
        // round-trip through the orchestrator so the inbound MessageReceived entry's CausationId
        // equals the outbound MessagePublished entry's MessageId — that's the only thing that lets the
        // map join a reply back to the request it answers.
        var correlationId = Guid.NewGuid();
        await _transport.PublishAsync(new OrderSubmitted("ORD-13", 22m), MessageEnvelope.New(correlationId));

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        var outboundMessageId = Assert.Single(timeline, e => e.EntryType == SagaEntryType.MessagePublished).MessageId!;

        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.From("InventoryService", correlationId, causationId: outboundMessageId));

        var timelineAfterReply = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        var received = Assert.Single(timelineAfterReply, e => e.EntryType == SagaEntryType.MessageReceived &&
            string.Equals(e.MessageType, nameof(InventoryReserved), StringComparison.Ordinal));
        Assert.Equal(outboundMessageId, received.CausationId);
    }

    [Fact]
    public async Task Compensation_EmitsStartedAndStepSucceededEntries()
    {
        var correlationId = Guid.NewGuid();
        await _transport.PublishAsync(new OrderSubmitted("ORD-10", 10m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new PaymentFailed(), MessageEnvelope.New(correlationId));

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);

        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.CompensationStarted);

        var succeeded = Assert.Single(timeline, e => e.EntryType == SagaEntryType.CompensationStepSucceeded);
        Assert.Equal(_saga.AwaitingInventory.Name, succeeded.FromState);
    }

    [Fact]
    public async Task Compensation_StepThatThrows_LogsStepFailedButLaterStepsStillRun()
    {
        var correlationId = Guid.NewGuid();
        await _transport.PublishAsync(new OrderSubmitted("ORD-11", 25m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new PaymentFailed(), MessageEnvelope.New(correlationId));

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);

        var failed = Assert.Single(timeline, e => e.EntryType == SagaEntryType.CompensationStepFailed);
        Assert.Equal(_saga.AwaitingPayment.Name, failed.FromState);
        Assert.Contains("simulated compensation failure", failed.ErrorMessage, StringComparison.Ordinal);

        var succeeded = Assert.Single(timeline, e => e.EntryType == SagaEntryType.CompensationStepSucceeded);
        Assert.Equal(_saga.AwaitingInventory.Name, succeeded.FromState);

        // AwaitingPayment's compensation runs first (most-recently-visited) and throws, but
        // AwaitingInventory's ReleaseInventory publish still happens afterward — one failing
        // compensation step must not abandon the rest.
        Assert.Contains(_transport.GetPublished(), p => p.Message is ReleaseInventory);
    }

    [Fact]
    public async Task IsDuplicateCheck_IgnoresOutboundEntries_SoALaterInboundMessageReusingThatIdStillProcesses()
    {
        var correlationId = Guid.NewGuid();
        await _transport.PublishAsync(new OrderSubmitted("ORD-12", 18m), MessageEnvelope.New(correlationId));

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        var outboundMessageId = Assert.Single(timeline, e => e.EntryType == SagaEntryType.MessagePublished).MessageId!;

        // A coincidental collision between an inbound MessageId and an earlier *outbound* entry's
        // MessageId must not be mistaken for a redelivery — IsDuplicateAsync only recognizes inbound
        // entry types (SagaStarted/MessageReceived).
        Assert.False(await eventLog.IsDuplicateAsync(_saga.SagaType, correlationId, outboundMessageId));

        await _transport.PublishAsync(new InventoryReserved(), new MessageEnvelope(correlationId, outboundMessageId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);
        Assert.NotNull(state);
        Assert.Equal(_saga.AwaitingPayment.Name, state.CurrentState); // processed, not skipped as a dup
    }
}
