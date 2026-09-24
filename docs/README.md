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

- [`design/`](design/) — design documents for features as they were planned, all shipped but one: the
  release-automation half of `production-readiness.md` §3 was never built, and that file's own status
  lines say so. Read these for the *reasoning* behind a decision; read the reference docs above for the
  shipped shape.
  - [`design/http-based-sagas.md`](design/http-based-sagas.md)
  - [`design/mixed-sagas.md`](design/mixed-sagas.md)
  - [`design/sub-saga-composition.md`](design/sub-saga-composition.md)
  - [`design/production-readiness.md`](design/production-readiness.md)

## History

- [`history/`](history/) — the changelog narrative this project's README used to carry directly,
  preserved verbatim, one file per topic, each headed with the commit(s) it describes. Read these for
  *how* a feature was built and verified — live-verification traces, mutation-testing results, bugs
  found and fixed along the way — content that matters for provenance but would clutter a reference
  doc meant to describe the feature as it stands today.

## Project meta

- [`../README.md`](../README.md) — what vSaga is, install, a first saga, running the demo.
- [`../CONTRIBUTING.md`](../CONTRIBUTING.md) — build/test commands and PR conventions.
