# Persistence

vSaga ships three persistence providers. EF Core/Postgres is the reference and the only one documented
at full production tier by default. Redis is durable but RAM-bound and single-node: at its default
configuration a hard kill loses up to one second of acknowledged writes, which for vSaga means a
redelivered message can be reprocessed rather than deduped. Redis reaches production tier only under
the Tier B configuration in [Redis → Durability policy](#durability-policy). In-memory remains dev/test
only.

All three implement the same set of store contracts (`VSaga.Abstractions.Persistence`):
`ISagaSnapshotStore<TState>`, `ISagaSummaryReader`, `ISagaEventLogStore`, `ISagaTimeoutStore`,
`ISagaOutboxStore`, `ISagaAdminStore`, and `IServiceTopologyStore`. The hosts pick one with the
[`Persistence:Provider`](configuration.md#persistenceprovider--picking-the-store) switch.

All three are held to the same written contracts by the cross-provider `VSaga.Persistence.Conformance`
suite, which a third-party provider can run against itself. The eight divergences and three shared
defects the first two once had are catalogued, with their fixes, in
[`design/persistence-contracts.md`](design/persistence-contracts.md) §1 and §3; all are fixed.

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

(This is exactly what `VSaga.Dashboard.Api`'s own `Program.cs` does — see there for the non-fatal
try/catch around it, useful if the app might start before Postgres is reachable.) See
`dotnet/src/VSaga.Persistence.EFCore.Postgres/Migrations/` for the migration history: identity scoping
to `(SagaType, CorrelationId)`, the Saga Map's service-map fields, sub-saga parent-linkage columns, the
outbox table (plus its own follow-up index migration), the business-key column with its partial unique
index, and the `SagaInstances.UpdatedAtUtc` index the dashboard's change poller needs (eight migrations
in total).

**The five tables** `VSagaDbContext` maps, for anyone querying the database directly:

| Table | Holds |
| --- | --- |
| `SagaInstances` | One row per saga instance (the snapshot), keyed by `(SagaType, CorrelationId)`. `DataJson` holds the whole serialized `TState`; the other columns are a queryable projection of it — see [`adr/0005-saga-state-storage-model.md`](adr/0005-saga-state-storage-model.md). |
| `SagaEventLog` | The append-only `SagaLogEntry` timeline behind the dashboard (see [`observability.md`](observability.md)). |
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
> `EfCoreSagaTimeoutStore.ClaimDueAsync` and `EfCoreSagaOutboxStore.ClaimPendingAsync`). **Every** other provider — including `UseSqlServer`,
> suggested above — silently takes a plain load-then-update fallback that is correct for exactly one
> dispatcher instance. Two replicas on a non-Postgres provider will fire the same timeout twice and
> publish the same outbox row twice, with no error anywhere. See
> [`adr/0004-postgres-only-atomic-claim.md`](adr/0004-postgres-only-atomic-claim.md).

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
violated. Not a Cluster provider: Cluster mode is refused (see the health check below). Not a migration
path: `Persistence:Provider` is a greenfield choice.

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
| Cluster | refused | refused |
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
- the key space's schema marker (`{ns}:meta` `sv`) missing or of another version;
- **`CONFIG GET` being disabled**, as it is on several managed tiers — reported as *unverifiable*, never
  as healthy;
- server-side Lua unavailable.

The healthy description names the server, its version and the tier it is running at:
`redis 7.4.11, durability tier A (appendfsync everysec, min-replicas-to-write 0, 0 replica(s)), 0.7 %
of maxmemory used`. Bootstrap is a retrying hosted service (`RedisPersistenceBootstrapper`), never
fail-fast: the host starts without Redis, the stores fail until it is reachable, and the health check
stays Unhealthy until a probe passes — which is what `docker-compose.redis.yml`'s
`depends_on: service_healthy` gates on.

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
`{ns}:torn` first and deletes it last, and the health check goes Unhealthy naming every instance left
there. Repair the instance, then `HDEL` its field.

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
`MaxSearchScanMembers` (default 100 000). Above the bound `ListAsync` **throws**
`RedisSearchScanLimitExceededException` rather than silently truncating a page, because the dashboard's
change poller reads a short page as "drained" and a false positive there is a permanently dropped
update. No other provider throws here; narrow the search with a type, status or kind filter, or raise
the bound. Without a search, a list sorted by `UpdatedAt` over at most one filter — the change poller's
shape — is answered by rank from one sorted set, O(log n + page); the Status sort walks the seven status
buckets in enum order; anything else materialises the candidates server-side (`ZINTER`, read-only) and
pages client-side.

Head-of-line blocking is the cost: Redis is single-threaded, so a deep page, a broad search or a
`LRANGE` over a long timeline stalls every other command, including live `UpdateAsync`s — where Postgres
runs the dashboard's query on a separate backend. The endpoint's 500-row `pageSize` clamp is a hard
prerequisite of this provider, not a nicety.

### Capacity model

Measured with `MEMORY USAGE` against the OrderProcessing sample under `docker-compose.redis.yml`
(Redis 7.4.11), 206 sagas across the sample's seven types:

| | bytes |
| --- | --- |
| a completed `OrderSaga` snapshot hash (`dataJson` ≈ 400 B) | 1 556 |
| its 15-entry timeline list | 7 288 |
| its dedupe set | 232 |
| **every provider key, averaged over all 206 sagas** (indexes, timeouts and outbox rows included) | **≈ 5 500 per saga** |

So a completed saga costs **≈ 5–10 KB** and one gigabyte holds on the order of **100 000–180 000
retained sagas**, growing monotonically: nothing the engine does deletes a saga, and no retention shape
that keeps compensation and redelivery dedupe correct exists (a TTL would also make keys eviction
candidates under the `volatile-*` policies managed platforms default to). Set `maxmemory`, budget 20 %
headroom above the projected dataset, and alert on the health check's `memoryRatio` before it reaches
`WriteMemoryThreshold` — past it, every persist is refused and the engine dead-letters after its
redelivery budget. Terminal-saga archival is the only retention shape considered and is deferred with its
own entry gate (`design/redis-persistence.md` §6.5).

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
| Redis Cluster (any vendor) | **Unsupported, refused at bootstrap** | the persist script's atomic unit needs one shard |
| Redis Cloud / Azure Managed Redis / ElastiCache / MemoryDB / Memorystore, non-cluster | Untested | should work where `INFO`, `CONFIG GET`, `EVAL` are allowed; where `CONFIG GET` is disabled the health check reports the guarantees as *unverifiable* and stays Unhealthy |
| Any shared or evicting instance | **Unsupported** | `maxmemory-policy` other than `noeviction` deletes snapshots, timeouts and outbox rows silently |

### The compose overlay

```bash
docker compose -p vsaga-redis -f docker-compose.yml -f docker-compose.redis.yml up -d --build
```

Runs the sample and the dashboard against a Tier A Redis (ports 6479 / 5680; see
[`transports/index.md`](transports/index.md#running-an-adapters-own-overlay) for the overlay
conventions). Its `vsaga-redis-data` volume holds the AOF and, like the Postgres volume, is not reset by
`up`; use `down -v` for a clean run.

## In-memory

`VSaga.Persistence.InMemory` (`AddVSagaInMemoryPersistence()`) backs six store contracts with a single
shared `InMemorySagaStore` singleton (`ISagaSnapshotStore<>` is separate — an open-generic
`InMemorySagaSnapshotStore<>`, `ServiceCollectionExtensions.cs:25`) — intended for local development and as the foundation of
`VSaga.Testing`'s `SagaTestHarness` (see [`testing.md`](testing.md)), **not for production use**: state
does not survive a process restart, and there is no concurrency-safe claim semantics beyond a single
process's own in-memory locking. Its `EnqueueAsync` also commits immediately rather than staging, so the
outbox's crash-atomicity guarantee does not hold — the provider documents this itself at
`InMemorySagaStore.cs:332-337`.

```csharp
services.AddVSagaInMemoryPersistence();
```

## Choosing a provider

Use EF Core/Postgres for anything that needs to survive a restart, run more than one replica, or be
queried by the dashboard against real historical data. Use Redis when a team will not run Postgres and
accepts [Tier A's loss window](#durability-policy) — development, CI, single-node and edge deployments
— or, at Tier B, as a production store whose dataset fits in RAM and whose dashboard queries stay narrow.
Use in-memory for local development without a database, or (via `SagaTestHarness`) for unit tests that
exercise the real engine without any broker/database dependency at all.
