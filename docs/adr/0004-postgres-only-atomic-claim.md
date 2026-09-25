# ADR 0004: The atomic claim is Postgres-only by choice, and the fallback is not multi-instance-safe

**Status:** **Accepted** — retroactive. Records a decision already implemented and shipped.
**Date:** 2026-09-25 (decision made earlier; see `docs/design/production-readiness.md` §4)
**Relates to:** [`0003-persistence-contract-clauses.md`](0003-persistence-contract-clauses.md) —
clause 10's saga-type filter touches the same claim path.

> **Retroactive.** This records reasoning that until now existed **only in code comments**. It changes
> nothing. It exists because the one reference doc that describes this behaviour
> (`docs/persistence.md:62-66`) describes it *wrongly*, in a way that can cost a production user
> duplicate side effects — see Consequences.

---

## Context

`ISagaTimeoutStore.ClaimDueAsync` and `ISagaOutboxStore.ClaimPendingAsync` must atomically claim a batch
of rows so that multiple `SagaTimeoutDispatcherHostedService` /
`SagaOutboxDispatcherHostedService` instances — replicas of the same service — cannot both claim the
same row and fire the same timeout or publish the same message twice.

The EF Core provider implements this **twice**, once per store, with the same shape:

- `EfCoreSagaTimeoutStore.cs:87-99` and `EfCoreSagaOutboxStore.cs:117-129` issue an atomic
  `UPDATE … WHERE "Id" IN (SELECT … ORDER BY … LIMIT … FOR UPDATE SKIP LOCKED) RETURNING …`.
- `EfCoreSagaTimeoutStore.cs:51-73` and `EfCoreSagaOutboxStore.cs:81-107` are a plain
  load-then-update fallback, explicitly documented as "not safe for multiple concurrent dispatcher
  instances".

Which path runs is decided by an **exact string comparison against the Npgsql provider name**:

```csharp
private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";
...
string.Equals(db.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal)
```

(`EfCoreSagaTimeoutStore.cs:37`, `:46-49`; duplicated verbatim at `EfCoreSagaOutboxStore.cs:67`,
`:76-79`.)

---

## Decision

**The atomic claim is provided for Postgres only. Every other EF Core provider silently takes the
load-then-update fallback, which is correct for exactly one dispatcher instance and no more.**

This was deliberate, and the rationale is recorded at `EfCoreSagaTimeoutStore.cs:39-45`: the fallback
"exists for provider portability, not as a v1 shortcut". `VSaga.Persistence.EFCore` depends only on
`Microsoft.EntityFrameworkCore`, `.Relational` and DI.Abstractions
(`VSaga.Persistence.EFCore.csproj:14-16`) so that it stays usable with any relational provider;
`FOR UPDATE SKIP LOCKED` is not portable, so a provider-conditional branch was the only way to have both.

Two adjacent decisions are recorded here because they share the same call path and are equally
undocumented:

**Claim marks terminal, so redelivery is at-most-once.** `ClaimDueAsync` marks a row `Fired` and
`ClaimPendingAsync` marks it `Dispatched` *as part of the claim*. If the dispatcher then fails to act on
it, the row is not retried — `SagaOutboxDispatcherHostedService.cs:42-45` states this as an intentional
trade-off ("an at-most-once redelivery attempt, not a retried one"), matching the timeout dispatcher's
identical choice. The word "outbox" carries the opposite default assumption in most readers, which is
why it belongs in a record.

**Every `DateTimeOffset` is physically a UTC `DateTime`.** A global convention
(`VSagaDbContext.cs:17-20`) applies `DateTimeOffsetToUtcDateTimeConverter` (`:11-13`) to every
`DateTimeOffset` property. On Postgres the stored column therefore truncates to microsecond resolution
while the serialized state blob keeps full 100-nanosecond ticks — so a projected timestamp and the same
value inside `DataJson` are not bit-identical, and any comparison between them must be at storage
resolution.

---

## Consequences

### Negative — and one is live

**`docs/persistence.md:62-66` states the gate incorrectly**, saying "Providers without that clause
(SQLite, used in tests) fall back to a plain select-then-update". That reads as *"only SQLite, only in
tests"*. The gate is an exact match on the Npgsql provider name, so **every** non-Npgsql provider takes
the fallback — including `UseSqlServer`, which the same document recommends three lines earlier at
`:19`. Two replicas on SQL Server will double-claim timeouts and double-publish outbox rows, silently,
with no error anywhere.

This is worse than a documentation gap because SQL Server *does* have an equivalent construct
(`UPDLOCK, READPAST`), so a reader has no reason to suspect the fallback applies to them.

**Required:** correct `docs/persistence.md`, and emit a startup warning when
`db.Database.ProviderName` is not Npgsql, naming the concurrency limitation. A silent unsafe default on
a documented-as-supported provider is not acceptable once it is known.

**The same gate is written twice**, byte-identically, in two files that must never disagree — folded
into one internal constant by
[`../design/persistence-contracts.md`](../design/persistence-contracts.md) commit 21.

### Positive

- `VSaga.Persistence.EFCore` stays genuinely provider-agnostic, which is what allows the SQLite-backed
  test suite to exercise the same store code the Postgres deployment runs.
- The fast path is a single statement — no lease, no re-claim, no lock table.

### Neutral

- The MongoDB and Redis plans both inherit this shape as a requirement rather than a choice:
  [`../design/mongodb-persistence.md`](../design/mongodb-persistence.md) §6.1 and
  [`../design/redis-persistence.md`](../design/redis-persistence.md) §4.7 each state their claim's
  failure mode against this one, and both note they are *not* tighter than Postgres.

---

## What would invalidate this decision later

1. **A second provider is documented at production tier.** The provider-conditional branch then needs a
   capability concept rather than one hard-coded name — the shape ADR 0003 clause 10 and the conformance
   suite's `SupportsConcurrentClaim` flag already anticipate.
2. **SQL Server becomes a supported target.** `UPDLOCK, READPAST` is the equivalent and would need its
   own branch; until then the documentation must say SQL Server is single-dispatcher-only.
3. **The engine gains a lease / re-claim model.** That would change at-most-once to at-least-once for
   every provider and remove the reason the claim marks terminal up front.
