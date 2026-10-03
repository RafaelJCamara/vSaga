# Persistence

vSaga ships four persistence providers. EF Core/Postgres is the reference. MongoDB is its
production-tier peer for teams already standardised on it: majority-acknowledged writes and a
multi-document transaction per persist, at the cost of a hard replica-set prerequisite — see
[MongoDB](#mongodb). Redis is durable but RAM-bound and single-node: at its default configuration a
hard kill loses up to one second of acknowledged writes, which for vSaga means a redelivered message
can be reprocessed rather than deduped. Redis reaches production tier only under the Tier B
configuration in [Redis → Durability policy](#durability-policy). In-memory remains dev/test only.

All four implement the same set of store contracts (`VSaga.Abstractions.Persistence`):
`ISagaSnapshotStore<TState>`, `ISagaSummaryReader`, `ISagaEventLogStore`, `ISagaTimeoutStore`,
`ISagaOutboxStore`, `ISagaAdminStore`, and `IServiceTopologyStore`. The hosts pick one of the three
durable providers with the [`Persistence:Provider`](configuration.md#persistenceprovider--picking-the-store)
switch (`Postgres`, `Redis` or `MongoDb`); in-memory is not one of its values.

All four are held to the same written contracts by the cross-provider `VSaga.Persistence.Conformance`
suite, which a third-party provider can run against itself; its
[README](../dotnet/tests/VSaga.Persistence.Conformance/README.md) covers the `IProviderFixture` to
implement and the xUnit v2 requirement. The eight divergences and three shared defects the first two
once had are catalogued, with their fixes, in
[`design/persistence-contracts.md`](design/persistence-contracts.md) §1 and §3; all are fixed.

Each provider has a small runnable sample in
[`dotnet/samples/Persistence/`](../dotnet/samples/Persistence/): the same saga on each store, the
provider's wiring marked out in its `Program.cs`, and a compose file for its database.

**The dashboard's identity store is not one of these.** The dashboard API keeps its users, teams, roles, grants
and the key ring that protects its sessions in a SQLite file of its own (`Dashboard:Identity:Sqlite:Path`, on
the `vsaga-dashboard-identity` volume in compose), whatever `Persistence:Provider` says. It implements none of
the store contracts above, the engine hosts never read it, and it has its own migrations and its own backup
story: see [`dashboard.md`](dashboard.md#the-identity-store).

## EF Core / Postgres

`VSaga.Persistence.EFCore` implements every store against `VSagaDbContext` and is **provider-agnostic**
— it takes no dependency on any specific database provider, only on `Microsoft.EntityFrameworkCore`,
`Microsoft.EntityFrameworkCore.Relational`, and `Microsoft.Extensions.DependencyInjection.Abstractions`.
That `.Relational` reference is a real (if narrow) constraint: any *relational* provider works, but
non-relational EF Core providers are out of scope — `EfCoreSagaTimeoutStore` uses `FromSqlInterpolated`,
which relational providers alone support.
`AddVSagaEfCore(this IServiceCollection, Action<DbContextOptionsBuilder> configureDbContext)`
registers `VSagaDbContext` **Scoped** (a fresh `DbContext` per message/timeout/retry, matching how the
rest of the engine resolves per-unit-of-work services) plus EF-backed implementations of all seven
store contracts. Pass the actual provider hookup (`UseNpgsql`, `UseSqlServer`, `UseSqlite`, ...)
yourself via `configureDbContext`.

**Postgres-specific migrations live in a separate project**, `VSaga.Persistence.EFCore.Postgres`, kept
apart from `VSaga.Persistence.EFCore` specifically so the latter stays provider-agnostic. Because of
that split, `UseNpgsql` alone is not enough — EF Core looks for migrations in the `DbContext`'s own
assembly by default, which is `VSaga.Persistence.EFCore` and has none, so `MigrateAsync()` silently logs
"no migrations were applied" and every table is missing. Point `MigrationsAssembly` at the Postgres
project instead:

```csharp
services.AddVSagaEfCore(db => db.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsAssembly("VSaga.Persistence.EFCore.Postgres")));
```

Apply them with `db.Database.MigrateAsync()` at startup — not `EnsureCreatedAsync()`, which does not
apply migrations and leaves a database schema untracked by them:

```csharp
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VSagaDbContext>();
    await db.Database.MigrateAsync();
}
```

(This is what `VSaga.Dashboard.Api`'s own `Program.cs` does — see there for the non-fatal try/catch
around it, useful if the app might start before Postgres is reachable, and for its
`GetService<VSagaDbContext>()` guard, which skips the step when `Persistence:Provider` selects Redis or
MongoDB.) See `dotnet/src/VSaga.Persistence.EFCore.Postgres/Migrations/` for the migration history:
identity scoping to `(SagaType, CorrelationId)`, the Saga Map's service-map fields, sub-saga
parent-linkage columns, the outbox table (plus its own follow-up index migration), the business-key
column with its partial unique index, and the `SagaInstances.UpdatedAtUtc` index the dashboard's change
poller needs (eight migrations in total).

**The five tables** `VSagaDbContext` maps, for anyone querying the database directly:

| Table | Holds |
| --- | --- |
| `SagaInstances` | One row per saga instance (the snapshot), keyed by `(SagaType, CorrelationId)`. `DataJson` holds the whole serialized `TState`; the other columns are a queryable projection of it — see [`adr/0005-saga-state-storage-model.md`](adr/0005-saga-state-storage-model.md). |
| `SagaEventLog` | The append-only `SagaLogEntry` timeline behind the dashboard (see [`observability.md`](observability.md)). `EntryType` is stored as its number: `21` is `StatePersisted`, whose `PayloadJson` is a copy of `SagaInstances.DataJson` as it stood after a step (see [State snapshots](observability.md#state-snapshots)); `SagaStarted`, `MessageReceived` and `StepFailed` rows carry the inbound message body. |
| `SagaTimeouts` | Scheduled/fired timeouts, claimed by the dispatcher below. |
| `SagaOutboxMessages` | Transactional-outbox rows, staged with the snapshot and drained inline or by the poller. |
| `SagaConsumerRegistrations` | The service topology (`IServiceTopologyStore`), keyed by `(ServiceName, MessageType)`. |

**Every `DateTimeOffset` is stored as a UTC `DateTime`.** A global convention
(`VSagaDbContext.cs:17-20`) applies `DateTimeOffsetToUtcDateTimeConverter` to every `DateTimeOffset`
property, so on Postgres the column truncates to microsecond resolution while the same value inside
`DataJson` keeps full 100-nanosecond ticks. Compare the two at storage resolution, never for exact
equality.

**Concurrency-safe claiming — Postgres only.** `EfCoreSagaTimeoutStore.ClaimDueAsync` and
`EfCoreSagaOutboxStore.ClaimPendingAsync` (two separate implementations, one per store) each use an
atomic `UPDATE ... WHERE ... FOR UPDATE SKIP LOCKED ... RETURNING`, so multiple
`SagaTimeoutDispatcherHostedService`/`SagaOutboxDispatcherHostedService` instances (or replicas) can
poll concurrently without double-claiming a row.

`ClaimDueAsync` also takes the saga types the caller can dispatch, filtered inside that locking subquery:
`SagaTimeoutDispatcherHostedService` passes the ones it has runtimes for, so a process hosting some of
the saga types sharing a database never fires — and so loses — the other services' timeouts.
`ClaimPendingAsync` deliberately has no such filter; the outbox dispatcher republishes raw bytes and can
act on every row.

> **This applies to Postgres and nothing else.** The choice is an exact string comparison against
> `"Npgsql.EntityFrameworkCore.PostgreSQL"` (`EfCoreProviderNames.Npgsql`, tested by
> `EfCoreSagaTimeoutStore.ClaimDueAsync` and `EfCoreSagaOutboxStore.ClaimPendingAsync`). **Every** other EF Core provider — including `UseSqlServer`,
> suggested above — silently takes a plain load-then-update fallback that is correct for exactly one
> dispatcher instance. Two replicas on any other EF Core provider will fire the same timeout twice and
> publish the same outbox row twice, with no error anywhere. See
> [`adr/0004-postgres-only-atomic-claim.md`](adr/0004-postgres-only-atomic-claim.md). (The Redis and
> MongoDB providers claim atomically and are not affected.)

**Claiming marks the row terminal, so redelivery is at-most-once.** A claim marks a timeout `Fired` and
an outbox row `Dispatched` as part of the claim itself. If the dispatcher then fails to act on it, the
row is not retried (`SagaOutboxDispatcherHostedService.cs:42-45`). "Outbox" often implies the opposite;
it does not here.

**Optimistic concurrency.** `SagaInstances.Version` is the concurrency token
(`VSagaDbContext.cs:105`). `ISagaSnapshotStore.UpdateAsync` takes the version the caller read and throws
`SagaConcurrencyException` if the stored row has moved on — this is what stops two concurrent messages
for one saga instance from corrupting its state.

### The volume caveat

`docker-compose.yml`'s named Postgres volume (`vsaga-postgres-data`) is **not reset** by `docker
compose up` — it persists across restarts and rebuilds, by design, so saga history survives a
redeploy. Two consequences worth knowing:

- **Counting/filtering live data** for any kind of before/after comparison must filter by
  `createdAtUtc`/`updatedAtUtc` after the container's own start timestamp, or stale rows from a
  previous run pollute the counts. Use `docker compose down -v` for a genuinely clean read.
- **A volume created before the EF Core migrations pass** was bootstrapped with the old
  `EnsureCreatedAsync()` schema, which `MigrateAsync()` will not apply cleanly against — run `docker
  compose down -v` once before bringing the stack back up if your volume predates the migrations pass.
  This does **not** apply to a volume that has already had the versioned migrations applied at least
  once: those upgrade in place, and wiping one only costs you saga history, not correctness.

The dashboard's `vsaga-dashboard-identity` volume is a second, separate one. It holds the dashboard's users and
session keys, not saga data, so wiping only the Postgres volume leaves every user in place, and `docker compose
down -v` removes both (the demo administrator is then seeded again on the next start).

## Redis

`VSaga.Persistence.Redis` (`AddVSagaRedis(Action<VSagaRedisOptions>, Action<ConfigurationOptions>? = null)`)
implements every store on **core Redis data types plus server-side Lua**, through `StackExchange.Redis`
(MIT). No module dependency: anything that speaks core Redis ≥ 7.0 works, **Valkey included**, and the
package's own licence is unaffected by the server's (RSALv2/SSPLv1/AGPLv3 for Redis, BSD-3 for
Valkey). The decision is recorded in [`adr/0002-redis-persistence-provider.md`](adr/0002-redis-persistence-provider.md)
and the plan it was built from in [`design/redis-persistence.md`](design/redis-persistence.md).

```csharp
services.AddVSagaRedis(o =>
{
    o.ConnectionString = "redis:6379";   // StackExchange.Redis's own format, not ADO.NET's
    o.Namespace = "orders";              // every key is prefixed {vsaga:orders}:
});
services.AddHealthChecks().AddCheck<RedisPersistenceHealthCheck>("persistence");
```

Options are in [`configuration.md`](configuration.md#vsagaredisoptions-vsagapersistenceredis). The
second parameter hands you the client's `ConfigurationOptions` for TLS, `AbortOnConnectFail`, retries
and multiplexer sizing, on the same reasoning EF Core's `DbContextOptionsBuilder` is exposed rather than
wrapped.

**What it is not.** Not a cache: no key the provider owns ever carries a TTL, and `maxmemory-policy` must
be `noeviction` — pointing it at a team's existing shared cache Redis is unsupported, and since "we
already run Redis" usually means a cache tuned for eviction, that is the prerequisite most likely to be
violated. Not a Cluster provider: Cluster mode is reported Unhealthy (see the health check below). Not a
migration path: `Persistence:Provider` is a greenfield choice.

### Where Redis is better, and where it is worse

For the three **claim-and-reserve** contracts it is the best fit of any provider vSaga has:

- `ClaimDueAsync` and `ClaimPendingAsync` are a sorted set plus one Lua script — atomic, earliest-first
  by construction, one round trip, and safe for any number of dispatcher replicas. The Redis provider
  declares `SupportsConcurrentClaim` and passes the same racing-dispatcher conformance cases Postgres
  does (ADR 0004's Postgres-only caveat does not apply here).
- The business-key reservation is `SET … NX` — literally "reserve before the step runs".
- `AppendAsync` is one `RPUSH`, whose reply *is* the per-instance sequence number.

For the two **record-keeping** contracts it is a poor fit, by the nature of the store:

- The event log can never be expired (it feeds compensation and the redelivery dedupe), so **RAM is the
  dataset ceiling** — see the capacity model below.
- `ListAsync`'s search is a scan (see [Search](#search)); its filters and sorts are answered from
  hand-maintained indexes written inside the same script as the snapshot, so they can never drift.

### Durability policy

**This is the part that decides whether the provider is for you.** `SagaOrchestrator` appends
`MessageReceived` to the event log, then runs the step, then persists. The append returns as soon as
Redis replies — under `appendfsync everysec` up to a second before the AOF fsync. Kill the host in that
second and the broker still holds the messages the step published, but Redis rewinds: on redelivery,
`IsDuplicateAsync` says "not seen", the step runs again, and its publishes carry fresh message ids the
receiving sagas cannot dedupe either. **A `ReserveInventory` or `ChargeCard` command executes twice,
and nothing anywhere notices.** That is why the provider ships two configuration tiers, and why the
tier is the decision:

| | **Tier A** (default) | **Tier B** (earns the production claim) |
| --- | --- | --- |
| `appendonly` | `yes` | `yes` |
| `appendfsync` | `everysec` | `always` |
| `maxmemory-policy` | `noeviction` | `noeviction` |
| replication | optional, async | ≥ 1 replica **and** `min-replicas-to-write 1`, `min-replicas-max-lag 10` |
| instance | dedicated | dedicated |
| Cluster | unsupported (Unhealthy) | unsupported (Unhealthy) |
| **loss on `kill -9`** | **up to 1 s of acknowledged writes** | none locally |
| **loss on failover** | **unbounded by replication lag** | bounded: writes are *rejected* (`NOREPLICAS`) rather than silently at risk |
| write latency | Redis-class | Postgres-with-`synchronous_commit=on`-class |

Tier A is right for development, CI, single-node and edge deployments, and for teams who will not run
Postgres and accept the window in exchange for one less piece of infrastructure. Tier B is the **only**
configuration under which this document calls the provider production-tier — and even there it offers
none of Postgres's query model. `min-replicas-to-write` is preferred over a per-call `WAITAOF` because
it turns the loss window into an up-front, observable rejection instead of a blocking wait on the
multiplexer every saga in the process shares.

One case has **no** mitigation at either tier and is documented rather than handled: a
partitioned-but-unaware primary accepts writes that are discarded when the partition heals —
acknowledged, then erased, invisible at every call site.

**AOF recovery after a hard kill, measured.** A script's effects reach the AOF wrapped in
`MULTI`/`EXEC`, and a kill can leave a partial transaction Redis may refuse to boot on
(`redis-check-aof --fix`). Measured once against the pinned major (Redis 7.4.11, the compose overlay,
the sample submitting orders continuously): `docker kill -s KILL` on the Redis container with 256 sagas
stored, a 94-second outage, then `docker start`. It booted unaided — `DB loaded from append only file:
0.028 seconds` — with no `redis-check-aof` step; the health check returned to Healthy on its next
probe; 323 sagas were listed a few seconds later and the torn-write sentinel was empty. During the
outage the saga host logged the client's connection exceptions and the engine's own
`Infrastructure error processing … (attempt n/5); redelivering` lines; 18 messages exhausted their five
redelivery attempts inside the 94 seconds and were dead-lettered, each with the engine's
`Failed to record delivery-exhausted state … message is still being dead-lettered` warning, because the
store that would record the dead-letter entry was the store that was down. That is the engine's
behaviour under *any* store outage longer than its redelivery budget, not a Redis property — but a
store that goes down for a restart is a store outage, so size the redelivery budget against the restart
time you expect. One run is one data point, not a guarantee: the partial-`MULTI` case is a documented
Redis behaviour, and a deployment that needs the boot to be unattended should keep `redis-check-aof` in
its runbook.

### Configuration is validated, not documented

`RedisPersistenceHealthCheck` re-runs the provider's probe on **every** call — a `CONFIG SET` by a
neighbouring operator is a live change — and reports **Unhealthy**, naming the guarantee, on any of:

- `appendonly` not `yes`, or `maxmemory-policy` not `noeviction`;
- Cluster mode (`cluster_enabled`), or a connection to a replica rather than the primary;
- `used_memory / maxmemory` above `WriteMemoryThreshold` (default 90 %);
- any torn write (below);
- the key space's schema marker (`{vsaga:<Namespace>}:meta` `sv`) missing or of another version;
- **`CONFIG GET` being disabled**, as it is on several managed tiers — reported as *unverifiable*, never
  as healthy;
- server-side Lua unavailable.

The healthy description names the server, its version and the tier it is running at:
`redis 7.4.11, durability tier A (appendfsync everysec, min-replicas-to-write 0, 0 replica(s)), 0.7 %
of maxmemory used`. Bootstrap is a retrying hosted service (`RedisPersistenceBootstrapper`), never
fail-fast: the host starts without Redis, the stores fail until it is reachable, and the health check
stays Unhealthy until a probe passes. Under `docker-compose.redis.yml` that is what gates
`order-processing`: its `depends_on: dashboard-api: service_healthy` (from `docker-compose.yml`) waits
on dashboard-api's `/health`, where this check runs as `persistence`; the overlay's own
`depends_on: redis: service_healthy` waits only for `redis-cli ping`.

### Atomicity and the torn-write sentinel

`ISagaOutboxStore.EnqueueAsync` stages into a Scoped `RedisSagaUnitOfWork`; the persist
(`InsertAsync`/`UpdateAsync`) is the **sole committer**, running one Lua script that writes the
snapshot, its staged outbox rows and every summary index on one shard, atomically. Inside the script the
outbox row *hashes* are written before the snapshot but their *pending-index entries* after it, so a
script that dies early leaves garbage, never a phantom publish. Redis has no rollback, so the two abort
classes are removed by construction — every key is computed in C#, no `cjson`, no unbounded loop — and
a pre-flight memory gate refuses a persist before any write when the last probe put the server above
`WriteMemoryThreshold` (`RedisMemoryPressureException`, an infrastructure failure the engine redelivers).
The residual — an abort at 100 % memory — is made **loud**: the script writes the instance to
`{vsaga:<Namespace>}:torn` first and deletes it last, and the health check goes Unhealthy with the
torn-write count, naming the first ten instances left there. Repair the instance, then `HDEL` its field.

Script-cache discipline: every script runs as `EVALSHA` and, on `NOSCRIPT` (a restart, `SCRIPT FLUSH`,
a replica taking over), retries once as `EVAL` inside the provider — a `NOSCRIPT` that escaped would
redeliver every in-flight message at once, at the moment a failover has already degraded the system.
Tested by running every write path with the cache flushed immediately before it.

### The key space

Every key is `{vsaga:<Namespace>}:…` — the braces are a hash tag, so the whole key space hashes to one
slot. Composite keys are injective: a saga instance is `{correlationId}|{sagaType}` (the GUID is
fixed-width), and any other user-supplied component that is not last in its key is length-prefixed.

| Key | Type | Holds |
| --- | --- | --- |
| `saga:{corr}\|{type}` | HASH | the snapshot: projected fields plus `dataJson` |
| `bk:{len}:{type}\|{businessKey}` | STRING | the business-key reservation (the reserving correlation id) |
| `log:{corr}\|{type}` | LIST | the timeline, one JSON element per `SagaLogEntry`; position = sequence number |
| `dedupe:{corr}\|{type}` | SET | inbound message ids only — the O(1) `IsDuplicateAsync` |
| `to:row:{id}`, `to:due`, `to:for:…` | HASH, ZSET, SET | a timeout row, the due-ordered claim set (Pending rows only), the per-scope cancel lookup |
| `ob:row:{messageId}`, `ob:pending` | HASH, ZSET | an outbox row, the created-ordered claim set |
| `to:seq`, `ob:seq` | STRING | the `INCR` counters behind `SagaTimeout.Id` and `SagaOutboxMessage.Id` |
| `ix:updated`, `ix:status:{s}`, `ix:type:{type}`, `ix:kind:{k}`, `ix:parent:{corr}\|{type}` | ZSET | the summary indexes: score = updated (or created) microseconds, member = `{corr}\|{type}` |
| `ix:corr:{corr}`, `ix:types`, `ix:typecount` | SET, HASH, HASH | `FindByCorrelationIdAsync`, `GetSagaTypesAsync` |
| `topo` | HASH | the service topology |
| `meta`, `torn` | HASH | the schema marker; the torn-write sentinel |

**Every projected timestamp is stored as integer Unix microseconds** — as the sorted-set score that
orders it and as the hash field that reads it back, one representation so the order an index yields and
the value a predicate compares can never disagree. A double score cannot hold `UtcTicks` exactly
(≈ 6.4 × 10¹⁷, far above 2⁵³), and microseconds are exact until the year 2255. The truncation is the
same one Postgres's `timestamp` columns apply, so the provider declares the same one-microsecond
`TimestampResolution`; the state blob keeps full ticks on both. Enums are stored numerically, because
the dashboard's Status sort is over `SagaStatus`'s declared order.

The member string is the design's quiet win: it carries both fields `Search` matches, so a search never
reads a snapshot, and the tie order within one microsecond is the members' own lexicographic order —
total and deterministic, the stable order the read contract requires (ties break by member, in the
direction of the walk; this is per-provider, as the contract allows).

### Search

Core Redis has no substring index. `Search` is a case-folded scan of the candidate members — contract-
complete (case-insensitive, both fields, independently, no minimum term length) — bounded by
`MaxSearchScanMembers` (default 100 000) per candidate set. Under the Status sort each of the seven
status buckets is a candidate set of its own, so a Status-sorted search can scan up to seven times the
bound. Above the bound `ListAsync` **throws** `RedisSearchScanLimitExceededException` rather than
silently truncating a page, because the dashboard's change poller reads a short page as "drained" and a
false positive there is a permanently dropped update. No other provider throws here; narrow the search
with a type, status or kind filter, or raise the bound. Without a search, a list sorted by `UpdatedAt`
over at most one filter — the change poller's shape — is answered by rank from one sorted set,
O(log n + offset + page); the Status sort walks the seven status buckets in enum order; anything else
materialises the candidates server-side (`ZINTER`, read-only) and pages client-side.

Head-of-line blocking is the cost: Redis is single-threaded, so a deep page, a broad search or a
`LRANGE` over a long timeline stalls every other command, including live `UpdateAsync`s — where Postgres
runs the dashboard's query on a separate backend. The endpoint's 500-row `pageSize` clamp is a hard
prerequisite of this provider, not a nicety. The engine's own reads are timelines too:
`GetVisitedStatesAsync` runs an `LRANGE` over the saga's whole list before every message and timeout,
and the dashboard reads it again for the timeline and the map on every live update. Since every
committed step appends a [state snapshot](observability.md#state-snapshots), each of those reads also
returns every state the saga recorded, so its size grows with steps × state size; the per-saga snapshot
budget ([`MaxStateSnapshotBytesPerSaga`](configuration.md#sagaorchestratoroptions), 1 MiB by default)
is what bounds it for a saga with many steps or a large state.

### Capacity model

Measured with `MEMORY USAGE` against the OrderProcessing sample under `docker-compose.redis.yml`
(Redis 7.4.11), 206 sagas across the sample's seven types:

| | bytes |
| --- | --- |
| a completed `OrderSaga` snapshot hash (`dataJson` ≈ 400 B) | 1 556 |
| its 15-entry timeline list | 7 288 |
| its dedupe set | 232 |
| **every provider key, averaged over all 206 sagas** (indexes, timeouts and outbox rows included) | **≈ 5 500 per saga** |

[2026-10-02: the timeline figures above predate [state snapshots](observability.md#state-snapshots) and
the message body on every `MessageReceived`, both on by default since; they are superseded by the
re-measurement below. The snapshot hash, dedupe set and 206-saga average were not re-measured.]

Re-measured on 2026-10-02 with `MEMORY USAGE <key> SAMPLES 0` against the same overlay (completed
`OrderSaga`s, four handled messages each; the timeline list of every saga measured came out at the same
size):

| A completed `OrderSaga`'s timeline list | entries | bytes |
| --- | --- | --- |
| before snapshots and `MessageReceived` bodies (the figure above, re-measured) | 15 | 7 288 |
| with `MessageReceived` bodies, snapshots off (`RecordStateSnapshots = false`) | 15 | 8 312 |
| **with bodies and snapshots, the default** | **19** | **12 528** |

The four snapshots add 4 216 bytes (+51 %) and the bodies another 1 024 (`MEMORY USAGE` moves in
allocator-sized steps; the list elements themselves grew by about 540 bytes for 360 characters of
bodies). Each snapshot element is about 1 075 bytes for a 426–433-byte state: the entry's other fields,
plus the state stored JSON-escaped inside the entry. Added to the hash and dedupe set above, a completed
sample `OrderSaga` now costs **≈ 14 KB**, so one gigabyte holds on the order of **70 000** of them. A saga
type with more steps or a larger state pays roughly steps × escaped state size on top, up to the per-saga
snapshot budget; `RecordStateSnapshots = false` or a small `MaxStateSnapshotBytes` takes most of it back.

Whatever the per-saga cost, retained sagas grow monotonically: nothing the engine does deletes a saga,
and no retention shape that keeps compensation and redelivery dedupe correct exists (a TTL would also
make keys eviction candidates under the `volatile-*` policies managed platforms default to). Set
`maxmemory`, budget 20 % headroom above the projected dataset, and alert on the health check's
`memoryRatio` before it reaches `WriteMemoryThreshold` — past it, every persist is refused and the
engine dead-letters after its redelivery budget. Terminal-saga archival is the only retention shape
considered and is deferred with its own entry gate (`design/redis-persistence.md` §6.5).

### Supported servers

Capability is verified at bootstrap by running a script, not by parsing a version string. A dedicated
instance is a hard prerequisite, not a recommendation: a key prefix is weaker than a Postgres schema or a
Mongo database, `SELECT n` is a convention, and a neighbouring tenant's `FLUSHALL` or `CONFIG SET` is a
total-loss event with no permission boundary. Pair the dedicated instance with an ACL that limits the
provider's user to its own commands.

| Server | Status | Reason |
| --- | --- | --- |
| Self-hosted Redis ≥ 7.0, single primary (± replicas) | **Supported** | the live-verified configuration (7.4) |
| Self-hosted Valkey ≥ 7.2 | **Supported** | core commands and Lua only; no module dependency |
| Redis Cluster (any vendor) | **Unsupported**, reported Unhealthy by the probe | the persist script's atomic unit needs one shard |
| Redis Cloud / Azure Managed Redis / ElastiCache / MemoryDB / Memorystore, non-cluster | Untested | should work where `INFO`, `CONFIG GET`, `EVAL` are allowed; where `CONFIG GET` is disabled the health check reports the guarantees as *unverifiable* and stays Unhealthy |
| Any shared or evicting instance | **Unsupported** | `maxmemory-policy` other than `noeviction` deletes snapshots, timeouts and outbox rows silently |

### The compose overlay

```bash
docker compose -p vsaga-redis -f docker-compose.yml -f docker-compose.redis.yml up -d --build
```

Runs the sample and the dashboard against a Tier A Redis (Redis on port 6479, the dashboard UI on
http://localhost:4800 and its API on 5680; see
[`transports/index.md`](transports/index.md#running-an-adapters-own-overlay) for the overlay
conventions). Its `vsaga-redis-data` volume holds the AOF and, like the Postgres volume, is not reset by
`up`; use `down -v` for a clean run.

## MongoDB

`VSaga.Persistence.MongoDB` (`AddVSagaMongoDb(Action<VSagaMongoOptions>, Action<MongoClientSettings>? = null)`)
implements every store on the **native `MongoDB.Driver` 3.x** (Apache-2.0) — not the MongoDB EF Core
provider, which `VSaga.Persistence.EFCore`'s relational-only model (`FromSqlInterpolated`, `HasFilter`)
cannot host. The decision is recorded in
[`adr/0001-mongodb-persistence-provider.md`](adr/0001-mongodb-persistence-provider.md) and the plan it
was built from in [`design/mongodb-persistence.md`](design/mongodb-persistence.md).

```csharp
services.AddVSagaMongoDb(o =>
{
    o.ConnectionString = "mongodb://mongo:27017/?replicaSet=rs0";
    o.DatabaseName = "orders";           // one database per service
});
services.AddHealthChecks().AddCheck<MongoPersistenceHealthCheck>("persistence");
```

Options are in [`configuration.md`](configuration.md#vsagamongooptions-vsagapersistencemongodb). The
second parameter hands you the driver's `MongoClientSettings` for TLS, pool sizing, timeouts and the
`ClusterConfigurator` hook (driver 3.6, which this repo resolves, exposes no `ActivitySource`, so
provider-level tracing goes through that hook; 3.7.0 and later, inside the package's `[3.6.0,4.0.0)`
range, add built-in OpenTelemetry tracing of their own), on the same reasoning EF Core's
`DbContextOptionsBuilder` is exposed rather than wrapped.

**What it is not.** Not a standalone-`mongod` provider, and not a sharded-cluster one in this version:
the health check reports both Unhealthy (below). Not a migration path: `Persistence:Provider` is a
greenfield choice. Not a document-modelling exercise: `TState` stays a `System.Text.Json` string in
`dataJson`, byte-identical to EF Core's `DataJson`
([`adr/0005-saga-state-storage-model.md`](adr/0005-saga-state-storage-model.md)); native BSON storage
is a named follow-up with a fidelity test suite as its entry gate.

### The replica set is a hard prerequisite

`ISagaOutboxStore.EnqueueAsync` must not commit on its own; the persist that follows it commits the
outbox rows and the snapshot together. EF Core gets that from one `DbContext` per message — a change
tracker, not a transaction, with a transaction only for the duration of each `SaveChangesAsync`. The
MongoDB provider mirrors exactly that: `EnqueueAsync` stages into a Scoped `MongoSagaUnitOfWork` with
zero MongoDB operations, and the snapshot store's `InsertAsync`/`UpdateAsync` is the **sole
committer** — a bare `insertOne`/`replaceOne` when nothing is staged (the common case), or one short
multi-document transaction inserting the staged rows and writing the snapshot when something is. The
flush is peek-commit-clear: a persist that throws or loses its version guard aborts the transaction and
leaves the rows staged, so a later `DiscardPendingAsync` finds them and a later persist in the same
scope (the dead-letter path's `Failed` snapshot) commits them atomically with it — where EF Core commits
that row through an incidental `AppendAsync` flush.

Multi-document transactions need a replica set; a **single-member set is enough**, and
`docker-compose.mongo.yml` runs one. There is **no non-transactional escape hatch**, deliberately: the
in-memory provider's documented gap is survivable because its phantom rows die with the process, but a
MongoDB row committed without its snapshot would be durable, and the recovery poller would faithfully
republish it after a restart for a transition that never happened. On a standalone `mongod` a persist
with staged rows fails loudly (`Standalone servers do not support transactions`), and the health check
says why before the first message arrives.

The session is opened at persist time and never held across a message: no user step I/O (`.CallHttp`
is a shipped DSL feature) runs inside the 60 s `transactionLifetimeLimitSeconds`, and the 5 ms
`maxTransactionLockRequestTimeoutMillis` cannot turn same-instance contention into an abort of the whole
message. Everything else commits on its own, outside any transaction, exactly as EF Core's stores do:
event-log appends, sequence allocation, `ScheduleAsync`, `CancelAsync`, `MarkDispatchedAsync` and every
read. The event log in particular *must* — the engine's redelivery path relies on the `MessageReceived`
entry surviving an aborted persist — and that is enforced structurally: `MongoSagaEventLogStore` takes no
dependency on the unit of work and has no code path that accepts a session.

### Write concern, read preference and read concern are pinned, and contradictions are reported

| Setting | Pinned to | What it protects |
| --- | --- | --- |
| `writeConcern` | `majority`, on **every** collection handle, not only inside the transaction | a `w:1` event-log append that has not replicated is rolled back when its primary loses an election; `IsDuplicateAsync` then says "not seen" and the redelivered message is reprocessed as new |
| `readPreference` | `primary` | a read from a secondary can miss a committed log entry, business-key reservation or snapshot — a silent reprocess, double-start or **short compensation set**, undetectable by any correctness test |
| `readConcern` | `local` | read-your-own-writes on the primary; `majority` or `snapshot` would lag them |

A connection string that *explicitly* says otherwise (`readPreference=secondaryPreferred`, `w=1`,
`readConcernLevel=majority`) is overridden **and** reported by the health check as Unhealthy, naming the
guarantee it would have broken: an operator who wrote it meant it, and should be told it does not apply.
Primary-secondary-arbiter (PSA) topologies are **unsupported**: `w:majority` hangs when the single
data-bearing secondary is down.

### Configuration is verified, not documented

`MongoPersistenceHealthCheck` re-runs the provider's probe on **every** call and reports **Unhealthy**,
naming the prerequisite, on any of:

- a standalone `mongod` (`hello` reports no `setName`), a `mongos` (`msg: isdbgrid`), or a member that
  is not the writable primary;
- a server below 6.0;
- any of the provider's named indexes missing on any of its collections — in particular
  `ux_sagaType_businessKey`, the adjudicator of the concurrent-double-initiate race, without which sagas
  must not run;
- the schema marker (`vsagaMeta/schema`, `sv: 1`) missing or of another version;
- a connection-string contradiction (above);
- the probe itself failing to reach the server.

The healthy description names the server, topology and state: `MongoDB 8.0.32, replica set rs0
(primary), indexes in place, 0 stranded outbox row(s)`. The stranded count — Pending outbox rows older
than `StrandedOutboxThreshold` — is reported in the check's data, not failed on: it means the outbox
dispatcher is not draining, not that the store is broken. Bootstrap is a retrying hosted service
(`MongoPersistenceBootstrapper`), never fail-fast: the host starts without MongoDB, the stores fail
until it is reachable, index creation runs on every tick (idempotent when the definition matches, an
error when it does not — the marker that a layout change is due), and the schema marker is written only
once every index exists. Creating the partial indexes is also the capability check: a server without
them fails at bootstrap rather than silently later. The health check stays Unhealthy until a probe
passes. Under `docker-compose.mongo.yml` that is what gates `order-processing`: its
`depends_on: dashboard-api: service_healthy` (from `docker-compose.yml`) waits on dashboard-api's
`/health`, where this check runs as `persistence`; the overlay's own `depends_on: mongo: service_healthy`
waits only for the member to report itself PRIMARY.

### The collections and indexes

Eight collections in one database (`DatabaseName`; when unset, the connection string's own database
path, else `vsaga`; **one database per service** — two services sharing one would share an outbox and a
timeout schedule). Every `_id` is a scalar (a string, a number or, on `sagaEventLog`, an `ObjectId`),
never a subdocument: a composite subdocument `_id` compares by field order and byte equality, so
a reordered class map would make every lookup miss and every insert create a second document instead of
colliding — the one schema change that could never be migrated in place.

| Collection | Mirrors | `_id` | Note |
| --- | --- | --- | --- |
| `sagaInstances` | `SagaInstances` | `{correlationId}\|{sagaType}` | the snapshot: the projected fields plus `dataJson`, `sagaTypeLower` (for search), and `businessKey` **absent** rather than null when the saga has none |
| `sagaEventLog` | `SagaEventLog` | ObjectId | one entry per document; `seq` (per instance) replaces the identity column |
| `sagaSequences` | *(none)* | `{correlationId}\|{sagaType}` | the per-instance `seq` counter — **correctness-bearing, never prune it independently of its timeline**: deleting a counter restarts `seq` at 1 mid-timeline and silently corrupts the order compensation is derived from. It is not a cache. |
| `sagaCounters` | *(none)* | collection name | the numeric ids of timeouts and outbox rows (`SagaTimeout.Id`/`SagaOutboxMessage.Id` are `long`) |
| `sagaTimeouts` | `SagaTimeouts` | `long` | |
| `sagaOutboxMessages` | `SagaOutboxMessages` | the message id | `destination` is an **explicit null**, never omitted: the dispatcher branches on it to choose a broadcast over an addressed send |
| `sagaConsumerRegistrations` | `SagaConsumerRegistrations` | `{len}:{serviceName}\|{messageType}` | the composite id gives the upsert its idempotency |
| `vsagaMeta` | *(none)* | `schema` | the schema marker |

**Every projected timestamp is a pair:** a BSON `Date` (`updatedAt`) for a human in `mongosh`, and the
instant's exact UTC ticks as an `Int64` (`updatedAtTicks`), which every filter, sort and index uses.
BSON `Date` is millisecond-precision where the engine stamps at 100 ns and `UpdatedSince` is strictly
greater: a millisecond field would collapse a thousand instants onto one value, and the dashboard's
change poller — whose watermark is the last timestamp it pushed — would silently skip every row sharing
it. Both halves are written from one mapping and only the ticks are read back, so the provider declares a
one-tick `TimestampResolution`. Enums are stored numerically, because the dashboard's Status sort is over
`SagaStatus`'s declared order. Correlation ids are stored as their lower-case text (no `Guid` is ever
handed to the driver, and no serializer is registered process-wide). Every document except the
`sagaSequences` and `sagaCounters` counters carries `sv: 1`.

The indexes, by name — the health check lists these and reports Unhealthy until every one exists:

| Collection | Index | Keys |
| --- | --- | --- |
| `sagaInstances` | `ux_sagaType_businessKey` | `{sagaType, businessKey}`, **unique**, partial on `businessKey: {$type: "string"}` — `$type`, not `$exists`, because `$exists: true` matches an explicit null and nearly every saga declares no key. `FindByBusinessKeyAsync` carries the same `$type` predicate: the planner does not infer a string from an equality, and without it the lookup scans every instance of the saga type (the provider's explain test caught exactly that). |
| | `ix_updatedAtTicks_total` | `{updatedAtTicks, sagaType, correlationId}` — the default listing's total order, tiebreakers *in* the index |
| | `ix_status_updatedDesc`, `ix_statusDesc_updatedDesc` | `{status: ±1, updatedAtTicks: -1, sagaType: -1, correlationId: -1}` — two, because both Status directions keep `UpdatedAt` descending inside a bucket (as EF Core does), and that is not the exact inverse of either |
| | `ix_sagaType_updated`, `ix_kind_updated` | the two equality filters, each followed by the total order |
| | `ix_parent` | `{parentSagaType, parentCorrelationId, createdAtTicks}`, partial on `parentSagaType: {$type: "string"}` |
| | `ix_correlationId` | `{correlationId, sagaType}` |
| `sagaEventLog` | `ux_instance_seq` | `{sagaType, correlationId, seq}`, **unique** — the timeline's order |
| | `ix_instance_messageId_entryType` | the dedupe lookup, partial on `messageId: {$type: "string"}` |
| `sagaTimeouts` | `ix_timeout_claim` | `{status, dueAtTicks, _id}`, partial on `status: Pending` |
| | `ix_timeout_cancel` | `{sagaType, correlationId, forState, status}` |
| `sagaOutboxMessages` | `ix_outbox_claim` | `{status, createdAtTicks, id}`, partial on `status: Pending` |

No search index, by design (below). Eight secondary indexes on `sagaInstances` against EF Core's six,
five of them containing `updatedAtTicks`, which changes on every persist — extra index maintenance
inside the transaction window is the accepted cost.

**Schema evolution.** There are no migrations; there is the `sv` marker, explicit index names, and this
procedure: stop every host, change the documents (a copy-collection aggregation, or an `updateMany`),
drop and recreate the affected indexes by name, bump the marker. **Dropping `ux_sagaType_businessKey`
disables the business-key race adjudicator for the duration of the rebuild** — that is a correctness
window, not a footnote, which is why the procedure starts with stopping the hosts. `HasMaxLength`
constraints also disappear here: a value Postgres rejects succeeds silently, and MongoDB has had no
index-key size limit since 4.2 (the old 1024-byte cap), so an oversized `_id`, business key or parent
pointer is indexed rather than rejected. Only the 16 MB document cap bounds it.

### Claims and appends: the round-trip costs

`ClaimDueAsync` and `ClaimPendingAsync` are a **`findOneAndUpdate` loop**: each iteration flips one
row's status and returns it in one atomic, retryable operation, sorted by the claim index, so the MongoDB
provider declares `SupportsConcurrentClaim` and passes the same racing-dispatcher conformance cases
Postgres does (ADR 0004's Postgres-only caveat does not apply here). A batch costs up to `batchSize`
round trips where Postgres costs one statement; an idle poll costs exactly one. The alternative — a
claim-token `find`/`updateMany`/`find` — was rejected because `updateMany` is not a retryable write, and a
crash between it and the follow-up read strands the whole batch marked terminal with no way to identify
it. This loop's own failure mode — a crash at iteration *k* leaves *k* rows claimed in a list that dies
with the process — is exactly Postgres's, not tighter.

`AppendAsync` is **two round trips**: a `findOneAndUpdate` `$inc` upsert on the instance's
`sagaSequences` document (retried on the duplicate-key error two concurrent first appends can race
into), then one `insertOne`. The engine appends 6–8 entries per message, so per-message oplog volume is
roughly double Postgres's. `SequenceNumber` is therefore **per instance**, which is what the contract
already specified; nothing in the engine compares it across instances. A per-scope block reservation
was rejected on correctness grounds: two processes handling concurrent messages for one instance would
reserve disjoint blocks and appends could sort in the wrong order — the order compensation is derived
from.

### Search and listing

`ListAsync` is one `countDocuments` and one `find` with skip/limit, so `TotalCount` is exact over the
same filtered set the page comes from. Every sort arm ends in a total order — the requested column, then
(for a Status sort) `UpdatedAt` descending as EF Core does, then the row's own `(sagaType, correlationId)`
— with the identity tiebreak following the direction of the key before it (the sort column's, or
`UpdatedAt`'s descending on a Status sort) rather than staying ascending, because MongoDB serves a sort
from an index only when the pattern equals the index or its exact inverse; the contract asks for a
per-provider deterministic order, not EF Core's. `Search` is an `$or` of two
non-anchored `$regex` predicates over `sagaTypeLower` and the lower-cased correlation-id text — two
**independent** matches, as the contract states, never one concatenated field that would match a term
straddling the two — with the term regex-escaped and lower-cased. It is unindexed on purpose: a
non-anchored regex cannot seek, and the planner can serve the search or the sort from an index but not
both, so the sort index streams and the search is a residual filter, exactly as Postgres's `LIKE '%x%'`.
A search over a large collection is therefore a scan, on a separate server thread — unlike Redis, it
never blocks the saga hot path, and there is no scan bound and no HTTP 400.

### Supported servers

Capability is verified at bootstrap by creating the indexes and probing the topology, not inferred from
a version string; the version is parsed only to enforce the 6.0 floor.

| Server | Status | Reason |
| --- | --- | --- |
| Self-hosted MongoDB ≥ 6.0, replica set (single-member is enough) | **Supported** | 8.0 is the live-verified configuration; 6.0 is the oldest the probe accepts |
| MongoDB Atlas, replica-set (dedicated or shared) tier | Untested | should work. The driver's default pool is 100 connections per client per host and Atlas caps connections per tier (500 on a Free/M0 cluster), so size the pool through `configureClient` when several vSaga services share a small tier |
| Standalone `mongod` | **Unsupported**, reported Unhealthy by the probe | no multi-document transactions |
| Sharded cluster (`mongos`) | **Unsupported**, reported Unhealthy by the probe | untested in this version: no shard key designed, cross-shard transaction cost unmeasured |
| Primary-secondary-arbiter (PSA) | **Unsupported** | `w:majority` hangs when the single data-bearing secondary is down |
| Amazon DocumentDB | **Unsupported** | its partial indexes (5.0 instance-based clusters only) accept no `$type` filter, so `ux_sagaType_businessKey`, `ix_parent` and `ix_instance_messageId_entryType` cannot be created — bootstrap fails and the health check stays Unhealthy |
| Azure Cosmos DB for MongoDB (RU) | **Unsupported** | lacks the multi-document transaction scope the persist relies on |

### The compose overlay

```bash
docker compose -p vsaga-mongo -f docker-compose.yml -f docker-compose.mongo.yml up -d --build
```

Runs the sample and the dashboard against a single-member MongoDB 8 replica set (MongoDB on port 27018,
the dashboard UI on http://localhost:4700 and its API on 5580;
see [`transports/index.md`](transports/index.md#running-an-adapters-own-overlay) for the overlay
conventions). The set is initiated idempotently by the container's own healthcheck, which passes only
once the member reports itself PRIMARY, so a second `up` on the persisted `vsaga-mongo-data` volume
starts cleanly; the volume, like the Postgres one, is not reset by `up` — use `down -v` for a clean run.
From the host, connect with `mongodb://localhost:27018/?directConnection=true`: the member advertises
itself as `mongo:27017`, which only the compose network resolves.

**Live-verified, 2026-09-26** (MongoDB 8.0.32, the overlay, the sample submitting orders
continuously): 426 sagas across the sample's seven types, 95 timeouts fired, sub-sagas completing and
their parents finishing, zero infrastructure errors, zero stranded outbox rows, the health check
Healthy throughout. Then the gate the plan would not let anyone assert: with the broker paused so an
inline drain could not complete, `docker kill -s KILL` on the saga host left one committed outbox row
Pending for a `MixedFulfilmentSaga` mid-step; after the restart the recovery poller claimed and
republished it, and the saga's timeline shows the received copy carrying the outbox row's own message
id, the transition it drove, the reply timeout that then fired, and the compensation that ran — the
entire downstream story on the recovered message. A `docker compose down` (volume kept) and `up`
started the set again through the idempotent healthcheck, with every saga still listed. The full record
is in [`history/mongodb-persistence-provider.md`](history/mongodb-persistence-provider.md).

**Measured storage**, from `collStats` after 153 sagas: a snapshot document averages 821 bytes, a
timeline entry 436 bytes, and the sample's completed sagas write 10–15 entries each, so a completed
saga costs **≈ 6–8 KB of data plus roughly the same again in indexes** (nine on `sagaInstances`
and three on `sagaEventLog`, counting `_id` on both) before WiredTiger's compression, which took the
900 KB of data to 580 KB on disk. [2026-10-02: those entry counts predate
[state snapshots](observability.md#state-snapshots) and the message body on every `MessageReceived`, both
on by default since; superseded for the event log by the re-measurement below.]

Re-measured on 2026-10-02 against the same overlay, as the sum of `$bsonSize` over each completed
`OrderSaga`'s `sagaEventLog` documents (four handled messages each; every saga measured came within a few
bytes of the average):

| A completed `OrderSaga`'s event log | documents | BSON bytes |
| --- | --- | --- |
| before snapshots and `MessageReceived` bodies | 15 | 6 426 |
| with `MessageReceived` bodies, snapshots off (`RecordStateSnapshots = false`) | 15 | 6 806 |
| **with bodies and snapshots, the default** | **19** | **10 040** |

The four `StatePersisted` documents add 3 234 bytes (+48 %), about 808 bytes each for a 426–433-byte
state, and the four bodies add about 380. A sample `OrderSaga`'s event log is therefore about 10 KB of
data, before indexes and compression; a saga type with more steps or a larger state pays roughly steps ×
state size on top, up to the per-saga snapshot budget. A snapshot is never larger than
`MaxPayloadJsonBytes` either: above it the payload guard stores its own marker.

Nothing the engine does deletes a saga, and no retention shape that
keeps compensation and redelivery dedupe correct exists; a TTL index on the event log or a naive one on
the outbox (which would delete Pending rows) is out of scope by design.

## In-memory

`VSaga.Persistence.InMemory` (`AddVSagaInMemoryPersistence()`) backs all seven store contracts with a
single shared `InMemorySagaStore` singleton (six resolve to it directly; `ISagaSnapshotStore<>` is an
open-generic `InMemorySagaSnapshotStore<>` that delegates to it, `ServiceCollectionExtensions.cs:25`)
— intended for local development and as the foundation of
`VSaga.Testing`'s `SagaTestHarness` (see [`testing.md`](testing.md)), **not for production use**: state
does not survive a process restart, and there is no concurrency-safe claim semantics beyond a single
process's own in-memory locking. Its `EnqueueAsync` also commits immediately rather than staging, so the
outbox's crash-atomicity guarantee does not hold — the provider documents this itself at
`InMemorySagaStore.cs:380-385`.

```csharp
services.AddVSagaInMemoryPersistence();
```

## Choosing a provider

Use EF Core/Postgres for anything that needs to survive a restart, run more than one replica, or be
queried by the dashboard against real historical data. Use MongoDB for the same, when a team is already
standardised on it and runs a [replica set](#the-replica-set-is-a-hard-prerequisite) — it is the one
other provider with majority-acknowledged, transactional persists. Use Redis when a team will not run Postgres and
accepts [Tier A's loss window](#durability-policy) — development, CI, single-node and edge deployments
— or, at Tier B, as a production store whose dataset fits in RAM and whose dashboard queries stay narrow.
Use in-memory for local development without a database, or (via `SagaTestHarness`) for unit tests that
exercise the real engine without any broker/database dependency at all.
