using System.Text;
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
/// A dashboard retry republishes a message by type under the saga's correlation id, which every saga
/// type subscribed to that message type receives. <see cref="MessageEnvelope.TargetSagaTypeHeader"/>
/// confines it to the retried saga type (docs/design/dashboard-usability-and-access.md §7, ADR 0008):
/// another saga type acknowledges and ignores it without a trace. Both fixtures below subscribe to the
/// same two message types and track the same correlation id, the arrangement where an untargeted
/// redrive drives steps in a saga nobody retried. The same class pins that every step's
/// MessageReceived records the message body, which is what lets the retry replay a step other than
/// the initiating one.
/// </summary>
public sealed class TargetedRedriveTests : IAsyncDisposable
{
    public sealed record RedriveOrderPlaced(string OrderId);

    public sealed record RedrivePaymentTaken(decimal Amount);

    public sealed record RedriveShipOrder(Guid CorrelationId, decimal Amount);

    public sealed class RedriveAlphaState : SagaState
    {
        public string? OrderId { get; set; }

        public int PaymentsTaken { get; set; }
    }

    public sealed class RedriveBetaState : SagaState
    {
        public string? OrderId { get; set; }

        public int PaymentsTaken { get; set; }
    }

    /// <summary>The saga a redrive is addressed to; its payment step publishes, so a test can inspect the outbound envelope.</summary>
    public sealed class RedriveAlphaSaga : OrchestratedSagaDefinition<RedriveAlphaState>
    {
        public State<RedriveAlphaState> Placed { get; }
        public State<RedriveAlphaState> AwaitingPayment { get; }
        public State<RedriveAlphaState> Paid { get; }

        public RedriveAlphaSaga()
        {
            Placed = InitialState(nameof(Placed));
            AwaitingPayment = State(nameof(AwaitingPayment));
            Paid = State(nameof(Paid));

            During(Placed)
                .When<RedriveOrderPlaced>()
                    .Then((ctx, m) => ctx.Saga.OrderId = m.OrderId)
                    .TransitionTo(AwaitingPayment);

            During(AwaitingPayment)
                .When<RedrivePaymentTaken>()
                    .Then((ctx, _) => ctx.Saga.PaymentsTaken++)
                    .Publish((ctx, m) => new RedriveShipOrder(ctx.CorrelationId, m.Amount))
                    .TransitionTo(Paid);
        }
    }

    /// <summary>Another saga type observing the same messages under the same correlation id.</summary>
    public sealed class RedriveBetaSaga : OrchestratedSagaDefinition<RedriveBetaState>
    {
        public State<RedriveBetaState> Placed { get; }
        public State<RedriveBetaState> AwaitingPayment { get; }
        public State<RedriveBetaState> Paid { get; }

        public RedriveBetaSaga()
        {
            Placed = InitialState(nameof(Placed));
            AwaitingPayment = State(nameof(AwaitingPayment));
            Paid = State(nameof(Paid));

            During(Placed)
                .When<RedriveOrderPlaced>()
                    .Then((ctx, m) => ctx.Saga.OrderId = m.OrderId)
                    .TransitionTo(AwaitingPayment);

            During(AwaitingPayment)
                .When<RedrivePaymentTaken>()
                    .Then((ctx, _) => ctx.Saga.PaymentsTaken++)
                    .TransitionTo(Paid);
        }
    }

    /// <summary>The in-memory transport acks with a no-op, so the ignored-message test drives the orchestrator directly with this to see the ack.</summary>
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

    private const string AlphaType = nameof(RedriveAlphaSaga);
    private const string BetaType = nameof(RedriveBetaSaga);

    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;
    private readonly ISagaEventLogStore _eventLog;

    public TargetedRedriveTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o
            .AddSaga<RedriveAlphaSaga, RedriveAlphaState>()
            .AddSaga<RedriveBetaSaga, RedriveBetaState>());

        _provider = services.BuildServiceProvider();
        _transport = _provider.GetRequiredService<InMemoryMessageTransport>();
        _eventLog = _provider.GetRequiredService<ISagaEventLogStore>();

        foreach (var hosted in _provider.GetServices<IHostedService>())
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);

        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task TargetedMessage_RunsTheAddressedSagasStep_AndLeavesTheOtherSagaTypeUntouched()
    {
        var correlationId = Guid.NewGuid();
        await _transport.PublishAsync(new RedriveOrderPlaced("ORD-T1"), MessageEnvelope.New(correlationId));

        var betaBefore = await FindBetaAsync(correlationId);
        var betaTimelineBefore = await _eventLog.GetTimelineAsync(BetaType, correlationId);

        var redrive = Targeted(correlationId, AlphaType);
        await _transport.PublishAsync(new RedrivePaymentTaken(12m), redrive);

        var alpha = await FindAlphaAsync(correlationId);
        Assert.Equal(nameof(RedriveAlphaSaga.Paid), alpha.CurrentState);
        Assert.Equal(1, alpha.PaymentsTaken);
        var alphaTimeline = await _eventLog.GetTimelineAsync(AlphaType, correlationId);
        Assert.Contains(alphaTimeline, e => e.EntryType == SagaEntryType.MessageReceived && string.Equals(e.MessageId, redrive.MessageId, StringComparison.Ordinal));

        var beta = await FindBetaAsync(correlationId);
        Assert.Equal(nameof(RedriveBetaSaga.AwaitingPayment), beta.CurrentState);
        Assert.Equal(0, beta.PaymentsTaken);
        Assert.Equal(betaBefore.Version, beta.Version);
        var betaTimeline = await _eventLog.GetTimelineAsync(BetaType, correlationId);
        Assert.Equal(betaTimelineBefore, betaTimeline);
        Assert.DoesNotContain(betaTimeline, e => string.Equals(e.MessageId, redrive.MessageId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UntargetedMessage_StillReachesEverySagaTypeSubscribedToIt()
    {
        var correlationId = Guid.NewGuid();
        await _transport.PublishAsync(new RedriveOrderPlaced("ORD-T2"), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new RedrivePaymentTaken(5m), MessageEnvelope.New(correlationId));

        Assert.Equal(1, (await FindAlphaAsync(correlationId)).PaymentsTaken);
        Assert.Equal(1, (await FindBetaAsync(correlationId)).PaymentsTaken);
    }

    [Fact]
    public async Task TargetedInitiatingMessage_DoesNotStartTheOtherSagaType()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new RedriveOrderPlaced("ORD-T3"), Targeted(correlationId, AlphaType));

        Assert.Equal(nameof(RedriveAlphaSaga.AwaitingPayment), (await FindAlphaAsync(correlationId)).CurrentState);
        Assert.Null(await _provider.GetRequiredService<ISagaSnapshotStore<RedriveBetaState>>().FindAsync(BetaType, correlationId));
        Assert.Empty(await _eventLog.GetTimelineAsync(BetaType, correlationId));
    }

    /// <summary>
    /// The match is exact and ordinal (design doc §7.4): a header that is present with any other value,
    /// a different case or an empty string included, names no subscribed saga type, so every saga type
    /// ignores the message rather than treating the value as absent or as a loose match.
    /// </summary>
    [Theory]
    [InlineData("redrivebetasaga")]
    [InlineData("")]
    public async Task TargetHeaderThatMatchesNoSagaTypeExactly_IsIgnoredByEverySagaType(string target)
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new RedriveOrderPlaced("ORD-T8"), Targeted(correlationId, target));

        Assert.Null(await _provider.GetRequiredService<ISagaSnapshotStore<RedriveBetaState>>().FindAsync(BetaType, correlationId));
        Assert.Empty(await _eventLog.GetTimelineAsync(BetaType, correlationId));
        Assert.Null(await _provider.GetRequiredService<ISagaSnapshotStore<RedriveAlphaState>>().FindAsync(AlphaType, correlationId));
        Assert.Empty(await _eventLog.GetTimelineAsync(AlphaType, correlationId));
    }

    [Fact]
    public async Task MessageTargetedAtAnotherSagaType_IsAcknowledgedWithoutBeingRead()
    {
        var correlationId = Guid.NewGuid();
        var ack = new RecordingAckContext();

        // A body that cannot deserialise: had the orchestrator read it, the failure would be an
        // infrastructure error, redelivered through the transport instead of acknowledged.
        var received = new ReceivedMessage(nameof(RedriveOrderPlaced), correlationId, Guid.NewGuid().ToString("N"),
            Encoding.UTF8.GetBytes("{not json"),
            new Dictionary<string, string>(StringComparer.Ordinal) { [MessageEnvelope.TargetSagaTypeHeader] = AlphaType },
            ack);

        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SagaOrchestrator<RedriveBetaState>>().HandleAsync(received, CancellationToken.None);

        Assert.Equal(1, ack.Acks);
        Assert.Equal(0, ack.Nacks);
        Assert.Empty(_transport.GetPublished());
        Assert.Empty(await _eventLog.GetTimelineAsync(BetaType, correlationId));
        Assert.Null(await _provider.GetRequiredService<ISagaSnapshotStore<RedriveBetaState>>().FindAsync(BetaType, correlationId));
    }

    [Fact]
    public async Task MessagesTheRedrivenStepPublishes_DoNotCarryTheTarget()
    {
        var correlationId = Guid.NewGuid();
        await _transport.PublishAsync(new RedriveOrderPlaced("ORD-T5"), MessageEnvelope.New(correlationId));

        var redrive = Targeted(correlationId, AlphaType);
        await _transport.PublishAsync(new RedrivePaymentTaken(9m), redrive);

        var shipped = Assert.Single(_transport.GetPublished(), p => p.Message is RedriveShipOrder);
        Assert.Equal(redrive.MessageId, shipped.Envelope.Headers![MessageEnvelope.CausationIdHeader]);
        Assert.False(shipped.Envelope.Headers.ContainsKey(MessageEnvelope.TargetSagaTypeHeader));
    }

    [Fact]
    public async Task RedeliveryAfterAnInfrastructureFailure_KeepsTheTarget()
    {
        var correlationId = Guid.NewGuid();
        var envelope = Targeted(correlationId, AlphaType);

        // The addressed saga fails to read this body and redelivers it until the attempts run out; the
        // other saga type ignores every copy.
        await _transport.PublishRawAsync(nameof(RedrivePaymentTaken), Encoding.UTF8.GetBytes("{not json"), envelope);

        var copies = _transport.GetPublished().Where(p => string.Equals(p.Envelope.MessageId, envelope.MessageId, StringComparison.Ordinal)).ToList();
        Assert.True(copies.Count > 1, "the infrastructure failure should have redelivered the message");
        Assert.All(copies, p => Assert.Equal(AlphaType, p.Envelope.Headers![MessageEnvelope.TargetSagaTypeHeader]));
        Assert.Empty(await _eventLog.GetTimelineAsync(BetaType, correlationId));
    }

    [Fact]
    public async Task MessageReceived_CarriesTheMessageBody_OnTheFirstStepAndLaterOnes()
    {
        var correlationId = Guid.NewGuid();
        var placed = new RedriveOrderPlaced("ORD-T7");
        var paid = new RedrivePaymentTaken(21.5m);

        await _transport.PublishAsync(placed, MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(paid, MessageEnvelope.New(correlationId));

        var received = (await _eventLog.GetTimelineAsync(AlphaType, correlationId))
            .Where(e => e.EntryType == SagaEntryType.MessageReceived)
            .ToList();

        Assert.Equal(2, received.Count);
        Assert.Equal(JsonSerializer.Serialize(placed), received[0].PayloadJson);
        Assert.Equal(JsonSerializer.Serialize(paid), received[1].PayloadJson);
    }

    private static MessageEnvelope Targeted(Guid correlationId, string sagaType) =>
        MessageEnvelope.New(correlationId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessageEnvelope.TargetSagaTypeHeader] = sagaType,
        });

    private async Task<RedriveAlphaState> FindAlphaAsync(Guid correlationId) =>
        await _provider.GetRequiredService<ISagaSnapshotStore<RedriveAlphaState>>().FindAsync(AlphaType, correlationId)
        ?? throw new InvalidOperationException("no RedriveAlphaSaga instance");

    private async Task<RedriveBetaState> FindBetaAsync(Guid correlationId) =>
        await _provider.GetRequiredService<ISagaSnapshotStore<RedriveBetaState>>().FindAsync(BetaType, correlationId)
        ?? throw new InvalidOperationException("no RedriveBetaSaga instance");
}
