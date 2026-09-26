# vSaga

vSaga is an orchestration-first saga library with two first-class runtimes: a .NET 10 engine, built
directly on `RabbitMQ.Client` (no MassTransit/Wolverine dependency required — though adapters for both
exist if you're already standardized on one), and a TypeScript SDK for writing Node.js **participants**
that react to those sagas without running the engine itself. Both sides speak the same wire protocol,
so a Node participant and a .NET saga exchange messages with neither side aware of the other's
language.

The .NET engine gives you a fluent saga DSL for both orchestrated and choreographed sagas, a persisted
event log, EF Core (Postgres), Redis and in-memory persistence, six interchangeable `IMessageTransport`
adapters, a transport-agnostic `.CallHttp` step for calling plain REST APIs, an in-memory testing
harness, OpenTelemetry instrumentation, and a chaos-engineering fault-injection package. The TypeScript
SDK — seven `@vsaga/*` packages — gives Node participants the same dispatch/dedupe/reply-with-causation
semantics, a broker transport (`transport-rabbitmq`) and a brokerless HTTP transport (`transport-http`)
with Express/Fastify/NestJS hosting adapters, wire-compatible with their .NET counterparts. A
saga-type-agnostic ops dashboard (ASP.NET Core API + Angular SPA) gives both runtimes live updates, a
per-saga visual service map, and manual retry.

## Install

```bash
dotnet add package VSaga.Core
dotnet add package VSaga.Persistence.InMemory   # or VSaga.Persistence.EFCore + .EFCore.Postgres, or VSaga.Persistence.Redis
dotnet add package VSaga.Transport.InMemory     # or VSaga.Transport.RabbitMQ / .Wolverine / .MassTransit / .Brighter / .Http
```

```bash
npm install @vsaga/participant @vsaga/protocol @vsaga/transport-rabbitmq   # or @vsaga/transport-http + @vsaga/express / .fastify / .nestjs
```

> No tagged release exists yet, so these aren't published to nuget.org/npm as of this commit — see
> [`docs/getting-started.md`](docs/getting-started.md) for how to reference them locally in the
> meantime. The commands above are the shape usage will take once the first release ships.

## Quick start

### A first saga (.NET)

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

### A first participant (TypeScript)

```ts
import { createParticipant } from '@vsaga/participant';
import { message } from '@vsaga/protocol';
import { createRabbitMqTransport } from '@vsaga/transport-rabbitmq';

const ChargeCard = message<{ CorrelationId: string; OrderId: string; Amount: number }>('ChargeCard');
const CardCharged = message<{ CorrelationId: string; OrderId: string }>('CardCharged');

const transport = await createRabbitMqTransport({ connectionString: 'amqp://localhost' });
const payments = createParticipant({ serviceName: 'payments', queue: 'vsaga.participant.payments', transport });

payments.on(ChargeCard, async (body, ctx) => {
  // ... charge the card ...
  await ctx.reply(CardCharged, { CorrelationId: body.CorrelationId, OrderId: body.OrderId });
});

await payments.start();
```

A participant isn't a saga — it holds no persisted state and never talks to the orchestrator directly —
but it exchanges messages with one over the exact same wire format, message-type-name for
message-type-name, with no translation layer. Swap `@vsaga/transport-rabbitmq` for
`@vsaga/transport-http` (plus `@vsaga/express`, `@vsaga/fastify`, or `@vsaga/nestjs` to receive inbound
requests) to run the same participant over a broker-free HTTP topology instead. See
[`docs/typescript-participants.md`](docs/typescript-participants.md) for the full SDK reference.

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

Then serve the dashboard UI — a dev server, deliberately not part of `docker-compose.yml`:

```bash
cd typescript/dashboard-web && npm install && npx ng serve     # http://localhost:4200
```

> This command chains with `&&`, which Windows PowerShell 5.1 (`powershell.exe`) can't parse. Use
> PowerShell 7+ (`pwsh`) or Git Bash/WSL, or just run each command on its own line.

(`dashboard-web` has its own lockfile and toolchain — it is deliberately not part of the
`typescript/` npm workspace, so it needs its own `npm install`.)

| What | Where | Notes |
| --- | --- | --- |
| Dashboard UI | http://localhost:4200 | `ng serve`; must match `Dashboard__WebOrigin` |
| Dashboard API | http://localhost:5080 | API key `dev-local-only-change-me` — see [`docs/dashboard.md`](docs/dashboard.md#authentication) |
| RabbitMQ management | http://localhost:15672 | `guest` / `guest` |
| Postgres | `localhost:5433` | `postgres`/`postgres`, database `vsaga` (port 5433, not 5432, to avoid clashing with a local Postgres) |

The sample submits orders on a loop as soon as it starts, so the saga list fills on its own — nothing
to trigger by hand. Try the chaos overlay for fault injection
(`docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d --build`, see
[`docs/chaos.md`](docs/chaos.md)), or one of the other transport adapters via their own overlay — these
need a `-p <project-name>` flag and use different, remapped ports so they can run alongside the plain
stack; see ["Running an adapter's own overlay"](docs/transports/index.md#running-an-adapters-own-overlay)
for the exact commands and ports.

### See both runtimes at once

```bash
docker compose -f docker-compose.yml -f docker-compose.node.yml up -d --build
docker compose logs -f notification-participant
```

This overlay hands the sample's `NotificationService` to a Node process
([`typescript/samples/notification-participant`](typescript/samples/notification-participant)) — same
queue, same message types, same replies, different language. The .NET sagas are not reconfigured for
it and the dashboard still draws `NotificationService` as a named node on the Saga Map, which is the
whole point: nothing on either side has to know the other's runtime.

> **Postgres volume note:** `docker compose up` reuses the named volume across restarts — it is not
> reset for you. See [`docs/persistence.md`](docs/persistence.md#the-volume-caveat) if you're
> comparing before/after counts or your volume predates the EF Core migrations pass.

## Repository layout

```
dotnet/                  .NET 10 solution — engine, persistence, six transport adapters, dashboard API, samples
typescript/
  packages/               The TypeScript SDK: @vsaga/protocol, participant, transport-http,
                           transport-rabbitmq, express, fastify, nestjs
  samples/                A runnable Node participant — notification-participant swaps into the
                           OrderProcessing stack via docker-compose.node.yml
  dashboard-web/          Angular 21 SPA for the dashboard (its own toolchain — see
                           docs/typescript-participants.md)
docs/                     Reference documentation, design records, and project history — see below
docker-compose*.yml       The reference stack plus one overlay per transport adapter, one for chaos,
                           one for Redis persistence, and one that swaps in the Node participant
```

## Documentation

Full index: [`docs/README.md`](docs/README.md). Straight to the reference docs:

- [`docs/getting-started.md`](docs/getting-started.md) — install and your first saga, written out in
  full.
- [`docs/concepts.md`](docs/concepts.md) — orchestrated vs. choreographed, correlation (including
  business-key correlation), compensation, timeouts.
- [`docs/typescript-participants.md`](docs/typescript-participants.md) — the Node.js SDK: the seven
  `@vsaga/*` packages, wire compatibility with the .NET side, dispatch semantics, and the HTTP hosting
  adapters.
- [`docs/saga-dsl.md`](docs/saga-dsl.md) — the complete DSL method reference.
- [`docs/configuration.md`](docs/configuration.md) — every **.NET** options class, including the
  transactional outbox and transport options (the TypeScript SDK's options live in each package's own
  README instead).
- [`docs/persistence.md`](docs/persistence.md) — EF Core/Postgres, Redis, in-memory, migrations.
- [`docs/observability.md`](docs/observability.md) — traces, metrics, the persisted event log, OTLP
  wiring.
- [`docs/dashboard.md`](docs/dashboard.md) — API endpoints, authentication, the SPA, the Saga Map.
- [`docs/testing.md`](docs/testing.md) — `SagaTestHarness`.
- [`docs/chaos.md`](docs/chaos.md) — `VSaga.Chaos` fault injection.
- [`docs/transports/index.md`](docs/transports/index.md) — the transport contract and all six
  adapters (RabbitMQ, Wolverine, MassTransit, Brighter, HTTP, in-memory).
- [`docs/design/`](docs/design/) — design records for features as they were planned.
- [`docs/history/`](docs/history/) — the project's changelog, preserved by topic — live-verification
  traces, mutation-testing results, and bugs found and fixed along the way.

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for build/test commands and PR conventions, and
[`LICENSE`](LICENSE) (MIT).
