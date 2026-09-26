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
/// Bug B2 (docs/design/persistence-contracts.md §4): HandleStepFailureAsync stages the engine's
/// ChildSagaFinished before its failure persist, and when that persist throws an infrastructure error the
/// row's next chance to commit is RecordDeliveryExhaustedAsync's own append. That is right when the path
/// can mark the saga Failed, since the row then matches what was recorded, and wrong when it cannot: no
/// snapshot at all, or a status already terminal. Both halves are pinned. MaxDeliveryAttempts is 0 so the
/// first failure dead-letters in the same unit of work, with nothing redelivered in between.
/// </summary>
public sealed class DeliveryExhaustedChildSagaFinishedTests
{
    [Fact]
    public async Task DeliveryExhausted_WithNoSnapshotToMarkFailed_DiscardsTheStagedChildSagaFinished()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddSingleton(new SagaOrchestratorOptions { MaxDeliveryAttempts = 0 });
        services.AddVSagaEngine(o => o
            .AddSaga<TestFragileParentSaga, TestFragileParentState>()
            .AddSaga<TestFragileChildSaga, TestFragileChildState>());

        InfrastructureFailingSnapshotStore<TestFragileChildState>? fragile = null;
        services.AddSingleton<ISagaSnapshotStore<TestFragileChildState>>(sp =>
            fragile = new InfrastructureFailingSnapshotStore<TestFragileChildState>(new InMemorySagaSnapshotStore<TestFragileChildState>(sp.GetRequiredService<InMemorySagaStore>())));

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);

        try
        {
            var transport = provider.GetRequiredService<InMemoryMessageTransport>();
            var parentId = Guid.NewGuid();

            // The child has no business key, so nothing reserves a row for it before its step runs; its
            // very first step throws, and the failure persist -- an insert -- then fails as infrastructure.
            // Dead-lettered on the spot, RecordDeliveryExhaustedAsync finds no snapshot to mark Failed.
            // Resolved here, not at start-up: the child's orchestrator, and so its store, is only built per message.
            provider.GetRequiredService<ISagaSnapshotStore<TestFragileChildState>>();
            fragile!.ArmedToThrowOnInsert = true;
            await transport.PublishAsync(new BeginFragileJob("JOB-B2-NULL"), MessageEnvelope.New(parentId));

            var reader = provider.GetRequiredService<ISagaSummaryReader>();
            Assert.Empty(await reader.FindChildrenAsync(nameof(TestFragileParentSaga), parentId));

            var pending = await provider.GetRequiredService<ISagaOutboxStore>().ClaimPendingAsync(DateTimeOffset.UtcNow.AddYears(1), batchSize: 100);
            Assert.DoesNotContain(pending, m => string.Equals(m.MessageTypeName, nameof(ChildSagaFinished), StringComparison.Ordinal));

            // Nothing announced a finish for a child that was never recorded, so the parent still waits.
            var parent = (await provider.GetRequiredService<ISagaSnapshotStore<TestFragileParentState>>().FindAsync(nameof(TestFragileParentSaga), parentId))!;
            Assert.Equal(nameof(TestFragileParentSaga.AwaitingResult), parent.CurrentState);
            Assert.Equal(SagaStatus.Running, parent.Status);
            Assert.Null(parent.ChildFinishedStatus);
        }
        finally
        {
            foreach (var service in hosted)
                await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>The other half, unchanged by the fix: when the path can mark the saga Failed, the row it commits is right and stays for the recovery poller.</summary>
    [Fact]
    public async Task DeliveryExhausted_ThatMarksTheSagaFailed_KeepsTheStagedChildSagaFinishedForRecovery()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddSingleton(new SagaOrchestratorOptions { MaxDeliveryAttempts = 0 });
        services.AddVSagaEngine(o => o
            .AddSaga<TestChildSafetyNetParentSaga, TestChildSafetyNetParentState>()
            .AddSaga<TestRiskyChildSaga, TestRiskyChildState>());

        InfrastructureFailingSnapshotStore<TestRiskyChildState>? fragile = null;
        services.AddSingleton<ISagaSnapshotStore<TestRiskyChildState>>(sp =>
            fragile = new InfrastructureFailingSnapshotStore<TestRiskyChildState>(new InMemorySagaSnapshotStore<TestRiskyChildState>(sp.GetRequiredService<InMemorySagaStore>())));

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);

        try
        {
            var transport = provider.GetRequiredService<InMemoryMessageTransport>();
            var reader = provider.GetRequiredService<ISagaSummaryReader>();
            var parentId = Guid.NewGuid();
            await transport.PublishAsync(new BeginSafeguardedJob("JOB-B2-FAILED"), MessageEnvelope.New(parentId));
            var child = Assert.Single(await reader.FindChildrenAsync(nameof(TestChildSafetyNetParentSaga), parentId));

            // The child is Running with a snapshot; its step throws and the failure persist -- an update --
            // fails once as infrastructure. Dead-lettered on the spot, RecordDeliveryExhaustedAsync marks
            // it Failed itself, so the staged row now matches what was recorded and must survive.
            fragile!.ArmedToThrowOnUpdate = true;
            await transport.PublishAsync(new TriggerFailure("JOB-B2-FAILED"), MessageEnvelope.New(child.CorrelationId));

            Assert.Equal(SagaStatus.Failed, (await reader.GetAsync(nameof(TestRiskyChildSaga), child.CorrelationId))!.Status);

            var pending = await provider.GetRequiredService<ISagaOutboxStore>().ClaimPendingAsync(DateTimeOffset.UtcNow.AddYears(1), batchSize: 100);
            var row = Assert.Single(pending, m => string.Equals(m.MessageTypeName, nameof(ChildSagaFinished), StringComparison.Ordinal));
            Assert.Equal(parentId, row.CorrelationId);
        }
        finally
        {
            foreach (var service in hosted)
                await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Throws an infrastructure-shaped exception, once, from whichever write is armed; everything else goes through.</summary>
    private sealed class InfrastructureFailingSnapshotStore<TState>(ISagaSnapshotStore<TState> inner) : ISagaSnapshotStore<TState>
        where TState : SagaState
    {
        public bool ArmedToThrowOnInsert { get; set; }

        public bool ArmedToThrowOnUpdate { get; set; }

        public Task<TState?> FindAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(sagaType, correlationId, cancellationToken);

        public Task<TState?> FindByBusinessKeyAsync(string sagaType, string businessKey, CancellationToken cancellationToken = default) =>
            inner.FindByBusinessKeyAsync(sagaType, businessKey, cancellationToken);

        public Task InsertAsync(TState state, CancellationToken cancellationToken = default)
        {
            if (ArmedToThrowOnInsert)
            {
                ArmedToThrowOnInsert = false;
                throw new InvalidOperationException("Simulated infrastructure failure on insert.");
            }

            return inner.InsertAsync(state, cancellationToken);
        }

        public Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default)
        {
            if (ArmedToThrowOnUpdate)
            {
                ArmedToThrowOnUpdate = false;
                throw new InvalidOperationException("Simulated infrastructure failure on update.");
            }

            return inner.UpdateAsync(state, expectedVersion, cancellationToken);
        }
    }
}

public sealed record BeginFragileJob(string JobId);

public sealed record BeginFragileChild(string JobId);

public sealed class TestFragileParentState : SagaState
{
    public string? JobId { get; set; }

    public SagaStatus? ChildFinishedStatus { get; set; }
}

/// <summary>Same shape as TestRacyFailureParentSaga, paired with a child that has no business key.</summary>
public sealed class TestFragileParentSaga : OrchestratedSagaDefinition<TestFragileParentState>
{
    public State<TestFragileParentState> Requested { get; }
    public State<TestFragileParentState> AwaitingResult { get; }
    public State<TestFragileParentState> Rescued { get; }

    public TestFragileParentSaga()
    {
        Requested = InitialState(nameof(Requested));
        AwaitingResult = State(nameof(AwaitingResult));
        Rescued = State(nameof(Rescued));

        During(Requested)
            .When<BeginFragileJob>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.StartChildAsync(new BeginFragileChild(m.JobId), ctx.CancellationToken))
                .TransitionTo(AwaitingResult);

        During(AwaitingResult)
            .When<ChildSagaFinished>()
                .Then((ctx, m) => ctx.Saga.ChildFinishedStatus = m.Status)
                .TransitionTo(Rescued)
                .Finalize(SagaStatus.Failed);

        WithTimeout(AwaitingResult, TimeSpan.FromMinutes(5), t => t.Finalize(SagaStatus.TimedOut));
    }
}

public sealed class TestFragileChildState : SagaState;

/// <summary>
/// Fails in the step that starts it, like TestImmediatelyFailingChildSaga, but declares no business key:
/// nothing reserves a row for it before the step, so its failure persist is the insert, and when that
/// insert fails no snapshot of it exists anywhere.
/// </summary>
public sealed class TestFragileChildSaga : OrchestratedSagaDefinition<TestFragileChildState>
{
    public State<TestFragileChildState> Failing { get; }

    public TestFragileChildSaga()
    {
        Failing = InitialState(nameof(Failing));

        During(Failing)
            .When<BeginFragileChild>()
                .Then((ctx, _) => throw new InvalidOperationException("Simulated immediate unhandled failure."));
    }
}
