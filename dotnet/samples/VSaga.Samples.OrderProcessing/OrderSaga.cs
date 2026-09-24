using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;
using VSaga.Samples.OrderProcessing.Contracts;

namespace VSaga.Samples.OrderProcessing;

public sealed class OrderSagaState : SagaState
{
    public string? OrderId { get; set; }

    public string? CustomerId { get; set; }

    public decimal Amount { get; set; }

    public bool InventoryReserved { get; set; }

    public bool PaymentCharged { get; set; }
}

/// <summary>
/// Order -> reserve inventory + charge payment in parallel -> ship -> done, with compensation on any
/// downstream failure and a timeout on every awaiting state (the payment participant occasionally
/// simulates a hung gateway to exercise it). This is the reference orchestrated saga the whole v1 slice
/// is validated against, and — since the "Parallel fan-out and join" pass — the sample demonstration of
/// that primitive too: reserving inventory and charging payment no longer wait on each other.
/// <para>
/// Caveat, on the HTTP track only (Transport:Provider=Http): the HTTP adapter's delivery model is
/// synchronous request/response, so the two publishes below become two *blocking* round-trips and the
/// fan-out is strictly sequential there — the parallelism above is real on every broker-backed adapter,
/// not on that one. See docs/transports/http.md's "Known, deliberate limitations".
/// </para>
/// </summary>
public sealed class OrderSaga : OrchestratedSagaDefinition<OrderSagaState>
{
    public State<OrderSagaState> Submitted { get; }
    public State<OrderSagaState> Gathering { get; }
    public State<OrderSagaState> AwaitingShipment { get; }
    public State<OrderSagaState> Completed { get; }
    public State<OrderSagaState> Failed { get; }

    /// <summary>
    /// How long any one participant gets to reply before its state is treated as stalled. Generous
    /// against the participants' 150–500ms simulated work and chaos mode's 4s inbound delay ceiling, so
    /// it fires on genuinely lost messages rather than on ordinary slowness.
    /// </summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    public OrderSaga()
    {
        Submitted = InitialState(nameof(Submitted));
        Gathering = State(nameof(Gathering));
        AwaitingShipment = State(nameof(AwaitingShipment));
        Completed = State(nameof(Completed));
        Failed = State(nameof(Failed));

        During(Submitted)
            .When<OrderSubmitted>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .Then((ctx, m) =>
                {
                    ctx.Saga.CustomerId = m.CustomerId;
                    ctx.Saga.Amount = m.Amount;
                })
                .Publish((ctx, m) => new ReserveInventory(ctx.CorrelationId, m.OrderId))
                .Publish((ctx, m) => new ChargePayment(ctx.CorrelationId, m.OrderId, m.Amount))
                .TransitionTo(Gathering);

        ConfigureGathering();

        During(AwaitingShipment)
            .When<OrderShipped>()
                .TransitionTo(Completed)
                .Finalize(SagaStatus.Completed)
            .When<ShipmentFailed>()
                .Compensate()
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed);

        ConfigureRecovery();
    }

    /// <summary>
    /// The join: nothing here assumes which reply lands first, or that the other branch has even been
    /// sent yet from the participant's point of view. Registering the same computed TransitionTo
    /// selector on every branch — the pattern EventBuilder.TransitionTo's own doc comment demonstrates —
    /// is what lets either order release the join. Publishing ShipOrder from inside the *last* branch's
    /// own Then (rather than as a separate step) is the one thing that selector pattern doesn't cover by
    /// itself: no branch alone knows whether it is first or last, so each checks the other's flag before
    /// deciding to publish. Split out of the constructor purely for length, same reason as
    /// ConfigureRecovery below.
    /// </summary>
    private void ConfigureGathering()
    {
        During(Gathering)
            .When<InventoryReserved>()
                .Then(async (ctx, _) =>
                {
                    ctx.Saga.InventoryReserved = true;
                    if (ctx.Saga.PaymentCharged)
                        await ctx.PublishAsync(new ShipOrder(ctx.CorrelationId, ctx.Saga.OrderId!), ctx.CancellationToken);
                })
                .TransitionTo(s => s.InventoryReserved && s.PaymentCharged ? AwaitingShipment : Gathering)
            .When<PaymentCharged>()
                .Then(async (ctx, _) =>
                {
                    ctx.Saga.PaymentCharged = true;
                    if (ctx.Saga.InventoryReserved)
                        await ctx.PublishAsync(new ShipOrder(ctx.CorrelationId, ctx.Saga.OrderId!), ctx.CancellationToken);
                })
                .TransitionTo(s => s.InventoryReserved && s.PaymentCharged ? AwaitingShipment : Gathering)
            // Reserving and charging in parallel means either failure can now arrive while the other
            // branch has already succeeded — impossible in the old strictly-sequential shape, where
            // payment was never attempted until inventory had already been confirmed. Both branches
            // therefore compensate defensively (see ConfigureRecovery), not just the payment one.
            .When<InventoryReservationFailed>()
                .Compensate()
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed)
            .When<PaymentFailed>()
                .Compensate()
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed);
    }

    /// <summary>
    /// How a stalled or failed order unwinds. Split out of the constructor purely for length; it reads
    /// as one topic, and every rule here is about undoing work rather than driving the order forward.
    /// </summary>
    private void ConfigureRecovery()
    {
        // Gathering's compensation can't tell which of the two branches actually landed — a failure or
        // a timeout here might follow either reservation, both, or neither having gone through — so, per
        // the same defensive-compensation constraint the timeout paths already depend on, it always
        // sends both release and refund rather than checking the flags. Both participants already treat
        // their compensating command as a safe no-op for work that never happened (see
        // InventoryParticipant/PaymentParticipant), which is exactly what makes that safe rather than
        // just convenient.
        //
        // Sequential awaits, not Task.WhenAll: ctx.PublishAsync shares this saga's own SagaContext (and,
        // transitively, the one DbContext behind its event log) across every action in a step, and that
        // is only ever safe to use one operation at a time — the same reason .Publish(...).Publish(...)
        // chains elsewhere in this DSL run their actions sequentially rather than concurrently. Running
        // both publishes concurrently here threw "a second operation was started on this context
        // instance" under real chaos-testing load, and a caught exception on one branch could silently
        // leave the other's compensating message unsent — found live, not merely theoretical.
        Compensate(Gathering, async (ctx, ct) =>
        {
            await ctx.PublishAsync(new ReleaseInventory(ctx.CorrelationId, ctx.Saga.OrderId!), ct);
            await ctx.PublishAsync(new RefundPayment(ctx.CorrelationId, ctx.Saga.OrderId!), ct);
        });
        Compensate(AwaitingShipment, async (ctx, ct) =>
        {
            await ctx.PublishAsync(new ReleaseInventory(ctx.CorrelationId, ctx.Saga.OrderId!), ct);
            await ctx.PublishAsync(new RefundPayment(ctx.CorrelationId, ctx.Saga.OrderId!), ct);
        });

        // Every state that waits on a reply gets a timeout. Each is the same shape —
        // Compensate().TransitionTo(Failed).Finalize(TimedOut) — but Gathering's self-transition while
        // only one branch has reported does not cancel or reschedule this timeout (a self-transition is
        // "no transition" to the orchestrator), so one deadline really does cover the whole gather:
        //
        //   Gathering         — releases whichever of the inventory hold / payment charge went through,
        //                       defensively, since a stalled gather could be waiting on either branch.
        //   AwaitingShipment  — refunds and releases; both branches are known to have already succeeded
        //                       to have reached this state at all.
        //
        // A timeout here always means "no reply arrived in time", never "the participant declined" —
        // a decline is a real reply and takes the explicit ...Failed branch instead. So compensation is
        // necessarily defensive: the request may well have succeeded with only its reply lost, so the
        // compensating messages must be safe to receive for work that never happened. That is a real
        // constraint this sample places on its participants, not an incidental detail.
        foreach (var awaitingReply in new[] { Gathering, AwaitingShipment })
        {
            WithTimeout(awaitingReply, ReplyTimeout,
                t => t.Compensate().TransitionTo(Failed).Finalize(SagaStatus.TimedOut));
        }
    }
}
