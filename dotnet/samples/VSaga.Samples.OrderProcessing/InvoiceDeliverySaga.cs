using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;
using VSaga.Samples.OrderProcessing.Contracts;

namespace VSaga.Samples.OrderProcessing;

public sealed class InvoiceDeliveryState : SagaState
{
    public string? OrderId { get; set; }

    public string? InvoiceNumber { get; set; }
}

/// <summary>
/// The sample's sub-saga: getting an issued invoice into the customer's hands, which is its own small
/// flow with its own retry window and its own failure ending. <see cref="PostShipmentChoreography"/>
/// starts one per <see cref="InvoiceIssued"/> via <c>ctx.StartChildAsync</c> and does not wait for it.
///
/// <para>
/// <b>It runs under its own correlation id</b>, and that is the difference from
/// <c>PostShipmentChoreography</c>, which deliberately shares <see cref="OrderSaga"/>'s. Those two are
/// one business transaction observed twice; this is a separate unit of work started by one of them, so
/// it gets its own id and a stored pointer back to the instance that started it
/// (<c>SagaState.ParentSagaType</c>/<c>ParentCorrelationId</c>, stamped by the engine from headers on
/// <see cref="DeliverInvoice"/>). The dashboard shows the pair as "started by" / "started", and
/// <c>GET /api/sagas/{sagaType}/{correlationId}/children</c> is the query behind it.
/// </para>
///
/// <para>
/// <b>Nothing here knows it is a child.</b> The parent published <see cref="DeliverInvoice"/> and this
/// saga's <c>CanInitiate</c> matched it — there is no compile-time link in either direction. The one
/// place the relationship shows up is <c>Saga.ParentCorrelationId</c>, which is what a child would
/// address if it ever needed to report back. It doesn't: nothing waits on this saga, so a bounced
/// invoice ends here as a Failed sub-saga and the order it belongs to is unaffected.
/// </para>
/// </summary>
public sealed class InvoiceDeliverySaga : OrchestratedSagaDefinition<InvoiceDeliveryState>
{
    public State<InvoiceDeliveryState> Requested { get; }
    public State<InvoiceDeliveryState> AwaitingDelivery { get; }
    public State<InvoiceDeliveryState> Delivered { get; }
    public State<InvoiceDeliveryState> Undeliverable { get; }

    /// <summary>
    /// How long the mail service gets to report back. Shorter than <see cref="OrderSaga"/>'s 30s reply
    /// timeout on purpose — a child's lifecycle is its own, and not having to inherit the parent's
    /// deadlines is half the reason to make it a separate saga rather than another state on the parent.
    /// </summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(20);

    public InvoiceDeliverySaga()
    {
        Requested = InitialState(nameof(Requested));
        AwaitingDelivery = State(nameof(AwaitingDelivery));
        Delivered = State(nameof(Delivered));
        Undeliverable = State(nameof(Undeliverable));

        During(Requested)
            .When<DeliverInvoice>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .Then((ctx, m) => ctx.Saga.InvoiceNumber = m.InvoiceNumber)
                .Publish((ctx, m) => new SendInvoiceEmail(ctx.CorrelationId, m.OrderId, m.InvoiceNumber))
                .TransitionTo(AwaitingDelivery);

        During(AwaitingDelivery)
            .When<InvoiceEmailSent>()
                .TransitionTo(Delivered)
                .Finalize(SagaStatus.Completed)
            .When<InvoiceEmailBounced>()
                .TransitionTo(Undeliverable)
                .Finalize(SagaStatus.Failed);

        // Same rule docs/history/timeout-coverage-every-awaiting-state.md arrived at the
        // hard way: a state that waits on a reply gets a timeout, or a lost message parks the instance
        // Running forever. No Compensate() — nothing has been reserved or charged that a bounced or
        // silent invoice email would need to unwind.
        WithTimeout(AwaitingDelivery, DeliveryTimeout,
            t => t.TransitionTo(Undeliverable).Finalize(SagaStatus.TimedOut));
    }
}
