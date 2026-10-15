# Design: stuck-saga detection

**Status: slice 1 of §8 is built** (the `Statuses` and `UpdatedBefore` filter members, in all five readers,
with three conformance cases); slices 2 to 6 are not. Scope, definition and delivery were decided with the
maintainer on 2026-10-15 (§1); the rest is a proposal for review. This closes the "stuck-saga alerting" item that
[`production-readiness.md`](production-readiness.md) §1 named as a real gap and kept out of scope.

---

## 1. Decisions already taken

| Question | Decision | Rejected |
| --- | --- | --- |
| How far does it go? | **Detect and surface.** No automatic recovery and no new write path. An operator recovers a saga with the dashboard's existing retry. | Auto-rescue (a wrong guess compensates a saga that was fine); a one-click re-arm (needs a new admin-store write and an access scope) |
| What is "stuck"? | **A saga that is `Running` or `Compensating` and has not been updated for longer than a threshold,** classified by cause (§3). The threshold is per saga type, with a global default. | Overdue timeout only (misses a lost timeout and a state that never had one); a per-state "expected duration" declared in the DSL (every saga author must adopt it) |
| Where does it run, and how does an alert leave the process? | **A hosted service in `VSaga.Core`** that sweeps the store, emits an OpenTelemetry gauge and a warning log, and calls an optional `ISagaStuckNotifier`. It works without the dashboard. | Dashboard only (nothing alerts anyone when nobody has the page open); a metric only (no way to see which sagas) |

## 2. What is stuck today, and why nothing notices

Every awaiting state of the samples has a `WithTimeout`, so a lost *reply* is rescued. These are the paths
that are not rescued, each read from the engine:

1. **A timeout whose step throws, or whose process dies mid-step.** `HandleTimeoutAsync` claims the row
   with a version-checked persist *before* it runs the saga's timeout step
   (`SagaOrchestrator.cs:272`), so the row is already `Fired`. If the step then throws, the dispatcher logs
   an error (the `catch` around `HandleTimeoutAsync` in `SagaTimeoutDispatcherHostedService`) and nothing re-arms it. The saga stays
   `Running` in the same state, with no timeout left, forever.
2. **A saga in a state that declares no `WithTimeout`.** It waits for a message that may never come. That
   is sometimes by design (a human approval) and sometimes an oversight; only a threshold separates the two.
3. **A deferred publish that fails after the commit.** The drain failure is caught and recorded as a
   `DeliveryExhausted` entry and the saga is "left `Running` for its own state timeout to rescue"
   (`saga-dsl.md`). If its state has no timeout, nothing does.
4. **A `Compensating` saga whose process died** between two compensation steps.
5. **The timeout dispatcher not running at all**: no process hosts the saga type, or the hosting service
   is down. Every timeout of that type goes overdue together.

Cases 1, 3 and 4 were seen as 60 sagas stuck `Running` for good
([`timeout-coverage-every-awaiting-state.md`](../history/timeout-coverage-every-awaiting-state.md)); that
pass found them by looking at the database, not because anything flagged them.

## 3. The definition

A sweep reads, per saga type the process hosts, every instance with `Status` in `{Running, Compensating}`
whose `UpdatedAtUtc` is older than a floor, then applies each instance's exact threshold:

| Cause | Instance | Counts as stuck when idle longer than |
| --- | --- | --- |
| `TimeoutOverdue` | `Running`, and `definition.GetTimeout(CurrentState)` is set | that timeout **plus** `TimeoutGrace` |
| `IdleWithoutTimeout` | `Running`, and its state declares no timeout | the saga type's idle threshold |
| `CompensationIdle` | `Compensating` | the saga type's idle threshold |

**Why `UpdatedAtUtc` is enough, and the timeout store need not be read.** A timeout that fires persists the
saga first (the claim above), so a healthy timeout moves `UpdatedAtUtc` forward. A `Running` saga idle for
longer than its state's timeout plus the grace therefore had its timeout lost, whatever the reason, and the
store need not say so. `ISagaTimeoutStore` has no read method today, and this design does not add one. The
definition is in the process (`GetTimeout` is on `ISagaDefinition`), which is why the sweep belongs in the
engine host and not in the dashboard, whose process is not required to register saga definitions.

**A state's timeout longer than the idle threshold is never flagged early.** Its threshold is the timeout
plus the grace; the idle threshold applies only where no timeout exists. A self-transition (a join that keeps
waiting) moves `UpdatedAtUtc` without re-arming the timeout, so it can only delay a flag, never cause a false
one.

**Not covered, named so it is not mistaken for an oversight:**
- A saga with continuing traffic that never reaches a terminal state (a loop). It is busy, not idle.
- A stuck **outbox** row. That is a different table with its own age; a sweep over it is a separate item.
- A saga in `Failed`, `TimedOut` or `Cancelled`. Those are terminal; the dashboard already lists them.

## 4. Options

`SagaStuckOptions`, set through `AddVSagaEngine(b => b.ConfigureStuckDetection(o => ...))` beside
`ConfigureOutbox`:

| Option | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | **Opt-in.** An upgrade adds no polling query and no log line on its own. |
| `SweepInterval` | `00:01:00` | Time between sweeps. |
| `IdleThreshold` | `01:00:00` | Idle time that counts as stuck where no timeout applies. Deliberately long: a human approval is not stuck. |
| `IdleThresholdBySagaType` | empty | Overrides by saga type name (ordinal). |
| `TimeoutGrace` | `00:02:00` | Added to a state's timeout. Covers the dispatcher's 5 s poll, the step's own retries and a clock difference between replicas. |
| `ExcludedSagaTypes` | empty | Types never swept, for a saga that waits for days by design. |
| `MaxReportedPerSweep` | `200` | Cap per saga type on instances listed and notified; the gauge still counts every one (§5.1). |

The defaults are a proposal. The two that matter most, `IdleThreshold` and `TimeoutGrace`, are chosen to
prefer a missed flag over a false one, since a flag on a healthy saga teaches operators to ignore the signal.

## 5. Outputs

### 5.1 The gauge

`vsaga.saga.stuck`, an `ObservableGauge<long>` in `VSagaDiagnostics.Meter`, tagged `saga.type` and `cause`.
This is the gauge `VSagaDiagnostics.cs` already names as the right shape for "how many are in this state"
and records as a follow-up, instead of an `UpDownCounter` that a restart or a second replica would
desynchronise. The comment there says the obstacle is scoped-store access from a meter callback. The
sweep removes it: the hosted service queries the store in its own scope, keeps the last result in a field,
and the callback only reads that field. A restart loses nothing but the previous reading.

Each replica reports its own reading of the same store, so a sum across replicas multiplies the count by
the replica count. `docs/observability.md` will say to aggregate with `max`, not `sum`, which is correct
because every replica of a type reports the same population.

### 5.2 The log

A `Warning` the first time a saga is seen stuck, naming the type, correlation id, state, cause, idle time and
the threshold it crossed, and an `Information` when a saga that was reported leaves the stuck set. The hosted
service keeps the set of reported keys in memory, bounded by `MaxReportedPerSweep` per type. A restart
re-reports what is still stuck, which is acceptable and says so in the docs.

### 5.3 The notifier

```csharp
// VSaga.Abstractions/Notifications
public interface ISagaStuckNotifier
{
    Task SagaStuckAsync(StuckSaga saga, CancellationToken cancellationToken = default);
    Task SagaRecoveredAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default);
}

public sealed record StuckSaga(string SagaType, Guid CorrelationId, string CurrentState, SagaStatus Status,
    StuckCause Cause, TimeSpan IdleFor, TimeSpan Threshold, DateTimeOffset UpdatedAtUtc, int Version);
```

The default is a no-op, as `ISagaChangeNotifier`'s is (`NullSagaChangeNotifier`). A webhook, a pager or a
chat message is the host's implementation; vSaga ships none. A notifier that throws is caught and logged, and
never fails the sweep. With several replicas, each notifies for the same saga, so a receiver de-duplicates on
`(SagaType, CorrelationId, Version)`, which is why `Version` is on the record. Electing a single reporting
replica is a distributed-lock problem this design does not take on.

### 5.4 The dashboard

The dashboard API does not host saga definitions and so cannot classify. It gets the cause-free half:

- the saga list gains an **idle for** filter (`Running` or `Compensating`, not updated for N), served by the
  same two new filter members the sweep uses (§6), and a count beside the list's status tabs;
- scoped callers see only the saga types their access names, through `ScopedSagaLister` as every list does.

The cause stays in the log, the gauge and the notifier. Writing a `SagaStuck` entry to the event log would
put the cause on the timeline, but it duplicates per replica, adds an entry type with the old-dashboard
numeric-enum hazard `StatePersisted` had (`observability.md`, "Upgrade order"), and is not needed to ship the
signal. It is recorded as an open question (§9).

## 6. The persistence contract change

The sweep needs "these statuses, idle since before T, oldest first". `SagaListFilter` has neither a
multi-status filter nor an upper bound on `UpdatedAtUtc`. Two additive members, in the way `UpdatedSince` and
`SagaTypes` were added:

- `Statuses`: `IReadOnlyCollection<SagaStatus>?`. Null puts no limit; empty matches nothing. When `Status` is
  also set, both must hold.
- `UpdatedBefore`: `DateTimeOffset?`, **strictly** less than, to the exact tick, mirroring `UpdatedSince`'s
  strictness and each provider's existing timestamp handling (`MongoTimestamps`, `RedisTimestamps`).

Both must be applied before the count, the sort and the page, so `TotalCount` is over the filtered set. The
oldest-first order is `SortBy = UpdatedAt, SortDescending = false`, which exists.

This is a contract addition on a packable interface, so a third-party reader that ignores the new members
returns wrong rows; the same hazard `SagaTypes` documented, and the answer is the same: the conformance suite
fails such a reader. The new cases belong in the suite, run on all five providers.

Index fit, checked in the code:

- **EF Core**: `(SagaType, UpdatedAtUtc)`, `(SagaType, Status)` and `(Status, UpdatedAtUtc)` already exist
  (`VSagaDbContext.cs:106-119`). No migration.
- **Redis**: one sorted set per status scored by updated-microseconds, so with no other filter an idle range is
  a score-bounded read per status; no scan. **With a saga type as well (the sweep's own query) it is a
  server-side intersection of the status bucket and the type's index per status**, which costs the smaller of
  the two sets, not the idle rows alone, and applies the bounds on the client. Correct and bounded, but not
  free on a large `Running` bucket; the cheaper shape (a bounded range of the type's index, then a status
  membership check of just those rows) is not built, and is worth measuring in slice 2 before it is.
- **MongoDB**: `ix_status_updatedDesc` and `ix_sagaType_updated` already serve both shapes; no new index.
- **In-memory**: a linear filter.

## 7. The sweep

Per tick, in a fresh DI scope (EF's store is scoped, as in the timeout dispatcher):

1. For each saga type the process has a runtime for, not excluded: compute its floor, the smallest threshold
   any of its states can have (the idle threshold, or its shortest timeout plus grace).
2. `ListAsync` with `SagaType`, `Statuses = {Running, Compensating}`, `UpdatedBefore = now - floor`, oldest
   first, in pages up to `MaxReportedPerSweep`.
3. For each row, resolve its exact threshold from its state (§3). Drop the rows still inside it. Classify the rest.
4. Publish the cache the gauge reads, diff against the reported set, and log and notify the new and the recovered.

One query per hosted type per tick, scoped to that type, because a process must only report what it can
classify: the same rule `ClaimDueAsync`'s `sagaTypes` parameter already holds for timeouts. A process that
hosts no saga types does nothing, as the timeout dispatcher does.

A sweep that throws is logged and the next tick runs; the previous cache is kept so the gauge does not drop to
zero during a database outage, which would read as "all clear". It is marked stale after three failed
sweeps and the gauge stops reporting, so a dead sweep is not mistaken for a healthy store.

## 8. Slices

One commit each, per this repo's habit; each leaves the build green.

1. **Contract.** `Statuses` and `UpdatedBefore` on `SagaListFilter`, in the five readers, with the conformance
   cases (including the strict bound, the empty `Statuses`, and composition with `SagaTypes` and paging).
   `ScopedSagaLister` passes them through.
2. **Detection.** `SagaStuckOptions`, `ConfigureStuckDetection`, the classifier as a pure function over
   `(summary, definition, options, now)` with its own unit tests, and the hosted service with the gauge and log.
3. **Notifier.** `ISagaStuckNotifier`, `StuckSaga`, `StuckCause`, the no-op default, and the diff of the reported set.
4. **Dashboard API.** The `idleFor` filter on the saga list, scope-checked.
5. **Dashboard SPA.** The idle filter and count, with the accessibility and tour conventions the other
   filters follow. Separable: slices 1 to 4 are useful without it.
6. **Docs.** `observability.md`, `configuration.md`, `dashboard-guide.md`, `persistence-contracts.md`, and this
   file's status line.

## 9. Open questions

- **A `SagaStuck` timeline entry** (§5.4): the cause on the timeline, against a new entry type and a
  duplicate per replica. Leaning to leave it out of v1.
- **A per-state idle override**, for one slow state in a saga type. The per-type override is enough for now.
- **Whether `Enabled` should default to `true`** once the defaults have run against a real load. Opt-in is
  the safe first release.

## 10. Verification plan

- **Classifier**: a table-driven unit test per cause and per boundary (idle equal to the threshold is not
  stuck; one tick over is), the longer-than-idle-threshold timeout, a state with no definition, and a type
  override.
- **Conformance**: the new filter cases on InMemory, SQLite, Postgres, Redis and MongoDB, each mutated (drop
  the `UpdatedBefore` predicate, flip its strictness, ignore `Statuses`) and shown red.
- **Hosted service**: a fake `TimeProvider` and the in-memory store; a saga that goes idle is reported once,
  reported again only after it recovers and goes idle, and a throwing notifier does not stop the sweep.
- **Live**, on the compose stack, for the one path that has no unit test's honesty: make a timeout step throw
  (path 1 of §2), confirm the saga stays `Running` with no pending timeout, then confirm the log line, the
  gauge and the notifier all fire after the threshold, and that the dashboard's retry clears it.
