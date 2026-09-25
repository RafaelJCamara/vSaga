# ADR 0005: One shared table, one opaque state blob, and a promotion rule for queryable fields

**Status:** **Accepted** — retroactive. Records a decision already implemented and shipped.
**Date:** 2026-09-25 (decision made at the project's origin)
**Relates to:** [`0001-mongodb-persistence-provider.md`](0001-mongodb-persistence-provider.md) Q12,
which reopens the blob format for a document store.

> **Retroactive and deliberately narrow.** The *promotion* half of this decision is already recorded in
> detail at [`../design/production-readiness.md`](../design/production-readiness.md) §5.2 — why
> `DataJson` is an opaque `text` blob rather than `jsonb`, why a queryable field must become a real
> column, and the invariant that the two never disagree. **This ADR does not restate that.** It records
> only the part with no record anywhere: the shared-table root, and the promotion rule as a *rule*.

---

## Context

Every saga instance, of every saga type, is one row in **one** table. The business-specific state lives
in a single serialized blob; a small fixed set of fields is duplicated out of that blob into real
columns.

The only statement of this anywhere is a doc comment on the entity itself
(`dotnet/src/VSaga.Persistence.EFCore/Entities.cs:6-9`):

> Current-state snapshot row, shared by every saga type — the business-specific fields live in
> `DataJson` (the serialized TState), so one table works for any number of saga types.

That says *what*, not *why*, and records no alternative. The obvious alternative — a table per saga type,
with each saga's own fields as columns — is what most hand-rolled saga stores do and what an ORM-shaped
instinct reaches for first.

---

## Decision

**One shared table (`SagaInstances`), keyed by `(SagaType, CorrelationId)`, holding the serialized
`TState` as an opaque blob, plus a fixed set of promoted columns.**

The promoted set is exactly: `SagaType`, `CorrelationId`, `Kind`, `CurrentState`, `Status`, `Version`,
`ParentSagaType`, `ParentCorrelationId`, `BusinessKey`, `CreatedAtUtc`, `UpdatedAtUtc`
(`Entities.cs:10-70`).

**The rule this establishes: anything that must be queried, filtered, sorted or uniquely constrained
must first become a promoted column.** The blob is opaque to the database by design. Every promotion
since has followed it — the sub-saga parent pointer, the business key, the service-map fields — each
with its own migration.

### Why one table rather than one per saga type

- **`ISagaSummaryReader` is saga-type-agnostic by contract.** `ISagaSummaryReader.cs:11-14` exists so
  the dashboard can list, filter and page across *every* saga type without knowing any of them.
  `VSaga.Dashboard.Api` deliberately never calls `AddSaga<>()` (`Program.cs:33-36`). A table per saga
  type makes that query a dynamic `UNION` over a set of tables discovered at runtime — or forces the
  dashboard to know every saga definition, which is precisely what it is designed not to do.
- **Adding a saga type must not require a schema migration.** `AddSaga<TState>()` is a DI registration;
  under a table-per-type model it would become a deployment step.
- **Two saga types may legitimately track the same correlation id**, which is what lets a choreographed
  saga observe messages already flowing under an orchestrated saga's id. The composite key expresses
  that directly (`VSagaDbContext.cs:98-101`).

### What it costs, accepted knowingly

- The blob is not queryable, so **every** new queryable field is a migration plus a projection plus the
  never-disagree invariant `EfCoreSagaSnapshotStore.cs:72-78` names. Eight migrations exist and several
  are exactly this.
- A saga's own fields get no database-level typing or constraints.
- The projection and the blob can drift. They have: ADR 0003 records that both providers leave
  `UpdatedAtUtc` stale in the blob on `ResetStateAsync`, and in-memory leaves `Version` stale too.

---

## Consequences

### Positive

- The dashboard's provider-agnostic, saga-type-agnostic premise (`docs/dashboard.md:6-8`) is
  implementable at all.
- Registering a new saga type is a code change, never a schema change.
- The model ports to a document store almost unchanged — both the MongoDB and Redis plans keep the blob
  as a `System.Text.Json` string, byte-identical to `DataJson`, precisely because it is opaque.

### Negative

- The promotion rule is a real tax, and it is the reason the migration history is as long as it is.
- Nothing in the type system enforces that a promoted column and its blob field agree. ADR 0003 clause 7
  and the conformance suite's blob/projection-agreement case are the first mechanical check of it.

### Neutral

- `DataJson` is `text`, not `jsonb` — recorded at
  [`../design/production-readiness.md`](../design/production-readiness.md) §5.2, not here.

---

## What would invalidate this decision later

1. **A provider stores `TState` natively rather than as a JSON string.** ADR 0001 names this as a
   deferred follow-up and [`../design/mongodb-persistence.md`](../design/mongodb-persistence.md) Q12
   holds it open. Native BSON would make `GetDataJsonAsync` a fidelity-critical reconstruction rather
   than a field read, and would make the promotion rule partly unnecessary — enough of a shift to
   warrant its own ADR.
2. **The promoted set grows past what a single row should carry**, or a saga type needs its own indexes.
   Then per-type storage, or a satellite table, becomes worth its cost.
3. **`ISagaSummaryReader` stops being saga-type-agnostic.** That premise is the load-bearing argument
   here; if the dashboard ever requires saga definitions, the table-per-type objection largely
   evaporates.
