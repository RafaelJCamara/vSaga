# Design: Redis persistence

**Status: implemented, 2026-09-26.** `VSaga.Persistence.Redis` is built, green on the full conformance
suite against Redis 7.4 plus its own Redis-specific cases, selectable through `Persistence:Provider` in
both hosts, and live-verified under `docker-compose.redis.yml` including a `kill -9` and unaided AOF
restart. §8.1 records what landed against each stage and the four places the build deviated from this
plan; §9 records how each blocking question was decided. The shipped shape is documented in
[`../persistence.md`](../persistence.md#redis); this file stays as the reasoning behind it, and the
decision it argued for is recorded in
[`../adr/0002-redis-persistence-provider.md`](../adr/0002-redis-persistence-provider.md).

**Read §1 before anything else.** Redis is not a peer of the EF Core/Postgres provider and this plan
does not claim it is. At Redis's default durability an *acknowledged* write can be lost, and the most
exposed write is the one the engine's redelivery-dedupe net rests on — the failure mode is a business
command executing twice, silently. That is stated here rather than in a risk table because it is the
single fact that should decide whether this provider is worth building.

Every claim about the current codebase carries a `file:line`. Line numbers are accurate at commit
`b2b99fd`; re-grep rather than trusting them once the tree moves.

---

## 1. Positioning — read this first

**Redis is asymmetrically good here, and the asymmetry is the design.**

For the three **claim-and-reserve** contracts it is the best fit of any provider vSaga has:

- `ISagaTimeoutStore.ClaimDueAsync` and `ISagaOutboxStore.ClaimPendingAsync` become a sorted set plus
  one Lua script — atomic, ordered by construction, non-blocking, and a **single round trip**. That
  matches Postgres's `UPDATE … FOR UPDATE SKIP LOCKED … RETURNING`
  (`EfCoreSagaTimeoutStore.cs:87-99`), beats the MongoDB plan's `findOneAndUpdate` loop
  ([`mongodb-persistence.md`](mongodb-persistence.md) §6.1), and fixes by construction the
  unordered-claim divergence the in-memory provider ships today (`InMemorySagaStore.cs:312-330`,
  `:378-396`).
- The business-key reservation is `SET key value NX` — literally "reserve before the step runs", which
  is what `docs/design/production-readiness.md` §5.2 asks for, expressed more directly than either
  a partial unique index or a `ConcurrentDictionary` dance.
- `AppendAsync` is **one `RPUSH`**, whose reply *is* the per-instance sequence number
  `ISagaEventLogStore.cs:14` already specifies. No counter key, no retry loop — strictly better than
  Mongo's `sagaSequences` collection and better-specified than EF's globally-ordered identity column.

For the two **record-keeping** contracts it is a poor fit, and no amount of engineering changes that:

- `ISagaEventLogStore` can **never be expired**, because `GetVisitedStatesAsync` derives the entire
  compensation set from it (`SagaOrchestrator.cs:902-911`) and `IsDuplicateAsync` is the redelivery
  dedupe (`:398`). On a RAM-resident store it therefore grows without bound and **RAM is the dataset
  ceiling** where on Postgres it is disk.
- `ISagaSummaryReader.ListAsync` wants a case-insensitive substring search over two independent fields,
  a sort with a stable tiebreak, offset paging, and an exact `TotalCount` over the *filtered* set
  (`EfCoreSagaSummaryReader.cs:10-53`). Core Redis has no secondary index for any of it. §5 shows this
  is solvable without a module — but only by hand-maintaining eight index structures and scanning.

### 1.1 The durability problem, stated concretely

This is the part that matters. Walk it through:

`SagaOrchestrator.cs:585` appends `MessageReceived`. The append returns as soon as Redis replies —
under `appendfsync everysec` that is **up to a second before the AOF fsync**, and under asynchronous
replication before any replica has it. The step then runs, rows are staged (`:727`), the persist
commits (`:731`), and `DrainDeferredPublishesAsync` publishes to the broker. **Kill the host now.**

The broker keeps the published messages. Redis rewinds. On redelivery — which
`HandleInfrastructureFailureAsync` (`SagaOrchestrator.cs:78-93`) explicitly reasons about, relying on
"a durable log entry for this exact message was already written" — `IsDuplicateAsync` at `:398` returns
**false**, because the append is gone. The step re-runs. Its `ctx.PublishAfterCommitAsync` goes through
`MessageEnvelope.From`, which mints `Guid.NewGuid().ToString("N")` (`MessageEnvelope.cs:50`), so the
*receiving* saga's own `IsDuplicateAsync` — keyed on `messageId` (`ISagaEventLogStore.cs:20`) — sees a
genuinely new message and processes it.

**A `ReserveInventory` or `ChargeCard` command executes twice, and nothing anywhere notices.**

This is not a benign replay. It is the reason §6 defines two configuration tiers rather than one, and
the reason Q6 raises a `VSaga.Core` change this plan does not itself schedule.

### 1.2 What this provider therefore is

**A durable single-node provider with a stated loss window — not a Postgres peer.**

- **Tier A** (`appendonly yes`, `appendfsync everysec`, `maxmemory-policy noeviction`, dedicated
  instance, single primary) is the default. Documented as losing **up to one second of acknowledged
  writes on a hard kill, and more on a failover**. Suitable for development, CI, single-node and edge
  deployments, and for teams who will not run Postgres and accept that window in exchange for one less
  piece of infrastructure.
- **Tier B** (`appendfsync always` plus `min-replicas-to-write 1` / `min-replicas-max-lag`) is opt-in.
  It closes the window at Postgres-class write latency while still offering none of Postgres's query
  model, and is the **only** configuration under which `docs/persistence.md` may call this provider
  production-tier.

**Redis Cluster is unsupported** — and that is a design choice, not a concession. Single-shard operation
is exactly what makes one Lua script an honest atomic unit for the snapshot, its staged outbox rows and
every summary index at once. See §4.6.

### 1.3 The sentence for `docs/persistence.md`

Pre-written, because `docs/persistence.md:3` said "vSaga ships two persistence providers" until this
provider landed; it now opens with the sentence below, verbatim:

> vSaga ships three persistence providers. EF Core/Postgres is the reference and the only one
> documented at full production tier by default. Redis is durable but RAM-bound and single-node: at its
> default configuration a hard kill loses up to one second of acknowledged writes, which for vSaga means
> a redelivered message can be reprocessed rather than deduped. Redis reaches production tier only under
> the Tier B configuration in *Redis → Durability policy*. In-memory remains dev/test only.

---

## 2. What it is

`VSaga.Persistence.Redis`, implementing the same seven contracts in
`dotnet/src/VSaga.Abstractions/Persistence/` as the two shipped providers. Built on
**`StackExchange.Redis`** (MIT), using **only core data types plus server-side Lua** — no Redis Query
Engine, no `FT.*`, no module dependency at all.

```csharp
AddVSagaRedis(Action<VSagaRedisOptions> configure,
              Action<ConfigurationOptions>? configureConnection = null)
```

The second parameter follows the precedent `docs/configuration.md:190-201` records for EF ("there is no
`VSagaEfCoreOptions` class, because EF Core already has one"): `ConfigurationOptions` is the only way to
reach TLS material, `AbortOnConnectFail`, `ConnectRetry`, multiplexer sizing, and the
`ConnectionMultiplexer` event hooks provider-level tracing needs.

### 2.1 What it is not

- **Not a Query Engine provider.** Rejected on correctness first (§5.7), platform availability second.
  The payoff is an unusually wide support matrix: anything that speaks core Redis ≥ 7.0, **including
  Valkey**, which matters more than it looks — see §9 Q2.
- **Not a Cluster provider** (§4.6).
- **Not a cache.** No key the provider owns ever carries a TTL, and `maxmemory-policy` must be
  `noeviction`. **Pointing this provider at a team's existing shared cache Redis is unsupported** — and
  since "we already run Redis" almost always means "we already run a cache tuned for eviction", this is
  the prerequisite most likely to be violated in practice.
- **Not a migration path**, and **not a second simultaneously-registered provider** — both for the same
  reasons as [`mongodb-persistence.md`](mongodb-persistence.md) §1.1. `AddVSagaEfCore` uses plain
  `AddScoped` (`VSaga.Persistence.EFCore/ServiceCollectionExtensions.cs:18-26`), so two provider
  extensions resolve last-one-wins per interface, silently, by call order.

### 2.2 The seven engine requirements, answered

Taking ADR 0001's "Context" list as given:

| # | Requirement | Redis answer | Verdict |
|---|---|---|---|
| 1 | Outbox rows and snapshot commit together (`ISagaOutboxStore.cs:41-49`) | Scoped staging buffer; one Lua script writes rows **and** snapshot **and** indexes | **Good** — atomic on one shard, no replica-set-class prerequisite |
| 2 | Event-log append durable *independently* of the persist (`SagaOrchestrator.cs:88-92` → `:398`) | `RPUSH` + `SADD`, outside every script | **Structurally right, physically weak** — §1.1. The defining weakness |
| 3 | Version mutated in place before serialising (`EfCoreSagaSnapshotStore.cs:58-61`) | Assigned in C# before serialising; restored on a non-success sentinel | **Good** — simpler than Mongo, the script never retries internally |
| 4 | Ordered reads + read-your-own-writes (`SagaOrchestrator.cs:589` → `:904`) | `RPUSH`/`LRANGE 0 -1`; single-threaded server makes read-your-own-writes free | **Excellent** |
| 5 | Business-key reservation adjudicates before either step runs | `SET …:bk:… NX` inside the insert script | **Excellent** |
| 6 | Atomic, ordered, non-blocking claim | ZSET + one bounded Lua script | **Excellent** — one round trip, beats all three alternatives |
| 7 | Stable total-ordered `UpdatedSince`-filterable list | ZSET, integer-ms score, member `{correlationId}\|{sagaType}` giving a total lexicographic tiebreak | **Good, and better than Postgres today** — §5.3 |

---

## 3. A bug this planning found

**Not Redis-specific. It is live in the shipped engine today, on Postgres.**

`EnqueueOutboxRowsAsync` keys each outbox row on `publish.Envelope.CorrelationId`, and the comment at
`SagaOrchestrator.cs:803-806` says exactly why:

> A row keyed on the publishing saga instead would have the recovery poller republish the message under
> the wrong identity.

That matters because `SagaOutboxDispatcherHostedService.cs:61` rebuilds the envelope from the **stored**
correlation id:

```csharp
var envelope = new MessageEnvelope(message.CorrelationId, message.MessageId, message.Headers);
```

But `StageChildSagaFinishedAsync` does the thing the comment warns against. Its envelope carries the
**parent's** id (`:939`, `MessageEnvelope.From(SagaType, parentCorrelationId, …)`) while its outbox row
is enqueued under `state.CorrelationId` — the **child's** (`:941`).

**Consequence.** If the engine's own `ChildSagaFinished` row is ever recovered by the outbox poller —
i.e. a crash between the persist committing and the inline drain marking it dispatched, which is
precisely the window the outbox exists for — it is republished under the child's correlation id. The
parent correlates on its own id, so it never receives the notification and hangs until its own state
timeout rescues it.

This is invisible on Postgres because `CorrelationId` is a plain column nothing filters the claim on. It
is not load-bearing for the Redis design either, because outbox keys are `messageId`-keyed (§5.6). But
it is a real defect and it belongs in Stage 0's written clauses. It should be fixed independently of
whether either of these providers is ever built.

---

## 4. Atomicity strategy

### 4.1 The unit of work

`RedisSagaUnitOfWork` — Scoped, one per DI scope, identical in shape to the Mongo plan's
`MongoSagaUnitOfWork` and for the same reason: `SagaRuntime.cs:25-27` opens a fresh scope per
message/timeout/retry, and EF holds a **change tracker**, not a transaction, across a message. It holds
a `List<RedisOutboxRow>` and nothing else — no `IDatabase`, no script handle, no connection.

**Peek-commit-clear, never take-then-write**, for the two reasons
[`mongodb-persistence.md`](mongodb-persistence.md) §4.3 establishes, which transfer verbatim.

### 4.2 The persist script is the sole committer

`RedisSagaSnapshotStore<TState>.InsertAsync`/`UpdateAsync` is the only code that runs it, exactly as
EF's `SaveChangesAsync` is the only committer. An empty buffer — the common case — simply means zero
outbox writes inside the script; there is no separate fast path to get wrong.

**Write order inside the script is load-bearing and is specified, not left to the implementer:**

```
1.  HSET  {ns}:torn  <sagaKey> <startTicks>       -- torn-write sentinel, FIRST
2.  version CAS / existence check                -- read-only; return sentinel and abort on mismatch
3.  business-key reserve (SET NX), and on update release of the old key
4.  HSET each staged outbox row hash             -- INVISIBLE: no index entry yet
5.  HSET the snapshot hash
6.  ZADD/ZREM every summary index in §5.2
7.  ZADD {ns}:ob:pending <createdMs> <messageId>  -- rows become publishable HERE
8.  HDEL  {ns}:torn  <sagaKey>                   -- LAST
```

**Steps 4 and 7 are deliberately split.** An outbox row hash that exists but is absent from
`{ns}:ob:pending` is garbage, not a phantom publish — `ClaimPendingAsync` reads only the ZSET. So a
script that dies anywhere before step 7 cannot produce the bug `SagaOrchestrator.cs:734-745` documents.

### 4.3 Lua has no rollback, so aborts are eliminated by construction

Redis does not roll back a partially-executed script. Two abort classes can strike mid-script:

- **A Lua runtime error** — eliminated by construction: all keys computed in C# and passed as `KEYS`, no
  dynamic key construction in Lua, no `cjson`, no unbounded loops, fixed arity, every script a fixed
  reviewed string.
- **Out of memory** — mitigated by a **pre-flight memory gate**: the provider reads
  `used_memory`/`maxmemory` on the health-check interval and refuses to run a persist script above
  `WriteMemoryThreshold` (default 0.90), throwing **before any write**. With `noeviction`, the failure
  is loud and pre-emptive rather than torn.

**Residual, stated rather than engineered away on paper:** at 100% memory an abort can still tear. The
`{ns}:torn` sentinel makes that *loud* — Redis is single-threaded and scripts are serialised, so the only
way a field survives there is a script that aborted. The health check reports `HLEN {ns}:torn` and goes
`Unhealthy` on any non-zero value, naming the affected `(sagaType, correlationId)` pairs. Two extra
commands per persist, converting the worst silent-failure class into an observable one.

### 4.4 Everything else stays outside every script

Event-log appends, `ScheduleAsync`, `CancelAsync`, `MarkDispatchedAsync`, both claim scripts and every
read run on their own. §2.2 requirement 2 is why the event log in particular *must*.

Enforced **structurally**, as in the Mongo plan: `RedisSagaEventLogStore` takes no dependency on
`RedisSagaUnitOfWork`; `RedisSagaOutboxStore.EnqueueAsync` takes no `IDatabase` at all.

### 4.5 `EVALSHA` / `NOSCRIPT` discipline

The script cache is volatile: cleared on restart, on `SCRIPT FLUSH`, and **when a replica assumes the
master role**. A `NOSCRIPT` is not a `SagaConcurrencyException`, so it would propagate to
`HandleInfrastructureFailureAsync` → redelivery **for every in-flight message simultaneously**, at the
exact moment the system is already degraded.

Every call is therefore `EVALSHA` → on `NOSCRIPT` → `SCRIPT LOAD` → retry **once**, in the provider,
not delegated to assumed client behaviour. A conformance test issues `SCRIPT FLUSH` mid-run and asserts
zero observable failures. The retry is safe because the payload is already serialised and the
`state.Version` assignment happens once, before the first attempt — contrast Mongo's
`WithTransactionAsync`, whose callback can run more than once.

### 4.6 Cluster is unsupported, and the reason is the outbox

`EVAL` requires every key to hash to one slot. The atomic unit is `{snapshot, its staged outbox rows,
every global index the write touches}`. The global indexes in §5.2 are single keys, so co-locating them
with any saga means one constant hash tag for the entire keyspace — one slot, one shard, zero write
scaling.

And outbox rows cannot be co-located with their saga **even in principle**: `SagaOrchestrator.cs:807`
keys them by the *envelope's* correlation id, and `SagaContext.cs:108` mints a fresh one for
`StartChildAsync`. Hash-tagging them to the publishing saga restores atomicity but then breaks
`ClaimPendingAsync` — a global, time-ordered claim across every instance — because no Redis command
scans all slots atomically.

So **Cluster mode is refused at bootstrap with a named error**, and `CROSSSLOT`/`MOVED`/`ASK`/`TRYAGAIN`
never appear at runtime.

### 4.7 Claim-loop stranding, stated honestly

A crash between the claim script returning and the dispatcher acting strands the batch already marked
terminal, in a local list that dies with the process. This is **exactly** Postgres's failure mode and
Mongo's. Redis is **not tighter**, and the plan must not claim it is.

---

## 5. Key-space and index design

### 5.1 Conventions

- **One configurable prefix carrying a constant hash tag**: `{vsaga:<namespace>}:…`. Cluster is
  unsupported, but the tag costs nothing and means a future Cluster story starts from "already one slot"
  rather than a key-format migration.
- **Every non-GUID key component is length-prefixed** (`…:{len}:{value}`). `sagaType`, `businessKey`,
  `forState` and `serviceName` are user-supplied with no enforced character set once `HasMaxLength`
  disappears, so `a|b` vs `a` + `|b` must not collide. Snapshot ids are GUID-first, for the reason
  [`mongodb-persistence.md`](mongodb-persistence.md) §5.1 gives.
- **Every timestamp exists twice: an integer-millisecond ZSET score, and exact `UtcTicks` in the owning
  hash, with Ticks authoritative for every comparison the caller can observe.** This is not fussiness. A
  ZSET score is an IEEE-754 double; today's `UtcTicks` ≈ 6.39 × 10¹⁷ is ~71× above 2⁵³, so the ULP is
  128 ticks = **12.8 µs**, making a ticks-valued score silently lossy *and non-injective*. Unix
  milliseconds (≈ 1.79 × 10¹²) fit exactly for ~285 000 years. `SagaListFilter.UpdatedSince` is
  contractually **strictly** greater (`SagaSummary.cs:37-47`), so range queries are **inclusive on the
  millisecond floor** and the exact-ticks predicate is re-applied in the provider. Over-fetching by
  ≤1 ms is correctness-safe; under-fetching is not. See R-3.
- **No `cjson` in any script.** Lua 5.1's `cjson` mangles 64-bit integers and does not preserve key
  order; `dataJson` is the user's serialised `TState` and must round-trip byte-identically. JSON stays
  opaque bytes to Redis — which is why `ResetStateAsync` is a single-shot read-patch-CAS in C# rather
  than one script. **Not** a retry loop: ADR 0003 decision 3 rejects retrying by name, so a lost CAS
  throws `SagaConcurrencyException`.
- **No key the provider owns may carry a TTL.** A contract clause, asserted in the conformance suite.
- **`{ns}:meta` holds `sv: 1`**, checked at bootstrap. `VSaga.Persistence.EFCore.Postgres/Migrations/`
  holds eight migrations in a month including a primary-key change; "no migrations" is a liability
  transfer, not a free benefit.
- **Enums stored numerically.** `EfCoreSagaSummaryReader.cs:49-50` sorts on `SagaStatus`'s *numeric*
  progression (`Running=0 … Cancelled=6`, `SagaStatus.cs:5-11`); string storage would reorder the
  dashboard's Status column alphabetically with no error.

### 5.2 The keys

| Key | Type | Holds |
|---|---|---|
| `{ns}:saga:{correlationId:D}\|{len}:{sagaType}` | HASH | the snapshot — projected fields + `dataJson` + `sv` |
| `{ns}:bk:{len}:{sagaType}\|{businessKey}` | STRING | the business-key reservation |
| `{ns}:log:{correlationId:D}\|{len}:{sagaType}` | LIST | the timeline, one JSON element per `SagaLogEntry` |
| `{ns}:dedupe:{correlationId:D}\|{len}:{sagaType}` | SET | inbound message ids only |
| `{ns}:to:{id}` / `{ns}:to:due` / `{ns}:to:for:…` | HASH / ZSET / STRING | timeout row, due-ordered claim set, the cancel lookup |
| `{ns}:ob:{len}:{messageId}` / `{ns}:ob:pending` | HASH / ZSET | outbox row, created-ordered claim set |
| `{ns}:ix:updated`, `:ix:status:{s}`, `:ix:type:…`, `:ix:kind:{k}`, `:ix:parent:…` | ZSET | summary indexes, score = `unixMs`, member = `{correlationId:D}\|{sagaType}` |
| `{ns}:ix:corr:{correlationId:D}` | SET | `FindByCorrelationIdAsync` |
| `{ns}:ix:types`, `{ns}:ix:typecount` | HASH | `GetSagaTypesAsync` |

**The member string is the design's quiet win.** `{correlationId:D}|{sagaType}` carries *both* fields
`SagaSummary.cs:34` says `Search` matches, independently. So `Search` is evaluable over index members
alone with **no snapshot read**, and the tie order within one millisecond is the members' lexicographic
order — **total and deterministic**. That is exactly the stable total order Stage 0 requires and that
both shipped providers currently lack (`EfCoreSagaSummaryReader.cs:44-53`,
`InMemorySagaStore.cs:169-178` break ties by `UpdatedAtUtc` only).

**No trigram index in v1.** It was designed and dropped: a 36-character GUID yields 34 trigrams over an
alphabet of `[0-9a-f-]`, so every trigram set holds ~0.7% of the keyspace and an intersection over a
long term is worse than the residual scan it replaces, while costing ~62 `SADD`s per insert and
unbounded RAM. Named as a follow-up gated on a *measured* p99.

### 5.3 `ListAsync`, written down rather than improvised

1. **Pick a driving index** by selectivity: `SagaType` > `Status` > `Kind` > `{ns}:ix:updated`.
   Remaining equality filters become residual predicates. **No `ZINTERSTORE`** — it writes, and a read
   path must not write.
2. **`UpdatedSince`**: `ZRANGEBYSCORE driving <floorUnixMs(since)> +inf`, inclusive on the floor, with
   the exact-ticks `>` predicate applied provider-side.
3. **Sort.** `UpdatedAt` (the poller's shape and the default) is a direct
   `ZRANGEBYSCORE`/`ZREVRANGEBYSCORE`. `Status` iterates the 7 buckets in enum order, reading each with
   `ZREVRANGEBYSCORE` so the `UpdatedAtUtc` tiebreak stays **descending in both directions**, matching
   `EfCoreSagaSummaryReader.cs:49-50` exactly; paging across buckets is arithmetic over `ZCARD`s. No
   composite score is attempted — a `status·2^k + ms` composite does not fit a double.
4. **`Search`**: case-folded substring test against the member string's two components. Contract-complete
   — case-insensitive, substring, both fields, independently, no minimum term length, no degradation.
   Bounded by `MaxSearchScanMembers` (default 100 000); exceeding it **throws** a named exception mapped
   to HTTP 400 rather than silently truncating a page. See Q5 — the thrown error is a new observable
   behaviour no other provider has.
5. **Paging and `TotalCount`.** The provider must **never return a short page while more matching rows
   exist**: `HasDrainedEveryChange` (`SagaChangePollingService.cs:183-184`) reads
   `Items.Count < PageSize` as "the filtered sequence is exhausted", and a false positive there is a
   *permanently dropped live update*. So the reader keeps pulling until it fills `PageSize` or the index
   is exhausted, and `TotalCount` is exact over the same filtered set.

This is the whole read surface, with no module and no drift — because every index write in §5.2 happens
**inside the same script as the snapshot write**.

### 5.4 Event log

`AppendAsync` is one `RPUSH`; its reply is the sequence number. `{ns}:dedupe:…` is written by the same
script **only** for `SagaStarted`/`MessageReceived` entries, matching
`EfCoreSagaEventLogStore.cs:41-49`'s narrowing, so `IsDuplicateAsync` is an O(1) `SISMEMBER`.

`GetTimelineAsync` is `LRANGE 0 -1` — O(entries), and it blocks the single-threaded server for that
duration. `GetVisitedStatesAsync` calls it on **every message** (`SagaOrchestrator.cs:589` → `:904`).
Trivial for a 30-entry saga; a real stall for a long-lived one — and unlike Postgres, that stall is
head-of-line blocking for every other saga in the process. See R-9.

### 5.5 Timeouts

`{ns}:to:for:…` exists so `CancelAsync`'s four-predicate scope (`ISagaTimeoutStore.cs:19-23` — sagaType
**and** correlationId **and** forState **and** Pending) is an O(1) lookup rather than a scan. Claiming
`ZREM`s from `{ns}:to:due`, so the ZSET only ever holds Pending members and the claim script needs no
status predicate.

### 5.6 Outbox

Keyed on `messageId` because both `MarkDispatchedAsync` and `DiscardPendingAsync` are messageId-keyed
(`ISagaOutboxStore.cs:54-62`, `:64-77`) and, as `:58-60` says, no database-generated id exists at
enqueue time. This also side-steps the §3 correlation-id disagreement entirely.

`destination` is **omitted when null** — a missing hash field reads back as nil, which is exactly what
`SagaOutboxDispatcherHostedService.cs:63-65` branches on. Where Mongo needed an explicit-null rule,
Redis gets it free.

### 5.7 Why not the Redis Query Engine

Rejected on **correctness first**: the Query Engine's index is updated asynchronously with respect to the
write, so a saga persisted and immediately listed may not appear — and the change poller's watermark
would advance past it, dropping the update permanently. Platform availability is a second, independent
reason: it is absent from AWS ElastiCache and MemoryDB, absent from Azure Cache's Basic/Standard/Premium
tiers, and absent from Valkey. Taking no module dependency is what makes the support matrix wide.

---

## 6. Durability policy

### 6.1 The two tiers as a configuration contract

| | **Tier A** (default) | **Tier B** (earns the production claim) |
|---|---|---|
| `appendonly` | `yes` | `yes` |
| `appendfsync` | `everysec` | `always` |
| `maxmemory-policy` | `noeviction` | `noeviction` |
| replication | ≥1 replica, async | ≥1 replica + `min-replicas-to-write 1`, `min-replicas-max-lag 10` |
| Cluster | refused | refused |
| Instance | dedicated | dedicated |
| **Loss on `kill -9`** | **up to 1 s of acknowledged writes** | none locally |
| **Loss on failover** | **unbounded by replication lag** | bounded; writes are *rejected* with `NOREPLICAS` rather than silently at risk |
| Write latency | Redis-class | **Postgres-with-`synchronous_commit=on`-class** |

`min-replicas-to-write` is preferred over `WAITAOF` because it converts the loss window into an
**up-front, observable rejection** rather than a per-call blocking wait whose short-ack case has only two
honest handlings, both bad. See Q7.

### 6.2 Configuration is validated, not documented

At bootstrap and on every health tick the provider issues `CONFIG GET appendonly`, `appendfsync`,
`maxmemory-policy`, `maxmemory`, plus `INFO replication` and `INFO memory`, and reports `Unhealthy` on:

- `maxmemory-policy != noeviction`, `appendonly != yes`, or Cluster mode — each naming the broken
  guarantee;
- any non-zero `HLEN {ns}:torn`;
- `used_memory / maxmemory > WriteMemoryThreshold`;
- **`CONFIG GET` being disabled** (as it is on several managed tiers) — naming the guarantee as
  *unverifiable*. Failing open here would reproduce `PostgresHealthCheck.cs:19-21`'s exact bug.

Re-probing every tick is required, not paranoid: `CONFIG SET maxmemory-policy` is a live change a
neighbouring operator can make at any moment.

Bootstrap is a **retrying hosted service, never fail-fast**, mirroring ADR 0001's Q4 decision — but the
health check stays `Unhealthy` until the probe passes, because `docker-compose.yml:70-74` gates
`order-processing` on `dashboard-api` being healthy.

### 6.3 AOF recovery is not automatic, and that must be verified

A script's effects replicate to the AOF wrapped in `MULTI`/`EXEC`. A hard kill can leave a *partial*
transaction, which Redis detects at restart and **refuses to boot on**, requiring `redis-check-aof
--fix`. (`aof-load-truncated yes`, the default, covers a truncated tail of a single command; the partial
-`MULTI` case is separate.) Postgres's WAL and MongoDB's journal recover automatically; Redis may not.

**Verify empirically against the pinned major as a stage gate, do not assert.** Stage 9's killed-host
scenario must include a **restart of the killed node**, not only a failover. If the measured result is
"manual intervention required to boot after `kill -9`", that sentence goes in §1.

### 6.4 Memory is the dataset ceiling, and the number gets published

Estimate, to be **replaced by a measured `MEMORY USAGE` figure** against the OrderProcessing sample as a
Stage 5 gate:

- Snapshot hash ≈ **1.5 KB** (`dataJson` exceeds the 64-byte `hash-max-listpack-value` default, forcing
  hashtable encoding).
- Event log ≈ **18–20 KB** for a 4-message saga (26–32 entries; the `SagaStarted` entry records the
  **full inbound message body** as `payloadJson`, `SagaOrchestrator.cs:393-396`).
- Dedupe set + index membership ≈ **650 B**.

**≈ 20–25 KB per completed 4-message saga ⇒ ~20–25 GB per million retained sagas**, monotonically
growing. On Postgres this is disk; on Redis it is RAM, and exceeding it under `noeviction` is a
system-wide write rejection that `HandleInfrastructureFailureAsync` converts into mass dead-lettering.
`docs/persistence.md` must publish a supported-sagas-per-GB figure, a required headroom percentage, and
the required alert.

### 6.5 No TTL, no retention — and RAM pressure does not reopen it

[`mongodb-persistence.md`](mongodb-persistence.md) §5.5 drops retention on **correctness** grounds and
that reasoning is repeated verbatim here. Two Redis-specific aggravations:

- Redis expiry is lazy-plus-sampled, so a TTL is not even a precise retention boundary.
- **Adding a TTL to any key makes it an eviction candidate under `volatile-*` policies before its TTL
  expires** — and `volatile-lru` is the default `maxmemory-policy` on the largest managed platforms.

The only retention shape ever considered is **terminal-saga archival** (copy the timeline to durable
storage, *then* delete), deferred with its own entry gate: the archived saga must be past the transport's
maximum redelivery window, because `IsDuplicateAsync` needs those inbound message ids for as long as a
redelivery is possible.

### 6.6 Error-to-exception mapping

Mis-mapping any of these silently abandons a timeout (`TryPersistOrLogRaceLossAsync` at
`SagaOrchestrator.cs:335-357` only *logs* and returns false) or discards committed work.

| Redis reply | VSaga exception |
|---|---|
| provider sentinel `-1` (version mismatch) | `SagaConcurrencyException` |
| provider sentinel `-2` (key absent) | `SagaNotFoundException` |
| provider sentinel `-3`/`-4` (id / business key taken) | `SagaAlreadyExistsException` |
| `NOSCRIPT` | retried once in-provider; recurrence → infrastructure |
| `OOM`, `LOADING`, `MASTERDOWN`, `BUSY`, `READONLY`, `NOREPLICAS` | infrastructure → redelivery |
| `WRONGTYPE`, Lua runtime error | infrastructure + torn-sentinel check + `Unhealthy` |
| `CROSSSLOT`, `MOVED`, `ASK`, `CLUSTERDOWN` | **bootstrap refusal**, never runtime |

**The rule, as a contract:** only the provider's own explicit numeric sentinels map to VSaga domain
exceptions. **Every** Redis error string is an infrastructure failure. Mapping an infrastructure error to
`SagaConcurrencyException` makes a timeout vanish with its side effects already sent; mapping a genuine
version mismatch to an infrastructure error produces an unbounded redelivery loop. Each row becomes a
conformance case.

One case has **no** mapping and must be documented rather than handled: a partitioned-but-unaware primary
accepts writes that are discarded when the partition heals. Acknowledged-then-erased data, invisible at
every call site. It is the reason Tier B exists.

---

## 7. Risks

**R-1 (blocker) — Tier A loses acknowledged event-log appends, and the resulting duplicate is
undedupable.** §1.1 walks it. **Not a benign replay.** Mitigation: state it in §1, ship Tier B, raise Q6.

**R-2 (blocker) — eviction silently deletes correctness-bearing state; `noeviction` is necessary but not
sufficient.** Under any other policy Redis deletes snapshots, timeout members and outbox rows with no
error at any call site (`FindAsync` returns null; `HandleTimeoutAsync` simply returns at
`SagaOrchestrator.cs:229-230`). Under `noeviction`, writes fail with `OOM` → mass dead-lettering. The
usual reason a team already runs Redis is a cache tuned for eviction. Mitigation: §6.2's re-probe, plus
"a shared or multi-tenant Redis is unsupported" as a written prerequisite.

**R-3 (blocker) — ZSET double scores quantise ticks and would silently drop live updates.** 12.8 µs of
collapse against a contractually strict-`>` predicate. Worst case is a **terminal status**. Mitigation:
§5.1's integer-ms score + exact-ticks authoritative + inclusive floor + provider-side exact predicate.

**R-4 (blocker) — a short page makes the change poller declare a truncated drain complete**
(`SagaChangePollingService.cs:183-184`, then `:200-207` advances past everything unread, permanently).
Mitigation: §5.3 step 5, conformance-tested with a continuous-mutation drain.

**R-5 (blocker) — key-prefix isolation is weaker than a Postgres schema or a Mongo database.** Cluster
supports only database 0; even standalone, `SELECT n` is a convention, not a boundary. A neighbouring
tenant's `FLUSHALL` or `CONFIG SET` is a total-loss event with no permission boundary. Mitigation: a
**dedicated instance** as a written prerequisite, with an ACL recommendation alongside.

**R-6 (blocker) — a Lua abort tears the write set with no rollback.** §4.3 eliminates both abort classes
and makes the residual loud, but the residual is real at 100% memory.

**R-7 (major) — RAM is the ceiling and the growth curve never turns over** (§6.4, §6.5).

**R-8 (major) — a hard crash can leave Redis refusing to start** (§6.3).

**R-9 (major) — single-threaded head-of-line blocking couples the dashboard to the saga hot path.** A
deep-offset page, a broad `Search`, or a `LRANGE` over a long timeline blocks *every* other command
including `UpdateAsync` on live sagas. Postgres runs the same query on a separate backend at zero cost to
the saga path. Aggravated by `SagaEndpoints.cs:31` accepting an **unbounded** `pageSize` (it only rejects
`<= 0`) — so that server-side clamp becomes a **hard prerequisite** of this provider, not a Stage 0
nicety. Note also that a write-script already past its first write **cannot be killed**: `SCRIPT KILL`
only works on read-only scripts, and the only escape is `SHUTDOWN NOSAVE`, which loses the unfsynced
window by definition.

**R-10 (major) — `StackExchange.Redis`'s transitive graph is a solution-wide restore-failure surface**
under `TreatWarningsAsErrors` + empty `<WarningsAsErrors />` (`Directory.Build.props:9-10`, precedent at
`:36-39`). A real `dotnet restore` is the Stage 1 gate.

**R-11 (major) — `NOSCRIPT` storms after a failover** (§4.5).

**R-12 (major) — the durability properties are not unit-testable** and the inherited conformance suite
cannot express any of them. Mitigation: the fault-injection tier, sequenced *before* any store code.

**R-13 (major, cross-provider) — `ClaimDueAsync` has no saga-type filter.** Pre-existing (Mongo's R-17),
but Redis amplifies it twice: a shared instance is the normal idiom, and the claim ZSET is a single key
on a single node.

**R-14 (major) — index drift, if atomicity is ever relaxed.** Not live in this design — every index write
is inside the snapshot's script. Recorded because the moment anyone proposes Cluster support the drift
shapes become live, and `SCAN`'s own guarantees ("a given element may be returned multiple times";
elements not constantly present "may be returned or not: it is undefined") rule out a sound
reconciliation sweep.

**R-15 (major) — claim-loop stranding.** §4.7. Not tighter than Postgres.

**R-16 (minor) — length constraints disappear**, mitigated by length-prefixing making the encoding
injective regardless, plus a documented soft cap.

**R-17 (minor) — `SagaType` exact-match is ordinal while `Search` is case-folded.** Both coexist cleanly
because the folding happens in the provider, not in an index — written down because that is exactly where
an implementer using a case-normalising index would silently diverge.

**R-18 (minor) — `GetSagaTypesAsync` is a maintained index, not a derivation.** Behaviourally identical
today because the engine never deletes snapshots; the counter exists so the dropdown does not accumulate
dead types if deletion is ever added.

---

## 8. Stages

Shared seams are **consumed, not re-planned** — see §10.

| # | Title | Gate |
|---|---|---|
| **0** | **Shared groundwork** — consume [`persistence-contracts.md`](persistence-contracts.md) unchanged (accepted, ADR 0003; no longer owned by either provider plan) | that plan's gate |
| **0b** | **Redis additions to Stage 0**: a fault-injection test tier (`SIGKILL` + restart, failover, `SCRIPT FLUSH`, memory exhaustion); the §3 outbox correlation-id clause; the no-TTL clause; the never-return-a-short-page clause | the harness actually kills a container and asserts, per `CONTRIBUTING.md:59-62` |
| 1 | Package skeleton, key encoders, options, script loader | a **real** `dotnet restore dotnet/VSaga.slnx` then `dotnet pack` (R-10) |
| 2 | Unit of work + snapshot store + the persist script | torn-sentinel test: inject a failure between step 5 and step 7 and assert the sentinel is set and health goes `Unhealthy` |
| 3 | Event log store | 50 concurrent appends yield 50 distinct increasing sequence numbers; `SISMEMBER` dedupe narrowing matches `EfCoreSagaEventLogStore.cs:41-49` |
| 4 | Timeout + outbox stores and the claim scripts | the four `PostgresEfCoreStoreTests` claim tests ported into the conformance suite, including 20 rows / 2 concurrent claimers with no overlap and no loss |
| 5 | Summary reader, admin store, topology store | a 2000-row multi-page ascending drain where every row shares one millisecond returns each row exactly once (R-3/R-4); **measured** bytes-per-saga and worst-query p99 published (R-7/R-9) |
| 6 | DI, retrying bootstrapper, config probe, health check | a `maxmemory-policy allkeys-lru` instance reports `Unhealthy` naming the guarantee; a `CONFIG GET`-disabled instance reports `Unhealthy`, not healthy |
| 7 | Conformance green + one end-to-end orchestrator-sequence test | whole suite green |
| 8 | `Persistence:Provider` seam | consume from Mongo if it landed; otherwise author it here |
| 9 | compose overlay + live verification | `kill -9` the Redis container mid-step, **restart it**, and record whether it boots (§6.3); plus a timeout firing and compensating |
| 10 | CI, packaging, docs | zero stale hits for "two persistence providers", "five suites"; package count stated as a **delta**, not an absolute |

### 8.1 What landed, 2026-09-26

Every stage from 1 to 10 landed; the record of the live run is
[`../history/redis-persistence-provider.md`](../history/redis-persistence-provider.md). Stage gates as
met: `dotnet restore` of the solution with `StackExchange.Redis [3.0.11,4.0.0)` under
`TreatWarningsAsErrors` (R-10); the torn-sentinel case injects the abort between steps 5 and 6 through
an internal script hook and asserts the sentinel and the Unhealthy verdict; 8 workers × 50 concurrent
appends yield 400 distinct ascending sequence numbers (the conformance suite's own case); the racing
claim cases run at 8 workers over 120 rows; a 2000-row drain over rows sharing one instant returns each
exactly once and the following watermark excludes them all; a `maxmemory-policy allkeys-lru` server, an
`appendonly no` server and a `CONFIG`-renamed server each report Unhealthy naming the guarantee;
measured bytes-per-saga are published in `persistence.md`; the `kill -9` was run and the node **booted
unaided** from its AOF in 0.028 s, so §6.3's "manual intervention" sentence did not have to be written.

**Stage 0b, the fault-injection tier, landed narrower than planned.** `SCRIPT FLUSH` mid-run, the torn
write, memory pressure (through the provider's gate, with the server's `maxmemory` set live) and the
three misconfigured servers are ordinary xUnit cases in `VSaga.Persistence.Redis.Tests`, so CI runs them
on every push. `SIGKILL`-and-restart and failover are **not** automated: they were run by hand against
the compose overlay as Stage 9's gate, and Q12's separate `Durability` project was not created.
A future run of the kill test is a documented manual step in `persistence.md`, not a trait-excluded
suite.

**Four deviations from this plan, each deliberate:**

1. **Timestamps are Unix microseconds, stored once, not milliseconds plus exact ticks** (§5.1, R-3).
   Every projected timestamp is one `int64` of Unix microseconds, used as both the sorted-set score and
   the hash field. Microseconds fit a double exactly until 2255, and one representation means the order
   an index yields and the value a predicate compares cannot disagree — so `UpdatedSince`'s strict `>`
   is an exclusive score range with no boundary re-check, and the poller's tie-group watermark logic
   sees the same values it sorts by. The cost is the truncation Postgres's `timestamp` already applies;
   the provider declares the same one-microsecond `TimestampResolution`. The plan's ms-plus-ticks shape
   would have ordered rows within a millisecond by member while filtering them by exact ticks, which is
   the inconsistency the poller's early-stop retreat cannot survive.
2. **Two scripts build a key.** The claim scripts and the cancel script append a row id read from a
   sorted set to a prefix passed in `ARGV` — the only way a claim is one round trip. String concatenation
   cannot raise, and Cluster is refused, so the rule's two purposes (no Lua runtime error, no
   `CROSSSLOT`) both still hold. The timeout id itself is `INCR`ed in C# before the schedule script so
   the row key is computed there like every other key.
3. **The persist script commits staged outbox rows alone in a third mode** (`2`), used only by the
   conformance fixture's `CommitAsync` hook, which needs to make a staged row durable without a snapshot
   write. The engine never reaches it; §4.2's "the persist is the sole committer" stands.
4. **The health check is registered by the host, not by `AddVSagaRedis`.** `RedisPersistenceHealthCheck`
   is registered in DI by the provider and added by `Dashboard.Api` under `"persistence"`, alongside
   `PostgresHealthCheck` under the same name for the EF branch. The Mongo plan's Stage 6 asked for the
   registration to live inside each provider's extension; that needs a `Microsoft.Extensions.Diagnostics.HealthChecks`
   (not `.Abstractions`) dependency in a persistence package and was left for that plan to decide.

---

## 9. Open questions

Q3 was partly resolved and Q9 mostly resolved by ADR 0003. **The five blocking questions were decided
on 2026-09-26, at Stage 1, as this section recommended;** each carries its resolution below.

### Q1 — What tier does this provider claim? *(blocking)*

**Options.** (1) Tier A only, documented as not-a-peer. (2) Tier A default + Tier B as the conditional
production configuration. (3) Tier B only — refuse to start below `appendfsync always`.

**Recommendation: (2)**, with §1.3's sentence pre-written. (3) is defensible but forecloses the dev/CI
use case that is half the point.

**Decided: (2).** §1.3's sentence opens `persistence.md`; the health check reports which tier the
server is running at (`durability tier A/B`), derived from `appendfsync` and `min-replicas-to-write`.

### Q2 — Which servers are supported, and is Cluster in scope? *(blocking)*

**Recommendation:** single primary + replicas only; Redis ≥ 7.0 **and Valkey ≥ 7.2** both supported;
Cluster refused at bootstrap. Ship a Supported-Servers table marking each row supported / unsupported /
untested **with the reason**, covering self-hosted Redis and Valkey, Redis Cloud, Azure Managed Redis and
Azure Cache tiers, AWS ElastiCache (Redis and Valkey), AWS MemoryDB, and GCP Memorystore. Verify
capability at bootstrap by *running a script*, not parsing a version string.

**Coupled: licensing.** Redis stopped being BSD in March 2024 (RSALv2/SSPLv1); Redis 8 added AGPLv3 as a
third option; Valkey is BSD-3 and is now the default Redis-compatible package in Debian/Ubuntu/Fedora, so
a large share of "we already run Redis" is in fact Valkey. Because this design takes **no module
dependency**, Valkey is a first-class target and the AGPL question is the operator's server choice, not
vSaga's. The shipped package depends only on MIT `StackExchange.Redis` and vSaga's own MIT licence is
unaffected — **say so explicitly** so no consumer has to guess. This repo already treats SDK licensing as
first-class (`Directory.Packages.props:15-17` bounds MassTransit below 9.0.0 for exactly this reason), so
silence would be inconsistent.

**Decided as recommended.** Single primary with replicas; Redis ≥ 7.0 and Valkey ≥ 7.2 supported; Cluster
refused by the probe (`cluster_enabled`), and a connection to a replica refused likewise. The
supported-servers table is in `persistence.md`, the managed platforms marked *untested* with the
`CONFIG GET` caveat rather than claimed. Capability is verified by running a script at bootstrap. The
package depends only on MIT `StackExchange.Redis` (`[3.0.11,4.0.0)`, with the licence note in
`Directory.Packages.props`), and both the package README and `persistence.md` say so.

### Q3 — Is Stage 0 a prerequisite, who owns it, and does it gain a fault-injection tier? — **PARTLY RESOLVED**

**Answered 2026-09-25:** the shared groundwork is accepted, extracted to
[`persistence-contracts.md`](persistence-contracts.md) (ADR 0003), and owned by **neither** provider plan
— so the ownership half of this question is gone.

**Still open, and still blocking for Redis:** the fault-injection tier. None of R-1, R-2, R-6 or R-8 is a
*behavioural* property the conformance suite can express — they are crash properties — so
`persistence-contracts.md` §7 explicitly leaves the tier here, as Stage 0b. **For Redis it is not a gate
on the argument, it is the argument**, and it must be sequenced before any store code.

**Resolved, narrower than asked** — see §8.1. The behavioural half of the tier (`SCRIPT FLUSH`, the torn
write, memory pressure, the misconfigured servers) is ordinary CI-run xUnit; `SIGKILL`-and-restart and
failover were run by hand as Stage 9's gate and stay a documented manual step. R-1 (the lost append) and
R-8 (a node that refuses to boot) therefore remain properties this repo has *observed once*, not ones its
CI guards.

### Q4 — Validate or override the operator's Redis configuration, and what when it cannot be seen? *(blocking)*

Mongo's Q3 in a different skin. `maxmemory-policy`, `appendonly`, Cluster mode, and never routing reads
to a replica are four silent-corruption paths no correctness test catches — and `CONFIG GET` is disabled
on several managed tiers, so the guarantee may be *unverifiable*.

**Recommendation:** override what the client controls (command flags, read routing to primary), probe
what it does not, and report `Unhealthy` — never crash — on contradiction **or on inability to probe**.

**Decided as recommended.** The client is left on its default primary-only routing and given
`AllowAdmin` (the probe needs `INFO` and `CONFIG GET`); `RedisServerProbe` probes the rest and never
throws; `RedisPersistenceHealthCheck` re-runs it on every call; a disabled `CONFIG GET` is reported as
*unverifiable* and Unhealthy. Three misconfigured containers pin the verdicts in CI.

### Q5 — Is `Search`'s bounded-scan behaviour acceptable, and does the bound belong in the contract? *(blocking)*

§5.3 step 4 delivers full contract fidelity with no degradation, at the cost of an O(candidates) scan
that blocks the single-threaded server and **throws** above `MaxSearchScanMembers`. That thrown error is
a new observable behaviour no other provider has; silently truncating would be the exact
plausible-wrong-answer class ADR 0001's drivers name as the enemy.

**Options.** (1) Throw, mapped to 400, limit configurable and documented. (2) Truncate plus a `Truncated`
flag on `PagedResult` — an abstractions change touching all providers. (3) Minimum term length of 3 plus
a trigram index.

**Recommendation: (1)** for v1, (2) as the right long-term shape, (3) gated on a measured p99. Whichever
is chosen, `SagaEndpoints.cs:31`'s missing server-side `pageSize` clamp becomes a hard prerequisite.

**Decided: (1).** `RedisSearchScanLimitExceededException`, bound `VSagaRedisOptions.MaxSearchScanMembers`
(default 100 000), documented in `persistence.md` as the one observable behaviour no other provider has;
the dashboard's list endpoint maps it to 400 with the provider's message. The `pageSize` clamp had
already landed as persistence-contracts F8. Option (2)'s `Truncated` flag stays open as the long-term
shape; option (3) stays gated on a measured p99 that no workload has yet produced.

### Q6 — Should `VSaga.Core` mint *deterministic* MessageIds for deferred publishes?

The only fix that makes step **re-execution** safe end to end. `MessageEnvelope.From` mints
`Guid.NewGuid().ToString("N")` (`MessageEnvelope.cs:50`), so a re-run step publishes a message the
receiving saga's `IsDuplicateAsync` cannot recognise (§1.1). Every provider benefits; Redis Tier A merely
makes the exposure routine rather than rare. **ADR 0001 asserts "`VSaga.Core` requires no changes" — for
Mongo that stays true; for Redis Tier A it is conditionally false, and that must be recorded rather than
silently inherited.**

**Recommendation:** out of scope as implementation, but a **gate** — Redis ships without it, and
`docs/persistence.md` may not describe Tier B as a Postgres peer until it lands. Proposed shape: derive
the MessageId from `(inbound messageId, publishing saga correlation id, ordinal within the step)`.

### Q7 — What is Tier B's mechanism, and does the client support it without stalling the multiplexer?

`StackExchange.Redis` deliberately does not offer blocking commands as first-class API, because they
stall the shared multiplexer every saga in the process is behind — and `WAIT`/`WAITAOF` guarantees are
scoped to *the current connection*.

**Recommendation:** `min-replicas-to-write` + `appendfsync always` (server-side, no per-call blocking),
with `WAITAOF` investigated in a **required spike before Stage 2** and adopted only if the measured
per-saga-step latency is published. If adopted, specify what happens on a short ack — the only honest
answers are "throw and redeliver a step whose write already applied" or "log and continue, silently
weaker than advertised". Pick one and write it down.

**Decided: server-side only.** Tier B is `appendfsync always` plus `min-replicas-to-write`, detected by
the probe and reported as the tier; no `WAITAOF` call exists in the provider, so the multiplexer is never
blocked and the short-ack dilemma never arises. The spike was not run; `WAITAOF` remains unadopted rather
than rejected.

### Q8 — Capacity model, ceiling behaviour, and retention

**Recommendation:** publish **measured** bytes-per-saga as a Stage 5 gate, plus a sagas-per-GB figure, a
headroom percentage, and the required alert. Retention stays out of scope on §6.5's grounds; terminal-saga
archival is the only shape considered and is deferred with its own entry gate.

### Q9 — Which `VSaga.Abstractions` changes, in which release? — **MOSTLY RESOLVED**

**Answered 2026-09-25 (ADR 0003):** the ten contract clauses are doc-only, and
`ClaimDueAsync`/`ClaimPendingAsync` gain `IReadOnlyCollection<string>? sagaTypes = null` — so **R-13 is
already fixed** by the accepted groundwork, and with it the contract-shape objection that permanently
blocked Cluster support (§4.6's other reasons stand on their own).

**Still open:** only the conditional `Truncated` flag on `PagedResult` (Q5 option 2), which is needed
solely if the bounded-scan-then-throw behaviour is rejected.

### Q10 — Client and bound

**Recommendation:** `StackExchange.Redis` (MIT, the de facto .NET client, and the one whose absence of
blocking-command API shapes Q7), bounded range with a comment matching `Directory.Packages.props`'s
convention. **No `NRedisStack`** — that package exists for the module commands this design does not use.

### Q11 — Namespace, schema marker, isolation rule

**Recommendation:** a mandatory configurable prefix carrying the constant hash tag, a `{ns}:meta` `sv`
marker checked at bootstrap, a documented **one-instance-per-service** rule, and a blunt sentence in
`docs/persistence.md` that prefix-only isolation is not a security boundary (R-5).

### Q12 — Where does the fault-injection harness live, and does CI run it?

`CONTRIBUTING.md:59-62` forbids silently skipping, but a `SIGKILL`-and-restart harness is slow and is not
a unit test. CI today is a flat `dotnet test` over `VSaga.slnx` with no matrix.

**Recommendation:** a separate `dotnet/tests/VSaga.Persistence.Durability` project, excluded from the
default run by trait, with its own CI job or a documented manual gate tied to Stage 9. Non-blocking, but
decide it before Stage 0b is scheduled.

**Decided: the documented manual gate, no separate project.** Everything a container can express without
being killed runs in `VSaga.Persistence.Redis.Tests` on every CI push; the kill-and-restart procedure and
its one measured result live in `persistence.md`.

---

## 10. Relationship to the MongoDB plan

### Shared, and authored exactly once

**Resolved 2026-09-25.** The largest shared seam — the contract clauses, the conformance suite and the
divergence fixes — was extracted to [`persistence-contracts.md`](persistence-contracts.md) and accepted
on its own (ADR 0003). It is owned by **neither** provider plan, so the "whichever lands first owns it"
coupling is gone for that seam.

Two smaller seams remain shared and unowned, because neither matters until a second production provider
exists: the `Persistence:Provider` switch, and the `PostgresHealthCheck` move + rename.

| Seam | Files |
|---|---|
| **Stage 0** — contract clauses, the conformance suite, the six divergence fixes | `VSaga.Abstractions/Persistence/*`, `VSaga.Persistence.EFCore/*`, `InMemorySagaStore.cs`, the two test projects, `SagaEndpoints.cs:31` |
| **`Persistence:Provider` switch** | `Dashboard.Api/Program.cs:38-45` and `:87-102`, the OrderProcessing sample, `docs/configuration.md` |
| **Health-check move + rename** to the provider-neutral `"persistence"` | `Program.cs:77-79`, `HealthChecks/PostgresHealthCheck.cs`, `HealthEndpointTests.cs:39`, `docs/dashboard.md:25` |

A fourth is shared in practice: `DashboardApiFactory`'s by-concrete-type `RemoveAll` calls become a
silent trap the moment `Program.cs` stops registering EF unconditionally.

**If Mongo starts first**, Redis consumes all three and its stage list starts at Stage 0b. **If Mongo is
rejected or deferred**, Redis inherits authorship of all three and its stage list grows by the Mongo
plan's Stage 0 and Stage 8 content. Redis's four Stage 0b additions are Redis-authored either way, but
written provider-neutrally and run against all providers.

### Where the two would conflict

| Conflict | Resolution |
|---|---|
| `dotnet pack` count: each plan's gate once named an absolute | state it as a **delta** — each provider adds one package — never an absolute, which unrelated packages move (the conformance suite already made it 17 before either provider landed) |
| Port slots in `docs/transports/index.md:125-130` | Mongo claims 27018 / 5580 / 6172-16172; Redis takes 6479 / 5680 / 6272-16272, conditional on the Mongo overlay existing |
| `docs/persistence.md:3`, `CONTRIBUTING.md:9-12`/`:59-62`, `docs/README.md`, `ci.yml:16` | both plans edit the same sentences; land coordinated |
| `VSaga.Abstractions` edits | Mongo scopes three, Redis adds one conditional — **one coordinated change, not two** |
| Both plans re-planning Stage 0 | **explicitly forbidden**; this plan's Stage 0 row says "consume unchanged" |

### Where they genuinely diverge in reasoning

- Mongo's atomicity needs a **replica set** as a hard prerequisite; Redis's needs a **single shard** —
  opposite constraints from the same requirement.
- Mongo needs a `sagaSequences` collection with a duplicate-key retry; Redis gets the sequence free from
  `RPUSH`'s return value. Redis's event-log **write** shape is strictly better; its **durability** is
  strictly worse.
- Mongo's claim is a loop costing up to `batchSize` round trips; Redis's is one script costing one. Redis
  wins here against every other provider.

---

## 11. Explicitly deferred, and named as such rather than built

- **Redis Cluster** (§4.6) — permanently blocked unless `ClaimDueAsync`/`ClaimPendingAsync` gain a
  scoping parameter.
- **Terminal-saga archival** — the only retention shape considered (§6.5).
- **A trigram search index** (§5.2) — gated on a measured p99.
- **Deterministic deferred-publish MessageIds** (Q6) — a `VSaga.Core` change this plan raises but does
  not schedule.
- **Provider-to-provider migration** — out of scope, as in the Mongo plan.
- **Redis as an accelerator alongside Postgres** — i.e. Redis serving only `ISagaTimeoutStore`,
  `ISagaOutboxStore` and the business-key reservation, with Postgres keeping the snapshot, event log and
  read side. This is arguably the *idiomatic* use and it plays to exactly the three contracts §1 finds
  Redis best at. It is deferred, not dismissed: it needs a split-registration story the DI extensions do
  not currently permit, and requirement 1's atomicity would then span two stores, which is the one thing
  the outbox contract cannot tolerate. Worth revisiting if the contracts ever gain an explicit
  unit-of-work seam.
