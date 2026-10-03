using VSaga.Abstractions.Notifications;
using VSaga.Abstractions.Persistence;
using Microsoft.AspNetCore.SignalR;
using VSaga.Dashboard.Api.Endpoints;

namespace VSaga.Dashboard.Api.Hubs;

/// <summary>The only place SignalR meets the saga engine — Core depends on ISagaChangeNotifier, never on this.</summary>
public sealed class SignalRSagaChangeNotifier(IHubContext<SagaHub, ISagaHubClient> hub) : ISagaChangeNotifier
{
    public Task SagaUpdatedAsync(SagaSummary summary, CancellationToken cancellationToken = default) =>
        SagaHub.PushSagaUpdatedAsync(hub, summary);

    /// <summary>
    /// Pushes the entry without its payload and error message, for everyone: a hub group is joined per
    /// saga, not per permission, so a push cannot be redacted per caller. The detail page refetches the
    /// timeline, which goes through <see cref="SagaTimelineRedaction"/> with the caller's own answer.
    /// </summary>
    public Task TimelineEntryAddedAsync(string sagaType, Guid correlationId, SagaLogEntry entry, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(SagaHub.GroupForSaga(sagaType, correlationId)).TimelineEntryAdded(sagaType, correlationId, SagaTimelineRedaction.WithoutData(entry));
}
