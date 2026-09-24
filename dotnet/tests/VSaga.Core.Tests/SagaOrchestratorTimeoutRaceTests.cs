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
/// Verifies the fix for the timeout/message race VSaga.Chaos's live verification surfaced (see
/// README's "Chaos-engineering transport middleware" section): SagaTimeoutDispatcherHostedService's
/// periodic poll and SagaOrchestrator.HandleAsync's normal message-handling path can both read the
/// same saga snapshot at the same version before either writes back, when a reply arrives just before
/// its state's timeout is due. There's no reliable way to force that interleaving through real timing,
/// so this decorates the snapshot store to trigger the "concurrent" reply synchronously from inside
/// the timeout path's own read — the same controlled-fake technique
/// SagaOrchestratorInfrastructureFailureTests uses for infrastructure failures.
/// </summary>
public sealed class SagaOrchestratorTimeoutRaceTests
{
    /// <summary>
    /// Wraps the real snapshot store. When <see cref="OnNextFind"/> is set, the very next
    /// <see cref="FindAsync"/> call runs it synchronously — after capturing the pre-race snapshot but
    /// before returning it — then clears itself so nothing else is affected. <see cref="OnNextUpdate"/>
    /// works the same way but for the next successful <see cref="UpdateAsync"/> call, letting a test
    /// inject a race that lands right after one write commits (e.g. a claim) rather than before a read.
    /// </summary>
    private sealed class RaceInjectingSnapshotStore<TState>(ISagaSnapshotStore<TState> inner) : ISagaSnapshotStore<TState>
        where TState : SagaState
    {
        public Func<Task>? OnNextFind { get; set; }

        public Func<Task>? OnNextUpdate { get; set; }

        public async Task<TState?> FindAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
        {
            var state = await inner.FindAsync(sagaType, correlationId, cancellationToken);

            var trigger = OnNextFind;
            if (trigger is not null)
            {
                OnNextFind = null;
                await trigger();
            }

            return state;
        }

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

    private static async Task<ServiceProvider> BuildProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<TestOrderSaga, TestOrderSagaState>());

        // Registered after AddVSagaInMemoryPersistence so it wins service resolution, wrapping the
        // same underlying InMemorySagaStore the rest of the engine still reads/writes directly — see
        // SagaOrchestratorInfrastructureFailureTests for the same override-order trick applied to the
        // event log store.
        services.AddSingleton<ISagaSnapshotStore<TestOrderSagaState>>(sp =>
            new RaceInjectingSnapshotStore<TestOrderSagaState>(new InMemorySagaSnapshotStore<TestOrderSagaState>(sp.GetRequiredService<InMemorySagaStore>())));

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        return provider;
    }

    [Fact]
    public async Task Timeout_LosingRaceAgainstConcurrentReply_DoesNotPublishCompensationSideEffects()
    {
        var correlationId = Guid.NewGuid();

        await using var provider = await BuildProviderAsync();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();

        // Drive the saga to AwaitingPayment (a real reservation happened, so a losing timeout's
        // Compensate() would have something concrete to release) and schedule/claim its timeout,
        // exactly like SagaOrchestratorTests.Timeout_FiresAndTransitionsSaga.
        await transport.PublishAsync(new OrderSubmitted("ORD-RACE-1", 30m), MessageEnvelope.New(correlationId));
        await transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));

        var timeoutStore = provider.GetRequiredService<ISagaTimeoutStore>();
        var due = await timeoutStore.ClaimDueAsync(DateTimeOffset.UtcNow.AddHours(1), batchSize: 10);
        var timeout = Assert.Single(due, t => t.CorrelationId == correlationId);

        // Arm the race for the timeout path's own upcoming read: HandleTimeoutAsync's very first
        // snapshotStore.FindAsync call (below) will synchronously publish+fully-process a PaymentCharged
        // reply — read, transition to Completed, persist, bumping the version — before returning the
        // pre-race snapshot (still AwaitingPayment at the old version) to the timeout handler. This
        // reproduces "the reply landed just before the timeout was due" without relying on real timing.
        var racingStore = (RaceInjectingSnapshotStore<TestOrderSagaState>)provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        racingStore.OnNextFind = () => transport.PublishAsync(new PaymentCharged(), MessageEnvelope.New(correlationId));

        // Root-scope resolution matches SagaOrchestratorTests.Timeout_FiresAndTransitionsSaga.
        var orchestrator = provider.GetRequiredService<SagaOrchestrator<TestOrderSagaState>>();
        await orchestrator.HandleTimeoutAsync(timeout, CancellationToken.None);

        var sagaType = provider.GetRequiredService<TestOrderSaga>().SagaType;
        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(sagaType, correlationId);

        // The reply won the race: the saga legitimately completed...
        Assert.NotNull(state);
        Assert.Equal(nameof(TestOrderSaga.Completed), state.CurrentState);
        Assert.Equal(SagaStatus.Completed, state.Status);

        // ...and the losing timeout must never have published compensation side effects. AwaitingPayment's
        // own Compensate() only throws (no publish), but AwaitingInventory's does publish ReleaseInventory
        // and is included (most-recent-first) whenever RunCompensationAsync runs at all for this saga — so
        // its absence is a faithful signal that Compensate() was never invoked for the losing timeout.
        Assert.DoesNotContain(transport.GetPublished(), p => p.Message is ReleaseInventory);

        var eventLog = provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(sagaType, correlationId);
        Assert.DoesNotContain(timeline, e => e.EntryType is SagaEntryType.CompensationStarted
            or SagaEntryType.CompensationStepSucceeded or SagaEntryType.CompensationStepFailed);

        // The timeout aborted before even logging that it fired — it lost the claim before doing anything.
        Assert.DoesNotContain(timeline, e => e.EntryType == SagaEntryType.TimeoutFired);

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Timeout_LosingRaceAfterItsClaimSucceeded_FailsGracefullyInsteadOfThrowing()
    {
        // A NARROWER, second race window the primary fix's claim-first persist cannot close: the claim
        // only proves no concurrent write had landed *yet*. definition.HandleTimeoutAsync (which runs
        // right after the claim succeeds) executes real Compensate()/Publish() side effects, and a
        // concurrent write can still land in that window before this timeout's own final persist. When
        // that happens, the side effects already fired and can't be un-sent — this documents that known,
        // accepted residual limitation (shared by all three claim-first design options considered for
        // the primary fix) and locks in the one thing that *did* change: the resulting
        // SagaConcurrencyException is now caught and logged distinctly instead of propagating uncaught
        // out to SagaTimeoutDispatcherHostedService's generic catch-and-log.
        var correlationId = Guid.NewGuid();

        await using var provider = await BuildProviderAsync();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();

        await transport.PublishAsync(new OrderSubmitted("ORD-RACE-2", 30m), MessageEnvelope.New(correlationId));
        await transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));

        var timeoutStore = provider.GetRequiredService<ISagaTimeoutStore>();
        var due = await timeoutStore.ClaimDueAsync(DateTimeOffset.UtcNow.AddHours(1), batchSize: 10);
        var timeout = Assert.Single(due, t => t.CorrelationId == correlationId);

        // Arm the race for right after the claim's own write commits: the PaymentCharged reply is fully
        // processed (read, transition to Completed, persist) between the claim succeeding and
        // definition.HandleTimeoutAsync running — so the claim itself succeeds, but the final persist
        // afterward is now stale.
        var racingStore = (RaceInjectingSnapshotStore<TestOrderSagaState>)provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        racingStore.OnNextUpdate = () => transport.PublishAsync(new PaymentCharged(), MessageEnvelope.New(correlationId));

        var orchestrator = provider.GetRequiredService<SagaOrchestrator<TestOrderSagaState>>();
        // The key assertion: this must complete normally. Before the second catch was added, the
        // SagaConcurrencyException from the final persist propagated straight out of this call.
        await orchestrator.HandleTimeoutAsync(timeout, CancellationToken.None);

        var sagaType = provider.GetRequiredService<TestOrderSaga>().SagaType;
        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(sagaType, correlationId);

        // The reply still won overall — its persist landed after the claim's, so it's what's stored.
        Assert.NotNull(state);
        Assert.Equal(nameof(TestOrderSaga.Completed), state.CurrentState);
        Assert.Equal(SagaStatus.Completed, state.Status);

        // Unlike the first (pre-claim) race, Compensate() DID run here — the claim had already
        // succeeded, so HandleTimeoutAsync proceeded past it before the second write landed. This is
        // the accepted residual leak, not a regression: it demonstrates why this fix reduces the race
        // window rather than eliminating it outright.
        Assert.Contains(transport.GetPublished(), p => p.Message is ReleaseInventory);

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);
    }
}
