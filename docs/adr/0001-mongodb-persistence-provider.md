# ADR 0001: MongoDB as a persistence provider

**Status:** **Accepted** — 2026-09-26, by the maintainer. Not built. Accepting the ADR settles the *what*: a `VSaga.Persistence.MongoDB` provider on the native driver, at the tier and with the prerequisites argued below. The plan's blocking questions Q2–Q4 (replica set, connection-string override, bootstrap/health model) are decided in the plan, not here, and remain open until Stage 1 starts; its Stage 0 prerequisite landed in full on 2026-09-26 (ADR 0003).
**Date:** 2026-09-25
**Supersedes:** nothing. **Superseded by:** nothing.
**Implementation plan:** [`../design/mongodb-persistence.md`](../design/mongodb-persistence.md)

> This is the first architecture decision record in this repository. There was no prior ADR convention
> (no file mentioned "ADR", "architecture decision" or "decision record"); the closest existing
> categories are [`docs/design/`](../design/) for forward-looking design records and
> [`docs/history/`](../history/) for post-hoc narrative. Keeping `docs/adr/` as a third category was
> ratified on 2026-09-25 — see [`0003`](0003-persistence-contract-clauses.md) decision 5.

---

## Context

vSaga ships two persistence providers today (three since 2026-09-26, when the Redis provider of ADR 0002
landed; the count below is as of this decision — [`docs/persistence.md`](../persistence.md)), both
implementing the same seven contracts in `dotnet/src/VSaga.Abstractions/Persistence/`:
`ISagaSnapshotStore<TState>`, `ISagaEventLogStore`, `ISagaOutboxStore`, `ISagaTimeoutStore`,
`ISagaAdminStore`, `ISagaSummaryReader`, and `IServiceTopologyStore`. The reference implementation is EF
Core over Postgres (`dotnet/src/VSaga.Persistence.EFCore/`); the other is a dev/test in-memory store
that documents its own residual outbox gap (`InMemorySagaStore.cs:332-337`,
`docs/design/production-readiness.md` §4.4).

We want a third: MongoDB, for teams already standardised on it who would otherwise have to run Postgres
solely for vSaga.

### What the engine actually requires of a persistence provider

This is the part that makes the decision non-obvious. Seven properties, most of them recorded only in
implementation comments rather than in the contracts themselves:

1. **Outbox rows and the snapshot commit together.** `ISagaOutboxStore.EnqueueAsync` deliberately does
   not commit; the snapshot store's write is what commits both (`ISagaOutboxStore.cs:41-49`). An
   implementation that commits early "would reopen exactly the dual-write window the outbox exists to
   close" — and `SagaOrchestrator.cs:734-745` records a **live repro** of that bug on the in-memory
   provider.
2. **Event-log appends are durable *before and independently of* the persist.** The redelivery safety net
   republishes under the same MessageId and relies on a durable log entry having already landed, so the
   dedupe check recognises the redelivered copy (`SagaOrchestrator.cs:85-93` → `:398`).
3. **Optimistic concurrency mutates `state.Version` in place**, before serialisation, because
   `HandleTimeoutAsync` persists the same live object twice (`SagaOrchestrator.cs:240`, `:289` → `:339`)
   and `FindAsync` reads the version back out of the serialised blob
   (`EfCoreSagaSnapshotStore.cs:58-61`).
4. **Event-log reads are ordered**, because `GetVisitedStatesAsync` derives the entire compensation set
   from timeline order (`SagaOrchestrator.cs:902-911`) — microseconds after appending to it (`:585` →
   `:589`).
5. **A partial unique index on `(SagaType, BusinessKey)`** adjudicates the concurrent-double-initiate
   race *before* either side's step runs (`VSagaDbContext.cs:126-128`,
   `docs/design/production-readiness.md` §5.2).
6. **Atomic, ordered, non-blocking claim** for timeouts and outbox rows — on Postgres, an
   `UPDATE … FOR UPDATE SKIP LOCKED … RETURNING` (`EfCoreSagaTimeoutStore.cs:87-99`).
7. **A stable, total-ordered, `UpdatedSince`-filterable list**, because the dashboard's change poller
   pages through it once a second and a reshuffled tie group silently drops an update
   (`SagaChangePollingService.cs`).

Properties 1 and 2 are **opposite transactional requirements on adjacent stores**. That single fact
shapes the whole decision.

### The state of the existing providers

The copy-paste strategy that produced the second provider has already produced eight verified divergences
between the two — unordered claim results in-memory, a missing `Version` patch in
`InMemorySagaStore.ResetStateAsync`, a case-**sensitive** `Search` in EF against a contract that says
otherwise, and more (design doc §3.1). None were caught, because **there is no shared conformance
suite**. A third hand-written provider would compound a proven failure mode.

---

## Decision drivers

- **Contract fidelity over elegance.** A third provider whose behaviour differs from the reference in any
  way the engine can observe is worse than no third provider.
- **Silent failure is the enemy.** Nearly every hazard identified here produces a *plausible wrong
  answer*, not an error: a short compensation set, a dropped live update, a phantom publish, a page of
  the wrong sagas.
- **The repo's conventions are binding** — one logical change per commit with mutation testing, zero
  warnings, live verification for anything outbox- or timeout-related, overlay compose files rather than
  base-file edits, lockstep package versioning (`CONTRIBUTING.md`, `Directory.Build.props:21`).
- **The dashboard's premise** is that it works against provider-agnostic contracts
  (`docs/dashboard.md:6-8`). A provider that forces provider-specific code into `VSaga.Dashboard.Api`
  falsifies that sentence.
- **Operational honesty.** A new prerequisite and a new failure surface must not degrade the existing
  stack's startup resilience (`Dashboard.Api/Program.cs:87-101`).

---

## Considered options

### A. MongoDB EF Core provider, reusing `VSagaDbContext`

*Pros.* Near-zero new code; one model; migrations-shaped mental model preserved.

*Cons.* Structurally disqualified. `VSaga.Persistence.EFCore` references
`Microsoft.EntityFrameworkCore.Relational`; the model uses `ToTable`/`HasMaxLength` throughout plus
`HasFilter("\"BusinessKey\" IS NOT NULL")` (`VSagaDbContext.cs:128`); the claim path uses
`FromSqlInterpolated` (`EfCoreSagaTimeoutStore.cs:87`); and the entire
`VSaga.Persistence.EFCore.Postgres` migrations assembly has no non-relational equivalent.
`docs/persistence.md:10-15` already records this constraint in the abstract.

**Rejected.**

### B. Native driver, one session/transaction held for the whole message

*Pros.* Superficially the obvious mirror of "EF holds a `DbContext` for the message".

*Cons.* **Factually wrong as a mirror.** EF holds a *change tracker* across the message, not a
transaction; a transaction exists only for the duration of each `SaveChangesAsync`, and each store calls
it independently. Worse, it breaks driver 2 outright: the log entry vanishes with an aborted transaction,
so every redelivered message is reprocessed non-idempotently. It would also hold a transaction open
across arbitrary user step I/O (`.CallHttp` is a shipped DSL feature), blowing the 60 s
`transactionLifetimeLimitSeconds`; the 5 ms default `maxTransactionLockRequestTimeoutMillis` would turn
same-instance contention into an abort of the *entire message*; and the dispatchers dispose their claim
scope *before* the claimed work runs (`SagaTimeoutDispatcherHostedService.cs:63-68`), so a Scoped session
is structurally wrong there anyway.

**Rejected.**

### C. Native driver, Scoped staging buffer + one short transaction opened at persist time

*Pros.* The exact behavioural mirror of EF's change-tracker-plus-`SaveChangesAsync`. No session held
across user I/O. `EnqueueAsync` honours its must-not-commit contract literally, with zero Mongo
operations. The staging window is tight and uninterrupted — every staging site is immediately followed by
a persist. Event-log appends stay outside every transaction, preserving the redelivery net. The
asymmetry is encoded **structurally**: the event-log store takes no dependency on the unit of work; the
outbox store's `EnqueueAsync` takes no collection handle.

*Cons.* Requires a replica set or mongos. Introduces machinery EF gets from the `DbContext` for free — a
staging buffer, client-minted ids, a sequence allocator, an index bootstrapper. The flush must be
**non-destructive** (peek, commit, clear): the naive take-then-write variant makes `DiscardPendingAsync`
structurally unreachable and loses the dead-letter `ChildSagaFinished` row.

**Chosen.**

### D. Embed pending outbox rows as an array inside the snapshot document

*Pros.* Genuinely atomic on *any* topology including a standalone mongod — MongoDB's real atomicity
primitive, and its own documentation warns that distributed transactions should not substitute for
effective schema design. No replica set anywhere: simpler compose, simpler fixtures, simpler dev
prerequisites. `DiscardPendingAsync` becomes a `$pull` on a local array.

*Cons.* Breaks `ClaimPendingAsync`: the recovery poller would have to claim sub-documents across *all*
snapshot documents, the claim index becomes multikey over an unbounded array, and there is no way to
atomically claim N array elements across N documents. The snapshot becomes the hottest and largest
document in the system, sharing one 16 MB budget with every pending message body. And it is a semantic
mismatch: an outbox row's `CorrelationId` is the **envelope's**, not the publishing saga's — a queued
`StartChildAsync` carries a fresh id and `NotifyParentAsync` the parent's (`SagaOrchestrator.cs:803-806`
is explicit).

**Rejected**, and recorded here because it is the genuinely interesting alternative: the replica-set cost
is small and universal in production, whereas the storage-model distortion is permanent.

### E. Claim-token batch claim (`find` ids → `updateMany` with token → `find` by token)

*Pros.* Three round trips regardless of batch size, versus up to `batchSize` for a `findOneAndUpdate`
loop. The `status: Pending` re-check in the `updateMany` filter gives SKIP LOCKED's no-overlap property.

*Cons.* `updateMany` is **not** a retryable write. A crash between the `updateMany` and the follow-up
`find` strands the **whole batch** marked terminal with no way to identify it — a failure mode Postgres's
single statement structurally cannot produce. Recovering it would need a lease-and-reclaim sweep,
converting the outbox from at-most-once to at-least-once with no engine-level dedupe guard behind it.

**Rejected** in favour of the `findOneAndUpdate` loop, whose cost is bounded by work actually present
(break on the first null, so an idle poll is one round trip).

### F. Register MongoDB *alongside* EF Core rather than as a replacement

**Not expressible today.** `AddVSagaEfCore` uses plain `AddScoped`
(`VSaga.Persistence.EFCore/ServiceCollectionExtensions.cs:18-26`), not `TryAdd`, so two provider
extensions silently resolve last-one-wins per interface. And the outbox's atomicity guarantee cannot span
two databases in any case. **Rejected and explicitly out of scope.**

---

## Decision

Build **`VSaga.Persistence.MongoDB` on the native `MongoDB.Driver` 3.x** (option C), registered via

```csharp
AddVSagaMongoDb(Action<VSagaMongoOptions> configure,
                Action<MongoClientSettings>? configureClient = null)
```

and selected as a **replacement for** the EF provider through a new `Persistence:Provider` configuration
switch, mirroring the `Transport:Provider` switch at `Dashboard.Api/Program.cs:56-71`. The second
parameter follows the precedent `docs/configuration.md:190-201` records for EF ("there is no
`VSagaEfCoreOptions` class, because EF Core already has one") and is the only way to reach connection
pooling, TLS material, and the `ClusterConfigurator` hook that provider-level tracing requires — the
MongoDB .NET driver exposes no `ActivitySource` of its own.

**Atomicity** is a Scoped in-memory staging buffer plus one short multi-document transaction opened
inside the snapshot store's own write, flushed **non-destructively** (peek → commit → clear, restore on
throw). Event-log appends, sequence allocation, timeout schedule/cancel, `MarkDispatchedAsync` and every
read stay outside any transaction.

**A replica set or mongos is a hard prerequisite, with no non-transactional escape hatch.** The tempting
precedent — `EfCoreSagaTimeoutStore.cs:39-49`'s non-Postgres fallback — is not comparable: that fallback
is provider-*detected* and only ever reached by SQLite in tests, not an operator-settable flag. And parity
with the in-memory provider's documented gap is false in the dimension that matters: in-memory's phantom
outbox rows live in a `ConcurrentDictionary`, so a crash destroys them; Mongo's would be **durable**, and
the recovery poller would faithfully republish them after restart.

**Read preference, read concern and write concern are pinned by the provider and validated against the
operator's connection string**, not assumed — including `WriteConcern.WMajority` on *every collection
handle*, not only inside `TransactionOptions`, because the event-log append is the one write the entire
redelivery net rests on.

**Bootstrapping** (capability probe + index creation) runs in a **retrying hosted service**, never in
`AddVSagaMongoDb` and never fail-fast, with the health check reporting `Unhealthy` until it lands so
`docker-compose.yml:70-74`'s ordering gate stays meaningful.

**Preceding all of it, Stage 0** writes the seven unwritten contracts down, extracts a cross-provider
conformance suite, and fixes the six behavioural divergences that suite surfaces in the two shipped
providers.

Mongo ships as a **provisional** production-tier peer: the parity claim in `docs/persistence.md` is
earned only once the conformance suite is green on all three providers *and* the killed-host-mid-step
live verification passes.

---

## Consequences

### Positive

- A third provider whose acceptance criteria exist in **executable** form, green on two providers before
  the third is written — so a Mongo failure is unambiguous rather than a contract argument.
- Seven previously-undocumented contract clauses become written contract, and six existing cross-provider
  divergences are fixed as a side effect. Two of those are live bugs today.
- `ListAsync` gains a stable **total** order on every provider, closing a tie-group paging hazard that
  exists on Postgres too, just narrower.
- The outbox/event-log transactional asymmetry becomes enforced by **dependency shape** rather than by
  convention and comment.
- The dead-letter `ChildSagaFinished` row commits **atomically with the `Failed` snapshot**, where EF
  commits it via an incidental `AppendAsync` flush — a strict improvement.
- The health check moves inside each provider's own registration extension, making "a health check named
  after a database that is not running" structurally impossible. (`PostgresHealthCheck.cs:19-21` fails
  **open** today.) *The Redis provider landed the provider-neutral `"persistence"` name and a check that
  never fails open, but registers it from the host rather than from `AddVSagaRedis` — see
  [`../design/redis-persistence.md`](../design/redis-persistence.md) §8.1 for why; this plan's
  Stage 6 still decides whether to move both checks inside the extensions.*
- `docs/dashboard.md:6-8`'s provider-agnostic premise stays true.

### Negative

- **A replica set becomes a hard operational prerequisite**, including for every test run and every dev
  machine. There is no in-process fake — every Mongo store test needs Docker, where the EF provider gets
  broad coverage cheaply from SQLite.
- Machinery EF gets free: a staging buffer, a client-minted id generator, a sequences collection, an
  index bootstrapper, a schema-version marker.
- `AppendAsync` costs two round trips where EF costs one; the engine appends 6–8 entries per message, so
  per-message oplog volume roughly doubles.
- Claim loops cost up to `batchSize` round trips per poll where Postgres costs one statement.
- Nine secondary indexes on `sagaInstances` against EF's six, three containing a field that changes on
  every persist — extra index maintenance inside the transaction window.
- A new unbounded, correctness-bearing collection (`sagaSequences`, one document per saga instance ever
  created) that must never be pruned independently of its timeline.
- A hard 16 MB document cap on `dataJson`, `payloadJson` and the outbox `body`, with a lossy (loud) guard
  on `payloadJson` — which records the full inbound body *before* the step runs, so an oversized message
  that Postgres accepts would otherwise make the saga permanently unstartable.
- `HasMaxLength` constraints disappear: a value Postgres rejects silently succeeds on Mongo, and
  unbounded strings can exceed WiredTiger's 1024-byte index-key limit on a hot path.
- **Stage 0 changes two shipped providers' observable behaviour and a published abstractions package
  before any Mongo code exists.**
- `dotnet pack` grows by one package, and `MongoDB.Driver`'s transitive graph becomes a
  solution-wide restore-failure surface under `TreatWarningsAsErrors` — a failure class
  `Directory.Build.props:36-39` records having already hit this repo once.

### Neutral

- `SagaLogEntry.SequenceNumber` becomes per-instance rather than globally comparable. Behaviourally
  invisible (Core never reads it; `SagaMapBuilder` only sorts and mints ids within one timeline), and it
  is what `ISagaEventLogStore.cs:14` already documents — EF and in-memory are the ones diverging from the
  written contract.
- Timestamps are stored as a `Date` + `Ticks` pair, Ticks authoritative, because BSON `Date` is
  millisecond-precision and the change poller's watermark is strictly greater.
- `TState` stays a `System.Text.Json` string, byte-identical to EF's `DataJson`. Native BSON storage is a
  named follow-up with a fidelity test suite as its entry gate.
- **`VSaga.Core` requires no changes** — `SagaRuntime.cs:25-27` already opens a fresh scope per unit of
  work, and both pollers already resolve their stores through `IServiceScopeFactory` per poll.

---

## What would invalidate this decision later

1. **A supported transaction-free atomic shape appears.** If a schema redesign finds a way to commit the
   snapshot and its outbox rows in a single-document update *without* breaking `ClaimPendingAsync`, the
   replica-set prerequisite evaporates and option D becomes the better trade.
2. **The measured claim-loop or append cost proves unacceptable.** The fix would be widening the
   *contracts* (`AppendRangeAsync`, a batch-claim primitive) — a cross-provider abstractions change, not a
   Mongo implementation choice.
3. **`SequenceNumber` becomes load-bearing outside one timeline.** The per-instance decision would need
   revisiting, and with it the global-counter hotspot trade.
4. **The engine gains a lease/re-claim model for timeouts and outbox rows.** That changes observable
   semantics from at-most-once to at-least-once for *every* provider, and makes option E viable, since a
   stranded batch would then be recoverable.
5. **The staged-row lifecycle is pinned the other way** — i.e. if the contract requires a later persist to
   commit rows staged before an *earlier* failed one on every path, `MongoSagaUnitOfWork` needs an explicit
   flush hook the orchestrator can reach, which is a `VSaga.Core` change.
6. **The dashboard stops being provider-agnostic.** If change streams or any other provider-specific push
   source lands in `VSaga.Dashboard.Api`, the `Persistence:Provider` switch's shape should be revisited.
7. **A distributed-transaction or dual-provider story becomes a requirement.** Both are out of scope today
   precisely because the outbox's atomicity guarantee cannot span two databases.
8. **Native BSON storage of `TState` ships.** That turns `GetDataJsonAsync` from a one-field projection
   into a fidelity-critical reconstruction and makes `ResetStateAsync`'s `JsonNode` patch obsolete — a
   large enough shift to warrant its own ADR.

---

## Open questions blocking acceptance

Q1 is resolved (ADR 0003). **Three remain**, all specific to MongoDB itself — stated in full with
options and recommendations in the design doc's §9:

| | Question | Recommendation |
| --- | --- | --- |
| ~~Q1~~ | ~~Is Stage 0 a prerequisite?~~ | **RESOLVED** 2026-09-25 — accepted in full and extracted to ADR 0003 |
| **Q2** | Is the replica-set prerequisite hard, with no escape hatch? | Yes |
| **Q3** | Does the provider override the operator's connection string, or only validate it? | Override, and fail bootstrap on an explicit contradiction |
| **Q4** | Fail-fast startup, or retrying bootstrapper with `Unhealthy` until indexes land? | Retrying bootstrapper |

Of the nine non-blocking questions (Q5–Q13), four are also resolved by ADR 0003 or alongside it
(Q5 abstractions surface, Q6 search case-sensitivity, Q7 staged-row lifecycle, Q11 ADR location).
