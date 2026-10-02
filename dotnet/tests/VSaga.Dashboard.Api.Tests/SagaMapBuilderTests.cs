using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Direct unit tests for <see cref="SagaMapBuilder"/>: each test calls <see cref="SagaMapBuilder.Build"/>
/// on a hand-built timeline and pins one per-entry rule, so a builder change breaks a named rule instead
/// of drifting the <c>/map</c> response (SagaEndpointsTests, CallHttpSagaMapTests).
/// </summary>
public sealed class SagaMapBuilderTests
{
    private const string SagaType = "OrderSaga";
    private static readonly Guid CorrelationId = Guid.Parse("6f1b0c52-3c1e-4d7a-9a51-0d2c4b8e7f10");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private static SagaSummary Summary(SagaStatus status = SagaStatus.Running) =>
        new(CorrelationId, SagaType, SagaKind.Orchestrated, "Current", status, Start, Start, 1, null, null);

    private static SagaLogEntry Entry(
        long sequenceNumber,
        SagaEntryType entryType,
        string? messageType = null,
        string? messageId = null,
        string? source = null,
        string? destination = null,
        string? causationId = null,
        string? error = null) =>
        new(sequenceNumber, CorrelationId, SagaType, entryType, null, null, messageType, messageId, null, error,
            null, null, Start.AddSeconds(sequenceNumber), source, destination, causationId);

    private static ServiceTopologyEntry Consumer(string service, string messageType, string? queue = null) =>
        new(service, messageType, queue ?? $"{service}-queue", Start);

    private static SagaMap Build(IReadOnlyList<SagaLogEntry> timeline, SagaStatus status = SagaStatus.Running, IReadOnlyList<ServiceTopologyEntry>? topology = null) =>
        SagaMapBuilder.Build(Summary(status), timeline, topology ?? []);

    private static SagaMapNode Node(SagaMap map, string id) =>
        Assert.Single(map.Nodes, n => string.Equals(n.Id, id, StringComparison.Ordinal));

    private static SagaMapEdge Edge(SagaMap map, string id) =>
        Assert.Single(map.Edges, e => string.Equals(e.Id, id, StringComparison.Ordinal));

    private static SagaMapEvent Event(SagaMap map, long sequenceNumber) =>
        Assert.Single(map.Events, e => e.SequenceNumber == sequenceNumber);

    [Fact]
    public void EmptyTimeline_ProducesOnlyTheOrchestratorNode()
    {
        var map = Build([]);

        var orchestrator = Assert.Single(map.Nodes);
        Assert.Equal(SagaType, orchestrator.Id);
        Assert.Equal(SagaType, orchestrator.DisplayName);
        Assert.Equal(SagaMapNodeKind.Orchestrator, orchestrator.Kind);
        Assert.Equal("ok", orchestrator.Status);
        Assert.Equal(0, orchestrator.MessagesIn);
        Assert.Equal(0, orchestrator.MessagesOut);
        Assert.Empty(map.Edges);
        Assert.Empty(map.Events);
        Assert.Null(map.FailureEventIndex);
    }

    [Fact]
    public void EveryEntry_BecomesOneEvent_CarryingItsSequenceNumberAndType()
    {
        var map = Build(
        [
            Entry(3, SagaEntryType.SagaStarted, "OrderPlaced", "m-start", source: "checkout"),
            Entry(7, SagaEntryType.StateEntered),
            Entry(8, SagaEntryType.MessagePublished, "ReserveStock", "m-out"),
            Entry(12, SagaEntryType.StepSucceeded, "OrderPlaced", "m-start"),
            Entry(20, SagaEntryType.TimeoutScheduled),
        ]);

        Assert.Equal([3L, 7L, 8L, 12L, 20L], map.Events.Select(e => e.SequenceNumber));
        Assert.Equal(
            [SagaEntryType.SagaStarted, SagaEntryType.StateEntered, SagaEntryType.MessagePublished, SagaEntryType.StepSucceeded, SagaEntryType.TimeoutScheduled],
            map.Events.Select(e => e.EntryType));
        Assert.Equal(Start.AddSeconds(8), Event(map, 8).OccurredAtUtc);
        Assert.Equal("ReserveStock", Event(map, 8).MessageType);
    }

    [Fact]
    public void Entries_AreOrderedBySequenceNumber_RegardlessOfInputOrder()
    {
        var map = Build(
        [
            Entry(5, SagaEntryType.SagaCompleted),
            Entry(1, SagaEntryType.SagaStarted, "OrderPlaced", "m-start", source: "checkout"),
            Entry(3, SagaEntryType.MessageReceived, "StockReserved", "m-reply", source: "inventory", causationId: "m-out"),
            Entry(2, SagaEntryType.MessagePublished, "ReserveStock", "m-out"),
            Entry(4, SagaEntryType.StepSucceeded, "StockReserved", "m-reply"),
        ]);

        Assert.Equal([1L, 2L, 3L, 4L, 5L], map.Events.Select(e => e.SequenceNumber));
        // Edges follow the same order, and the request (seq 2) still stitches to its reply (seq 3) even
        // though the reply came first in the input.
        Assert.Equal(["e1", "e2-inventory", "e3"], map.Edges.Select(e => e.Id), StringComparer.Ordinal);
        Assert.False(Edge(map, "e2-inventory").Unanswered);
    }

    [Fact]
    public void InboundEntryFromAnotherService_IsAnEdgeIntoTheOrchestrator()
    {
        var map = Build(
        [
            Entry(1, SagaEntryType.SagaStarted, "OrderPlaced", "m-start", source: "checkout"),
            Entry(2, SagaEntryType.MessageReceived, "PaymentCaptured", "m-pay", source: "payments"),
        ]);

        var started = Edge(map, "e1");
        Assert.Equal("checkout", started.FromNodeId);
        Assert.Equal(SagaType, started.ToNodeId);
        Assert.Equal("OrderPlaced", started.MessageType);
        Assert.Equal("m-start", started.MessageId);
        Assert.False(started.Unanswered);
        Assert.False(started.Failed);
        Assert.False(started.IsCompensation);

        var startedEvent = Event(map, 1);
        Assert.Equal("e1", startedEvent.EdgeId);
        Assert.Null(startedEvent.NodeId);

        // The SagaStarted sender is the initiator; any other sender is a participant.
        Assert.Equal(SagaMapNodeKind.Initiator, Node(map, "checkout").Kind);
        Assert.Equal(SagaMapNodeKind.Participant, Node(map, "payments").Kind);
        Assert.Equal(1, Node(map, "checkout").MessagesOut);
        Assert.Equal(2, Node(map, SagaType).MessagesIn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SagaType)]
    public void InboundEntryWithoutAnotherSender_IsAnOrchestratorNodeEvent(string? source)
    {
        var map = Build([Entry(1, SagaEntryType.MessageReceived, "Tick", "m-tick", source: source)]);

        Assert.Empty(map.Edges);
        var tick = Event(map, 1);
        Assert.Null(tick.EdgeId);
        Assert.Equal(SagaType, tick.NodeId);
        Assert.Single(map.Nodes);
    }

    [Fact]
    public void OutboundEntryAnsweredByALaterReply_IsStitchedToTheRepliersNode()
    {
        // inventory is subscribed to ReserveStock too: the stitch wins over the topology, so the request
        // draws exactly one edge, to the service that actually answered.
        var map = Build(
            [
                Entry(1, SagaEntryType.MessagePublished, "ReserveStock", "m-out"),
                Entry(2, SagaEntryType.MessageReceived, "StockReserved", "m-reply", source: "warehouse", causationId: "m-out"),
            ],
            topology: [Consumer("inventory", "ReserveStock")]);

        var request = Assert.Single(map.Edges, e => string.Equals(e.MessageId, "m-out", StringComparison.Ordinal));
        Assert.Equal("e1-warehouse", request.Id);
        Assert.Equal(SagaType, request.FromNodeId);
        Assert.Equal("warehouse", request.ToNodeId);
        Assert.False(request.Unanswered);
        Assert.Equal("e1-warehouse", Event(map, 1).EdgeId);
        Assert.Null(Event(map, 1).NodeId);

        var reply = Edge(map, "e2");
        Assert.Equal("warehouse", reply.FromNodeId);
        Assert.Equal(SagaType, reply.ToNodeId);

        Assert.DoesNotContain(map.Nodes, n => string.Equals(n.Id, "inventory", StringComparison.Ordinal));
        Assert.Equal("ok", Node(map, "warehouse").Status);
        Assert.Equal(1, Node(map, "warehouse").MessagesIn);
        Assert.Equal(1, Node(map, "warehouse").MessagesOut);
    }

    [Fact]
    public void UnansweredPublish_ResolvesItsDestinationFromTheTopologyByMessageType()
    {
        var map = Build(
            [Entry(1, SagaEntryType.MessagePublished, "ReserveStock", "m-out")],
            topology: [Consumer("inventory", "ReserveStock"), Consumer("billing", "ChargeCard")]);

        var edge = Assert.Single(map.Edges);
        Assert.Equal("e1-inventory", edge.Id);
        Assert.Equal("inventory", edge.ToNodeId);
        Assert.True(edge.Unanswered);

        var inventory = Node(map, "inventory");
        Assert.Equal(SagaMapNodeKind.Participant, inventory.Kind);
        Assert.Equal("unanswered", inventory.Status);
    }

    [Fact]
    public void UnansweredSend_ResolvesItsDestinationQueueFromTheTopology()
    {
        // A send stores its queue name as DestinationService; the queue, not the message type, decides.
        var map = Build(
            [Entry(1, SagaEntryType.MessageSent, "ChargeCard", "m-out", destination: "billing-priority")],
            topology: [Consumer("billing", "ChargeCard"), Consumer("payments", "Other", queue: "billing-priority")]);

        var edge = Assert.Single(map.Edges);
        Assert.Equal("payments", edge.ToNodeId);
        Assert.True(edge.Unanswered);
    }

    [Theory]
    [InlineData(SagaEntryType.MessagePublished, "ShipOrder", null, "unresolved:ShipOrder")]
    [InlineData(SagaEntryType.MessageSent, "ShipOrder", "shipping-queue", "unresolved:shipping-queue")]
    [InlineData(SagaEntryType.MessagePublished, null, null, "unresolved:unknown")]
    public void UnansweredOutboundTheTopologyDoesNotKnow_GoesToAnUnresolvedPlaceholder(
        SagaEntryType entryType, string? messageType, string? destination, string expectedNodeId)
    {
        var map = Build(
            [Entry(1, entryType, messageType, "m-out", destination: destination)],
            topology: [Consumer("inventory", "ReserveStock")]);

        var edge = Assert.Single(map.Edges);
        Assert.Equal(expectedNodeId, edge.ToNodeId);
        Assert.Equal($"e1-{expectedNodeId}", edge.Id);
        Assert.True(edge.Unanswered);

        var placeholder = Node(map, expectedNodeId);
        Assert.Equal("?", placeholder.DisplayName);
        Assert.Equal(SagaMapNodeKind.Unresolved, placeholder.Kind);
        Assert.Equal("unanswered", placeholder.Status);
    }

    [Fact]
    public void FanOutToSeveralConsumers_DrawsOneEdgeEach_AndTheEventPointsAtTheFirst()
    {
        var map = Build(
            [Entry(1, SagaEntryType.MessagePublished, "OrderPlaced", "m-out")],
            topology:
            [
                Consumer("email", "OrderPlaced"),
                Consumer("audit", "OrderPlaced"),
                Consumer("email", "OrderPlaced", queue: "email-replay"),
            ]);

        // One edge per distinct consumer service, in topology order; the duplicate binding adds none.
        Assert.Equal(["e1-email", "e1-audit"], map.Edges.Select(e => e.Id), StringComparer.Ordinal);
        Assert.All(map.Edges, e => Assert.True(e.Unanswered));
        Assert.Equal("e1-email", Event(map, 1).EdgeId);
        Assert.Single(map.Events);
        Assert.Equal(2, Node(map, SagaType).MessagesOut);
    }

    [Fact]
    public void EdgesAfterCompensationStarted_AreMarkedAsCompensation()
    {
        var map = Build(
        [
            Entry(1, SagaEntryType.SagaStarted, "OrderPlaced", "m-start", source: "checkout"),
            Entry(2, SagaEntryType.MessagePublished, "ReserveStock", "m-reserve"),
            Entry(3, SagaEntryType.CompensationStarted),
            Entry(4, SagaEntryType.MessagePublished, "ReleaseStock", "m-release"),
            Entry(5, SagaEntryType.MessageReceived, "StockReleased", "m-released", source: "inventory", causationId: "m-release"),
        ]);

        Assert.False(Edge(map, "e1").IsCompensation);
        Assert.False(Edge(map, "e2-unresolved:ReserveStock").IsCompensation);
        Assert.True(Edge(map, "e4-inventory").IsCompensation);
        Assert.True(Edge(map, "e5").IsCompensation);

        // CompensationStarted itself is a plain event: no edge, no node.
        var compensation = Event(map, 3);
        Assert.Null(compensation.EdgeId);
        Assert.Null(compensation.NodeId);
    }

    [Theory]
    [InlineData(SagaStatus.Failed, "failed")]
    [InlineData(SagaStatus.TimedOut, "failed")]
    [InlineData(SagaStatus.Running, "ok")]
    [InlineData(SagaStatus.Completed, "ok")]
    [InlineData(SagaStatus.Compensating, "ok")]
    [InlineData(SagaStatus.Compensated, "ok")]
    [InlineData(SagaStatus.Cancelled, "ok")]
    public void OrchestratorStatus_FollowsTheSagaStatus(SagaStatus status, string expected)
    {
        var map = Build([Entry(1, SagaEntryType.SagaStarted, "OrderPlaced", "m-start", source: "checkout")], status);

        Assert.Equal(expected, Node(map, SagaType).Status);
        Assert.Equal("ok", Node(map, "checkout").Status);
    }

    [Theory]
    [InlineData(SagaEntryType.StepFailed)]
    [InlineData(SagaEntryType.TimeoutFired)]
    [InlineData(SagaEntryType.DeliveryExhausted)]
    public void FailureEventIndex_PointsAtTheFirstFailureEntry(SagaEntryType failure)
    {
        var map = Build(
        [
            Entry(1, SagaEntryType.SagaStarted, "OrderPlaced", "m-start", source: "checkout"),
            Entry(2, SagaEntryType.StateEntered),
            Entry(3, failure, "OrderPlaced", "m-start", error: "boom"),
            Entry(4, SagaEntryType.StepFailed, "OrderPlaced", "m-start", error: "again"),
            Entry(5, SagaEntryType.TimeoutFired),
        ]);

        Assert.Equal(2, map.FailureEventIndex);
        var failed = map.Events[map.FailureEventIndex!.Value];
        Assert.Equal(3, failed.SequenceNumber);
        Assert.Equal(failure, failed.EntryType);
        // Failure entries light the orchestrator node; the error text rides on the event.
        Assert.Equal(SagaType, failed.NodeId);
        Assert.Null(failed.EdgeId);
        Assert.Equal("boom", failed.ErrorMessage);
    }

    [Fact]
    public void FailureEventIndex_IsNull_WithoutAFailureEntry()
    {
        var map = Build(
            [
                Entry(1, SagaEntryType.SagaStarted, "OrderPlaced", "m-start", source: "checkout"),
                Entry(2, SagaEntryType.SagaCompleted),
            ],
            SagaStatus.Failed);

        Assert.Null(map.FailureEventIndex);
        Assert.Null(Event(map, 2).NodeId);
    }

    [Fact]
    public void InboundEdgeOfAStepThatFailed_IsMarkedFailed()
    {
        var map = Build(
            [
                Entry(1, SagaEntryType.SagaStarted, "OrderPlaced", "m-start", source: "checkout"),
                Entry(2, SagaEntryType.MessageReceived, "PaymentCaptured", "m-pay", source: "payments"),
                Entry(3, SagaEntryType.StepFailed, "PaymentCaptured", "m-pay", error: "boom"),
            ],
            SagaStatus.Failed);

        Assert.False(Edge(map, "e1").Failed);
        Assert.True(Edge(map, "e2").Failed);
        Assert.Equal("failed", Node(map, "payments").Status);
        Assert.Equal("ok", Node(map, "checkout").Status);
    }

    [Fact]
    public void BusinessFailure_MarksTheLastInboundEdgeBeforeSagaCompletedAsFailed()
    {
        // A declined payment is a normal transition to a failed final state: no StepFailed is logged, so
        // the failing hop is the last inbound message before SagaCompleted.
        var timeline = new[]
        {
            Entry(1, SagaEntryType.SagaStarted, "OrderPlaced", "m-start", source: "checkout"),
            Entry(2, SagaEntryType.MessageReceived, "PaymentDeclined", "m-declined", source: "payments"),
            Entry(3, SagaEntryType.StepSucceeded, "PaymentDeclined", "m-declined"),
            Entry(4, SagaEntryType.SagaCompleted),
            Entry(5, SagaEntryType.MessageReceived, "LateNotice", "m-late", source: "notifications"),
        };

        var failedMap = Build(timeline, SagaStatus.Failed);
        Assert.False(Edge(failedMap, "e1").Failed);
        Assert.True(Edge(failedMap, "e2").Failed);
        Assert.False(Edge(failedMap, "e5").Failed);
        Assert.Null(failedMap.FailureEventIndex);

        // The same timeline on a saga that completed normally marks nothing.
        var completedMap = Build(timeline, SagaStatus.Completed);
        Assert.DoesNotContain(completedMap.Edges, e => e.Failed);
    }
}
