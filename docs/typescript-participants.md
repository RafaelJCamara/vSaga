# TypeScript participants

vSaga ships a TypeScript SDK for writing Node.js **participants** — services that react to saga
messages without running the saga engine itself — under `typescript/packages/`. It has been
undocumented outside individual package READMEs since it was first built; this page is its first
mention from the project's own doc index.

Participants are not sagas: they hold no persisted state, run no timeouts, and never talk to
`ISagaSnapshotStore`/`SagaOrchestrator` directly — exactly like this repo's own .NET sample
participants, which reference only `VSaga.Abstractions`. That symmetry is what makes cross-runtime
participation possible: a Node participant and a .NET saga can exchange messages over the same wire
format with neither side aware of the other's language.

## Packages

Seven npm packages under `typescript/packages/`, each independently published as `@vsaga/*`:

| Package | Purpose |
| --- | --- |
| [`@vsaga/protocol`](../typescript/packages/protocol/README.md) | The wire contract: envelope shape, header names, routing-key convention, PascalCase body codec. Pulls in no third-party runtime code — its one `dependencies` entry is `@types/node`, which ships types only — and every other package builds on this one. |
| [`@vsaga/participant`](../typescript/packages/participant/README.md) | Framework-agnostic participant runtime: dispatch, duplicate-delivery dedup, ack/nack, reply-with-causation, topology reporting. |
| [`@vsaga/transport-rabbitmq`](../typescript/packages/transport-rabbitmq/README.md) | An `amqplib`-backed `MessageTransport`, wire-compatible with [`VSaga.Transport.RabbitMQ`](transports/rabbitmq.md). |
| [`@vsaga/transport-http`](../typescript/packages/transport-http/README.md) | A brokerless `MessageTransport` over plain HTTP, wire-compatible with [`VSaga.Transport.Http`](transports/http.md). |
| [`@vsaga/express`](../typescript/packages/express/README.md) | Mounts `@vsaga/transport-http`'s inbound receive endpoint on an Express app/Router. |
| [`@vsaga/fastify`](../typescript/packages/fastify/README.md) | Registers it as a Fastify plugin. |
| [`@vsaga/nestjs`](../typescript/packages/nestjs/README.md) | A NestJS dynamic module mounting it on the Express-platform adapter. |

Each package's own README (linked above) is the detailed reference for its API — this page is the
index and the cross-runtime picture; it doesn't restate every option.

## Quick example

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

Swap `@vsaga/transport-rabbitmq` for `@vsaga/transport-http` (plus a hosting adapter — `@vsaga/express`,
`@vsaga/fastify`, or `@vsaga/nestjs` — to receive inbound requests) to run the same participant against
a broker-free HTTP topology instead; the `createParticipant`/`.on(...)` code is unchanged either way.

## Wire compatibility

A message type is declared once, as its C# short type name (`message<TBody>('OrderShipped')`) — this
doubles as both the `x-vsaga-message-type` header and the derived routing key, so a Node participant
and a .NET saga round-trip the exact same message with no translation layer, as long as both sides
declare the same type name. `@vsaga/protocol` implements the same PascalCase JSON body codec, the same
four envelope headers (plus `traceparent`/`tracestate` — see
[`observability.md`](observability.md#traces)), and the same causation-id-on-reply wiring as
`VSaga.Abstractions`.

## Dispatch semantics (ported 1:1 from the .NET reference participant)

- Unknown message type → ack and drop (someone else's message on a shared binding).
- Duplicate message id → ack and skip, via a pluggable `IdempotencyStore`.
- Handler resolves → ack.
- Handler throws → `nack(requeue: false)` — straight to the dead-letter queue, never a hot redelivery
  loop.
- A handler may legitimately reply zero times (e.g. a compensating command that must be an idempotent
  no-op).

The two handler outcomes above — resolve → ack, throw → nack — are what the default `autoAck: true`
does. Under `autoAck: false` the runtime settles nothing either way and the handler owns it via
`ctx.ack()`/`ctx.nack()`, for work that must only acknowledge after an external commit. (The
unknown-type and duplicate acks are not gated on it: those are the runtime's own, and happen before
any handler runs.) See [that package's README](../typescript/packages/participant/README.md) for the
option itself.

What `ctx.nack(true)` then *means* is the transport's call. On `@vsaga/transport-rabbitmq` it is a
broker requeue. On `@vsaga/transport-http` there is no broker, so it genuinely redelivers only for
messages that transport enqueued itself — a same-process publish, or a `200` synchronous reply — while
a message that arrived as an inbound HTTP request logs at error and drops, because the retry belongs
to the peer that POSTed it. `ctx.nack(false)` always logs at error and drops there: that transport has
no dead-letter queue, so the log line is the dead-letter record. See
[`transports/http.md`](transports/http.md#ack-nack-and-requeue).

## Topology reporting

Pass a `topology: TopologyReporter` (`@vsaga/participant`'s `httpTopologyReporter` — the only
implementation in the SDK, usable regardless of which transport the participant itself runs over) so a
participant resolves to a named node on the dashboard's Saga Map (see
[`dashboard.md`](dashboard.md#saga-map)) instead of an `Unresolved` one. Registration is best-effort —
a cold dashboard never stops a participant from starting.

It posts to the Dashboard API's `POST /api/topology/registrations` rather than writing to Postgres
the way .NET's `AddVSagaTopologyRecording` does. That asymmetry is deliberate: registering two rows
should not require handing a Node service database credentials and a duplicate copy of the schema.
The endpoint is an upsert keyed on `(serviceName, messageType)`, so re-reporting on every restart is
the intended usage, not a leak.

## A runnable cross-runtime example

[`typescript/samples/notification-participant`](../typescript/samples/notification-participant) is
the OrderProcessing sample's `NotificationService` rewritten in TypeScript. Layer its overlay on the
reference stack and a Node process handles messages published by .NET sagas, replying with messages
those sagas resume on:

```bash
docker compose -f docker-compose.yml -f docker-compose.node.yml up -d --build
docker compose logs -f notification-participant
```

The .NET side changes by exactly one flag — `Participants__NotificationInProcess=false`, which stops
it registering the participant being replaced. Nothing else is reconfigured, because there is nothing
else to reconfigure. See [that sample's README](../typescript/samples/notification-participant/README.md)
for what it handles and why the wire formats line up.

## Why this SDK has its own toolchain, separate from the dashboard

`typescript/` is an npm workspace (`workspaces: ["packages/*", "samples/*"]`) covering these seven SDK
packages plus the runnable samples. `typescript/dashboard-web` (the Angular dashboard SPA — see
[`dashboard.md`](dashboard.md#the-spa)) is **deliberately not a member of that workspace**: it has its
own lockfile, its own Angular CLI toolchain, and nothing in common with a set of publishable Node SDK
packages beyond living under the same `typescript/` directory. CI runs them as two separate jobs
(`angular` and `typescript` in `.github/workflows/ci.yml`) for the same reason. This is also why
`typescript/eslint.config.mjs` and `typescript/vitest.config.mts` both explicitly exclude
`dashboard-web/**`.

## Install, build, test

```bash
cd typescript
npm install
npm run lint
npm run typecheck
npm run build
npm run test
```

See [`../CONTRIBUTING.md`](../CONTRIBUTING.md) for the full command set this repo's CI runs.
