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
/// Parallel fan-out and join in an <b>orchestrated</b> saga: one step dispatches several commands at
/// once, and the saga gathers their replies, advancing only when the last one lands.
///
/// <para>
/// The fan-out half needed no new DSL (<c>.Publish(...)</c> already chains). The join half is
/// <c>TransitionTo(Func&lt;TState, State&lt;TState&gt;&gt;)</c>, and — where the join is itself the
/// saga's ending — <c>Finalize(Func&lt;TState, SagaStatus?&gt;)</c>.
/// </para>
/// </summary>
public sealed class ParallelFanOutJoinTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;

    public ParallelFanOutJoinTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o
            .AddSaga<TestParallelFulfilmentSaga, ParallelFulfilmentState>()
            .AddSaga<TestTerminalJoinSaga, TerminalJoinState>());

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

    private TestParallelFulfilmentSaga Saga => _provider.GetRequiredService<TestParallelFulfilmentSaga>();

    private Task<ParallelFulfilmentState?> FindAsync(string sagaType, Guid correlationId) =>
        _provider.GetRequiredService<ISagaSnapshotStore<ParallelFulfilmentState>>().FindAsync(sagaType, correlationId);

    private Task<TerminalJoinState?> FindTerminalAsync(string sagaType, Guid correlationId) =>
        _provider.GetRequiredService<ISagaSnapshotStore<TerminalJoinState>>().FindAsync(sagaType, correlationId);

    private async Task PublishAsync(Guid correlationId, params object[] messages)
    {
        foreach (var message in messages)
            await _transport.PublishAsync(message, MessageEnvelope.New(correlationId), CancellationToken.None);
    }

    private static object Branch(string name, string orderId) => name switch
    {
        "S" => new StockReserved(orderId),
        "P" => new PaymentAuthorized(orderId),
        _ => new FraudCheckCleared(orderId),
    };

    [Fact]
    public async Task OneStepDispatchesEveryBranchCommandAtOnce()
    {
        var correlationId = Guid.NewGuid();
        var timeline = new List<string>();

        // The fan-out is observable as three separate outbound messages from a single step.
        await PublishAsync(correlationId, new ParallelOrderPlaced("ORD-1"));

        var log = await _provider.GetRequiredService<ISagaEventLogStore>()
            .GetTimelineAsync(Saga.SagaType, correlationId);

        timeline.AddRange(log
            .Where(e => e.EntryType == SagaEntryType.MessagePublished)
            .Select(e => e.MessageType!));

        Assert.Equal(3, timeline.Count);
        Assert.Contains(nameof(ReserveStock), timeline, StringComparer.Ordinal);
        Assert.Contains(nameof(AuthorizePayment), timeline, StringComparer.Ordinal);
        Assert.Contains(nameof(RunFraudCheck), timeline, StringComparer.Ordinal);
    }

    [Fact]
    public async Task StaysGatheringUntilTheLastBranchReplies()
    {
        var correlationId = Guid.NewGuid();
        await PublishAsync(correlationId, new ParallelOrderPlaced("ORD-2"));

        await PublishAsync(correlationId, new StockReserved("ORD-2"));
        Assert.Equal(Saga.Gathering.Name, (await FindAsync(Saga.SagaType, correlationId))!.CurrentState);

        await PublishAsync(correlationId, new PaymentAuthorized("ORD-2"));
        var twoOfThree = await FindAsync(Saga.SagaType, correlationId);
        Assert.Equal(Saga.Gathering.Name, twoOfThree!.CurrentState);
        Assert.True(twoOfThree.StockReserved && twoOfThree.PaymentAuthorized);
        Assert.False(twoOfThree.FraudCleared);

        await PublishAsync(correlationId, new FraudCheckCleared("ORD-2"));
        Assert.Equal(Saga.ReadyToShip.Name, (await FindAsync(Saga.SagaType, correlationId))!.CurrentState);
    }

    public static TheoryData<string[]> ArrivalOrders()
    {
        var data = new TheoryData<string[]>();

        foreach (var order in new[] { "SPF", "SFP", "PSF", "PFS", "FSP", "FPS" })
            data.Add(order.Select(c => c.ToString()).ToArray());

        return data;
    }

    /// <summary>
    /// The property the join exists for: whichever branch replies last is the one that releases the
    /// saga. A fixed <c>TransitionTo(ReadyToShip)</c> on each branch would advance on the first reply,
    /// with two still outstanding.
    /// </summary>
    [Theory]
    [MemberData(nameof(ArrivalOrders))]
    public async Task ReleasesOnTheLastReplyWhateverTheArrivalOrder(string[] order)
    {
        var correlationId = Guid.NewGuid();
        await PublishAsync(correlationId, new ParallelOrderPlaced("ORD-3"));

        for (var i = 0; i < order.Length; i++)
        {
            await PublishAsync(correlationId, Branch(order[i], "ORD-3"));

            var state = await FindAsync(Saga.SagaType, correlationId);
            var expected = i == order.Length - 1 ? Saga.ReadyToShip.Name : Saga.Gathering.Name;
            Assert.Equal(expected, state!.CurrentState);
        }
    }

    /// <summary>
    /// A branch that leaves the saga gathering is a self-transition, which the orchestrator treats as
    /// "no transition" — so the gather's timeout is neither cancelled nor rescheduled. One deadline
    /// covers the whole gather rather than each arriving branch silently extending it.
    /// </summary>
    [Fact]
    public async Task ArrivingBranchesDoNotResetTheGatherTimeout()
    {
        var correlationId = Guid.NewGuid();
        await PublishAsync(correlationId, new ParallelOrderPlaced("ORD-4"));
        await PublishAsync(correlationId, new StockReserved("ORD-4"), new PaymentAuthorized("ORD-4"));

        var log = await _provider.GetRequiredService<ISagaEventLogStore>()
            .GetTimelineAsync(Saga.SagaType, correlationId);

        // Exactly one schedule, from Placed -> Gathering; the two branch replies added none.
        Assert.Single(log, e => e.EntryType == SagaEntryType.TimeoutScheduled);

        var pending = await _provider.GetRequiredService<ISagaTimeoutStore>()
            .ClaimDueAsync(DateTimeOffset.UtcNow.AddHours(1), batchSize: 10);

        var timeout = Assert.Single(pending, t => t.CorrelationId == correlationId);
        Assert.Equal(Saga.Gathering.Name, timeout.ForState);
    }

    [Fact]
    public async Task AStalledGatherTimesOutAndCompensates()
    {
        var correlationId = Guid.NewGuid();
        await PublishAsync(correlationId, new ParallelOrderPlaced("ORD-5"));
        await PublishAsync(correlationId, new StockReserved("ORD-5"));

        var timeoutStore = _provider.GetRequiredService<ISagaTimeoutStore>();
        var due = await timeoutStore.ClaimDueAsync(DateTimeOffset.UtcNow.AddHours(1), batchSize: 10);
        var timeout = Assert.Single(due, t => t.CorrelationId == correlationId);

        await _provider.GetRequiredService<VSaga.Core.Runtime.SagaOrchestrator<ParallelFulfilmentState>>()
            .HandleTimeoutAsync(timeout, CancellationToken.None);

        var state = await FindAsync(Saga.SagaType, correlationId);
        Assert.Equal(Saga.Abandoned.Name, state!.CurrentState);
        Assert.Equal(SagaStatus.TimedOut, state.Status);
    }

    /// <summary>
    /// When the join *is* the ending, the last branch must both release and finish the saga. A fixed
    /// <c>Finalize(Completed)</c> on each branch would complete it on the first reply instead.
    /// </summary>
    [Theory]
    [MemberData(nameof(ArrivalOrders))]
    public async Task ATerminalJoinCompletesOnlyOnTheLastReply(string[] order)
    {
        var terminal = _provider.GetRequiredService<TestTerminalJoinSaga>();
        var correlationId = Guid.NewGuid();
        await PublishAsync(correlationId, new ParallelOrderPlaced("ORD-6"));

        for (var i = 0; i < order.Length; i++)
        {
            await PublishAsync(correlationId, Branch(order[i], "ORD-6"));

            var state = await FindTerminalAsync(terminal.SagaType, correlationId);
            var isLast = i == order.Length - 1;

            Assert.Equal(isLast ? SagaStatus.Completed : SagaStatus.Running, state!.Status);
            Assert.Equal(isLast ? terminal.Done.Name : terminal.Gathering.Name, state.CurrentState);
        }
    }
}
