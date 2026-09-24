# Design: HTTP-based sagas

**Status: both phases are built and live-verified.** Phase 1 (§4, `VSaga.Transport.Http`) and Phase 2
(§5.1's `ISagaContext.PublishAfterCommitAsync`, §5.2's `dotnet/src/VSaga.Http`/`.CallHttp(...)`, §5.3's Saga Map
fix, §5.4's `dotnet/tests/VSaga.Http.Tests` including the mutation-tested ordering proof) are all built, tested,
and live-verified — §5.1 in isolation on the existing RabbitMQ stack per its own instruction, then §5.2/
§5.3 together via a new `LoyaltyLookupSaga` calling a real, no-vSaga-awareness REST endpoint added to the
sample. See the README's "Transport adapter: HTTP" and "Outbound REST calls from a saga step: `.CallHttp`"
sections for both features' shipped shape and live-verification evidence — Phase 1's includes a genuine
cross-process deadlock found only by live `docker compose` traffic (never by the unit suite), a fourth
instance of "caught only by a live run," alongside the three in §3.

Written to be picked up cold in a later session: every claim about the current codebase carries a
`file:line` so it can be re-checked rather than trusted. Line numbers were accurate at commit
`93af87f` and will drift — re-grep rather than trusting them.

**§3 is the part to read first.** It is three constraints found by tracing the engine, each of which
would be a real defect if the obvious implementation were written instead. Two of them killed an
earlier draft of this design outright. The rest of the document only makes sense in their light.

---

## 1. What it is, and the two shapes it has to cover

vSaga can only move saga messages over a broker today. All five transport adapters
(`dotnet/src/VSaga.Transport.RabbitMQ`, `.InMemory`, `.Wolverine`, `.MassTransit`, `.Brighter`) either *are*
RabbitMQ or sit on top of it, so every saga in the repo assumes a durable queue, topic routing, and
fire-and-forget publish with the reply arriving later as its own message.

That rules out two integration shapes that are not the same problem:

1. **Two vSaga services talking without a broker.** Symmetric, both sides know the envelope, and the
   Service Map should keep working exactly as it does over RabbitMQ. This is a *transport* concern.
2. **A saga step calling an ordinary REST API** — a payment gateway, an internal service that was
   never going to grow a queue consumer. Asymmetric, the far side knows nothing about vSaga, and the
   mapping from status codes to saga messages has to be declared per call. This is a *DSL* concern,
   and it is **transport-agnostic** — a RabbitMQ-hosted saga wants it just as much.

Conflating them is the main trap in this design. They share the word HTTP and nothing else. §4 and §5
are deliberately independent: §5 depends on §4 for *ordering*, not for code.

### Decisions already taken

Asked and answered before this was written, so treat them as settled rather than reopening them:

- **Delivery model: synchronous request/response.** The HTTP *response body* becomes the reply message
  fed back into the saga; status codes map to success/failure events. (The async-webhook alternative —
  participant returns `202`, then POSTs its reply back later — was considered and deferred; see §7.)
- **Participants: vSaga-aware first, then arbitrary REST.** That is the §4-then-§5 order.
- **Sample hosting: convert `dotnet/samples/VSaga.Samples.OrderProcessing` to a web host**, rather than build
  a second sample alongside it. §4.6 extends this with a `Role` switch, and says why it has to.
- **The engine change is opt-in**, not a change to what every existing publish does. §5.1 argues that
  at length, because default-for-all is superficially the better idea.

---

## 2. What already exists that this builds on

Read these before designing anything new — the intent is to reuse the patterns, not invent parallel
ones.

| Capability | Where | Why it matters here |
|---|---|---|
| The single transport seam | `dotnet/src/VSaga.Abstractions/Transport/IMessageTransport.cs:8-32` | Four methods. A new adapter implements exactly these; Core never learns HTTP exists |
| Middleware decorator | `dotnet/src/VSaga.Transport.Common/MiddlewarePipelineTransport.cs:13-71` | Wrapping in it is what keeps `VSaga.Chaos` working over any new transport unchanged |
| Canonical adapter registration | `dotnet/src/VSaga.Transport.RabbitMQ/ServiceCollectionExtensions.cs:16-31` | The factory-lambda shape every adapter copies — and it is mandatory, see §3.3 |
| Topology recording | `dotnet/src/VSaga.Abstractions/Transport/TopologyRecordingTransport.cs:11-27` | Observes `SubscribeAsync` only, so it keeps working over HTTP for free — with one deployment trap, §4.6 |
| Local-dispatch registry | `dotnet/src/VSaga.Transport.InMemory/InMemoryMessageTransport.cs:58-87` | The subscriber-matching logic an HTTP adapter needs for its own local half |
| Public DSL extension point | `dotnet/src/VSaga.Core/Dsl/EventBuilder.cs:54-58` | `Then(Func<…, Task>)` is the only seam an outside assembly can attach a step action through, §5.2 |
| Envelope headers | `dotnet/src/VSaga.Abstractions/Transport/MessageEnvelope.cs:10-24` | Four well-known headers every adapter must round-trip byte-for-byte |
| Saga Map edge stitching | `dotnet/src/VSaga.Dashboard.Api/SagaMapBuilder.cs:151,286-287` | `outbound.MessageId → inbound.CausationId`. §5.3 is entirely about not breaking this |

---

## 3. Three constraints, each found by tracing the engine

### 3.1 A reply must never re-enter a saga while its own step is still running

`SagaOrchestrator.RunStepAsync` (`dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs:371-418`) runs every step
action — including every `ctx.PublishAsync` — and only *then* persists, with an optimistic-concurrency
check against the version captured before the step ran:

```
expectedVersion = state.Version                                                  // :375
definition.HandleAsync(context, message, ct)                                     // :403
HandleStepSuccessAsync(...) -> PersistAsync(state, isNew, expectedVersion, ct)   // :471
```

So a message published from inside a step is on the wire **before** that step's state is committed. If
it comes back and is dispatched immediately, both writers hold `expectedVersion = V` and one loses its
`SagaConcurrencyException`. Which one loses is a coin flip — both do comparable database work.

**The loss is silent and permanent**, because the redelivery safety net is structurally disabled for
this class of failure. `RunStepAsync` commits the `MessageReceived` entry *before* running the step
(`:385`), `EfCoreSagaEventLogStore.AppendAsync` saves immediately, and `IsDuplicateAsync` matches on
exactly that entry — so `HandleInfrastructureFailureAsync`'s same-MessageId republish (`:80`, same id
*by design*, `:76-79`) is discarded as a duplicate at `HandleCoreAsync:331`. One `LogWarning` at `:67`
and one `LogDebug` at `:333` are the entire trace.

Concretely, per loser:

- **Outer step loses** — its `ctx.Saga.*` mutations are gone (the reply loaded a separate snapshot
  object at `V`), while its `StepSucceeded fromState→toState` entry is *already committed* (`:452`,
  before `PersistAsync` at `:471`). The timeline claims a transition the snapshot never took. With a
  `.Finalize(...)` in the step, a `SagaCompleted` entry (`:469`) is committed while the row stays
  `Running` — permanently inconsistent in the dashboard.
- **Reply loses** — it is dropped, and the saga sits until its state timeout. In `OrderSaga` that
  means `Compensate()` firing `RefundPayment` (`dotnet/samples/…/OrderSaga.cs:142-143`) for a payment that
  actually succeeded. The money-shaped outcome, on roughly half of all calls.

(Orphaned/duplicated `SagaTimeouts` rows also result, since `ScheduleAsync` appends rather than
upserts and `CancelAsync` filters on exact `ForState` — but both are absorbed by the state check at
`HandleTimeoutAsync:194` and the claim at `:205`. Cosmetic, not a hang.)

This is the same class as the two races already documented in the README's sub-saga sections, and the
same reason `.Publish(…).Publish(…)` chains in this DSL are sequential by construction. The difference
is severity of exposure: those races need an unlucky interleaving, whereas §5's `.CallHttp` addresses
the same saga instance on **every single call**.

**Answers differ per phase, and that is deliberate.** §4 solves it inside the transport with a
per-correlation dispatch gate and needs no engine change. §5 cannot — it loops its reply back through
*whatever* transport is configured, and RabbitMQ has no such gate — so §5 needs §5.1.

### 3.2 Sync-reply capture must key on routing, not on correlation id

For a `200` body to *be* the reply, `ParticipantService.ReplyAsync`'s ordinary
`transport.PublishAsync` (`dotnet/samples/…/Participants/ParticipantService.cs:78-79`) has to be intercepted
while its inbound request is still in flight. `IMessageTransport.PublishAsync` has no context
parameter and `ReceivedMessage` has no reply affordance, so an ambient (`AsyncLocal`) collector
installed by the receive endpoint is the only available seam. That part is forced, not chosen.

What is chosen is the predicate for "is this the reply?", and the obvious one is **wrong**. Keying on
"shares the in-flight request's correlation id" is broken by this repo's own sample:
`dotnet/samples/…/OrderSaga.cs:94` and `:102` publish `ShipOrder` **under the saga's own correlation id**,
from inside the `InventoryReserved` / `PaymentCharged` handlers. Correlation-based capture returns
`ShipOrder` as the response body to the *payment service*, which has no idea what it is. Shipping
never hears about it, and the order hangs to its 30s timeout and then compensates a successful
payment.

Key on **"this message resolves to no destination"** instead. Every case then falls out correctly:

| case | behaviour |
|---|---|
| handler publishes one unroutable message | `200` + that message |
| handler publishes nothing (the deliberately hung gateway in `PaymentParticipant`) | `202`; the saga waits for its own timeout — behaviour preserved exactly |
| a second unroutable message | throws `MessageTransportPublishException(isUnroutable: true)` → the participant's own catch → nack. Loud, not silently swallowed |
| `ShipOrder` (has a route) | normal outbound POST |
| `StartChildAsync` | fresh correlation id (`dotnet/src/VSaga.Core/Runtime/SagaContext.cs:61`) *and* a routed type — correct on both counts |
| `NotifyParentAsync`, engine-published `ChildSagaFinished` | parent's correlation id, routed type → normal POST |

The collector must be an **instance** field, never `static`, and must be **sealed** once the response
is written, with anything published afterwards falling through to a real POST — a handler that does
`_ = Task.Run(…)` inherits the `AsyncLocal` and would otherwise write into a completed collector.

### 3.3 Local subscriptions are part of the route table, and inbound headers are case-insensitive

Two ways an HTTP adapter can look correct and quietly break the engine.

**(a) Redelivery.** `HandleInfrastructureFailureAsync` recovers by calling
`transport.PublishRawAsync(received.MessageTypeName, …)` (`SagaOrchestrator.cs:80`) — republishing an
*inbound* type such as `PaymentCharged`, which nobody ever publishes deliberately. On RabbitMQ that
works because the saga's own queue is bound to that routing key
(`dotnet/src/VSaga.Transport.RabbitMQ/RabbitMqTransport.cs:130-134`): **local subscriptions are part of
RabbitMQ's routing table.** An HTTP route table built only from config has no entry, a "no route ⇒
unroutable" rule fires, and that exception is *designed to propagate* (`SagaOrchestrator.cs:56-59`)
into a dispatch-level catch — which RabbitMQ has (`RabbitMqTransport.cs:156-163`, log + nack without
requeue) and a naive HTTP adapter would not. Message vanishes; saga stuck.

So: resolve `PublishAsync`/`PublishRawAsync` to the **union** of every locally-registered
`SubscribeAsync` type (the same match `InMemoryMessageTransport.DispatchAsync:74` does) and every
configured remote route; unroutable only when **both** are empty. And mirror RabbitMQ's
dispatch-level catch.

**(b) `x-vsaga-delivery-attempt`.** `GetDeliveryAttempt` does an **ordinal** lookup
(`SagaOrchestrator.cs:120-124`) and every adapter builds its inbound header dictionary with
`StringComparer.Ordinal` (`RabbitMqTransport.cs:214`). HTTP header names are case-insensitive on the
wire and any proxy may normalize them. Copy that pattern and the counter reads `0` forever, so
`attempt < MaxDeliveryAttempts` is always true, and a message whose failure precedes its
`MessageReceived` entry — a deserialize failure at `:304`, which leaves no dedupe entry to stop it —
**republishes forever**. Build the *inbound* dictionary with `OrdinalIgnoreCase`. One character.

This is the third instance in this repo of the same scar: envelope-header data threaded through the
wire that the orchestrator never actually reads back correctly. The first two (`SourceService`, then
`CausationId`) were both caught only by live `docker compose` verification, never by tests that
hand-built the objects under test. See §6.

Related: `MessageEnvelope.Headers` is an open `IReadOnlyDictionary<string,string>` a saga author can
put anything into. RabbitMQ does not care; HTTP header injection does. Reject CR/LF on write.

---

## 4. Phase 1 — `dotnet/src/VSaga.Transport.Http`

The vSaga-aware, symmetric half. Follows the five-part shape every existing adapter uses, referencing
**`VSaga.Abstractions` + `VSaga.Transport.Common` only** — never a sibling adapter, per the rationale
in commit `c413c4b` that moved `MiddlewarePipelineTransport` into `Transport.Common` in the first
place.

**No DSL change at all in this phase.** `.Publish(…)`/`.Send(…)` already say everything the transport
needs, and `ParticipantService` runs unmodified. That is the point of doing this half first.

Project is a plain `Microsoft.NET.Sdk` library with
`<FrameworkReference Include="Microsoft.AspNetCore.App" />` — `MapVSagaHttp()` needs
`IEndpointRouteBuilder`, and the framework reference also brings `Microsoft.Extensions.Http`, so **no
new `Directory.Packages.props` entry is needed for this phase**. Do not split into a separate
`.AspNetCore` package: the adapter is useless without a receive endpoint, so the split buys nothing.

### 4.1 Files

| File | Role |
|---|---|
| `HttpTransportOptions.cs` | `ServiceName`, `Endpoints` (name → base URL), `Routes` (message type name → endpoint names), `RequestTimeout`, `InboundPath` (default `/vsaga/messages`) |
| `IHttpRouteTable.cs` + `ConfigHttpRouteTable.cs` | Resolve a type name / destination name to targets. `TryAddSingleton`, so it is user-overridable — mirrors `IRoutingKeyConvention` |
| `HttpMessageTransport.cs` | The `IMessageTransport` implementation |
| `HttpInboundDispatcher.cs` | Subscriber registry + per-correlation dispatch gate + reply pump |
| `VSagaHttpEndpointExtensions.cs` | `app.MapVSagaHttp()`; returns the `RouteHandlerBuilder` so callers can chain `.RequireAuthorization()` — vSaga ships no auth opinion |
| `ServiceCollectionExtensions.cs` | `AddVSagaHttp(services, Action<HttpTransportOptions>?)` |

### 4.2 Wire format

Header-based, so the body is exactly the message JSON and nothing else — which is what makes §5's
brownfield case a small step rather than a rewrite.

```
POST {baseUrl}/vsaga/messages
Content-Type: application/json
x-vsaga-message-type: ChargePayment
x-vsaga-correlation-id: 7f3a…
x-vsaga-message-id: 9c21…
x-vsaga-source-service: OrderSaga
x-vsaga-causation-id: 4b90…
[+ x-vsaga-parent-saga-type / -parent-correlation-id / -delivery-attempt when present]

{"correlationId":"…","orderId":"…","amount":42}
```

Reuse the three header names `RabbitMqTransport.cs:23-25` already defines plus the four well-known
envelope headers (`MessageEnvelope.cs:10-24`). All of them round-trip byte-for-byte — that is what the
Saga Service Map depends on.

Responses: `200` + the **same full header set** + a reply body → a reply message. `202` + empty body →
accepted, no synchronous reply. `4xx`/`5xx` → publish failure.

Note that `x-vsaga-message-type` has no home in `MessageEnvelope` (RabbitMQ carries it as its own
header, `RabbitMqTransport.cs:25`). The **response** path is exactly where that gets forgotten, and
forgetting it silently breaks the map. Dedicated test.

### 4.3 Routing

```json
"Http": {
  "ServiceName": "order-processing",
  "Endpoints": { "payments": "http://payments:8080", "shipping": "http://shipping:8080" },
  "Routes": { "ChargePayment": ["payments"], "ShipOrder": ["shipping"] }
}
```

- `PublishAsync` → POST to **every** target for that type; the topic fan-out analogue. This is what
  keeps choreography working: `OrderShipped` has to reach `PostShipmentChoreography` *and* its three
  participants.
- `SendAsync(destination, …)` → resolve `destination` as an endpoint name directly, bypassing
  `Routes` — the AMQP default-exchange analogue.
- Targets = configured remote routes **∪ local subscribers** (§3.3a). Unroutable ⇒ throw
  `MessageTransportPublishException(…, isUnroutable: true, …)`.

**Status note: a fourth rule was added after this was written, so the three above are no longer
exhaustive.** `Routes` now honours a **wildcard key**, `ConfigHttpRouteTable.WildcardRoute` (`"*"`, in
`dotnet/src/VSaga.Transport.Http/IHttpRouteTable.cs`): a `PublishAsync`/`PublishRawAsync` for a type with
no explicit `Routes` entry falls back to the wildcard's endpoints before "unroutable" is concluded. Its
rationale is recorded on `HttpTransportOptions.Routes` — a dashboard/ops process that only ever redrives
messages toward a single saga host has no reason to enumerate every message type that host understands —
so it exists to serve §4.6's Dashboard.Api manual-retry fix rather than to relax routing generally. Pinned
by `Publish_WithNoExplicitRouteForTheType_FallsBackToTheWildcardEndpoint` in
`dotnet/tests/VSaga.Transport.Http.Tests/HttpTransportFailureTests.cs`.

Worth noting this is *higher* fidelity than the Wolverine and Brighter adapters, whose tests assert
the verified *absence* of an unroutable signal. `README.md:127` currently presents unroutable-publish
detection as a RabbitMQ-specific property and will need correcting.

### 4.4 Dispatch

`HttpInboundDispatcher` owns the subscriber registry (type name → handler, populated by
`SubscribeAsync`) and a per-correlation-id async gate. Two entry points, and the asymmetry between
them is the whole §3.1 answer:

- **Inbound HTTP request** → dispatched **inline**, holding the gate. Inline is *required*: the
  handler's reply has to be captured before the response is written.
- **A `200` reply to our own outbound POST** → **never inline.** Enqueued to a `Channel`, drained by a
  background pump that takes the same gate. The publishing step is itself running inside a gated
  dispatch, so the reply waits for it to finish — i.e. until after `PersistAsync` and after the ack,
  since a dispatch returning *is* `HandleAsync` completing.

Both paths need §3.3a's dispatch-level catch.

**§4.4a (found live, not anticipated in this design): a fan-out reply that routes back to its own
originating service can deadlock that service's gate against itself.** `OrderShipped` has to reach both
its local participants and back to the saga host (this section's own fan-out example) — but when the
saga host's own dispatch (say, handling `PaymentCharged`) is still holding its correlation gate while
awaiting `ShipOrder`'s HTTP response, and the participant's reply to `ShipOrder` is `OrderShipped`
routing back to that same saga host, the inbound `OrderShipped` request cannot acquire the very gate the
outbound `ShipOrder` call is waiting behind — a genuine cross-process circular wait, resolved only by
`ShipOrder`'s own `RequestTimeout` expiring. Live traffic showed this on effectively every order that
reached shipping. Fixed by bounding the inline path's gate acquisition (`InlineGateAcquireTimeout`, 5s
default) and falling back to the same deferred (enqueue-to-pump) path a reply already uses on timeout —
lossless, just delayed, and self-healing once the holder's own step finishes. See README.md's Transport
adapter: HTTP section for the live evidence.

**Status note: "`InlineGateAcquireTimeout`, 5s default" reads as a configurable knob. It is not one.** It
shipped as a fixed `private static readonly TimeSpan` on `HttpInboundDispatcher` and is deliberately
**absent from `HttpTransportOptions`**, so there is no way to tune it per deployment — "default" here
means "the value", not "the value unless you override it".
[`docs/transports/http.md`](../transports/http.md) already states this correctly; only this design record
reads as though the value were exposed.

Ack model, with no broker underneath: `AckAsync` → drop; `NackAsync(requeue: true)` → re-enqueue;
`NackAsync(requeue: false)` → log at error and drop. **No `IHttpDeadLetterSink` abstraction** — an
interface with one logging implementation is ceremony; add it when a second implementation exists.

**Status note: this ack model went unimplemented for a long time, and was addressed later.** Phase 1
shipped without it — every `ReceivedMessage` the HTTP adapter constructed carried a no-op ack context, so
`NackAsync(requeue: true)` did not re-enqueue anything and `NackAsync(requeue: false)` logged nothing.
The paragraph above therefore described intent, not behaviour, for the whole period between Phase 1 and
the later pass that took it up. **This design record is not the authority on where that landed** — the
adapter has more than one place a `ReceivedMessage` is constructed and not all of them have somewhere to
re-enqueue to. See [`docs/transports/http.md`](../transports/http.md) for the current ack/nack behaviour
and for which paths actually honour `requeue: true`.

The channel is **in-process and not durable**, and the README must say so rather than implying
at-least-once: a crash between an HTTP response and its dispatch loses that reply, and the saga's
state timeout is what covers it — the same safety net that already covers a lost broker message
(`dotnet/samples/…/OrderSaga.cs:151-171`). A durable inbox is a deliberate non-goal.

### 4.5 Registration

Copy `dotnet/src/VSaga.Transport.RabbitMQ/ServiceCollectionExtensions.cs:16-31`. Registering
`IMessageTransport` via a **factory lambda** is mandatory rather than stylistic:
`AddVSagaTopologyRecording` throws when the last `IMessageTransport` descriptor has a null
`ImplementationFactory` (`dotnet/src/VSaga.Core/TopologyRecordingServiceCollectionExtensions.cs:23-31`).
Wrapping in `MiddlewarePipelineTransport` is what keeps `VSaga.Chaos` working unchanged.

### 4.6 Sample, infrastructure, dashboard

**Web host.** `VSaga.Samples.OrderProcessing.csproj` `Sdk="Microsoft.NET.Sdk.Worker"` →
`Microsoft.NET.Sdk.Web`; `Host.CreateApplicationBuilder` → `WebApplication.CreateBuilder`;
`app.MapVSagaHttp()`; `case "Http":` added to the provider switch (`Program.cs:33-52`). Everything
else — chaos, topology recording, OpenTelemetry, `AddVSagaEngine`, the participant hosted services —
untouched, and the other four transports keep behaving exactly as today. The runtime image is already
`mcr.microsoft.com/dotnet/aspnet:10.0` (`dotnet/samples/…/Dockerfile:23`), so the Dockerfile needs only the
new `.csproj` COPY line — miss it and `docker compose up` fails with `NETSDK1004`. Set
`ASPNETCORE_URLS: http://+:8080` in compose, matching `dashboard-api`; **do not** add host `ports:` to
`order-processing` in the base file, since the `!override` convention
(`docker-compose.wolverine.yml:14-24`) would then force edits to all four existing overlays.

**A `Role` switch, and why it is not optional.** Because local subscribers count as routes (§3.3a), a
*single-process* sample over the HTTP transport resolves everything locally and performs **zero
HTTP** — the compose run would pass while exercising nothing. Cheapest fix that preserves the
one-sample-for-all-transports property: one image, a `Role` config key
(`All` (default) | `Sagas` | `Participants`) gating which `AddHostedService` calls run, and two
services in the HTTP overlay. `Role` is unset on every other track, so RabbitMQ, Wolverine,
MassTransit and Brighter runs stay bit-for-bit what they are today.

The participants container needs `AddVSagaEfCore` **and** `AddVSagaTopologyRecording`, or
`IServiceTopologyStore` silently falls back to `NullServiceTopologyStore`
(`TopologyRecordingServiceCollectionExtensions.cs:21`) and every participant node in the map degrades
to `unresolved:{MessageType}`, rendered as `?` (`SagaMapBuilder.cs:211,221`).

**`docker-compose.http.yml` cannot be a pure overlay.** The base file wires
`order-processing.depends_on: {rabbitmq, dashboard-api}` (`docker-compose.yml:60-69`) and
`dashboard-api.depends_on.rabbitmq` (`:44-45`), and Compose can override a `depends_on` entry but not
delete a key. Keep the rabbitmq container in the HTTP stack — it costs nothing, keeps
`dashboard-api`'s health check green, and the point is proven by *traffic*, not by absence (§6). Use
`ports: !override [...]` on fresh host ports; the existing overlays hold 5443/5444/5445.

**Dashboard manual retry, fixed in the same pass.** `dotnet/src/VSaga.Dashboard.Api/Program.cs:46` hardcodes
`AddVSagaRabbitMq` and its csproj references only that adapter, so `/retry`'s `PublishRawAsync`
(`Endpoints/SagaEndpoints.cs:157`) publishes into a RabbitMQ nobody is consuming: `200 OK`, nothing
happens. This is a *pre-existing* gap — it already misbehaves on Wolverine and MassTransit, whose wire
formats differ — but HTTP is the first configuration where it is silently wrong on a path this repo
live-verifies. Roughly fifteen lines: the same `Transport:Provider` switch, the HTTP adapter
reference, a route pointing at the saga host, and a health check that is not unconditionally RabbitMQ.

**`dotnet/VSaga.slnx`**: add both `dotnet/src/VSaga.Transport.Http` and `dotnet/tests/VSaga.Transport.Http.Tests`. CI runs
`dotnet test dotnet/VSaga.slnx` unfiltered (`.github/workflows/ci.yml:28`) and picks them up automatically.

### 4.7 Tests — `dotnet/tests/VSaga.Transport.Http.Tests`

No Testcontainers: `Microsoft.AspNetCore.Mvc.Testing` is already pinned at 10.0.11 in
`Directory.Packages.props`, making this the fastest transport suite in the repo. Keep the four
canonical names so the family still reads as one:

1. `PublishAndSubscribe_DeliversMessageWithCorrelationAndType`
2. `Send_DeliversDirectlyToNamedQueueWithoutExchange`
3. `Publish_ToUnroutedMessageType_ThrowsUnroutablePublishException`
4. `PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged`

Plus five specific to this adapter, each pinning one constraint from §3:

5. `SyncReply_ResponsePathCarriesFullEnvelopeIncludingMessageType` — §4.2
6. `SyncReply_IsNotDispatchedInlineDuringThePublishingStep` — §3.1
7. `Publish_OfLocallySubscribedType_ReEntersLocalSubscriber` — §3.3a, the redelivery path
8. `DeliveryAttemptHeader_SurvivesACaseNormalizingRoundTrip` — §3.3b
9. `Publish_OfRoutedTypeFromInsideAHandler_IsNotCapturedAsTheSyncReply` — §3.2, the `ShipOrder` case

---

## 5. Phase 2 — after-commit publish, then `.CallHttp(...)`

### 5.1 Step 2a — the engine change, landed and verified on its own

Ship this **before any `.CallHttp` code exists**, and live-verify it on the **existing RabbitMQ
stack** — zero new infrastructure variables, so a regression has exactly one candidate cause. This is
the riskiest item in the whole design and it should not be debugged alongside a brand-new transport.

`ISagaContext.PublishAfterCommitAsync<TMessage>(…)` as a **C# default interface method** whose default
body is `PublishAsync(…)`; `SagaContext` overrides it to queue; `SagaOrchestrator`
`HandleStepSuccessAsync` drains **after** `PersistAsync` (`:471`), **sequentially** — never
`Task.WhenAll`, per the `DbContext` scar documented in the README's fan-out section — reaching the
queue through an internal cast exactly like the existing `ISagaContextLogSink` precedent
(`dotnet/src/VSaga.Core/Runtime/SagaContext.cs:12,85`). `SagaContext` is the only implementer of
`ISagaContext<>` in the repo, so the default body exists purely for external compatibility.

**Opt-in, not default-for-all.** Default-for-all is superficially better — it would retire both
documented Slice 2a/2b races outright and make `InMemoryMessageTransport` behave like a broker — but
one consequence disqualifies it: **a publish that throws after commit has nowhere to go.** Today a
failing `ctx.PublishAsync` throws inside `definition.HandleAsync` → `HandleStepFailureAsync`
(`:409,420-440`) → saga `Failed`, `StepFailed` with `payloadJson`, dashboard can redrive. Deferred,
the step has already succeeded and persisted, so a drain failure lands in `HandleAsync`'s catch →
redelivery → **suppressed as duplicate** (§3.1) → message permanently lost. Applied to every publish,
that includes `Compensate()`'s `RefundPayment`/`ReleaseInventory` (`dotnet/samples/…/OrderSaga.cs:140-149`),
and losing a compensating refund after the saga is already persisted `Failed` is strictly worse than
today's behaviour.

Secondary reasons, any one of which would want its own verification pass: `.Retry(policy)` silently
stops covering publish failures (`dotnet/src/VSaga.Core/Dsl/StepExecutor.cs:24-33`); and
`SagaContext.cs:96-99` logs the timeline entry only *after* the transport call succeeds, so deferral
reorders every `MessagePublished` after its `StepSucceeded`, which perturbs `SagaMapBuilder`'s
order-sensitive `ResolveFailedMessageIds` heuristic (`:267-271`) and `_failureEventIndex`
(`:122-123`). `HandleTimeoutAsync` would also need its own parallel drain after `:235`.

Make the drain-failure policy explicit rather than implicit: catch, `LogError`, append a
`DeliveryExhausted` entry, leave the saga `Running` so its state timeout rescues it. Document it
instead of pretending it cannot happen.

Regression proof: the whole existing suite, and specifically the two pinned race tests
(`NotifyParentAsyncTests`, `ChildSagaFinishedTests` in `dotnet/tests/VSaga.Core.Tests/`) must be unchanged
and green. The change is opt-in, so nothing existing may move.

### 5.2 Step 2b — `dotnet/src/VSaga.Http`, and how it attaches to the DSL

Target shape:

```csharp
During(Gathering)
    .When<OrderPlaced>()
        .CallHttp(h => h
            .Post("https://payments/charge")
            .Body((ctx, m) => new { m.OrderId, m.Amount })
            .OnSuccess<PaymentCharged>()
            .OnStatus(402).As<PaymentDeclined>()
            .OnFailure<PaymentFailed>())
        .TransitionTo(Gathering);
```

`EventBuilder<TState,TMessage>` is `public sealed` with a `private` `_step`
(`dotnet/src/VSaga.Core/Dsl/EventBuilder.cs:12,16`); `StepDefinition<TState>` and `SagaDefinitionModel<TState>`
are `internal sealed`; and `StateBuilder.Model` is `private protected` with an `internal` constructor
(`dotnet/src/VSaga.Core/Dsl/StateBuilder.cs:8-11`) — so an outside assembly can neither subclass the builder
nor reach the model. The only route in is an **extension method delegating to the public
`Then(Func<ISagaContext<TState>, TMessage, Task>)`** (`EventBuilder.cs:54-58`). Two consequences:

- It is **transport-agnostic** and needs no change to `VSaga.Core`'s DSL, keeping Core free of an
  `HttpClient` dependency. A RabbitMQ-hosted saga gets `.CallHttp` for free.
- It needs `<InternalsVisibleTo Include="VSaga.Http" />` in `VSaga.Core.csproj` for §5.3. There is
  precedent (`dotnet/src/VSaga.Dashboard.Api/VSaga.Dashboard.Api.csproj:7`), so this is repo-consistent
  rather than a novelty.

`VSaga.Http` needs a real `Microsoft.Extensions.Http` `PackageVersion` in `Directory.Packages.props` —
central package management is on (`ManagePackageVersionsCentrally=true`) and a missing entry is a
guaranteed build break. It must **not** take the `Microsoft.AspNetCore.App` framework reference; that
belongs to the transport only.

**Two result shapes, both supported.**

- *Inline* — `.OnSuccess(s => s.X = …)`, `.OnStatus(402).TransitionTo(Declined)`. A synchronous call
  already has the answer; no loopback, no race, no map problem. Recommend this as the default in docs.
- *Message loopback* — `.OnSuccess<PaymentCharged>()`, as sketched above. Publishes the mapped message
  via **`PublishAfterCommitAsync`**, never `PublishAsync`.

**`.Retry()` is a trap here and must be documented as one.** `StepExecutor.RunAsync` replays **all**
`step.Actions` from index 0 on any throw (`StepExecutor.cs:24-33`), so
`.Publish(x).CallHttp(charge).Retry(policy)` re-publishes `x` and **re-POSTs the charge** on every
attempt. Since `.CallHttp` is exactly the thing people will reach for `.Retry()` with, it needs its
own internal retry knob and a loud note that `.Retry()` is not the right tool.

### 5.3 The Saga Map needs an explicit fix, and this is what `InternalsVisibleTo` is for

`SagaMapBuilder` stitches an outbound entry to its reply by `outbound.MessageId → inbound.CausationId`
(`dotnet/src/VSaga.Dashboard.Api/SagaMapBuilder.cs:151`, index built at `:286-287`). A naive loopback via
`ctx.PublishAsync` stamps `causationId = inboundMessageId` (`SagaContext.cs:106-108`) — the id of the
message that triggered the step, **not** the outbound publish's own MessageId. So:

- the stitch at `:151` misses;
- the outbound falls through to `ResolveUnstitchedDestinations`, resolves by type, finds the saga host
  as its own registered consumer, and renders as a bogus **unanswered self-loop**;
- the inbound reply has `sourceService == _orchestratorId`, so `ProcessInboundEntry:131` produces no
  edge at all;
- **the REST endpoint that was actually called never appears as a node.**

The map is a headline feature of this repo. `.CallHttp` must therefore write its own timeline entries
naming the HTTP host as the service — `destinationService` on the outbound, `sourceService` plus the
correct `causationId` on the reply — which needs the internal `ISagaContextLogSink`
(`SagaContext.cs:12`).

### 5.4 Phase 2 tests

- Mapping table: 2xx → success message; explicit status → its mapped message; 5xx → failure message;
  timeout / `HttpRequestException` → failure message.
- The reply reaches the saga and drives the expected transition, through `VSaga.Testing`'s harness.
  Note this works *only because of* `PublishAfterCommitAsync`: `SagaTestHarness` runs on
  `InMemoryMessageTransport` (`dotnet/src/VSaga.Testing/SagaTestHarness.cs:53`), which dispatches
  synchronously and re-entrantly (`InMemoryMessageTransport.cs:85`), so a plain `PublishAsync`
  loopback re-enters the saga mid-step on every single test.
- Saga Map: a `.CallHttp` step produces a node for the remote host and a stitched request→reply edge,
  not an unanswered self-loop.
- **Mutation-test the ordering fix** — swap `PublishAfterCommitAsync` back to `PublishAsync`, confirm
  exactly the concurrency test fails, restore. This repo's established habit, and the reason the test
  is worth having at all.

---

## 6. Verification

```
dotnet build dotnet/VSaga.slnx
dotnet test dotnet/VSaga.slnx --filter "FullyQualifiedName~Transport.Http"
dotnet test dotnet/VSaga.slnx        # full suite green at every phase boundary
```

Live verification is **not optional in this repo**. Two separate passes have shipped envelope-header
threading the orchestrator never actually read, both caught only by a live run and neither by tests
that hand-built the objects under test. §3.3b is the third instance of the same class.

**Phase 2a**, on the *existing* stack, before any HTTP code exists — `docker compose up --build`, full
suite green, sub-saga behaviour unchanged.

**Phases 1 and 2b**:

```
docker compose -p vsaga-http -f docker-compose.yml -f docker-compose.http.yml up --build
```

Then, against the running stack rather than test doubles:

1. Orders complete end to end. The proof that the broker is out of the message path is **traffic, not
   absence** — the rabbitmq container stays up for `dashboard-api`'s health check (§4.6), so check its
   management UI shows **no `vsaga.saga.*` queues and zero throughput** while orders flow.
2. `curl` the saga-detail endpoint and check the **Map** tab: nodes named as services, edges stitched
   request→reply. This is the direct test of whether `x-vsaga-source-service` and
   `x-vsaga-causation-id` survived the HTTP hop — the exact pair that broke before — and of whether
   the participants container got its own topology recording (otherwise every participant node
   renders `?`).
3. Timeline entries show `MessagePublished`/`MessageReceived` in the same shape as a RabbitMQ run.
4. Exercise the dashboard's **manual retry** on a Failed saga and confirm it actually redrives; the
   §4.6 Dashboard.Api fix is only proven this way.
5. Re-run with `-f docker-compose.chaos.yml` to confirm `MiddlewarePipelineTransport` still applies
   chaos middleware over HTTP, and that timeouts and compensation fire on dropped deliveries.
6. Filter every count by `createdAtUtc` after the containers' start time — the Postgres volume is
   reused across `docker compose up`, and stale sagas otherwise pollute the numbers.

---

## 7. Known semantic changes, and open questions

State these in the README when the work ships, rather than discovering them live.

1. **Sync request/response serializes the parallel fan-out.** `dotnet/samples/…/OrderSaga.cs:58-59` publishes
   `ReserveInventory` then `ChargePayment` as two sequential actions in one step. Over RabbitMQ those
   are two fire-and-forget publishes; over sync HTTP they become two **blocking** round-trips, each
   including the participant's 150–500ms simulated work. The sample's headline "parallel fan-out"
   demo is strictly sequential on the HTTP track, and its own doc comment (`OrderSaga.cs:20-26`) needs
   a caveat. Inherent to the chosen delivery model, not a bug.

   **Status: documented.** The caveat is now on `OrderSaga`'s own class doc comment
   (`dotnet/samples/VSaga.Samples.OrderProcessing/OrderSaga.cs`), and the substance is in
   [`docs/transports/http.md`](../transports/http.md)'s "Known, deliberate limitations".
2. **`ParticipantService`'s dedupe changes meaning.** `TryClaim`
   (`dotnet/samples/…/Participants/ParticipantService.cs:55-60,83-93`) acks a repeated MessageId *without*
   invoking the handler. Over RabbitMQ that is right — the original reply was already published. Over
   sync HTTP a redelivered request returns `202` with no body and the caller gets nothing until its
   timeout. Recommendation: accept it (redeliveries are rare) and document it, rather than having
   participants cache replies.

   **Status: still undocumented — this section's own "state these in the README when the work ships"
   instruction was not carried out for this item.** The *accept it* half happened (the behaviour is
   unchanged, and still correct: `ParticipantService.HandleAsync` acks a repeated `MessageId` without
   invoking the handler, so no reply is published, so the HTTP adapter's sync-reply collector finds
   nothing unroutable to return). The *document it* half did not: nothing in
   [`docs/transports/http.md`](../transports/http.md),
   [`docs/transports/index.md`](../transports/index.md), or `ParticipantService.cs` itself mentions that
   the dedupe's meaning changes on the HTTP track. Items 1, 4 and 6 of this list did get documented;
   this one is the outstanding one. It belongs in `docs/transports/http.md`'s "Known, deliberate
   limitations", not here.
3. **Async webhook delivery is deferred, not rejected.** Participant returns `202`, then POSTs its
   reply back later as its own inbound request. The §4.2 wire format already leaves room — `202` is a
   defined response — but nothing implements the return leg. The natural third phase, and the one that
   would restore true parallel fan-out (item 1).
4. **Durable HTTP inbox/outbox — open.** §4.4's channel plus the saga's state timeout is the story for
   now. A durable inbox would change the honest claim from "best-effort, timeout-covered" to
   at-least-once. Not scoped here.
5. **Making after-commit publish the default — open, argued against in §5.1.** Revisit only with its
   own live chaos-verification pass. If it were ever done, it would retire both documented Slice
   2a/2b races, which is the one genuine argument for it.
6. **Auth on the receive endpoint — deliberately not vSaga's opinion.** `MapVSagaHttp` returns the
   `RouteHandlerBuilder`; callers chain `.RequireAuthorization()`.
7. **Mixed sagas — a saga that also drives RabbitMQ participants alongside `.CallHttp` — are a separate
   design**, not this document's "natural third phase" (item 3, async webhook delivery, is still that
   slot). See [`docs/mixed-sagas.md`](mixed-sagas.md): it needs its own engine change (draining
   `PublishAfterCommitAsync` on the timeout path, currently missing) and its own DSL addition
   (`ctx.CallHttpAsync`, for compensation delegates and timeout steps, where `.CallHttp` cannot reach).
