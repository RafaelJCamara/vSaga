using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Testing.Tests;

public sealed class SagaTestHarnessTests
{
    [Fact]
    public async Task GivenWhenThen_HappyPath_TransitionsAndPublishes()
    {
        await using var harness = new SagaTestHarness<DemoSaga, DemoSagaState>();

        await harness.Given(Guid.NewGuid())
            .WhenAsync(new OrderPlaced("ORD-1"));

        var state = await harness.AssertStateAsync(harness.Saga.AwaitingShipment);
        Assert.Equal("ORD-1", state.OrderId);

        await harness.WhenAsync(new ShipmentConfirmed());

        await harness.AssertStateAsync(harness.Saga.Shipped);
        await harness.AssertStatusAsync(SagaStatus.Completed);
    }

    [Fact]
    public async Task GivenWhenThen_FailurePath_RunsCompensation()
    {
        await using var harness = new SagaTestHarness<DemoSaga, DemoSagaState>();

        await harness.Given(Guid.NewGuid())
            .WhenAsync(new OrderPlaced("ORD-2"));
        await harness.WhenAsync(new ShipmentFailed());

        await harness.AssertStateAsync(harness.Saga.Failed);
        await harness.AssertStatusAsync(SagaStatus.Failed);
        harness.AssertPublished<ReleaseHold>();
    }

    [Fact]
    public async Task Timeout_FiresDeterministicallyWithoutRealWaiting()
    {
        await using var harness = new SagaTestHarness<DemoSaga, DemoSagaState>();

        await harness.Given(Guid.NewGuid())
            .WhenAsync(new OrderPlaced("ORD-3"));

        await harness.AssertStateAsync(harness.Saga.AwaitingShipment);

        await harness.AdvanceTimeByAsync(TimeSpan.FromMinutes(31));

        await harness.AssertStateAsync(harness.Saga.Failed);
        await harness.AssertStatusAsync(SagaStatus.TimedOut);
    }

    [Fact]
    public async Task NonInitiatingMessage_DoesNotCreateASaga()
    {
        await using var harness = new SagaTestHarness<DemoSaga, DemoSagaState>();

        await harness.Given(Guid.NewGuid())
            .WhenAsync(new ShipmentConfirmed());

        await harness.AssertNoSagaCreatedAsync();
    }

    [Fact]
    public async Task Timeline_ExposesServiceMapFieldsOnOutboundAndCompensationEntries()
    {
        await using var harness = new SagaTestHarness<DemoSaga, DemoSagaState>();

        await harness.Given(Guid.NewGuid())
            .WhenAsync(new OrderPlaced("ORD-4"));
        await harness.WhenAsync(new ShipmentFailed());

        var timeline = await harness.GetTimelineAsync();

        var compensatingPublish = Assert.Single(timeline, e => e.EntryType == SagaEntryType.MessagePublished);
        Assert.Equal(harness.Saga.SagaType, compensatingPublish.SourceService);
        Assert.NotNull(compensatingPublish.MessageId);

        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.CompensationStarted);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.CompensationStepSucceeded);

        harness.AssertPublished<ReleaseHold>();
    }

    /// <summary>The harness runs the real engine, so its timeline holds a StatePersisted snapshot after every committed step, timeouts included, and the last one is the stored state.</summary>
    [Fact]
    public async Task Timeline_ContainsAStateSnapshotPerCommittedStep_ByDefault()
    {
        await using var harness = new SagaTestHarness<DemoSaga, DemoSagaState>();

        await harness.Given(Guid.NewGuid())
            .WhenAsync(new OrderPlaced("ORD-5"));
        await harness.AdvanceTimeByAsync(TimeSpan.FromMinutes(31));

        var timeline = await harness.GetTimelineAsync();
        var snapshots = timeline.Where(e => e.EntryType == SagaEntryType.StatePersisted).ToList();
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(nameof(OrderPlaced), snapshots[0].MessageType);
        Assert.Null(snapshots[1].MessageId);

        var reader = harness.Services.GetRequiredService<ISagaSummaryReader>();
        Assert.Equal(await reader.GetDataJsonAsync(harness.Saga.SagaType, harness.CorrelationId), snapshots[1].PayloadJson);
    }

    /// <summary>A test that asserts on exact timeline counts opts out by registering its own orchestrator options.</summary>
    [Fact]
    public async Task Timeline_HoldsNoStateSnapshots_WhenTheTestOptsOut()
    {
        await using var harness = new SagaTestHarness<DemoSaga, DemoSagaState>(
            s => s.AddSingleton(new SagaOrchestratorOptions { RecordStateSnapshots = false }));

        await harness.Given(Guid.NewGuid())
            .WhenAsync(new OrderPlaced("ORD-6"));
        await harness.WhenAsync(new ShipmentConfirmed());

        var timeline = await harness.GetTimelineAsync();
        Assert.DoesNotContain(timeline, e => e.EntryType == SagaEntryType.StatePersisted);
        await harness.AssertStatusAsync(SagaStatus.Completed);
    }
}
