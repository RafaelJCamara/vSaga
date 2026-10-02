using System.Text;
using System.Text.Json;
using VSaga.Abstractions.Notifications;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using RaceSaga = VSaga.Core.Tests.SagaOrchestratorConcurrencyRedeliveryTests.ConcurrencyRedeliveryRaceSaga;
using RaceState = VSaga.Core.Tests.SagaOrchestratorConcurrencyRedeliveryTests.ConcurrencyRedeliveryRaceSagaState;

namespace VSaga.Core.Tests;

/// <summary>
/// docs/design/dashboard-usability-and-access.md §6.3: after every committed transition (step success,
/// step failure, the timeout's final persist, delivery exhaustion) the engine appends a StatePersisted
/// entry carrying the state exactly as the snapshot store wrote it, after the persist and before the
/// drain, and nothing for a transition that did not commit.
/// </summary>
public sealed class StateSnapshotTests
{
    private static readonly string ProbeType = nameof(SnapshotProbeSaga);

    /// <summary>The notifications that end a probe's first step: the snapshot after the step's bookkeeping, SagaUpdated after the snapshot.</summary>
    private static readonly string[] FirstStepNotificationTail = ["StepSucceeded", "TimeoutScheduled", "StatePersisted", "SagaUpdated"];

    // Not nameof(RaceSaga): nameof of a using alias yields the alias.
    private static readonly string RaceType = nameof(SagaOrchestratorConcurrencyRedeliveryTests.ConcurrencyRedeliveryRaceSaga);

    [Fact]
    public async Task StepSuccess_RecordsOneSnapshotPerHandledMessage_AfterItsBookkeeping_BeforeTheNextStep()
    {
        await using var host = await SnapshotTestHost.StartAsync(o => o.AddSaga<TestOrderSaga, TestOrderSagaState>());
        var correlationId = Guid.NewGuid();
        var envelopes = new[] { MessageEnvelope.New(correlationId), MessageEnvelope.New(correlationId), MessageEnvelope.New(correlationId) };

        await host.Transport.PublishAsync(new OrderSubmitted("ORD-SNAP-1", 12m), envelopes[0]);
        await host.Transport.PublishAsync(new InventoryReserved(), envelopes[1]);
        await host.Transport.PublishAsync(new PaymentCharged(), envelopes[2]);

        var timeline = await host.EventLog.GetTimelineAsync(nameof(TestOrderSaga), correlationId);
        Assert.Equal(3, timeline.Count(e => e.EntryType == SagaEntryType.StatePersisted));

        foreach (var (envelope, messageType) in envelopes.Zip([nameof(OrderSubmitted), nameof(InventoryReserved), nameof(PaymentCharged)]))
        {
            var snapshot = Assert.Single(timeline, e => e.EntryType == SagaEntryType.StatePersisted && Same(e.MessageId, envelope.MessageId));
            Assert.Equal(messageType, snapshot.MessageType);
            Assert.Null(snapshot.FromState);
            Assert.Null(snapshot.ToState);

            var stepEntries = timeline.Where(e => Same(e.MessageId, envelope.MessageId) && e.EntryType is SagaEntryType.StepSucceeded
                or SagaEntryType.TimeoutScheduled or SagaEntryType.SagaCompleted).ToList();
            Assert.NotEmpty(stepEntries);
            Assert.All(stepEntries, e => Assert.True(e.SequenceNumber < snapshot.SequenceNumber, $"{e.EntryType} follows its snapshot"));

            var nextReceived = timeline.FirstOrDefault(e => e.EntryType == SagaEntryType.MessageReceived && e.SequenceNumber > snapshot.SequenceNumber);
            var precedingReceived = timeline.Last(e => e.EntryType == SagaEntryType.MessageReceived && e.SequenceNumber < snapshot.SequenceNumber);
            Assert.Equal(envelope.MessageId, precedingReceived.MessageId);
            Assert.True(nextReceived is null || !Same(nextReceived.MessageId, envelope.MessageId));
        }
    }

    /// <summary>The snapshot text equals the blob the store holds after every kind of persist, because Core makes the same generic Serialize call on the same object the store just wrote.</summary>
    [Theory]
    [InlineData("insert")]
    [InlineData("update")]
    [InlineData("business-key")]
    [InlineData("step-failure")]
    [InlineData("timeout")]
    [InlineData("exhaustion")]
    public async Task Snapshot_EqualsTheStoredBlob_AfterEveryKindOfPersist(string kind)
    {
        await using var host = await SnapshotTestHost.StartAsync(
            o => o.AddSaga<SnapshotProbeSaga, SnapshotProbeState>().AddSaga<RaceSaga, RaceState>(),
            s =>
            {
                s.AddSingleton(new SagaOrchestratorOptions { MaxDeliveryAttempts = 0 });
                HookedSnapshotStore<SnapshotProbeState>.Register(s);
            });

        var (sagaType, correlationId) = await DriveAsync(host, kind);

        var snapshots = await host.SnapshotsAsync(sagaType, correlationId);
        Assert.NotEmpty(snapshots);
        Assert.Equal(await host.Reader.GetDataJsonAsync(sagaType, correlationId), snapshots[^1].PayloadJson);
    }

    private static async Task<(string SagaType, Guid CorrelationId)> DriveAsync(SnapshotTestHost host, string kind)
    {
        var correlationId = Guid.NewGuid();
        if (string.Equals(kind, "business-key", StringComparison.Ordinal))
        {
            await host.Transport.PublishAsync(new SagaOrchestratorConcurrencyRedeliveryTests.OrderOpened("ORD-SNAP-BK"), MessageEnvelope.New(correlationId));
            var opened = await host.Provider.GetRequiredService<ISagaSnapshotStore<RaceState>>().FindByBusinessKeyAsync(RaceType, "ORD-SNAP-BK");
            return (RaceType, opened!.CorrelationId);
        }

        await host.Transport.PublishAsync(new ProbeStarted("REF-BLOB"), MessageEnvelope.New(correlationId));
        switch (kind)
        {
            case "update":
                await host.Transport.PublishAsync(new ProbeAdvanced(), MessageEnvelope.New(correlationId));
                break;
            case "step-failure":
                await host.Transport.PublishAsync(new ProbeBroken(), MessageEnvelope.New(correlationId));
                break;
            case "timeout":
                await host.FireTimeoutAsync<SnapshotProbeState>(ProbeType, correlationId);
                break;
            case "exhaustion":
                HookedSnapshotStore<SnapshotProbeState>.From(host.Provider).ThrowOnNextUpdate = true;
                await host.Transport.PublishAsync(new ProbeAdvanced(), MessageEnvelope.New(correlationId));
                break;
        }

        return (ProbeType, correlationId);
    }

    /// <summary>
    /// Core is now an independent serialisation site for the state (ISagaSnapshotStore's clause on the
    /// blob format), so the exact text is pinned: default System.Text.Json, PascalCase, enums as numbers,
    /// the engine's own fields included, the derived type's properties first.
    /// </summary>
    [Fact]
    public async Task Snapshot_MatchesTheGoldenText_ForAFixedClockAndCorrelationId()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero));
        var correlationId = Guid.Parse("4f7d1a2e-0b3c-4d5e-8f60-718293a4b5c6");
        await using var host = await SnapshotTestHost.StartProbeAsync(s => s.AddSingleton<TimeProvider>(clock));

        await host.Transport.PublishAsync(new ProbeStarted("REF-GOLDEN & co"), MessageEnvelope.New(correlationId));

        var snapshot = Assert.Single(await host.SnapshotsAsync(ProbeType, correlationId));
        Assert.Equal(
            "{\"Reference\":\"REF-GOLDEN \\u0026 co\",\"Steps\":0,\"Note\":null,\"CorrelationId\":\"4f7d1a2e-0b3c-4d5e-8f60-718293a4b5c6\"," +
            "\"SagaType\":\"SnapshotProbeSaga\",\"Kind\":0,\"CurrentState\":\"Working\",\"Status\":0,\"Version\":0," +
            "\"ParentSagaType\":null,\"ParentCorrelationId\":null,\"BusinessKey\":null," +
            "\"CreatedAtUtc\":\"2025-03-04T05:06:07+00:00\",\"UpdatedAtUtc\":\"2025-03-04T05:06:07+00:00\"}",
            snapshot.PayloadJson);

        // An update bumps Version inside UpdateAsync, so this text also pins the order: a snapshot taken
        // before the persist would still read version 0.
        await host.Transport.PublishAsync(new ProbeAdvanced(), MessageEnvelope.New(correlationId));

        var updated = (await host.SnapshotsAsync(ProbeType, correlationId))[^1];
        Assert.Equal(
            "{\"Reference\":\"REF-GOLDEN \\u0026 co\",\"Steps\":1,\"Note\":null,\"CorrelationId\":\"4f7d1a2e-0b3c-4d5e-8f60-718293a4b5c6\"," +
            "\"SagaType\":\"SnapshotProbeSaga\",\"Kind\":0,\"CurrentState\":\"Working\",\"Status\":0,\"Version\":1," +
            "\"ParentSagaType\":null,\"ParentCorrelationId\":null,\"BusinessKey\":null," +
            "\"CreatedAtUtc\":\"2025-03-04T05:06:07+00:00\",\"UpdatedAtUtc\":\"2025-03-04T05:06:07+00:00\"}",
            updated.PayloadJson);
    }

    [Fact]
    public async Task StepFailure_RecordsTheFailedState_WithTheMutationMadeBeforeTheThrow()
    {
        await using var host = await SnapshotTestHost.StartProbeAsync();
        var correlationId = Guid.NewGuid();
        var broken = MessageEnvelope.New(correlationId);

        await host.Transport.PublishAsync(new ProbeStarted("REF-FAIL"), MessageEnvelope.New(correlationId));
        await host.Transport.PublishAsync(new ProbeBroken(), broken);

        var snapshot = Assert.Single(await host.SnapshotsAsync(ProbeType, correlationId), e => Same(e.MessageId, broken.MessageId));
        Assert.Equal(nameof(ProbeBroken), snapshot.MessageType);
        Assert.Contains("\"Status\":2", snapshot.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"Note\":\"touched before the throw\"", snapshot.PayloadJson, StringComparison.Ordinal);

        var timeline = await host.EventLog.GetTimelineAsync(ProbeType, correlationId);
        var failed = Assert.Single(timeline, e => e.EntryType == SagaEntryType.StepFailed);
        Assert.True(failed.SequenceNumber < snapshot.SequenceNumber);
    }

    [Fact]
    public async Task Timeout_RecordsExactlyOneSnapshot_TwoVersionsOn_WithNoMessageIdentity()
    {
        await using var host = await SnapshotTestHost.StartProbeAsync();
        var correlationId = Guid.NewGuid();
        await host.Transport.PublishAsync(new ProbeStarted("REF-TIMEOUT"), MessageEnvelope.New(correlationId));
        var before = await host.Reader.GetAsync(ProbeType, correlationId);

        await host.FireTimeoutAsync<SnapshotProbeState>(ProbeType, correlationId);

        var timeline = await host.EventLog.GetTimelineAsync(ProbeType, correlationId);
        var fired = Assert.Single(timeline, e => e.EntryType == SagaEntryType.TimeoutFired);
        var snapshot = Assert.Single(timeline, e => e.EntryType == SagaEntryType.StatePersisted && e.SequenceNumber > fired.SequenceNumber);
        Assert.Null(snapshot.MessageType);
        Assert.Null(snapshot.MessageId);
        Assert.Equal(before!.Version + 2, VersionOf(snapshot));
        Assert.Contains("\"Status\":5", snapshot.PayloadJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// The drained loopback is dispatched synchronously from inside the drain, so its whole step (and its
    /// higher-version snapshot) runs before the drain returns. The timeout's snapshot must already be
    /// stored by then: snapshot versions ascend in timeline order.
    /// </summary>
    [Fact]
    public async Task Timeout_SnapshotPrecedesTheDrainedLoopbacksStep_AndVersionsAscend()
    {
        await using var host = await SnapshotTestHost.StartAsync(o => o.AddSaga<TimeoutDrainTestSaga, TimeoutDrainTestState>());
        var correlationId = Guid.NewGuid();
        await host.Transport.PublishAsync(new BeginDrainTest("ORD-SNAP-DRAIN"), MessageEnvelope.New(correlationId));

        await host.FireTimeoutAsync<TimeoutDrainTestState>(nameof(TimeoutDrainTestSaga), correlationId);

        var timeline = await host.EventLog.GetTimelineAsync(nameof(TimeoutDrainTestSaga), correlationId);
        var snapshots = timeline.Where(e => e.EntryType == SagaEntryType.StatePersisted).ToList();
        Assert.Equal(3, snapshots.Count);
        Assert.Equal(snapshots.Select(VersionOf).Order(), snapshots.Select(VersionOf));

        var timeoutSnapshot = Assert.Single(snapshots, e => e.MessageId is null);
        var loopback = Assert.Single(timeline, e => e.EntryType == SagaEntryType.MessageReceived && Same(e.MessageType, nameof(DrainLoopbackAck)));
        Assert.True(timeoutSnapshot.SequenceNumber < loopback.SequenceNumber);
    }

    [Fact]
    public async Task UnhandledTimeout_BumpsTheVersionButRecordsNoSnapshot()
    {
        await using var host = await SnapshotTestHost.StartAsync(o => o.AddSaga<TestOrderSaga, TestOrderSagaState>());
        var correlationId = Guid.NewGuid();
        await host.Transport.PublishAsync(new OrderSubmitted("ORD-SNAP-UNHANDLED", 3m), MessageEnvelope.New(correlationId));
        var before = await host.Reader.GetAsync(nameof(TestOrderSaga), correlationId);

        // AwaitingInventory declares no WithTimeout, so the claim commits and the definition declines it.
        var timeout = new SagaTimeout(0, correlationId, nameof(TestOrderSaga), nameof(TestOrderSaga.AwaitingInventory), DateTimeOffset.UtcNow, SagaTimeoutStatus.Pending);
        await using (var scope = host.Provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<SagaOrchestrator<TestOrderSagaState>>().HandleTimeoutAsync(timeout, CancellationToken.None);

        Assert.Equal(before!.Version + 1, (await host.Reader.GetAsync(nameof(TestOrderSaga), correlationId))!.Version);
        Assert.Single(await host.SnapshotsAsync(nameof(TestOrderSaga), correlationId));
    }

    [Fact]
    public async Task DeliveryExhaustion_RecordsTheFailedState_UnderTheDeadLetteredMessage_EvenPastTheBudget()
    {
        await using var host = await SnapshotTestHost.StartProbeAsync(s =>
        {
            s.AddSingleton(new SagaOrchestratorOptions { MaxDeliveryAttempts = 0, MaxStateSnapshotBytesPerSaga = 1 });
            HookedSnapshotStore<SnapshotProbeState>.Register(s);
        });
        var correlationId = Guid.NewGuid();
        var deadLettered = MessageEnvelope.New(correlationId);
        await host.Transport.PublishAsync(new ProbeStarted("REF-DLQ"), MessageEnvelope.New(correlationId));

        HookedSnapshotStore<SnapshotProbeState>.From(host.Provider).ThrowOnNextUpdate = true;
        await host.Transport.PublishAsync(new ProbeAdvanced(), deadLettered);

        var snapshot = Assert.Single(await host.SnapshotsAsync(ProbeType, correlationId), e => Same(e.MessageId, deadLettered.MessageId));
        Assert.Equal(nameof(ProbeAdvanced), snapshot.MessageType);
        Assert.Contains("\"Status\":2", snapshot.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(await host.Reader.GetDataJsonAsync(ProbeType, correlationId), snapshot.PayloadJson);
    }

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

    private static int VersionOf(SagaLogEntry snapshot)
    {
        using var document = JsonDocument.Parse(snapshot.PayloadJson!);
        return document.RootElement.GetProperty("Version").GetInt32();
    }

    /// <summary>
    /// SagaOrchestratorConcurrencyRedeliveryTests' race: B and C reach one instance under different
    /// transport ids, C commits first and B's persist loses. Only C's transition was stored, so only C
    /// has a snapshot, and B's redelivery is a duplicate that records nothing either.
    /// </summary>
    [Fact]
    public async Task LostStepRace_RecordsTheWinnersSnapshotOnly()
    {
        await using var host = await SnapshotTestHost.StartAsync(o => o.AddSaga<RaceSaga, RaceState>(), HookedSnapshotStore<RaceState>.Register);
        const string businessKey = "ORD-SNAP-RACE";
        await host.Transport.PublishAsync(new SagaOrchestratorConcurrencyRedeliveryTests.OrderOpened(businessKey), MessageEnvelope.New(Guid.NewGuid()));

        var envelopeB = MessageEnvelope.New(Guid.NewGuid());
        var envelopeC = MessageEnvelope.New(Guid.NewGuid());
        HookedSnapshotStore<RaceState>.From(host.Provider).OnNextFindByBusinessKey = () =>
            host.Transport.PublishAsync(new SagaOrchestratorConcurrencyRedeliveryTests.ConfirmationReceived(businessKey), envelopeC);
        await host.Transport.PublishAsync(new SagaOrchestratorConcurrencyRedeliveryTests.ConfirmationReceived(businessKey), envelopeB);

        var winner = await host.Provider.GetRequiredService<ISagaSnapshotStore<RaceState>>().FindByBusinessKeyAsync(RaceType, businessKey);
        var snapshots = await host.SnapshotsAsync(RaceType, winner!.CorrelationId);
        var confirmation = Assert.Single(snapshots, e => Same(e.MessageType, nameof(SagaOrchestratorConcurrencyRedeliveryTests.ConfirmationReceived)));
        Assert.Equal(envelopeC.MessageId, confirmation.MessageId);
        Assert.DoesNotContain(snapshots, e => Same(e.MessageId, envelopeB.MessageId));
        Assert.Equal(await host.Reader.GetDataJsonAsync(RaceType, winner.CorrelationId), snapshots[^1].PayloadJson);
    }

    /// <summary>TimeoutDrainTests' nudging race: a message commits between the timeout's claim and its final persist, which then loses. The losing timeout records nothing.</summary>
    [Fact]
    public async Task LostTimeoutRace_RecordsNoSnapshotForTheTimeout()
    {
        await using var host = await SnapshotTestHost.StartAsync(
            o => o.AddSaga<TimeoutDrainTests.NudgingTimeoutDrainTestSaga, TimeoutDrainTestState>(), HookedSnapshotStore<TimeoutDrainTestState>.Register);
        var sagaType = nameof(TimeoutDrainTests.NudgingTimeoutDrainTestSaga);
        var correlationId = Guid.NewGuid();
        await host.Transport.PublishAsync(new BeginDrainTest("ORD-SNAP-NUDGE"), MessageEnvelope.New(correlationId));

        HookedSnapshotStore<TimeoutDrainTestState>.From(host.Provider).OnNextUpdate = () =>
            host.Transport.PublishAsync(new TimeoutDrainTests.NudgeVersion(), MessageEnvelope.New(correlationId));
        await host.FireTimeoutAsync<TimeoutDrainTestState>(sagaType, correlationId);

        var snapshots = await host.SnapshotsAsync(sagaType, correlationId);
        Assert.Equal(new string?[] { nameof(BeginDrainTest), nameof(TimeoutDrainTests.NudgeVersion) }, snapshots.Select(e => e.MessageType), StringComparer.Ordinal);
        Assert.DoesNotContain(snapshots, e => e.MessageId is null);
    }

    [Fact]
    public async Task UnexpectedEventAndDuplicateDelivery_RecordNoSnapshot()
    {
        await using var host = await SnapshotTestHost.StartAsync(o => o.AddSaga<TestOrderSaga, TestOrderSagaState>());
        var correlationId = Guid.NewGuid();
        var reserved = MessageEnvelope.New(correlationId);
        await host.Transport.PublishAsync(new OrderSubmitted("ORD-SNAP-DUP", 5m), MessageEnvelope.New(correlationId));

        await host.Transport.PublishAsync(new PaymentCharged(), MessageEnvelope.New(correlationId));
        await host.Transport.PublishAsync(new InventoryReserved(), reserved);
        await host.Transport.PublishAsync(new InventoryReserved(), reserved);

        var timeline = await host.EventLog.GetTimelineAsync(nameof(TestOrderSaga), correlationId);
        Assert.Single(timeline, e => e.EntryType == SagaEntryType.UnexpectedEvent);
        Assert.Equal(new string?[] { nameof(OrderSubmitted), nameof(InventoryReserved) },
            timeline.Where(e => e.EntryType == SagaEntryType.StatePersisted).Select(e => e.MessageType), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Notifier_SeesTheSnapshot_WithItsStoredSequenceNumberAndPayload()
    {
        var notifier = new RecordingChangeNotifier();
        await using var host = await SnapshotTestHost.StartProbeAsync(s => s.AddSingleton<ISagaChangeNotifier>(notifier));
        var correlationId = Guid.NewGuid();

        await host.Transport.PublishAsync(new ProbeStarted("REF-NOTIFY"), MessageEnvelope.New(correlationId));

        var stored = Assert.Single(await host.SnapshotsAsync(ProbeType, correlationId));
        var notified = Assert.Single(notifier.Entries, e => e.EntryType == SagaEntryType.StatePersisted);
        Assert.True(notified.SequenceNumber > 0);
        Assert.Equal(stored, notified);

        // SagaUpdated follows the snapshot, so a subscriber that refetches on it finds the entry.
        Assert.Equal(FirstStepNotificationTail, notifier.Calls.Skip(notifier.Calls.Count - FirstStepNotificationTail.Length), StringComparer.Ordinal);
    }

    /// <summary>
    /// The success-path counterpart of the timeout ordering test: the step's deferred loopback is handled
    /// from inside its drain, so the step's own snapshot must be stored before the drain starts.
    /// </summary>
    [Fact]
    public async Task StepSuccess_SnapshotPrecedesTheDrainedLoopbacksStep_AndVersionsAscend()
    {
        await using var host = await SnapshotTestHost.StartProbeAsync();
        var correlationId = Guid.NewGuid();
        var looped = MessageEnvelope.New(correlationId);
        await host.Transport.PublishAsync(new ProbeStarted("REF-LOOP"), MessageEnvelope.New(correlationId));

        await host.Transport.PublishAsync(new ProbeLooped(), looped);

        var timeline = await host.EventLog.GetTimelineAsync(ProbeType, correlationId);
        var snapshots = timeline.Where(e => e.EntryType == SagaEntryType.StatePersisted).ToList();
        Assert.Equal(3, snapshots.Count);
        Assert.Equal(snapshots.Select(VersionOf).Order(), snapshots.Select(VersionOf));

        var loopedSnapshot = Assert.Single(snapshots, e => Same(e.MessageId, looped.MessageId));
        var loopback = Assert.Single(timeline, e => e.EntryType == SagaEntryType.MessageReceived && Same(e.MessageType, nameof(ProbeLoopback)));
        Assert.True(loopedSnapshot.SequenceNumber < loopback.SequenceNumber);
        Assert.Equal(nameof(SnapshotProbeSaga.Working), (await host.Reader.GetAsync(ProbeType, correlationId))!.CurrentState);
    }

    [Fact]
    public async Task RecordStateSnapshotsOff_RecordsNone_OnAnyPath()
    {
        await using var host = await SnapshotTestHost.StartProbeAsync(s => s.AddSingleton(new SagaOrchestratorOptions { RecordStateSnapshots = false }));
        var succeeded = Guid.NewGuid();
        var failed = Guid.NewGuid();

        await host.Transport.PublishAsync(new ProbeStarted("REF-OFF-1"), MessageEnvelope.New(succeeded));
        await host.FireTimeoutAsync<SnapshotProbeState>(ProbeType, succeeded);
        await host.Transport.PublishAsync(new ProbeStarted("REF-OFF-2"), MessageEnvelope.New(failed));
        await host.Transport.PublishAsync(new ProbeBroken(), MessageEnvelope.New(failed));

        Assert.Empty(await host.SnapshotsAsync(ProbeType, succeeded));
        Assert.Empty(await host.SnapshotsAsync(ProbeType, failed));
        Assert.Equal(SagaStatus.TimedOut, (await host.Reader.GetAsync(ProbeType, succeeded))!.Status);
    }

    /// <summary>
    /// The per-snapshot cap, tuned to the state's own length under a fixed clock (ProbeAdvanced keeps the
    /// length: Steps and Version stay one digit). The reference holds an e-acute, which System.Text.Json
    /// writes as its six-character escape, so the marker reports the stored text's UTF-8 length.
    /// </summary>
    [Fact]
    public async Task MaxStateSnapshotBytes_GivesTheExactMarkerAboveIt_AndTheBlobAtIt()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero));
        var options = new SagaOrchestratorOptions { MaxStateSnapshotBytes = 0 };
        await using var host = await SnapshotTestHost.StartProbeAsync(s => s.AddSingleton<TimeProvider>(clock).AddSingleton(options));
        var correlationId = Guid.NewGuid();

        await host.Transport.PublishAsync(new ProbeStarted("Ren" + (char)0x00E9), MessageEnvelope.New(correlationId));
        var blob = (await host.Reader.GetDataJsonAsync(ProbeType, correlationId))!;
        var bytes = Encoding.UTF8.GetByteCount(blob);
        Assert.Contains("Ren\\u00E9", blob, StringComparison.Ordinal);
        Assert.Equal($"{{\"$vsagaStateOmitted\":true,\"bytes\":{bytes},\"limit\":0}}", (await host.SnapshotsAsync(ProbeType, correlationId))[^1].PayloadJson);

        options.MaxStateSnapshotBytes = bytes - 1;
        await host.Transport.PublishAsync(new ProbeAdvanced(), MessageEnvelope.New(correlationId));
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount((await host.Reader.GetDataJsonAsync(ProbeType, correlationId))!));
        Assert.Equal($"{{\"$vsagaStateOmitted\":true,\"bytes\":{bytes},\"limit\":{bytes - 1}}}", (await host.SnapshotsAsync(ProbeType, correlationId))[^1].PayloadJson);

        options.MaxStateSnapshotBytes = bytes;
        await host.Transport.PublishAsync(new ProbeAdvanced(), MessageEnvelope.New(correlationId));
        Assert.Equal(await host.Reader.GetDataJsonAsync(ProbeType, correlationId), (await host.SnapshotsAsync(ProbeType, correlationId))[^1].PayloadJson);
    }

    /// <summary>
    /// The per-saga budget (feasibility review finding 1): the first snapshot fits it exactly; the next
    /// success-path snapshot would pass it and becomes the budget marker; the step-failure snapshot after
    /// it is still recorded in full, because that is what an investigation needs.
    /// </summary>
    [Fact]
    public async Task PerSagaBudget_TurnsSuccessSnapshotsIntoMarkersPastIt_ButKeepsTheFailureSnapshotFull()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero));
        var options = new SagaOrchestratorOptions { MaxStateSnapshotBytesPerSaga = 0 };
        await using var host = await SnapshotTestHost.StartProbeAsync(s => s.AddSingleton<TimeProvider>(clock).AddSingleton(options));
        var correlationId = Guid.NewGuid();

        await host.Transport.PublishAsync(new ProbeStarted("REF-BUDGET"), MessageEnvelope.New(correlationId));
        var first = (await host.Reader.GetDataJsonAsync(ProbeType, correlationId))!;
        var bytes = Encoding.UTF8.GetByteCount(first);
        options.MaxStateSnapshotBytesPerSaga = bytes;

        await host.Transport.PublishAsync(new ProbeAdvanced(), MessageEnvelope.New(correlationId));
        await host.Transport.PublishAsync(new ProbeBroken(), MessageEnvelope.New(correlationId));

        var snapshots = await host.SnapshotsAsync(ProbeType, correlationId);
        Assert.Equal(3, snapshots.Count);
        Assert.Equal(first, snapshots[0].PayloadJson);
        Assert.Equal($"{{\"$vsagaStateOmitted\":true,\"bytes\":{bytes},\"budget\":{bytes}}}", snapshots[1].PayloadJson);
        Assert.Equal(await host.Reader.GetDataJsonAsync(ProbeType, correlationId), snapshots[2].PayloadJson);
        Assert.Contains("\"Status\":2", snapshots[2].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerSagaBudget_AlsoTurnsTheTimeoutSnapshotIntoAMarker()
    {
        await using var host = await SnapshotTestHost.StartProbeAsync(s => s.AddSingleton(new SagaOrchestratorOptions { MaxStateSnapshotBytesPerSaga = 1 }));
        var correlationId = Guid.NewGuid();
        await host.Transport.PublishAsync(new ProbeStarted("REF-BUDGET-TIMEOUT"), MessageEnvelope.New(correlationId));

        await host.FireTimeoutAsync<SnapshotProbeState>(ProbeType, correlationId);

        var snapshots = await host.SnapshotsAsync(ProbeType, correlationId);
        Assert.Equal(2, snapshots.Count);
        Assert.All(snapshots, e => Assert.StartsWith("{\"$vsagaStateOmitted\":true,\"bytes\":", e.PayloadJson, StringComparison.Ordinal));
        Assert.All(snapshots, e => Assert.EndsWith(",\"budget\":1}", e.PayloadJson, StringComparison.Ordinal));
        Assert.Null(snapshots[1].MessageId);
    }
}
