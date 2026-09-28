# History: project origins and the production-hardening pass

> Preserved verbatim from the original `README.md` (the repo's only README until the
> production-readiness docs restructure, §8.19). Describes the state of the project as of commit
> `a2692f0` ("Harden BugsMQ for production: auth, health checks, migrations, and code quality"),
> which builds on the two commits before it, `a17f31d` ("Add BugsMQ v1: saga engine, dashboard, and
> OrderProcessing sample") and `b1ff30b` ("Add CI workflow and expand test coverage"). The repo was
> renamed BugsMQ → vSaga in `93af87f`, after this pass; names below are as they were written at the
> time. The "Repository layout" tree below already reflects the `dotnet/`/`typescript/` top-level split
> (`b081445`, which rewrote its paths in place) but predates the TypeScript SDK packages
> (`typescript/packages/*`, added across several later commits) — see
> [the root README](../../README.md) for the current tree. Since
> then (2026-09-27) the TypeScript participant SDK (`typescript/packages/*`) and its docs have been
> removed and the dashboard moved from `typescript/dashboard-web/` to a top-level `dashboard-web/`, so
> `typescript/` no longer exists.

---

## Repository layout

The repo is split by ecosystem: everything .NET lives under `dotnet/`, everything Node/TypeScript
under `typescript/`. Shared assets (docs, compose files, CI) stay at the root.

```
dotnet/                           .NET 10 solution: VSaga.slnx, Directory.*.props, global.json, .editorconfig
  src/
    VSaga.Abstractions             Contracts only: saga/transport/persistence interfaces, no implementation deps
    VSaga.Chaos                    Opt-in fault-injection transport middleware (delay/drop/duplicate)
    VSaga.Core                     The saga engine: fluent DSL + orchestrator runtime
    VSaga.Observability            OpenTelemetry hosting extensions
    VSaga.Persistence.EFCore       EF Core store implementations (provider-agnostic)
    VSaga.Persistence.EFCore.Postgres   Postgres-specific EF Core migrations (see "EF Core migrations" below)
    VSaga.Persistence.InMemory     In-memory store implementations (dev/test)
    VSaga.Testing                  SagaTestHarness for unit-testing saga definitions
    VSaga.Transport.Common         Shared IMessageTransport decorator (MiddlewarePipelineTransport)
    VSaga.Transport.Http           IMessageTransport over plain HTTP, no broker (docs/design/http-based-sagas.md)
    VSaga.Transport.InMemory       In-memory IMessageTransport (dev/test)
    VSaga.Transport.RabbitMQ       Real IMessageTransport over RabbitMQ.Client
    VSaga.Transport.Wolverine      IMessageTransport over WolverineFx.RabbitMQ
    VSaga.Transport.MassTransit    IMessageTransport over MassTransit 8.x + RabbitMQ
    VSaga.Transport.Brighter       IMessageTransport over Paramore.Brighter's RabbitMQ gateway
    VSaga.Dashboard.Api            ASP.NET Core API + SignalR hub for the ops dashboard
  samples/
    VSaga.Samples.OrderProcessing(.Contracts)   End-to-end reference saga + participants
  tests/                           One test project per major dotnet/src/ project
  tools/BackfillStrandedTimeouts   One-off maintenance tool
typescript/                       Node 22+ / TypeScript
  dashboard-web/                   Angular 21 SPA for the dashboard (list/detail, live updates)
docs/                             Design sketches + shipped-feature design records
docker-compose.yml                Postgres + RabbitMQ + dashboard-api + the OrderProcessing sample
```

## Current state

Two commits exist before this pass (`a17f31d` "Add vSaga v1", `b1ff30b` "Add CI workflow and
expand test coverage"), plus an in-progress, previously-uncommitted code-quality pass (analyzers:
SonarAnalyzer.CSharp, Meziantou.Analyzer, AsyncFixer; `TreatWarningsAsErrors=true`; an expanded
`.editorconfig`) that this commit finishes and verifies alongside the production-hardening work
described below.

**What's implemented and tested:** orchestrated sagas (linear state machines with compensation,
step-level retry policies, per-state timeouts, manual whole-saga retry), choreographed sagas (see
"Choreographed saga support" below), EF Core/Postgres and in-memory persistence, four
`IMessageTransport` adapters — RabbitMQ (the reference implementation), Wolverine, MassTransit, and
Brighter, all over RabbitMQ-family brokers — plus the in-memory transport, the dashboard API/SPA, and
the `VSaga.Testing` harness. The OrderProcessing sample runs both saga kinds side by side against real
Postgres/RabbitMQ via `docker-compose.yml` — `OrderSaga` (orchestrated: compensation, timeouts) and
`PostShipmentChoreography` (choreographed: an independent fan-out tracked under the same correlation
id) — see "Choreography in the OrderProcessing sample" below. Its transport is config-switchable
(`Transport:Provider`, see `dotnet/samples/VSaga.Samples.OrderProcessing/Program.cs`) to run against any of
the four adapters via a dedicated docker-compose overlay per adapter (`docker-compose.wolverine.yml`,
`.masstransit.yml`, `.brighter.yml` — RabbitMQ needs none, it's the default).

**The original v1 roadmap note is now fully closed.** All four items it deferred are built: additional
transport adapters (see the four "Transport adapter: ..." sections below — MassTransit and Wolverine
were the two the note named, Brighter shipped alongside them), parallel/fan-out saga steps ("Parallel
fan-out and join" below), and sub-saga composition: a parent can start a child, the relationship is
persisted and queryable ("Sub-saga composition: parent linkage" below), a child can report back to a
parent that actually waits for it ("Sub-saga composition: completion notification" below), and the
engine publishes a safety net for a child that fails or times out before it ever reaches its own
report-back step ("Sub-saga composition: engine safety net" below). The SignalR hub and polling
service, listed here as an untested gap through several passes, are covered as of "SignalR hub and
polling service tests". Nothing from that original note remains deliberately out of scope.

**Proposed work.** Every section of this README describes something that exists. Designs for work that
does *not* exist yet live under `docs/` instead, so the two are never confused.

[`docs/design/http-based-sagas.md`](../design/http-based-sagas.md) covers HTTP-based sagas, in two independent
halves — an `IMessageTransport` adapter that moves messages between vSaga services over HTTP with no
broker at all, and a transport-agnostic `.CallHttp(...)` step that lets any saga call a plain REST API
and map its response into a saga message. Both halves are now built and live-verified; see "Transport
adapter: HTTP" and "Outbound REST calls from a saga step: `.CallHttp`" below. Its §3 is the part to read
first: three constraints found by tracing the engine, two of which killed an earlier draft of the design
outright — including a third instance of this repo's recurring "header the orchestrator never actually
reads back" scar, which here would cause infinite redelivery rather than a merely wrong dashboard. Live
verification of the first half found a fourth instance of that same scar-class: a cross-process deadlock
no unit test caught.

[`docs/design/mixed-sagas.md`](../design/mixed-sagas.md) covers mixed sagas — one saga that both publishes/sends
RabbitMQ messages and makes outbound REST calls via `.CallHttp`/`ctx.CallHttpAsync`, with compensation
that unwinds both kinds of hop. Built and live-verified; see "Mixed sagas: RabbitMQ messages and REST
calls in one saga" below. Its own adversarial second pass found a fifth instance of the scar above in a
different shape: a compensating REST call's loopback reply can resurrect an already-terminated saga
unless the state it flows into is designed not to finalize until that reply actually arrives — live-
verified directly, not only designed around.

[`docs/design/sub-saga-composition.md`](../design/sub-saga-composition.md) covered the whole of sub-saga
composition and has no open work left: its last piece — whether a parent's compensation cascades into
its children (Slice 3) — is closed rather than proposed, considered and deliberately not built per the
doc's own recommendation. See "Sub-saga composition: parent linkage" below for where that's documented
as shipped (non-)behaviour, and the doc itself for the full reasoning, including one claim it made that
turned out to be wrong, one about `UnhandledEventPolicy.Throw` that also turned out to be wrong, and two
race conditions it didn't anticipate at all.

Chaos-engineering transport middleware, formerly listed here as a proposal, is implemented; see
"Chaos-engineering transport middleware" below. Additional transport adapters — the last genuinely open
item from the original v1 roadmap note — are also implemented; see the four "Transport adapter: ..."
sections below.

## Production-hardening pass (this commit)

A full-codebase gap analysis found several v1 gaps explicitly flagged in code comments as "out of
scope for v1" or simply absent. The following six were scoped in and are covered by both unit
tests and a live `docker-compose up` verification (real sagas processed end-to-end, migrations
applied cleanly, auth and health checks exercised over HTTP):

- **Dashboard API authentication** — a shared API key (`Dashboard:ApiKey` config), chosen over
  JWT/OIDC or basic auth as the right fit for a v1 internal ops dashboard with no existing identity
  infrastructure. `dotnet/src/VSaga.Dashboard.Api/Auth/ApiKeyAuthenticationHandler.cs` checks the
  `X-Api-Key` header, then an `Authorization: Bearer` header, then falls back to the
  `?access_token=` query string. The SignalR JS client sends its `accessTokenFactory` token as an
  `Authorization: Bearer` header on the negotiate HTTP call, and only as the `?access_token=` query
  string on the actual WebSocket/SSE upgrade (which can't carry custom headers) — the handler needs
  all three to authenticate both legs of a hub connection. It **fails closed**: an unconfigured key
  denies every request rather than silently disabling auth. `/health` stays unauthenticated, per
  infra-probe convention. The Angular client sends the key via an `HttpInterceptorFn`
  (`typescript/dashboard-web/src/app/interceptors/api-key.interceptor.ts`) and the hub connection's
  `accessTokenFactory`. **Known limitation, accepted as part of this choice:** a key embedded in a
  compiled SPA bundle is visible via devtools — this closes off unauthenticated direct API access,
  it is not per-user auth.

- **Real `/health` check** — replaced the hardcoded `{ "status": "healthy" }` response with actual
  Postgres/RabbitMQ connectivity checks (`dotnet/src/VSaga.Dashboard.Api/HealthChecks/`), returning `503`
  with a per-check breakdown when either is unreachable. Both checks resolve their dependency
  lazily via `IServiceScopeFactory` rather than a direct constructor dependency, so they degrade to
  "not configured" instead of failing to construct when a test host swaps out the real providers.

- **EF Core migrations** — replaced `EnsureCreatedAsync()` with versioned migrations
  (`db.Database.MigrateAsync()`). The Postgres-specific migration code lives in a new project,
  `VSaga.Persistence.EFCore.Postgres`, kept separate from `VSaga.Persistence.EFCore` so the
  latter stays provider-agnostic (its own stated design goal). Discovered and fixed during
  verification: the OrderProcessing sample independently calls its own `EnsureCreatedAsync()` at
  startup, which raced against the dashboard API's new migration on a fresh database. Fixed at the
  `docker-compose.yml` level — the dashboard API now has a real Docker `HEALTHCHECK` (using the
  `/health` endpoint above), and `order-processing` waits for it via `condition: service_healthy`
  before starting, so the migration always completes first.

- **RabbitMQ publisher confirms** — publishes now enable `PublisherConfirmationsEnabled`/
  `PublisherConfirmationTrackingEnabled` and `mandatory: true`, so a broker-side nack or an
  unroutable message throws `MessageTransportPublishException` instead of vanishing silently. This is
  not a RabbitMQ-only property of this codebase: the HTTP transport (see "Transport adapter: HTTP"
  below) detects it too, at higher fidelity than the Wolverine and Brighter adapters, whose own tests
  assert the verified *absence* of an unroutable signal in those packages.

- **Concurrency-safe timeout claiming** — `EfCoreSagaTimeoutStore.ClaimDueAsync` now uses an
  atomic `UPDATE ... WHERE ... FOR UPDATE SKIP LOCKED ... RETURNING` on Postgres (verified under
  real concurrent load in `PostgresEfCoreStoreTests`), replacing the previous plain
  select-then-update that could double-claim a timeout across multiple dispatcher instances. Other
  providers (SQLite in tests) keep the original approach as a fallback.

- **Bounded orchestrator redelivery** — `SagaOrchestrator.HandleAsync`'s catch-all used to
  unconditionally requeue forever on any infrastructure-level failure (a deserialize error, a
  persistence-store exception — distinct from a saga step's own thrown exception, which was
  already handled correctly). It now redelivers with an incremented attempt-count header up to
  `SagaOrchestratorOptions.MaxDeliveryAttempts` (default 5), then routes to the dead-letter queue
  RabbitMQ already builds per consumer, logging a `SagaEntryType.DeliveryExhausted` timeline entry
  first.

Two incidental Dockerfile fixes surfaced only by building fresh (not from local caches), unrelated
to the above but necessary for the stack to build at all once `TreatWarningsAsErrors=true` took
effect: neither Dockerfile copied `.editorconfig` into the build context, so the analyzer rules it
relaxes (e.g. `MA0048`, deliberately disabled for this repo's intentionally-multi-type files) fell
back to their error-level defaults on a clean build.
