using VSaga.Abstractions.Notifications;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Core.Tests;

/// <summary>
/// docs/design/dashboard-usability-and-access.md §6.4: a snapshot is best effort. Its append runs after
/// the transition committed, so a failing or stalled append must cost the step its snapshot and nothing
/// else: the deferred publishes, the notifier, the ChildSagaFinished safety net, the timeout's drain and
/// the acknowledgement all still happen, and nothing is redelivered.
/// </summary>
public sealed class StateSnapshotContainmentTests
{
    private const string DeliveryAttemptHeader = "x-vsaga-delivery-attempt";
    private static readonly string ProbeType = nameof(SnapshotProbeSaga);

    [Fact]
    public async Task FailingAppend_OnStepSuccess_StillCommitsDrainsAndAcks()
    {
        await using var host = await SnapshotTestHost.StartProbeAsync(s => SnapshotFaultingEventLog.Register(s, SnapshotAppendFault.Throw));
        var correlationId = Guid.NewGuid();

        await host.Transport.PublishAsync(new ProbeStarted("REF-CONTAIN"), MessageEnvelope.New(correlationId));
        await host.Transport.PublishAsync(new ProbeFinished(), MessageEnvelope.New(correlationId));

        await AssertCompletedAndDrainedAsync(host, correlationId);
    }

    [Fact]
    public async Task FailingAppend_OnStepFailure_StillMarksFailedAndPublishesChildSagaFinished()
    {
        await using var host = await SnapshotTestHost.StartAsync(
            o => o.AddSaga<TestChildSafetyNetParentSaga, TestChildSafetyNetParentState>().AddSaga<TestRiskyChildSaga, TestRiskyChildState>(),
            s => SnapshotFaultingEventLog.Register(s, SnapshotAppendFault.Throw));
        var parentId = Guid.NewGuid();
        await host.Transport.PublishAsync(new BeginSafeguardedJob("JOB-SNAP-CONTAIN"), MessageEnvelope.New(parentId));
        var child = Assert.Single(await host.Reader.FindChildrenAsync(nameof(TestChildSafetyNetParentSaga), parentId));

        await host.Transport.PublishAsync(new TriggerFailure("JOB-SNAP-CONTAIN"), MessageEnvelope.New(child.CorrelationId));

        Assert.Equal(SagaStatus.Failed, (await host.Reader.GetAsync(nameof(TestRiskyChildSaga), child.CorrelationId))!.Status);
        Assert.Contains(host.Transport.GetPublished(), p => p.Message is ChildSagaFinished { Status: SagaStatus.Failed });
        Assert.Equal(nameof(TestChildSafetyNetParentSaga.Rescued), (await host.Reader.GetAsync(nameof(TestChildSafetyNetParentSaga), parentId))!.CurrentState);
        Assert.DoesNotContain(host.Transport.GetPublished(), p => p.Envelope.Headers?.ContainsKey(DeliveryAttemptHeader) == true);
    }

    [Fact]
    public async Task FailingAppend_OnTimeout_StillPersistsTheTransitionAndDrains()
    {
        await using var host = await SnapshotTestHost.StartAsync(
            o => o.AddSaga<TimeoutDrainTestSaga, TimeoutDrainTestState>(), s => SnapshotFaultingEventLog.Register(s, SnapshotAppendFault.Throw));
        var correlationId = Guid.NewGuid();
        await host.Transport.PublishAsync(new BeginDrainTest("ORD-SNAP-CONTAIN"), MessageEnvelope.New(correlationId));

        await host.FireTimeoutAsync<TimeoutDrainTestState>(nameof(TimeoutDrainTestSaga), correlationId);

        var state = await host.Reader.GetAsync(nameof(TimeoutDrainTestSaga), correlationId);
        Assert.Equal(nameof(TimeoutDrainTestSaga.Done), state!.CurrentState);
        Assert.Equal(SagaStatus.Completed, state.Status);
        Assert.Contains(host.Transport.GetPublished(), p => p.Message is DrainLoopbackAck);
        Assert.Empty(await host.SnapshotsAsync(nameof(TimeoutDrainTestSaga), correlationId));
    }

    [Fact]
    public async Task FailingAppend_OnDeliveryExhaustion_StillMarksFailedAndNotifies()
    {
        var notifier = new RecordingChangeNotifier();
        await using var host = await SnapshotTestHost.StartProbeAsync(s =>
        {
            s.AddSingleton(new SagaOrchestratorOptions { MaxDeliveryAttempts = 0 });
            s.AddSingleton<ISagaChangeNotifier>(notifier);
            HookedSnapshotStore<SnapshotProbeState>.Register(s);
            SnapshotFaultingEventLog.Register(s, SnapshotAppendFault.Throw);
        });
        var correlationId = Guid.NewGuid();
        await host.Transport.PublishAsync(new ProbeStarted("REF-CONTAIN-DLQ"), MessageEnvelope.New(correlationId));

        HookedSnapshotStore<SnapshotProbeState>.From(host.Provider).ThrowOnNextUpdate = true;
        await host.Transport.PublishAsync(new ProbeAdvanced(), MessageEnvelope.New(correlationId));

        Assert.Equal(SagaStatus.Failed, (await host.Reader.GetAsync(ProbeType, correlationId))!.Status);
        Assert.Contains(notifier.Summaries, s => s.CorrelationId == correlationId && s.Status == SagaStatus.Failed);
        Assert.Contains(await host.EventLog.GetTimelineAsync(ProbeType, correlationId), e => e.EntryType == SagaEntryType.DeliveryExhausted);
    }

    /// <summary>
    /// Feasibility review finding 3: a stalled append is abandoned at StateSnapshotTimeout instead of
    /// holding the drain and the acknowledgement for as long as the store's own timeout allows.
    /// </summary>
    [Fact]
    public async Task HangingAppend_IsAbandonedAtStateSnapshotTimeout_AndProcessingContinues()
    {
        await using var host = await SnapshotTestHost.StartProbeAsync(s =>
        {
            s.AddSingleton(new SagaOrchestratorOptions { StateSnapshotTimeout = TimeSpan.FromMilliseconds(200) });
            SnapshotFaultingEventLog.Register(s, SnapshotAppendFault.Hang);
        });
        var correlationId = Guid.NewGuid();

        var processing = Task.Run(async () =>
        {
            await host.Transport.PublishAsync(new ProbeStarted("REF-HANG"), MessageEnvelope.New(correlationId));
            await host.Transport.PublishAsync(new ProbeFinished(), MessageEnvelope.New(correlationId));
        });
        var finished = await Task.WhenAny(processing, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(processing, finished);
        await processing;
        Assert.Equal(2, ((SnapshotFaultingEventLog)host.EventLog).AbandonedAppends);
        await AssertCompletedAndDrainedAsync(host, correlationId);
    }

    private static async Task AssertCompletedAndDrainedAsync(SnapshotTestHost host, Guid correlationId)
    {
        var state = await host.Reader.GetAsync(ProbeType, correlationId);
        Assert.Equal(nameof(SnapshotProbeSaga.Done), state!.CurrentState);
        Assert.Equal(SagaStatus.Completed, state.Status);
        Assert.Contains(host.Transport.GetPublished(), p => p.Message is ProbeFinishedAck);
        Assert.Empty(await host.Provider.GetRequiredService<ISagaOutboxStore>().ClaimPendingAsync(DateTimeOffset.UtcNow.AddYears(1), batchSize: 100));
        Assert.DoesNotContain(host.Transport.GetPublished(), p => p.Envelope.Headers?.ContainsKey(DeliveryAttemptHeader) == true);
        Assert.Empty(await host.SnapshotsAsync(ProbeType, correlationId));
    }
}
