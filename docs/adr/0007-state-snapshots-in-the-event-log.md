# ADR 0007: Per-step saga state is recorded as StatePersisted entries in the existing event log

**Status:** **Accepted** — 2026-10-02. **Implemented** — 2026-10-05.
**Date:** 2026-10-02
**Relates to:** [`0005-saga-state-storage-model.md`](0005-saga-state-storage-model.md) (one opaque state
blob per instance, which this keeps); [`0003-persistence-contract-clauses.md`](0003-persistence-contract-clauses.md)
(clause 12, the blob format this relies on, and the outbox staging rule it must not break);
[`0002-redis-persistence-provider.md`](0002-redis-persistence-provider.md) (the Redis capacity model this
changes); [`0008-dashboard-retry-reruns-the-failed-step.md`](0008-dashboard-retry-reruns-the-failed-step.md)
(which adds inbound message payloads to the same log).
**Design:** [`../design/dashboard-usability-and-access.md`](../design/dashboard-usability-and-access.md)
**Implemented by** (the numbers are those of the design's §12): the EF Core detach, `3eda0ea` (C15); the
entry type and `SagaStateSnapshot`, with two conformance cases for all four providers, `e3379f0` (C16);
the map skipping snapshots, `46dbce4` (C17); the redaction seam and payload-free pushes, `4fce969` (C18);
recording after the four committing persists, with the options, the budget, the deadline and the samples,
`507f661` (C21); the dashboard's reset snapshot, `1f1e056` (C22); and in the SPA the fold of a timeline
into steps with their snapshots, `50c0583` (C11), the per-step data inspector, `f8e91f0` (C25), and the
Saga data bar, `25385b6` (C26). The measured storage is in [`../persistence.md`](../persistence.md) (`61354b9`).

---

## Context

A saga instance's state is one blob (ADR 0005), and every persist overwrites it: `ISagaSnapshotStore`
keeps the current `TState` and nothing else. The dashboard's Data tab can therefore show only the state
as it is now. What the state was after step 3 of a saga that failed at step 7 is gone the moment step 4
commits.

The event log (`ISagaEventLogStore`) records every transition, message and outcome, but no state. The
only payloads it holds are inbound message bodies, on `SagaStarted` and `StepFailed`. A user asking "what
did this step change?" has no record to answer from.

Any way of keeping per-step state has to live inside four constraints the engine already depends on:

1. **The outbox staging window.** The orchestrator stages outbox rows immediately before a persist, and
   on EF Core any `SaveChangesAsync` on the shared context commits whatever is staged
   (`dotnet/src/VSaga.Abstractions/Persistence/ISagaOutboxStore.cs`, remarks on `EnqueueAsync`). A write
   that commits between staging and the persist would make the staged publishes durable for a transition
   that may then lose its optimistic-concurrency race.
2. **Appends are independent of the persist.** On MongoDB and Redis an event-log append is a separate
   write that takes no part in the persist's unit of work; on EF Core it is its own `SaveChangesAsync`. No
   provider can make "state plus log entry" atomic without a persistence-contract change, and this round
   of work excludes contract and schema changes.
3. **Visited states.** `SagaOrchestrator.GetVisitedStatesAsync` derives the compensation set from the
   `ToState` of every timeline entry, in timeline order. A new entry must not carry a `ToState`.
4. **Dedupe.** `IsDuplicateAsync` counts only `SagaStarted` and `MessageReceived` entries (Redis feeds its
   dedupe set from those two alone). A new entry that carries the inbound message id must not count.

---

## Decision

**After each persist that commits a transition, the engine appends a `StatePersisted` entry to the
saga's existing event log. Its `PayloadJson` is the state blob exactly as the snapshot store wrote it.**

- `SagaEntryType.StatePersisted` is appended last to the enum (numeric value 21), under the enum's
  append-only rule.
- `PayloadJson` is the blob text, or a marker: `{"$vsagaStateOmitted":true,"bytes":N,"limit":L}` when
  the state exceeds the per-snapshot cap, or `{"$vsagaStateOmitted":true,"bytes":N,"budget":B}` when the
  per-saga budget is used up. `MessageType` and `MessageId` name the inbound message whose step it
  follows: set for step success, step failure and delivery exhaustion; null after a timeout and after a
  dashboard retry reset. `FromState` and `ToState` are always null.
- It is recorded at the four persists that commit a transition: step success, step failure, the timeout's
  final persist, and delivery exhaustion. Nothing is recorded for the timeout claim, the business-key
  reservation insert, an `UnexpectedEvent`, a duplicate, an unhandled timeout or a lost race.
- The shape and the marker are defined once, in `dotnet/src/VSaga.Abstractions/Persistence/SagaStateSnapshot.cs`,
  which both the engine and the dashboard call.
- `SagaOrchestratorOptions` gains `RecordStateSnapshots` (default on), `MaxStateSnapshotBytes`,
  `MaxStateSnapshotBytesPerSaga` and `StateSnapshotTimeout`.
- After a dashboard retry resets a saga, the dashboard appends a snapshot of the state the reset left,
  but only when the saga's timeline already holds a `StatePersisted` entry (so a host that switched
  snapshots off is honoured) and only when the stored blob's `Version` is the reset's own. Its cap is the
  smaller of `Dashboard:StateSnapshots:MaxBytes` and the `limit` of the most recent `$vsagaStateOmitted`
  marker that carries a `limit` (budget markers are ignored for this), so the dashboard cannot write a full blob for a host that chose
  size-only markers.
- `EfCoreSagaEventLogStore.AppendAsync` detaches its entity when the save throws. Without that, a failed
  snapshot append stays `Added` in the scoped `DbContext` and every later `SaveChangesAsync` in the unit
  of work retries it, which can break the drain. This is a provider fix, not a contract change.

The entry is a diagnostic record. Neither compensation nor dedupe reads it, and nothing in the engine
depends on it being present.

### Why an entry type rather than a field on existing entries or a versioned history store

The event log already has a free-text payload column in every provider, it is already ordered per
instance, and the dashboard already reads it. A new entry type uses all of that with no schema change, no
migration and no new contract.

A `StateJson` field on `SagaLogEntry` would need a column, a document field and an entry-format change in
four providers. It would also have no correct entry to sit on: `StepSucceeded`, `SagaCompleted` and
`StepFailed` are all appended before the persist (`SagaOrchestrator.HandleStepSuccessAsync`,
`HandleStepFailureAsync`), so a state attached to them would describe a transition that may never commit.

A versioned history store (a new `ISagaStateHistoryStore` keyed by instance and version) is the cleaner
model and the natural home for retention. It is also a new contract, four implementations, a conformance
section and a migration, which this round excludes. If retention or erasure is ever needed, that is
where it would go (see "What would invalidate this decision later").

### Why after the persist and before the drain

**After the persist**, so a snapshot only ever describes a state that was stored. A persist that throws,
a lost race included, propagates as it does today and leaves no snapshot. The staging window is safe at
every site: staging precedes the persist, the persist commits what was staged, and nothing is staged
again before the drain, so on EF Core the snapshot's own `SaveChangesAsync` flushes only its own row. On
the failure path the deferred queue is never staged, so appending before `DiscardDeferredPublishesAsync`
cannot commit a row the discard exists to drop.

**Before the drain**, because the in-memory transport dispatches synchronously from inside a publish. A
snapshot appended after the drain would be sequenced behind a nested step's entries and its
higher-version snapshot. On brokers and the HTTP pump a reply can only exist once the drain has
published, so the snapshot also precedes anything the drain causes, and a drain failure cannot lose it.
The change notifier fires after the snapshot, so an in-process subscriber that refetches on
`SagaUpdatedAsync` finds the entry.

### Why Core serialises the state itself

The snapshot stores do not return the text they wrote, and reading it back with `GetDataJsonAsync` would
cost a round trip and could race a later step. Instead the engine makes the same call every provider
makes, `JsonSerializer.Serialize(state)` typed as `TState` with default options (ADR 0003 clause 12), on
the same object, with no statement between the store returning and the serialisation.

That makes Core an independent serialisation site, which `ISagaSnapshotStore`'s remarks deliberately
avoid sharing between providers. It is pinned twice: a golden-text test for a known state under a fixed
clock, and a test that the snapshot's `PayloadJson` equals `GetDataJsonAsync` after every kind of
persist. Each provider's own golden-blob conformance case ties its text to the same literal.

### Budgets: a per-snapshot cap and a per-saga budget

- **Per snapshot.** `MaxStateSnapshotBytes`, default 262 144 (256 KiB) in UTF-8 bytes. A larger state
  is recorded as the `$vsagaStateOmitted` marker. A cap of 0 records size-only markers, which is the
  setting for a host whose state must not be copied into the log.
- **Per saga.** `MaxStateSnapshotBytesPerSaga`, default 1 048 576 (1 MiB); 0 means unlimited.
  `GetVisitedStatesAsync`, which already reads the whole timeline before every step and timeout, also
  sums the lengths of the instance's `StatePersisted` payloads, and the sum rides on `SagaContext`. Past
  the budget, success-path and timeout snapshots become the budget marker. Step-failure and
  delivery-exhaustion snapshots are still recorded in full, because those are the ones an investigation
  needs. Early snapshots are kept, so the "At start" view still works, and the "At end" view reads the
  live blob and never depended on a snapshot.
- **Its own deadline.** The append sits between the commit and the first deferred publish, limited only
  by the store's own timeout. A stall would hold every deferred publish and the ack, and past the
  outbox's 30 s `DispatchGracePeriod` the recovery poller would republish rows the inline drain then
  sends again (Npgsql's default command timeout is also 30 s). The append therefore runs under a linked
  `CancellationTokenSource` with `CancelAfter(StateSnapshotTimeout)`, 5 seconds by default (the Redis
  client's own timeout, well under the 30 s grace period). A timeout
  is treated like any other swallowed failure: a warning, and a step with no snapshot. The EF Core detach
  makes a cancelled append safe; the Redis append ignores the token but its client timeout is 5 s.

The per-saga budget answers the cost finding that a per-snapshot cap alone leaves volume per instance
unbounded. Dropping the newest snapshots outright was rejected for the reason above: they describe the
failure.

### What it costs, accepted knowingly

- **One more serialisation and one more append per committed transition**: one round trip on EF Core and
  Redis, two on MongoDB (counter and insert). It is paid even when a budget turns the result into a
  marker. Hosts with very large states switch recording off.
- **Storage grows with steps × state size.** On Redis the entry is JSON inside JSON, so every quote in the
  blob costs extra bytes (roughly 1.6 to 2 times the raw size), held in RAM, counted toward
  `WriteMemoryThreshold`, past which every persist is refused (ADR 0002's capacity model,
  [`../persistence.md`](../persistence.md#capacity-model)).
- **Read volume grows quadratically.** `GetVisitedStatesAsync` reads every entry, payloads included, on
  every message and timeout. With S recorded transitions of average size B, a late message reads about
  S·B more bytes and the saga reads about B·S²/2 more over its life. The dashboard reads the timeline
  twice per push (timeline and map) and downloads it once. Without the per-saga budget:

  | Steps × state | Extra per late message | Extra over the saga's life |
  | --- | --- | --- |
  | 8 × 0.5 KB (the sample) | 4 KB | 16 KB |
  | 30 × 4 KB | 120 KB | 1.8 MB |
  | 200 × 32 KB | 6.4 MB | about 640 MB |

  With the budget, full snapshots past 1 MiB stop accumulating except on failure and exhaustion, so the
  third row is bounded by the budget plus markers of a few dozen bytes each. ADR 0008 also records the
  inbound message body on every `MessageReceived`, which adds to the same reads.
- **Recording is best-effort.** A crash between the commit and the append, an append failure or timeout,
  a change-poller push that lands before the append, and every saga older than the upgrade all leave
  steps with no snapshot. Consumers must tolerate gaps, and `Version` can jump by more than one between
  snapshots.
- **Business data is copied into a log with no retention or erasure path.** Nothing in vSaga deletes
  event-log entries, and saga state can hold personal data. The mitigations are access control (payloads
  and `errorMessage` are withheld from callers without `sagas.data`, ADR 0006), payload-free SignalR
  pushes, the off switch and size-only markers. Retention and erasure are a recorded follow-up that needs
  a persistence-contract change.

These figures are estimates from arithmetic over the sample's shapes, not measurements. The cost review
estimated about 1 KB per snapshot on Redis for the sample's 400-byte state, roughly half again on a
completed `OrderSaga`'s 7.3 KB timeline. The live verification then measured both providers on the
sample, and the figures are in [`../persistence.md`](../persistence.md): per completed `OrderSaga`, the
four snapshots add 4 216 bytes to the Redis timeline list (`MEMORY USAGE`, 8 312 to 12 528 with the
message bodies of ADR 0008) and 3 234 bytes to the MongoDB event-log documents (6 806 to 10 040).

---

## Options considered

| | Option | Verdict |
| --- | --- | --- |
| A | A new `StateJson` field on `SagaLogEntry` | Rejected. A schema and entry-format change in four providers, and every candidate entry is appended before the persist. |
| B | A separate, versioned state-history store | Rejected for now. A new contract, four implementations and a migration. The right home if retention is required. |
| C | Capture the state before the persist, beside the outcome entry | Rejected. It records states that never commit (a lost race, a persist that throws), and an append between staging and the persist commits staged outbox rows on EF Core, reopening the dual-write window the outbox closes. |
| D | A post-commit `StatePersisted` entry with the state in `PayloadJson` | **Chosen.** No contract or schema change; describes only committed states; fits the staging window. |
| E | Rebuild past states in the dashboard | Rejected. The dashboard deliberately never knows saga definitions (ADR 0005), step actions have side effects and are not deterministic, and most entries carry no payload to replay. |
| F | Store diffs against the previous snapshot | Rejected. Smaller, but every read replays a chain from the first snapshot, one missing snapshot (recording is best-effort) breaks the chain, and the diff runs on the hot path. The SPA computes diffs for display anyway. |

---

## Consequences

### Positive

- The dashboard can show what each step changed and the full state after it, with no persistence
  contract, schema or migration change.
- A snapshot only ever describes a committed state, so it never contradicts the stored blob.
- One switch, two caps and a deadline give an operator control over cost and over whether state is
  copied at all.

### Negative

- Every committed transition pays a serialisation and an append, and every step reads more bytes.
- Saga state is duplicated into an append-only log that nothing prunes or erases.
- Core becomes a second serialisation site for the blob format, held to it by tests rather than shared
  code.
- Timeline consumers outside the SPA see a new entry type: `VSaga.Testing` users asserting on counts or
  the last entry, the persistence samples (which gain a `StatePersisted` arm in their output), and
  scripts calling `/timeline`. The opt-out is `RecordStateSnapshots = false`.

### Neutral

- A dashboard API older than this change renders entry type 21 as a number and returns snapshot payloads
  to any authenticated caller. Deploy the dashboard before the engine hosts; compose builds both together.
- MongoDB's existing `MaxPayloadJsonBytes` guard still applies and would substitute its own
  `$vsagaPayloadOmitted` marker if the cap were raised past it.
- Several entries can share one `MessageId` (the engine's in-process retry reuses the failed message's
  id), so consumers attach a snapshot by id and position, never by id alone.

---

## What would invalidate this decision later

1. **A payload-free timeline read.** A store operation that returns entries without payloads would remove
   the quadratic read from `GetVisitedStatesAsync` and the map, and the per-saga budget could be
   revisited. It is a persistence-contract addition and a recorded follow-up.
2. **Event-log retention or erasure.** Snapshots would be the first thing to prune, and an erasure request
   would need to reach them. That is the point at which option B, a store built for it, becomes worth its
   cost.
3. **Native state storage** ([ADR 0005](0005-saga-state-storage-model.md), item 1). A provider that stores
   `TState` natively rather than as default `System.Text.Json` text breaks byte equality between a
   snapshot and the stored blob, and with it the reason Core can serialise on the provider's behalf.
4. **A requirement that snapshots be atomic with the persist.** Best-effort recording would then be wrong,
   and the snapshot would have to be written inside the persist's own unit of work, which MongoDB and
   Redis do not share with the event log. That is a contract change, most likely option B with a
   transactional write.
