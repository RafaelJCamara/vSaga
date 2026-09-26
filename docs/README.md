# vSaga documentation

Reference documentation for vSaga. Start with [`getting-started.md`](getting-started.md) if you're
new here; the rest of this index is organized by topic, roughly in the order you'd want them.

## Core reference

- [`concepts.md`](concepts.md) — orchestrated vs. choreographed sagas, saga identity and
  correlation (including business-key correlation), compensation, timeouts, fan-out/join, sub-saga
  composition.
- [`saga-dsl.md`](saga-dsl.md) — the full method inventory for the fluent DSL:
  `OrchestratedSagaDefinition`, `ChoreographedSagaDefinition`, `StateBuilder`, `EventBuilder`,
  `ChoreographyEventBuilder`, `TimeoutBuilder`, `RetryPolicy`, `ISagaContext`, and `.CallHttp`.
- [`configuration.md`](configuration.md) — every **.NET** options class: `SagaOrchestratorOptions`, the
  outbox, each transport adapter, chaos, dashboard auth, OpenTelemetry wiring (the TypeScript SDK's
  options live in each package's own README instead, cross-linked from there).
- [`persistence.md`](persistence.md) — EF Core/Postgres (migrations, the Postgres-volume caveat)
  and in-memory persistence.
- [`observability.md`](observability.md) — the persisted event log, OpenTelemetry traces/metrics,
  and the one-line OTLP exporter wiring.
- [`dashboard.md`](dashboard.md) — API endpoints, API-key authentication, the Angular SPA, and the
  Saga Map.
- [`testing.md`](testing.md) — `SagaTestHarness`, for unit-testing saga definitions against the real
  engine with no broker/database.
- [`chaos.md`](chaos.md) — `VSaga.Chaos`'s fault-injection middleware (delay/drop/duplicate).
- [`typescript-participants.md`](typescript-participants.md) — the Node.js SDK for writing
  cross-runtime participants (`@vsaga/protocol`, `@vsaga/participant`, `@vsaga/transport-*`, hosting
  adapters).

## Transports

- [`transports/index.md`](transports/index.md) — the `IMessageTransport` contract and how to choose
  an adapter.
- [`transports/rabbitmq.md`](transports/rabbitmq.md) — the reference adapter, built on `RabbitMQ.Client`.
- [`transports/wolverine.md`](transports/wolverine.md) — built on WolverineFx.RabbitMQ.
- [`transports/masstransit.md`](transports/masstransit.md) — built on MassTransit 8.x.
- [`transports/brighter.md`](transports/brighter.md) — built on Paramore.Brighter's RabbitMQ gateway.
- [`transports/http.md`](transports/http.md) — no broker at all; plain HTTP request/response.
- [`transports/in-memory.md`](transports/in-memory.md) — single-process, dev/test only.

## Design records

- [`design/`](design/) — design documents for features as they were planned. Read these for the
  *reasoning* behind a decision; read the reference docs above for the shipped shape. Each carries its
  own **Status** line at the top, and four of them describe work that does not exist yet: the
  release-automation half of `production-readiness.md` §3 was never built, `persistence-contracts.md` is
  accepted but unbuilt, and both persistence-provider plans are still proposals.
  - [`design/http-based-sagas.md`](design/http-based-sagas.md)
  - [`design/mixed-sagas.md`](design/mixed-sagas.md)
  - [`design/sub-saga-composition.md`](design/sub-saga-composition.md)
  - [`design/production-readiness.md`](design/production-readiness.md)
  - [`design/persistence-contracts.md`](design/persistence-contracts.md) — **implemented,
    2026-09-26.** The contract clauses, cross-provider conformance suite, and divergence fixes both
    provider plans below depend on; all 21 commits landed. Stands alone; needs neither of them.
  - [`design/mongodb-persistence.md`](design/mongodb-persistence.md) — **accepted, nothing built.** Its
    Stage 0 prerequisite is done; the next decisions are the plan's Q2–Q4.
  - [`design/redis-persistence.md`](design/redis-persistence.md) — **accepted, nothing built.** Shares
    three seams with the MongoDB plan; neither depends on the other landing first. Its fault-injection
    tier and its blocking questions are the next decisions.

- [`adr/`](adr/) — architecture decision records: one decision per file, numbered, stating the context,
  the options weighed, and the consequences accepted. Newer and narrower than `design/`, which holds
  long-form plans; an ADR links to its plan rather than repeating it.
  - [`adr/0001-mongodb-persistence-provider.md`](adr/0001-mongodb-persistence-provider.md) — **Accepted**
    2026-09-26, not built.
  - [`adr/0002-redis-persistence-provider.md`](adr/0002-redis-persistence-provider.md) — **Accepted**
    2026-09-26, not built.
  - [`adr/0003-persistence-contract-clauses.md`](adr/0003-persistence-contract-clauses.md) —
    **Accepted** and **implemented** 2026-09-26.
  - [`adr/0004-postgres-only-atomic-claim.md`](adr/0004-postgres-only-atomic-claim.md) — **Accepted**,
    retroactive: records shipped behaviour, including a live documentation hazard for non-Postgres
    providers.
  - [`adr/0005-saga-state-storage-model.md`](adr/0005-saga-state-storage-model.md) — **Accepted**,
    retroactive: one shared table, one opaque state blob, and the promotion rule.

## History

- [`history/`](history/) — the changelog narrative this project's README used to carry directly,
  preserved verbatim, one file per topic, each headed with the commit(s) it describes. Read these for
  *how* a feature was built and verified — live-verification traces, mutation-testing results, bugs
  found and fixed along the way — content that matters for provenance but would clutter a reference
  doc meant to describe the feature as it stands today.

## Project meta

- [`../README.md`](../README.md) — what vSaga is, install, a first saga, running the demo.
- [`../CONTRIBUTING.md`](../CONTRIBUTING.md) — build/test commands and PR conventions.
