using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Dashboard.Api.Hubs;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The in-process notifier: what the orchestrator's change callbacks turn into on the wire. Fires only
/// when a saga engine runs in the dashboard's own process; the cross-process case is
/// <see cref="SagaChangePollingServiceTests"/>.
/// </summary>
public sealed class SignalRSagaChangeNotifierTests
{
    private static SagaSummary NewSummary(string sagaType, Guid correlationId, SagaKind kind = SagaKind.Orchestrated) =>
        new(correlationId, sagaType, kind, "AwaitingPayment", SagaStatus.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 2,
            ParentSagaType: null, ParentCorrelationId: null);

    [Fact]
    public async Task SagaUpdated_GoesToTheListGroup_ItsTypesListGroup_AndTheInstanceGroup()
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRSagaChangeNotifier(context);
        var correlationId = Guid.NewGuid();

        await notifier.SagaUpdatedAsync(NewSummary("OrderSaga", correlationId));

        var groups = context.Recorder.SagaUpdates.Select(c => c.Group).ToList();

        // The list view and an open detail page are separate subscriptions; a change has to reach both, and
        // a list view scoped to named saga types listens on its types' own list groups.
        Assert.Contains(SagaHub.ListGroup, groups, StringComparer.Ordinal);
        Assert.Contains(SagaHub.ListGroupForType("OrderSaga"), groups, StringComparer.Ordinal);
        Assert.Contains(SagaHub.GroupForSaga("OrderSaga", correlationId), groups, StringComparer.Ordinal);
        Assert.Equal(3, groups.Count);
    }

    /// <summary>
    /// The instance group is derived from the summary's own SagaType, not from correlation id alone —
    /// otherwise an update for one saga would be delivered to a detail page watching a different saga
    /// type that happens to share the id.
    /// </summary>
    [Fact]
    public async Task SagaUpdated_TargetsTheInstanceGroupOfItsOwnSagaType()
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRSagaChangeNotifier(context);
        var correlationId = Guid.NewGuid();

        await notifier.SagaUpdatedAsync(NewSummary("PostShipmentChoreography", correlationId, SagaKind.Choreographed));

        var groups = context.Recorder.SagaUpdates.Select(c => c.Group).ToList();
        var instanceGroups = groups
            .Where(g => !string.Equals(g, SagaHub.ListGroup, StringComparison.Ordinal)
                && !g.StartsWith(SagaHub.TypeListGroupPrefix, StringComparison.Ordinal))
            .ToList();

        Assert.Equal(SagaHub.GroupForSaga("PostShipmentChoreography", correlationId), Assert.Single(instanceGroups));
        Assert.DoesNotContain(SagaHub.GroupForSaga("OrderSaga", correlationId), instanceGroups, StringComparer.Ordinal);
        Assert.Contains(SagaHub.ListGroupForType("PostShipmentChoreography"), groups, StringComparer.Ordinal);
        Assert.DoesNotContain(SagaHub.ListGroupForType("OrderSaga"), groups, StringComparer.Ordinal);
    }

    [Fact]
    public async Task TimelineEntryAdded_GoesOnlyToTheInstanceGroupAndCarriesTheSagaType()
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRSagaChangeNotifier(context);
        var correlationId = Guid.NewGuid();
        var entry = SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessageReceived, messageType: "PaymentCharged");

        await notifier.TimelineEntryAddedAsync("OrderSaga", correlationId, entry);

        var call = Assert.Single(context.Recorder.TimelineEntries);
        Assert.Equal(SagaHub.GroupForSaga("OrderSaga", correlationId), call.Group);

        // The saga type travels as a payload argument too: the client filters on it before appending,
        // so dropping it here would let a sibling saga's entries render on the wrong detail page.
        Assert.Equal("OrderSaga", call.SagaType);
        Assert.Equal(correlationId, call.CorrelationId);
        Assert.Same(entry, call.Entry);

        // Timeline entries are per-instance detail, deliberately not broadcast to the list view.
        Assert.Empty(context.Recorder.SagaUpdates);
        Assert.DoesNotContain(context.Recorder.TimelineEntries, c => string.Equals(c.Group, SagaHub.ListGroup, StringComparison.Ordinal));
    }

    /// <summary>
    /// A hub group is joined per saga, not per permission, so a push cannot be redacted per caller: every
    /// pushed entry leaves its payload and error message behind, and the detail page's refetch (which goes
    /// through the timeline's redaction seam) is where a caller allowed to see them gets them.
    /// </summary>
    [Fact]
    public async Task TimelineEntryAdded_PushesTheEntryWithoutItsPayloadOrErrorMessage()
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRSagaChangeNotifier(context);
        var correlationId = Guid.NewGuid();
        var entry = SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StepFailed,
            fromState: "AwaitingPayment", messageType: "PaymentDeclined", messageId: "m-3",
            payloadJson: """{"Card":"4111"}""", errorMessage: "Card 4111 declined for customer alice",
            causationId: "m-2") with { SequenceNumber = 4 };

        await notifier.TimelineEntryAddedAsync("OrderSaga", correlationId, entry);

        var pushed = Assert.Single(context.Recorder.TimelineEntries).Entry;
        Assert.Null(pushed.PayloadJson);
        Assert.Null(pushed.ErrorMessage);

        // Only the data goes: the row still says which step failed, and where it sits in the timeline.
        Assert.Equal(entry with { PayloadJson = null, ErrorMessage = null }, pushed);
    }

    /// <summary>
    /// Two saga types sharing a correlation id must land in two different groups — the property the whole
    /// composite-key change exists to preserve, asserted here at the notification layer.
    /// </summary>
    [Fact]
    public async Task TwoSagaTypesSharingACorrelationIdNotifySeparateGroups()
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRSagaChangeNotifier(context);
        var correlationId = Guid.NewGuid();

        await notifier.TimelineEntryAddedAsync("OrderSaga", correlationId,
            SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessageReceived));
        await notifier.TimelineEntryAddedAsync("PostShipmentChoreography", correlationId,
            SagaLogEntry.Create(correlationId, "PostShipmentChoreography", SagaEntryType.MessageReceived));

        var groups = context.Recorder.TimelineEntries.Select(c => c.Group).ToList();

        Assert.Equal(2, groups.Distinct(StringComparer.Ordinal).Count());
    }
}
