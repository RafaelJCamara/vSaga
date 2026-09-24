# Transport adapter: HTTP

`VSaga.Transport.Http` implements `IMessageTransport` over plain HTTP with **no broker at all** —
Phase 1 of [`../design/http-based-sagas.md`](../design/http-based-sagas.md). `PublishAsync`/`SendAsync`
POST a header-based wire format to configured peer endpoints; a `200` response carrying the same
header set **is** the reply, fed back into whichever local subscriber its type resolves to. Full build
history and live-verification detail:
[`../history/transport-adapter-http.md`](../history/transport-adapter-http.md).

Not to be confused with `.CallHttp`/`ctx.CallHttpAsync` (`VSaga.Http`) — a transport-agnostic saga step
that calls an *ordinary* REST API. This adapter replaces the broker entirely for vSaga-to-vSaga
traffic; `.CallHttp` is for calling something that was never a vSaga participant at all. See
[`saga-dsl.md`](../saga-dsl.md#callhttp-from-vsagahttp) for `.CallHttp`.

## The two mechanisms

`HttpInboundDispatcher` drives both:

- **A per-correlation dispatch gate.** Every local dispatch (a genuine inbound request, a same-process
  publish, or a captured reply) serializes against every other dispatch for the same correlation id, so
  a reply can never re-enter a saga while its own publishing step is still persisting.
- **An ambient (`AsyncLocal`) reply collector**, installed only around a genuine inbound request, that
  captures a handler's own publish as that request's synchronous reply exactly when the publish
  resolves to **no destination** — never by matching correlation id, since a saga can legitimately
  publish something under its own correlation id from inside a reply handler that has a real route and
  must go out as a normal POST.

## Wiring it up

Register the transport and map its inbound receive endpoint — same one-call-plus-one-map shape as
every other `AddVSaga*`/`Map*` pair:

```csharp
builder.Services.AddVSagaHttp(o =>
{
    o.ServiceName = "orders";
    o.Endpoints["payments"] = "http://payments:8080";
    o.Routes["ChargeCard"] = new List<string> { "payments" };
});

var app = builder.Build();
app.MapVSagaHttp();
```

`AddVSagaHttp` (`ServiceCollectionExtensions.cs`) registers `HttpMessageTransport` as the host's
`IMessageTransport`; `MapVSagaHttp` (`VSagaHttpEndpointExtensions.cs`) maps this service's own receive
endpoint at `HttpTransportOptions.InboundPath` (default `/vsaga/messages`) and returns the
`RouteHandlerBuilder`, so you can chain `.RequireAuthorization()` yourself — vSaga ships no auth
opinion here. Full option reference:
[`../configuration.md#httptransportoptions-vsagatransporthttp`](../configuration.md#httptransportoptions-vsagatransporthttp).

Not to be confused with `VSaga.Http`'s `AddVSagaHttpCalls()` — a different package registering a
transport-agnostic `.CallHttp` step, not this adapter; see
[`saga-dsl.md`](../saga-dsl.md#callhttp-from-vsagahttp).

## A cross-process deadlock, found live

A fan-out reply that routes back to its own originating service can deadlock that service's dispatch
gate against itself: if a saga host's own dispatch is still holding its correlation gate while awaiting
an outbound call's HTTP response, and that call's own reply routes back to the same saga host under the
same correlation id, the inbound reply cannot acquire the very gate the outbound call is blocked
behind — a genuine cross-process circular wait, breakable only by a timeout. Fixed by bounding the
inline dispatch path's own gate-acquisition wait (`InlineGateAcquireTimeout`, a fixed 5s — not exposed
on `HttpTransportOptions`, so it isn't independently tunable today) and falling
back — on timeout only — to the same deferred-to-a-background-pump path a captured reply already uses:
a `202` now, dispatched once the gate frees, lossless rather than a long block.

## Ack, nack, and requeue

`HttpInboundDispatcher` also owns [`../design/http-based-sagas.md`](../design/http-based-sagas.md)'s
§4.4 ack model. With no broker underneath it, a requeue has exactly one place it could go — the same
in-process local-dispatch channel — and whether it is honoured turns on one question: **can a
redelivery reproduce the original delivery exactly?**

- **Yes for a local dispatch and for a synchronous reply.** A same-process `PublishAsync`/
  `PublishRawAsync` that resolved to a local subscriber, and a `200` reply captured off one of our own
  outbound POSTs, both arrive through `EnqueueLocalDelivery` and get a real implementation:
  `AckAsync` drops, `NackAsync(requeue: true)` genuinely re-enqueues onto that same channel, and
  `NackAsync(requeue: false)` logs at error and drops (no dead-letter queue exists here by design, so
  an error log carrying type/correlation/message id *is* the dead-letter record). Requeue is honest on
  these paths because the redelivered copy differs from the original in nothing but time — same body,
  the very same headers dictionary instance, same deferred-never-inline dispatch, landing back exactly
  where the first copy came from.
- **No for a genuine inbound HTTP request.** That delivery gets `CreateInboundRequestAck`, where
  `AckAsync` drops and **both** nack forms log at error and drop. It is inseparable from the request
  carrying it: it is dispatched inline under the ambient `SyncReplyCollector`, and the status and body
  the peer receives are decided by that dispatch's own outcome. A re-enqueued copy would run later off
  the pump with no collector installed and the response already written — so the handler's reply
  publish, the entire point of an inbound request on this transport, would find nothing to capture it
  and throw unroutable instead. That is a different, reply-less delivery wearing the original's name,
  not a redelivery of it. The peer that POSTed the message owns its retry; this process cannot ask for
  one. The same ack context is deliberately used for the copy the gate-acquire timeout defers to the
  pump, even though that copy *does* land on the channel: letting gate contention silently decide
  whether a nack means "redeliver" or "drop" would be worse than one flat rule.

Both contexts settle idempotently behind an `Interlocked` guard, so a handler that acks and then nacks
in a `finally`, or nacks twice down two unwinding paths, can never enqueue the same message twice —
first settle wins.

**Requeue chains are capped at `HttpInboundDispatcher.MaxRequeueAttempts` (5).** The count rides on
each successive delivery's ack context rather than in a header, deliberately: that is what keeps a
redelivered copy byte-identical to its original, `x-vsaga-delivery-attempt` included. The cap is also
genuinely necessary, because it bounds something no other counter does — `SagaOrchestrator` never
calls `NackAsync(requeue: true)` at all, it republishes through `PublishRawAsync` with an incremented
`x-vsaga-delivery-attempt` and dead-letters at `SagaOrchestratorOptions.MaxDeliveryAttempts`. A
requeue therefore increments nothing the orchestrator reads, so a handler that always requeues would
spin this channel forever on a counter nobody owns. The two bounds compose rather than cancel:
requeue chains terminate here, republish chains terminate at `MaxDeliveryAttempts` because every
republish increments the header this dispatcher preserves, and interleaving them is bounded by their
product.

**TypeScript parity.** `@vsaga/transport-http` implements the same model, with the same split and the
same cap of five. A delivery the transport enqueued itself — a same-process `publish()`/`send()` that
resolved to a local subscriber, or a `200` synchronous reply to one of its own outbound POSTs —
honours `nack(requeue: true)` by re-dispatching it byte-identically, and degrades to an error-level
drop once the transport is closed, exactly as the .NET side does on a completed channel. A delivery
that arrived as an inbound HTTP request logs at error and drops on both nack forms. The one divergence
is the log sink: .NET uses `ILogger`, TypeScript uses `console.error`/`console.warn` behind a
`[vsaga]` prefix, because `@vsaga/transport-http` takes no logger dependency — `@vsaga/participant`
owns the `Logger` interface, and depending on it would invert the package layering.

## Known, deliberate limitations

- **The local-dispatch channel is in-process and not durable.** A crash between an HTTP response and
  its local dispatch loses that reply — covered by the saga's own state timeout, the same safety net
  that already covers a lost broker message on any other adapter.
- **Synchronous request/response serializes what is parallel fan-out on a broker-backed adapter.** Two
  `.Publish(...)` calls that would be two independent fire-and-forget broker publishes become two
  blocking HTTP round trips here. Both limitations are inherent to the synchronous delivery model this
  adapter deliberately chose, not gaps in this implementation of it.
- **`NackAsync(requeue: true)` is not honoured for a delivery that arrived as an inbound HTTP
  request** — it logs at error and drops, exactly as `requeue: false` does. See
  [Ack, nack, and requeue](#ack-nack-and-requeue) for why re-enqueuing a copy would be a fake rather
  than a redelivery. Every other delivery path on this adapter requeues for real; the saga's own state
  timeout is the net under this one, and the peer that POSTed the message is the party that can retry
  it.
- **A redelivered request is acknowledged, not answered.** Participant-side dedupe — the sample's
  `ParticipantService`, and any consumer that skips a repeated `MessageId` — acks a duplicate delivery
  without invoking its handler, which is correct on a broker because the original reply was already
  published. Over this adapter's synchronous request/response the handler is *also* what produces the
  response body, so a redelivered request returns `202` with no body and the calling saga gets nothing
  until its `RequestTimeout` expires and, eventually, its own state timeout rescues it. This is
  accepted deliberately rather than fixed: redeliveries are rare, and the alternative is making every
  participant cache and replay its replies.

## Unroutable-publish detection

A non-2xx or connection-level failure on the outbound POST is surfaced as
`MessageTransportPublishException`, matching the RabbitMQ adapter's own detection fidelity — at higher
fidelity than the Wolverine and Brighter adapters, whose underlying gateway packages have no
unroutable-return signal at all.

Options: [`../configuration.md#httptransportoptions-vsagatransporthttp`](../configuration.md#httptransportoptions-vsagatransporthttp).
Compose overlay: `docker-compose.http.yml` (splits the sample into separate Sagas/Participants
containers so local-subscription counting as a "route" doesn't collapse into one process).

## TypeScript

`@vsaga/transport-http` is wire-compatible with this adapter for Node participants — see
[`../typescript-participants.md`](../typescript-participants.md).

```ts
import { createHttpTransport } from '@vsaga/transport-http';
import { createVSagaRouter } from '@vsaga/express'; // or @vsaga/fastify, @vsaga/nestjs

const transport = createHttpTransport({
  serviceName: 'payments',
  endpoints: { orders: 'http://orders:8080' },
  routes: { ChargeCard: ['orders'] },
});

app.use(createVSagaRouter(transport)); // mounts the inbound receive endpoint
```
