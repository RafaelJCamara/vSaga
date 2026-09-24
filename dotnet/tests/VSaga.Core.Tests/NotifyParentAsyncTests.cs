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
/// Slice 2a: a child can address its parent directly, and a parent can actually wait rather than only
/// parking until its own timeout (Slice 1's <c>TestFulfilmentSaga</c>/<c>TestParcelSaga</c>).
///
/// <para>
/// <b>Every test here drives the real publish → receive → orchestrator path</b>, for the same reason
/// <c>SubSagaCompositionTests</c> does: this repo has previously shipped envelope-header threading the
/// orchestrator never actually read, and tests that hand-set the field prove nothing. Nothing below ever
/// assigns <c>ParentCorrelationId</c> or stamps a header — the only way <c>NotifyParentAsync</c> gets
/// exercised for real is <c>ctx.StartChildAsync</c> publishing, the transport delivering, the
/// orchestrator stamping the child's state, and then <c>ctx.NotifyParentAsync</c> reading it back off
/// that state and publishing under it.
/// </para>
/// </summary>
public sealed class NotifyParentAsyncTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;

    public NotifyParentAsyncTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o
            .AddSaga<TestWaitingParentSaga, TestWaitingParentState>()
            .AddSaga<TestReportingChildSaga, TestReportingChildState>()
            .AddSaga<TestOrphanSaga, TestOrphanState>()
            .AddSaga<TestRacyParentSaga, TestRacyParentState>()
            .AddSaga<TestImmediatelyReportingChildSaga, TestImmediatelyReportingChildState>());

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

    private Task<TestWaitingParentState?> FindParentAsync(Guid correlationId) =>
        _provider.GetRequiredService<ISagaSnapshotStore<TestWaitingParentState>>().FindAsync(nameof(TestWaitingParentSaga), correlationId);

    /// <summary>Starts the parent, finds the child StartChildAsync created for it, and drives the child's own second message — the part a real participant round-trip stands in for. See TestReportingChildSaga's doc comment for why this can't be one message.</summary>
    private async Task<Guid> StartJobAndDriveTheChildAsync(string jobId)
    {
        var parentId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginJob(jobId), MessageEnvelope.New(parentId));

        var reader = _provider.GetRequiredService<ISagaSummaryReader>();
        var child = Assert.Single(await reader.FindChildrenAsync(nameof(TestWaitingParentSaga), parentId));

        await _transport.PublishAsync(new ItemActuallyProcessed(jobId), MessageEnvelope.New(child.CorrelationId));

        return parentId;
    }

    [Fact]
    public async Task NotifyParentAsync_ReleasesTheParentsWaitWithTheChildsActualResult()
    {
        var parentId = await StartJobAndDriveTheChildAsync("JOB-1");

        var parent = await FindParentAsync(parentId);

        Assert.NotNull(parent);
        Assert.Equal(nameof(TestWaitingParentSaga.Done), parent.CurrentState);
        Assert.Equal(SagaStatus.Completed, parent.Status);

        // Not just "the child finished" — the actual payload NotifyParentAsync carries. An
        // engine-published ChildSagaFinished(status) could tell the parent this happened; it could not
        // tell it what happened.
        Assert.True(parent.ChildSucceeded);
    }

    [Fact]
    public async Task NotifyParentAsync_IsLoggedOnTheChildsOwnTimeline_AsAnOrdinaryPublish()
    {
        // Unlike StartChildAsync's fixed-shape hop (retagged ChildSagaStarted in Slice 2b),
        // NotifyParentAsync publishes a caller-defined domain message — there is no single type to give
        // a dedicated entry type to, so this stays an ordinary MessagePublished. Only the engine's own
        // ChildSagaFinished publish (the Slice 2b safety net, a fixed contract type) gets its own entry
        // type — see ChildSagaFinishedTests.
        var parentId = await StartJobAndDriveTheChildAsync("JOB-2");

        var log = _provider.GetRequiredService<ISagaEventLogStore>();
        var reader = _provider.GetRequiredService<ISagaSummaryReader>();

        var child = Assert.Single(await reader.FindChildrenAsync(nameof(TestWaitingParentSaga), parentId));
        var childTimeline = await log.GetTimelineAsync(nameof(TestReportingChildSaga), child.CorrelationId);

        var published = Assert.Single(childTimeline, e => e.EntryType == SagaEntryType.MessagePublished);
        Assert.Equal(nameof(ItemProcessed), published.MessageType);

        // And on the parent's side it arrives as an ordinary MessageReceived, same as any other message.
        var parentTimeline = await log.GetTimelineAsync(nameof(TestWaitingParentSaga), parentId);
        Assert.Contains(parentTimeline, e => e.EntryType == SagaEntryType.MessageReceived && string.Equals(e.MessageType, nameof(ItemProcessed), StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyParentAsync_FromAChildsOwnInitiatingStep_CanRaceAheadOfTheParentsUnpersistedTransition()
    {
        // A gap worth pinning rather than discovering: InMemoryMessageTransport.DispatchAsync invokes
        // every subscriber synchronously and recursively, so a child that calls NotifyParentAsync from
        // the very same step that StartChildAsync's ProcessItem started it in is still nested inside the
        // parent's own StartChildAsync call — before the parent's HandleStepSuccessAsync has persisted
        // its AwaitingResult transition, let alone inserted the row at all for a brand-new parent. The
        // notification arrives for a saga that, from this recursive call's point of view, does not exist
        // yet; ItemProcessed is not among TestRacyParentSaga's initiating message types, so it is logged
        // as UnexpectedEvent and silently dropped — no exception, no redelivery, and (unlike the
        // optimistic-concurrency conflict two independent messages would hit) nothing here ever retries.
        //
        // Real transports decouple the child's dispatch onto its own consumer rather than nesting it
        // inside the publisher's call stack, so this is not expected to reproduce deterministically over
        // RabbitMQ — every real child in this repo also has genuine I/O (a participant round-trip)
        // between StartChildAsync and NotifyParentAsync, which is what TestReportingChildSaga's own
        // two-message shape exists to model. This test's TestImmediatelyReportingChildSaga is the
        // deliberately-unrealistic, zero-I/O case that makes the underlying hazard reproduce every time.
        var parentId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginRacyJob("JOB-5"), MessageEnvelope.New(parentId));

        var parent = await _provider.GetRequiredService<ISagaSnapshotStore<TestRacyParentState>>()
            .FindAsync(nameof(TestRacyParentSaga), parentId);

        Assert.NotNull(parent);
        Assert.Equal(nameof(TestRacyParentSaga.AwaitingResult), parent.CurrentState);
        Assert.Equal(SagaStatus.Running, parent.Status);
        Assert.Null(parent.ChildSucceeded);

        var log = _provider.GetRequiredService<ISagaEventLogStore>();
        var parentTimeline = await log.GetTimelineAsync(nameof(TestRacyParentSaga), parentId);
        Assert.Contains(parentTimeline, e => e.EntryType == SagaEntryType.UnexpectedEvent && string.Equals(e.MessageType, nameof(ItemProcessed), StringComparison.Ordinal));

        // The child itself is none the wiser — NotifyParentAsync is fire-and-forget, exactly like
        // StartChildAsync, so it completes successfully regardless of whether anything received it.
        var reader = _provider.GetRequiredService<ISagaSummaryReader>();
        var child = Assert.Single(await reader.FindChildrenAsync(nameof(TestRacyParentSaga), parentId));
        Assert.Equal(SagaStatus.Completed, child.Status);
    }

    [Fact]
    public async Task NotifyParentAsync_OnARootSaga_ThrowsBeforePublishingAnything()
    {
        var orphanId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginOrphanJob("JOB-3"), MessageEnvelope.New(orphanId));

        // The Then(...) delegate's exception propagates out of definition.HandleAsync, which
        // SagaOrchestrator's step-failure path catches: the saga ends Failed rather than the process
        // crashing, and — because the throw happens before PublishInternalAsync — no ItemProcessed ever
        // reaches the transport for anyone to receive.
        var orphan = await _provider.GetRequiredService<ISagaSnapshotStore<TestOrphanState>>()
            .FindAsync(nameof(TestOrphanSaga), orphanId);

        Assert.NotNull(orphan);
        Assert.Equal(SagaStatus.Failed, orphan.Status);

        var timeline = await _provider.GetRequiredService<ISagaEventLogStore>().GetTimelineAsync(nameof(TestOrphanSaga), orphanId);
        var failure = Assert.Single(timeline, e => e.EntryType == SagaEntryType.StepFailed);
        Assert.Contains("no parent to notify", failure.ErrorMessage, StringComparison.Ordinal);

        Assert.DoesNotContain(timeline, e => e.EntryType == SagaEntryType.MessagePublished);
    }

    [Fact]
    public async Task TheParentsOwnTimeoutStillCoversAChildThatNeverStarts()
    {
        // Same failure mode SubSagaCompositionTests pins for StartChildAsync: publishing a message
        // nothing initiates on creates no child and tells nobody. NotifyParentAsync changes nothing
        // about that, since nothing exists to call it — the parent's own timeout on AwaitingResult is
        // still the only rescue. What this test pins is specific to NotifyParentAsync's contract: the
        // parent parks exactly as it would have on success. The timeout itself actually firing is
        // already covered by this repo's existing tests for the equivalent Slice 1 case.
        var parentId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginJobWithNoWorker("JOB-4"), MessageEnvelope.New(parentId));

        var parent = await FindParentAsync(parentId);
        Assert.NotNull(parent);
        Assert.Equal(nameof(TestWaitingParentSaga.AwaitingResult), parent.CurrentState);
        Assert.Equal(SagaStatus.Running, parent.Status);
        Assert.Null(parent.ChildSucceeded);

        var reader = _provider.GetRequiredService<ISagaSummaryReader>();
        Assert.Empty(await reader.FindChildrenAsync(nameof(TestWaitingParentSaga), parentId));
    }
}
