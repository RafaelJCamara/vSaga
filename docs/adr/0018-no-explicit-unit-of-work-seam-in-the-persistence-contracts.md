# ADR 0018: The persistence contracts keep their implicit per-scope unit of work; an explicit unit-of-work seam is not built, because it would not unlock the Redis-plus-Postgres split

**Status:** **Proposed** — 2026-11-09. Not accepted; only the maintainer accepts. This is an investigation
record: nothing here is implemented, and the recommendation is **not to build the seam**.
**Date:** 2026-11-09
**Relates to:** [`0002-redis-persistence-provider.md`](0002-redis-persistence-provider.md) (option D,
"Redis as an accelerator alongside Postgres", deferred "if the contracts ever gain an explicit
unit-of-work seam", and invalidation 2);
[`0001-mongodb-persistence-provider.md`](0001-mongodb-persistence-provider.md) (option F, registering two
providers side by side, and invalidation 5, the flush hook the contract does not require);
[`0003-persistence-contract-clauses.md`](0003-persistence-contract-clauses.md) (decision 2, the staged-row
lifecycle a seam would restate); [`0004-postgres-only-atomic-claim.md`](0004-postgres-only-atomic-claim.md)
and [`0009-sql-server-persistence-provider.md`](0009-sql-server-persistence-provider.md) (the claims a
second store would have to reproduce).
**Design:** [`../design/redis-persistence.md`](../design/redis-persistence.md) §11 (the deferral) and §4.1
(the unit of work as built).

**Recommendation in one paragraph.** ADR 0002 deferred the split on the premise that a unit-of-work seam
would make it expressible. Going through the seven contracts one at a time shows the premise is mostly
wrong: the one contract Redis could take over safely (timeouts) is already its own commit and needs no seam,
only a way to register two providers; the contracts that must be atomic with the snapshot (outbox,
business-key reservation) cannot be split across two stores with or without a seam, because a seam supplies
a place to call commit, not atomicity between two databases. What a seam would buy is real but internal:
a simpler contract text, no provider-specific "ambient flush" rule, and an in-memory provider that stages.
That is a clean-up, and it is paid for with a breaking change to a published package, five providers, the
orchestrator and the conformance suite. Do not build it now; write down the evidence that would change that.

---

## Context

### The unit of work today is a DI scope

There is no unit-of-work type in `VSaga.Abstractions`. `VSaga.Core` opens one DI scope per inbound message,
timeout or retry (`dotnet/src/VSaga.Core/Runtime/SagaRuntime.cs`, `HandleReceivedAsync`,
`HandleTimeoutAsync`, `RetryAsync`), resolves the stores from it, and each provider turns "the stores of one
scope" into a unit of work in its own way:

| Provider | The unit of work | Where staged outbox rows become durable |
| --- | --- | --- |
| EF Core (Postgres, SQL Server, SQLite) | the scoped `VSagaDbContext` change tracker, shared by every store (`EfCoreSagaOutboxStore.cs:8-33`) | **any** `SaveChangesAsync`: snapshot insert/update, event-log append (`EfCoreSagaEventLogStore.cs:32`), timeout schedule/cancel (`EfCoreSagaTimeoutStore.cs:21`, `:36`), `MarkDispatchedAsync`, the admin reset |
| MongoDB | the scoped `MongoSagaUnitOfWork` buffer (`MongoSagaUnitOfWork.cs:20-47`) | **only** the snapshot write, in one short transaction (`MongoPersistWrites.cs:37-60`) |
| Redis | the scoped `RedisSagaUnitOfWork` buffer (`RedisSagaUnitOfWork.cs:17-47`) | **only** the persist script |
| In-memory | none: a singleton store (`InMemory/ServiceCollectionExtensions.cs:16-26`) | immediately, at `EnqueueAsync` (`InMemorySagaStore.cs:381-398`); the provider declares `SupportsAtomicUnitOfWork == false` |

The Mongo and Redis buffers are near copies of each other (a list of staged rows, `Stage`, `Discard`,
`Committed`, `Clear`), written to mirror EF's change tracker (ADR 0001 option C).

### The engine's protocol already is "stage, commit, then drain or discard"

`SagaOrchestrator` stages one outbox row per deferred publish (`EnqueueOutboxRowsAsync`, `:859`, the
`EnqueueAsync` call at `:867`), commits by persisting the snapshot (`PersistAsync`, `:961-967`), and then
either drains (send, then `MarkDispatchedAsync`, `:894`) or, when the persist lost its race, discards
(`:917`, `:938`). The protocol has one ordering rule that exists only because of EF's ambient flush:
`DiscardDeferredPublishesAsync` must discard **before** it logs, "those append to the event log through the
same shared unit of work, so their `SaveChangesAsync` would commit the very outbox rows this discard exists
to suppress" (`:934-937`).

### The contract text is stronger than what is pinned

`ISagaOutboxStore.cs:40-62` says a staged row becomes durable "at the next successful commit in the same
unit of work, *whichever store issues it* — not only at a persist", and ADR 0003 decision 2 states the same.
That describes EF. The Mongo and Redis stores commit staged rows only in the snapshot write; their event-log
stores take no dependency on the unit of work, deliberately (ADR 0001, option C), so an append there does
not flush. The conformance suite pins only the intersection: a staged row becomes durable at the next
`UpdateAsync` or `InsertAsync` (`OutboxStoreConformanceTests.cs:171-213`), is not committed by a persist
that loses its race, and does not survive an abandoned unit of work
(`AtomicUnitOfWorkConformanceTests.cs:21-69`, derived only by providers declaring
`SupportsAtomicUnitOfWork`). No case asserts that an append or a schedule commits staged rows, and Mongo and
Redis would not satisfy one. The discrepancy is harmless today because the orchestrator orders its calls so
that it never depends on an append flushing; it is a reason the seam is attractive and it is a documentation
gap either way.

The only explicit unit of work in the repository is the test-side `IStoreUnitOfWork`
(`dotnet/tests/VSaga.Persistence.Conformance/IProviderFixture.cs:62-91`): `BeginAsync`, `CommitAsync`,
`AbandonAsync`, with every store handed out sharing it. It exists because the suite needed to talk about
the thing the production contracts leave implicit.

### What ADR 0002 deferred

Option D proposed Redis serving only `ISagaTimeoutStore`, `ISagaOutboxStore` and the business-key
reservation, with Postgres keeping the snapshot, the event log and the read side, and rejected it for two
reasons: DI cannot express it (`AddVSagaEfCore` and its siblings use plain `AddScoped`, so two providers
resolve last-one-wins per interface, silently) and "requirement 1's atomicity would then span two stores".
It deferred it "if the contracts ever gain an explicit unit-of-work seam". Hosts today choose exactly one
provider with the `Persistence:Provider` switch (`dotnet/src/VSaga.Dashboard.Api/Program.cs:45-83`).

---

## What a split would actually need, contract by contract

For each contract: is it atomic with the snapshot today, could a second store hold it, and does a seam
change the answer? Grounded in where the orchestrator calls it.

| Contract | Atomic with the snapshot today? | Could it live in Redis next to a Postgres snapshot? | Does a seam change that? |
| --- | --- | --- | --- |
| Snapshot and business-key reservation (`ISagaSnapshotStore`) | Yes: one row, and the partial unique index adjudicates the double-initiate race before the step runs (ADR 0001 property 5) | No. The reservation commits with the row or it is not a reservation. | No |
| Outbox (`ISagaOutboxStore`) | Yes, by staging and the persist's commit (ADR 0001 property 1) | Not as the durable copy. Commit Redis first and a crash leaves a durable row for a transition that never committed (a phantom publish the poller republishes); commit Postgres first and a crash loses the publish. Both are the dual-write window the outbox exists to close. Only a rebuildable claim *index* over rows that stay in Postgres avoids it. | A seam gives the commit a name, not atomicity between two databases. For the index, an after-commit hook (option C) is what is needed, plus a reconcile sweep. |
| Timeouts (`ISagaTimeoutStore`) | **No.** `CancelAsync` and `ScheduleAsync` run before the persist and each is its own commit (`SagaOrchestrator.cs:291`, `:758-763`); a timeout that fires for a state the saga has left is ignored (`HandleTimeoutAsync` checks `CurrentState` and `Status`, `:261`) | Yes, in principle | No. The engine already treats it as a separate store; only registration blocks it. |
| Event log (`ISagaEventLogStore`) | No: durable before and independently of the persist, by design (ADR 0001 property 2) | Not on ADR 0002's Tier A, where a lost acknowledged append duplicates a business command | No |
| Topology (`IServiceTopologyStore`) | No | Yes, pointlessly | No |
| Summary reader and admin (`ISagaSummaryReader`, `ISagaAdminStore`) | Derived from, and a guarded write on, the snapshot row | No | No |

So of option D's three contracts, **one (timeouts) can be split today with no seam, one (the
reservation) cannot be split at all, and one (the outbox) cannot be split by any seam.** The motivation
for the seam, as the deferral states it, does not survive the table.

What remains is a performance argument ("Redis claims in one round trip"), and nothing here measures
that Postgres is the bottleneck: the claim is already one `FOR UPDATE SKIP LOCKED` statement (ADR 0004),
the polling queries are indexed, and the SQL Server provider has the same shape (ADR 0009).

---

## Options weighed

### A. Do nothing — keep the implicit per-scope unit of work — **proposed**

*Pros.* No contract change, no breaking change to a lockstep-versioned published package, no risk to the
outbox and redelivery paths that were live-verified against four providers. The orchestrator's discard-
before-log ordering is already correct for all of them.

*Cons.* The contract prose is stronger than the suite and than two providers (above), and a future
provider author has to infer the model from three implementations. In-memory keeps its documented crash
gap. A second composition use case (below) stays blocked.

### B. An explicit commit seam: `ISagaUnitOfWork { CommitAsync; AbandonAsync }`, stores stage only

Promote the test-side `IStoreUnitOfWork` into `VSaga.Abstractions` as a Scoped service. `InsertAsync` and
`UpdateAsync` stop being committers; the orchestrator calls `CommitAsync` where `PersistAsync` is now.

*What it would buy.*
1. The staged-row lifecycle becomes three method names instead of the prose at `ISagaOutboxStore.cs:40-62`.
2. Append and schedule can never flush staged rows on any provider, which removes the discard-before-log
   ordering rule and structurally fixes the defect ADR 0003 decision 2 fixed by ordering.
3. In-memory can stage and drop its `SupportsAtomicUnitOfWork == false` and the gated suite flag.
4. A named place to hang an after-commit hook (option C), if one is ever wanted.

*What it would cost, specifically.*
- **A breaking change to `VSaga.Abstractions`**, versioned in lockstep with everything (`Directory.Build.props:21`).
  Any external `ISagaSnapshotStore<T>` or `ISagaOutboxStore` implementer breaks at compile time, as ADR
  0003's fix to `ClaimDueAsync` did.
- **Concurrency and version semantics move.** Today `UpdateAsync` throws `SagaConcurrencyException`, bumps
  `state.Version` in place before serialising and restores it on every throw path
  (`ISagaSnapshotStore.cs:81-106`), because a timeout persists the same live object twice and `FindAsync`
  reads the version back from the blob. If `UpdateAsync` only stages, the bump, the version guard, the
  `SagaAlreadyExistsException` for a moved business key and the restore on failure all move to
  `CommitAsync`, which commits several things. That is the most delicate invariant in the contracts, and it
  is the one the suite's `StagedRow_IsNotCommittedByAPersistThatLosesItsRace` case and the engine's
  race-loss paths rest on.
- **EF's behaviour changes**: the event log's `AppendAsync` would stop committing staged rows. ADR 0003
  decision 2 would be superseded and the EF stores re-tested against it.
- **Five providers and the orchestrator change**: the EF Core stores (which also serve SQL Server and
  SQLite), `InMemorySagaStore` and its snapshot store, the Mongo stores and `MongoPersistWrites`, the Redis
  stores and the persist scripts, `SagaOrchestrator`/`SagaContext`, and `VSaga.Testing`'s harness. 27 files in `VSaga.Core.Tests` and six in `VSaga.Dashboard.Api.Tests` name
  the timeout, outbox or snapshot contracts; those that implement or wrap them change.
- **Live verification of every provider** under `AGENTS.md` rule 3, including the kill-the-host outbox
  recovery check for each compose overlay (Postgres, Mongo, Redis, SQL Server).
- A transaction must still never span user step I/O (ADR 0001 option B): the seam has to keep "stage, then
  one short commit at persist". Nothing about B prevents the mistake; it would have to be written into the
  contract.

*Migration of existing data.* None: no stored shape changes, and in-flight outbox rows, timeouts and
snapshots are untouched. The migration is of **code**: external implementers, and a rolling deploy in
which old and new binaries share a database is unaffected because the database does not change.

**Not recommended** now: every benefit is internal, and the cost is a breaking change across the stack.

### C. An additive after-commit hook (`ISagaCommitObserver`), providers invoke it after a successful persist

*Pros.* Non-breaking if optional. The one mechanism that expresses "Redis as a rebuildable claim index over
Postgres outbox rows": Postgres commits the row, then the observer pushes its id to a Redis sorted set; a
reconcile sweep re-indexes rows the observer missed on a crash; the claim reads the sorted set and
confirms against Postgres.

*Cons.* A second moving part with its own crash window (hence the sweep), a new public surface, and a
benefit that has not been measured. It would also make Postgres the only authoritative outbox, so Redis's
"best claim implementation" (ADR 0002 §3) is spent on an index.

**Rejected for now**, not dismissed: it is the shape to start from if the evidence below ever appears.

### D. Registration-level composition with a validated atomic set (no contract change)

`AddVSagaPersistence(p => p.Use(...))` style: separate contracts can come from separate providers, and the
registration refuses any combination that splits the atomic set (snapshot, outbox, event log, summary
reader and admin must share one provider). Timeouts and topology may differ. This makes the one splittable
contract splittable, and turns today's silent last-wins into an error.

*Pros.* No seam, no contract change, and it fixes a real hazard independent of Redis: two `AddVSagaX`
calls in one host silently resolve per interface by call order (ADR 0001 option F, ADR 0002 option D).

*Cons.* A new public configuration surface (so an ADR of its own), and it enables a configuration (Redis
timeouts, Postgres for the rest) whose benefit is unmeasured and whose failure mode (Redis restarts and
loses Tier A's window of timeouts; a stranded timeout is the failure `BackfillStrandedTimeouts` exists for)
is worse than Postgres's.

**Not recommended** as a feature without a measured need. The cheaper piece of it, making a second
`AddVSaga*` provider registration fail loudly instead of silently, is worth considering on its own (see
Open questions).

### E. Make two stores atomic (two-phase commit, XA, a distributed outbox)

**Rejected.** Neither Redis nor the drivers in use offer a prepare phase, and a coordinator would be a
new durable component whose loss is its own outage. ADR 0001 invalidation 7 already puts it out of scope.

### F. Fold the duplicated Mongo/Redis staging buffers into one shared type

*Pros.* The two classes are line-for-line the same logic over different row types.

*Cons.* Not a contract change, so not what this ADR is about. It needs a shared home both provider
packages depend on without a new package (`VSaga.Abstractions` is the only candidate and would then carry
an implementation detail). Left as a possible refactor.

---

## Decision

**Do not add a unit-of-work seam to the persistence contracts.** The seven contracts keep their implicit
per-scope unit of work, and `IStoreUnitOfWork` stays a test-suite abstraction.

**Record the corrected premise.** The Redis-plus-Postgres split ADR 0002 deferred is not blocked by the
missing seam. Of its three contracts, timeouts need only registration (option D), the business-key
reservation cannot leave the snapshot's store, and the outbox cannot be split across stores by any seam.
ADR 0002's invalidation 2 ("the contracts gain a unit-of-work seam, making option D expressible") is
therefore a weaker trigger than it reads, and this ADR should be linked from it by a one-line pointer when
accepted. (Not done here: an accepted ADR is not edited by a proposing one.)

**What would justify reopening** (any one):
1. A measured Postgres bottleneck on outbox or timeout polling or claiming, at a stated rate, that an
   index or `SKIP LOCKED` tuning does not fix. That is the only thing that makes the split worth its
   failure modes, and then option C (an after-commit index with a reconcile sweep) is the shape, not B.
2. A **second consumer** of "a persist committed" (not the dashboard's poller, which works across
   processes): a push feed, a cache invalidator, a metrics projection. Two uses make a hook, and then B's
   named commit point is cheaper than it looks.
3. A new provider whose atomic unit cannot be built from a change-tracker-shaped buffer, so that the
   implicit model stops being expressible.
4. The contract prose and the providers disagreeing causes a defect (rather than being a documentation
   gap): for instance an engine change that starts to rely on an append flushing staged rows.

### The conformance changes, if B were built anyway

- `IStoreUnitOfWork` moves from `IProviderFixture.cs` into `VSaga.Abstractions` as the production service
  and the fixture adapts to it. `AtomicUnitOfWorkConformanceTests` and `SupportsAtomicUnitOfWork` go: every
  provider, in-memory included, stages.
- New cases: a staged row is invisible to every other unit of work until `CommitAsync` on **every**
  provider; an `AppendAsync`, `ScheduleAsync` or `CancelAsync` never commits it (this restates, and
  tightens, what `OutboxStoreConformanceTests.cs:171-213` pins); `CommitAsync` that throws leaves the rows
  staged and a later `DiscardPendingAsync` finds them; `CommitAsync` after `AbandonAsync` is refused.
- Clause 1 is re-pinned: where `SagaConcurrencyException`, `SagaAlreadyExistsException` and the
  `state.Version` bump and restore happen when the stores only stage. The existing snapshot cases
  (`Update_*`, the rival-write cases in `SnapshotStoreConformanceTests`) are rewritten around the commit.
- `ConformanceCoverage` drops its second gate; the provider test projects (EF Core, SQLite, in-memory,
  Mongo, Redis, SQL Server) all change in step.
- The engine-level tests that assert on the discard-before-log ordering
  (`SagaOrchestratorConcurrencyRedeliveryTests`, `ChildSagaFinishedRecoveryTests`) are re-read for what
  they were protecting.

---

## Consequences

### Positive

- No breaking change to `VSaga.Abstractions`, no re-verification of four providers' outbox recovery.
- The deferral in ADR 0002 gets a precise answer instead of an open condition, and the work that *would*
  be needed for a split is on record: registration-level composition, an after-commit index, and a
  reconcile sweep, none of them a unit-of-work seam.
- A documentation gap is named: `ISagaOutboxStore.cs:40-62` and ADR 0003 decision 2 describe EF's ambient
  flush as the contract while Mongo and Redis do not do it and no test requires it.

### Negative

- The implicit model stays implicit; a provider author still reads three implementations and a test-side
  abstraction to learn it.
- The in-memory provider keeps its crash gap (dev and test only).
- The split people ask about stays unbuilt, and this ADR argues it should.

### Neutral

- `Persistence:Provider` stays "exactly one provider per host".
- Nothing here changes a stored format, a wire format or a public option.

---

## What would invalidate this decision later

1. Evidence of the kind listed under "What would justify reopening".
2. **`VSaga.Core` mints deterministic MessageIds for deferred publishes** (ADR 0002 Q6). Then step
   re-execution is idempotent end to end, Tier A's acknowledged-write loss stops duplicating commands, and
   the event log (and so more of the split) becomes safe to place on Redis. That changes the table above
   more than any seam would.
3. **Retention becomes expressible** (ADR 0002 invalidation 6). A bounded Redis dataset is a different
   proposition as a secondary store.
4. **The staged-row lifecycle is pinned the other way** (ADR 0001 invalidation 5): if the contract is
   rewritten to require an append to flush on every provider, Mongo and Redis need the explicit hook after
   all, and B's commit point is what provides it.

---

## Open questions

1. **Should the contract prose be corrected now**, whatever this ADR decides? `ISagaOutboxStore.cs:40-62`
   and ADR 0003 decision 2 say "whichever store issues it"; Mongo and Redis commit only at the snapshot
   write. The honest fix is either to weaken the text to what the suite pins, or to add the missing cases
   and make Mongo and Redis conform. This is a documentation or small-fix question, not a feature, and it
   is independent of A versus B.
2. **Should a second `AddVSagaEfCore`/`AddVSagaMongoDb`/`AddVSagaRedis` registration fail loudly** rather
   than resolve last-one-wins by call order? It is option D's cheap half: a guard, not a composition
   feature. Whether it needs an ADR is the maintainer's call (it adds an exception to a registration path).
3. **Is there a measured claim or polling problem behind the original interest in the split?** If so, what
   rate and which store; that number is what would move this ADR from "no" to "option C".
4. **Not verified.** I read the orchestrator, the four providers' units of work and the suite; I did not
   run any provider, and I did not measure Postgres or Redis claim cost. Statements about what Mongo and
   Redis do on an `AppendAsync` come from reading their stores (the event-log stores take no unit-of-work
   dependency), not from a test, since no test asserts it.
