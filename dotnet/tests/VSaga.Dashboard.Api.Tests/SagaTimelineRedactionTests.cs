using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Dashboard.Api.Endpoints;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The redaction seam behind <c>/timeline</c>, <c>/map</c> and the <c>TimelineEntryAdded</c> push:
/// payloads and error messages are data and leave together, entries never do.
/// </summary>
public sealed class SagaTimelineRedactionTests
{
    private const string SagaType = "OrderSaga";
    private static readonly Guid CorrelationId = Guid.Parse("0b6d3f7e-21a4-4c59-8e0f-5d7a9c1b2e34");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private static SagaLogEntry Entry(
        long sequenceNumber,
        SagaEntryType entryType,
        string? messageType = null,
        string? messageId = null,
        string? payload = null,
        string? error = null,
        string? source = null,
        string? destination = null,
        string? causationId = null) =>
        new(sequenceNumber, CorrelationId, SagaType, entryType, "From", "To", messageType, messageId, payload, error,
            "trace", "span", Start.AddSeconds(sequenceNumber), source, destination, causationId);

    /// <summary>A started saga that received a message, persisted its state, then failed on the next message.</summary>
    private static IReadOnlyList<SagaLogEntry> FailedTimeline() =>
    [
        Entry(1, SagaEntryType.SagaStarted, "OrderPlaced", "m-1", payload: """{"OrderId":"order-42"}""", source: "Shop", destination: SagaType),
        Entry(2, SagaEntryType.MessageSent, "ChargePayment", "m-2", payload: """{"Amount":99.5}""", source: SagaType, destination: "Payments", causationId: "m-1"),
        Entry(3, SagaEntryType.StatePersisted, "OrderPlaced", "m-1", payload: """{"CurrentState":"AwaitingPayment"}"""),
        Entry(4, SagaEntryType.StepFailed, "PaymentDeclined", "m-3", payload: """{"Card":"4111"}""", error: "Card 4111 declined for customer alice", source: "Payments", destination: SagaType, causationId: "m-2"),
        Entry(5, SagaEntryType.SagaCompleted),
    ];

    [Fact]
    public void Apply_WithData_ReturnsTheTimelineItself()
    {
        var timeline = FailedTimeline();

        Assert.Same(timeline, SagaTimelineRedaction.Apply(timeline, includeData: true));
    }

    [Fact]
    public void Apply_WithoutData_NullsPayloadsAndErrorMessagesAndKeepsEveryEntry()
    {
        var timeline = FailedTimeline();

        var redacted = SagaTimelineRedaction.Apply(timeline, includeData: false);

        // Nothing is dropped or reordered: the SPA's step fold and the map join key on sequence numbers.
        Assert.Equal(timeline.Select(e => e.SequenceNumber), redacted.Select(e => e.SequenceNumber));
        Assert.Equal(timeline.Select(e => e.EntryType), redacted.Select(e => e.EntryType));
        Assert.All(redacted, e => Assert.Null(e.PayloadJson));
        Assert.All(redacted, e => Assert.Null(e.ErrorMessage));

        // Everything that is not data survives, so a redacted timeline still shows where the saga failed.
        Assert.Equal(timeline.Select(e => e with { PayloadJson = null, ErrorMessage = null }), redacted);
    }

    [Fact]
    public void Apply_WithoutData_LeavesTheSourceTimelineUntouched()
    {
        var timeline = FailedTimeline();

        _ = SagaTimelineRedaction.Apply(timeline, includeData: false);

        Assert.Equal("Card 4111 declined for customer alice", timeline[3].ErrorMessage);
        Assert.Equal("""{"Card":"4111"}""", timeline[3].PayloadJson);
    }

    [Fact]
    public void Apply_WithoutData_NullsAnErrorMessageThatHasNoPayload()
    {
        var timeline = new[] { Entry(1, SagaEntryType.CompensationStepFailed, error: "Refund for order-42 rejected") };

        var entry = Assert.Single(SagaTimelineRedaction.Apply(timeline, includeData: false));

        Assert.Null(entry.ErrorMessage);
        Assert.Equal(SagaEntryType.CompensationStepFailed, entry.EntryType);
    }

    [Fact]
    public void WithoutData_ReturnsTheSameInstanceWhenThereIsNothingToStrip()
    {
        var entry = Entry(7, SagaEntryType.TimeoutScheduled, "PaymentTimeout");

        Assert.Same(entry, SagaTimelineRedaction.WithoutData(entry));
    }

    [Fact]
    public void ApplyToMap_WithData_ReturnsTheMapItself()
    {
        var map = BuildMap();

        Assert.Same(map, SagaTimelineRedaction.ApplyToMap(map, includeData: true));
    }

    [Fact]
    public void ApplyToMap_WithoutData_NullsEventErrorMessagesAndKeepsTheShape()
    {
        var map = BuildMap();
        Assert.Contains(map.Events, e => e.ErrorMessage is not null);

        var redacted = SagaTimelineRedaction.ApplyToMap(map, includeData: false);

        Assert.All(redacted.Events, e => Assert.Null(e.ErrorMessage));
        Assert.Equal(map.Events.Select(e => e with { ErrorMessage = null }), redacted.Events);
        Assert.Same(map.Summary, redacted.Summary);
        Assert.Same(map.Nodes, redacted.Nodes);
        Assert.Same(map.Edges, redacted.Edges);
        Assert.Equal(map.FailureEventIndex, redacted.FailureEventIndex);
        Assert.Contains(redacted.Edges, e => e.Failed);
    }

    private static SagaMap BuildMap() =>
        SagaMapBuilder.Build(
            new SagaSummary(CorrelationId, SagaType, SagaKind.Orchestrated, "AwaitingPayment", SagaStatus.Failed, Start, Start, 3, null, null),
            FailedTimeline(),
            []);
}
