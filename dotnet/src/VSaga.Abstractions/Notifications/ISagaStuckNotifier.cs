
namespace VSaga.Abstractions.Notifications;

/// <summary>
/// Told by the stuck-saga sweep when a saga it hosts goes idle past its threshold and when it stops being idle
/// (docs/design/stuck-saga-detection.md §5.3). vSaga ships no implementation beyond a no-op: a webhook, a pager or a
/// chat message is the host's own, registered in place of <see cref="NullSagaStuckNotifier"/>.
/// <para>
/// Delivery is at-least-once. A call that throws is logged and the sweep goes on; the saga is offered again at the
/// next sweep, so a notifier that was down for a while is told everything that is still true when it returns. With
/// several replicas of a service each sweeps, so each notifies; a receiver de-duplicates on
/// <see cref="StuckSaga.SagaType"/>, <see cref="StuckSaga.CorrelationId"/> and <see cref="StuckSaga.Version"/>. A
/// restart forgets what was announced, so a saga still stuck is announced again.
/// </para>
/// </summary>
public interface ISagaStuckNotifier
{
    /// <summary>A saga went idle past its threshold. Called once for each saga until it recovers, however many sweeps see it stuck.</summary>
    Task SagaStuckAsync(StuckSaga saga, CancellationToken cancellationToken = default);

    /// <summary>
    /// A saga that was announced stuck no longer is: it moved on, finished, or was deleted. Not called for a saga
    /// whose announcement never got through.
    /// </summary>
    Task SagaRecoveredAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default);
}

public sealed class NullSagaStuckNotifier : ISagaStuckNotifier
{
    public static readonly NullSagaStuckNotifier Instance = new();

    public Task SagaStuckAsync(StuckSaga saga, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SagaRecoveredAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
