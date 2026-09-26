# Design: MongoDB persistence

**Status: planned, nothing built.** No `VSaga.Persistence.MongoDB` project exists; no line of this has
been written. This file is the plan and the reasoning behind it, written to be picked up cold. The
decision it argues for is recorded separately in
[`../adr/0001-mongodb-persistence-provider.md`](../adr/0001-mongodb-persistence-provider.md) — read the
ADR for *what* was decided and why; read this file for *how* it gets built and what is still open.

**Stage 0 is blocking and it is not Mongo work.** The first three commits change two already-shipped
providers and a published abstractions package before any Mongo code exists. That is the single most
important thing to agree or reject before anything starts — see §9 (Q1).

Every claim about the current codebase carries a `file:line` so it can be re-checked rather than
trusted. Line numbers are accurate at commit `b2b99fd`; re-grep rather than trusting them once the tree
moves.

---

## 1. What it is

A third persistence provider — `VSaga.Persistence.MongoDB` — implementing the same seven contracts in
`dotnet/src/VSaga.Abstractions/Persistence/` that `VSaga.Persistence.EFCore` and
`VSaga.Persistence.InMemory` implement today: `ISagaSnapshotStore<TState>`, `ISagaEventLogStore`,
`ISagaOutboxStore`, `ISagaTimeoutStore`, `ISagaAdminStore`, `ISagaSummaryReader`, and
`IServiceTopologyStore`.

It is built on the **native `MongoDB.Driver` 3.x**, not on the MongoDB EF Core provider, and it is a
**replacement for** the EF provider rather than a peer registered alongside it — selected by a new
`Persistence:Provider` configuration switch mirroring the `Transport:Provider` switch that
`VSaga.Dashboard.Api/Program.cs:56-71` already establishes.

### 1.1 What it is not

- **Not a migration path.** `Persistence:Provider` is a greenfield choice. Flipping it on a running
  system points every store at an empty database: in-flight sagas vanish, pending timeouts never fire,
  Pending outbox rows are never drained. Named out of scope in §10, following the pattern
  `docs/design/mixed-sagas.md:556` sets.
- **Not a second simultaneously-registered provider.** `AddVSagaEfCore` uses plain `AddScoped`
  (`dotnet/src/VSaga.Persistence.EFCore/ServiceCollectionExtensions.cs:18-26`), not `TryAdd`, so two
  provider extensions resolve last-one-wins per interface. "Alongside" is not expressible today and
  this plan does not make it expressible.
- **Not a document-modelling exercise.** `TState` stays a `System.Text.Json` string in `dataJson`,
  byte-identical to `SagaInstanceEntity.DataJson`. Native BSON storage of user state is a named
  follow-up with its own entry gate (§10).

---

## 2. What already exists that this builds on

Five things in the engine constrain the design hard enough that they are worth stating before any
Mongo detail.

### 2.1 The outbox stages, the snapshot commits

`ISagaOutboxStore.EnqueueAsync` deliberately does **not** commit. Its own remarks
(`ISagaOutboxStore.cs:41-49`) say so in as many words: the orchestrator calls it immediately before its
own `PersistAsync`, and because every EF store shares one `VSagaDbContext` per message, it is the
snapshot store's `SaveChangesAsync` that commits outbox rows and snapshot together. An implementation
that commits in `EnqueueAsync` "would reopen exactly the dual-write window the outbox exists to close".

This is not theoretical. `SagaOrchestrator.cs:734-745` records a live repro of the resulting bug on the
in-memory provider, "whose `EnqueueAsync` commits non-transactionally": an undiscarded row survives a
lost concurrency race and the recovery poller later sends it for a transition the snapshot never
recorded.

`EnqueueOutboxRowsAsync` (`SagaOrchestrator.cs:799-810`) and `DiscardDeferredPublishesAsync` (`:872-889`)
are the two halves of the contract. The ordering comment at `:874-877` is load-bearing: the discard must
run *before* the `LogAsync` calls below it, because those append through the same shared unit of work
and their `SaveChangesAsync` would commit the very rows the discard exists to suppress.

### 2.2 The event log must be durable *independently* of the persist

Exactly the opposite requirement, on the store sitting next to it.
`HandleInfrastructureFailureAsync` (`SagaOrchestrator.cs:85-93`) republishes with the same
CorrelationId/MessageId, reasoning: "if a durable log entry for this exact message was already written
before the failure, the dedupe check in `HandleCoreAsync` will correctly recognize the redelivered copy
and skip it rather than reprocess it." That check is `IsDuplicateAsync` at `:398`.

A provider that wrapped a whole message in one transaction would take the log entry down with the
aborted persist, and every redelivered message would be reprocessed non-idempotently.

**The two stores at the heart of this design have opposite transactional requirements.** That
asymmetry is the design.

### 2.3 Optimistic concurrency mutates `state.Version` in place

`EfCoreSagaSnapshotStore.cs:58-61` carries the reason: "Bump `state.Version` BEFORE serializing
`DataJson` from it — otherwise the JSON blob embeds the stale version even though the entity's own
Version column is correct, and `FindAsync` (which deserializes from `DataJson`) would silently return
the old version." The restore on failure is at `:86`.

`HandleTimeoutAsync` calls `TryPersistOrLogRaceLossAsync` twice against the **same live object**
(`SagaOrchestrator.cs:240`, `:289` → `:339`), both times reading `state.Version`. A store that writes the
bumped version to storage but forgets the in-place mutation makes every timeout reaching its final
persist report a race it did not lose — and that branch only logs (`:342-348`), so the saga stalls
silently with its side effects already sent.

This contract exists **only** as an implementation comment. Nothing in `ISagaSnapshotStore.cs` says it.

### 2.4 Timeline order feeds compensation

`GetVisitedStatesAsync` (`SagaOrchestrator.cs:902-911`) derives the entire compensation set from
`GetTimelineAsync`. `:589` calls it microseconds after `:585` appended to the same timeline. Ordering
and read-your-own-writes are both correctness requirements, not conveniences — a stale or unordered read
produces a **short** compensation set, which fails silently.

### 2.5 The business-key race is adjudicated by a partial unique index

`docs/design/production-readiness.md` §5.2 is explicit: "Resolve the race by reserving before the step
runs, not by catching after it." The adjudicator is the partial unique index on
`(SagaType, BusinessKey) WHERE BusinessKey IS NOT NULL` (`VSagaDbContext.cs:126-128`), and the loser's
`SagaAlreadyExistsException` is caught at `SagaOrchestrator.cs:487-503` and resolved to the winner.

---

## 3. Stage 0 — now extracted, accepted, and not this plan's to own

**This section used to carry the whole Stage 0 specification. It has been extracted to
[`persistence-contracts.md`](persistence-contracts.md) and its decisions accepted in
[`../adr/0003-persistence-contract-clauses.md`](../adr/0003-persistence-contract-clauses.md).**

The reasoning for extraction: Stage 0 is not a provider's work. It writes down contracts the engine
already depends on, extracts a cross-provider conformance suite, and fixes eight verified divergences
between the two providers shipping today. It is required by any third provider and valuable without
one — so it no longer belongs to whichever provider effort happens to land first, and the
"whichever lands first owns it" coupling between this plan and
[`redis-persistence.md`](redis-persistence.md) is dissolved.

**Four questions this plan raised are now settled there** (ADR 0003): `Search` is case-insensitive and
EF's `LIKE` is the bug; a staged outbox row is committed by the next successful persist in the same unit
of work and dropped if none succeeds, which makes EF's `SagaOrchestrator.cs:130`-false flush a defect;
`ResetStateAsync` is version-guarded and throws rather than retries; and `ClaimDueAsync`/`ClaimPendingAsync`
gain an optional `sagaTypes` filter.

Two engine bugs found while planning are scheduled there too — both concerning `ChildSagaFinished` being
published for a transition no snapshot recorded.

**What this plan consumes:** the ten contract clauses, the `VSaga.Persistence.Conformance` project and
its `IProviderFixture` shape (capability flags plus the `BeginAsync`/`CommitAsync`/`AbandonAsync`
unit-of-work hook), and the seven behaviour fixes. Stage 1 below starts from a green suite on SQLite,
Postgres-Testcontainers and in-memory.

---

## 4. The atomicity strategy

### 4.1 What EF actually does (the input assumption was wrong)

EF does **not** hold a database transaction across a message. `VSagaDbContext` is Scoped
(`SagaRuntime.cs:25-27` opens a fresh scope per message/timeout/retry) and holds an **in-memory change
tracker**; a transaction exists only for the duration of each `SaveChangesAsync`, and each store calls
it independently (`EfCoreSagaSnapshotStore.cs:38` and `:82`, `EfCoreSagaEventLogStore.cs:30`,
`EfCoreSagaTimeoutStore.cs:19` and `:34`, `EfCoreSagaOutboxStore.cs:41`).

So the faithful Mongo analogue is **not** a long session. It is a staging buffer plus a short
transaction, opened at the moment EF would call `SaveChangesAsync`.

### 4.2 The mechanism

**`MongoSagaUnitOfWork`** — Scoped, one per DI scope, i.e. per message/timeout/retry. It holds a plain
`List<SagaOutboxDocument>` and nothing else. **No `IClientSessionHandle` lives in it.**

```
Stage(doc)                 — append
Unstage(messageIds)        — remove by messageId (DiscardPendingAsync)
PeekStaged()               — NON-destructive
ClearStaged()              — called only after a committed flush
HasStaged                  — bool
```

**`MongoSagaSnapshotStore<TState>.InsertAsync`/`UpdateAsync` is the sole committer**, exactly as EF's
`SaveChangesAsync` is. It peeks the buffer. If it is empty — the common case for a saga that never
deferred a publish — it issues a bare `insertOne`/`updateOne` with no session at all, costing nothing.
If it is non-empty, it opens a session and runs `WithTransactionAsync` over
`{ insertMany(staged) ; updateOne(idAndVersionFilter, set) }`, throwing inside the callback when
`MatchedCount == 0` so the transaction aborts.

Three details are not incidental:

- **The callback API (`WithTransactionAsync`), not `StartTransaction`/`CommitTransaction`** — it
  incorporates the `TransientTransactionError` and `UnknownTransactionCommitResult` retry loops the core
  API makes the caller write. Consequence: **the callback may run more than once**, so
  `state.Version = expectedVersion + 1` is assigned *outside* it, and restored outside it on throw.
- **`MatchedCount`, never `ModifiedCount`.** A no-op update (identical document) modifies zero rows
  without being a concurrency failure.
- **Peek-commit-clear, never take-then-write.** See below.

### 4.3 Why the flush must be non-destructive

Draining the buffer *before* the write is the obvious implementation and it is wrong in two ways:

1. **It makes `DiscardPendingAsync` structurally unreachable.** Every staging site is followed by a
   persist (`SagaOrchestrator.cs:277`/`:279` → `:289`; `:645` → `:649`; `:727` → `:731`), and every
   discard call site runs strictly *after* that persist (`:291`, `:292`, `:662`, `:674`, `:746`). Under
   take-then-write the buffer is always empty by the time a discard arrives, so `Unstage` could never
   find anything — the whole `ISagaOutboxStore.cs:69-76` remarks block would ship untested and the
   ordering constraint at `:874-877` would be vacuous. Under peek-commit-clear, a failed persist leaves
   rows staged and the discard removes exactly the right ones, mirroring EF's change-tracker detach
   (`EfCoreSagaOutboxStore.cs:50-65`).
2. **It loses the dead-letter `ChildSagaFinished` row.** `HandleStepFailureAsync` stages it at `:645`;
   if the persist at `:649` throws anything other than `SagaConcurrencyException` (a
   `SagaNotFoundException`, a connection error), the catch at `:651` does not fire and the exception
   unwinds to `RecordDeliveryExhaustedAsync`. On EF the row is flushed by the `LogAsync` at `:126`, and
   `SagaOrchestrator.cs:640-644` names that as intended. On Mongo, with the buffer still populated, the
   second `PersistAsync` at `:134` commits it **atomically with the `Failed` snapshot** — strictly
   better than EF, not merely equal.

### 4.4 Everything else stays outside any transaction

Event-log appends, sequence allocation, `ScheduleAsync`, `CancelAsync`, `MarkDispatchedAsync` and every
read commit on their own, matching EF. §2.2 is why the event log in particular *must*.

The asymmetry is enforced **structurally, not by convention**: `MongoSagaEventLogStore` takes no
dependency on `MongoSagaUnitOfWork` and has no code path that can accept a session;
`MongoSagaOutboxStore.EnqueueAsync` takes no `IMongoCollection` at all, only the unit of work. Folding
appends into the message transaction would require a deliberate API change rather than an omission.

### 4.5 Why not a session held for the whole message

- It would stay open across arbitrary user step I/O — `definition.HandleAsync` can make HTTP calls, and
  `.CallHttp` is a shipped DSL feature — blowing the 60 s default `transactionLifetimeLimitSeconds` and
  holding WiredTiger cache throughout.
- The default 5 ms `maxTransactionLockRequestTimeoutMillis` would turn same-instance contention into a
  transient abort of the *entire message*.
- It breaks the redelivery dedupe (§2.2).
- The dispatchers' claim scope is disposed **before** the claimed work runs
  (`SagaTimeoutDispatcherHostedService.cs:63-68`), so a Scoped session held across dispatch is
  structurally wrong there anyway.
- And it is factually not what EF does.

### 4.6 Replica set is a hard prerequisite

No `AllowNonTransactionalOutbox` escape hatch. The tempting precedent —
`EfCoreSagaTimeoutStore.cs:39-49`'s non-Postgres fallback — is not comparable: that fallback is
provider-**detected** and only ever reached by SQLite in tests, not an operator-settable flag.

And the claimed parity with the in-memory provider's documented gap
(`InMemorySagaStore.cs:332-337`, `docs/design/production-readiness.md` §4.4) is false in the dimension
that matters. In-memory's phantom rows live in a `ConcurrentDictionary`, so a crash destroys them and
nothing is ever published. Mongo's would be **durable**, and `SagaOutboxDispatcherHostedService`
faithfully republishes them after restart — precisely the bug `SagaOrchestrator.cs:734-745` documents.

Detection is from `IMongoClient.Cluster.Description.Type` (`ReplicaSet` or `Sharded`), which the driver
populates from its own SDAM handshake — **not** a `hello` command, which returns `CommandNotFound` on
MongoDB 4.x. The `Sharded` arm is **dropped from v1** unless a shard key is designed and cross-shard
transaction cost is measured.

---

## 5. Collections and indexes

### 5.1 Cross-cutting conventions

**`_id` is always a scalar injective string or an `ObjectId`, never a subdocument.** A composite BSON
subdocument `_id` compares by *field order and byte equality*, so a `[BsonElement]` reordering silently
makes every lookup miss and makes `InsertAsync` create a *second* document instead of throwing
`SagaAlreadyExistsException`. It is the one schema change that can never be migrated in place.
`sagaInstances._id` and `sagaSequences._id` are `$"{correlationId:D}|{sagaType}"` — GUID-first, so the
encoding stays injective whatever `sagaType` contains.

**Every timestamp is stored as a pair: `<name>: Date` plus `<name>Ticks: Int64`, with Ticks
authoritative for every range filter and every sort.** BSON `Date` is millisecond-precision where
`timestamptz` is microsecond; `SagaOrchestrator.cs:895` stamps at 100 ns resolution, and
`SagaListFilter.UpdatedSince` is contractually **strictly** greater (`SagaSummary.cs:37-47`). See §7 R-7
— this is a silent-update-loss bug, not a rounding nicety. Both halves are written from one mapping
function so they cannot disagree.

**Every document carries `sv: Int32`** (schema version, `1` in v1). `VSaga.Persistence.EFCore.Postgres/Migrations/`
holds eight migrations in a month, including a primary-key change. "No migrations" is a liability
transfer, not a free benefit.

**Enums stay `Int32`.** `EfCoreSagaSummaryReader.cs:49-50` sorts on `SagaStatus`'s *numeric* progression;
string storage would silently reorder the dashboard's Status column alphabetically, with no error.

**Collation is the binary default.** Every string comparison in Core is `StringComparison.Ordinal`
(`SagaOrchestrator.cs:229` gates timeout validity; `:909` dedupes visited states).

**Serializer registration is per-class only** — never `BsonDefaults` or a global
`BsonSerializer.RegisterSerializer`, whose registry is process-wide and settable once: a host application
with its own Mongo usage would be broken by merely referencing vSaga.

### 5.2 The six collections

Five mirror the five EF tables; `sagaSequences` is new.

| Collection | Mirrors | Note |
| --- | --- | --- |
| `sagaInstances` | `SagaInstances` | Adds `sagaTypeLower` and `correlationIdText` search fields, and `updatedAtTicks`/`createdAtTicks`. |
| `sagaEventLog` | `SagaEventLog` | `seq: Int64` replaces the identity column. |
| `sagaSequences` | *(none)* | Per-instance `seq` counter. See §5.4. |
| `sagaTimeouts` | `SagaTimeouts` | |
| `sagaOutboxMessages` | `SagaOutboxMessages` | `destination` round-trips as an **explicit null**, never omitted — `SagaOutboxDispatcherHostedService.cs:63-65` branches on `Destination is null` to choose `PublishRawAsync` vs `SendRawAsync`. |
| `sagaConsumerRegistrations` | `SagaConsumerRegistrations` | Composite `_id` gives upsert idempotency for free, as the EF composite key does. |

### 5.3 Indexes worth arguing about

The full set is mechanical; four are not.

- **`ux_sagaType_businessKey`** — `{sagaType:1, businessKey:1}`, unique, with
  `partialFilterExpression: {businessKey: {$type: "string"}}`. **`$type:"string"`, not `$exists:true`.**
  `$exists:true` matches documents holding an explicit null, so every second business-key-less saga of
  the same type would collide — and sagas that never declare `CorrelateOn` leave `BusinessKey` null,
  which is the common case. `$ne` is not an accepted `partialFilterExpression` operator, so "not null"
  is simply not expressible. Belt-and-braces: `businessKey` is `[BsonIgnoreIfNull]`, so the field is
  *absent* rather than null.
- **`ix_updatedAt_total`** — `{updatedAtTicks:1, sagaType:1, correlationId:1}`. The tiebreakers must be
  *in* the index: Mongo serves a sort from an index only when the sort key pattern equals the index
  pattern or its **exact inverse**, so tiebreak direction must follow the lead key's direction.
- **`ix_status_updatedDesc`** and **`ix_statusDesc_updatedDesc`** — genuinely two indexes, because the
  query wants the UpdatedAt tiebreak to stay descending in both Status directions, and that is not the
  exact inverse of either. The second is explicitly **deferrable**: omitting it costs a blocking sort on
  one dashboard sort option.
- **No search index in v1.** `ListAsync`'s `search` is an `$or` of two non-anchored `$regex` predicates.
  A non-anchored regex cannot seek, and the planner can serve either the search predicate or the sort
  from an index, not both — so leaving it unindexed lets the sort index stream and applies search as a
  residual filter, which is what Postgres does today with `LIKE '%x%'`. Two fields, not one concatenated
  `searchText`: `SagaSummary.cs:34` specifies two **independent** matches, and a concatenated field
  would match a boundary-spanning term neither other provider can match.

### 5.4 Why `sagaSequences` exists

MongoDB has no auto-increment, and an embedded per-snapshot counter is not viable: `AppendAsync` is
legitimately called when **no snapshot document exists**. The `SagaStarted` append at
`SagaOrchestrator.cs:393-396` precedes the end-of-step `InsertAsync`; `RecordDeliveryExhaustedAsync`'s
append at `:126` and the `UnexpectedEvent` append at `:375` can both fire for an instance never
persisted.

Allocated with `findOneAndUpdate({_id}, {$inc:{seq:1}}, upsert:true, returnDocument:After)`, **wrapped in
a bounded retry on duplicate-key 11000** — an upsert on a non-existent document can race two concurrent
inserts. This is not a corner case: two messages for the same correlation id arriving concurrently both
call `LogAsync(MessageReceived)` at `SagaOrchestrator.cs:585` with no serialization gate.

**Operational note that belongs in `docs/persistence.md`:** this collection is durable,
correctness-bearing state, and it must **never** be pruned independently of its instance's timeline —
deleting a counter restarts `seq` at 1 mid-timeline and silently corrupts `GetTimelineAsync`'s ordering.
It is not a cache.

`SequenceNumber` therefore becomes **per-instance** rather than globally comparable. Verified safe: Core
never reads it, and `SagaMapBuilder` only sorts and mints edge ids *within one timeline*. Worth noting
that `ISagaEventLogStore.cs:14` already says "per-saga-instance-ordered" — EF and in-memory are the ones
diverging from the written contract.

### 5.5 No TTL retention in v1

Both `EventLogRetention` and `DispatchedOutboxRetention` are dropped. The event log is not audit-only:
`GetVisitedStatesAsync` derives the compensation set from it and `IsDuplicateAsync` is the redelivery
dedupe, so expiring entries of a still-running long-lived saga silently shrinks `ctx.VisitedStates` and
turns a redelivered message into a fresh one. `docs/design/production-readiness.md:72-75` already lists
event-log retention among items "Explicitly out of scope, named so they are not mistaken for oversights".
For the outbox, a naive TTL on `createdAtUtc` would delete **Pending** rows, silently discarding
undispatched messages.

---

## 6. Store-by-store mapping

| Contract | Method | Mongo operation |
| --- | --- | --- |
| `ISagaSnapshotStore<TState>` | `FindAsync` | `find({_id})`, deserialize `dataJson` |
| | `FindByBusinessKeyAsync` | `find({sagaType, businessKey})` on the partial unique index |
| | `InsertAsync` | `insertOne` (or the transactional flush when rows are staged); 11000 on `_id_` or `ux_sagaType_businessKey` → `SagaAlreadyExistsException` |
| | `UpdateAsync` | `updateOne({_id, version: expected}, {$set})`; `MatchedCount==0` disambiguated by a follow-up `findOne({_id})` projecting only `_id` → `SagaConcurrencyException` vs `SagaNotFoundException` |
| `ISagaEventLogStore` | `AppendAsync` | counter `$inc` upsert (retried on 11000), then `insertOne`. **No session.** |
| | `GetTimelineAsync` | `find({sagaType, correlationId}).sort({seq:1})` |
| | `IsDuplicateAsync` | `find({sagaType, correlationId, messageId, entryType:{$in:[SagaStarted, MessageReceived]}}).limit(1)` |
| `ISagaOutboxStore` | `EnqueueAsync` | `uow.Stage(doc)` — **zero Mongo operations** |
| | `MarkDispatchedAsync` | `updateOne({messageId}, {$set:{status:Dispatched}})` |
| | `DiscardPendingAsync` | `uow.Unstage(messageIds)` — in-memory only |
| | `ClaimPendingAsync` | `findOneAndUpdate` loop, break on first null |
| `ISagaTimeoutStore` | `ScheduleAsync` | `insertOne`, no session |
| | `CancelAsync` | `updateMany` on the four-predicate filter |
| | `ClaimDueAsync` | `findOneAndUpdate` loop |
| `ISagaSummaryReader` | `ListAsync` | `find` + `countDocuments`, total-ordered sort |
| | `GetAsync` / `GetDataJsonAsync` | `find({_id})` with projection |
| | `FindByCorrelationIdAsync` | `find({correlationId}).sort({sagaType:1})` |
| | `FindChildrenAsync` | `find({parentSagaType, parentCorrelationId})` on the partial `ix_parent` |
| | `GetSagaTypesAsync` | `$group` on `{sagaType, kind}` |
| `ISagaAdminStore` | `ResetStateAsync` | read, patch the blob in C# with `JsonNode`, then a single version-guarded `updateOne` writing both the projection and the patched blob. **Not** a pure `updateOne`: `dataJson` is an opaque `System.Text.Json` string (§1.1), and Mongo's update language cannot patch JSON properties *inside a string*. Single-shot, not a retry loop — ADR 0003 decision 3 |
| `IServiceTopologyStore` | `RecordAsync` | one `updateOne` upsert (EF needs two round trips) |
| | `GetAllAsync` | unfiltered `find` |

### 6.1 The claim loop

`findOneAndUpdate` claims exactly **one** document atomically. Claiming a batch means looping until null
or `batchSize`. This costs up to `batchSize` round trips where Postgres costs one statement; an *idle*
poll costs exactly one.

The alternative — a claim-token `find`/`updateMany`/`find` — was rejected: `updateMany` is **not** a
retryable write, and a crash between the `updateMany` and the follow-up `find` strands the **whole
batch** marked terminal with no way to identify it. That is a failure mode Postgres's single statement
structurally cannot produce.

**Honest statement of the loop's own failure mode:** a crash at iteration *k* leaves *k* documents
already marked terminal, existing only in a local list that dies with the process. This is **not tighter
than Postgres**, whose committed batch strands the same way, and the plan must not claim it is. It is
still the right choice — `findOneAndUpdate` flips and returns in one operation and *is* a retryable
write.

---

## 7. Risks

Ordered by severity. Blockers are design-defining; the rest are things to keep visible.

**R-1 (blocker) — the two stores need opposite transactional guarantees.** §2.1 vs §2.2. Mitigated
structurally by the dependency shape (§4.4), and covered in both directions by the conformance fixture's
`AbandonAsync` hook: an abandoned unit of work must leave zero outbox documents **and** the log entries
appended during it must still be present.

**R-2 (blocker) — `UpdateAsync`'s in-place `Version` mutation.** §2.3. Written into
`ISagaSnapshotStore.cs` in Stage 0, before any Mongo code exists. Complicated on Mongo by
`WithTransactionAsync`'s retrying callback, hence the assignment living outside it.

**R-3 (blocker) — write concern.** `w:1` is the driver default. A `w:1` event-log append that has not
replicated is rolled back when its primary loses an election; `IsDuplicateAsync` then returns false for
the redelivered copy and the message is reprocessed non-idempotently. The redelivery net's entire
premise rests on that one write. Mitigation: `WriteConcern.WMajority` pinned on **every collection
handle**, not only inside `TransactionOptions`. PSA (primary-secondary-arbiter) topologies documented as
**unsupported** — `w:majority` hangs when the single data-bearing secondary is down.

**R-4 (blocker) — read preference.** `readPreference=secondaryPreferred` in a user-supplied URI — a
common "read-heavy dashboard" default — silently breaks `IsDuplicateAsync`, the business-key race
adjudication (`SagaOrchestrator.cs:498-500`), `GetVisitedStatesAsync` (a stale read produces a **short**
compensation set) and the poller's watermark. Undetectable by any correctness test. Mitigation:
`ReadPreference.Primary` and `ReadConcern.Local` pinned, overriding the connection string, with bootstrap
rejection of an explicit non-primary preference naming the guarantee it breaks.

**R-5 (major) — `MongoDB.Driver`'s transitive graph.** `TreatWarningsAsErrors=true` with an empty
`<WarningsAsErrors />` (`Directory.Build.props:9-10`) turns any NuGetAudit NU19xx advisory into a restore
error **across every project in the solution**. `Directory.Build.props:36-39` records that this has
already bitten this repo once, over `Microsoft.Build.Tasks.Git`. Mitigation: a **real**
`dotnet restore dotnet/VSaga.slnx` is the Stage 1 acceptance gate, not a guess.

**R-6 (major) — driver v3's default `DateTimeOffset` representation** changed from an Array to a BSON
**Document** `{DateTime, Ticks, Offset}`. Leaving it at the default **fails while still returning
documents**: `$gt` and `$sort` would compare subdocuments rather than instants, so the dashboard returns
pages — just the wrong ones. The Date+Ticks pair serializer fixes this and the precision problem
together.

**R-7 (major) — millisecond truncation silently drops live updates.** BSON `Date` is millisecond;
`SagaChangePollingService`'s watermark is strict `>`; `ResolveWatermark` (`:200-207`) only retreats below
a trailing tie group when `stoppedEarly`. On Postgres the exposure window is one microsecond; on Mongo it
becomes a thousand times wider. Worst case is a **terminal status** — the last update a saga ever gets.
No sort tiebreaker fixes this; it is a predicate-precision problem, which is why `updatedAtTicks` is
authoritative.

**R-8 (major) — offset paging over a non-unique sort key.** `$skip`/`$limit` with ties has no guaranteed
tie order across the separate finds backing pages 1..N. A row can be returned twice (harmless —
`SagaUpdated` is a client-side upsert) or **skipped**, which is a dropped live update. Mitigation: total
orders with direction-following tiebreakers *in* the supporting index. This hazard exists on Postgres
too, just narrower — hence the Stage 0 fix to both providers.

**R-9 (major) — the partial unique index's null handling.** See §5.3. The obvious translation is wrong.

**R-10 (major) — `MatchedCount==0` ambiguity.** `SagaConcurrencyException` drives redelivery or timeout
abandonment; `SagaNotFoundException` is an uncaught infrastructure failure. Conflating them turns a
genuinely missing saga into an infinite optimistic-retry loop.

**R-11 (major) — append round-trip cost.** Two round trips where EF costs one, 6–8 appends per message,
so per-message oplog volume roughly doubles. **Per-scope block reservation is rejected on correctness
grounds, not deferred:** two processes handling concurrent messages for the *same* instance would reserve
disjoint blocks, so appends could sort in the wrong order — which feeds `GetVisitedStatesAsync` and
therefore compensation order. A future `AppendRangeAsync` batch contract is the correct optimisation.

**R-12 (major) — write amplification.** Nine secondary indexes against EF's six, three of them containing
`updatedAtTicks`, which changes on every persist — extra index maintenance **inside the transaction
window**. Mitigated by deferring `ix_statusDesc_updatedDesc` and shipping no search index.

**R-13 (major) — Mongo tests are Docker-only.** The EF provider gets broad cheap coverage from SQLite
(`EfCoreStoreTests.cs`, ~980 lines) with Postgres reserved for a targeted subset. Mongo has no in-process
equivalent and every container must be a replica set. `CONTRIBUTING.md:61-62` forbids skipping silently.

**R-14 (major) — no schema-evolution mechanism.** Mitigated by `sv`, explicit index names everywhere, and
documented drop-and-recreate / copy-collection procedures — **including that dropping
`ux_sagaType_businessKey` disables the business-key race adjudicator for the duration of the rebuild.**
That is a correctness window, not a docs footnote.

**R-15 (major on `payloadJson`) — the 16 MB document cap.** `SagaOrchestrator.cs:393-396` appends the
**full inbound message body** on every `SagaStarted` *before* the step runs at `:404`. An oversized
message that Postgres accepts becomes, on Mongo, a permanently unstartable saga that redelivers to
exhaustion and dead-letters. Mitigation: a `MaxPayloadJsonBytes` guard replacing the payload with a loud
marker and a warning log. `dataJson` and the outbox `body` let the write exception surface naturally.

**R-16 (minor) — claim-loop stranding.** See §6.1, including the correction that it is *not* tighter than
Postgres.

**R-17 (major, cross-provider) — `ClaimDueAsync` has no saga-type filter.** `ISagaTimeoutStore.cs:32` takes
only `asOf` and `batchSize`: the dispatcher claims and marks Fired **every** due row in the collection,
then silently drops any whose `SagaType` has no registered runtime in this process
(`SagaTimeoutDispatcherHostedService.cs:34-38`) — *after* the row is already terminal, so it can never
fire again. This is a pre-existing defect, not a Mongo one, but a shared Mongo database across services is
a far more common idiom than a shared Postgres schema, so Mongo makes it far more likely to be hit.

**R-18 (minor) — length constraints disappear.** `VSagaDbContext` caps `SagaType`/`CurrentState`/`ForState`
at 200 and `MessageType`/`MessageTypeName`/`QueueName`/`Destination`/`BusinessKey` at 400. Mongo enforces
none, so a value Postgres rejects silently succeeds. Conversely, unbounded strings can exceed WiredTiger's
1024-byte index-key limit on `_id`, `ux_sagaType_businessKey` and `ix_parent` — an insert-time error on a
hot path. At EF's caps the worst case is ~600 bytes, comfortably under.

---

## 8. Stages

Each stage is independently reviewable and leaves the build green.

| # | Title | Gate |
| --- | --- | --- |
| **0** | **Extracted — see [`persistence-contracts.md`](persistence-contracts.md)**, decisions accepted in ADR 0003. Consumed, not owned (§3) | that plan's gate: the conformance suite green on SQLite, Postgres-Testcontainers and in-memory |
| 1 | Package skeleton, document model, serializers, index initializer | A **real** `dotnet restore dotnet/VSaga.slnx` (R-5), then `dotnet pack -p:MinVerVersionOverride=0.1.0-local`, inspecting the nuspec for transitives promoted to direct dependencies by `CentralPackageTransitivePinningEnabled` |
| 1b | **Testcontainers replica-set fixture spike** | One throwaway file that actually stands up a replica-set container. `MongoDbBuilder.WithReplicaSet(...)` is **unverified**; `CONTRIBUTING.md:59-62` says "compiled but never run" is not a pass. **Stage 2 does not start until this is written down.** |
| 2 | Unit of work + snapshot store (the atomicity crux, §4) | N parallel `InsertAsync` on one business key → exactly one success; staged row + version-losing update → zero outbox documents **and the row still staged**; `explain()` shows no COLLSCAN on `FindByBusinessKeyAsync` |
| 3 | Event log store + sequence allocator | 50 concurrent appends for one instance **whose counter document does not yet exist** yield 50 distinct increasing `seq` — this is the test that catches a missing 11000 retry; a version that pre-creates the counter passes vacuously |
| 4 | Timeout and outbox stores, claim loops | The four `PostgresEfCoreStoreTests` claim tests ported verbatim into the conformance suite, including 20 rows / 2 concurrent claimers with no overlap and no loss |
| 5 | Summary reader, admin store, topology store | A 2000-row multi-page ascending drain where **every row shares one millisecond** returns each row exactly once (the R-7/R-8 proof); `explain()` shows no SORT stage |
| 6 | DI registration, retrying bootstrapper, capability probe, health check | Standalone (non-replica-set) mongod: DI resolves, health reports Unhealthy naming the prerequisite, host never crashes |
| 7 | Conformance green on Mongo + one end-to-end orchestrator-sequence test | Whole suite green. `SagaTestHarness` is deliberately **not** modified — it hardwires in-memory persistence (`SagaTestHarness.cs:44-52`) and excludes both pollers (`:72-78`) precisely because it is a deterministic unit-testing tool. |
| 8 | `Persistence:Provider` seam in `Dashboard.Api` and the sample | Existing `VSaga.Dashboard.Api.Tests` green; `docker compose build` succeeds for both images |
| 9 | `docker-compose.mongo.yml` overlay + live verification | A real `docker compose -f docker-compose.yml -f docker-compose.mongo.yml -p vsaga-mongo up --build` run: a timeout firing and compensating, a **killed saga host mid-step** so the outbox poller republishes, a second `up` on the persisted volume, and the saga map resolving destinations |
| 10 | CI, packaging, documentation | Zero stale hits for "two persistence providers", "five suites", "EF Core/Postgres and in-memory"; `dotnet pack` produces one package more than at the stage's start — a delta, since unrelated packages move the absolute count |

### 8.1 Notes on specific stages

**Stage 6 registers the health check from inside `AddVSagaMongoDb`**, in `VSaga.Persistence.MongoDB` —
not in `VSaga.Dashboard.Api`. Companion commit: move `PostgresHealthCheck` into
`VSaga.Persistence.EFCore`, register it from `AddVSagaEfCore`, and rename both to the provider-neutral
`"persistence"`. This is what makes drift structurally impossible rather than conventional —
`PostgresHealthCheck.cs:19-21` fails **open** today (`Healthy("No relational database configured.")`), so
a Mongo-configured dashboard would report `postgres: healthy` having probed nothing.

**Stage 6's bootstrap model is a retrying `IHostedService`, never fail-fast.**
`HealthEndpointTests.cs:7-16` states the composition root must resolve without a live database, and
`Dashboard.Api/Program.cs:87-101` deliberately warns-and-continues so "this container wins the startup
race against the DB" is survivable. But the health check must report `Unhealthy` until indexes land,
because `docker-compose.yml:70-74` gates `order-processing` on `dashboard-api` being healthy — if that
gate goes vestigial, sagas run without the business-key adjudicator.

**Stage 8 must also fix `DashboardApiFactory`.** Its by-concrete-type `RemoveAll` calls (`:39-48`) become
a silent trap the moment `Program.cs` stops registering EF unconditionally: `RemoveAll` no-ops on
unregistered types, so they keep compiling and passing while doing nothing. Replace with `RemoveAll` over
the seven interface types plus `DbContextOptions<VSagaDbContext>`/`VSagaDbContext`. And
`HealthEndpointTests` boots the real composition root via a bare `WebApplicationFactory<Program>`
(`:17`), so the factory override does not reach it and it asserts the check name verbatim at `:39`.

**Stage 9's compose overlay needs an idempotent replica-set init** folded into the healthcheck —
`try { rs.status() } catch { rs.initiate(...) }`, exit 0 on `AlreadyInitialized`. A naive `rs.initiate()`
breaks the *second* `docker compose up` on a persisted volume, and because `dashboard-api` gates
`order-processing`, the whole stack then fails to start. The overlay also **removes** the `postgres`
service and both `depends_on: postgres` arms — leaving it running would leave a `persistence` health
check probing a database nothing uses. Next free port slot in the allocated sequence
(`docs/transports/index.md:125-130`): Mongo 27018, Dashboard API 5580, RabbitMQ 6172/16172.

**Stage 10 needs no CI workflow change beyond a comment.** There is no matrix and no `services:` block —
the dotnet job is a flat restore/build/test over `VSaga.slnx` (`.github/workflows/ci.yml:35`, `:38`,
`:41`), so adding the project to the solution is picked up automatically. Only the parenthetical
Testcontainers list in the `TESTCONTAINERS_RYUK_DISABLED` comment (`:16`) needs "Mongo" added.

Docs touched in Stage 10: `docs/persistence.md` (two providers → three, a new `## MongoDB` section, the
support matrix, the replica-set prerequisite, the write-concern table, the `sagaSequences` lifecycle
warning, the volume caveat, the one-database-per-service rule), `docs/observability.md` (the MongoDB .NET
driver exposes no `ActivitySource`, so provider-level spans need the `configureClient` →
`ClusterConfigurator` hook), `docs/README.md`, `docs/configuration.md:190-211`, `docs/getting-started.md`,
`README.md:11`/`:24`/`:186`, `CONTRIBUTING.md:9-12` and `:59-62` (five → six suites, plus the
Docker-always note), and two package READMEs. **Do not** update
`docs/design/production-readiness.md`'s project counts: its preamble (`:5-8`) declares it a historical
record kept rather than rewritten.

---

## 9. Open questions

Five are now resolved (Q1, Q5, Q6, Q7, Q11 — see ADR 0003). **Three blocking questions remain**, all
specific to MongoDB itself: Q2, Q3, Q4.

### Q1 — Is Stage 0 a prerequisite, or can Mongo start first? — **RESOLVED**

**Answered 2026-09-25: yes, in full, and extracted.** See
[`../adr/0003-persistence-contract-clauses.md`](../adr/0003-persistence-contract-clauses.md) and
[`persistence-contracts.md`](persistence-contracts.md). It no longer blocks *this* plan specifically,
because it is no longer this plan's to own — it proceeds independently of whether MongoDB is ever built.

### Q2 — Is the replica-set prerequisite hard, with no escape hatch? *(blocking)*

Determines the compose recipe, every test fixture, dev-machine prerequisites, and the tier claim in
`docs/persistence.md`.

**Options.** (1) Hard requirement. (2) Hard by default with an `AllowNonTransactionalOutbox` opt-in.
(3) Ship non-transactional and document the gap. (4) Embed pending outbox rows inside the snapshot
document (single-document atomicity, works on standalone mongod).

**Recommendation: (1).** §4.6 gives the reasoning. Option (4) is the genuinely interesting alternative and
is rejected on storage-model grounds, not ideology: it breaks `ClaimPendingAsync` (no way to atomically
claim N array elements across N documents), makes the claim index multikey over an unbounded array, puts
every pending message body inside the snapshot's own 16 MB budget, and is a semantic mismatch besides —
the outbox row's `CorrelationId` is the **envelope's**, not the publishing saga's
(`SagaOrchestrator.cs:803-806` is explicit).

**Coupled sub-question:** is the "production-tier peer" claim conditional? Recommend **yes** — Mongo is
documented at parity only once the conformance suite is green on all three providers *and* Stage 9's
killed-host-mid-step verification passes.

### Q3 — Does the provider override the operator's connection string, or only validate it? *(blocking)*

R-3 and R-4 are two silent-corruption paths that no correctness test can catch.

**Options.** (1) Override silently. (2) Override, and fail bootstrap when the URI explicitly contradicts.
(3) Validate only, never override.

**Recommendation: (2)**, with a per-collection write-concern table in `docs/persistence.md` and PSA
documented as unsupported. Cheap, testable without Docker, closes both paths at once.

### Q4 — What is the bootstrap/health/startup failure model? *(blocking)*

The difference between a self-healing stack and a permanent outage. A fail-fast probe plus an index-gated
health check converts one transient blip or one slow index build into a crash-loop; conversely a check
that passes while indexes are missing makes `docker-compose.yml:70-74`'s gate vestigial.

**Options.** (1) Fail fast at startup. (2) Retrying background bootstrapper, `Unhealthy` until indexes
land, never crash. (3) Retrying bootstrapper, `Degraded` (HTTP 200) while bootstrapping.

**Recommendation: (2).** It is the only option that keeps both properties. (3) is tempting because
`Degraded` maps to 200 in `MapHealthChecks` (`Program.cs:115`), but that starts `order-processing` against
a database with no unique index.

### Q5 — Which `VSaga.Abstractions` changes are in scope, in which release? — **RESOLVED**

**Answered 2026-09-25 (ADR 0003 decision 4).** The ten contract clauses are doc-only;
`ClaimDueAsync`/`ClaimPendingAsync` gain `IReadOnlyCollection<string>? sagaTypes = null`, source-compatible
via `SagaLogEntry.cs:18-22`'s precedent. Both land in `persistence-contracts.md`, not here. The deferred
stranded-timeout listing on `ISagaAdminStore` remains deferred (§10).

### Q6 — Search case-sensitivity — **RESOLVED**

**Answered 2026-09-25 (ADR 0003 decision 1):** case-insensitive is the contract, EF moves to
provider-agnostic lowering of both column and term, on both disjuncts — **not** `EF.Functions.ILike`,
which is an Npgsql extension this package cannot reference. Lands in `persistence-contracts.md`.

### Q7 — Does a staged row survive to be committed by a *later* persist in the same scope? — **RESOLVED**

**Answered 2026-09-25 (ADR 0003 decision 2):** a staged row is committed by the next successful persist
in the same unit of work, and dropped if none succeeds — Mongo's behaviour, pinned as the contract. EF's
`SagaOrchestrator.cs:130`-false flush is therefore a defect and is scheduled as bug B2 in
`persistence-contracts.md` §4.

### Q8 — Timestamps: the `Date` + `Ticks` pair, or Ticks only?

Ticks-only is smaller and has one source of truth; the pair is readable in `mongosh` and keeps a
TTL-indexable field available. Since retention is dropped from v1 (§5.5), the TTL argument is weak.

**Recommendation:** the pair for v1 on operational-readability grounds, with a named follow-up to drop
the `Date` half. Both are written from one mapping function, so they cannot disagree.

### Q9 — One connection-string key or two?

`ConnectionStrings:VSaga` holds an Npgsql DSN in `appsettings.json`, `docker-compose.yml:37`/`:58`, and a
hardcoded fallback at `Program.cs:38-39`. A `mongodb://` URI cannot share that format, and handing the
wrong DSN to the wrong driver fails at connect time rather than config time.

**Recommendation:** separate `ConnectionStrings:VSaga` and `ConnectionStrings:VSagaMongo`, validated in
the switch. Documented gotcha: a single-node replica set advertising itself as `mongo:27017` is not
resolvable from the host, so host-side tooling on `localhost:27018` needs `directConnection=true`.

### Q10 — Server and platform support matrix

A topology check is necessary but nowhere near sufficient. **Amazon DocumentDB does not support partial
indexes**, which breaks both `ux_sagaType_businessKey` and `ix_parent` while passing any topology probe.
Cosmos DB's Mongo API in RU mode lacks the transaction scope this design assumes.

**Recommendation:** publish a minimum server version, the Atlas tiers actually tested, and an explicit
"not supported" line for DocumentDB and Cosmos Mongo API **with the reason**. Verify partial-index support
at bootstrap by actually creating the index rather than inferring it. Drop the `Sharded` arm from v1. Note
that Atlas M0 caps connections at 100 while the driver defaults to a 100-connection pool per client per
host, so two vSaga services with defaults saturate it — another argument for the `configureClient` hook.

### Q11 — Where does the ADR live? — **RESOLVED**

**Answered 2026-09-25:** `docs/adr/` is kept, numbered, distinct from the long-form plans in
`docs/design/`.

### Q12 — `TState` as a JSON string, or native BSON?

**Recommendation:** string for v1 (parity with EF is worth more than elegance for a first release), with
native BSON as a named follow-up whose entry gate is a fidelity test suite (long, decimal,
`DateTimeOffset`, enum, null-vs-missing). `payloadJson` and `headersJson` stay strings regardless — the
latter because envelope header keys are an open set containing dots and dashes
(`x-vsaga-delivery-attempt`, `SagaOrchestrator.cs:34`), the same reasoning `Entities.cs:115` already
records.

### Q13 — Health-check naming and ownership

The name is an operational contract: it is in `/health`'s JSON (`Program.cs:121-137`), asserted verbatim
at `HealthEndpointTests.cs:39`, documented at `docs/dashboard.md:25`, and behind the compose gate.

**Recommendation:** register from each `AddVSagaXxx` extension under a neutral `"persistence"` name, so
drift is structurally impossible — accepting the one-line test update, the doc update, and the fact that
moving `PostgresHealthCheck` into `VSaga.Persistence.EFCore` adds a
`Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` dependency to a shipped package.

---

## 10. Explicitly deferred, and named as such rather than built

Following `docs/design/mixed-sagas.md:556`'s pattern.

- **Provider-to-provider migration.** Out of scope. `docs/persistence.md` gains a blunt sentence:
  `Persistence:Provider` is a greenfield choice, not a live migration; switching it on a running system
  abandons every in-flight saga, leaves pending timeouts unfired in the old store, and leaves Pending
  outbox rows undrained.
- **Stranded-timeout ops tooling.** `dotnet/tools/BackfillStrandedTimeouts/Program.cs:16-24` is hardcoded
  to `VSagaDbContext` + `UseNpgsql` + `localhost:5433`, and bypasses `ISagaTimeoutStore` entirely because
  the contract has no "does a pending timeout already exist for this `(correlationId, forState)`?" query.
  If built: add that query to `ISagaAdminStore` (where operator-facing questions already live) rather than
  `ISagaTimeoutStore` (hot path, minimality deliberate), and rewrite the tool against the abstractions so
  one tool serves both providers. **Cheap interim mitigation shipped in Stage 6 instead:** the health check
  reports counts of Pending outbox rows older than 10× `DispatchGracePeriod` and Fired timeouts older than
  an hour — two `countDocuments` calls that make stranding visible rather than invisible.
- **Native BSON storage of `TState`** (Q12).
- **Change streams** as an opt-in push source for the dashboard, with the existing poller as the portable
  fallback. Rejected for v1: it requires a replica set *and* oplog-retention tuning *and* resume-token
  handling, and it would put provider-specific behaviour into `VSaga.Dashboard.Api`, falsifying
  `docs/dashboard.md:6-8`'s provider-agnostic premise.
- **Retention / TTL indexes** (§5.5).
- **A batch `AppendRangeAsync` contract** (R-11) — the correct fix if Stage 9 shows the two-round-trip
  append dominating latency, but a cross-provider abstractions change, not a Mongo implementation choice.
