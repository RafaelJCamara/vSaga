# ADR 0008: A dashboard retry re-runs the failed step, targeted at one saga type

**Status:** **Accepted** — 2026-10-02, by the maintainer. Not yet implemented.
**Date:** 2026-10-02
**Relates to:** [`0006-dashboard-authentication-and-identity-store.md`](0006-dashboard-authentication-and-identity-store.md)
(a `sagas.retry` grant scoped to saga types is only a boundary if a retry stays inside the type);
[`0007-state-snapshots-in-the-event-log.md`](0007-state-snapshots-in-the-event-log.md) (`MessageReceived`
now carries a payload too, so the log grows further, and a retry reset records a snapshot).
**Design:** [`../design/dashboard-usability-and-access.md`](../design/dashboard-usability-and-access.md),
section "Retry from the failed step".

---

## Context

`POST /api/sagas/{sagaType}/{correlationId}/retry` (`RetrySagaAsync` in
`dotnet/src/VSaga.Dashboard.Api/Endpoints/SagaEndpoints.cs`) has two shapes today:

1. When the timeline has a `StepFailed` entry, it replays the **last** one's message against the saga's
   current state. It does not reset anything; the engine's `RunStepAsync` flips a `Failed` saga back to
   `Running` when the message arrives.
2. Otherwise (a business failure or a timeout, where no step threw), it resets the saga to the state its
   `SagaStarted` entry entered and replays the initiating message. Every step from the start runs again,
   with every side effect those steps have.

Either way it republishes through `IMessageTransport.PublishRawAsync` with a fresh message id under the
same correlation id. Every saga type subscribed to that message type receives it, not only the one being
retried. The endpoint's own comment says so. The sample reproduces the consequence: retrying a `TimedOut`
`InvoiceFollowUpSaga` replays `InvoiceIssued`, `PostShipmentChoreography` handles it again, and a second
`InvoiceDeliverySaga` sends the customer a second email. With per-type grants (ADR 0006), the same
fan-out lets a user whose `sagas.retry` names one saga type drive steps in types they hold no grant for.

Two smaller defects follow from the shape. "Last `StepFailed`" ignores recency: a saga that failed
technically, was retried, and later failed for a business reason replays the old failure. And a failed
republish answers 502 but leaves a reset in place.

The maintainer decided on 2026-10-02 that a retry must re-drive only the retried saga, from the step that
failed onwards, and that the failed step must be obvious in the UI before the user confirms.

---

## Decision

**A dashboard retry identifies the step that failed, resets the saga to the state before that step, and
republishes only that step's message, carrying a header that names the one saga type allowed to handle
it.** The reset-to-start path is removed.

### The engine

- `MessageEnvelope.TargetSagaTypeHeader = "x-vsaga-target-saga-type"`. The `x-vsaga-` prefix is what
  every wire adapter, HTTP and Brighter included, already round-trips.
- `SagaOrchestrator.HandleCoreAsync` checks it first. When the header is present and does not equal
  `SagaType` (ordinal), the orchestrator logs at Debug and returns: the message is acknowledged, with no
  timeline entry, no deserialisation and no instance lookup. When it is absent, or equal, nothing changes.
- The header does not spread. Outbound envelopes are built fresh by `MessageEnvelope.From`, so messages the
  re-run step publishes carry no target. Redelivery after an infrastructure failure copies the inbound
  headers, so a retry that fails transiently stays targeted.
- `RunStepAsync` records the inbound message body in the `MessageReceived` entry's `PayloadJson` on every
  step, the first included, serialised the way `StepFailed` already records it
  (`JsonSerializer.Serialize(message, message.GetType())`). MongoDB's payload guard applies, and the
  redaction that withholds payloads without `sagas.data` covers it.

### Which step failed

A pure `SagaRetryPlanner.Plan(summary, timeline)` in `VSaga.Dashboard.Api` decides, ignoring
`StatePersisted` entries. Only a `Failed` or `TimedOut` saga is retryable. The failure entry is the
**latest** of `StepFailed`, `DeliveryExhausted` carrying a message id, `SagaCompleted` and
`TimeoutFired` in the timeline. A `DeliveryExhausted` without a message id records a deferred publish
that failed or was discarded (the failure path logs one after `StepFailed` whenever the step had queued
a publish), so it is skipped. Recency, not a fixed precedence ("the last `StepFailed`; else the business
failure; else the timeout"), and skipping id-less `DeliveryExhausted` entries keep the decision's
intent: the most recent failure is the one to re-run.

| Failure entry | Kind | Message replayed | Reset to |
| --- | --- | --- | --- |
| `StepFailed` | `StepFailed` | The entry's own message and payload | Its `FromState` |
| `DeliveryExhausted` (with a message id) | `DeliveryExhausted` | The latest `MessageReceived` or `SagaStarted` with the entry's message id and a payload | The current state; the dead-lettered step never committed |
| `SagaCompleted` | `BusinessFailure` | The step's inbound message: the entry's message id, else the latest `StepSucceeded` before it | That `StepSucceeded`'s `FromState` |
| `TimeoutFired` | `TimedOut` | The latest `StepSucceeded` before it that entered the timed-out state from another state | That step's `FromState` |

Using the step's own message id, rather than "the last inbound entry", avoids picking a `.CallHttp`
reply logged mid-step. `SagaCompleted` carries the inbound message id from this change on; older entries
fall back to the `StepSucceeded`. A state entered by a timeout has no message to replay and is not
retryable. No failure entry, or no recorded payload, is not retryable either: the latter means the saga
was recorded before vSaga stored message payloads on every step, and the reason says so in plain words.
`StepFailed` and `SagaStarted` have always carried payloads, so older sagas that failed by an exception,
or in their first step, stay retryable. A dead-lettered message is usually not retryable at all: its
`MessageReceived` exists only when the append succeeded on the final delivery attempt, because a durable
one on an earlier attempt makes the next redelivery a duplicate that is acknowledged, not dead-lettered.

### The endpoints

- `GET /api/sagas/{sagaType}/{correlationId}/retry-plan` returns the plan: `retryable`, `reason`,
  `failureKind`, `failureSequenceNumber`, and `step` with `sequenceNumber`, `messageType`, `messageId`
  and `fromState`. 404 for an unknown saga; otherwise always 200, with `retryable: false` and a reason
  rather than 409 or 422. It is read-only and needs `sagas.view` once authentication lands.
- `POST …/retry`: 404 for an unknown saga; 409 when not `Failed` or `TimedOut`; 422 with the plan's
  reason when not retryable. Otherwise it appends `ManualRetryRequested` (from the current state to the
  step's `FromState`, naming the replayed message's original id), then **always** calls
  `ISagaAdminStore.ResetStateAsync(sagaType, id, step.FromState, Running, summary.Version, now)`. The
  version check is the concurrency guard (409 when the saga moved). Business fields in the blob are not
  rolled back; the reset changes only `CurrentState`, `Status`, `Version` (+1) and `UpdatedAtUtc`, under
  its unchanged contract. The reset's snapshot is recorded (ADR 0007), then the message is republished
  with a fresh message id and the target header set to the saga's type. 202 on success.
- If the publish throws `MessageTransportPublishException`, the endpoint restores `CurrentState` and
  `Status` as it found them (best effort; a concurrent change is logged as a warning and left alone),
  records a snapshot of the restored state, and answers 502 saying whether the saga was restored.
- `SagaMapBuilder` is unchanged. The planner is the authority for which step failed: the SPA marks that
  step "Failed here" in the timeline, opens the map focused on it, and its confirmation reads
  `Re-run step N (<message type>, <from state>) for this saga only`, adding that other services consuming
  the message type still receive it.
- The in-process `SagaOrchestrator.RetryAsync` (`ISagaRetryDispatcher`) is unchanged. It runs the step
  inside the engine that owns the saga type, so it never fanned out.

---

## Options considered

| Option | Verdict |
| --- | --- |
| Keep the reset-to-start path for business failures and timeouts | Rejected. Re-runs every earlier step's side effects, and the republish still reaches every subscribed saga type. |
| Before publishing, find every saga type tracking the correlation id and refuse (403) unless the caller holds `sagas.retry` for all of them (the consistency review's alternative) | Rejected by the maintainer. It refuses rather than contains: an unscoped operator's retry still sends the second email, and types that would start from the message are not tracking the id yet, so the check cannot see them. |
| Honour `sagas.retry` only from unscoped grants, as `access.manage` is | Rejected. It removes scoped retry as a feature and leaves the fan-out for everyone who can retry. |
| Republish with a target-saga-type header the engine honours | **Chosen.** Contains the redrive to the saga being retried, and lets one step re-run instead of the whole saga. |

---

## Consequences

### Positive

- A retry touches only the saga type being retried, so a scoped `sagas.retry` grant is a real boundary
  between saga types.
- A business failure or timeout re-runs one step, not the saga from its start.
- The user sees which step will re-run, and why a saga cannot be retried, before confirming.
- A failed republish restores the saga's `CurrentState` and `Status` as the user found them, best
  effort; the 502 says whether that happened, and the `ManualRetryRequested` entry stays in the timeline.

### Negative

- **Consumers that are not sagas still receive the replay.** The header only instructs vSaga engines;
  participants and other services subscribed to the message type process it as a new message. The user
  guide and the retry confirmation say so.
- **An engine older than this change ignores the header** and the fan-out returns, with nothing to show
  it. The minimum engine version for targeted retry is the release that ships the header;
  `docs/dashboard.md` "Manual retry" and ADR 0006 state it.
- **Business fields set by the failed step stay as they were.** The step re-runs against them. The
  `StepFailed` redrive already works this way, since mutations made before the throw are persisted with
  the `Failed` status, but it now applies to business failures and timeouts too: a step that accumulates
  (appends to a list, increments a counter) will do so twice.
- **A step whose effects already went out runs again.** For a business failure or a timeout, the step
  succeeded: its publishes were drained and its HTTP calls made, and the re-run repeats them.
- **The event log grows.** Every `MessageReceived` now stores the inbound body, which adds to the storage
  and read costs ADR 0007 accepts.
- Sagas recorded before this change whose failing step was neither the first nor a `StepFailed` are
  refused with 422.

### Neutral

- `ManualRetryRequested` keeps its shape; its `ToState` is now the re-run step's `FromState` rather than
  the initial state.
- The header is visible on the wire, like the other `x-vsaga-` headers.

---

## What would invalidate this decision later

1. **Per-saga-type addressing in the transport.** If the dashboard could publish to one saga type's
   subscription directly, the header would be unnecessary, and non-saga consumers would stop receiving
   the replay too.
2. **The dashboard knowing saga definitions.** ADR 0005 makes the dashboard definition-agnostic. If that
   changed, a retry could run the step in-process, as `RetryAsync` does, with no republish at all.
3. **A requirement to roll back business fields.** That needs a restore source for the blob and a
   different reset operation. ADR 0007's snapshots are best-effort and capped, so they are not that source
   as they stand.
4. **Event-log retention that drops inbound entries or their payloads.** The planner reads its message
   and payload from the timeline; without them nothing is retryable.
