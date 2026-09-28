using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;
using VSaga.Samples.OrderProcessing.Contracts;

namespace VSaga.Samples.OrderProcessing;

public sealed class PostShipmentState : SagaState
{
    public string? OrderId { get; set; }

    public string? TrackingNumber { get; set; }

    public bool CustomerNotified { get; set; }

    public bool PointsAwarded { get; set; }

    public bool InvoiceIssued { get; set; }

    /// <summary>The three post-shipment services that have reported in so far, out of three.</summary>
    public int CompletedBranches =>
        (CustomerNotified ? 1 : 0) + (PointsAwarded ? 1 : 0) + (InvoiceIssued ? 1 : 0);
}

/// <summary>
/// The sample's choreographed counterpart to <see cref="OrderSaga"/>, tracking what happens after an
/// order ships: NotificationService, LoyaltyService, and InvoicingService each react to
/// <see cref="OrderShipped"/> independently and publish their own event. No conductor exists — this
/// definition commands none of the three, it only records the fan-out and decides when the whole leg
/// is done.
///
/// <para>
/// <b>The one thing it does publish</b> is a sub-saga: on <see cref="InvoiceIssued"/> it starts an
/// <see cref="InvoiceDeliverySaga"/> and does not wait for it. That is not a command to any of the
/// three services above and does not participate in the join below — see the <c>On&lt;InvoiceIssued&gt;</c>
/// branch.
/// </para>
///
/// <para>
/// <b>It runs under <see cref="OrderSaga"/>'s own correlation id</b>, which is the point: it is the
/// same business transaction, so both sagas track the same order under one id and the dashboard's
/// <c>/api/correlations/{id}</c> shows them side by side. That is only possible because a saga
/// instance is keyed by <c>(SagaType, CorrelationId)</c> — see docs/concepts.md's "Saga instances and
/// identity" section. The three participants above propagate the inbound correlation id onto their
/// replies (via <c>MessageEnvelope.From</c>), so their events land on this instance without anyone
/// minting a new id.
/// </para>
///
/// <para>
/// <b>Why every branch declares <c>StartsNewInstance()</c>.</b> The three services are genuinely
/// independent and deliberately differ in latency, so a fast one can publish before this tracker has
/// processed <see cref="OrderShipped"/> itself. Whichever event arrives first has to be able to open
/// the instance, or that branch would be dropped as an event for a saga that does not exist yet. An
/// orchestrated saga cannot express this: it has exactly one initial state and one designated first
/// step.
/// </para>
/// </summary>
public sealed class PostShipmentChoreography : ChoreographedSagaDefinition<PostShipmentState>
{
    public State<PostShipmentState> AwaitingFulfilment { get; }
    public State<PostShipmentState> Shipped { get; }
    public State<PostShipmentState> Notified { get; }
    public State<PostShipmentState> PointsAwarded { get; }
    public State<PostShipmentState> Invoiced { get; }
    public State<PostShipmentState> Abandoned { get; }

    /// <summary>
    /// A stalled leg is failed rather than left Running forever. Generous relative to the participants'
    /// sub-second delays: it should only ever fire when a message genuinely goes missing, which in this
    /// sample means chaos mode's Drop fault (see docker-compose.chaos.yml) rather than ordinary slowness.
    /// </summary>
    private static readonly TimeSpan FulfilmentStallTimeout = TimeSpan.FromSeconds(45);

    public PostShipmentChoreography()
    {
        AwaitingFulfilment = InitialState(nameof(AwaitingFulfilment));
        Shipped = State(nameof(Shipped));
        Notified = State(nameof(Notified));
        PointsAwarded = State(nameof(PointsAwarded));
        Invoiced = State(nameof(Invoiced));
        Abandoned = State(nameof(Abandoned));

        // Opens the instance and captures the tracking number, but is not itself a branch of the join:
        // OrderShipped is the trigger the other three services are reacting to, not one of their results.
        //
        // It records Shipped rather than the initial AwaitingFulfilment, and that is load-bearing rather
        // than cosmetic. The orchestrator schedules a timeout only on a real transition
        // (ToState != FromState), and OrderShipped usually creates the instance, so its FromState is the
        // initial state — recording AwaitingFulfilment here would be a self-transition, schedule nothing,
        // and leave an order whose three post-shipment events were all lost stuck Running forever. Caught
        // by checking TimeoutScheduled rows against the live stack: every other milestone had hundreds,
        // AwaitingFulfilment had none.
        On<OrderShipped>()
            .StartsNewInstance()
            .CorrelateBy(m => m.OrderId, s => s.OrderId)
            .Then((ctx, m) => ctx.Saga.TrackingNumber = m.TrackingNumber)
            .RecordState(Shipped);

        On<CustomerNotified>()
            .StartsNewInstance()
            .CorrelateBy(m => m.OrderId, s => s.OrderId)
            .Then((ctx, _) => ctx.Saga.CustomerNotified = true)
            .RecordState(Notified)
            .Finalize(CompleteWhenAllBranchesReported);

        On<LoyaltyPointsAwarded>()
            .StartsNewInstance()
            .CorrelateBy(m => m.OrderId, s => s.OrderId)
            .Then((ctx, _) => ctx.Saga.PointsAwarded = true)
            .RecordState(PointsAwarded)
            .Finalize(CompleteWhenAllBranchesReported);

        // The one branch that does more than record: issuing the invoice is not the same thing as the
        // customer receiving it, and getting it delivered has its own retry window and its own failure
        // ending. So it becomes a sub-saga rather than more states here — StartChildAsync publishes
        // DeliverInvoice under a fresh correlation id, stamped with this instance's identity, and
        // whichever saga initiates on that message records this one as its parent.
        //
        // This does not wait, and deliberately so: this leg is finished once all three services have
        // reported, and an undeliverable invoice must not hold the order's fulfilment leg open. The
        // child's outcome is visible in the dashboard under this saga's "started" relation rather than
        // being folded back into the join above.
        On<InvoiceIssued>()
            .StartsNewInstance()
            .CorrelateBy(m => m.OrderId, s => s.OrderId)
            .Then((ctx, _) => ctx.Saga.InvoiceIssued = true)
            .Then((ctx, m) => ctx.StartChildAsync(new DeliverInvoice(m.OrderId, m.InvoiceNumber), ctx.CancellationToken))
            .RecordState(Invoiced)
            .Finalize(CompleteWhenAllBranchesReported);

        ConfigureStallTimeouts();
    }

    /// <summary>
    /// Every non-terminal milestone needs its own timeout registration, not just the first. Timeouts are
    /// keyed on CurrentState and the orchestrator cancels the pending one whenever the saga transitions
    /// away — so registering only on the first milestone would be silently cancelled by the first branch
    /// to report, leaving a saga stalled at two-of-three to hang forever.
    /// <para>
    /// <c>AwaitingFulfilment</c> is deliberately absent: it is the initial state, so nothing ever
    /// transitions into it and a timeout registered on it could never be scheduled. <c>Shipped</c>
    /// exists precisely so the instance-creating event has a real transition to carry one.
    /// </para>
    /// </summary>
    private void ConfigureStallTimeouts()
    {
        foreach (var milestone in new[] { Shipped, Notified, PointsAwarded, Invoiced })
        {
            WithTimeout(milestone, FulfilmentStallTimeout,
                t => t.TransitionTo(Abandoned).Finalize(SagaStatus.TimedOut));
        }
    }

    /// <summary>
    /// The join condition, registered identically on all three branches. Evaluated after the branch's
    /// own action has run, so the flag it just set counts. Returning null means "recorded, but the leg
    /// is not finished yet" — whichever branch happens to arrive last is the one that sees all three
    /// flags set and completes the saga, and none of them has to assume it is last.
    ///
    /// <para>
    /// This is why <c>Finalize</c> needed a state-dependent overload. With the fixed-status form the only
    /// options were to complete on a nominated event — wrong, because these three have no fixed order and
    /// the nominated one may land first — or never to complete at all.
    /// </para>
    /// </summary>
    private static SagaStatus? CompleteWhenAllBranchesReported(PostShipmentState state) =>
        state.CustomerNotified && state.PointsAwarded && state.InvoiceIssued
            ? SagaStatus.Completed
            : null;
}
