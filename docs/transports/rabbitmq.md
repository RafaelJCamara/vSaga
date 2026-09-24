# Transport adapter: RabbitMQ

`VSaga.Transport.RabbitMQ` is the reference `IMessageTransport` implementation, built directly on
`RabbitMQ.Client` (no MassTransit/Wolverine/Brighter dependency). Every other adapter's design is
judged against this one's shape.

- **Topology.** One durable topic exchange (`RabbitMqOptions.ExchangeName`, default
  `vsaga.saga.events`); `PublishAsync` routes by a kebab-cased derivation of the message-type name as
  the routing key (`IRoutingKeyConvention`, e.g. `OrderApproved` -> `order-approved`) — not the literal
  PascalCase type name, which matters if you're binding a non-vSaga AMQP consumer directly to the
  exchange. Kebab-case is *this* adapter's convention (Brighter's matches it; the Wolverine and
  MassTransit adapters publish the raw PascalCase name onto the same default exchange name — see
  [`index.md`](index.md#choosing-an-adapter) before assuming the advice above carries across).
  `SendAsync`
  targets AMQP's default (nameless) exchange directly, routing key = destination — a genuine direct
  send, not a topic-exchange trick. `SubscribeAsync` declares one durable queue per consumer, bound to
  the shared exchange for each declared message type, plus a dead-letter exchange/queue pair
  (`DeadLetterExchangeName`, default `vsaga.dlx`) before returning.
- **Delivery.** `AsyncEventingBasicConsumer`, one channel per `SubscribeAsync` call, with
  `BasicQosAsync(prefetchCount: 32)`. Deliveries on one subscription are handled one at a time,
  sequentially, awaited to completion before the next — but **prefetch is not what makes that true**.
  Prefetch bounds how many unacked messages the broker will push at the consumer; dispatch concurrency
  is a separate knob, `ConsumerDispatchConcurrency`, which `RabbitMqConnectionManager` never sets and
  which RabbitMQ.Client 7.x documents as defaulting to `1` ("set to a value greater than one to enable
  concurrent processing"). Raising `prefetchCount` therefore buys throughput-smoothing, not
  parallelism; a sequential consumer is what you still have. (This matters when tuning chaos-injected
  delay against this adapter — see [`../chaos.md`](../chaos.md#running-it-against-the-sample).)
- **Unroutable-publish detection.** Publisher confirms plus `mandatory: true` are both enabled, so a
  broker-side nack or an unroutable message throws `MessageTransportPublishException.IsUnroutable`
  instead of vanishing silently. The most common way to hit this: publishing a message type nothing has
  called `SubscribeAsync` for yet — see the callout in
  [`getting-started.md`](../getting-started.md#run-it) if you're moving a saga from the in-memory
  transport to this one for the first time. The exception message names the unbound routing key/queue as
  the likely cause.
- **Automatic recovery.** `AutomaticRecoveryEnabled`/`TopologyRecoveryEnabled` are both on
  (`RabbitMqConnectionManager`) — a connection blip recovers without operator intervention, though a
  message delivered-but-unacked immediately before a recovery can hit the documented RabbitMQ.Client
  limitation where the recovered channel's restarted delivery-tag numbering can no longer ack it (a
  transient, auto-recovering condition also seen on the Brighter adapter's gateway; see
  [`brighter.md`](brighter.md)).
- **Delivery bodies are copied, not passed through.** RabbitMQ.Client 7.x backs a delivery's body with
  pooled/rented memory valid only for the duration of the event handler; the client can reuse that
  buffer for a later frame the moment a handler awaits something or returns while `ReceivedMessage` is
  still retained. `DispatchReceivedAsync` copies into a freshly-owned array before handing it off, so
  every caller sees a stable body regardless of how long it holds onto it (see
  [`../history/`](../history/) for the corrupted-payload bug this closes).

Options: [`../configuration.md#rabbitmqoptions-vsagatransportrabbitmq`](../configuration.md#rabbitmqoptions-vsagatransportrabbitmq).

Confirms/DLQ/redelivery, and the header-threading gotchas RabbitMQ's own adapter first surfaced (the
`SourceService`/`CausationId` story), are covered in the general
[production-hardening history](../history/project-origins-and-hardening-pass.md) and
[sub-saga parent-linkage history](../history/sub-saga-parent-linkage.md).

## TypeScript

`@vsaga/transport-rabbitmq` is wire-compatible with this adapter for Node participants — see
[`../typescript-participants.md`](../typescript-participants.md).

```ts
import { createRabbitMqTransport } from '@vsaga/transport-rabbitmq';
import { createParticipant } from '@vsaga/participant';

const transport = await createRabbitMqTransport({
  connectionString: 'amqp://guest:guest@localhost:5672/',
});

const payments = createParticipant({
  serviceName: 'payments',
  queue: 'vsaga.participant.payments',
  transport,
});
```
