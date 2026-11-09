# ADR 0013: A `vsaga.saga.running` gauge counts `Running` instances in the store, through a new count method on the summary reader

**Status:** **Accepted** — 2026-11-09, by the maintainer. **Implemented** — 2026-11-09, `a6cf4e9`, `d1b5f45`, `e659f3f`.
**Date:** 2026-11-09
**Relates to:** [`0003-persistence-contract-clauses.md`](0003-persistence-contract-clauses.md) (the store contracts
every provider must satisfy, and the conformance suite that enforces them);
[`0002-redis-persistence-provider.md`](0002-redis-persistence-provider.md) (the hand-maintained indexes that make
a count on Redis the expensive one);
[`0009-sql-server-persistence-provider.md`](0009-sql-server-persistence-provider.md) (the fifth provider the
method must cover).
**Design:** [`../design/production-readiness.md`](../design/production-readiness.md), section 6 ("Wire
`SagaDuration`; delete `RunningSagas` instead of wiring it"), which names this gauge as the follow-up;
[`../design/stuck-saga-detection.md`](../design/stuck-saga-detection.md), whose cached-gauge shape this reuses.

---

## Context

`VSagaDiagnostics` once declared a `RunningSagas` instrument that nothing ever recorded. The production-readiness
review (section 6) deleted it rather than wire it as an `UpDownCounter`: that is process-local and
non-idempotent, so a restart, a redelivery or a second replica desynchronises it for good. It named the right
instrument instead, an `ObservableGauge` backed by `COUNT(*) WHERE Status = Running` against the store, and left it
as a follow-up for `VSaga.Observability` (`docs/observability.md`, "No `vsaga.saga.running` gauge"). Today
`started - (completed + failed)` is not a valid in-flight estimate either (`started` counts first-step successes,
not instance creations), so there is no in-flight number at all.

Four things shape the answer.

1. **A meter callback is synchronous and a store is not.** `ObservableGauge` calls back on the metric reader's
   thread (a dedicated one for a periodic exporter, a request thread for a pull exporter such as Prometheus).
   The stores are async, and EF Core's `DbContext` is scoped. A callback that holds a store directly is the
   captive-dependency shape `SagaTimeoutDispatcherHostedService` had before it was fixed (a singleton holding a
   scoped service); a callback that blocks on the store (`.GetAwaiter().GetResult()`) holds a metric-reader thread
   for the length of a database round trip on every collection.
2. **There is precedent.** The stuck-saga gauge (`vsaga.saga.stuck`, `SagaStuckMetrics` in
   `dotnet/src/VSaga.Core/Runtime`) reads the store from a hosted service on an interval, through an
   `IServiceScopeFactory` scope per sweep, caches the result, and lets the callback return the cache. A restart
   loses only the previous reading, and three failed sweeps in a row withdraw it. This gauge is the same shape.
3. **The reader has no cheap count.** `ISagaSummaryReader.ListAsync` returns a `TotalCount`, so
   `ListAsync(Status = Running, PageSize = 1)` is a count, but it is a count that also runs a sort and a page read
   and, with a `SagaType` filter, which the `saga.type` tag needs, costs one call per saga type. On Redis a status
   plus a saga type is a materialised server-side `ZINTER` that reads every member of the smaller of the two sets
   (`RedisSagaSummaryReader.MaterializeAsync`), which `docs/observability.md` ("Stuck sagas", "Cost") already
   warns about. A purpose-built count is one grouped query on EF Core and MongoDB, and on Redis it is `ZINTERCARD`,
   which counts without returning a member.
4. **Where it runs.** `VSaga.Observability` references only `VSaga.Abstractions`, not `VSaga.Core`. The gauge
   needs the reader and nothing else, so it can live where `AddVSagaOpenTelemetry` already does and be wired by a
   host that has a store (the dashboard API, an engine host) and not by one that does not (a participant-only
   host).

The maintainer asked for this on 2026-11-09 (task B of the parallel session): a real gauge, a store reachable from
the meter callback via `IServiceScopeFactory` without the captive-dependency shape, the cost per scrape on each
provider stated, and the metric table in `docs/observability.md` updated.

---

## Decision

**`vsaga.saga.running` is an `ObservableGauge<long>` (unit `{saga}`, tag `saga.type`) whose value is the number of
instances in status `Running`, read from the store by a background service in `VSaga.Observability` on an interval
and cached for the callback.** It is opt-in: `services.AddVSagaRunningSagasGauge(configure)`.

### The contract change (`VSaga.Abstractions`)

One new member on `ISagaSummaryReader`:

```csharp
Task<IReadOnlyDictionary<string, long>> CountByTypeAsync(SagaStatus status, CancellationToken cancellationToken = default);
```

It returns, per saga type, how many instances are in `status`, **omitting types with none**, keyed by the saga type name
as stored. It is a **default interface member**: its default implementation reads
`GetSagaTypesAsync` and takes the `TotalCount` of one `ListAsync(Status, SagaType, PageSize = 1)` per type, so a
third-party reader written before this ADR compiles and answers correctly, just expensively. Every provider in the
repository overrides it with the cheapest form its storage has:

| Provider | Implementation |
| --- | --- |
| InMemory | one pass over the snapshot dictionary, grouped by type; no I/O |
| EF Core (Postgres, SQL Server, SQLite) | `SELECT SagaType, COUNT(*) FROM SagaInstances WHERE Status = @s GROUP BY SagaType` |
| MongoDB | `aggregate [{$match: {status}}, {$group: {_id: "$sagaType", n: {$sum: 1}}}]`, served from the existing `Status, UpdatedAtTicks, SagaType, ...` index without reading a document |
| Redis | the types from `ix:types` (`HKEYS`), then one `ZINTERCARD 2 ix:status:<n> ix:type:<type>` per type, issued together; no member is returned |

The conformance suite (`SummaryReaderConformanceTests`) gains cases that run against every provider: only the
requested status is counted, per type, types with none are absent, an empty store answers empty, a status change is
reflected, and a type name is a key as stored. This is a contract change: a third-party provider that does not
override it keeps working through the default and fails nothing, but it is only conformant, not cheap.

### The gauge (`VSaga.Observability`)

- `RunningSagasRefreshHostedService` (a `BackgroundService`, internal) holds an `IServiceScopeFactory`, never a
  store. Every `RefreshInterval` (30 seconds by default, first refresh at start) it creates a scope, resolves
  `ISagaSummaryReader` from it, calls `CountByTypeAsync(Running)` under a deadline of one interval, and publishes
  the result. Nothing scoped is held across a refresh, so there is no captive dependency.
- `RunningSagasMetrics` (internal, static, as `SagaStuckMetrics` is) holds the readings and the one instrument; the
  meter callback only returns what the service left, so it never blocks and never touches the store.
- **Zeros.** The reader omits types with none, so the service remembers every type it has seen for the life of the
  process and reports a remembered type that is absent from a reading as `0`: an alert rule sees a recovery as a
  value. A type never seen running in this process is absent, not zero.
- **Failure.** A failed or timed-out refresh keeps the previous reading and is logged at Error. After three in a
  row the reading is withdrawn (the instrument reports nothing), so a dead refresh does not look like a quiet
  store; the next success publishes again. Disposal withdraws the reading.
- **No reader.** A host that enables the gauge but registered no `ISagaSummaryReader` logs one warning and the
  service stops; it does not throw at start.
- **Opt-in, with its own switch.** `AddVSagaRunningSagasGauge(Action<RunningSagasOptions>?)` registers the service;
  calling it is the opt-in, so `AddVSagaOpenTelemetry` keeps its signature and an upgrade adds no query on its own.
  `RunningSagasOptions.RefreshInterval` is the only option; a zero, negative or over-a-day value throws
  `ArgumentOutOfRangeException` from the call.
- **Aggregation.** Every replica that enables it reports the same population, so a dashboard aggregates with `max`
  across replicas, not `sum` (the stuck gauge's rule). Enabling it on one process per store is enough.

### What it counts

Instances whose `Status` is `Running`. A `Compensating` saga is not running in this sense (it is rolling back, and
`vsaga.saga.stuck` already watches it), and a terminal saga never is. The number is a reading of the store at the
last refresh, so it is up to `RefreshInterval` old plus the exporter's own interval.

---

## Options considered

| Option | Verdict |
| --- | --- |
| `UpDownCounter` incremented on start and decremented on terminal status | Rejected in section 6 of the production-readiness design, and still: process-local, not idempotent, desynchronised for good by a restart, a redelivery or a second replica. |
| Query the store inside the meter callback, blocking on the result | Rejected. It blocks a metric-reader thread (a request thread under a pull exporter) for a database round trip on every collection, makes the store's cost proportional to the scrape rate and to the number of exporters. |
| Query inside the callback with a cache and a fire-and-forget refresh when the cache is stale | Rejected. It needs no timer and costs nothing with no scraper, but it makes every reading one scrape old, hides a failure inside an unobserved task, and needs its own single-flight and shutdown handling. The background service has all of that already (`SagaStuckSweepHostedService`). |
| Hold the store in the callback by capturing a scope at startup | Rejected. That is the captive dependency: one `DbContext` for the life of the process, shared by every collection. |
| Put the gauge in `VSaga.Core`, beside `vsaga.saga.stuck` | Rejected. The production-readiness review named `VSaga.Observability`, which is where a host that wires OpenTelemetry already looks, and which a participant-only host (no store) can leave out entirely. `VSaga.Core` would run a store query in every engine host whether or not anything scrapes it. |
| Reuse `ListAsync(Status = Running, SagaType = t, PageSize = 1)` per type, no contract change | Rejected as the implementation, kept as the default for foreign readers. EF Core and MongoDB pay a sort and a page read for nothing, and Redis pays a materialised `ZINTER` per type (the cost the stuck-saga note already warns about). |
| `CountAsync(SagaListFilter)` returning one number | Rejected. The tag is `saga.type`, so a grouped count is what is needed; a single filtered count would still be one query per type. |
| A total with no `saga.type` tag: one `COUNT(*)` / `ZCARD` / `countDocuments` | Considered, and the cheapest option on every provider (Redis: one `ZCARD`). Rejected because every other instrument carries `saga.type`, and a per-type number is what tells an operator which saga is piling up. If the per-type cost on Redis proves too high in someone's store, the answer is a longer `RefreshInterval`, not a different shape. |
| A `ZINTER` or per-type counter maintained inside the Redis persist script | Rejected for now. It would make the Redis count `O(types)` with no intersection, but it changes the stored key space (a new key, a schema version bump and a backfill for existing data) for a gauge that is opt-in. Recorded as the escape hatch if the measured cost matters. |
| Always on | Rejected. Every stated cost is a read the store did not do before; the stuck-saga sweep is opt-in for the same reason. |

---

## Consequences

**Easier.**
- There is an in-flight number: `vsaga.saga.running{saga.type}`, correct after a restart, a redelivery or a second
  replica, because it is read from the store rather than accumulated.
- An alert on a pile-up (a type's running count that only grows) does not depend on the stuck-saga sweep.
- Any future per-status gauge is a call to the same method with another status.

**Harder, and the costs it states.** Each refresh is one grouped read per process that enables it, every
`RefreshInterval`, independent of how often the metric is scraped. Per refresh, with `T` saga types, `R` instances
in the status and `N` instances in all:

| Provider | Cost of one refresh |
| --- | --- |
| InMemory | `O(N)` in process, no I/O. |
| EF Core Postgres, SQL Server | One `GROUP BY` over `WHERE Status = @s`. The `(Status, UpdatedAtUtc)` index finds the `R` rows and the saga type is read from the table (`O(R)`); the `(SagaType, Status)` index can serve it as an index-only scan (`O(N)` entries, no table reads). Whether the planner picks the first or the second is the engine's choice; Measured: 10.3 ms on SQL Server and 21.1 ms on Postgres at 100,000 instances with 60,000 `Running` (the interface default: 162 ms and 88 ms). |
| MongoDB | One aggregation, covered by the `Status, UpdatedAtTicks, SagaType, CorrelationId` index: `O(R)` index keys, no document read. Measured: 28.2 ms at the same size (default: 159 ms). |
| Redis | `1 + T` commands, pipelined. `ZINTERCARD` of the status bucket and a type's index costs `O(min(R, size of the type) x 2)` and returns a number, so the refresh costs at most `T x R` and at most `N` set operations. It is the dearest of the five because the indexes are not grouped by status and type together. Measured: 42.9 ms at 100,000 instances with 60,000 `Running` (server time about 5 ms per `ZINTERCARD`, 8 types; default: 215 ms), and 297 ms at 500,000 with 300,000 `Running` (about 37 ms per `ZINTERCARD`, default 1.19 s), so it grows faster than the data and each call occupies Redis's single command thread. The full table, with its method and caveats, is in `docs/observability.md#running-sagas`. |

**The stated limitations.**
- The reading is a sample, not an event: up to `RefreshInterval` stale, plus the exporter's interval. A saga that
  starts and finishes between two refreshes is never counted.
- Two replicas that enable it both read the store; aggregate with `max`. One replica per store is enough.
- A type that has not been `Running` in this process since it started is absent rather than `0`.
- `AddVSagaRunningSagasGauge` is a new public method and `CountByTypeAsync` a new interface member, so the
  `VSaga.Abstractions` and `VSaga.Observability` public surface grows by that. The default implementation keeps
  an out-of-tree `ISagaSummaryReader` source- and behaviour-compatible.
- The gauge cannot be seen without an exporter; the repository wires none (`AddVSagaOpenTelemetry`'s
  unopinionated stance), so the live check is a `MeterListener` over a real store, not a dashboard panel.
- `VSaga.Observability` gains its first test project, `VSaga.Observability.Tests`, which must be added to
  `dotnet/VSaga.slnx`.
