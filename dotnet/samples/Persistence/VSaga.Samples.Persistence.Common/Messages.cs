namespace VSaga.Samples.Persistence.Common;

// Initiating message. OrderNumber is the saga's business key (CheckoutSaga's CorrelateOn), so a second
// SubmitOrder for the same order number finds the existing instance instead of opening another one —
// each provider enforces that with its own unique index or reservation.
//
// PaymentToken, not a card number: the engine stores every inbound message body in the event log
// (the SagaStarted/MessageReceived payloads), so nothing a store should not hold belongs in a message.
public sealed record SubmitOrder(string OrderNumber, decimal Amount, string PaymentToken);

public sealed record ChargeCard(Guid CorrelationId, string OrderNumber, decimal Amount, string PaymentToken);
public sealed record CardCharged(Guid CorrelationId, string OrderNumber, string AuthorizationCode);
public sealed record CardDeclined(Guid CorrelationId, string OrderNumber, string Reason);

// Published once the payment lands. Nothing subscribes to it in these samples; it is here so the final
// step also writes an outbox row, not only the first.
public sealed record OrderConfirmed(Guid CorrelationId, string OrderNumber);

/// <summary>
/// The payment tokens <see cref="PaymentGateway"/> understands, in the style of a payment provider's
/// test tokens: each one forces one outcome, so every run produces the same three sagas.
/// </summary>
public static class PaymentTokens
{
    public const string Approved = "tok_approved";
    public const string Declined = "tok_declined";

    /// <summary>The gateway accepts the charge and never replies — CheckoutSaga's timeout ends the saga.</summary>
    public const string NoResponse = "tok_no_response";
}
