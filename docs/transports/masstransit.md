# Transport adapter: MassTransit

`VSaga.Transport.MassTransit`, pinned to **MassTransit 8.5.8** (Apache-2.0; v9 is transitioning to a
commercial license, so `Directory.Packages.props` bounds the version range below `9.0.0` so a
consumer's own restore can never silently float onto it). Built entirely on
`IBus`/`IPublishEndpoint`/`ISendEndpointProvider`/`IConsumer<T>` — never MassTransit's Courier
(routing slips) or its own saga persistence (Automatonymous). Full build history and live-verification
detail: [`../history/transport-adapter-masstransit.md`](../history/transport-adapter-masstransit.md).

- **One MassTransit contract for every vSaga message, not one per type.** MassTransit's pub/sub
  topology is built around compile-time generics (`Publish<T>`, `IConsumer<T>`), but vSaga only ever
  knows message types as runtime `Type` instances. `VSagaEnvelopeMessage` is the one fixed record every
  vSaga message actually travels as (`MessageTypeName` plus an already-serialized JSON body), forced
  onto one durable topic exchange with the routing key read back off the message itself.
  `ConfigureConsumeTopology = false` is set explicitly — left on, every subscriber's queue would
  receive every vSaga message ever published, since they all share one contract.
- **Routing keys are the raw PascalCase type name, not kebab-case.** The send-side
  `UseRoutingKeyFormatter` reads `MessageTypeName` straight off the envelope record, and
  `IRabbitMqReceiveEndpointConfigurator.Bind` uses `messageType.Name` verbatim — `OrderApproved`, not
  the `order-approved` the RabbitMQ and Brighter adapters use, and both default to the same exchange
  name, `vsaga.saga.events`. Those two tracks therefore do **not** interoperate on one broker despite
  looking identically shaped; a mismatched publish here surfaces as an unroutable
  `MessageTransportPublishException` rather than silently vanishing, which at least makes it
  diagnosable. Give each track its own `ExchangeName` if they share a broker, and use the PascalCase
  form when binding a non-vSaga AMQP consumer to this adapter's exchange. See
  [`index.md`](index.md#choosing-an-adapter) for the full comparison.
- **The four vSaga headers ride on MassTransit's own header pipeline** (`SendContext.Headers`/
  `ConsumeContext.Headers`), not smuggled inside the wrapper record — genuine MassTransit metadata
  making the round trip.
- **Ack/nack.** MassTransit has no mid-flight ack primitive — a consumer settles only by returning
  (ack) or throwing (fault). `VSagaEnvelopeConsumer` bridges `IMessageAckContext` onto that: a recorded
  nack (or no decision at all) becomes a thrown exception once the handler completes. Every receive
  endpoint disables MassTransit-level retry (`UseMessageRetry(r => r.None())`) — `SagaOrchestrator`
  owns all redelivery.
- **Unroutable-publish detection.** MassTransit surfaces RabbitMQ's mandatory-publish-plus-return
  semantics as `MessageReturnedException` when `PublishContext.Mandatory` is set and no queue is bound;
  the adapter sets it on every publish and wraps the exception into the same provider-agnostic
  `MessageTransportPublishException` RabbitMQ's own adapter throws, `IsUnroutable` included.

Options: [`../configuration.md#masstransitoptions-vsagatransportmasstransit`](../configuration.md#masstransitoptions-vsagatransportmasstransit).
Compose overlay: `docker-compose.masstransit.yml`.
