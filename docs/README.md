# vSaga documentation

Reference documentation for vSaga. Start with [`getting-started.md`](getting-started.md) if you're
new here; the rest of this index is organized by topic, roughly in the order you'd want them.

## Core reference

- [`concepts.md`](concepts.md) — orchestrated vs. choreographed sagas, saga identity and
  correlation (including business-key correlation), compensation, timeouts, fan-out/join, sub-saga
  composition.
- [`saga-dsl.md`](saga-dsl.md) — the full method inventory for the fluent DSL:
  `OrchestratedSagaDefinition`, `ChoreographedSagaDefinition`, `State`, `StateBuilder`, `EventBuilder`,
  `ChoreographyEventBuilder`, `TimeoutBuilder`, `RetryPolicy`, `ISagaContext`, and `.CallHttp`.
- [`configuration.md`](configuration.md) — every options class: `SagaOrchestratorOptions`, the
  outbox, each transport adapter, persistence (the `Persistence:Provider` switch, `VSagaRedisOptions`,
  `VSagaMongoOptions`, `ConnectionStrings:VSaga`), `.CallHttp`'s `HttpCallOptions`, chaos, the
  dashboard's keys (API key, CORS origin, trusted proxies) and the UI container's variables,
  OpenTelemetry wiring.
- [`persistence.md`](persistence.md) — EF Core/Postgres (migrations, the Postgres-volume caveat),
  MongoDB (the replica-set prerequisite, pinned write concern, the collections and indexes, supported
  servers), Redis (durability tiers, supported servers, the key space, the capacity model) and in-memory
  persistence. Each provider has a runnable sample in
  [`dotnet/samples/Persistence/`](../dotnet/samples/Persistence/).
- [`observability.md`](observability.md) — the persisted event log, OpenTelemetry traces/metrics,
  and the one-line OTLP exporter wiring.
- [`dashboard.md`](dashboard.md) — API endpoints, API-key authentication, live updates over SignalR,
  the Angular SPA and how it is served (an nginx container in the compose stack, on the API's own
  origin, and what to keep behind your own proxy or TLS), and the Saga Map.
- [`testing.md`](testing.md) — `SagaTestHarness`, for unit-testing saga definitions against the real
  engine with no broker/database.
- [`chaos.md`](chaos.md) — `VSaga.Chaos`'s fault-injection middleware (delay/drop/duplicate).

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
  own **Status** line at the top. Two of them plan work that was never built: the release-automation half of
  `production-readiness.md` §3, and `sub-saga-composition.md`'s Slice 3, closed by a recorded decision
  rather than left open. The three persistence plans are all implemented. `production-readiness.md`
  also plans an npm/TypeScript-SDK half; that SDK was removed on 2026-09-27, and a status note at the
  top of the plan says which parts no longer apply.
  - [`design/http-based-sagas.md`](design/http-based-sagas.md)
  - [`design/mixed-sagas.md`](design/mixed-sagas.md)
  - [`design/sub-saga-composition.md`](design/sub-saga-composition.md)
  - [`design/production-readiness.md`](design/production-readiness.md)
  - [`design/persistence-contracts.md`](design/persistence-contracts.md) — **implemented,
    2026-09-26.** The contract clauses, cross-provider conformance suite, and divergence fixes both
    provider plans below depend on; all 21 commits landed. Stands alone; needs neither of them.
  - [`design/mongodb-persistence.md`](design/mongodb-persistence.md) — **implemented, 2026-09-26.**
    `VSaga.Persistence.MongoDB` is built on the native driver and live-verified against a replica set,
    including a killed saga host mid-step; the plan records how each blocking question was decided and
    where the build deviated from it.
  - [`design/redis-persistence.md`](design/redis-persistence.md) — **implemented, 2026-09-26.**
    `VSaga.Persistence.Redis` is built and live-verified; the plan records where the build deviated from
    it. It also authored the two seams it shared with the MongoDB plan (the `Persistence:Provider`
    switch and the provider-neutral `persistence` health check), which that plan now consumes.
  - [`design/dashboard-usability-and-access.md`](design/dashboard-usability-and-access.md) —
    **accepted, not yet implemented, 2026-10-02.** The dashboard UI in compose, a labelled timeline
    with a jump to the map, per-step state snapshots, a retry that re-runs only the failed step, sign-in
    with role and saga-type scoped access, and a guide mode with a user guide. ADRs 0006, 0007 and 0008
    record its three decisions.

- [`adr/`](adr/) — architecture decision records: one decision per file, numbered, stating the context,
  the options weighed, and the consequences accepted. Newer and narrower than `design/`, which holds
  long-form plans; an ADR links to its plan rather than repeating it.
  - [`adr/0001-mongodb-persistence-provider.md`](adr/0001-mongodb-persistence-provider.md) — **Accepted**
    and **implemented** 2026-09-26.
  - [`adr/0002-redis-persistence-provider.md`](adr/0002-redis-persistence-provider.md) — **Accepted**
    and **implemented** 2026-09-26.
  - [`adr/0003-persistence-contract-clauses.md`](adr/0003-persistence-contract-clauses.md) —
    **Accepted** 2026-09-25 and **implemented** 2026-09-26.
  - [`adr/0004-postgres-only-atomic-claim.md`](adr/0004-postgres-only-atomic-claim.md) — **Accepted**,
    retroactive: records shipped behaviour, including the claim fallback every non-Postgres EF Core
    provider silently takes, safe for one dispatcher instance only. `persistence.md` now states it; the
    startup warning the ADR requires has not been added.
  - [`adr/0005-saga-state-storage-model.md`](adr/0005-saga-state-storage-model.md) — **Accepted**,
    retroactive: one shared table, one opaque state blob, and the promotion rule.
  - [`adr/0006-dashboard-authentication-and-identity-store.md`](adr/0006-dashboard-authentication-and-identity-store.md)
    — **Accepted** 2026-10-02, not yet implemented: session-cookie sign-in, role and saga-type scoped
    access, and a dashboard-owned SQLite identity store, replacing the shared API key as the way people
    sign in.
  - [`adr/0007-state-snapshots-in-the-event-log.md`](adr/0007-state-snapshots-in-the-event-log.md) —
    **Accepted** 2026-10-02, not yet implemented: per-step saga state recorded as `StatePersisted`
    entries in the existing event log, with a per-snapshot cap and a per-saga budget.
  - [`adr/0008-dashboard-retry-reruns-the-failed-step.md`](adr/0008-dashboard-retry-reruns-the-failed-step.md)
    — **Accepted** 2026-10-02, not yet implemented: a dashboard retry resets the saga to the state
    before the failed step and republishes that step's message, targeted at the retried saga type.

## History

- [`history/`](history/) — one file per topic: the changelog narrative this project's README used to
  carry directly, preserved verbatim and headed with the commit(s) it describes, plus five records
  written fresh after the docs restructure (the CI flakes, the third field test, the requirements
  audit, and the Redis and MongoDB providers). Read these for *how* a feature was built and verified —
  live-verification traces, mutation-testing results, bugs found and fixed along the way — content
  that matters for provenance but would clutter a reference doc meant to describe the feature as it
  stands today. Because they are kept unedited, some still describe the TypeScript participant SDK
  (removed 2026-09-27) and the dashboard's old `typescript/dashboard-web` path (now `dashboard-web/`).

## Project meta

- [`../README.md`](../README.md) — what vSaga is, install, a first saga, running the demo.
- [`../CONTRIBUTING.md`](../CONTRIBUTING.md) — build/test commands and PR conventions.
