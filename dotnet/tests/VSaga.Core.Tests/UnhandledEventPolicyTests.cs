using System.Text.Json;
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
/// Pins <see cref="UnhandledEventPolicy"/>'s two branches, which nothing in the test suite covered
/// before -- which is how the enum's own doc comment drifted into claiming Throw "nacks/redelivers"
/// while the code has always acked. Both fixtures below declare their unhandled-in-this-state message
/// under a *later* state, not nowhere at all: a message type absent from the whole definition never
/// reaches <c>definition.HandleAsync</c> (HandleCoreAsync drops it as an unknown type) and so exercises
/// neither policy.
/// </summary>
public sealed class UnhandledEventPolicyTests
{
    // Empty records are intentional, same as TestOrderSaga's: these markers only need a distinct CLR
    // type for routing, since correlation rides on the envelope.
#pragma warning disable S2094
    public sealed record ThrowShipmentDispatched;
    public sealed record ThrowPaymentSettled;
    public sealed record IgnoreShipmentDispatched;
    public sealed record IgnorePaymentSettled;
#pragma warning restore S2094

    public sealed record ThrowOrderPlaced(string OrderId);
    public sealed record IgnoreOrderPlaced(string OrderId);

    public sealed class ThrowUnhandledSagaState : SagaState
    {
        public string? OrderId { get; set; }
    }

    public sealed class IgnoreUnhandledSagaState : SagaState
    {
        public string? OrderId { get; set; }
    }

    /// <summary>Kept as its own fixture rather than bolted onto TestOrderSaga, whose 100+ sharers all assume the default policy.</summary>
    public sealed class ThrowUnhandledSaga : OrchestratedSagaDefinition<ThrowUnhandledSagaState>
    {
        public State<ThrowUnhandledSagaState> AwaitingShipment { get; }
        public State<ThrowUnhandledSagaState> Shipped { get; }
        public State<ThrowUnhandledSagaState> Settled { get; }

        public ThrowUnhandledSaga()
        {
            AwaitingShipment = InitialState(nameof(AwaitingShipment));
            Shipped = State(nameof(Shipped));
            Settled = State(nameof(Settled));

            OnUnhandledEvent(UnhandledEventPolicy.Throw);

            During(AwaitingShipment)
                .When<ThrowOrderPlaced>()
                    .Then((ctx, m) => ctx.Saga.OrderId = m.OrderId)
                .When<ThrowShipmentDispatched>()
                    .TransitionTo(Shipped);

            // Only reachable from Shipped -- the whole point: it arrives while the saga is still in
            // AwaitingShipment, which is the unhandled-in-this-state case the policy governs.
            During(Shipped)
                .When<ThrowPaymentSettled>()
                    .TransitionTo(Settled)
                    .Finalize(SagaStatus.Completed);
        }
    }

    /// <summary>Identical shape to <see cref="ThrowUnhandledSaga"/> but never calls OnUnhandledEvent, so it pins the default.</summary>
    public sealed class DefaultUnhandledSaga : OrchestratedSagaDefinition<IgnoreUnhandledSagaState>
    {
        public State<IgnoreUnhandledSagaState> AwaitingShipment { get; }
        public State<IgnoreUnhandledSagaState> Shipped { get; }
        public State<IgnoreUnhandledSagaState> Settled { get; }

        public DefaultUnhandledSaga()
        {
            AwaitingShipment = InitialState(nameof(AwaitingShipment));
            Shipped = State(nameof(Shipped));
            Settled = State(nameof(Settled));

            During(AwaitingShipment)
                .When<IgnoreOrderPlaced>()
                    .Then((ctx, m) => ctx.Saga.OrderId = m.OrderId)
                .When<IgnoreShipmentDispatched>()
                    .TransitionTo(Shipped);

            During(Shipped)
                .When<IgnorePaymentSettled>()
                    .TransitionTo(Settled)
                    .Finalize(SagaStatus.Completed);
        }
    }

    /// <summary>
    /// The in-memory transport hands every delivery a no-op ack context, so ack/nack is invisible when a
    /// message is published through it. Driving the orchestrator's own entry point with this instead is
    /// what makes the ack a direct assertion rather than an inference from "no redelivery was published".
    /// </summary>
    private sealed class RecordingAckContext : IMessageAckContext
    {
        public int Acks { get; private set; }

        public int Nacks { get; private set; }

        public Task AckAsync(CancellationToken cancellationToken = default)
        {
            Acks++;
            return Task.CompletedTask;
        }

        public Task NackAsync(bool requeue, CancellationToken cancellationToken = default)
        {
            Nacks++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Mirrors what InMemoryMessageTransport builds for a subscriber, minus its no-op ack context.</summary>
    private static ReceivedMessage Delivery<TMessage>(TMessage message, Guid correlationId, IMessageAckContext ack)
        where TMessage : notnull =>
        new(typeof(TMessage).Name,
            correlationId,
            Guid.NewGuid().ToString("N"),
            JsonSerializer.SerializeToUtf8Bytes(message),
            new Dictionary<string, string>(StringComparer.Ordinal),
            ack);

    private static async Task<ServiceProvider> BuildProviderAsync(Action<SagaEngineBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(configure);

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        return provider;
    }

    /// <summary>
    /// docs/saga-dsl.md's correction, pinned: Throw raises out of OrchestratedSagaDefinition.HandleAsync,
    /// RunStepAsync's catch routes it to HandleStepFailureAsync exactly like an ordinary step failure, the
    /// saga is marked Failed, and HandleAsync then ACKS. The failure never reaches
    /// HandleInfrastructureFailureAsync, so there is no redelivery and no dead-lettering either.
    /// </summary>
    [Fact]
    public async Task ThrowPolicy_UnhandledMessage_MarksTheSagaFailedAndAcksInsteadOfRedelivering()
    {
        var correlationId = Guid.NewGuid();

        await using var provider = await BuildProviderAsync(o => o.AddSaga<ThrowUnhandledSaga, ThrowUnhandledSagaState>());
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var saga = provider.GetRequiredService<ThrowUnhandledSaga>();
        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<ThrowUnhandledSagaState>>();
        var eventLog = provider.GetRequiredService<ISagaEventLogStore>();

        await transport.PublishAsync(new ThrowOrderPlaced("ORD-THROW-1"), MessageEnvelope.New(correlationId));

        var started = await snapshotStore.FindAsync(saga.SagaType, correlationId);
        Assert.NotNull(started);
        Assert.Equal(saga.AwaitingShipment.Name, started.CurrentState);

        var ack = new RecordingAckContext();
        var orchestrator = provider.GetRequiredService<SagaOrchestrator<ThrowUnhandledSagaState>>();
        await orchestrator.HandleAsync(Delivery(new ThrowPaymentSettled(), correlationId, ack), CancellationToken.None);

        // The ack is the claim the enum's old comment got wrong; awaiting the call above to completion
        // also proves the exception never escapes the orchestrator.
        Assert.Equal(1, ack.Acks);
        Assert.Equal(0, ack.Nacks);

        var state = await snapshotStore.FindAsync(saga.SagaType, correlationId);
        Assert.NotNull(state);
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.Equal(saga.AwaitingShipment.Name, state.CurrentState); // parked where it failed, no transition ran

        var timeline = await eventLog.GetTimelineAsync(saga.SagaType, correlationId);
        var failed = Assert.Single(timeline, e => e.EntryType == SagaEntryType.StepFailed);
        Assert.Equal(nameof(ThrowPaymentSettled), failed.MessageType);
        Assert.Contains(nameof(ThrowPaymentSettled), failed.ErrorMessage, StringComparison.Ordinal);

        // Throw's failure is business-level, not infrastructure-level: HandleInfrastructureFailureAsync
        // never runs, so no copy is republished with an incremented attempt header and nothing is ever
        // dead-lettered. This is what "no redelivery loop" looks like from the outside.
        Assert.DoesNotContain(transport.GetPublished(), p =>
            p.Envelope.Headers is not null && p.Envelope.Headers.ContainsKey("x-vsaga-delivery-attempt"));
        Assert.DoesNotContain(timeline, e => e.EntryType == SagaEntryType.DeliveryExhausted);
        Assert.DoesNotContain(timeline, e => e.EntryType == SagaEntryType.UnexpectedEvent);
    }

    /// <summary>
    /// The default a saga gets by never calling OnUnhandledEvent: the step is skipped, the saga keeps
    /// running in the state it was already in, and the only trace is an UnexpectedEvent timeline entry
    /// (SagaOrchestrator.HandleStepSuccessAsync's !WasHandled branch). Acked, same as Throw -- the
    /// difference between the policies is the saga's fate, never the message's.
    /// </summary>
    [Fact]
    public async Task DefaultPolicy_UnhandledMessage_LeavesTheSagaRunningAndRecordsAnUnexpectedEventEntry()
    {
        var correlationId = Guid.NewGuid();

        await using var provider = await BuildProviderAsync(o => o.AddSaga<DefaultUnhandledSaga, IgnoreUnhandledSagaState>());
        var saga = provider.GetRequiredService<DefaultUnhandledSaga>();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<IgnoreUnhandledSagaState>>();
        var eventLog = provider.GetRequiredService<ISagaEventLogStore>();

        await transport.PublishAsync(new IgnoreOrderPlaced("ORD-IGNORE-1"), MessageEnvelope.New(correlationId));

        var ack = new RecordingAckContext();
        var orchestrator = provider.GetRequiredService<SagaOrchestrator<IgnoreUnhandledSagaState>>();
        await orchestrator.HandleAsync(Delivery(new IgnorePaymentSettled(), correlationId, ack), CancellationToken.None);

        Assert.Equal(1, ack.Acks);
        Assert.Equal(0, ack.Nacks);

        var state = await snapshotStore.FindAsync(saga.SagaType, correlationId);
        Assert.NotNull(state);
        Assert.Equal(SagaStatus.Running, state.Status);
        Assert.Equal(saga.AwaitingShipment.Name, state.CurrentState);

        var timeline = await eventLog.GetTimelineAsync(saga.SagaType, correlationId);
        var unexpected = Assert.Single(timeline, e => e.EntryType == SagaEntryType.UnexpectedEvent);
        Assert.Equal(nameof(IgnorePaymentSettled), unexpected.MessageType);
        Assert.Equal(saga.AwaitingShipment.Name, unexpected.FromState);
        Assert.DoesNotContain(timeline, e => e.EntryType == SagaEntryType.StepFailed);

        // Still live: the message it could not handle cost it nothing, so the transition it was actually
        // waiting for still works afterwards.
        await transport.PublishAsync(new IgnoreShipmentDispatched(), MessageEnvelope.New(correlationId));

        var afterShipment = await snapshotStore.FindAsync(saga.SagaType, correlationId);
        Assert.NotNull(afterShipment);
        Assert.Equal(saga.Shipped.Name, afterShipment.CurrentState);
        Assert.Equal(SagaStatus.Running, afterShipment.Status);
    }
}
