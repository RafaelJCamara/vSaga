# Persistence

vSaga ships two persistence providers, both implementing the same set of store contracts
(`VSaga.Abstractions.Persistence`): `ISagaSnapshotStore<TState>`, `ISagaSummaryReader`,
`ISagaEventLogStore`, `ISagaTimeoutStore`, `ISagaOutboxStore`, `ISagaAdminStore`, and
`IServiceTopologyStore`.

Both are held to the same written contracts by the cross-provider `VSaga.Persistence.Conformance`
suite, which a third-party provider can run against itself. The eight divergences and three shared
defects the two once had are catalogued, with their fixes, in
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
> `"Npgsql.EntityFrameworkCore.PostgreSQL"` (`EfCoreSagaTimeoutStore.cs:37`, `:46-49`;
> `EfCoreSagaOutboxStore.cs:67`, `:76-79`). **Every** other provider — including `UseSqlServer`,
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
queried by the dashboard against real historical data. Use in-memory for local development without a
database, or (via `SagaTestHarness`) for unit tests that exercise the real engine without any
broker/database dependency at all.
