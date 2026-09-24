# Transport adapter: Wolverine

`VSaga.Transport.Wolverine`, built on WolverineFx.RabbitMQ, uses Wolverine's raw send/receive
primitives only — never its mediator/handler-discovery machinery, since that would mean Wolverine
owning dispatch to business logic. Full build history and live-verification detail:
[`../history/transport-adapter-wolverine.md`](../history/transport-adapter-wolverine.md).

- **The scope boundary.** Wolverine's normal mode deserializes an inbound envelope into a specific CLR
  type and invokes a `Handle(T)` discovered by assembly scanning at startup — the opposite of vSaga's
  runtime-registered `SubscribeAsync`. `RawEnvelope` is the fix: every vSaga message travels as this
  one empty marker type, so Wolverine's own discovery only ever needs to know about one static handler.
  The real message type, correlation id, message id, and all four vSaga headers travel inside a
  self-describing JSON payload (`WireEnvelope`) carried as `Envelope.Data`, deliberately not relying on
  Wolverine's own header-to-AMQP-property mapping.
- **Publish/send** funnel through Wolverine's raw-send primitive (`IDestinationEndpoint.SendRawMessageAsync`)
  against a topic-exchange or default-exchange URI, mirroring `RabbitMqTransport`'s own split.
- **Routing keys are the raw PascalCase type name, not kebab-case.** This adapter publishes to, and
  binds queues with, `messageType.Name` verbatim (`OrderApproved`), whereas the RabbitMQ and Brighter
  adapters kebab-case it (`order-approved`) — onto the same default exchange name, `vsaga.saga.events`.
  Those two tracks therefore do **not** interoperate on one broker despite looking identically shaped:
  a mismatched publish is silently unroutable here, since this gateway has no unroutable signal at all
  (see below). Give each track its own `ExchangeName` if they share a broker, and use the PascalCase
  form when binding a non-vSaga AMQP consumer to this adapter's exchange. See
  [`index.md`](index.md#choosing-an-adapter) for the full comparison.
- **Subscribe** starts a listener on the current node immediately
  (`IEndpointCollection.StartListenerAsync`) — Wolverine's higher-level `RegisterListenerAsync` API
  turned out to be its leader-elected, durability-store-backed dynamic-multi-tenancy machinery, not an
  immediate single-node start, and had to be avoided after it left tests hanging.
- **Ack/nack.** `SubscribeAsync`'s caller always decides ack/nack explicitly (vSaga's model); a nack is
  turned into a thrown exception so Wolverine's own `Handle` faults. Wolverine-level retry is disabled
  (`OnException<Exception>().MoveToErrorQueue()`) — `SagaOrchestrator` owns all redelivery.
- **Unroutable-publish detection: absent, confirmed by binary inspection.** WolverineFx.RabbitMQ
  exposes publisher-confirm *settings* but never sets AMQP's `mandatory` flag and has no
  unroutable-return handling anywhere in the package. A message to an unbound routing key is silently
  discarded by the broker; the adapter's own test
  (`Publish_ToUnboundRoutingKey_CompletesWithoutThrowing_NoWolverineUnroutableSignal`) documents this
  verified behaviour directly rather than faking RabbitMQ's exception.

Options: [`../configuration.md#wolverinetransportoptions-vsagatransportwolverine`](../configuration.md#wolverinetransportoptions-vsagatransportwolverine).
Compose overlay: `docker-compose.wolverine.yml`.
