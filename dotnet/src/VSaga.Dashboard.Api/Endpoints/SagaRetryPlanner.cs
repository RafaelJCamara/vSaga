using System.Diagnostics.CodeAnalysis;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Dashboard.Api.Endpoints;

/// <summary>
/// How the saga failed, named after the timeline entry that records it. On the wire as the member name
/// (the API's global <c>JsonStringEnumConverter</c>), like <see cref="SagaLogEntry.EntryType"/>.
/// </summary>
public enum SagaRetryFailureKind
{
    /// <summary>A step threw: the failure entry is a <see cref="SagaEntryType.StepFailed"/>.</summary>
    StepFailed,

    /// <summary>
    /// A step finished the saga with a failing status through a normal transition (no exception): the failure
    /// entry is a <see cref="SagaEntryType.SagaCompleted"/>. A saga that reached <c>TimedOut</c> through a
    /// message step is one of these too; only a timeout that fired is <see cref="TimedOut"/>.
    /// </summary>
    BusinessFailure,

    /// <summary>A message was dead-lettered after its redeliveries ran out: a <see cref="SagaEntryType.DeliveryExhausted"/> with a message id.</summary>
    DeliveryExhausted,

    /// <summary>A state timeout fired: the failure entry is a <see cref="SagaEntryType.TimeoutFired"/>.</summary>
    TimedOut,
}

/// <summary>
/// The step a dashboard retry re-runs: the message it replays (type and original id) and the state the saga
/// is reset to first. <paramref name="SequenceNumber"/> is the step's inbound timeline entry, which the SPA maps
/// to its step ordinal.
/// </summary>
public sealed record SagaRetryStep(long SequenceNumber, string MessageType, string MessageId, string FromState);

/// <summary>
/// What a dashboard retry of one saga would do, or why it cannot (<paramref name="Reason"/>, null when
/// <paramref name="Retryable"/>). <paramref name="FailureKind"/> and <paramref name="FailureSequenceNumber"/>
/// describe the failure entry and are set whenever one was found, retryable or not, so the UI can mark the
/// failed step either way; <paramref name="Step"/> is set whenever the step was identified, even when its
/// message body is missing.
/// </summary>
public sealed record SagaRetryPlan(bool Retryable, string? Reason, SagaRetryFailureKind? FailureKind, long? FailureSequenceNumber, SagaRetryStep? Step)
{
    /// <summary>
    /// The recorded body of the step's message, set only on a retryable plan. Internal, so it never reaches
    /// the retry-plan response: the body is saga data, which the plan endpoint does not serve.
    /// </summary>
    internal string? MessageBody { get; init; }
}

/// <summary>
/// Decides which step a dashboard retry re-runs (docs/design/dashboard-usability-and-access.md §7.2, ADR 0008).
/// Pure: it reads only the summary and the timeline it is handed, so the retry-plan endpoint and the retry
/// endpoint agree, and every rule is tested directly. <see cref="SagaEntryType.StatePersisted"/> entries never
/// match any of its searches, so snapshots between a step's entries change nothing.
/// </summary>
internal static class SagaRetryPlanner
{
    internal const string NotFailedReason = "Only Failed or TimedOut sagas can be retried.";
    internal const string NoStepReason = "No failed step could be identified in this saga's timeline.";
    internal const string EnteredByTimeoutReason = "The timed-out state was entered by a timeout, which has no message to replay.";
    internal const string BodyTooLargeReason = "The message that ran this step was too large to be recorded, so it cannot be replayed.";

    /// <summary>
    /// How MongoSagaEventLogStore's size marker (<c>{"$vsagaPayloadOmitted":true,"bytes":N,"limit":L}</c>) begins.
    /// It stands where a payload above the provider's cap would have been, so it is no body to replay: the engine
    /// would deserialise it into a message whose every field is default and re-run the step on that.
    /// </summary>
    private const string PayloadOmittedPrefix = "{\"$vsagaPayloadOmitted\":";

    /// <summary>
    /// The plan for retrying <paramref name="summary"/>'s saga given its <paramref name="timeline"/>, in
    /// sequence order as the event log returns it. The failure entry is the most recent of
    /// <see cref="SagaEntryType.StepFailed"/>, <see cref="SagaEntryType.SagaCompleted"/>,
    /// <see cref="SagaEntryType.TimeoutFired"/> and a <see cref="SagaEntryType.DeliveryExhausted"/> carrying a
    /// message id. Recency rather than a fixed precedence: a saga that failed technically, was retried, and
    /// later failed for a business reason re-runs the later step. An id-less DeliveryExhausted records a
    /// deferred publish that was discarded or failed (the failure path logs one after StepFailed whenever the
    /// step had queued a publish), so counting it would hide the step that failed.
    /// </summary>
    public static SagaRetryPlan Plan(SagaSummary summary, IReadOnlyList<SagaLogEntry> timeline)
    {
        if (summary.Status is not (SagaStatus.Failed or SagaStatus.TimedOut))
            return new SagaRetryPlan(false, NotFailedReason, null, null, null);

        var failureIndex = LatestIndexBefore(timeline, timeline.Count, IsFailureEntry);
        if (failureIndex < 0)
            return new SagaRetryPlan(false, NoStepReason, null, null, null);

        var failure = timeline[failureIndex];
        return failure.EntryType switch
        {
            SagaEntryType.StepFailed => PlanStepFailed(timeline, failureIndex),
            SagaEntryType.DeliveryExhausted => PlanDeliveryExhausted(summary, timeline, failureIndex),
            SagaEntryType.SagaCompleted => PlanBusinessFailure(timeline, failureIndex),
            _ => PlanTimedOut(timeline, failureIndex),
        };
    }

    private static bool IsFailureEntry(SagaLogEntry entry) =>
        entry.EntryType is SagaEntryType.StepFailed or SagaEntryType.SagaCompleted or SagaEntryType.TimeoutFired
        || entry is { EntryType: SagaEntryType.DeliveryExhausted, MessageId: not null };

    /// <summary>
    /// The failed step's own message and state; its body rides on the StepFailed entry itself. When that body
    /// is a size marker, the step's MessageReceived is tried before giving up.
    /// </summary>
    private static SagaRetryPlan PlanStepFailed(IReadOnlyList<SagaLogEntry> timeline, int failureIndex)
    {
        var failure = timeline[failureIndex];
        if (failure is not { MessageType: { } messageType, MessageId: { } messageId, FromState: { } fromState })
            return NoStep(SagaRetryFailureKind.StepFailed, failure);

        var inbound = InboundSequenceNumber(timeline, failureIndex, messageId) ?? failure.SequenceNumber;
        var body = IsRecordedBody(failure.PayloadJson)
            ? failure.PayloadJson
            : BodyBefore(timeline, failureIndex, messageId) ?? failure.PayloadJson;
        return Complete(SagaRetryFailureKind.StepFailed, failure, new SagaRetryStep(inbound, messageType, messageId, fromState), body,
            missingBodyDetail: null);
    }

    /// <summary>
    /// The dead-lettered message, re-run from the saga's current state: the dead-lettered step never committed,
    /// so the state is still the one it started from. Its body is found only when the step's MessageReceived
    /// append succeeded on the final delivery attempt, which is the exception rather than the rule.
    /// </summary>
    private static SagaRetryPlan PlanDeliveryExhausted(SagaSummary summary, IReadOnlyList<SagaLogEntry> timeline, int failureIndex)
    {
        var failure = timeline[failureIndex];
        var messageId = failure.MessageId!;
        if (failure.MessageType is not { } messageType)
            return NoStep(SagaRetryFailureKind.DeliveryExhausted, failure);

        var inbound = InboundSequenceNumber(timeline, failureIndex, messageId) ?? failure.SequenceNumber;
        return Complete(SagaRetryFailureKind.DeliveryExhausted, failure, new SagaRetryStep(inbound, messageType, messageId, summary.CurrentState),
            BodyBefore(timeline, failureIndex, messageId),
            missingBodyDetail: " The message may also have been dead-lettered before it was recorded at all.");
    }

    /// <summary>
    /// The step that finished the saga: its inbound message id is the one SagaCompleted carries (stamped since
    /// the engine records it there), else the latest StepSucceeded's. Going by the step's own id, never by the
    /// latest inbound entry, keeps a <c>.CallHttp</c> reply logged mid-step from being taken for the step.
    /// </summary>
    private static SagaRetryPlan PlanBusinessFailure(IReadOnlyList<SagaLogEntry> timeline, int failureIndex)
    {
        var failure = timeline[failureIndex];
        var messageId = failure.MessageId
            ?? LatestBefore(timeline, failureIndex, e => e.EntryType == SagaEntryType.StepSucceeded)?.MessageId;
        if (messageId is null)
            return NoStep(SagaRetryFailureKind.BusinessFailure, failure);

        var succeeded = LatestBefore(timeline, failureIndex,
            e => e.EntryType == SagaEntryType.StepSucceeded && string.Equals(e.MessageId, messageId, StringComparison.Ordinal));
        return PlanFromSucceededStep(SagaRetryFailureKind.BusinessFailure, timeline, failureIndex, succeeded);
    }

    /// <summary>
    /// The step that entered the timed-out state from another state. A state entered by a timeout transition
    /// (a StepSucceeded with no message id) has no message to replay.
    /// </summary>
    private static SagaRetryPlan PlanTimedOut(IReadOnlyList<SagaLogEntry> timeline, int failureIndex)
    {
        var failure = timeline[failureIndex];
        var timedOutState = failure.FromState;
        var entering = LatestBefore(timeline, failureIndex,
            e => e.EntryType == SagaEntryType.StepSucceeded
                 && timedOutState is not null
                 && string.Equals(e.ToState, timedOutState, StringComparison.Ordinal)
                 && !string.Equals(e.FromState, timedOutState, StringComparison.Ordinal));

        if (entering is { MessageId: null })
            return new SagaRetryPlan(false, EnteredByTimeoutReason, SagaRetryFailureKind.TimedOut, failure.SequenceNumber, null);

        return PlanFromSucceededStep(SagaRetryFailureKind.TimedOut, timeline, failureIndex, entering);
    }

    /// <summary>The plan for re-running the step <paramref name="succeeded"/> records, from its FromState.</summary>
    private static SagaRetryPlan PlanFromSucceededStep(SagaRetryFailureKind kind, IReadOnlyList<SagaLogEntry> timeline, int failureIndex,
        SagaLogEntry? succeeded)
    {
        var failure = timeline[failureIndex];
        if (succeeded is not { MessageType: { } messageType, MessageId: { } messageId, FromState: { } fromState })
            return NoStep(kind, failure);

        var inbound = InboundSequenceNumber(timeline, failureIndex, messageId) ?? succeeded.SequenceNumber;
        return Complete(kind, failure, new SagaRetryStep(inbound, messageType, messageId, fromState),
            BodyBefore(timeline, failureIndex, messageId), missingBodyDetail: null);
    }

    private static SagaRetryPlan NoStep(SagaRetryFailureKind kind, SagaLogEntry failure) =>
        new(false, NoStepReason, kind, failure.SequenceNumber, null);

    /// <summary>
    /// A retryable plan when the step's body was recorded; otherwise not retryable, saying why in plain words:
    /// a size marker where the body should be, or no body at all (a saga recorded before every step's body was).
    /// </summary>
    private static SagaRetryPlan Complete(SagaRetryFailureKind kind, SagaLogEntry failure, SagaRetryStep step, string? body, string? missingBodyDetail)
    {
        if (IsRecordedBody(body))
            return new SagaRetryPlan(true, null, kind, failure.SequenceNumber, step) { MessageBody = body };

        var reason = body is not null
            ? BodyTooLargeReason
            : "This saga was recorded before vSaga stored the message of every step, so the "
              + $"{step.MessageType} message that ran the step to re-run cannot be replayed." + missingBodyDetail;
        return new SagaRetryPlan(false, reason, kind, failure.SequenceNumber, step);
    }

    /// <summary>
    /// The step's inbound entry: the latest MessageReceived before the failure with the step's message id, else
    /// the latest SagaStarted with it.
    /// </summary>
    private static long? InboundSequenceNumber(IReadOnlyList<SagaLogEntry> timeline, int failureIndex, string messageId) =>
        (LatestBefore(timeline, failureIndex, e => e.EntryType == SagaEntryType.MessageReceived && HasId(e, messageId))
         ?? LatestBefore(timeline, failureIndex, e => e.EntryType == SagaEntryType.SagaStarted && HasId(e, messageId)))?.SequenceNumber;

    /// <summary>
    /// The body recorded for the step's message: the latest MessageReceived or SagaStarted before the failure
    /// with that id and a recorded body. A saga recorded before MessageReceived carried bodies still finds its
    /// first step's body on SagaStarted. With no recorded body but a size marker, the marker, so the caller can
    /// tell "too large" from "never recorded"; with neither, null.
    /// </summary>
    private static string? BodyBefore(IReadOnlyList<SagaLogEntry> timeline, int failureIndex, string messageId) =>
        (LatestBefore(timeline, failureIndex, e => IsInbound(e) && IsRecordedBody(e.PayloadJson) && HasId(e, messageId))
         ?? LatestBefore(timeline, failureIndex, e => IsInbound(e) && e.PayloadJson is not null && HasId(e, messageId)))?.PayloadJson;

    private static bool IsInbound(SagaLogEntry entry) =>
        entry.EntryType is SagaEntryType.MessageReceived or SagaEntryType.SagaStarted;

    /// <summary>True for a payload that is a message body: present and not a provider's size marker.</summary>
    private static bool IsRecordedBody([NotNullWhen(true)] string? payload) =>
        payload is not null && !payload.StartsWith(PayloadOmittedPrefix, StringComparison.Ordinal);

    private static bool HasId(SagaLogEntry entry, string messageId) =>
        string.Equals(entry.MessageId, messageId, StringComparison.Ordinal);

    private static SagaLogEntry? LatestBefore(IReadOnlyList<SagaLogEntry> timeline, int endExclusive, Func<SagaLogEntry, bool> predicate)
    {
        var index = LatestIndexBefore(timeline, endExclusive, predicate);
        return index < 0 ? null : timeline[index];
    }

    private static int LatestIndexBefore(IReadOnlyList<SagaLogEntry> timeline, int endExclusive, Func<SagaLogEntry, bool> predicate)
    {
        for (var i = endExclusive - 1; i >= 0; i--)
        {
            if (predicate(timeline[i]))
                return i;
        }

        return -1;
    }
}
