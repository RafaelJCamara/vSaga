using VSaga.Abstractions.Sagas;

namespace VSaga.Abstractions.Notifications;

/// <summary>
/// Why a saga that has gone quiet is counted as stuck. The causes are exclusive and each one names which
/// part of the engine should have moved the saga on and did not (docs/design/stuck-saga-detection.md §3).
/// </summary>
public enum StuckCause
{
    /// <summary>
    /// The saga is <c>Running</c> in a state that declares a timeout, and has been idle for longer than that
    /// timeout plus the grace: the timeout that should have resolved it was lost, failed, or never dispatched.
    /// </summary>
    TimeoutOverdue,

    /// <summary>
    /// The saga is <c>Running</c> in a state that declares no timeout, and has been idle for longer than the
    /// idle threshold. Sometimes by design (a human approval); the threshold is what separates the two.
    /// </summary>
    IdleWithoutTimeout,

    /// <summary>The saga is <c>Compensating</c> and has been idle for longer than the idle threshold.</summary>
    CompensationIdle,
}

/// <summary>
/// One saga the stuck-saga sweep found idle past its threshold. <see cref="Version"/> with
/// <see cref="SagaType"/> and <see cref="CorrelationId"/> identifies the episode: a saga that moves on and then
/// goes idle again has a new version, so a receiver that de-duplicates on the three does not mistake the second
/// episode for the first.
/// </summary>
/// <param name="SagaType">The saga type's name.</param>
/// <param name="CorrelationId">The instance.</param>
/// <param name="CurrentState">The state it has been idle in.</param>
/// <param name="Status"><c>Running</c> or <c>Compensating</c>.</param>
/// <param name="Cause">Why it counts as stuck.</param>
/// <param name="IdleFor">How long since its last update, at the sweep's clock.</param>
/// <param name="Threshold">The idle time it crossed.</param>
/// <param name="UpdatedAtUtc">Its last update.</param>
/// <param name="Version">Its stored version when it was seen.</param>
public sealed record StuckSaga(
    string SagaType,
    Guid CorrelationId,
    string CurrentState,
    SagaStatus Status,
    StuckCause Cause,
    TimeSpan IdleFor,
    TimeSpan Threshold,
    DateTimeOffset UpdatedAtUtc,
    int Version);
