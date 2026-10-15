using VSaga.Abstractions.Notifications;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Core.Runtime;

/// <summary>
/// The definition of "stuck" (docs/design/stuck-saga-detection.md §3) as pure functions, so every boundary of it
/// is testable without a store or a clock.
/// </summary>
internal static class SagaStuckClassifier
{
    /// <summary>
    /// The shortest idle time any instance of the saga type can be stuck at: the smaller of its idle threshold and
    /// its shortest state timeout plus the grace. The sweep asks the store only for instances idle for longer than
    /// this, then applies each one's own threshold.
    /// </summary>
    public static TimeSpan Floor(string sagaType, TimeSpan? shortestTimeout, SagaStuckOptions options)
    {
        var idle = options.IdleThresholdFor(sagaType);
        return shortestTimeout is { } timeout && timeout + options.TimeoutGrace < idle
            ? timeout + options.TimeoutGrace
            : idle;
    }

    /// <summary>
    /// <paramref name="saga"/> as a <see cref="StuckSaga"/> when it is idle for strictly longer than its threshold at
    /// <paramref name="now"/>, otherwise null. <paramref name="stateTimeout"/> is the timeout its definition declares
    /// for <see cref="SagaSummary.CurrentState"/>, or null. A saga that is neither <c>Running</c> nor
    /// <c>Compensating</c> is never stuck. A state's timeout longer than the idle threshold is not flagged early:
    /// its threshold is the timeout plus the grace, and the idle threshold applies only where no timeout does.
    /// </summary>
    public static StuckSaga? Classify(SagaSummary saga, TimeSpan? stateTimeout, SagaStuckOptions options, DateTimeOffset now)
    {
        if (saga.Status is not (SagaStatus.Running or SagaStatus.Compensating))
            return null;

        var (cause, threshold) = (saga.Status, stateTimeout) switch
        {
            (SagaStatus.Compensating, _) => (StuckCause.CompensationIdle, options.IdleThresholdFor(saga.SagaType)),
            (_, { } timeout) => (StuckCause.TimeoutOverdue, timeout + options.TimeoutGrace),
            _ => (StuckCause.IdleWithoutTimeout, options.IdleThresholdFor(saga.SagaType)),
        };

        var idleFor = now - saga.UpdatedAtUtc;
        return idleFor > threshold
            ? new StuckSaga(saga.SagaType, saga.CorrelationId, saga.CurrentState, saga.Status, cause, idleFor, threshold, saga.UpdatedAtUtc, saga.Version)
            : null;
    }
}
