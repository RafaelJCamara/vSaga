# Transports

Every message vSaga sends or receives goes through one `IMessageTransport` implementation
(`VSaga.Abstractions.Transport`). `VSaga.Core` depends on nothing else to move messages, and no
adapter uses the underlying bus's own saga/state-machine or handler-discovery machinery — only its
raw publish/consume primitives. That rule is the load-bearing constraint behind every adapter's design;
see each adapter's own page for how it plays out concretely.

## The contract

```csharp
public interface IMessageTransport
{
    Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull;

    Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull;

    Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default);

    Task SendRawAsync(string destination, string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        PublishRawAsync(messageTypeName, body, envelope, cancellationToken);

    Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default);
}
```

- **`PublishAsync`/`SendAsync`** — the typed entry points every saga step actually calls (via
  `ISagaContext`). `Publish` broadcasts to whatever subscribes to the message type; `Send` addresses
  one named destination directly, bypassing topic routing.
- **`PublishRawAsync`/`SendRawAsync`** — untyped counterparts, publishing pre-serialized JSON by
  message-type name rather than CLR type. Used by callers that hold a message's stored type name and
  payload rather than a typed instance: the engine's own bounded redelivery after an infrastructure
  failure (`SagaOrchestrator`, via `PublishRawAsync`), the transactional outbox's crash-recovery poller
  (`SagaOutboxDispatcherHostedService` — `SendRawAsync` for a row stored with a destination,
  `PublishRawAsync` otherwise), and the dashboard's manual-retry endpoint (see
  [`dashboard.md`](../dashboard.md#manual-retry), via `PublishRawAsync`). Both methods skip the
  outbound middleware pipeline (see [`../chaos.md`](../chaos.md#scope-what-chaos-can-and-cant-reach)).
  `SendRawAsync` defaults to falling back to a broadcast via `PublishRawAsync` for an adapter that
  predates the method; every adapter shipped in this repo overrides it with a real addressed send.
- **`SubscribeAsync`** — registers a handler for a runtime-declared set of message types
  (`TransportSubscription`), returning a disposable that stops it. Async because a real broker adapter
  needs to declare exchanges/queues/bindings and start consuming *before returning* — topology must
  exist by the time the call completes, not lazily on first use (a gap the Brighter adapter had to work
  around; see [`brighter.md`](brighter.md)).

## What travels with a message

Every adapter carries the same three things per message, though not all in the same place:

- **The message type name, correlation id and message id.** The type name is the CLR short name
  (`OrderShipped`), which the receiving engine matches against its saga's declared message types to
  pick the type the body deserializes to. The RabbitMQ and HTTP adapters send all three as the headers
  `x-vsaga-message-type`, `x-vsaga-correlation-id` and `x-vsaga-message-id` (RabbitMQ also fills the
  AMQP `correlation_id`/`message_id` properties), and the HTTP receive endpoint answers `400` to a POST
  that lacks any of them or whose correlation id is not a GUID. The Wolverine, MassTransit and Brighter
  adapters carry them wholly or partly in their bus's own envelope or a wrapper record instead; see
  each adapter's page.
- **`MessageEnvelope.Headers`**. Every adapter round-trips the `x-vsaga-`-prefixed keys and the W3C
  trace pair (see [below](#what-every-adapter-guarantees)); the HTTP and Brighter adapters drop any
  other key on receipt, so a custom header needs the `x-vsaga-` prefix to survive every adapter.
  `MessageEnvelope.From` stamps `x-vsaga-source-service`
  (the publisher; a saga publishes under its saga type) and, for a publish made while handling an
  inbound message, `x-vsaga-causation-id` (that message's id, which the Saga Map stitches edges with).
  `StartChildAsync` adds `x-vsaga-parent-saga-type`/`x-vsaga-parent-correlation-id` to a child's
  initiating message. The dashboard's [manual retry](../dashboard.md#manual-retry) adds
  `x-vsaga-target-saga-type` (`MessageEnvelope.TargetSagaTypeHeader`) to the message it republishes:
  an engine whose saga type is not the named one acknowledges the message and ignores it, so the retry
  re-runs a step in one saga type only. Nothing else sets it, and it never propagates, because outbound
  envelopes are built fresh. These five are the "five vSaga headers" the adapter pages refer to. Two
  more travel the same way: `x-vsaga-delivery-attempt`, which the engine adds when it redelivers after an
  infrastructure failure, and the unprefixed W3C `traceparent`/`tracestate` (see
  [`../observability.md`](../observability.md#traces)).
- **The body**: the message serialized by `System.Text.Json` with default options, so property names
  stay PascalCase. The Wolverine and MassTransit adapters wrap those bytes in a record of their own.

## The two decorators every adapter is wrapped in

Chaos and the Saga Map's topology registry both work across every adapter with zero adapter-specific
code, but they are **not** the same seam. Anyone writing a third-party adapter has to satisfy both:

- **`MiddlewarePipelineTransport`** (`VSaga.Transport.Common`) is the
  `IOutboundMessageMiddleware`/`IInboundMessageMiddleware` seam, and the only one `VSaga.Chaos` plugs
  into. Every transport registration call in this repo (`AddVSagaRabbitMq`, `AddVSagaWolverine`,
  `AddVSagaMassTransit`, `AddVSagaBrighter`, `AddVSagaHttp`, `AddVSagaInMemoryTransport`) applies it
  **unconditionally** — including the in-memory adapter's, which until recently registered its bare
  transport directly and therefore silently never ran chaos at all. Unconditional rather than "only
  when some middleware is registered", because a pipeline over an empty middleware list is a pure
  pass-through, so there is nothing to save by skipping it — and a registration whose resolved type
  depends on what else happens to be in the container would change shape the moment a caller adds
  `AddVSagaChaos`.
- **`TopologyRecordingTransport`** (`VSaga.Abstractions.Transport`) is a *sibling* decorator, not a
  middleware — topology recording does not go through the middleware pipeline at any point. It
  intercepts `SubscribeAsync` and records the consumer/message-type/queue triple that every
  `TransportSubscription` already declares, which is why no subscriber writes a line of code for the
  Saga Map. `AddVSagaTopologyRecording()` (`VSaga.Core`) applies it by removing the *last*
  `IMessageTransport` service descriptor and re-registering a factory that wraps whatever that
  descriptor built. That imposes a hard requirement on an adapter: register `IMessageTransport` as a
  **factory** (`services.AddSingleton<IMessageTransport>(sp => ...)`), never as a pre-built instance
  or an implementation type. A descriptor with no `ImplementationFactory` makes
  `AddVSagaTopologyRecording()` throw `InvalidOperationException` rather than silently skip recording.

## Choosing an adapter

| Adapter | Underlying bus | When to reach for it |
| --- | --- | --- |
| [`rabbitmq.md`](rabbitmq.md) | `RabbitMQ.Client` directly | The reference implementation. Default choice for a real broker with no other constraint. |
| [`wolverine.md`](wolverine.md) | WolverineFx.RabbitMQ | Already standardized on Wolverine elsewhere in your stack. |
| [`masstransit.md`](masstransit.md) | MassTransit 8.x + RabbitMQ | Already standardized on MassTransit 8.x (Apache-2.0; v9 is commercially licensed — this adapter is deliberately pinned below `9.0.0`). |
| [`brighter.md`](brighter.md) | Paramore.Brighter's RabbitMQ gateway | Already standardized on Brighter elsewhere in your stack. |
| [`http.md`](http.md) | Plain HTTP, no broker | No broker infrastructure available or wanted at all — trades parallel fan-out for synchronous request/response and an in-process (non-durable) local-dispatch path. |
| [`in-memory.md`](in-memory.md) | None (single process) | Local development and `SagaTestHarness`-based unit testing. Not for production. |

All four RabbitMQ-family adapters (RabbitMQ, Wolverine, MassTransit, Brighter) share one topic
exchange (`vsaga.saga.events` by default) and route by message-type name, so the *shape* of a
deployment looks the same regardless of which one is chosen — see [`configuration.md`](../configuration.md#transport-options)
for each adapter's options.

**The same shape is not the same wire format.** The routing keys diverge, and no adapter's default
exchange name diverges with them — all four default to `vsaga.saga.events`:

| Adapter | Routing key for `OrderApproved` |
| --- | --- |
| RabbitMQ (`DefaultRoutingKeyConvention`), Brighter (`RoutingKeyConvention`) | `order-approved` (lower-kebab-case) |
| Wolverine, MassTransit | `OrderApproved` (the raw PascalCase type name) |

Two adapters from different rows are therefore **not wire-interchangeable**, and running both against
one broker on the default exchange name is a live hazard: a Wolverine publisher's `OrderApproved`
never reaches a RabbitMQ-adapter subscriber bound to `order-approved`, and vice versa. The message is
simply unroutable, so what happens next is whatever that publisher's own unroutable detection does —
an exception on the RabbitMQ and MassTransit tracks, complete silence on the Wolverine and Brighter
ones (see [what every adapter guarantees](#what-every-adapter-guarantees)). Give each track its own
`ExchangeName` if they must share a broker, and check which row your adapter is in before binding a
non-vSaga AMQP consumer directly to the exchange.

## Running an adapter's own overlay

RabbitMQ is what plain `docker compose up` runs (see ["Run the demo"](../../README.md#run-the-demo)).
To try Wolverine, MassTransit, Brighter, or HTTP instead, each has its own compose overlay — but unlike
the chaos overlay, these two things are **not optional**:

- **A `-p <project-name>` compose project name.** No compose file here sets a `name:` key, so Compose
  derives the project name from the directory — `vsaga`. Without `-p`, this overlay's containers join
  that same default project instead of a distinct one, colliding with any stack already up under the
  plain command.
- **Every host port is remapped** (`!override` in each overlay file) so the overlay's stack can run
  *alongside* the plain one rather than fighting it for `5433`/`5672`/`15672`/`5080`/`4200`. Skip the
  `-p` flag and you'll bring these containers up fine, then find every URL this repo documents
  (`localhost:4200`, `localhost:5080`, `localhost:15672`, ...) pointing at whichever stack happened to
  bind the port first.

```bash
docker compose -p vsaga-wolverine    -f docker-compose.yml -f docker-compose.wolverine.yml    up -d --build
docker compose -p vsaga-masstransit  -f docker-compose.yml -f docker-compose.masstransit.yml  up -d --build
docker compose -p vsaga-brighter     -f docker-compose.yml -f docker-compose.brighter.yml     up -d --build
docker compose -p vsaga-http         -f docker-compose.yml -f docker-compose.http.yml         up -d --build
```

Each overlay remaps a different, non-overlapping port range, so more than one can genuinely run at
once — see that overlay's own file header for its exact ports:

| Overlay | Postgres | RabbitMQ (AMQP / mgmt) | Dashboard API | Dashboard UI |
| --- | --- | --- | --- | --- |
| `docker-compose.wolverine.yml` | `5443` | `5772` / `15772` | `5180` | `4300` |
| `docker-compose.masstransit.yml` | `5444` | `5872` / `15872` | `5280` | `4400` |
| `docker-compose.brighter.yml` | `5445` | `5972` / `15972` | `5380` | `4500` |
| `docker-compose.http.yml` | `5446` | `6072` / `16072` | `5480` | `4600` |
| `docker-compose.redis.yml` (a persistence overlay, not a transport one — see [`../persistence.md`](../persistence.md#redis); adds `redis` on `6479`) | `5447` | `6272` / `16272` | `5680` | `4800` |
| `docker-compose.mongo.yml` (a persistence overlay — see [`../persistence.md`](../persistence.md#mongodb); adds `mongo` on `27018`) | `5448` | `6172` / `16172` | `5580` | `4700` |

The dashboard ports, API and UI, are bound to `127.0.0.1` in every stack, and the UI's port is always
the API's minus 880.

Tear one down the same way you brought it up, naming the same `-p` project:
`docker compose -p vsaga-wolverine down`.

**Viewing an overlay's dashboard.** Open that stack's Dashboard UI port from the table above, e.g.
http://localhost:4300 for the Wolverine overlay; there is nothing to edit. Each stack's UI container
proxies `/api` and `/hubs` to its own stack's dashboard API, so several stacks' dashboards can be open
side by side (see [`../dashboard.md`](../dashboard.md#how-it-is-served)); in one browser each stack keeps its
own sign-in, because compose names the session cookie after the project (`vsaga.session.vsaga-wolverine`, say)
and cookies are scoped by host, not port, while the one cookie they share, `XSRF-TOKEN`, is handled by the
SPA reading the session again and retrying once when the API refuses a token from the other stack (see
[CSRF protection](../dashboard.md#csrf-protection)). To point the SPA's dev server
at an overlay instead, start it with `VSAGA_API_URL` set to that overlay's Dashboard API port — see
[`dashboard-web/README.md`](../../dashboard-web/README.md#run-it).

## What every adapter guarantees

- **All five vSaga envelope headers round-trip losslessly**: `x-vsaga-source-service`,
  `x-vsaga-causation-id`, `x-vsaga-parent-saga-type`, `x-vsaga-parent-correlation-id` and
  `x-vsaga-target-saga-type` — plus, since [production-readiness §6](../design/production-readiness.md)
  (shipped as that plan's §8 item 17), the W3C `traceparent`/`tracestate` pair (bare names, not
  `x-vsaga-`-prefixed — see [`../observability.md`](../observability.md#traces)). Every adapter that
  puts a message on a wire — RabbitMQ, Wolverine, MassTransit, Brighter, HTTP — has a dedicated
  round-trip test for **both** sets in its own suite
  (`PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged`, whose name predates the target saga
  type header it now carries as well, and its traceparent/tracestate sibling). The target header matters
  most here: an adapter that dropped it would turn a targeted dashboard retry back into one every
  subscribed saga type processes. Several of these tests were added specifically because an earlier
  version of this repo shipped header-threading code with tests that hand-built the field and proved
  nothing (see [`../history/sub-saga-parent-linkage.md`](../history/sub-saga-parent-linkage.md)). The
  in-memory adapter is the one exception and needs no such test: it hands the publisher's own
  `MessageEnvelope.Headers` dictionary straight to the subscriber, so there is no encode/decode step for
  a header to be lost in — which is exactly why a header bug caught only by one of those suites is
  invisible under `SagaTestHarness`.
- **An unroutable publish is detected where the underlying package supports it.** RabbitMQ, MassTransit,
  and HTTP all surface it as `MessageTransportPublishException.IsUnroutable`. Wolverine's and Brighter's
  underlying gateway packages have no equivalent as of the pinned versions — confirmed absent by
  reflecting over the packages' publish paths, not merely undocumented — see [`wolverine.md`](wolverine.md)
  and [`brighter.md`](brighter.md) for the tests that pin the verified absence directly.
- **`SagaOrchestrator` owns all retry/redelivery/dedup**, never the underlying bus's own retry policy —
  every adapter configures the underlying bus for zero (or broker-native at-least-once only) retries so
  it doesn't fight the engine's own bounded, application-level redelivery.
