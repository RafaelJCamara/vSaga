# Transport adapter: Brighter

`VSaga.Transport.Brighter`, built on `Paramore.Brighter` + `Paramore.Brighter.MessagingGateway.RMQ.Async`
10.7.0, implements `IMessageTransport` directly on Brighter's transport-level primitives
(`RmqMessageProducer.SendAsync` to publish, `RmqMessageConsumer` to receive/ack/reject) — never
Brighter's `CommandProcessor` dispatch pipeline, its Outbox/Inbox, or its request-handler routing.
Constructed directly as plain singletons rather than through `services.AddBrighter(...)`, since that
helper wires up the dispatch/outbox stack this adapter must not depend on. Full build history and
live-verification detail: [`../history/transport-adapter-brighter.md`](../history/transport-adapter-brighter.md).

- **No default exchange for a direct send.** Brighter's `RmqMessageProducer` is bound to exactly one
  exchange for its whole lifetime and always publishes using the message's `Topic` as the routing key —
  there's no "default/nameless exchange" concept anywhere in the package. The substitute is a routing
  key per queue, and it is **`SubscribeAsync` that establishes it, not `SendAsync`**: a subscription
  binds its queue to the kebab-cased routing key of every declared message type *plus* one more — the
  queue's own `QueueNameHint` (`.Append(new RoutingKey(subscription.QueueNameHint))`). `SendAsync` then
  merely publishes with the destination as the routing key; it declares and binds nothing. Mechanically
  a different route from RabbitMQ's default-exchange send, functionally identical outcome — with one
  consequence the ordering makes unavoidable: **a send to a queue that never subscribed is silently
  dropped**, not bound on demand. Nothing has bound that routing key, and this gateway has no
  `mandatory` flag to return the message (see below), so the publish reports success and the message
  is gone.
- **One queue, many routing keys, needs a lower-level primitive.** The higher-level `Subscription`/
  `IAmAChannelFactory` API exposes only a single `RoutingKey` per subscription — `RmqMessageConsumer`'s
  own constructor (which takes a `RoutingKeys` collection) is used directly instead.
- **Pull-based consumption needs its own pump.** `IAmAMessageConsumerAsync.ReceiveAsync(timeout)` is
  poll-based, unlike RabbitMQ.Client's push-based consumer — `SubscribeAsync` runs its own background
  loop, playing the role Brighter's own Service Activator pump would (deliberately never brought in).
- **Topology declares lazily by default — forced eager.** `RmqMessageConsumer.EnsureChannelAsync`
  declares the queue/bindings inside `ReceiveAsync` itself, not the constructor, so a message published
  before a fresh consumer's first receive is silently dropped. `SubscribeAsync` forces topology to
  exist before returning (the contract `IMessageTransport.SubscribeAsync` requires) with a 50ms
  warm-up receive before starting the real consume loop.
- **Unroutable-publish detection: absent for the zero-bound-queues case; a genuine nack is caught, but
  not the way an earlier version of this adapter assumed — both confirmed by live testing.**
  `RmqMessageProducer` never sets AMQP's `mandatory` flag and exposes no way to request it — publishing
  to a routing key nobody has ever bound a queue to still yields a broker-side confirm `Success = true`,
  with no exception and no way to detect it (the test
  `Publish_ToUnboundRoutingKey_DoesNotThrow_NoMandatoryReturnSupportInBrighterRmqGateway` documents this
  directly). A genuine broker-side nack (e.g. a queue at its length limit) is a different story: Brighter
  10.7.0 unconditionally creates every channel with RabbitMQ.Client's own publisher-confirmation
  *tracking* enabled, so it's RabbitMQ.Client 7.2.2 itself — not Brighter's `ISupportPublishConfirmationAsync`
  confirmation event — that detects the nack, by throwing `RabbitMQ.Client.Exceptions.PublishException`
  synchronously out of `RmqMessageProducer.SendAsync` before that method's own Task ever completes.
  `BrighterTransport.SendWithConfirmationAsync` catches that exception directly and rethrows it as
  `MessageTransportPublishException` (`IsUnroutable` mapped from the underlying `PublishException.IsReturn`,
  which is always `false` today since `mandatory` is never set) — the confirmation-event subscription this
  method used to rely on for that same job was proven, by live testing against a real broker, to be dead
  code for a genuine nack (the exception unwinds before that check is ever reached), and was removed. The
  test `Send_ToOverflowingQueue_ThrowsMessageTransportPublishException_NotIsUnroutable` (a queue declared
  with `x-max-length: 0` / `x-overflow: reject-publish`, so every publish into it is genuinely rejected)
  documents this verified behaviour directly.
- **Header filtering on receipt.** `ReceivedMessage.Headers` is filtered to the `x-vsaga-` prefix on
  the way in — plus the two bare W3C trace context headers, `traceparent`/`tracestate`, as a named
  allowlist exception (they interoperate only by *not* carrying the `x-vsaga-` prefix, so both still
  round-trip losslessly per [`index.md`](index.md#what-every-adapter-guarantees)) — rather than passed
  through unfiltered. Brighter's own `Bag` carries CloudEvents-flavoured echoes of core fields on
  receipt (`CorrelationId`, `Topic`, `HandledCount`, `cloudEvents_id`, ...)
  that would otherwise leak forward as bogus outbound headers on redelivery.
- **A known intermittent condition, not a defect.** The low-traffic sub-saga queues occasionally logged
  `ChannelFailureException`/`precondition_failed: unknown delivery tag` following RabbitMQ.Client's
  automatic connection recovery — a documented client limitation (a message delivered-but-unacked
  immediately before a recovery can't be acked against the recovered channel's restarted delivery-tag
  numbering), not unique to Brighter's gateway. The existing catch-log-retry loop recovers automatically
  and no message was lost during live verification.

Options: [`../configuration.md#brighteroptions-vsagatransportbrighter`](../configuration.md#brighteroptions-vsagatransportbrighter).
Compose overlay: `docker-compose.brighter.yml` (uses the `!override` YAML merge tag on `ports` — Compose's
default list-merge concatenates `ports` arrays across `-f` files instead of replacing them).
