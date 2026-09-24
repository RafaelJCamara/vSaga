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
/// Verifies SagaOrchestrator's bounded redelivery for infrastructure-level failures (a persistence
/// store exception, not a saga step's own thrown exception — HandleStepFailureAsync already covers
/// that case elsewhere). There's no way to force this failure mode through the public API surface, so
/// these tests decorate the event log store with one that fails a controlled number of times.
/// </summary>
public sealed class SagaOrchestratorInfrastructureFailureTests
{
    /// <summary>Throws on the first <paramref name="failuresBeforeSuccess"/> calls, then delegates to the real store.</summary>
    private sealed class FlakyEventLogStore(ISagaEventLogStore inner, int failuresBeforeSuccess) : ISagaEventLogStore
    {
        private int _remainingFailures = failuresBeforeSuccess;

        public Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default)
        {
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                throw new InvalidOperationException("simulated transient infrastructure failure");
            }

            return inner.AppendAsync(entry, cancellationToken);
        }

        public Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default) =>
            inner.IsDuplicateAsync(sagaType, correlationId, messageId, cancellationToken);

        public Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            inner.GetTimelineAsync(sagaType, correlationId, cancellationToken);
    }

    private static async Task<ServiceProvider> BuildProviderAsync(int failuresBeforeSuccess, int maxDeliveryAttempts)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddSingleton(new SagaOrchestratorOptions { MaxDeliveryAttempts = maxDeliveryAttempts });
        services.AddVSagaEngine(o => o.AddSaga<TestOrderSaga, TestOrderSagaState>());

        // Registered after AddVSagaInMemoryPersistence so it wins service resolution, wrapping the
        // same underlying InMemorySagaStore the rest of the engine (summary reader, timeout store) still
        // reads/writes directly — only the event log's AppendAsync is made flaky.
        services.AddSingleton<ISagaEventLogStore>(sp =>
            new FlakyEventLogStore(sp.GetRequiredService<InMemorySagaStore>(), failuresBeforeSuccess));

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        return provider;
    }

    [Fact]
    public async Task InfrastructureFailure_ThatStopsRecurring_RedeliversAndSucceeds()
    {
        await using var provider = await BuildProviderAsync(failuresBeforeSuccess: 1, maxDeliveryAttempts: 5);
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var correlationId = Guid.NewGuid();

        // Single await: the redelivery happens synchronously/recursively through the in-memory
        // transport's inline dispatch, so by the time this returns the whole fail-then-recover
        // sequence has already completed.
        await transport.PublishAsync(new OrderSubmitted("ORD-INFRA-1", 15m), MessageEnvelope.New(correlationId));

        var sagaType = provider.GetRequiredService<TestOrderSaga>().SagaType;
        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(sagaType, correlationId);

        Assert.NotNull(state);
        Assert.Equal(SagaStatus.Running, state.Status);
        Assert.Equal("AwaitingInventory", state.CurrentState);
        Assert.Contains(transport.GetPublished(), p => p.Message is ReserveInventory);

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InfrastructureFailure_ThatNeverRecovers_IsDeadLetteredAfterMaxAttempts()
    {
        // Fails one more time than the redelivery cap allows, so recovery never happens within budget.
        await using var provider = await BuildProviderAsync(failuresBeforeSuccess: 3, maxDeliveryAttempts: 2);
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var correlationId = Guid.NewGuid();

        await transport.PublishAsync(new OrderSubmitted("ORD-INFRA-2", 9m), MessageEnvelope.New(correlationId));

        // The saga never got past its first (always-failing) log append on any of the 3 attempts, so no
        // snapshot was ever created — the only durable trace is the DeliveryExhausted entry itself.
        var sagaType = provider.GetRequiredService<TestOrderSaga>().SagaType;
        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        Assert.Null(await snapshotStore.FindAsync(sagaType, correlationId));

        var eventLog = provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(sagaType, correlationId);
        var exhausted = Assert.Single(timeline, e => e.EntryType == SagaEntryType.DeliveryExhausted);
        Assert.Equal(nameof(OrderSubmitted), exhausted.MessageType);

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);
    }

    /// <summary>Like <see cref="FlakyEventLogStore"/>, but its remaining-failure budget is settable after construction, so a test can let one message through cleanly before arming failures for a later one.</summary>
    private sealed class ArmableFlakyEventLogStore(ISagaEventLogStore inner) : ISagaEventLogStore
    {
        public int RemainingFailures { get; set; }

        public Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default)
        {
            if (RemainingFailures > 0)
            {
                RemainingFailures--;
                throw new InvalidOperationException("simulated transient infrastructure failure");
            }

            return inner.AppendAsync(entry, cancellationToken);
        }

        public Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default) =>
            inner.IsDuplicateAsync(sagaType, correlationId, messageId, cancellationToken);

        public Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            inner.GetTimelineAsync(sagaType, correlationId, cancellationToken);
    }

    public sealed class ExhaustedBusinessKeySagaState : SagaState
    {
        public string? OrderId { get; set; }
    }

    public sealed record ExhaustedBusinessKeyOpen(string OrderId);
    public sealed record ExhaustedBusinessKeyPing(string OrderId);

    /// <summary>Minimal CorrelateOn-capable fixture, kept separate from TestOrderSaga for the same reason the race-test files keep their own — see SagaOrchestratorBusinessKeyRaceTests.</summary>
    public sealed class ExhaustedBusinessKeySaga : OrchestratedSagaDefinition<ExhaustedBusinessKeySagaState>
    {
        public State<ExhaustedBusinessKeySagaState> Open { get; }
        public State<ExhaustedBusinessKeySagaState> Pinged { get; }

        public ExhaustedBusinessKeySaga()
        {
            Open = InitialState(nameof(Open));
            Pinged = State(nameof(Pinged));

            CorrelateOn(s => s.OrderId);

            During(Open)
                .When<ExhaustedBusinessKeyOpen>()
                    .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .When<ExhaustedBusinessKeyPing>()
                    .CorrelateBy(m => m.OrderId, s => s.OrderId)
                    .TransitionTo(Pinged);
        }
    }

    /// <summary>
    /// production-readiness.md §8.14's review: RecordDeliveryExhaustedAsync used to key its bookkeeping
    /// on received.CorrelationId unconditionally -- correct before business-key resolution existed, but
    /// wrong the moment a message reaches an EXISTING instance via a business key while carrying its
    /// own, different transport correlation id (§5.3). The fix threads the instance HandleCoreAsync
    /// actually resolved through to this dead-letter path instead.
    /// </summary>
    [Fact]
    public async Task DeliveryExhaustedViaBusinessKeyResolution_RecordsAgainstTheResolvedInstance_NotTheTransportId()
    {
        const string businessKey = "ORD-INFRA-BK-1";
        var openCorrelationId = Guid.NewGuid();
        var freshTransportCorrelationId = Guid.NewGuid();
        const int maxDeliveryAttempts = 2;

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddSingleton(new SagaOrchestratorOptions { MaxDeliveryAttempts = maxDeliveryAttempts });
        services.AddVSagaEngine(o => o.AddSaga<ExhaustedBusinessKeySaga, ExhaustedBusinessKeySagaState>());

        ArmableFlakyEventLogStore? flaky = null;
        services.AddSingleton<ISagaEventLogStore>(sp =>
        {
            flaky = new ArmableFlakyEventLogStore(sp.GetRequiredService<InMemorySagaStore>());
            return flaky;
        });

        await using var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var sagaType = provider.GetRequiredService<ExhaustedBusinessKeySaga>().SagaType;
        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<ExhaustedBusinessKeySagaState>>();

        // Creates the instance cleanly -- not flaky yet.
        await transport.PublishAsync(new ExhaustedBusinessKeyOpen(businessKey), MessageEnvelope.New(openCorrelationId));
        Assert.NotNull(await snapshotStore.FindAsync(sagaType, openCorrelationId));

        // Exactly enough failures to exhaust every redelivery attempt for the next message (one per
        // delivery, attempts 0..maxDeliveryAttempts), then let RecordDeliveryExhaustedAsync's own
        // LogAsync succeed -- same accounting SagaOrchestratorInfrastructureFailureTests' sibling test
        // above uses for its failuresBeforeSuccess.
        flaky!.RemainingFailures = maxDeliveryAttempts + 1;

        // A fresh transport correlation id, never seen before: the transport-id lookup misses, so this
        // can only ever reach the existing instance through the business key.
        await transport.PublishAsync(new ExhaustedBusinessKeyPing(businessKey), MessageEnvelope.New(freshTransportCorrelationId));

        var eventLog = provider.GetRequiredService<ISagaEventLogStore>();

        // The bug this pins: before the fix, this bookkeeping was keyed on received.CorrelationId
        // (freshTransportCorrelationId), a timeline no row was ever stored under.
        var timelineUnderTransportId = await eventLog.GetTimelineAsync(sagaType, freshTransportCorrelationId);
        Assert.DoesNotContain(timelineUnderTransportId, e => e.EntryType == SagaEntryType.DeliveryExhausted);

        var timelineUnderResolvedId = await eventLog.GetTimelineAsync(sagaType, openCorrelationId);
        Assert.Contains(timelineUnderResolvedId, e => e.EntryType == SagaEntryType.DeliveryExhausted);

        var finalState = await snapshotStore.FindAsync(sagaType, openCorrelationId);
        Assert.NotNull(finalState);
        Assert.Equal(SagaStatus.Failed, finalState.Status);

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);
    }
}
