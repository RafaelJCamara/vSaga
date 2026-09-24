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
/// Slice 2b: the engine publishes ChildSagaFinished to a child's parent when the child goes terminal
/// through a path ctx.NotifyParentAsync structurally cannot reach — an unhandled exception, or a timeout
/// — rather than only through the child's own step code.
///
/// <para>
/// <b>Every test here drives the real publish → receive → orchestrator path</b>, the same discipline
/// SubSagaCompositionTests and NotifyParentAsyncTests hold to: nothing below ever assigns
/// <c>ParentCorrelationId</c>, hand-builds a <c>ChildSagaFinished</c> message, or stamps a header. The
/// only way a parent sees this message is <c>ctx.StartChildAsync</c> publishing, the transport
/// delivering, the child's own orchestrator failing/timing out for real, and the engine publishing on its
/// behalf.
/// </para>
/// </summary>
public sealed class ChildSagaFinishedTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;
    private readonly ISagaSummaryReader _reader;
    private readonly ISagaEventLogStore _log;

    public ChildSagaFinishedTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o
            .AddSaga<TestChildSafetyNetParentSaga, TestChildSafetyNetParentState>()
            .AddSaga<TestRiskyChildSaga, TestRiskyChildState>()
            .AddSaga<TestSlowChildSaga, TestSlowChildState>()
            .AddSaga<TestSucceedingChildSaga, TestSucceedingChildState>()
            .AddSaga<TestRacyFailureParentSaga, TestRacyFailureParentState>()
            .AddSaga<TestImmediatelyFailingChildSaga, TestImmediatelyFailingChildState>());

        _provider = services.BuildServiceProvider();
        _transport = _provider.GetRequiredService<InMemoryMessageTransport>();
        _reader = _provider.GetRequiredService<ISagaSummaryReader>();
        _log = _provider.GetRequiredService<ISagaEventLogStore>();

        foreach (var hosted in _provider.GetServices<IHostedService>())
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);

        await _provider.DisposeAsync();
    }

    private Task<TestChildSafetyNetParentState?> FindParentAsync(Guid correlationId) =>
        _provider.GetRequiredService<ISagaSnapshotStore<TestChildSafetyNetParentState>>().FindAsync(nameof(TestChildSafetyNetParentSaga), correlationId);

    [Fact]
    public async Task ChildSagaFinished_OnAnUnhandledException_ReleasesTheParentWithTheStatus()
    {
        var parentId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginSafeguardedJob("JOB-1"), MessageEnvelope.New(parentId));

        var child = Assert.Single(await _reader.FindChildrenAsync(nameof(TestChildSafetyNetParentSaga), parentId));
        await _transport.PublishAsync(new TriggerFailure("JOB-1"), MessageEnvelope.New(child.CorrelationId));

        var parent = await FindParentAsync(parentId);
        Assert.NotNull(parent);
        Assert.Equal(nameof(TestChildSafetyNetParentSaga.Rescued), parent.CurrentState);
        Assert.Equal(SagaStatus.Failed, parent.Status);
        Assert.Equal(SagaStatus.Failed, parent.ChildFinishedStatus);

        // The child's own failure is real and independent of whether the notification landed. Re-fetched
        // rather than reusing `child` above, which is a snapshot from before TriggerFailure was published.
        var childState = await _provider.GetRequiredService<ISagaSnapshotStore<TestRiskyChildState>>()
            .FindAsync(nameof(TestRiskyChildSaga), child.CorrelationId);
        Assert.NotNull(childState);
        Assert.Equal(SagaStatus.Failed, childState.Status);
    }

    /// <summary>
    /// production-readiness.md §4.3's second publish surface: the engine's own ChildSagaFinished bypasses
    /// SagaContext entirely, so it needs its own outbox row or a crash between the child's failure
    /// committing and this publish would strand the parent forever. The row is staged before the persist
    /// that records Failed and marked Dispatched by the inline send, so a healthy run leaves nothing for
    /// the recovery poller — asserted by claiming with a cutoff far in the future, which would return the
    /// row if it were still Pending.
    /// </summary>
    [Fact]
    public async Task ChildSagaFinished_IsBackedByAnOutboxRow_MarkedDispatchedByTheInlineSend()
    {
        var parentId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginSafeguardedJob("JOB-OUTBOX"), MessageEnvelope.New(parentId));
        var child = Assert.Single(await _reader.FindChildrenAsync(nameof(TestChildSafetyNetParentSaga), parentId));
        await _transport.PublishAsync(new TriggerFailure("JOB-OUTBOX"), MessageEnvelope.New(child.CorrelationId));

        var childTimeline = await _log.GetTimelineAsync(nameof(TestRiskyChildSaga), child.CorrelationId);
        var finished = Assert.Single(childTimeline, e => e.EntryType == SagaEntryType.ChildSagaFinished);

        var outbox = _provider.GetRequiredService<ISagaOutboxStore>();
        var stillPending = await outbox.ClaimPendingAsync(DateTimeOffset.UtcNow.AddYears(1), batchSize: 100);
        Assert.DoesNotContain(stillPending, m => string.Equals(m.MessageId, finished.MessageId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChildSagaFinished_IsLoggedOnTheChildsOwnTimeline_DistinctFromMessagePublished()
    {
        var parentId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginSafeguardedJob("JOB-2"), MessageEnvelope.New(parentId));
        var child = Assert.Single(await _reader.FindChildrenAsync(nameof(TestChildSafetyNetParentSaga), parentId));
        await _transport.PublishAsync(new TriggerFailure("JOB-2"), MessageEnvelope.New(child.CorrelationId));

        var childTimeline = await _log.GetTimelineAsync(nameof(TestRiskyChildSaga), child.CorrelationId);
        var published = Assert.Single(childTimeline, e => e.EntryType == SagaEntryType.ChildSagaFinished);
        Assert.Equal(nameof(ChildSagaFinished), published.MessageType);
        Assert.DoesNotContain(childTimeline, e => e.EntryType == SagaEntryType.MessagePublished);

        // And on the parent's side it arrives as an ordinary MessageReceived, same as any other message.
        var parentTimeline = await _log.GetTimelineAsync(nameof(TestChildSafetyNetParentSaga), parentId);
        Assert.Contains(parentTimeline, e => e.EntryType == SagaEntryType.MessageReceived && string.Equals(e.MessageType, nameof(ChildSagaFinished), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChildSagaFinished_OnATerminalTimeout_ReleasesTheParentWithTheStatus()
    {
        var parentId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginSafeguardedSlowJob("JOB-3"), MessageEnvelope.New(parentId));
        var child = Assert.Single(await _reader.FindChildrenAsync(nameof(TestChildSafetyNetParentSaga), parentId));

        var timeoutStore = _provider.GetRequiredService<ISagaTimeoutStore>();
        var due = await timeoutStore.ClaimDueAsync(DateTimeOffset.UtcNow.AddHours(1), batchSize: 10);
        var timeout = Assert.Single(due, t => t.CorrelationId == child.CorrelationId);

        await _provider.GetRequiredService<VSaga.Core.Runtime.SagaOrchestrator<TestSlowChildState>>()
            .HandleTimeoutAsync(timeout, CancellationToken.None);

        var childState = await _provider.GetRequiredService<ISagaSnapshotStore<TestSlowChildState>>()
            .FindAsync(nameof(TestSlowChildSaga), child.CorrelationId);
        Assert.NotNull(childState);
        Assert.Equal(SagaStatus.TimedOut, childState.Status);

        var parent = await FindParentAsync(parentId);
        Assert.NotNull(parent);
        Assert.Equal(nameof(TestChildSafetyNetParentSaga.Rescued), parent.CurrentState);
        Assert.Equal(SagaStatus.TimedOut, parent.ChildFinishedStatus);

        var childTimeline = await _log.GetTimelineAsync(nameof(TestSlowChildSaga), child.CorrelationId);
        Assert.Single(childTimeline, e => e.EntryType == SagaEntryType.ChildSagaFinished);
    }

    [Fact]
    public async Task ChildSagaFinished_ScopeBoundary_OrdinarySuccessDoesNotPublishIt()
    {
        // The scope-boundary decision: ChildSagaFinished must never fire from the ordinary
        // message-driven success path, even though this child does have a parent and does go terminal —
        // that path is NotifyParentAsync's territory (which this saga deliberately never calls either),
        // not the engine safety net's. Firing here too would be a redundant, data-free duplicate for any
        // child that does call NotifyParentAsync, and this test proves it doesn't happen even for one
        // that doesn't.
        var parentId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginSafeguardedSuccessJob("JOB-4"), MessageEnvelope.New(parentId));
        var child = Assert.Single(await _reader.FindChildrenAsync(nameof(TestChildSafetyNetParentSaga), parentId));
        await _transport.PublishAsync(new CompleteWork("JOB-4"), MessageEnvelope.New(child.CorrelationId));

        var childState = await _provider.GetRequiredService<ISagaSnapshotStore<TestSucceedingChildState>>()
            .FindAsync(nameof(TestSucceedingChildSaga), child.CorrelationId);
        Assert.NotNull(childState);
        Assert.Equal(SagaStatus.Completed, childState.Status);

        var childTimeline = await _log.GetTimelineAsync(nameof(TestSucceedingChildSaga), child.CorrelationId);
        Assert.Contains(childTimeline, e => e.EntryType == SagaEntryType.SagaCompleted);
        Assert.DoesNotContain(childTimeline, e => e.EntryType == SagaEntryType.ChildSagaFinished);

        // The parent never heard anything at all — still parked exactly where StartChildAsync left it.
        var parent = await FindParentAsync(parentId);
        Assert.NotNull(parent);
        Assert.Equal(nameof(TestChildSafetyNetParentSaga.AwaitingResult), parent.CurrentState);
        Assert.Equal(SagaStatus.Running, parent.Status);
        Assert.Null(parent.ChildFinishedStatus);
    }

    [Fact]
    public async Task ChildSagaFinished_OnARootSagaWithNoParent_IsNeverPublished()
    {
        // Driven directly, not via StartChildAsync, so ParentCorrelationId stays null — the same
        // no-parent-to-notify shape NotifyParentAsyncTests pins for the child-initiated path.
        var rootId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginRiskyWork("JOB-5"), MessageEnvelope.New(rootId));
        await _transport.PublishAsync(new TriggerFailure("JOB-5"), MessageEnvelope.New(rootId));

        var root = await _provider.GetRequiredService<ISagaSnapshotStore<TestRiskyChildState>>()
            .FindAsync(nameof(TestRiskyChildSaga), rootId);
        Assert.NotNull(root);
        Assert.Equal(SagaStatus.Failed, root.Status);
        Assert.Null(root.ParentCorrelationId);

        var timeline = await _log.GetTimelineAsync(nameof(TestRiskyChildSaga), rootId);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.StepFailed);
        Assert.DoesNotContain(timeline, e => e.EntryType == SagaEntryType.ChildSagaFinished);
    }

    [Fact]
    public async Task ChildSagaFinished_FromAChildsOwnInitiatingStep_CanRaceAheadOfTheParentsUnpersistedTransition()
    {
        // The StepFailed-path analogue of the race NotifyParentAsyncTests pins for NotifyParentAsync:
        // InMemoryMessageTransport.DispatchAsync invokes every subscriber synchronously and recursively,
        // so a child that fails in the very same step StartChildAsync started it in publishes
        // ChildSagaFinished while still nested inside the parent's own StartChildAsync call — before
        // HandleStepSuccessAsync has persisted the parent's AwaitingResult transition, let alone inserted
        // a row at all. The parent's orchestrator finds no existing instance, ChildSagaFinished isn't
        // among its initiating types (it's declared under AwaitingResult, not the initial state), so it
        // logs UnexpectedEvent and drops it — no exception, no redelivery, nothing retries.
        var parentId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginRacyFailureJob("JOB-6"), MessageEnvelope.New(parentId));

        var parent = await _provider.GetRequiredService<ISagaSnapshotStore<TestRacyFailureParentState>>()
            .FindAsync(nameof(TestRacyFailureParentSaga), parentId);
        Assert.NotNull(parent);
        Assert.Equal(nameof(TestRacyFailureParentSaga.AwaitingResult), parent.CurrentState);
        Assert.Equal(SagaStatus.Running, parent.Status);
        Assert.Null(parent.ChildFinishedStatus);

        var parentTimeline = await _log.GetTimelineAsync(nameof(TestRacyFailureParentSaga), parentId);
        Assert.Contains(parentTimeline, e => e.EntryType == SagaEntryType.UnexpectedEvent && string.Equals(e.MessageType, nameof(ChildSagaFinished), StringComparison.Ordinal));

        // The child itself is none the wiser — the engine's publish is fire-and-forget, exactly like
        // NotifyParentAsync, so the child's own status is unaffected by whether anyone received it.
        var reader = _provider.GetRequiredService<ISagaSummaryReader>();
        var child = Assert.Single(await reader.FindChildrenAsync(nameof(TestRacyFailureParentSaga), parentId));
        Assert.Equal(SagaStatus.Failed, child.Status);
    }

    /// <summary>Forces the next UpdateAsync call to lose the optimistic-concurrency check, once, then delegates normally.</summary>
    private sealed class ThrowOnNextUpdateSnapshotStore<TState>(ISagaSnapshotStore<TState> inner) : ISagaSnapshotStore<TState>
        where TState : SagaState
    {
        public bool ArmedToThrow { get; set; }

        public Task<TState?> FindAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(sagaType, correlationId, cancellationToken);

        public Task InsertAsync(TState state, CancellationToken cancellationToken = default) =>
            inner.InsertAsync(state, cancellationToken);

        public Task<TState?> FindByBusinessKeyAsync(string sagaType, string businessKey, CancellationToken cancellationToken = default) =>
            inner.FindByBusinessKeyAsync(sagaType, businessKey, cancellationToken);

        public Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default)
        {
            if (ArmedToThrow)
            {
                ArmedToThrow = false;
                throw new SagaConcurrencyException(state.SagaType, state.CorrelationId, expectedVersion);
            }

            return inner.UpdateAsync(state, expectedVersion, cancellationToken);
        }
    }

    /// <summary>
    /// The HandleStepFailureAsync counterpart to
    /// SagaOrchestratorConcurrencyRedeliveryTests.LostRaceOnDeferredPublish_DiscardsTheStagedOutboxRow --
    /// a lost race on THIS persist (a step that threw, not one that succeeded) stages a ChildSagaFinished
    /// outbox row the same way (StageChildSagaFinishedAsync, before the persist), and it must be
    /// discarded on a lost race too, or the recovery poller would later tell this child's parent it
    /// finished Failed for a transition the snapshot never actually recorded. Self-identified while
    /// fixing the sibling success-path bug (production-readiness.md §8.15/§8.18's reviews caught that
    /// one; this one shares the exact same root cause and was fixed alongside it).
    /// </summary>
    [Fact]
    public async Task ChildSagaFinished_LostRaceOnTheFailurePersist_DiscardsTheStagedOutboxRow()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o
            .AddSaga<TestChildSafetyNetParentSaga, TestChildSafetyNetParentState>()
            .AddSaga<TestRiskyChildSaga, TestRiskyChildState>());

        ThrowOnNextUpdateSnapshotStore<TestRiskyChildState>? racingStore = null;
        services.AddSingleton<ISagaSnapshotStore<TestRiskyChildState>>(sp =>
        {
            racingStore = new ThrowOnNextUpdateSnapshotStore<TestRiskyChildState>(
                new InMemorySagaSnapshotStore<TestRiskyChildState>(sp.GetRequiredService<InMemorySagaStore>()));
            return racingStore;
        });

        await using var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var reader = provider.GetRequiredService<ISagaSummaryReader>();

        var parentId = Guid.NewGuid();
        await transport.PublishAsync(new BeginSafeguardedJob("JOB-RACE"), MessageEnvelope.New(parentId));
        var child = Assert.Single(await reader.FindChildrenAsync(nameof(TestChildSafetyNetParentSaga), parentId));

        racingStore!.ArmedToThrow = true;

        // TriggerFailure's step throws, HandleStepFailureAsync stages ChildSagaFinished then calls
        // PersistAsync -- UpdateAsync throws SagaConcurrencyException once, per the arm above. That
        // reaches HandleAsync's own catch and triggers ordinary infrastructure-failure redelivery (same
        // MessageId) -- but per SagaOrchestratorConcurrencyRedeliveryTests'
        // AssertRedeliveryWasADeadEndForTheLoser, this redelivery is a dead end, not a retry-to-success:
        // RunStepAsync had already durably logged a MessageReceived entry for this exact MessageId
        // before the failing persist, so HandleCoreAsync's IsDuplicateAsync check recognizes the
        // redelivered copy and silently skips it. The child's Failed transition is therefore lost for
        // good -- confirmed empirically below, not assumed -- which is exactly why the discard matters:
        // nothing else will ever retry this, so a surviving outbox row would be the only trace left, and
        // it would be a false one.
        await transport.PublishAsync(new TriggerFailure("JOB-RACE"), MessageEnvelope.New(child.CorrelationId));

        var childState = await provider.GetRequiredService<ISagaSnapshotStore<TestRiskyChildState>>()
            .FindAsync(nameof(TestRiskyChildSaga), child.CorrelationId);
        Assert.NotNull(childState);
        Assert.Equal(SagaStatus.Running, childState.Status); // the Failed transition never actually committed

        var outbox = provider.GetRequiredService<ISagaOutboxStore>();
        var claimable = await outbox.ClaimPendingAsync(DateTimeOffset.UtcNow.AddYears(1), batchSize: 100);
        Assert.DoesNotContain(claimable, m => string.Equals(m.MessageTypeName, nameof(ChildSagaFinished), StringComparison.Ordinal));

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);
    }
}
