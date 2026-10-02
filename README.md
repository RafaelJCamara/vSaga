# vSaga

vSaga is an orchestration-first saga library for .NET 10, built directly on `RabbitMQ.Client` (no
MassTransit/Wolverine dependency required — though adapters for both exist if you're already
standardized on one).

The engine gives you a fluent saga DSL for both orchestrated and choreographed sagas, a persisted
event log, EF Core (Postgres), MongoDB, Redis and in-memory persistence, six interchangeable `IMessageTransport`
adapters, a transport-agnostic `.CallHttp` step for calling plain REST APIs, an in-memory testing
harness, OpenTelemetry instrumentation, and a chaos-engineering fault-injection package. A
saga-type-agnostic ops dashboard (ASP.NET Core API + Angular SPA) adds live updates, a per-saga visual
service map, and manual retry.

## Install

```bash
dotnet add package VSaga.Core
dotnet add package VSaga.Persistence.InMemory   # or VSaga.Persistence.EFCore + .EFCore.Postgres, VSaga.Persistence.MongoDB, or VSaga.Persistence.Redis
dotnet add package VSaga.Transport.InMemory     # or VSaga.Transport.RabbitMQ / .Wolverine / .MassTransit / .Brighter / .Http
```

Optional add-ons: `VSaga.Http` (the `.CallHttp` step — see
[`docs/saga-dsl.md`](docs/saga-dsl.md#callhttp-from-vsagahttp)), `VSaga.Observability`
(`AddVSagaOpenTelemetry`), `VSaga.Chaos` (fault injection) and `VSaga.Testing` (`SagaTestHarness`).
Authors of a third-party persistence provider can run the `VSaga.Persistence.Conformance` suite
against it — see [`docs/persistence.md`](docs/persistence.md).

> No tagged release exists yet, so these aren't published to nuget.org as of this commit — see
> [`docs/getting-started.md`](docs/getting-started.md) for how to reference them locally in the
> meantime. The commands above are the shape usage will take once the first release ships.

## Quick start

### A first saga

```csharp
public sealed class OrderApprovalSaga : OrchestratedSagaDefinition<OrderApprovalState>
{
    public State<OrderApprovalState> AwaitingApproval { get; }
    public State<OrderApprovalState> Approved { get; }

    public OrderApprovalSaga()
    {
        AwaitingApproval = InitialState(nameof(AwaitingApproval));
        Approved = State(nameof(Approved));

        During(AwaitingApproval)
            .When<SubmitOrder>()
                .Then((ctx, msg) => ctx.Saga.Amount = msg.Amount)
                .Publish((ctx, msg) => new OrderApproved(msg.OrderId))
                .TransitionTo(Approved)
                .Finalize(SagaStatus.Completed);
    }
}
```

That's the whole shape: declare states, gate steps on `During(state).When<TMessage>()`, run your logic
in `.Then(...)`, publish onward, transition, finalize. See
[`docs/getting-started.md`](docs/getting-started.md) for the complete, runnable version (messages,
state class, host wiring, and a test) and [`docs/saga-dsl.md`](docs/saga-dsl.md) for the full DSL
reference, including compensation, timeouts, fan-out/join, sub-saga composition, and choreographed
sagas.

## Run the demo

The full reference stack — Postgres, RabbitMQ, the dashboard API, and a continuously-submitting
`OrderProcessing` sample exercising orchestration, choreography, sub-sagas, parallel fan-out, and
`.CallHttp` all at once:

```bash
docker compose up -d --build     # Postgres + RabbitMQ + dashboard API + OrderProcessing sample
curl http://localhost:5080/health
curl -H "X-Api-Key: dev-local-only-change-me" http://localhost:5080/api/sagas
```

> In Windows PowerShell (not PowerShell 7+), `curl` is aliased to `Invoke-WebRequest`, which rejects
> `-H`. Call `curl.exe` explicitly (Windows 10+ ships a real curl) or use PowerShell 7+/Git Bash instead.

To run the same stack on MongoDB or Redis persistence instead of Postgres, layer that provider's overlay
(its dashboard API listens on `localhost:5580` and `localhost:5680` respectively; see
[`docs/persistence.md`](docs/persistence.md) for what each one requires or trades away):

```bash
docker compose -p vsaga-mongo -f docker-compose.yml -f docker-compose.mongo.yml up -d --build
docker compose -p vsaga-redis -f docker-compose.yml -f docker-compose.redis.yml up -d --build
```

The dashboard UI below proxies to the plain stack's API on `5080` by default. To browse an overlay's
sagas instead, start it with `VSAGA_API_URL` set to that overlay's API, e.g.
`VSAGA_API_URL=http://localhost:5580 npx ng serve` — see
[`dashboard-web/README.md`](dashboard-web/README.md#run-it), and
["Running an adapter's own overlay"](docs/transports/index.md#running-an-adapters-own-overlay) for each
overlay's ports.

Then serve the dashboard UI — a dev server, deliberately not part of `docker-compose.yml`:

```bash
cd dashboard-web && npm install && npx ng serve     # http://localhost:4201
```

> This command chains with `&&`, which Windows PowerShell 5.1 (`powershell.exe`) can't parse. Use
> PowerShell 7+ (`pwsh`) or Git Bash/WSL, or just run each command on its own line.

| What | Where | Notes |
| --- | --- | --- |
| Dashboard UI | http://localhost:4201 | `ng serve`; proxies `/api` and `/hubs` to the API (`VSAGA_API_URL`, default `http://localhost:5080`) |
| Dashboard API | http://localhost:5080 | API key `dev-local-only-change-me` — see [`docs/dashboard.md`](docs/dashboard.md#authentication) |
| RabbitMQ management | http://localhost:15672 | `guest` / `guest` |
| RabbitMQ (AMQP) | `localhost:5672` | `guest` / `guest`, i.e. `amqp://guest:guest@localhost:5672/` |
| Postgres | `localhost:5433` | `postgres`/`postgres`, database `vsaga` (port 5433, not 5432, to avoid clashing with a local Postgres) |
| MongoDB | `localhost:27018` | MongoDB overlay only. No auth, database `vsaga`, replica set `rs0`. Connect with `mongodb://localhost:27018/?directConnection=true`: the member advertises itself as `mongo:27017`, which only the compose network resolves |
| Redis | `localhost:6479` | Redis overlay only. No auth, namespace `vsaga`, so every key starts with `{vsaga:vsaga}:` |

The MongoDB and Redis overlays also move the plain stack's services to their own ports, so they can
run alongside it:

| Service | MongoDB overlay (`-p vsaga-mongo`) | Redis overlay (`-p vsaga-redis`) |
| --- | --- | --- |
| Dashboard API | `localhost:5580` | `localhost:5680` |
| RabbitMQ (AMQP / management) | `localhost:6172` / `16172` | `localhost:6272` / `16272` |
| Postgres (running, unused by that overlay) | `localhost:5448` | `localhost:5447` |

The sample submits orders on a loop as soon as it starts, so the saga list fills on its own — nothing
to trigger by hand. Try the chaos overlay for fault injection
(`docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d --build`, see
[`docs/chaos.md`](docs/chaos.md)), or one of the other transport adapters via their own overlay — these
need a `-p <project-name>` flag and use different, remapped ports so they can run alongside the plain
stack; see ["Running an adapter's own overlay"](docs/transports/index.md#running-an-adapters-own-overlay)
for the exact commands and ports.

> **Postgres volume note:** `docker compose up` reuses the named volume across restarts — it is not
> reset for you. See [`docs/persistence.md`](docs/persistence.md#the-volume-caveat) if you're
> comparing before/after counts or your volume predates the EF Core migrations pass.

## Persistence samples

To see one persistence provider on its own rather than the whole stack,
[`dotnet/samples/Persistence/`](dotnet/samples/Persistence/) has one small console sample per provider.
All four run the same saga over the in-memory transport, so no broker is needed, and the provider is the
only thing that differs between them:

| Provider | Sample | Database |
| --- | --- | --- |
| In-memory | [`VSaga.Samples.Persistence.InMemory`](dotnet/samples/Persistence/VSaga.Samples.Persistence.InMemory/) | none |
| EF Core / Postgres | [`VSaga.Samples.Persistence.EFCore.Postgres`](dotnet/samples/Persistence/VSaga.Samples.Persistence.EFCore.Postgres/) | Postgres 16 on `localhost:5434` |
| MongoDB | [`VSaga.Samples.Persistence.MongoDB`](dotnet/samples/Persistence/VSaga.Samples.Persistence.MongoDB/) | MongoDB 8 replica set on `localhost:27019` |
| Redis | [`VSaga.Samples.Persistence.Redis`](dotnet/samples/Persistence/VSaga.Samples.Persistence.Redis/) | Redis 7.4 on `localhost:6380` |

```bash
dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.InMemory

# The durable ones start their database from the sample's own compose file first, e.g.:
docker compose -f dotnet/samples/Persistence/VSaga.Samples.Persistence.MongoDB/docker-compose.yml up -d --wait
dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.MongoDB
```

## Repository layout

```
dotnet/                  .NET 10 solution — engine, persistence, six transport adapters, dashboard API,
                           samples (the OrderProcessing reference stack, and one per persistence provider)
dashboard-web/            Angular 21 SPA for the dashboard (built with npm and the Angular CLI, not the
                           .NET solution — see dashboard-web/README.md)
docs/                     Reference documentation, design records, and project history — see below
docker-compose*.yml       The reference stack (RabbitMQ transport, Postgres persistence) plus one
                           overlay each for the Wolverine, MassTransit, Brighter and HTTP adapters,
                           one for chaos, and one each for MongoDB and Redis persistence
```

## Documentation

Full index: [`docs/README.md`](docs/README.md). Straight to the reference docs:

- [`docs/getting-started.md`](docs/getting-started.md) — install and your first saga, written out in
  full.
- [`docs/concepts.md`](docs/concepts.md) — orchestrated vs. choreographed, correlation (including
  business-key correlation), compensation, timeouts.
- [`docs/saga-dsl.md`](docs/saga-dsl.md) — the complete DSL method reference.
- [`docs/configuration.md`](docs/configuration.md) — every options class, including the
  transactional outbox and transport options.
- [`docs/persistence.md`](docs/persistence.md) — EF Core/Postgres, MongoDB, Redis, in-memory, migrations.
- [`docs/observability.md`](docs/observability.md) — traces, metrics, the persisted event log, OTLP
  wiring.
- [`docs/dashboard.md`](docs/dashboard.md) — API endpoints, authentication, live updates over SignalR, the
  SPA, the Saga Map.
- [`docs/testing.md`](docs/testing.md) — `SagaTestHarness`.
- [`docs/chaos.md`](docs/chaos.md) — `VSaga.Chaos` fault injection.
- [`docs/transports/index.md`](docs/transports/index.md) — the transport contract and all six
  adapters (RabbitMQ, Wolverine, MassTransit, Brighter, HTTP, in-memory).
- [`docs/design/`](docs/design/) — design records for features as they were planned.
- [`docs/adr/`](docs/adr/) — architecture decision records, one numbered decision per file.
- [`docs/history/`](docs/history/) — the project's changelog, preserved by topic — live-verification
  traces, mutation-testing results, and bugs found and fixed along the way.

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for build/test commands and PR conventions, and
[`LICENSE`](LICENSE) (MIT).
