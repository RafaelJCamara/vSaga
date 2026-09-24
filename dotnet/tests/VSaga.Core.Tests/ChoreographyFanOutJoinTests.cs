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
/// Covers <c>ChoreographyEventBuilder.Finalize(Func&lt;TState, SagaStatus?&gt;)</c> through the real
/// orchestrator, using the same fan-out/join shape the OrderProcessing sample's
/// <c>PostShipmentChoreography</c> is built on: the saga completes when the last of three independent
/// branches reports, with no branch assuming it is last.
/// </summary>
public sealed class ChoreographyFanOutJoinTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;
    private readonly TestFanOutChoreography _saga;

    public ChoreographyFanOutJoinTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<TestFanOutChoreography, FanOutState>());

        _provider = services.BuildServiceProvider();
        _transport = _provider.GetRequiredService<InMemoryMessageTransport>();
        _saga = _provider.GetRequiredService<TestFanOutChoreography>();

        foreach (var hosted in _provider.GetServices<IHostedService>())
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);

        await _provider.DisposeAsync();
    }

    private Task<FanOutState?> FindAsync(Guid correlationId) =>
        _provider.GetRequiredService<ISagaSnapshotStore<FanOutState>>().FindAsync(_saga.SagaType, correlationId);

    /// <summary>Publishes each message under one correlation id, in the order given.</summary>
    private async Task PublishAllAsync(Guid correlationId, params object[] messages)
    {
        foreach (var message in messages)
            await _transport.PublishAsync(message, MessageEnvelope.New(correlationId), CancellationToken.None);
    }

    [Fact]
    public async Task StaysRunningUntilTheLastBranchReports()
    {
        var correlationId = Guid.NewGuid();

        await PublishAllAsync(correlationId, new FanOutTriggered("ORD-1"), new BranchAReported("ORD-1"));
        Assert.Equal(SagaStatus.Running, (await FindAsync(correlationId))!.Status);

        await PublishAllAsync(correlationId, new BranchBReported("ORD-1"));

        // Two of three: recorded and handled, but the join condition is not met, so not terminal.
        var twoOfThree = await FindAsync(correlationId);
        Assert.Equal(SagaStatus.Running, twoOfThree!.Status);
        Assert.True(twoOfThree.A);
        Assert.True(twoOfThree.B);
        Assert.False(twoOfThree.C);

        await PublishAllAsync(correlationId, new BranchCReported("ORD-1"));

        var complete = await FindAsync(correlationId);
        Assert.Equal(SagaStatus.Completed, complete!.Status);
        Assert.Equal(_saga.SawC.Name, complete.CurrentState);
    }

    public static TheoryData<string[]> BranchArrivalOrders()
    {
        var data = new TheoryData<string[]>();

        foreach (var order in new[] { "ABC", "ACB", "BAC", "BCA", "CAB", "CBA" })
            data.Add(order.Select(c => c.ToString()).ToArray());

        return data;
    }

    /// <summary>
    /// The property the whole design rests on: whichever branch lands last is the one that completes the
    /// saga. A fixed-status <c>Finalize</c> nominating a single finisher would pass for exactly one of
    /// these six orders and leave the saga Running for the other five.
    /// </summary>
    [Theory]
    [MemberData(nameof(BranchArrivalOrders))]
    public async Task CompletesOnTheLastBranchWhateverTheArrivalOrder(string[] order)
    {
        var correlationId = Guid.NewGuid();
        await PublishAllAsync(correlationId, new FanOutTriggered("ORD-2"));

        object Branch(string name) => name switch
        {
            "A" => new BranchAReported("ORD-2"),
            "B" => new BranchBReported("ORD-2"),
            _ => new BranchCReported("ORD-2"),
        };

        for (var i = 0; i < order.Length; i++)
        {
            await PublishAllAsync(correlationId, Branch(order[i]));

            var state = await FindAsync(correlationId);
            var expected = i == order.Length - 1 ? SagaStatus.Completed : SagaStatus.Running;
            Assert.Equal(expected, state!.Status);
        }
    }

    /// <summary>
    /// The branches are independent publishers, so one can beat the trigger to this tracker. Each branch
    /// declares StartsNewInstance() for that reason; the saga still converges on the same join.
    /// </summary>
    [Fact]
    public async Task ABranchArrivingBeforeTheTriggerStillOpensAndCompletesTheInstance()
    {
        var correlationId = Guid.NewGuid();

        await PublishAllAsync(correlationId,
            new BranchCReported("ORD-3"),
            new BranchAReported("ORD-3"),
            new FanOutTriggered("ORD-3"),
            new BranchBReported("ORD-3"));

        var state = await FindAsync(correlationId);
        Assert.NotNull(state);
        Assert.Equal(SagaStatus.Completed, state.Status);
        Assert.True(state.A && state.B && state.C);
        Assert.Equal("ORD-3", state.OrderId);
    }

    /// <summary>
    /// The selector is evaluated after the step's own actions, not before — otherwise the branch that
    /// completes the join would never see its own flag and the saga could only complete on a later,
    /// non-existent event.
    /// </summary>
    [Fact]
    public async Task TheFinalBranchSeesItsOwnWriteWhenTheJoinIsEvaluated()
    {
        var correlationId = Guid.NewGuid();

        await PublishAllAsync(correlationId,
            new FanOutTriggered("ORD-4"),
            new BranchAReported("ORD-4"),
            new BranchBReported("ORD-4"));

        Assert.Equal(SagaStatus.Running, (await FindAsync(correlationId))!.Status);

        // C is the branch that both sets the last flag and satisfies the condition, in one step.
        await PublishAllAsync(correlationId, new BranchCReported("ORD-4"));

        var state = await FindAsync(correlationId);
        Assert.True(state!.C);
        Assert.Equal(SagaStatus.Completed, state.Status);
    }

    [Fact]
    public async Task CompletionIsRecordedOnTheTimelineAndSurfacedToTheDashboard()
    {
        var correlationId = Guid.NewGuid();

        await PublishAllAsync(correlationId,
            new FanOutTriggered("ORD-5"),
            new BranchAReported("ORD-5"),
            new BranchBReported("ORD-5"),
            new BranchCReported("ORD-5"));

        var timeline = await _provider.GetRequiredService<ISagaEventLogStore>().GetTimelineAsync(_saga.SagaType, correlationId);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.SagaCompleted);

        // Exactly one: the two non-final branches must not have logged a completion.
        Assert.Single(timeline, e => e.EntryType == SagaEntryType.SagaCompleted);

        var summary = await _provider.GetRequiredService<ISagaSummaryReader>().GetAsync(_saga.SagaType, correlationId);
        Assert.Equal(SagaKind.Choreographed, summary!.Kind);
        Assert.Equal(SagaStatus.Completed, summary.Status);
    }
}
