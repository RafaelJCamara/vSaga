# Persistence

vSaga ships two persistence providers, both implementing the same set of store contracts
(`VSaga.Abstractions.Persistence`): `ISagaSnapshotStore<TState>`, `ISagaSummaryReader`,
`ISagaEventLogStore`, `ISagaTimeoutStore`, `ISagaOutboxStore`, `ISagaAdminStore`, and
`IServiceTopologyStore`.

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
outbox table (plus its own follow-up index migration), and the business-key column with its partial
unique index.

**The five tables** `VSagaDbContext` maps, for anyone querying the database directly:

| Table | Holds |
| --- | --- |
| `SagaInstances` | One row per saga instance (the snapshot), keyed by `(SagaType, CorrelationId)`. |
| `SagaEventLog` | The append-only `SagaLogEntry` timeline behind the dashboard (see [`observability.md`](observability.md)). |
| `SagaTimeouts` | Scheduled/fired timeouts, claimed by the dispatcher below. |
| `SagaOutboxMessages` | Transactional-outbox rows, staged with the snapshot and drained inline or by the poller. |
| `SagaConsumerRegistrations` | The service topology (`IServiceTopologyStore`), keyed by `(ServiceName, MessageType)`. |

**Concurrency-safe timeout claiming.** `EfCoreSagaTimeoutStore.ClaimDueAsync` uses an atomic
`UPDATE ... WHERE ... FOR UPDATE SKIP LOCKED ... RETURNING` on Postgres, so multiple
`SagaTimeoutDispatcherHostedService`/`SagaOutboxDispatcherHostedService` instances (or replicas) can
poll concurrently without double-claiming the same row. Providers without that clause (SQLite, used in
tests) fall back to a plain select-then-update.

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

`VSaga.Persistence.InMemory` (`AddVSagaInMemoryPersistence()`) backs every store contract with a single
shared `InMemorySagaStore` singleton — intended for local development and as the foundation of
`VSaga.Testing`'s `SagaTestHarness` (see [`testing.md`](testing.md)), **not for production use**: state
does not survive a process restart, and there is no concurrency-safe claim semantics beyond a single
process's own in-memory locking.

```csharp
services.AddVSagaInMemoryPersistence();
```

## Choosing a provider

Use EF Core/Postgres for anything that needs to survive a restart, run more than one replica, or be
queried by the dashboard against real historical data. Use in-memory for local development without a
database, or (via `SagaTestHarness`) for unit tests that exercise the real engine without any
broker/database dependency at all.
