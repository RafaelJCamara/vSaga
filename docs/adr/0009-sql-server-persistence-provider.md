# ADR 0009: SQL Server as a persistence provider, on the EF Core provider, with an atomic claim

**Status:** **Accepted** — 2026-11-02, by the maintainer, after it was written as Proposed alongside the
implementation, at the maintainer's request to add SQL Server persistence. **Implemented** 2026-11-02; the record of
what was built and checked is at the end. The questions below were answered as the build took them.
**Date:** 2026-11-02
**Relates to:** [`0004-postgres-only-atomic-claim.md`](0004-postgres-only-atomic-claim.md) — this ADR extends the
atomic claim to a second database, which changes what that record calls "Postgres only".
[`0005-saga-state-storage-model.md`](0005-saga-state-storage-model.md) — the storage model is reused unchanged.
[`0003-persistence-contract-clauses.md`](0003-persistence-contract-clauses.md) — the clauses the conformance suite
holds this provider to.

---

## Context

vSaga ships four persistence providers: EF Core on Postgres (the reference), MongoDB, Redis and in-memory. Teams
whose estate is SQL Server today cannot adopt vSaga without also running Postgres, which is the same adoption
barrier ADR 0001 and ADR 0002 answered for MongoDB and Redis.

Unlike those two, SQL Server needs no new store implementation. `VSaga.Persistence.EFCore` is deliberately
provider-agnostic: it depends on `Microsoft.EntityFrameworkCore` and `.Relational` only, its README says
`UseSqlServer` is a supported hookup, and its date convention (`DateTimeOffsetToUtcDateTimeConverter`) names SQL
Server as a target. What is missing is everything around that promise:

1. **No migrations.** The migrations live in `VSaga.Persistence.EFCore.Postgres` because they are generated
   against one provider's type mapping. `MigrateAsync` against SQL Server finds none.
2. **No atomic claim.** `ClaimDueAsync` and `ClaimPendingAsync` take the `FOR UPDATE SKIP LOCKED` path only when
   `ProviderName` is Npgsql (ADR 0004). SQL Server would silently take the load-then-update fallback, correct for
   exactly one dispatcher instance. Two replicas of a service sharing one SQL Server database would then fire the
   same timeout or publish the same outbox row twice. A provider presented as production-grade cannot ship that
   as its default.
3. **No host wiring, no tests, no docs.** `Persistence:Provider` accepts `Postgres`, `Redis` and `MongoDb`; the
   conformance suite runs against Postgres and SQLite only.

Other facts that shape the choice, checked against the code:

- The schema is portable as it stands. Longest keys: the primary key of `SagaConsumerRegistrations`
  (`ServiceName` 200 + `MessageType` 400 characters) is the one place a SQL Server limit bites (below); every other
  index is within the 1,700-byte nonclustered limit.
- `HasFilter("\"BusinessKey\" IS NOT NULL")` is a double-quoted identifier. SQL Server accepts it with
  `QUOTED_IDENTIFIER ON`, which Microsoft.Data.SqlClient sets by default; a filtered index requires that setting.
- Search is `ToLower().Contains(...)`, which EF Core translates to `LOWER(...)` and `LIKE`; that works under any
  collation.
- `SagaInstances.Version` is an EF concurrency token and the stores compare it in code, not through `rowversion`.
  Nothing there is provider-specific.

## Decision

**Add SQL Server as a fifth provider, as a second migrations package over the existing EF Core provider, and extend
the atomic claim to it.**

1. **A new project, `VSaga.Persistence.EFCore.SqlServer`**, mirroring `VSaga.Persistence.EFCore.Postgres`: the
   migrations plus the `Microsoft.EntityFrameworkCore.SqlServer` dependency, nothing else. `VSaga.Persistence.EFCore`
   gains no database-specific package reference and stays provider-agnostic. A consumer points
   `MigrationsAssembly("VSaga.Persistence.EFCore.SqlServer")` at it, as with Postgres.
2. **The atomic claim for SQL Server**, selected, like Postgres's, by an ordinal comparison against the provider
   name (`EfCoreProviderNames.SqlServer`). It is one statement per store:
   `WITH due AS (SELECT TOP (n) ... WITH (ROWLOCK, UPDLOCK, READPAST) WHERE ... ORDER BY ...) UPDATE due SET ... OUTPUT inserted.*`.
   `READPAST` makes a second dispatcher skip rows the first has locked rather than wait or take them, which is the
   behaviour `SKIP LOCKED` gives. The saga-type filter binds one JSON array parameter read with `OPENJSON`, the
   counterpart of Postgres's `= ANY(array)`, so the set is never expanded into a variable number of parameters.
   Every other provider keeps the fallback.
3. **Selected by `Persistence:Provider = SqlServer`** in `Dashboard.Api` and the OrderProcessing sample host, with
   the connection string in `ConnectionStrings:VSaga`, exactly as Postgres's. Both hosts must agree, as today.
4. **Held to the same contracts.** The provider runs the whole `VSaga.Persistence.Conformance` suite against a real
   SQL Server, on the migrated schema, including the concurrent-claim suite (`SupportsConcurrentClaim = true`).
5. **Migrations are generated** with `dotnet ef`, never hand-written, from a single `InitialCreate` over the current
   model. The Postgres history of ten migrations is not replayed: a fresh provider has nothing to upgrade.
6. **A compose overlay, a runnable sample and documentation** in the same shapes as the other providers
   (`docker-compose.sqlserver.yml`, `dotnet/samples/Persistence/VSaga.Samples.Persistence.EFCore.SqlServer`,
   `docs/persistence.md`, `docs/configuration.md`).

### Options weighed

- **Do nothing; tell users to call `UseSqlServer` themselves.** This is what the README implies today. Rejected: it
  ships no migrations and silently degrades the claim, so a user who follows it gets duplicate timeouts under two
  replicas with no warning. That is the failure ADR 0004 already flagged for non-Postgres providers.
- **Put the SQL Server migrations in `VSaga.Persistence.EFCore`.** Rejected: it would make the provider-agnostic
  package depend on one database and ship migrations for a database most consumers do not use.
- **Use an application lock (`sp_getapplock`) or serializable transactions for the claim.** Rejected: a lock around
  the whole claim serialises dispatchers instead of letting them take disjoint batches, and serializable isolation
  adds range-lock deadlocks to a path that runs every second. `READPAST` is the primitive built for a queue table.
- **Make the claim portable (a status-column compare-and-swap per row).** Rejected for now: one round trip per row
  and a retry loop on contention, against one statement. It would also change Postgres's tested behaviour.
- **Use `rowversion` for optimistic concurrency.** Rejected: the stores compare an explicit `Version` the state blob
  also carries, and ADR 0005 keeps the two in step by construction. A second concurrency mechanism would diverge
  the providers.

## Consequences

**Easier.** A team on SQL Server adopts vSaga with its existing database, backups and HA, and gets a multi-replica-safe
claim. The change reuses every store, so there is no second implementation to keep in step with the contracts; the
conformance suite proves the new combination rather than trusting the reuse.

**Harder.**

- **ADR 0004's wording changes.** "The atomic claim is Postgres-only" is now "Postgres and SQL Server". The ADR is
  left as written, as the history of a decision, and a pointer is added at its top. `persistence.md`'s "Concurrency-safe
  claiming" paragraph is updated.
- **The `SagaConsumerRegistrations` clustered key can exceed SQL Server's 900-byte limit.** Its key is
  `ServiceName` (nvarchar(200)) plus `MessageType` (nvarchar(400)); the declared maximum is 1,200 bytes. SQL Server
  creates the table with a warning and rejects only an insert whose actual key exceeds 900 bytes, i.e. a service name
  and message type together longer than 450 characters. Real fully-qualified type names sit well under that, and
  lowering the declared lengths would need a Postgres migration, so the limit is documented rather than changed.
- **String comparison follows the database collation, except where a contract demands exactness.** The default
  SQL Server collation is case-insensitive and ignores trailing spaces, so two saga types differing only in case
  would share a primary key, and a lookup by saga type or business key is not case-sensitive. The conformance suite
  found the two places that matter on the first run against a real SQL Server and they are fixed: a set of saga types
  (`SagaListFilter.SagaTypes`) and the types a timeout claim is scoped to must match exactly, so those are compared
  under `Latin1_General_100_BIN2` (the claim with a `DATALENGTH` check as well, since `=` pads). The remaining
  comparisons are left to the collation: CLR type names do not differ only by case in practice, and making the
  columns binary would need either a model change shared with Postgres or a hand-edited migration. The guidance is
  not to set a case-sensitive collation without understanding that every key comparison changes with it.
- **A fifth provider to verify.** Anything that touches the claim, the indexes or the date convention is now checked
  against two relational databases, and the CI cost grows by one more Testcontainers suite (the SQL Server image is
  large and needs the EULA accepted by Testcontainers' module).
- **Requires SQL Server 2016 or later** (compatibility level 130), for `OPENJSON` and filtered-index behaviour, and EF
  Core 10's own floor is the same. Azure SQL Database and Managed Instance are in scope; both support `READPAST`.
- **Timestamps keep 100 ns**, finer than Postgres's microsecond (`datetime2` default), so the provider declares
  `TimestampResolution` of one tick.

**Not decided here.** Whether to ship a migration-less `EnsureCreated` path (rejected as in ADR 0004's context: it
bypasses `__EFMigrationsHistory`), and a Tier-B style durability claim (SQL Server has a durable log by default, so
there is no tier to describe).

## Implementation and verification (2026-11-02)

What was built and what was checked, recorded here because the questions above were answered as the build took them.

- **Built.** `VSaga.Persistence.EFCore.SqlServer` with one generated `InitialCreate`; `EfCoreProviderNames.SqlServer`
  and the `READPAST` claim in both stores; `Persistence:Provider=SqlServer` in `Dashboard.Api` and the OrderProcessing
  host (no connection string default: a SQL Server string carries a login, and Sonar S2068 rejected one in source);
  `docker-compose.sqlserver.yml` (UI 4900, API 5780, SQL Server 1435); the
  `VSaga.Samples.Persistence.EFCore.SqlServer` sample (SQL Server 1436); the CI port and identity-volume checks, and
  `EXPECTED_PACKAGES` 19 to 20 in the release workflow.
- **Conformance.** The whole suite runs against SQL Server 2022 on the migrated schema (110 SQL Server cases including
  the two below; 333 in the EF Core test project with Postgres and SQLite). The first run failed two cases, both on
  exact saga-type matching, and the collation fix above followed.
- **Mutation.** Removing `READPAST` from both stores passes the cross-provider concurrent-claim suite, which proves
  safety only: an `UPDATE` takes update locks itself, so a dispatcher waits behind a claim in flight instead of
  double-claiming. `SqlServerClaimTests` holds an uncommitted claim on a second connection and requires the next
  claim to return the other rows without waiting; with `READPAST` removed exactly those two cases fail. A bare
  `UPDLOCK` on the row is not a faithful holder: `READPAST` skips on the index the scan reads, which a covering
  nonclustered index does not see.
- **Live.** `docker compose -p vsaga-sqlserver -f docker-compose.yml -f docker-compose.sqlserver.yml up -d --build`:
  all six containers healthy, `/health` reports `persistence` healthy, one migration applied, the dashboard lists the
  OrderProcessing sagas read from SQL Server, and timeouts and outbox rows were claimed and marked. The standalone
  sample ran its three scenarios, including a fired timeout.
- **Not verified.** Two dispatcher replicas against one database under load (the concurrent-claim suite and
  `SqlServerClaimTests` cover it at the store level), Azure SQL, SQL Server 2016 to 2019, and a case-sensitive
  database collation. The CI compose job's new `sqlserver` leg and the release workflow's package count were edited
  but not run here.

**Closed 2026-11-09, except Azure SQL and SQL Server 2016 to 2019.** The gaps above were run afterwards, on a local
Windows machine with Docker, against SQL Server 2022. Nothing in the decision changed.

- **Case-sensitive collation: run.** The whole SQL Server conformance suite and `SqlServerClaimTests` passed, 111
  cases, with every case's database created `COLLATE Latin1_General_CS_AS`
  (`VSAGA_SQLSERVER_COLLATION`, a new fixture setting; a case reads `DATABASEPROPERTYEX` back so a run whose
  variable was ignored fails). A database created that way works; converting an existing one was not tried.
- **Two replicas under load: run.** Two complete hosts in one process (the real engine and both dispatcher hosted
  services, separate connection pools) shared one migrated database over the in-memory transport. 1,000
  `CheckoutSaga` orders (60% never answered by the gateway, so their timeouts fired) were submitted through both
  hosts while 1,500 aged outbox rows were seeded, with the dispatcher timers sped up 25 times and the outbox batch
  at 7. Recording wrappers on the timeout and outbox stores logged each claimed row per host: 986 timeouts and 1,500
  orphaned outbox rows, none claimed twice, each host about half; every saga finished, and each ended `TimedOut` if
  and only if its timeline held exactly one `TimeoutFired`; no error-level log entry. Not covered: two operating
  system processes or containers, a real broker (a second `order-processing` container against RabbitMQ was not
  run), and a run at the unscaled five-second poll. The harness is not in the repository.
- **CI compose job's `sqlserver` leg: run in equivalent, not in the workflow.** The leg is configuration checks, no
  container (`ci.yml`): the dashboard port pair for the overlay (5780 and 4900, both on 127.0.0.1) and the identity
  database sitting on the `vsaga-dashboard-identity` volume. The same predicates, applied to
  `docker compose ... config --format json` for the base file and every overlay, all pass. They were
  re-implemented in Python because `jq`, which the workflow uses, is not installed on that machine, so the workflow
  file itself was not executed.
- **Release workflow's package count: run in equivalent.** `dotnet build` of the solution in Release (zero warnings),
  then `dotnet pack` with the workflow's flags at `0.0.1-dryrun.1` produced 20 `.nupkg` and 20 `.snupkg`, and the
  workflow's "Check the packages" script, run unchanged, accepted them against `EXPECTED_PACKAGES: 20`. The
  workflow's own `dotnet test` step and the GitHub-hosted runner were not run.
- **Still not verified:** Azure SQL Database and Managed Instance, and SQL Server 2016 to 2019. Neither is
  available on that machine; the floor stays a design claim (`OPENJSON`, `READPAST`).
