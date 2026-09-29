using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Samples.Persistence.Common;

public sealed class CheckoutState : SagaState
{
    public string? OrderNumber { get; set; }

    public decimal Amount { get; set; }

    public string? AuthorizationCode { get; set; }

    public string? DeclineReason { get; set; }
}

/// <summary>
/// Submit an order, charge the card, finish. Small on purpose, but it touches every store contract a
/// persistence provider implements, so each sample has something provider-specific to show:
/// <list type="bullet">
///   <item>the snapshot (<c>ISagaSnapshotStore</c>) — <see cref="CheckoutState"/>, versioned on every persist;</item>
///   <item>the event log (<c>ISagaEventLogStore</c>) — every step's timeline entries;</item>
///   <item>the business key — <c>CorrelateOn(OrderNumber)</c>, one instance per order number;</item>
///   <item>a timeout (<c>ISagaTimeoutStore</c>) — scheduled on entering <see cref="AwaitingPayment"/>,
///     cancelled by a reply, fired when the gateway never answers;</item>
///   <item>the outbox (<c>ISagaOutboxStore</c>) — the hosts run the engine with
///     <c>SagaOutboxMode.All</c>, so both publishes below are committed with the snapshot first.</item>
/// </list>
/// </summary>
public sealed class CheckoutSaga : OrchestratedSagaDefinition<CheckoutState>
{
    /// <summary>
    /// How long the gateway gets to reply. The timeout dispatcher polls every 5 seconds, so the
    /// no-response scenario ends 5–10 seconds after submission.
    /// </summary>
    public static readonly TimeSpan PaymentTimeout = TimeSpan.FromSeconds(5);

    public State<CheckoutState> Submitted { get; }
    public State<CheckoutState> AwaitingPayment { get; }
    public State<CheckoutState> Paid { get; }
    public State<CheckoutState> PaymentDeclined { get; }
    public State<CheckoutState> PaymentTimedOut { get; }

    public CheckoutSaga()
    {
        // Declared first: a CorrelateBy written before CorrelateOn registers no business-key extractor.
        CorrelateOn(s => s.OrderNumber);

        Submitted = InitialState(nameof(Submitted));
        AwaitingPayment = State(nameof(AwaitingPayment));
        Paid = State(nameof(Paid));
        PaymentDeclined = State(nameof(PaymentDeclined));
        PaymentTimedOut = State(nameof(PaymentTimedOut));

        During(Submitted)
            .When<SubmitOrder>()
                .CorrelateBy(m => m.OrderNumber, s => s.OrderNumber)
                .Then((ctx, m) => ctx.Saga.Amount = m.Amount)
                .Publish((ctx, m) => new ChargeCard(ctx.CorrelationId, m.OrderNumber, m.Amount, m.PaymentToken))
                .TransitionTo(AwaitingPayment);

        During(AwaitingPayment)
            .When<CardCharged>()
                .Then((ctx, m) => ctx.Saga.AuthorizationCode = m.AuthorizationCode)
                .Publish((ctx, m) => new OrderConfirmed(ctx.CorrelationId, m.OrderNumber))
                .TransitionTo(Paid)
                .Finalize(SagaStatus.Completed)
            .When<CardDeclined>()
                .Then((ctx, m) => ctx.Saga.DeclineReason = m.Reason)
                .TransitionTo(PaymentDeclined)
                .Finalize(SagaStatus.Failed);

        WithTimeout(AwaitingPayment, PaymentTimeout,
            t => t.TransitionTo(PaymentTimedOut).Finalize(SagaStatus.TimedOut));
    }
}
