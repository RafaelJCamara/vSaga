using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Dashboard.Api.Endpoints;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// <see cref="SagaRetryPlanner"/> on its own (docs/design/dashboard-usability-and-access.md §7.2 and §7.6): one
/// case per failure kind, the recency rule, the entries it must never pick (an id-less DeliveryExhausted, a
/// <c>.CallHttp</c> reply logged mid-step, a StatePersisted carrying the step's id), and every way a plan is
/// not retryable. Sequence numbers follow the design's worked examples where there is one.
/// </summary>
public sealed class SagaRetryPlannerTests
{
    private static readonly Guid CorrelationId = Guid.NewGuid();

    [Fact]
    public void Plan_TechnicalFailure_ReRunsTheFailedMessageFromItsStateAndSkipsTheIdlessDeliveryExhausted()
    {
        var plan = SagaRetryPlanner.Plan(Summary("Gathering", SagaStatus.Failed),
        [
            Entry(30, SagaEntryType.MessageReceived, messageType: "PaymentCharged", messageId: "a1", payload: "{\"Received\":1}"),
            Entry(31, SagaEntryType.StepFailed, fromState: "Gathering", messageType: "PaymentCharged", messageId: "a1",
                payload: "{\"Failed\":1}", error: "boom"),
            Entry(32, SagaEntryType.StatePersisted, messageType: "PaymentCharged", messageId: "a1", payload: "{\"Status\":2}"),
            Entry(33, SagaEntryType.DeliveryExhausted, error: "Deferred publish discarded"),
        ]);

        Assert.True(plan.Retryable);
        Assert.Null(plan.Reason);
        Assert.Equal(SagaRetryFailureKind.StepFailed, plan.FailureKind);
        Assert.Equal(31, plan.FailureSequenceNumber);
        Assert.Equal(new SagaRetryStep(30, "PaymentCharged", "a1", "Gathering"), plan.Step);
        Assert.Equal("{\"Failed\":1}", plan.MessageBody);
    }

    [Fact]
    public void Plan_TechnicalFailureWithNoInboundEntry_PointsTheStepAtTheFailureEntry()
    {
        var plan = SagaRetryPlanner.Plan(Summary("Gathering", SagaStatus.Failed),
        [
            Entry(7, SagaEntryType.StepFailed, fromState: "Gathering", messageType: "PaymentCharged", messageId: "a1", payload: "{}"),
        ]);

        Assert.True(plan.Retryable);
        Assert.Equal(7, plan.Step!.SequenceNumber);
    }

    [Fact]
    public void Plan_BusinessFailureWithTheStepIdOnSagaCompleted_ReRunsThatStepFromItsFromState()
    {
        var plan = SagaRetryPlanner.Plan(Summary("Failed", SagaStatus.Failed),
        [
            Entry(130, SagaEntryType.MessageReceived, messageType: "InventoryReserved", messageId: "b2", payload: "{\"B\":2}"),
            Entry(131, SagaEntryType.StepSucceeded, fromState: "Reserving", toState: "Gathering", messageType: "InventoryReserved", messageId: "b2"),
            Entry(138, SagaEntryType.MessageReceived, messageType: "PaymentFailed", messageId: "c3", payload: "{\"C\":3}"),
            Entry(139, SagaEntryType.CompensationStarted),
            Entry(140, SagaEntryType.CompensationStepSucceeded, fromState: "Gathering"),
            Entry(141, SagaEntryType.StepSucceeded, fromState: "Gathering", toState: "Failed", messageType: "PaymentFailed", messageId: "c3"),
            Entry(142, SagaEntryType.SagaCompleted, toState: "Failed", messageId: "c3"),
            Entry(143, SagaEntryType.StatePersisted, messageType: "PaymentFailed", messageId: "c3", payload: "{\"Status\":2}"),
        ]);

        Assert.True(plan.Retryable);
        Assert.Equal(SagaRetryFailureKind.BusinessFailure, plan.FailureKind);
        Assert.Equal(142, plan.FailureSequenceNumber);
        Assert.Equal(new SagaRetryStep(138, "PaymentFailed", "c3", "Gathering"), plan.Step);
        Assert.Equal("{\"C\":3}", plan.MessageBody);
    }

    [Fact]
    public void Plan_BusinessFailureWithNoIdOnSagaCompleted_TakesTheLatestStepSucceeded()
    {
        // Recorded before the engine stamped the step's id on SagaCompleted.
        var plan = SagaRetryPlanner.Plan(Summary("Failed", SagaStatus.Failed),
        [
            Entry(1, SagaEntryType.MessageReceived, messageType: "InventoryReserved", messageId: "b2", payload: "{\"B\":2}"),
            Entry(2, SagaEntryType.StepSucceeded, fromState: "Reserving", toState: "Gathering", messageType: "InventoryReserved", messageId: "b2"),
            Entry(3, SagaEntryType.MessageReceived, messageType: "PaymentFailed", messageId: "c3", payload: "{\"C\":3}"),
            Entry(4, SagaEntryType.StepSucceeded, fromState: "Gathering", toState: "Failed", messageType: "PaymentFailed", messageId: "c3"),
            Entry(5, SagaEntryType.SagaCompleted, toState: "Failed"),
        ]);

        Assert.True(plan.Retryable);
        Assert.Equal(SagaRetryFailureKind.BusinessFailure, plan.FailureKind);
        Assert.Equal(5, plan.FailureSequenceNumber);
        Assert.Equal(new SagaRetryStep(3, "PaymentFailed", "c3", "Gathering"), plan.Step);
    }

    [Fact]
    public void Plan_BusinessFailureAfterACallHttpReplyLoggedMidStep_NeverPicksTheReply()
    {
        // The reply is the latest inbound entry before the failure, with its own fresh id and a payload of its
        // own; the step's id (from StepSucceeded, SagaCompleted carrying none) is what identifies the step.
        var plan = SagaRetryPlanner.Plan(Summary("Failed", SagaStatus.Failed),
        [
            Entry(10, SagaEntryType.MessageReceived, messageType: "OrderPriced", messageId: "m5", payload: "{\"Step\":true}"),
            Entry(11, SagaEntryType.MessagePublished, messageType: "POST /payments", messageId: "call-1", causationId: "m5"),
            Entry(12, SagaEntryType.MessageReceived, messageType: "PaymentDeclined", messageId: "reply-1", payload: "{\"Reply\":true}",
                causationId: "call-1"),
            Entry(13, SagaEntryType.StepSucceeded, fromState: "Pricing", toState: "Failed", messageType: "OrderPriced", messageId: "m5"),
            Entry(14, SagaEntryType.SagaCompleted, toState: "Failed"),
        ]);

        Assert.True(plan.Retryable);
        Assert.Equal(new SagaRetryStep(10, "OrderPriced", "m5", "Pricing"), plan.Step);
        Assert.Equal("{\"Step\":true}", plan.MessageBody);
    }

    [Fact]
    public void Plan_TechnicalFailureFixedByARetryBeforeALaterBusinessFailure_ReRunsTheLaterStep()
    {
        var plan = SagaRetryPlanner.Plan(Summary("Failed", SagaStatus.Failed),
        [
            Entry(1, SagaEntryType.MessageReceived, messageType: "InventoryReserved", messageId: "m1", payload: "{\"First\":1}"),
            Entry(2, SagaEntryType.StepFailed, fromState: "Reserving", messageType: "InventoryReserved", messageId: "m1",
                payload: "{\"First\":1}", error: "boom"),
            Entry(3, SagaEntryType.ManualRetryRequested, fromState: "Reserving", toState: "Reserving", messageType: "InventoryReserved", messageId: "m1"),
            Entry(4, SagaEntryType.MessageReceived, messageType: "InventoryReserved", messageId: "m1-redrive", payload: "{\"First\":1}"),
            Entry(5, SagaEntryType.StepSucceeded, fromState: "Reserving", toState: "Gathering", messageType: "InventoryReserved", messageId: "m1-redrive"),
            Entry(6, SagaEntryType.MessageReceived, messageType: "PaymentFailed", messageId: "m2", payload: "{\"Second\":2}"),
            Entry(7, SagaEntryType.StepSucceeded, fromState: "Gathering", toState: "Failed", messageType: "PaymentFailed", messageId: "m2"),
            Entry(8, SagaEntryType.SagaCompleted, toState: "Failed", messageId: "m2"),
        ]);

        Assert.True(plan.Retryable);
        Assert.Equal(SagaRetryFailureKind.BusinessFailure, plan.FailureKind);
        Assert.Equal(8, plan.FailureSequenceNumber);
        Assert.Equal(new SagaRetryStep(6, "PaymentFailed", "m2", "Gathering"), plan.Step);
    }

    [Fact]
    public void Plan_StatePersistedCarryingTheStepsIdAndABlob_IsNeverTakenForTheBody()
    {
        var plan = SagaRetryPlanner.Plan(Summary("Failed", SagaStatus.Failed),
        [
            Entry(1, SagaEntryType.MessageReceived, messageType: "PaymentFailed", messageId: "m2", payload: "{\"Message\":true}"),
            Entry(2, SagaEntryType.StepSucceeded, fromState: "Gathering", toState: "Failed", messageType: "PaymentFailed", messageId: "m2"),
            Entry(3, SagaEntryType.StatePersisted, messageType: "PaymentFailed", messageId: "m2", payload: "{\"State\":true}"),
            Entry(4, SagaEntryType.SagaCompleted, toState: "Failed", messageId: "m2"),
            Entry(5, SagaEntryType.StatePersisted, messageType: "PaymentFailed", messageId: "m2", payload: "{\"State\":true}"),
        ]);

        Assert.True(plan.Retryable);
        Assert.Equal(4, plan.FailureSequenceNumber);
        Assert.Equal(1, plan.Step!.SequenceNumber);
        Assert.Equal("{\"Message\":true}", plan.MessageBody);
    }

    [Fact]
    public void Plan_TimedOutThroughAMessageStep_IsABusinessFailure()
    {
        // InvoiceFollowUpSaga's ChildSagaFinished branch: TimedOut status, but no timeout fired.
        var plan = SagaRetryPlanner.Plan(Summary("Abandoned", SagaStatus.TimedOut),
        [
            Entry(1, SagaEntryType.MessageReceived, messageType: "ChildSagaFinished", messageId: "f1", payload: "{}"),
            Entry(2, SagaEntryType.StepSucceeded, fromState: "AwaitingArchival", toState: "Abandoned", messageType: "ChildSagaFinished", messageId: "f1"),
            Entry(3, SagaEntryType.SagaCompleted, toState: "Abandoned", messageId: "f1"),
        ]);

        Assert.True(plan.Retryable);
        Assert.Equal(SagaRetryFailureKind.BusinessFailure, plan.FailureKind);
        Assert.Equal(new SagaRetryStep(1, "ChildSagaFinished", "f1", "AwaitingArchival"), plan.Step);
    }

    [Fact]
    public void Plan_TimeoutFired_ReRunsTheStepThatEnteredTheTimedOutState()
    {
        var plan = SagaRetryPlanner.Plan(Summary("Abandoned", SagaStatus.TimedOut),
        [
            Entry(60, SagaEntryType.SagaStarted, toState: "Requested", messageType: "InvoiceIssued", messageId: "e5", payload: "{\"Raw\":true}"),
            Entry(61, SagaEntryType.MessageReceived, messageType: "InvoiceIssued", messageId: "e5", payload: "{\"Serialized\":true}"),
            Entry(62, SagaEntryType.ChildSagaStarted, messageType: "ArchiveInvoice", messageId: "out-1", causationId: "e5"),
            Entry(63, SagaEntryType.StepSucceeded, fromState: "Requested", toState: "AwaitingArchival", messageType: "InvoiceIssued", messageId: "e5"),
            Entry(64, SagaEntryType.TimeoutScheduled, toState: "AwaitingArchival", messageId: "e5"),
            Entry(65, SagaEntryType.StatePersisted, messageType: "InvoiceIssued", messageId: "e5", payload: "{}"),
            Entry(66, SagaEntryType.TimeoutFired, fromState: "AwaitingArchival"),
            Entry(67, SagaEntryType.StepSucceeded, fromState: "AwaitingArchival", toState: "Abandoned"),
            Entry(68, SagaEntryType.StatePersisted, payload: "{\"Status\":5}"),
        ]);

        Assert.True(plan.Retryable);
        Assert.Equal(SagaRetryFailureKind.TimedOut, plan.FailureKind);
        Assert.Equal(66, plan.FailureSequenceNumber);
        Assert.Equal(new SagaRetryStep(61, "InvoiceIssued", "e5", "Requested"), plan.Step);
        Assert.Equal("{\"Serialized\":true}", plan.MessageBody);
    }

    [Fact]
    public void Plan_TimedOutStateEnteredByATimeout_IsNotRetryable()
    {
        var plan = SagaRetryPlanner.Plan(Summary("Abandoned", SagaStatus.TimedOut),
        [
            Entry(1, SagaEntryType.MessageReceived, messageType: "InvoiceIssued", messageId: "e5", payload: "{}"),
            Entry(2, SagaEntryType.StepSucceeded, fromState: "Requested", toState: "AwaitingArchival", messageType: "InvoiceIssued", messageId: "e5"),
            Entry(3, SagaEntryType.TimeoutFired, fromState: "AwaitingArchival"),
            Entry(4, SagaEntryType.StepSucceeded, fromState: "AwaitingArchival", toState: "Reminding"),
            Entry(5, SagaEntryType.TimeoutFired, fromState: "Reminding"),
            Entry(6, SagaEntryType.StepSucceeded, fromState: "Reminding", toState: "Abandoned"),
        ]);

        Assert.False(plan.Retryable);
        Assert.Equal(SagaRetryPlanner.EnteredByTimeoutReason, plan.Reason);
        Assert.Equal(SagaRetryFailureKind.TimedOut, plan.FailureKind);
        Assert.Equal(5, plan.FailureSequenceNumber);
        Assert.Null(plan.Step);
    }

    [Fact]
    public void Plan_TimeoutInAStateNoStepEntered_FindsNoStepButStillNamesTheFailure()
    {
        var plan = SagaRetryPlanner.Plan(Summary("Abandoned", SagaStatus.TimedOut),
        [
            Entry(1, SagaEntryType.SagaStarted, toState: "Requested", messageType: "InvoiceIssued", messageId: "e5", payload: "{}"),
            Entry(2, SagaEntryType.TimeoutFired, fromState: "Requested"),
        ]);

        Assert.False(plan.Retryable);
        Assert.Equal(SagaRetryPlanner.NoStepReason, plan.Reason);
        Assert.Equal(SagaRetryFailureKind.TimedOut, plan.FailureKind);
        Assert.Equal(2, plan.FailureSequenceNumber);
        Assert.Null(plan.Step);
    }

    [Fact]
    public void Plan_DeliveryExhaustedRecordedOnTheFinalAttempt_ReRunsItFromTheCurrentState()
    {
        var plan = SagaRetryPlanner.Plan(Summary("Gathering", SagaStatus.Failed),
        [
            Entry(50, SagaEntryType.MessageReceived, messageType: "InventoryReserved", messageId: "d4", payload: "{\"D\":4}"),
            Entry(51, SagaEntryType.DeliveryExhausted, messageType: "InventoryReserved", messageId: "d4", error: "store down"),
            Entry(52, SagaEntryType.StatePersisted, messageType: "InventoryReserved", messageId: "d4", payload: "{\"Status\":2}"),
        ]);

        Assert.True(plan.Retryable);
        Assert.Equal(SagaRetryFailureKind.DeliveryExhausted, plan.FailureKind);
        Assert.Equal(51, plan.FailureSequenceNumber);
        Assert.Equal(new SagaRetryStep(50, "InventoryReserved", "d4", "Gathering"), plan.Step);
        Assert.Equal("{\"D\":4}", plan.MessageBody);
    }

    [Fact]
    public void Plan_DeliveryExhaustedNeverRecorded_IsNotRetryableAndSaysItMayNotHaveBeenRecorded()
    {
        var plan = SagaRetryPlanner.Plan(Summary("Gathering", SagaStatus.Failed),
        [
            Entry(51, SagaEntryType.DeliveryExhausted, messageType: "InventoryReserved", messageId: "d4", error: "store down"),
        ]);

        Assert.False(plan.Retryable);
        Assert.StartsWith("This saga was recorded before vSaga stored the message of every step", plan.Reason, StringComparison.Ordinal);
        Assert.Contains("dead-lettered before it was recorded", plan.Reason, StringComparison.Ordinal);
        Assert.Equal(SagaRetryFailureKind.DeliveryExhausted, plan.FailureKind);
        Assert.Equal(new SagaRetryStep(51, "InventoryReserved", "d4", "Gathering"), plan.Step);
        Assert.Null(plan.MessageBody);
    }

    [Fact]
    public void Plan_StepWhoseMessageWasRecordedWithoutABody_IsNotRetryableAndExplainsWhy()
    {
        // A saga recorded before MessageReceived carried the message body.
        var plan = SagaRetryPlanner.Plan(Summary("Failed", SagaStatus.Failed),
        [
            Entry(1, SagaEntryType.SagaStarted, toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payload: "{}"),
            Entry(2, SagaEntryType.MessageReceived, messageType: "OrderSubmitted", messageId: "m0"),
            Entry(3, SagaEntryType.StepSucceeded, fromState: "Submitted", toState: "Gathering", messageType: "OrderSubmitted", messageId: "m0"),
            Entry(4, SagaEntryType.MessageReceived, messageType: "PaymentFailed", messageId: "c3"),
            Entry(5, SagaEntryType.StepSucceeded, fromState: "Gathering", toState: "Failed", messageType: "PaymentFailed", messageId: "c3"),
            Entry(6, SagaEntryType.SagaCompleted, toState: "Failed"),
        ]);

        Assert.False(plan.Retryable);
        Assert.Equal(
            "This saga was recorded before vSaga stored the message of every step, so the PaymentFailed message that ran the step to re-run cannot be replayed.",
            plan.Reason);
        Assert.Equal(SagaRetryFailureKind.BusinessFailure, plan.FailureKind);
        Assert.Equal(6, plan.FailureSequenceNumber);
        Assert.Equal(new SagaRetryStep(4, "PaymentFailed", "c3", "Gathering"), plan.Step);
    }

    [Fact]
    public void Plan_FirstStepOfASagaRecordedWithoutBodies_StaysRetryableThroughSagaStarted()
    {
        var plan = SagaRetryPlanner.Plan(Summary("Rejected", SagaStatus.Failed),
        [
            Entry(1, SagaEntryType.SagaStarted, toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payload: "{\"Raw\":true}"),
            Entry(2, SagaEntryType.MessageReceived, messageType: "OrderSubmitted", messageId: "m0"),
            Entry(3, SagaEntryType.StepSucceeded, fromState: "Submitted", toState: "Rejected", messageType: "OrderSubmitted", messageId: "m0"),
            Entry(4, SagaEntryType.SagaCompleted, toState: "Rejected"),
        ]);

        Assert.True(plan.Retryable);
        Assert.Equal(new SagaRetryStep(2, "OrderSubmitted", "m0", "Submitted"), plan.Step);
        Assert.Equal("{\"Raw\":true}", plan.MessageBody);
    }

    [Fact]
    public void Plan_StepWhoseBodyWasReplacedByTheMongoSizeMarker_IsNotRetryableAndSaysItWasTooLarge()
    {
        // MongoSagaEventLogStore stores this in place of a payload above MaxPayloadJsonBytes. Replayed, it would
        // deserialise into a message whose every field is default, and the step would re-run on that.
        const string marker = "{\"$vsagaPayloadOmitted\":true,\"bytes\":13000000,\"limit\":12582912}";
        var plan = SagaRetryPlanner.Plan(Summary("Failed", SagaStatus.Failed),
        [
            Entry(1, SagaEntryType.MessageReceived, messageType: "PaymentFailed", messageId: "c3", payload: marker),
            Entry(2, SagaEntryType.StepSucceeded, fromState: "Gathering", toState: "Failed", messageType: "PaymentFailed", messageId: "c3"),
            Entry(3, SagaEntryType.SagaCompleted, toState: "Failed", messageId: "c3"),
        ]);

        Assert.False(plan.Retryable);
        Assert.Equal(SagaRetryPlanner.BodyTooLargeReason, plan.Reason);
        Assert.Equal(SagaRetryFailureKind.BusinessFailure, plan.FailureKind);
        Assert.Equal(new SagaRetryStep(1, "PaymentFailed", "c3", "Gathering"), plan.Step);
        Assert.Null(plan.MessageBody);
    }

    [Fact]
    public void Plan_TechnicalFailureWhoseBodyIsTheSizeMarker_IsNotRetryableAndSaysItWasTooLarge()
    {
        const string marker = "{\"$vsagaPayloadOmitted\":true,\"bytes\":13000000,\"limit\":12582912}";
        var plan = SagaRetryPlanner.Plan(Summary("Gathering", SagaStatus.Failed),
        [
            Entry(1, SagaEntryType.MessageReceived, messageType: "PaymentCharged", messageId: "a1", payload: marker),
            Entry(2, SagaEntryType.StepFailed, fromState: "Gathering", messageType: "PaymentCharged", messageId: "a1", payload: marker, error: "boom"),
        ]);

        Assert.False(plan.Retryable);
        Assert.Equal(SagaRetryPlanner.BodyTooLargeReason, plan.Reason);
        Assert.Equal(SagaRetryFailureKind.StepFailed, plan.FailureKind);
        Assert.Equal(new SagaRetryStep(1, "PaymentCharged", "a1", "Gathering"), plan.Step);
    }

    [Theory]
    [InlineData(SagaStatus.Running)]
    [InlineData(SagaStatus.Completed)]
    [InlineData(SagaStatus.Compensating)]
    public void Plan_SagaThatIsNotFailedOrTimedOut_IsNotRetryableAndNamesNoFailure(SagaStatus status)
    {
        var plan = SagaRetryPlanner.Plan(Summary("Gathering", status),
        [
            Entry(1, SagaEntryType.StepFailed, fromState: "Gathering", messageType: "PaymentCharged", messageId: "a1", payload: "{}"),
        ]);

        Assert.Equal(new SagaRetryPlan(false, SagaRetryPlanner.NotFailedReason, null, null, null), plan);
    }

    [Fact]
    public void Plan_FailedSagaWithNoFailureEntry_IsNotRetryableAndNamesNoFailure()
    {
        var plan = SagaRetryPlanner.Plan(Summary("Failed", SagaStatus.Failed),
        [
            Entry(1, SagaEntryType.SagaStarted, toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payload: "{}"),
            Entry(2, SagaEntryType.StatePersisted, messageType: "OrderSubmitted", messageId: "m0", payload: "{}"),
            Entry(3, SagaEntryType.DeliveryExhausted, error: "Deferred publish failed"),
        ]);

        Assert.Equal(new SagaRetryPlan(false, SagaRetryPlanner.NoStepReason, null, null, null), plan);
    }

    private static SagaSummary Summary(string currentState, SagaStatus status) =>
        new(CorrelationId, "OrderSaga", SagaKind.Orchestrated, currentState, status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 3, null, null);

    private static SagaLogEntry Entry(long sequenceNumber, SagaEntryType type, string? fromState = null, string? toState = null,
        string? messageType = null, string? messageId = null, string? payload = null, string? error = null, string? causationId = null) =>
        SagaLogEntry.Create(CorrelationId, "OrderSaga", type, fromState, toState, messageType, messageId, payload, error, causationId: causationId)
            with { SequenceNumber = sequenceNumber };
}
