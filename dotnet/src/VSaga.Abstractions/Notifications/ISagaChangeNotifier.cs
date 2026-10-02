using VSaga.Abstractions.Persistence;

namespace VSaga.Abstractions.Notifications;

/// <summary>
/// Fired by the orchestrator on every persisted saga change. Core's default implementation is a
/// no-op; VSaga.Dashboard.Api registers a SignalR-backed implementation. Keeps Core free of any
/// SignalR/web dependency.
/// </summary>
public interface ISagaChangeNotifier
{
    Task SagaUpdatedAsync(SagaSummary summary, CancellationToken cancellationToken = default);

    /// <summary>
    /// The (sagaType, correlationId) pair names the instance whose timeline grew — subscribers group per instance, not per correlation id.
    /// <paramref name="entry"/> carries the sequence number the event log assigned when it stored the entry.
    /// </summary>
    Task TimelineEntryAddedAsync(string sagaType, Guid correlationId, SagaLogEntry entry, CancellationToken cancellationToken = default);
}

public sealed class NullSagaChangeNotifier : ISagaChangeNotifier
{
    public static readonly NullSagaChangeNotifier Instance = new();

    public Task SagaUpdatedAsync(SagaSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task TimelineEntryAddedAsync(string sagaType, Guid correlationId, SagaLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
