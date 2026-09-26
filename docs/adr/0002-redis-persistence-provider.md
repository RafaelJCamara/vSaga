# ADR 0002: Redis as a persistence provider

**Status:** **Accepted** — 2026-09-26, by the maintainer. **Implemented** 2026-09-26: `VSaga.Persistence.Redis` ships, passes the full conformance suite against Redis 7.4 plus the provider's own script-flush, torn-write, misconfiguration and same-instant-drain cases, is selectable through the `Persistence:Provider` switch in both hosts, and was live-verified under `docker-compose.redis.yml` including a `kill -9` and unaided AOF restart — the record is in [`../persistence.md`](../persistence.md#redis) and [`../history/redis-persistence-provider.md`](../history/redis-persistence-provider.md). The blocking questions were decided as the plan recommended (Q1 two tiers, Q2 single primary with Redis ≥ 7.0 and Valkey, Cluster refused; Q4 probe and report Unhealthy including on an unverifiable server; Q5 bounded scan that throws), with the deviations §8.1 of the plan now records. Accepting the ADR settled the *what*: a `VSaga.Persistence.Redis` provider on `StackExchange.Redis` with core data types and server-side Lua, positioned as the plan's §1 states — a durable single-node provider with a stated loss window, never a Postgres peer except under its opt-in tier. The plan's blocking questions Q1, Q2, Q4 and Q5 (tier claim, server and Cluster scope, configuration validation, the bounded-scan search) and the fault-injection tier of Q3 are decided in the plan, not here, and remain open until Stage 1 starts; the Stage 0 prerequisite landed in full on 2026-09-26 (ADR 0003).
**Date:** 2026-09-25
**Relates to:** [`0003-persistence-contract-clauses.md`](0003-persistence-contract-clauses.md) — the
accepted groundwork this depends on, owned by neither provider plan. Also
[`0001-mongodb-persistence-provider.md`](0001-mongodb-persistence-provider.md), with which it still
shares two unowned seams (the `Persistence:Provider` switch and health-check ownership); neither depends
on the other landing first.
**Implementation plan:** [`../design/redis-persistence.md`](../design/redis-persistence.md)

---

## Context

vSaga ships two persistence providers today (as of this decision; this ADR's own provider is the third),
both implementing the seven contracts in
`dotnet/src/VSaga.Abstractions/Persistence/`. ADR 0001's "Context" section enumerates the seven
properties the engine requires of any provider; that list is taken as given here rather than repeated.

The proposal is a third (or fourth) provider backed by Redis. The motivation is real — teams already
running Redis would rather not add Postgres solely for vSaga — but Redis differs from both existing
providers in two ways that dominate every other consideration.

### 1. Redis has no secondary index

`ISagaSummaryReader.ListAsync` requires a case-insensitive substring search over `SagaType` **and** the
correlation id's string form, *independently* (`SagaSummary.cs:34`), combined with equality filters, a
sort with a stable tiebreak, offset paging, and an exact `TotalCount` over the filtered set
(`EfCoreSagaSummaryReader.cs:10-53`). The Angular dashboard ships a live search box bound to it
(`typescript/dashboard-web/src/app/pages/saga-list/saga-list.html:23-26`), so degrading it is a visible
feature loss in a shipped UI, not a hypothetical.

Core Redis can serve none of this without hand-maintained index structures. The Redis Query Engine
(RediSearch) can — but its index is updated **asynchronously** with respect to the write, so a saga
persisted and immediately listed may not appear, and the dashboard's change poller would advance its
watermark past it and drop the update permanently. It is also absent from AWS ElastiCache and MemoryDB,
from Azure Cache's Basic/Standard/Premium tiers, and from Valkey.

### 2. Redis can lose an acknowledged write

At `appendfsync everysec` — the configuration people actually run — a write returns up to a second
before it is fsynced. Replication is asynchronous, so a failover can discard writes the client already
got an OK for.

For vSaga this is not a generic durability caveat, it is a specific correctness failure. The engine's
redelivery safety net republishes under the same MessageId and relies on the event-log entry for that
message already being durable, so the dedupe check recognises the redelivered copy
(`SagaOrchestrator.cs:78-93` → `:398`). If that append is lost, `IsDuplicateAsync` returns false and the
step re-runs. Its deferred publishes go through `MessageEnvelope.From`, which mints a fresh
`Guid.NewGuid().ToString("N")` (`MessageEnvelope.cs:50`) — so the *receiving* saga's own dedupe check,
keyed on message id (`ISagaEventLogStore.cs:20`), sees a genuinely new message and processes it.

**A `ReserveInventory` or `ChargeCard` command executes twice, and nothing anywhere notices.**

### 3. Where Redis is genuinely better than anything vSaga has

The picture is not one-sided, and the asymmetry is what makes the decision interesting rather than
obvious. For the three claim-and-reserve requirements Redis is the best fit of any provider:

- Atomic ordered claim (requirement 6) is a sorted set plus one Lua script: **one round trip**, ordered
  by construction. It matches Postgres's `SKIP LOCKED` statement, beats the MongoDB plan's
  `findOneAndUpdate` loop, and fixes by construction the unordered-claim divergence the in-memory
  provider ships today (`InMemorySagaStore.cs:312-330`, `:378-396`).
- The business-key reservation (requirement 5) is `SET key value NX` — literally "reserve before the
  step runs", expressed more directly than a partial unique index.
- `AppendAsync` is one `RPUSH` whose reply *is* the per-instance sequence number
  `ISagaEventLogStore.cs:14` specifies — no counter key, no retry loop, better than both shipped
  providers and better than the Mongo plan's `sagaSequences` collection.

---

## Decision drivers

- **Honest positioning over feature-count.** A third provider that reads as a Postgres peer while
  losing acknowledged writes is worse than no third provider. Whatever is shipped must be labelled
  accurately in `docs/persistence.md`.
- **Silent failure is the enemy** (inherited from ADR 0001). Redis raises the stakes: eviction deletes
  live sagas with no error at any call site, a quantised sort score drops live updates, and a lost
  append duplicates a business command — all invisible.
- **The repo's conventions are binding** — one logical change per commit with mutation testing, zero
  warnings, live verification for outbox/timeout work, and `CONTRIBUTING.md:59-62`'s rule that
  "compiled but never run" is not a pass.
- **The dashboard's provider-agnostic premise** (`docs/dashboard.md:6-8`) must survive.
- **Do not fork the shared seams.** The contract groundwork (now ADR 0003), the `Persistence:Provider`
  switch and the health-check rename are common to any third provider and must be authored once.

---

## Considered options

### A. Native `StackExchange.Redis`, core data types + Lua, no module — **chosen**

*Pros.* One Lua script gives genuinely atomic snapshot + staged outbox rows + every summary index on a
single shard — no replica-set-class prerequisite. The claim path is one round trip. The business-key
reservation and the sequence number fall out for free. Taking **no module dependency** makes the support
matrix unusually wide, and makes **Valkey a first-class target** — which matters more than it looks,
since Valkey is now the default Redis-compatible package in Debian/Ubuntu/Fedora, so a large share of "we
already run Redis" is in fact Valkey.

*Cons.* Eight index structures must be hand-maintained. `Search` becomes a bounded scan. Everything is
RAM-resident and unbounded. Durability is configuration-dependent. Cluster is out.

### B. Redis Query Engine (RediSearch) for the read side

*Pros.* `FT.SEARCH` gives filtering, sorting, paging and a total count in one call, with real substring
support via `WITHSUFFIXTRIE`. No hand-maintained indexes.

*Cons.* **Rejected on correctness, not only availability.** The index is updated asynchronously with
respect to the write, so the change poller can advance past a saga that had not yet been indexed and drop
its update permanently — precisely the silent-failure class the drivers name as the enemy. Availability is
an independent second reason: absent from ElastiCache, MemoryDB, Azure Cache's non-Enterprise tiers, and
Valkey — which would also forfeit option A's licensing answer.

**Rejected.**

### C. Redis Cluster

*Pros.* Horizontal scale; removes the single-node ceiling that RAM-boundedness implies.

*Cons.* Every key in a Lua script must hash to one slot. The global indexes are single keys, so
co-locating them with any saga means one constant hash tag for the whole keyspace — one slot, one shard,
zero scaling. And outbox rows cannot be co-located with their saga *even in principle*: they are keyed by
the **envelope's** correlation id (`SagaOrchestrator.cs:803-806`) and `StartChildAsync` mints a fresh one
(`SagaContext.cs:108`). Hash-tagging them to the publishing saga restores atomicity but breaks
`ClaimPendingAsync`, a global time-ordered claim across every instance, because no Redis command scans
all slots atomically.

**Rejected** — and refused at bootstrap rather than left to fail at runtime. Single-shard operation is
the precondition that makes option A's atomicity honest.

### D. Redis as an accelerator alongside Postgres, not a system of record

Redis serving only `ISagaTimeoutStore`, `ISagaOutboxStore` and the business-key reservation — the three
contracts it is best at — with Postgres keeping the snapshot, event log and read side.

*Pros.* Plays entirely to Redis's strengths and none of its weaknesses. Arguably the idiomatic use.

*Cons.* Not expressible today: `AddVSagaEfCore` uses plain `AddScoped`
(`VSaga.Persistence.EFCore/ServiceCollectionExtensions.cs:18-26`), so two provider extensions resolve
last-one-wins per interface, silently, by call order. More fundamentally, requirement 1's atomicity would
then span two stores — the one thing the outbox contract structurally cannot tolerate
(`ISagaOutboxStore.cs:41-49`).

**Deferred, not dismissed.** Worth revisiting if the contracts ever gain an explicit unit-of-work seam.
This is the most interesting rejected option and the design doc records it as such (§11).

### E. Reject the provider outright

*Pros.* Redis is a cache and a queue substrate, not a system of record; the two structural problems above
are inherent.

*Cons.* Option A demonstrably satisfies six of the seven requirements well and one (the event log's
*physical* durability) conditionally. The dev/CI and single-node/edge use cases are real. Rejecting
outright would discard a provider that is the best of the four at exactly the contracts the engine finds
hardest.

**Rejected** — but the reasoning is why the decision below ships with a tier, not a blanket claim.

---

## Decision

Build **`VSaga.Persistence.Redis` on `StackExchange.Redis`, using only core data types plus server-side
Lua** (option A). No module dependency. Registered via
`AddVSagaRedis(Action<VSagaRedisOptions>, Action<ConfigurationOptions>? = null)` and selected through the
shared `Persistence:Provider` switch.

**Atomicity** is a Scoped in-memory staging buffer plus one Lua script at persist time, flushed
non-destructively (peek → commit → clear), writing the snapshot, its staged outbox rows and every summary
index in one atomic step. The script's write order is specified, not left to the implementer: outbox row
*hashes* are written before the snapshot but their *pending-index entries* after it, so a script that
dies early leaves garbage rather than a phantom publish. A `{ns}:torn` sentinel written first and deleted
last makes the residual out-of-memory tear **loud** — the health check goes `Unhealthy` on any non-zero
`HLEN`.

**Event-log appends, claims, schedules and reads stay outside every script**, enforced structurally by
dependency shape, exactly as ADR 0001 does.

**Redis Cluster is refused at bootstrap.**

**The provider ships two documented configuration tiers, and the tier is the decision:**

- **Tier A** (`appendonly yes`, `appendfsync everysec`, `maxmemory-policy noeviction`, dedicated
  instance, single primary) is the default, documented as **losing up to one second of acknowledged
  writes on a hard kill and more on a failover** — with the duplicate-business-command consequence
  spelled out in the design doc's opening section, not a risk table.
- **Tier B** (`appendfsync always` plus `min-replicas-to-write 1` / `min-replicas-max-lag`) is opt-in and
  is the **only** configuration under which `docs/persistence.md` may call this provider production-tier.

**Redis is therefore documented as a durable single-node provider with a stated loss window, not as a
Postgres peer.** That sentence is pre-written in the design doc §1.3.

Configuration is **validated, not documented**: the provider probes `appendonly`, `appendfsync`,
`maxmemory-policy`, `maxmemory`, replication and cluster mode at bootstrap *and on every health tick*,
and reports `Unhealthy` — never crashes — on contradiction, **including when `CONFIG GET` is disabled and
the guarantee is therefore unverifiable**. Failing open there would reproduce
`PostgresHealthCheck.cs:19-21`'s exact bug.

The shared groundwork was **accepted and extracted** as
[`0003-persistence-contract-clauses.md`](0003-persistence-contract-clauses.md) on 2026-09-25 and is
consumed unchanged — it is owned by neither provider plan. Redis adds four items on top, chiefly a
**fault-injection test tier** (`SIGKILL` + restart, failover, `SCRIPT FLUSH`, memory exhaustion), because
none of Redis's defining risks is a behavioural property a conformance suite can express. ADR 0003 also
already delivers the `ClaimDueAsync` saga-type filter, so R-13 is fixed before this provider starts.

---

## Consequences

### Positive

- The best claim-and-reserve implementation in the codebase: one round trip, ordered by construction,
  fixing a live in-memory divergence structurally rather than by test.
- `ListAsync` gains a genuinely **total** order for free — the index member string
  `{correlationId}|{sagaType}` is a deterministic lexicographic tiebreak, which both shipped providers
  currently lack.
- `Search` ships **contract-complete** — case-insensitive, substring, both fields, independently — with
  no module and no degradation, because the member string carries both searchable fields.
- Index drift is structurally impossible: every index write happens inside the same script as the
  snapshot write.
- No module dependency means a wide support matrix, **Valkey as a first-class target**, and a clean
  licensing answer: the package depends only on MIT `StackExchange.Redis`, and the server's licence
  (RSALv2/SSPL/AGPL for Redis, BSD-3 for Valkey) is the operator's choice, not vSaga's.
- The torn-write sentinel converts the worst silent-failure class into an observable one.

### Negative

- **At Tier A an acknowledged write can be lost, and the resulting duplicate is undedupable** — a
  business command executes twice, silently. This is the defining consequence and the reason the tier
  exists.
- **RAM is the dataset ceiling** — roughly 20–25 KB per completed four-message saga, so ~20–25 GB per
  million retained sagas, growing monotonically with **no correctness-safe pruning mechanism**, because
  the event log feeds both compensation and dedupe.
- **Eviction is a live hazard**: under any policy but `noeviction`, Redis deletes snapshots, timeout
  members and outbox rows with no error at any call site. And the usual reason a team already runs Redis
  is a cache tuned for eviction — so the most likely deployment is the unsupported one.
- **A dedicated instance is a hard prerequisite**, not a recommendation: key-prefix isolation is weaker
  than a Postgres schema or a Mongo database, and a neighbouring tenant's `FLUSHALL` is a total-loss
  event with no permission boundary.
- **Single-threaded head-of-line blocking couples the dashboard to the saga hot path** — a deep page, a
  broad search, or a `LRANGE` over a long timeline blocks every other command including live
  `UpdateAsync`s. A write-script past its first write cannot even be killed.
- **A hard crash can leave Redis refusing to start** (a partial `MULTI` in the AOF needs
  `redis-check-aof --fix`), where Postgres's WAL and Mongo's journal recover automatically.
- Redis Cluster is permanently out unless the claim contracts gain a scoping parameter.
- Tier B's write latency is Postgres-class — so the configuration that earns the production claim gives
  up the performance people choose Redis for, while still offering none of Postgres's query model.
- `docs/persistence.md` must carry a support matrix, a capacity model, and a write-concern table that no
  other provider needs.

### Neutral

- `SagaLogEntry.SequenceNumber` is per-instance, which is what `ISagaEventLogStore.cs:14` already
  documents — EF and in-memory are the ones diverging.
- Timestamps are stored twice: an integer-millisecond ZSET score plus exact `UtcTicks` in the owning
  hash, Ticks authoritative. Not fussiness — a ticks-valued double score has a 128-tick (12.8 µs) ULP,
  which against a contractually strict-`>` watermark silently drops live updates.
- `TState` stays a `System.Text.Json` string, as in every other provider.
- `destination` round-trips as an absent hash field, giving null-vs-missing for free where Mongo needed
  an explicit rule.

---

## What would invalidate this decision later

1. **`VSaga.Core` mints deterministic MessageIds for deferred publishes.** That makes step re-execution
   idempotent end to end and removes Tier A's defining consequence — at which point Redis's positioning
   changes materially. ADR 0001 asserts "`VSaga.Core` requires no changes"; for Redis Tier A that is
   conditionally false, and the design doc records it as a question raised rather than silently
   inherited.
2. **The contracts gain an explicit unit-of-work seam**, making option D (Redis as an accelerator
   alongside Postgres) expressible. That is the shape that plays to Redis's strengths and none of its
   weaknesses.
3. **`ClaimDueAsync`/`ClaimPendingAsync` gain a saga-type scoping parameter.** Cluster support is blocked
   by the contract shape, not by the provider — this is what would unblock it.
4. **A synchronously-indexed query engine becomes universally available** (including on Valkey and the
   major managed platforms). Option B's correctness objection is the primary one; availability is
   secondary, and both would need to fall.
5. **Measured durability turns out better or worse than assumed.** The AOF-restart behaviour (§6.3) is
   scheduled as an empirical stage gate precisely because asserting it would be unsafe. If a `kill -9`
   requires manual intervention to boot, Tier A's positioning weakens further.
6. **Retention becomes expressible without breaking compensation.** Terminal-saga archival is the only
   shape considered; if it ships, the RAM ceiling stops being monotonic and the capacity argument changes.
7. **Redis's licensing shifts again, or Valkey and Redis diverge functionally.** The no-module decision is
   what keeps both as targets; that stops being free if their core command sets drift.

---

## Open questions blocking acceptance

Five, stated in full with options and recommendations in the design doc's §9:

| | Question | Recommendation |
| --- | --- | --- |
| **Q1** | What tier does this provider claim, and what exact sentence goes in `docs/persistence.md`? | Tier A default + Tier B as the conditional production configuration |
| **Q2** | Which servers are supported, and is Cluster in scope? | Single primary + replicas; Redis ≥ 7.0 and Valkey ≥ 7.2; Cluster refused at bootstrap |
| **Q3** | ~~Prerequisite? Who owns it?~~ **RESOLVED** (ADR 0003, owned by neither plan). Still open: does it gain a fault-injection tier? | Yes — and for Redis it is not a gate on the argument, it *is* the argument |
| **Q4** | Validate or override the operator's Redis configuration, and what when it cannot be seen? | Override what the client controls, probe the rest, `Unhealthy` on contradiction **or** on inability to probe |
| **Q5** | Is `Search`'s bounded-scan-then-throw behaviour acceptable? | Throw, mapped to 400; `SagaEndpoints.cs:31`'s missing `pageSize` clamp becomes a hard prerequisite |

Seven further non-blocking questions (Q6–Q12) are recorded there, including Q6's proposed `VSaga.Core`
change. Q9 is mostly resolved by ADR 0003.

---

## A defect found while writing this

Not Redis-specific — **live in the shipped engine today, on Postgres.**
`StageChildSagaFinishedAsync` builds its envelope with the *parent's* correlation id
(`SagaOrchestrator.cs:939`) but enqueues the outbox row under `state.CorrelationId`, the *child's*
(`:941`). `SagaOutboxDispatcherHostedService.cs:61` rebuilds the envelope from the **stored** id, which is
exactly what the comment at `SagaOrchestrator.cs:803-806` warns against: "A row keyed on the publishing
saga instead would have the recovery poller republish the message under the wrong identity."

So a recovered `ChildSagaFinished` is republished under the child's correlation id and the parent never
receives it, hanging until its own state timeout rescues it. It should be fixed independently of whether
either provider is ever built. Recorded in the design doc §3.
