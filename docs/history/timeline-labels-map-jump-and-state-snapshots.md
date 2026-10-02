# History: timeline labels, the map jump, state snapshots and the targeted retry

> Written fresh. Describes the timeline, map, snapshot and retry slice of the dashboard usability and
> access work, 21 commits on 2026-10-02 (`f2cbe38` to `73cbce6`), following §5, §6 and §7 of
> [`../design/dashboard-usability-and-access.md`](../design/dashboard-usability-and-access.md) and ADRs
> [0007](../adr/0007-state-snapshots-in-the-event-log.md) and
> [0008](../adr/0008-dashboard-retry-reruns-the-failed-step.md). See
> [`../dashboard.md`](../dashboard.md#the-saga-detail-page) (the detail page,
> [State snapshots](../dashboard.md#state-snapshots), [Manual retry](../dashboard.md#manual-retry)),
> [`../observability.md`](../observability.md) and [`../persistence.md`](../persistence.md) for the
> current reference documentation. Every observed value below comes from the live runs and mutation
> checks recorded while the slice was built, or from the commit messages themselves.

---

## What was built

Before this slice the detail page listed raw timeline entries with a bare `HH:mm:ss.SSS` in an unnamed
zone, the map and the timeline could not be crossed between, the Data tab showed only the current state
blob, and nothing recorded what a saga held after each step: the snapshot store overwrites its blob on
every persist. A dashboard retry reset a business-failed or timed-out saga to its initial state and
replayed the initiating message, addressed by message type only, so every saga type subscribed to that
type handled it again.

**The detail page (SPA).**

- **Pure helpers** (`f2cbe38`). `src/app/util/` gains `state-json` (which also recognises any
  `$vsaga...Omitted` marker), `json-diff` (stops at 200 changes and says so), `time-format` (local time,
  exact UTC, offsets; .NET's seven fractional digits are cut to three before `Date.parse`) and
  `entry-type-label`, each with its own spec.
- **The fold** (`50c0583`). `util/saga-transitions.ts` folds the timeline into steps: an entry joins the
  step its message id started, then the step holding the message its causation id names, and only then
  the step touched last. Adjacency alone breaks when one instance handles messages concurrently, when
  the in-process retry reuses the failed message id, and when a `.CallHttp` reply is logged mid-step
  under a fresh id. `StatePersisted` becomes its step's snapshot and is never a row. "Pending" lasts only
  while the step's newest entry is younger than 5 s (`PENDING_SNAPSHOT_MS`), because a lost persist race
  or an unhandled timeout leaves a final step with no snapshot for good. `src/app/testing/` is excluded
  from the app build in the same commit.
- **Labelled steps** (`feae3d1`). The Timeline tab shows "Step N <title> · <outcome>", each row reading
  "#i <entry type> ... Recorded at <local time> <offset>", a `<time>` whose title is the exact UTC time, and
  one hint naming the viewer's zone. The summary reads "Created (UTC+02:00)".
- **Jump to the map** (`b38752d`). Every row is a native button that opens the map as of that entry; a
  step title jumps to the step's last entry. The map pins its replay on the entry (or the last earlier
  event), a `role="status"` banner says where it stands and links back, and an orchestrator standing for
  a plain entry gets its own `node--focus` ring. `?tab=` and `?entry=` are URL state.
- **Per-step data** (`f8e91f0`). A "Data" toggle per step opens an inspector: the diff against the
  nearest earlier snapshot, the full state, the inbound message where recorded, and Copy JSON of the
  stored text. Open inspectors live in `SagaDetail`, so they survive the trip to the map and back.
- **The Saga data bar** (`25385b6`) replaces the Data tab: "At start", "At end" (or "Current" while the
  saga runs) and "Compare", as `?data=start|end|compare`.
- **Coalesced refresh and load errors** (`c633055`). A push patches the summary at once and asks for one
  refresh, audited over 250 ms, that re-reads the detail, timeline, map and both relation strips. Pushed
  timeline entries are no longer appended. A failed timeline or map load shows an error banner with "Try
  again", or a warning above content loaded earlier.
- **The failed step** (`f28636e`). For a Failed or TimedOut saga the page reads the retry plan: the step
  holding the failure entry reads "Failed here", the step a retry re-runs reads "Re-run starts here" when
  it is another step (the timeout case), the map opens on the failure entry when no `?entry` is given,
  and the confirmation reads "Re-run step N (<message type>, <from state>) for this saga only?" with a
  line saying other services that consume that message type still receive it.

**State snapshots (engine and API).**

- **`SagaMapBuilder` unit tests** (`bbf3dcf`), ahead of the builder change, so a rule breaks by name rather
  than as a drifting HTTP response.
- **EF Core detach** (`3eda0ea`). A failed event-log append used to stay `Added` in the scoped
  `DbContext`, so every later `SaveChangesAsync` re-sent the row. The append now detaches its own entity
  and rethrows. This had to land first: a swallowed snapshot failure would otherwise poison the rest of
  the step on EF Core.
- **The entry type** (`e3379f0`). `SagaEntryType.StatePersisted` is appended last (value 21), with a test
  pinning every member's numeric value, and `SagaStateSnapshot` is the one definition of the entry and of
  the `{"$vsagaStateOmitted":true,"bytes":N,"limit":L}` marker. Two conformance cases run against all four
  providers: the payload text round-trips exactly, and a snapshot carrying a message id never makes that
  id a duplicate.
- **The map skips snapshots** (`46dbce4`), filtered before ordering so they shift no event or failure index.
- **Redaction seam** (`4fce969`). `SagaTimelineRedaction` nulls `PayloadJson` and `ErrorMessage` together
  when the caller may not see data, and `TimelineEntryAdded` pushes carry neither, for everyone, because a
  hub group is joined per saga, not per permission.
- **Stamps** (`129115e`, `899dd9f`). Every entry pushed over SignalR used to carry sequence number 0,
  because the engine notified with the object it appended while every store stamps a copy; it now
  notifies with the stored number. `TimeoutScheduled` and `SagaCompleted` carry the step's inbound message
  id, and the saga context's log sink fills a null causation id with it.
- **Recording** (`507f661`). The engine appends a `StatePersisted` entry after every committing persist
  (step success, step failure, the timeout's final persist, delivery exhaustion), after the persist and
  before the drain and the notifier. It is best effort under its own deadline. `SagaOrchestratorOptions`
  gains `RecordStateSnapshots` (true), `MaxStateSnapshotBytes` (256 KiB), `MaxStateSnapshotBytesPerSaga`
  (1 MiB; past it success and timeout snapshots become a marker with a `"budget"` key, failure and
  exhaustion snapshots stay full) and `StateSnapshotTimeout` (5 s). The OrderProcessing sample binds an
  `Orchestrator` section, and the persistence samples print the entry as "state saved, N bytes".
- **The reset snapshot** (`1f1e056`). `SagaResetSnapshotRecorder` records the state a dashboard retry
  reset leaves behind, only when the timeline already holds a snapshot, capped by
  `Dashboard:StateSnapshots:MaxBytes` lowered to the limit of the timeline's last size marker.

**The targeted retry.** This part had no workstream blueprint. The second planning round decided that a
dashboard retry re-runs only the failed step of the retried saga, and the design was written first, in
§7 of the design document and in ADR 0008 (`6f44c8b`), before any code.

- **Engine** (`417a64e`). `MessageEnvelope.TargetSagaTypeHeader` (`x-vsaga-target-saga-type`; the
  `x-vsaga-` prefix is what every wire adapter carries). `HandleCoreAsync` returns first thing, before
  reading the body, when the header names another saga type (ordinal), which acknowledges the message
  with no timeline entry. Outbound envelopes are built fresh, so the header never reaches what the
  redriven step publishes. `MessageReceived` now records the message body on every step.
- **Planner and endpoints** (`3a817e6`). `SagaRetryPlanner.Plan(summary, timeline)` takes the latest
  failure entry and names the step to re-run and the state to reset to, per kind (`StepFailed`,
  `BusinessFailure`, `DeliveryExhausted`, `TimedOut`). `GET .../retry-plan` serves it (404 or 200, never
  409 or 422). `POST .../retry` answers 422 with the plan's reason, appends `ManualRetryRequested`, always
  resets to the step's from-state and Running at the version it read (409 on a race), records the reset
  snapshot and republishes the step's message with a fresh id and the target header. When the publish
  throws, it restores the previous state and status at version + 1 and answers 502 with `restored`.
- **SPA**: the failed-step marker and confirmation above (`f28636e`).

**Documentation** (`61354b9`, `73cbce6`). `dashboard.md` rewrites "Manual retry" and gains "State
snapshots" and "The saga detail page"; `observability.md`, `configuration.md`, `persistence.md` (storage
re-measured from the live runs below), the transport pages and `testing.md` follow. The second commit
corrects four places where the first did not match the code (listed under the problems below).

## How it was verified

### Tests and mutations

The slice-wide engine and API mutation run was on `646dece`, the form of the retry commit before its last
amend (see the problems below). Baseline: build 0 warnings, 0 errors; full suite 1077/1077 across 15 test
projects (Core 230, Dashboard.Api 199, EFCore 218, MongoDB 111, Redis 109, InMemory persistence 84,
Transport.Http 39, Chaos 27, Http 15, Transport.InMemory 13, Testing 7, Brighter 7, MassTransit 6,
RabbitMQ 6, Wolverine 6). Each mutation was a `sed -i` on the tracked file, followed by a full rebuild,
`dotnet build dotnet/VSaga.slnx -nologo` (0 warnings, 0 errors), and the full suite on that build,
`dotnet test dotnet/VSaga.slnx --no-build -nologo`, then `git checkout -- <file>`. A restored rebuild and
suite at the end gave 1077/1077 again. No testhost crash occurred in the 18 full-suite runs.

| # | Mutation | What failed (every other project green) |
| --- | --- | --- |
| 1 | Snapshot recorded before the persist (the two awaits swapped) | Core 10: the blob-equality theory (insert, update, business-key, step failure, exhaustion), the golden text, the exact cap marker, the per-saga budget, delivery exhaustion, the lost step race |
| 2 | Step-success snapshot moved after the drain | Core 1: `StepSuccess_SnapshotPrecedesTheDrainedLoopbacksStep_AndVersionsAscend` |
| 3 | Snapshot catch narrowed to `OutOfMemoryException` | Core 5: the whole `StateSnapshotContainmentTests` class, the hanging append included |
| 4 | `CancelAfter(StateSnapshotTimeout)` deleted | Core 1: `HangingAppend_IsAbandonedAtStateSnapshotTimeout_AndProcessingContinues` (its 30 s guard expired) |
| 5 | Budget check `<= 0` → `>= 0` (never exceeded) | Core 4: the two builder budget tests and the two engine per-saga budget tests |
| 6 | EF detach → `EntityState.Added` | EFCore 3: the three detach tests from `3eda0ea` |
| 7 | Map filter on `StatePersisted` deleted | Dashboard.Api 4: the map snapshot tests (unit, both statuses, and endpoint) |
| 8 | Redaction returns an unredacted copy | Dashboard.Api 2: both `SagaTimelineRedactionTests` |
| 9 | Notifier pushes the entry unstripped | Dashboard.Api 1: `TimelineEntryAdded_PushesTheEntryWithoutItsPayloadOrErrorMessage` |
| 10 | Reset recorder ignores the engine's marker limit | Dashboard.Api 3: the three host-limit cases of `RetryStateSnapshotTests` |
| 11 | Target-header comparison negated | Core 7: the `TargetedRedriveTests` targeted-message cases; the adapter header round-trips stayed green, as they do not run the engine's check |
| 12 | Target header not set on the redrive envelope | Dashboard.Api 2: the two endpoint envelope tests |
| 13 | Fixed precedence (last `StepFailed` wins) instead of recency | Dashboard.Api 1: `Plan_TechnicalFailureFixedByARetryBeforeALaterBusinessFailure_ReRunsTheLaterStep` |
| 14 | Reset only when the from-state differs from the current state | Dashboard.Api 5: the envelope test, the reset-snapshot test, the 409 race and both 502 restore tests (see below) |
| 15 | Restore never runs | Dashboard.Api 2: both 502 restore tests |
| 16 | `MessageReceived` payload line deleted | Core 1: `MessageReceived_CarriesTheMessageBody_OnTheFirstStepAndLaterOnes`; no Dashboard.Api test |

Two rows need a word. Mutation 14 fails more than its named test, but every failure follows from the
always-reset rule: the 409 and 502 fixtures are `StepFailed` sagas whose current state equals the step's
from-state, so without the reset there is no version check and the restore finds an unbumped version.
The first attempt at it failed the build on Sonar S3973 and was re-applied with the indentation the rule
wants. Mutation 16 shows a gap: the planner and endpoint tests seed hand-built timelines and never run the
engine, so the engine-to-planner coupling is covered only by that Core test and the live retry runs.

Earlier, per commit: the C21 implementer ran the same snapshot mutations against the `StateSnapshot*`
tests alone; `bbf3dcf` mutated the builder's ordering, first-failure guard, inbound compensation flag,
fan-out primary edge, business-failure trigger and unanswered status; `3eda0ea`'s third test also fails
when the detach is widened to `ChangeTracker.Clear()`; `4fce969` pushed the entry unchanged, nulled only
the payload and ignored `includeData`.

The SPA mutation run was on `f28636e`, from `dashboard-web/`: baseline 19 files, 427/427; each mutation a
one-line `sed -i`, `npx ng test --watch=false`, `git checkout -- <file>`, `npx ng test --watch=false` again
(427/427 every time, tree clean).

| # | Mutation | What failed |
| --- | --- | --- |
| 1 | "Pending" ignores the step's age | 6 age-window specs (fold and timeline) |
| 2 | Adjacency before message id in the fold | 4 "attachment by id" specs |
| 3 | A `StatePersisted` also becomes a row | 25 specs, every one asserting snapshot-free rows, ordinals or a step's last row |
| 4 | `[class.node--focus]` binding deleted | 2 map focus specs |
| 5 | `resolveFocusIndex` takes the next event, not the last earlier one | 4 specs; "falls back to the first event" passes, the mutation being equivalent there |
| 6 | `?entry=0` accepted | 1 detail spec |
| 7 | `auditTime` removed (a refresh per push) | 9 detail specs: five on refresh counts, four that push a summary and assert it before the deferred refresh |
| 8 | Refresh keeps the response whatever its version | 2 out-of-order specs |
| 9 | Failed-step marker from `step.sequenceNumber` | 1 failed-step spec |
| 10 | Arrays compared by identity in the diff | 5 `json-diff` specs |
| 11 | `formatOffset` `<` → `<=` at the minute | 1 spec (60 000 ms → `+1:00.000`) |
| 12 | Open inspectors not reset on a saga change | 1 detail spec |

At the end of the slice the SPA gates ran under Node 22 (v22.23.3), each as `npx -y -p node@22 -- <command>`
from `dashboard-web/`: `npm ci`, `npm audit --audit-level=low` (0 vulnerabilities), `npx ng build` with no
warning and an initial total of 462.26 kB (116.78 kB transfer), and `npx ng test --watch=false` (19 files,
427 passed). The C12 build had put the initial total at 415.53 kB, from 404.04 kB at
`50c0583`.

### Labelled times and steps (`feae3d1`), base stack, Chromium in Europe/Zurich

Two runs, the implementer's and a verifier's on `feae3d1`, each on a stack started with
`docker compose up -d --build --wait` from the repository root and stopped with `docker compose down` (no
`-v`), the page driven through Playwright MCP on `http://localhost:4200`. An OrderSaga showed four steps ("Started by
OrderSubmitted", "PaymentCharged", "InventoryReserved", "OrderShipped", each "· succeeded") over 15 rows
from `+0.000 s` to `+0.835 s`. A LoyaltyLookupSaga showed two steps, with the `.CallHttp` hop
(`POST http://localhost:8080/loyalty/lookup`, #3) and its reply (#4) inside step 1, not steps of their
own. The summary read `Created (UTC+02:00)` = "Oct 2, 2026, 12:53:12.792" with title
`2026-10-02 10:53:12.792 UTC`. Over every row: label "Recorded at", a title matching
`YYYY-MM-DD HH:mm:ss.SSS UTC` equal to the `datetime` instant to the millisecond, an offset, no
`StatePersisted` row, no button yet, no `[data-tour]`. Console: 2 messages, both SignalR information
lines; no error, warning or CSP violation.

### Jump to the map (`b38752d`)

Two runs, the implementer's and a verifier's on `b38752d`, on the same `docker compose up -d --build --wait`
stack and Playwright MCP (Chromium, viewer zone UTC+02:00). Every row was a `<button type="button">` with an
accessible name such as "#8 StepSucceeded Gathering → Gathering PaymentCharged Recorded at 13:15:08.509
+0.253 s since the saga's first entry Show on map". Clicking #8 (sequence 2144) set `?entry=2144`, opened
the Map tab and showed "As of entry #8 of 15: StepSucceeded, recorded at 13:15:08.509 (+0.253 s)" /
"Nothing moved between services at this entry, so OrderSaga is highlighted." with the orchestrator as the
only `node--focus`. Browser Back returned to `?tab=timeline` with no focus; "Back to this entry in the
timeline" gave `?entry=2144&tab=timeline` with row 2144 focused and in view; a step title jumped to that
step's last row (#12, `TimeoutScheduled`); a reload of `?tab=timeline&entry=2144` restored the focused
row; keyboard only (Tab, Tab, Enter) opened entry #9, a `MessageReceived` with an edge, so no orchestrator
outline. In the first run, Play on the focused map replaced the URL with the plain one and kept
`history.length`. On a failed OrderSaga the orchestrator read `node node--orchestrator node--failed
node--focus`: the failed red border (rgb(239, 85, 102)) inside the dashed focus ring (rgb(91, 140, 255)).
Console clean.

### Snapshots (`507f661`): base, options, chaos, MongoDB and Redis

Two runs, the implementer's on the working tree and a verifier's on `507f661`; the numbers below are the
verifier's unless marked.

**Base stack**, `docker compose up -d --build --wait`. A script calling the API on `127.0.0.1:5080` with
the compose key (`X-Api-Key: dev-local-only-change-me`) over every OrderSaga created after `up` (18 at the
second pass: 13 Completed, 5 Failed) found 0 mismatches. Each had exactly one `StatePersisted` per
distinct `StepSucceeded` message id, the last snapshot's payload string-identical to the detail endpoint's
`dataJson` (e.g. 425 vs 425 chars), and no snapshot among the `/map` events. The Failed ones ended on `"Status":2`. One timeout fired
in the window, on an OrderSaga created by an earlier stack: three snapshots (two handled messages plus the
timeout step), the last with no message id and `"Status":5`. In Postgres
(`docker compose exec -T postgres psql -U postgres -d vsaga`), OrderSaga `0a8f7fdd` in id order:

```
 7190 | 5  StepSucceeded     OrderSubmitted    Submitted->Gathering
 7192 | 10 TimeoutScheduled  msgId ae107efa... ->Gathering
 7194 | 21 StatePersisted    OrderSubmitted    ae107efa...  428 chars
 ...
 7215 | 5  StepSucceeded     OrderShipped      AwaitingShipment->Completed
 7217 | 14 SagaCompleted     msgId 667cc942... ->Completed
 7219 | 21 StatePersisted    OrderShipped      667cc942...  425 chars
```

An aggregate over every instance started in the window, all seven saga types (232 snapshots), found every
snapshot directly after its own step's `StepSucceeded`, `TimeoutScheduled` or `SagaCompleted`, with
nothing foreign in between, and every drained publish after the snapshot (10 in LoyaltyLookupSaga, 13 in
MixedFulfilmentSaga). `docker compose logs order-processing 2>&1 | grep -ciE snapshot`: 0.

**Options through the sample's `Orchestrator` section**, by scratch override files (untracked) that set one
variable under `services.order-processing.environment`, each applied with
`docker compose -f docker-compose.yml -f <override>.yml up -d --wait order-processing` and checked with
`docker compose exec -T order-processing printenv <variable>`. With
`Orchestrator__MaxStateSnapshotBytes: "64"` every new snapshot of every type was a marker, e.g.
`{"$vsagaStateOmitted":true,"bytes":428,"limit":64}` (49 of 49 rows). With
`Orchestrator__RecordStateSnapshots: "false"`, 0 snapshots against 23 new instances and their
`StepSucceeded` rows. Defaults restored with `docker compose up -d --wait order-processing`: snapshots
again, 0 markers.

**Chaos overlay**, `docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d --build --wait`,
about 3.5 minutes: 249 snapshots over 97 instances, the same ordering with nothing foreign between, 0
duplicate snapshots per (saga type, correlation id, message id) although chaos re-delivers about 10 % of
messages, and 256 `StepSucceeded` rows with a message id against 256 snapshots
with one. The API script over 23 OrderSagas found 0 mismatches, timed-out ones included (`StatePersisted=3
(withMsgId=2, noMsgId/timeout=1)`, last `"Status":5`). The order-processing log held only the chaos
middlewares' warnings and the participants' simulated failures; no snapshot warning.

**MongoDB and Redis overlays**, run beside the chaos stack under their own project names and ports (API
on 5580 and 5680):

```bash
docker compose -p vsaga-mongo -f docker-compose.yml -f docker-compose.mongo.yml up -d --build --wait
docker compose -p vsaga-redis -f docker-compose.yml -f docker-compose.redis.yml up -d --build --wait
```

The API check found 0 mismatches over 16 and 13 OrderSagas.

**Persistence sample** (implementer, in-memory,
`dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.InMemory`): each step's
snapshot printed as `state saved, 417 bytes`; a timed-out order ended
`28 TimeoutFired | 29 StepSucceeded | 30 StatePersisted`.

### The targeted retry (`3a817e6`), base and chaos stacks

The implementer's run on the root stack (`docker compose up -d --build`, then
`docker compose up -d --build --wait` for the re-check after the review fix, same volume), a script calling
`GET .../retry-plan` and `POST .../retry` with the compose API key and diffing the saga's timeline before
and after. Queue bindings were deleted and re-created through the RabbitMQ management API on 15672; the
record keeps the answers (204 on delete, 201 on re-create), not the calls themselves.

- **Timeout, the design's worked example.** A timed-out InvoiceFollowUpSaga (`87fdaa3b`), plan
  `failureKind TimedOut`, step `InvoiceIssued` from `Requested`. POST 202; the saga re-ran from Requested,
  started a new archival child and ended Completed/Archived v5. PostShipmentChoreography under the same
  correlation id kept 19 entries and 1 child: no second InvoiceDeliverySaga.
- **Business failure.** OrderSaga `94af6b81` re-ran only `InventoryReservationFailed` from Gathering:
  `ManualRetryRequested Failed -> Gathering`, the reset snapshot, `MessageReceived` under a fresh id with
  the original body, the step, `SagaCompleted -> Failed`. The plan afterwards named the newer failure.
- **Recorded before C23.** OrderSaga `bb78b984`: plan `retryable:false`; POST 422 with "This saga was
  recorded before vSaga stored the message of every step, so the PaymentFailed message that ran the step
  to re-run cannot be replayed."; nothing appended.
- **502 with restore.** With the `order-submitted` binding deleted through the management API, POST
  answered 502 with `"restored":true` and the unroutable-publish detail; the timeline gained
  `ManualRetryRequested`, the reset snapshot and the restored snapshot, and the saga was back to
  TimedOut/Failed at version 5.
- **Technical failure.** With the `reserve-inventory` binding deleted, OrderSaga `af895e58` failed with
  `StepFailed` in Submitted; with the binding back, the retry wrote `ManualRetryRequested Submitted ->
  Submitted`, a reset snapshot at version 1 with status Running, `MessageReceived` under a fresh id with the
  body, and the order completed at v5.

A verifier's run on `646dece` (`docker compose up -d --build --wait`; for the InvoiceFollowUpSaga case
`docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d --build --wait`, then chaos off with
`docker compose up -d --wait order-processing` before the retry) repeated the cases with an assertion script
run as `node c24v.mjs http://127.0.0.1:5080 retry <saga type> <correlation id>` (`ManualRetryRequested` from
the current state to the plan's from-state under the original id, the reset snapshot directly after it,
`MessageReceived` with a new id and a body `===` the original, the step re-run from the from-state, in
that order): a business-failed OrderSaga (`PaymentFailed` from Gathering, ended Failed v4 for the same
reason), a timed-out OrderSaga (re-ran from Submitted and completed, after which its post-shipment sagas
started), a `StepFailed` OrderSaga from the implementer's unbound window (completed at v5) and a timed-out
InvoiceFollowUpSaga caught under chaos and retried with chaos off. For that last one,
PostShipmentChoreography's rows by entry and message type were identical before and after in Postgres,
still one InvoiceDeliverySaga child, and 0 rows of another saga type under that correlation id since the
retry. Probes: retry-plan for
an unknown id 404; for a now Completed saga 200 with `"Only Failed or TimedOut sagas can be retried."`;
POST on it 409; POST for an unknown id 404. The compose API key was enough for both calls.

### Measured storage

On the MongoDB and Redis overlays (started as above), each provider ran with snapshots on (defaults), then
order-processing was recreated with snapshots off and new sagas were measured:

```bash
docker compose -p vsaga-mongo -f docker-compose.yml -f docker-compose.mongo.yml -f <override>.yml up -d --wait order-processing
docker compose -p vsaga-redis -f docker-compose.yml -f docker-compose.redis.yml -f <override>.yml up -d --wait order-processing
```

where the override sets `Orchestrator__RecordStateSnapshots: "false"` on `order-processing`. Only Completed
OrderSagas created inside each window count, because both volumes held earlier runs. MongoDB is the sum of
`$bsonSize` over a saga's `sagaEventLog` documents, by a mongosh script copied in with `docker cp` and run as
`MSYS_NO_PATHCONV=1 docker exec vsaga-mongo-mongo-1 mongosh --quiet vsaga /tmp/<script>.js`. Redis is
`MEMORY USAGE "{vsaga:vsaga}:log:<id>|OrderSaga" SAMPLES 0` (the same with default samples) and the summed
`LRANGE` element lengths, sent to `docker exec -i vsaga-redis-redis-1 redis-cli`. Per Completed OrderSaga
(4 handled messages):

| Measured | Run | Snapshots on | Snapshots off | Snapshot share |
| --- | --- | --- | --- | --- |
| MongoDB BSON bytes | after `507f661` (bodies not yet recorded) | 9 658.6 (19 docs, 10 sagas) | 6 425.9 (15 docs, 8 sagas) | 3 232.7; the 4 snapshot docs average 3 233.0, about 808 each |
| Redis `MEMORY USAGE` | after `507f661` | 12 016 (all 10 keys) | 7 288 (all 11 keys) | 4 728 |
| Redis list element bytes | after `507f661` | 11 052.0 | 6 751.0 | 4 301.0 |
| MongoDB BSON bytes | after `646dece` (bodies on every `MessageReceived`) | 10 039.6 (13 sagas) | 6 805.7 (13 sagas) | 3 233.9 |
| Redis `MEMORY USAGE` | after `646dece` | 12 528 (all 15 keys) | 8 312 (all 10 keys) | 4 216 |
| Redis list element bytes | after `646dece` | 11 593.9 | 7 293.1 | 4 300.8 |

The implementer's run of the first half agreed within a byte (MongoDB 9 658.7 and 6 426.0, Redis 12 016
and 7 288 on every key). The states themselves are 423 to 433 bytes; a snapshot costs about 808 BSON bytes
in MongoDB and about 1 076 list bytes in Redis, where the state's quotes are escaped inside the entry's
JSON. The four message bodies (359.8 chars in total) add about 380 BSON bytes in MongoDB and about 542
list element bytes in Redis; Redis's `MEMORY USAGE` moved by 1 024 (off) and 512 (on), allocator-rounded
steps for those same bytes. With snapshots off the Redis log key measured 7 288 bytes, the figure the
Redis provider's history recorded for a completed OrderSaga's 15-entry timeline. collStats for the whole
collection, which mixes every earlier run, stayed at an `avgObjSize` of 454 at both measurements of the
first half (464 and 465 in the second). `persistence.md` now quotes, per completed OrderSaga, 7 288 →
8 312 → 12 528 bytes for Redis and 6 426 → 6 806 → 10 040 BSON bytes for MongoDB (before bodies; with
bodies, snapshots off; the default), so snapshots cost 4 216 and 3 234 bytes.

### Live refresh and load errors (`c633055`), base stack, Chromium

On a `docker compose up -d --build --wait` stack. Sample sagas finish in about a second, so the verifier
held one mid-flight: a script polled the saga list every 80 ms and ran `docker pause vsaga-rabbitmq-1` as
soon as an OrderSaga was Running, and `docker unpause vsaga-rabbitmq-1` followed once its page was open.
The page went from 6 rows, Running, Gathering, v0 to 15 rows, Completed, v3 without a reload (an in-page
marker survived), with exactly two refreshes of the same five GETs and no lone timeline fetch. A finer run logged the summary patched first (`Failed v=3`)
and the timeline 240 ms later; the implementer's run caught the intermediate `Running AwaitingShipment v2`
at 552 ms.

Retrying a Failed OrderSaga from the page: "Retry accepted" at once; the summary showed version 5 about
250 ms before the timeline changed; then one refresh (five GETs) brought "Manual retry of ShipmentFailed" and
the re-run step, 1.18 s after the confirm click, and "At end" moved from version 3 to 5. Stopping the API
for about 14 s (`docker compose stop dashboard-api`, then `docker compose start dashboard-api`) showed
"Reconnecting to live updates…"; on reconnect, one negotiate 502, one 200, one refresh, banner gone. Switching tabs while the API is down fetches nothing, so the tab load errors were provoked with
Playwright request interception (503 on `/timeline` and `/map`): "Could not load the timeline. Try again"
and "Could not load the map. Try again" with nothing loaded, the warning variants ("Could not refresh the
timeline; it shows the entries as last loaded.") over stale content, and a hub reconnect cleared both
warnings without a click. `?tab=timeline&entry=24178&data=end` restored the tab, the focused row and the
open "At end" view after a reload. Console: no CSP violation and no warning; every error line falls in a
deliberate outage or an intercepted 503.

### The failed step in the page (`f28636e`), base stack, Chromium

Three runs: the implementer's, a verifier's on `4b27950` (the commit before the keyboard fix, see below)
and a re-verification on `f28636e`, each on a root stack from `docker compose up -d --build --wait` (once
without `--wait`; the UI rebuilt alone with `docker compose up -d --build --wait dashboard-web` after a
change) with Playwright MCP, and a
script listing Failed and TimedOut sagas with their retry plans to pick the cases. In the last:

- A business-failed OrderSaga opened its map on the failure entry with no `entry` in the URL ("As of entry
  #15 of 15: SagaFinalized"), and its timeline marked step 3 "PaymentFailed · succeeded Failed here"
  (`tl-step--failed-here`, the error border), with no "Re-run starts here", since the replayed entry is in
  the same step. Keyboard only: Tab ×3 to "Retry this saga", Enter, focus on Cancel; the prompt read
  "Re-run step 3 (PaymentFailed, Gathering) for this saga only?" / "Other services that consume
  PaymentFailed still receive it."; Shift+Tab to "Yes, retry", Enter. Six seconds later, same document:
  version 4, "Manual retry of PaymentFailed · requested", the re-run step now "Failed here", step 3
  unmarked, focus back on "Retry this saga". Network: retry-plan, POST 202, retry-plan again (the version
  moved), one refresh.
- A timed-out InvoiceFollowUpSaga opened its map on `TimeoutFired` (#6 of 7, not the last entry), marked
  step 1 "Re-run starts here" and step 2 "Timeout in AwaitingArchival · succeeded Failed here", and
  confirmed "Re-run step 1 (InvoiceIssued, Requested)". After the retry it was Completed/Archived v5, the
  retry row gone, focus on the `H1`, no marker left, and no further retry-plan request.
- A Completed saga had no banner, no retry row, no marker and no retry-plan request.
- A saga recorded before C23 showed "Retry this saga" disabled, `aria-describedby="retry-refusal"`, beside
  "This saga was recorded before vSaga stored the message of every step, so the ChildSagaFinished message
  that ran the step to re-run cannot be replayed.", and still marked its failed step.

Console: 0 errors, 0 warnings. In every run's scan of the volume only `BusinessFailure` and `TimedOut`
plans occurred.

## Problems found along the way

- **A paused broker stranded a retried saga.** The implementer's first live run paused RabbitMQ
  (`docker pause vsaga-rabbitmq-1`) during a retry: `RabbitMqTransport` threw `TaskCanceledException` from
  opening a channel, not a `MessageTransportPublishException`, so the restore did not run; the POST
  answered 500 after about 25 s and left OrderSaga `94af6b81` Running in Gathering at version 5 with no
  redrive, where the status guard refuses every further retry. The endpoint now restores on any publish
  exception and runs everything after the reset without the request's token. Re-checked on another
  `StepFailed` saga from the unbound window, OrderSaga `3bd90281` (Failed in Submitted, version 0), with
  the broker paused again (`docker unpause vsaga-rabbitmq-1` 26 s later): 502 after 26.2 s,
  `"restored":true`, back to Failed in Submitted at version 2 with both snapshots (reset at 1, restore at
  2). `94af6b81` was put back by hand to the state and status its first retry read, with a version-guarded
  SQL update (`WHERE Version = 5 AND Status = 0`), and ended Failed in Failed at version 6, retryable again.
  Wrapping channel-open failures in the RabbitMQ adapter is recorded as a follow-up in the design.
- **A restore that fails for another reason.** After the verifier and mutation runs on `646dece`, the
  commit was amended so that a restore failing for any reason other than a concurrent write (a store
  outage, a timeout, the saga deleted) is logged as an error and answered as a 502 saying the saga is
  Running in the step's from-state, instead of escaping as a bare 500; the amend added
  `Retry_WhenTheRestoreAfterAFailedPublishFailsForAnotherReason_Answers502SayingWhereTheSagaWasLeft` and
  rewrote stale comments about the old reset-to-start retry.
- **Consumers did not come back after the pause.** Retried again after the unpause, the redrive sat in
  `vsaga.saga.OrderSaga` (ready 1): order-processing had 0 consumers on every queue and did not re-attach
  them within a minute. `docker compose restart order-processing` did, and the redrive was consumed at
  once. This is the host's existing RabbitMQ consumer recovery, outside the slice, and was not changed.
- **Keyboard focus fell to the body around the retry prompt.** The verifier's run on `4b27950` found that
  opening the confirmation, Cancel and "Yes, retry" each removed the focused button and left
  `document.activeElement` on `<body>`, so the next Tab skipped the prompt for the Saga data bar. The
  amended commit moves focus itself: the prompt focuses Cancel (so a held Enter cannot run the retry it
  asked about), Cancel and the POST's answer return focus to "Retry this saga". The first rebuild still
  lost focus once in three keyboard retries, when the refresh for the re-running saga removed the row;
  the saga heading (`tabindex -1`) now takes focus then. Mutating `.focus()` to `.blur()` failed the three
  focus-move specs, and pointing the heading fallback at the Retry button (with the focus check disabled)
  failed the two heading specs.
- **A measurement artefact and a layout slip.** In the re-verification, Space presses read "no change"
  because each read ran before the render (an instrumented run showed the click at 2 705.8 ms and the
  prompt 6 ms later), and Space opens and closes the prompt. A long refusal reason wrapped the disabled button onto two lines;
  `flex: none` on the retry row's button fixed it before the commit.
- **A golden test that could not see the order.** The implementer's first snapshot mutation run found the
  golden-text test passing with the snapshot moved before the persist, since a fixed clock and an insert
  leave the serialised state unchanged by the persist. The committed test also checks the text after an
  update, whose bumped Version guards the order, and in the slice-wide run it fails under that mutation.
- **Pre-existing engine bugs found by tracing.** A failed event-log append stayed tracked by EF Core and
  was re-sent by every later save (`3eda0ea`), and every SignalR-pushed entry carried sequence number 0
  (`129115e`). Both were fixed before the snapshot entries depended on them.
- **The documentation commit's own errors.** Review of `61354b9` found four statements that did not match
  the code, corrected in `73cbce6`: `configuration.md` still said a failed redrive leaves the saga Running;
  `observability.md` said the engine never reads `StatePersisted` (it reads the payload length for the
  budget); the upgrade order (dashboard API before the engine hosts) was only in ADR 0007; and a dated note
  in `sub-saga-composition.md` claimed a retry started a second child, while the live run showed the saga
  had no child before the retry and one after.

## Where the build left the plan

- **The planner picks the failure by recency**, not by the plan's "last StepFailed, else ..." order, so a
  later business failure wins over an earlier technical failure a retry already fixed (mutation 13 pins
  it). A `DeliveryExhausted` with no message id (a deferred publish discarded after the step failed) is
  skipped. MongoDB's `$vsagaPayloadOmitted` counts as no body: replayed, it would deserialise into a
  message of default fields, so such a step is refused as too large to replay.
- **The restore runs after any publish exception**, not only `MessageTransportPublishException`, and the
  work after the reset ignores the request's token (the paused-broker problem above).
- **Focus on the map is its own class.** The blueprint routed it through `computeNodeStates`, which would
  have changed ordinary playback and lost to the failed style (review finding F8).
- **Fallback banners name no number**: stored sequence numbers are global on a shared store (1216 on the
  live stack) and appear nowhere on the timeline, so the design's "Entry 57 is not on the map yet" became
  "The selected entry is not on the map yet; showing the closest earlier entry".
- **The design doc was refined to match** in the fold table's two attachment rows (`50c0583`), the live
  refresh's follow-up condition (`c633055`) and the "Current data" aria-label while the end view reads
  Current (`25385b6`).
- **The per-saga budget marker** carries a `"budget"` key in place of `"limit"`, so the dashboard does not
  report a per-snapshot limit the state did not exceed. The reset recorder passes over budget markers when
  it lowers its cap.
- **The adapter header tests keep their `AllFour` names**, because docs reference them; their comments now
  say x-vsaga- header tests.

## Not verified

The design's verification list for this slice, and the mechanisms the slice added, also cover the
following; none of it was observed in the recorded live runs.

- A `DeliveryExhausted` retry. No saga had a `DeliveryExhausted` plan: the chaos window wrote 26
  `DeliveryExhausted` rows and none produced one.
- An organic `StepFailed`. Both stacks produced 0 `StepFailed` rows in the verifier's windows; the
  `StepFailed` retries ran on sagas made to fail by deleting a queue binding. The SPA runs saw no
  `StepFailed` saga, so its marker and confirmation for that kind were seen only in specs.
- The per-step data inspector, "At start" and "Compare" in a browser. "At end" was exercised in the
  live-refresh run and restored from the URL; the rest rests on the component specs.
- The per-saga budget, `StateSnapshotTimeout` and the containment of a failing snapshot append. No snapshot
  warning appeared in any live log, so these rest on the engine tests and mutations 3 to 5.
- Redaction for a caller without data. Every caller gets data until sign-in brings the `sagas.data`
  permission; the redaction seam and the payload-free push are covered by tests (mutations 8 and 9), and
  pushed frames were not inspected on the wire.
- The age window that turns "pending" into "missing" (or "not persisted"), and the 1500 ms follow-up
  timeline fetch after a push. The live-refresh runs saw only full five-GET refreshes and no lone timeline
  fetch; both rest on the specs (and SPA mutation 1 for the age window).
- The 409 when a retry's reset loses a race to another write, which only the endpoint tests and mutation 14
  reach; the live 409 was the status guard on a Completed saga.
- The refusal of a step whose MongoDB `MessageReceived` body is the `$vsagaPayloadOmitted` marker, covered
  by the planner tests only.
- A targeted retry, and the round-trip of its header, over the HTTP, Brighter, MassTransit and Wolverine
  transports. Every live retry ran on RabbitMQ (the MongoDB and Redis overlays were used for storage only);
  the other adapters rest on their header round-trip tests.
- The reset recorder's cap lowered by an engine marker, which only the `RetryStateSnapshotTests` cover.
- The amended restore-failure 502 on `3a817e6`, which no live run reached.
- The persistence samples other than the in-memory one.
- A screen reader. Accessible names and focus were read from the DOM and Playwright's accessibility
  snapshot.
