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
/// Verifies the fix for production-readiness.md §5.2/§5.3 (item 14): two concurrent initiating messages
/// carrying the same business key but different transport correlation ids must resolve to exactly one
/// saga instance, with the loser rerouted to the winner's instance rather than running its own copy of
/// the step's side effects. There's no reliable way to force that interleaving through real timing, so
/// this decorates the snapshot store to trigger the "concurrent" initiate synchronously from inside the
/// losing message's own reservation InsertAsync call -- the same controlled-fake technique
/// SagaOrchestratorTimeoutRaceTests uses for the timeout/message race.
/// </summary>
public sealed class SagaOrchestratorBusinessKeyRaceTests
{
    public sealed class BusinessKeyRaceSagaState : SagaState
    {
        public string? OrderId { get; set; }
    }

    public sealed record ReservationRequested(string OrderId);
    public sealed record ReservationMade(string OrderId);

    /// <summary>
    /// A minimal saga whose only purpose is to have declared CorrelateOn -- TestOrderSaga deliberately
    /// hasn't (see item 13's commit message: "All 39 existing call sites are unaffected"), so a
    /// business-key-routing test needs its own fixture rather than risking behaviour changes across the
    /// 100+ tests that already share TestOrderSaga.
    /// </summary>
    public sealed class BusinessKeyRaceSaga : OrchestratedSagaDefinition<BusinessKeyRaceSagaState>
    {
        public State<BusinessKeyRaceSagaState> Submitted { get; }
        public State<BusinessKeyRaceSagaState> Reserved { get; }

        public BusinessKeyRaceSaga()
        {
            Submitted = InitialState(nameof(Submitted));
            Reserved = State(nameof(Reserved));

            CorrelateOn(s => s.OrderId);

            During(Submitted)
                .When<ReservationRequested>()
                    .CorrelateBy(m => m.OrderId, s => s.OrderId)
                    .Publish((ctx, m) => new ReservationMade(m.OrderId))
                    .TransitionTo(Reserved);
        }
    }

    /// <summary>
    /// Wraps the real snapshot store. When <see cref="OnNextInsert"/> is set, the very next
    /// <see cref="InsertAsync"/> call runs it synchronously -- *before* delegating to the inner store --
    /// then clears itself so nothing else is affected. This lets a test inject a second initiate that
    /// fully completes (including its own InsertAsync reservation) while the first initiate's own
    /// InsertAsync is still "in flight", reproducing the race without relying on real thread timing.
    /// </summary>
    private sealed class RaceInjectingSnapshotStore<TState>(ISagaSnapshotStore<TState> inner) : ISagaSnapshotStore<TState>
        where TState : SagaState
    {
        public Func<Task>? OnNextInsert { get; set; }

        public Task<TState?> FindAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(sagaType, correlationId, cancellationToken);

        public async Task InsertAsync(TState state, CancellationToken cancellationToken = default)
        {
            var trigger = OnNextInsert;
            if (trigger is not null)
            {
                OnNextInsert = null;
                await trigger();
            }

            await inner.InsertAsync(state, cancellationToken);
        }

        public Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(state, expectedVersion, cancellationToken);

        public Task<TState?> FindByBusinessKeyAsync(string sagaType, string businessKey, CancellationToken cancellationToken = default) =>
            inner.FindByBusinessKeyAsync(sagaType, businessKey, cancellationToken);
    }

    private static async Task<ServiceProvider> BuildProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<BusinessKeyRaceSaga, BusinessKeyRaceSagaState>());

        // Registered after AddVSagaInMemoryPersistence so it wins service resolution, wrapping the same
        // underlying InMemorySagaStore the rest of the engine still reads/writes directly -- see
        // SagaOrchestratorTimeoutRaceTests for the same override-order trick.
        services.AddSingleton<ISagaSnapshotStore<BusinessKeyRaceSagaState>>(sp =>
            new RaceInjectingSnapshotStore<BusinessKeyRaceSagaState>(new InMemorySagaSnapshotStore<BusinessKeyRaceSagaState>(sp.GetRequiredService<InMemorySagaStore>())));

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        return provider;
    }

    [Fact]
    public async Task ConcurrentInitiatesWithSameBusinessKey_ResultInExactlyOneSagaInstance()
    {
        const string businessKey = "ORD-RACE-1";
        var correlationIdA = Guid.NewGuid();
        var correlationIdB = Guid.NewGuid();

        await using var provider = await BuildProviderAsync();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var sagaType = provider.GetRequiredService<BusinessKeyRaceSaga>().SagaType;
        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<BusinessKeyRaceSagaState>>();
        var eventLog = provider.GetRequiredService<ISagaEventLogStore>();

        // Arm the race for message A's upcoming reservation insert: right as A's InsertAsync is about
        // to run, synchronously deliver and fully process message B first -- same business key, a
        // DIFFERENT transport correlation id, neither aware of the other. Because A's own InsertAsync is
        // still suspended waiting on this trigger, B's FindByBusinessKeyAsync lookup also misses, so B
        // takes the same "brand new saga" path A is on -- B's own InsertAsync (with the hook already
        // cleared) reserves the business key for real and B runs its step to completion. Only once B has
        // fully finished does control return to A's InsertAsync, which now loses against B's reservation.
        var racingStore = (RaceInjectingSnapshotStore<BusinessKeyRaceSagaState>)snapshotStore;
        racingStore.OnNextInsert = () => transport.PublishAsync(new ReservationRequested(businessKey), MessageEnvelope.New(correlationIdB));

        await transport.PublishAsync(new ReservationRequested(businessKey), MessageEnvelope.New(correlationIdA));

        // Exactly one instance was ever created for this business key, and A's own correlation id never
        // got a row of its own -- the loser was rerouted, not duplicated.
        Assert.Null(await snapshotStore.FindAsync(sagaType, correlationIdA));

        var winner = await snapshotStore.FindByBusinessKeyAsync(sagaType, businessKey);
        Assert.NotNull(winner);
        Assert.Equal(correlationIdB, winner.CorrelationId);
        Assert.Equal(nameof(BusinessKeyRaceSaga.Reserved), winner.CurrentState);

        // The step's side effect (Publish(ReservationMade)) ran exactly once -- the loser's own
        // RunStepAsync call landed on the winner's already-Reserved instance, where ReservationRequested
        // has no registered handler, so it was logged as UnexpectedEvent and never re-ran the step.
        Assert.Single(transport.GetPublished(), p => p.Message is ReservationMade);

        // The winner's own version only ever advanced once (its own successful step), confirming the
        // loser's reroute did not also persist a second, duplicate transition on top of it.
        Assert.Equal(1, winner.Version);

        // A's message never opened a timeline of its own -- everything after the race loss logged onto
        // the winner's (B's) timeline instead, per §5.3's invariant.
        var timelineA = await eventLog.GetTimelineAsync(sagaType, correlationIdA);
        Assert.Empty(timelineA);

        var timelineB = await eventLog.GetTimelineAsync(sagaType, correlationIdB);
        Assert.Contains(timelineB, e => e.EntryType == SagaEntryType.SagaStarted);
        Assert.Single(timelineB, e => e.EntryType == SagaEntryType.StepSucceeded);
        Assert.Single(timelineB, e => e.EntryType == SagaEntryType.UnexpectedEvent);

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentInitiatesWithDifferentBusinessKeys_BothSucceedAsSeparateInstances()
    {
        // Control case: the reservation must not over-serialize unrelated initiates. Two different
        // business keys racing through the same InsertAsync hook point should both win their own
        // reservation and end up as two independent saga instances.
        var correlationIdA = Guid.NewGuid();
        var correlationIdB = Guid.NewGuid();

        await using var provider = await BuildProviderAsync();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var sagaType = provider.GetRequiredService<BusinessKeyRaceSaga>().SagaType;
        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<BusinessKeyRaceSagaState>>();

        var racingStore = (RaceInjectingSnapshotStore<BusinessKeyRaceSagaState>)snapshotStore;
        racingStore.OnNextInsert = () => transport.PublishAsync(new ReservationRequested("ORD-RACE-OTHER"), MessageEnvelope.New(correlationIdB));

        await transport.PublishAsync(new ReservationRequested("ORD-RACE-SELF"), MessageEnvelope.New(correlationIdA));

        var self = await snapshotStore.FindByBusinessKeyAsync(sagaType, "ORD-RACE-SELF");
        var other = await snapshotStore.FindByBusinessKeyAsync(sagaType, "ORD-RACE-OTHER");

        Assert.NotNull(self);
        Assert.Equal(correlationIdA, self.CorrelationId);
        Assert.NotNull(other);
        Assert.Equal(correlationIdB, other.CorrelationId);

        Assert.Equal(2, transport.GetPublished().Count(p => p.Message is ReservationMade));

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);
    }
}
