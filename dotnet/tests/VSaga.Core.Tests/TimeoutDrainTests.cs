using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Core.Dsl;
using VSaga.Core.Runtime;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

/// <summary>
/// docs/design/mixed-sagas.md §3.1/§5: HandleTimeoutAsync's own drain of ctx.PublishAfterCommitAsync, and the
/// discard path for the race it can't avoid. See TimeoutDrainFixtures for the saga this exercises.
/// </summary>
public sealed class TimeoutDrainTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;
    private readonly TimeoutDrainTestSaga _saga;

    public TimeoutDrainTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<TimeoutDrainTestSaga, TimeoutDrainTestState>());

        _provider = services.BuildServiceProvider();
        _transport = _provider.GetRequiredService<InMemoryMessageTransport>();
        _saga = _provider.GetRequiredService<TimeoutDrainTestSaga>();

        foreach (var hosted in _provider.GetServices<IHostedService>())
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);

        await _provider.DisposeAsync();
    }

    private async Task<SagaTimeout> BeginAndClaimTimeoutAsync(Guid correlationId, string orderId)
    {
        await _transport.PublishAsync(new BeginDrainTest(orderId), MessageEnvelope.New(correlationId));

        var timeoutStore = _provider.GetRequiredService<ISagaTimeoutStore>();
        var due = await timeoutStore.ClaimDueAsync(DateTimeOffset.UtcNow.AddHours(1), batchSize: 10);
        return Assert.Single(due, t => t.CorrelationId == correlationId);
    }

    [Fact]
    public async Task TimeoutQueuingALoopback_DrainsItAndTheSagaReachesItsFinalState()
    {
        var correlationId = Guid.NewGuid();
        var timeout = await BeginAndClaimTimeoutAsync(correlationId, "ORD-DRAIN-1");

        var orchestrator = _provider.GetRequiredService<SagaOrchestrator<TimeoutDrainTestState>>();
        await orchestrator.HandleTimeoutAsync(timeout, CancellationToken.None);

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TimeoutDrainTestState>>();
        var state = await snapshotStore.FindAsync(_saga.SagaType, correlationId);

        // Before the drain fix, the timeout's queued DrainLoopbackAck was silently dropped and the saga
        // would sit in Draining forever instead of reaching Done/Completed.
        Assert.NotNull(state);
        Assert.Equal(_saga.Done.Name, state.CurrentState);
        Assert.Equal(SagaStatus.Completed, state.Status);
        Assert.Contains(_transport.GetPublished(), p => p.Message is DrainLoopbackAck);
    }

    [Fact]
    public async Task TimeoutQueuingALoopback_DrainsAfterItsOwnPersist_NoUnexpectedEvent()
    {
        var correlationId = Guid.NewGuid();
        var timeout = await BeginAndClaimTimeoutAsync(correlationId, "ORD-DRAIN-2");

        var orchestrator = _provider.GetRequiredService<SagaOrchestrator<TimeoutDrainTestState>>();
        await orchestrator.HandleTimeoutAsync(timeout, CancellationToken.None);

        // If the drain ran BEFORE the timeout's own transition was persisted, InMemoryMessageTransport's
        // synchronous re-entrant dispatch would deliver DrainLoopbackAck while the saga was still
        // recorded as being in Waiting -- which has no handler for it -- logging UnexpectedEvent instead
        // of the reply actually driving Draining -> Done.
        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        Assert.DoesNotContain(timeline, e => e.EntryType == SagaEntryType.UnexpectedEvent);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.TimeoutFired);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.SagaCompleted);
    }

    [Fact]
    public async Task TimeoutBuilderThenAsyncOverload_RunsItsActionDirectly()
    {
        // TimeoutDrainTestSaga's own WithTimeout registration exercises the new async Then(Func<...,
        // Task>) overload directly (see TimeoutDrainFixtures.cs) -- this test just pins that it actually
        // ran, distinct from the drain assertions above.
        var correlationId = Guid.NewGuid();
        var timeout = await BeginAndClaimTimeoutAsync(correlationId, "ORD-DRAIN-3");

        var orchestrator = _provider.GetRequiredService<SagaOrchestrator<TimeoutDrainTestState>>();
        await orchestrator.HandleTimeoutAsync(timeout, CancellationToken.None);

        Assert.Contains(_transport.GetPublished(), p => p.Message is DrainLoopbackAck);
    }

    // Empty record is intentional -- see TestOrderSaga.cs's own precedent. A self-transition (handled
    // below) still persists (bumping Version) without cancelling Waiting's pending timeout -- exactly
    // the version bump needed to make the timeout's own final persist lose its race.
#pragma warning disable S2094
    public sealed record NudgeVersion;
#pragma warning restore S2094

    public sealed class NudgingTimeoutDrainTestSaga : OrchestratedSagaDefinition<TimeoutDrainTestState>
    {
        public State<TimeoutDrainTestState> Start { get; }
        public State<TimeoutDrainTestState> Waiting { get; }
        public State<TimeoutDrainTestState> Draining { get; }
        public State<TimeoutDrainTestState> Done { get; }

        public NudgingTimeoutDrainTestSaga()
        {
            Start = InitialState(nameof(Start));
            Waiting = State(nameof(Waiting));
            Draining = State(nameof(Draining));
            Done = State(nameof(Done));

            During(Start)
                .When<BeginDrainTest>()
                    .CorrelateBy(m => m.OrderId, s => s.OrderId)
                    .TransitionTo(Waiting);

            During(Waiting)
                .When<NudgeVersion>()
                    .TransitionTo(Waiting);

            During(Draining)
                .When<DrainLoopbackAck>()
                    .TransitionTo(Done)
                    .Finalize(SagaStatus.Completed);

            WithTimeout(Waiting, TimeSpan.FromMinutes(5), t => t
                .Then(ctx => ctx.PublishAfterCommitAsync(new DrainLoopbackAck(), ctx.CancellationToken))
                .TransitionTo(Draining));
        }
    }

    /// <summary>Mirrors SagaOrchestratorTimeoutRaceTests.RaceInjectingSnapshotStore -- decorates the real store so a test can inject a concurrent write at a precise point without relying on real timing.</summary>
    private sealed class RaceInjectingSnapshotStore<TState>(ISagaSnapshotStore<TState> inner) : ISagaSnapshotStore<TState>
        where TState : SagaState
    {
        public Func<Task>? OnNextUpdate { get; set; }

        public Task<TState?> FindAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(sagaType, correlationId, cancellationToken);

        public Task InsertAsync(TState state, CancellationToken cancellationToken = default) =>
            inner.InsertAsync(state, cancellationToken);

        public async Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default)
        {
            await inner.UpdateAsync(state, expectedVersion, cancellationToken);

            var trigger = OnNextUpdate;
            if (trigger is not null)
            {
                OnNextUpdate = null;
                await trigger();
            }
        }

        public Task<TState?> FindByBusinessKeyAsync(string sagaType, string businessKey, CancellationToken cancellationToken = default) =>
            inner.FindByBusinessKeyAsync(sagaType, businessKey, cancellationToken);
    }

    [Fact]
    public async Task TimeoutLosingItsFinalPersistRace_DiscardsTheQueuedPublishInsteadOfSendingIt()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<NudgingTimeoutDrainTestSaga, TimeoutDrainTestState>());

        services.AddSingleton<ISagaSnapshotStore<TimeoutDrainTestState>>(sp =>
            new RaceInjectingSnapshotStore<TimeoutDrainTestState>(new InMemorySagaSnapshotStore<TimeoutDrainTestState>(sp.GetRequiredService<InMemorySagaStore>())));

        await using var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        var correlationId = Guid.NewGuid();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var saga = provider.GetRequiredService<NudgingTimeoutDrainTestSaga>();

        await transport.PublishAsync(new BeginDrainTest("ORD-DRAIN-RACE"), MessageEnvelope.New(correlationId));

        var timeoutStore = provider.GetRequiredService<ISagaTimeoutStore>();
        var due = await timeoutStore.ClaimDueAsync(DateTimeOffset.UtcNow.AddHours(1), batchSize: 10);
        var timeout = Assert.Single(due, t => t.CorrelationId == correlationId);

        // Arm the race for right after the claim's own write commits: a concurrent message bumps the
        // saga's Version between the claim succeeding and the timeout's own final persist, so that final
        // persist loses its optimistic-concurrency check -- exactly like
        // SagaOrchestratorTimeoutRaceTests.Timeout_LosingRaceAfterItsClaimSucceeded_FailsGracefullyInsteadOfThrowing,
        // but here the timeout's own step actually queued a deferred publish, so the interesting question
        // is what happens to it.
        var racingStore = (RaceInjectingSnapshotStore<TimeoutDrainTestState>)provider.GetRequiredService<ISagaSnapshotStore<TimeoutDrainTestState>>();
        racingStore.OnNextUpdate = () => transport.PublishAsync(new NudgeVersion(), MessageEnvelope.New(correlationId));

        var orchestrator = provider.GetRequiredService<SagaOrchestrator<TimeoutDrainTestState>>();
        await orchestrator.HandleTimeoutAsync(timeout, CancellationToken.None);

        // The queued DrainLoopbackAck must never have been published -- the transition that queued it
        // was never actually committed (the final persist lost the race), so publishing it would
        // announce a transition nobody recorded.
        Assert.DoesNotContain(transport.GetPublished(), p => p.Message is DrainLoopbackAck);

        var eventLog = provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(saga.SagaType, correlationId);
        var exhausted = Assert.Single(timeline, e => e.EntryType == SagaEntryType.DeliveryExhausted);
        Assert.Equal(nameof(DrainLoopbackAck), exhausted.MessageType);

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);
    }
}
