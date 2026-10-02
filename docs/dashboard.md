# Dashboard

`VSaga.Dashboard.Api` (ASP.NET Core Minimal API + SignalR) and `dashboard-web` (Angular 21 SPA)
together form a saga-type-agnostic ops dashboard: list/filter/search every saga instance
across every registered saga type, drill into one instance's timeline or a visual service map, and
manually retry a failed or timed-out saga — all against provider-neutral contracts every persistence
provider implements (`ISagaSummaryReader`, `ISagaEventLogStore`, `ISagaAdminStore`,
`IServiceTopologyStore`), plus a raw `IMessageTransport` republish for retry, so the dashboard needs
no knowledge of any specific saga definition.

## API endpoints

All routes below require authentication (see [Authentication](#authentication)) except `/health` and
the Development-only `/openapi/v1.json`.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/sagas` | Paginated, filterable saga list. Query params: `status`, `sagaType`, `kind`, `search`, `page` (default 1), `pageSize` (default 25, clamped to a maximum of 500 — the response's `pageSize` reports the size applied), `sortBy` (`UpdatedAt` or `Status`), `sortDescending`. `400` under `Persistence:Provider=Redis` when a `search` would scan more index members than `Redis:MaxSearchScanMembers` allows — narrow it with a `sagaType`/`status`/`kind` filter; see [`persistence.md`](persistence.md#search). |
| `GET` | `/api/sagas/{sagaType}/{correlationId}` | One instance's summary plus its raw state `DataJson`. `404` if not found. |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/timeline` | The full, ordered `SagaLogEntry` history for one instance. |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/map` | The Saga Map for one instance — see [below](#saga-map). `404` if not found. |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/children` | Every saga this instance started via `StartChildAsync`. Empty (not `404`) for both "no children" and "no such saga" — the caller already has the plain `GET` above to tell those apart. |
| `POST` | `/api/sagas/{sagaType}/{correlationId}/retry` | Manually redrives a `Failed`/`TimedOut` instance — see [Manual retry](#manual-retry). `202` once the redrive is published, `404` if no such instance, `409` for any other status, or if the saga changed concurrently while a business-failure/timeout retry was resetting it to its initial state (reload and try again), `422` if the timeline has neither a `StepFailed` entry nor a usable `SagaStarted` one to redrive from, `502` if the transport republish itself fails. |
| `GET` | `/api/saga-types` | Every distinct saga type currently known to the store, for populating filter dropdowns. |
| `GET` | `/api/correlations/{correlationId}` | Every saga instance — of any type — currently tracking this correlation id. The one route that still takes a bare correlation id, since two saga types (an orchestrated one and a choreography observing the same transaction — see [`concepts.md`](concepts.md#saga-instances-and-identity)) may both track it. Does **not** include sub-saga children, which have their own correlation ids and are reached via `/children` instead. |
| `GET` | `/health` | Unauthenticated. Two real connectivity checks, `persistence` and `rabbitmq` — `503` with a per-check breakdown when either is unreachable, not a hardcoded `200`. `persistence` is named for the role, not the store: under `Persistence:Provider=Postgres` it is a `CanConnect` probe; under `Redis` it is the provider's own `RedisPersistenceHealthCheck`, which re-verifies `appendonly`, `maxmemory-policy`, cluster mode, the primary role, memory pressure, the schema marker, server-side Lua and the torn-write sentinel on every call and reports Unhealthy naming the guarantee (or that it could not be verified) — see [`persistence.md`](persistence.md#redis); under `MongoDb` it is `MongoPersistenceHealthCheck`, which re-verifies the replica-set topology, the primary, the server version, the indexes and the schema marker on every call, and reports any connection-string setting that contradicts the ones it pins — see [`persistence.md`](persistence.md#mongodb). The Postgres and RabbitMQ checks degrade to a pass when their dependency isn't registered at all ("No relational database configured." / "No message broker configured."), so under `Transport:Provider=Http` — where no RabbitMQ connection manager is registered — the broker check is an unconditional pass rather than a real probe. |
| `GET` | `/openapi/v1.json` | Unauthenticated, and mapped only in the `Development` environment (which `dotnet run` gets from `launchSettings.json`; the compose containers don't set it). The generated OpenAPI document. |

Every per-instance route is keyed by `(sagaType, correlationId)`, not correlation id alone — see
[`concepts.md`](concepts.md#saga-instances-and-identity) for why.

### Manual retry

Two distinct redrive shapes, chosen automatically from the instance's own timeline:

1. **A technical failure** (an action threw) — the last `StepFailed` entry carries the exact message
   that failed; retry replays just that message against the saga's current, unchanged state.
2. **A business failure or timeout** (the saga reached `Failed`/`TimedOut` through a normal,
   successful step transition, or a timeout, with no `StepFailed` entry at all) — retry resets the
   saga back to its initial state and replays the message that originally started it.

The redrive republishes the original message with a **fresh message id** (so the duplicate check
doesn't discard it) under the **same correlation id**, via `IMessageTransport.PublishRawAsync` — the
dashboard never needs to know the saga's `TState` or definition; whichever process actually runs that
saga's engine picks the republish up through its own normal subscription. Because the republish is
still correlation-id-addressed, every saga type subscribed to that message type sees it, not only the
one being retried — the same fan-out an original delivery has.

**In-process retry, without the dashboard.** `AddVSagaEngine` also registers `ISagaRetryDispatcher`
(`VSaga.Core.Runtime`), whose `RetryAsync(sagaType, correlationId)` redrives an instance from a process
that runs its saga's engine. It implements shape 1 only, and runs the replayed step directly in that
process instead of republishing it. It accepts only a `Failed` instance, throwing
`SagaRetryNotAllowedException` otherwise (including for `TimedOut`); it throws `SagaNotFoundException`
for an unknown instance, and `InvalidOperationException` when there is no `StepFailed` entry to replay
or the saga type isn't registered in that process. `SagaTestHarness.RetryAsync` is a thin wrapper over
it (see [`testing.md`](testing.md)).

## Authentication

A single shared API key (`Dashboard:ApiKey` in configuration) — chosen over JWT/OIDC or basic auth as
the right fit for an internal ops dashboard with no existing identity infrastructure.
`ApiKeyAuthenticationHandler` checks, in order:

1. The `X-Api-Key` header.
2. An `Authorization: Bearer <key>` header.
3. The `?access_token=` query string.

All three are needed because a SignalR hub connection has two legs with different constraints: the JS
client's `accessTokenFactory` sends the token as `Authorization: Bearer` on the negotiate HTTP call,
and falls back to the query string only for the actual WebSocket/SSE upgrade (which can't carry custom
headers).

**Fails closed.** An unconfigured `Dashboard:ApiKey` denies every authenticated request rather than
silently disabling auth. `/health` stays unauthenticated, per standard infra-probe convention.

**A rejected request explains itself.** Every `401` carries an `application/problem+json` body naming
the three accepted credential forms, so the common mistake — calling `curl` without a key — is
diagnosable from the response instead of a bare status code. The body is deliberately identical for a
missing key, a wrong key, and an unconfigured server: the specific reason is logged server-side but
never echoed, so the response can't be used to probe whether a guessed key was close.

**Client wiring.** The Angular app sends the key via an `HttpInterceptorFn`
(`dashboard-web/src/app/interceptors/api-key.interceptor.ts`) on ordinary HTTP calls, and
via the hub connection's own `accessTokenFactory` for SignalR. The key it sends
(`DASHBOARD_API_KEY`) is a plain compile-time constant in `dashboard-web/src/app/api-config.ts` — not
an environment variable and not an Angular environment file — so a server whose `Dashboard:ApiKey`
isn't the compose dev value means editing that file and rebuilding the SPA (in compose, rebuilding
the `dashboard-web` image). Which API it talks to is not configured in the SPA at all: it calls its
own origin, and whatever serves the page forwards `/api` and `/hubs` (see [The SPA](#the-spa)).

**Known limitation, accepted as part of this choice:** a key embedded in a compiled SPA bundle is
visible via browser devtools. This closes off unauthenticated direct API access; it is not per-user
authentication or authorization.

## Live updates (SignalR)

`SagaHub` is mapped at `/hubs/saga` (see `dotnet/src/VSaga.Dashboard.Api/Program.cs`) and requires the
same authentication as the REST routes above — see [Authentication](#authentication) for how a
non-Angular client should supply the key on the hub connection. It groups connections per saga instance
(`saga:{sagaType}:{correlationId}`) and per list view (`saga:list`), and every push targets a group, so
a connection receives nothing until it joins one by invoking the hub methods
`SubscribeToList()`/`UnsubscribeFromList()` or
`SubscribeToSaga(sagaType, correlationId)`/`UnsubscribeFromSaga(sagaType, correlationId)`. `correlationId` is passed as a string, and a malformed
one joins no group rather than failing the invocation. A connection that joins only an instance's group
receives updates for that instance alone. (The SPA shares one hub connection across
pages and never leaves the list group once the list view has joined it, so its detail page also
filters incoming `SagaUpdated` pushes by `(sagaType, correlationId)` client-side.)
Two paths push into it:

- **In-process** (`SignalRSagaChangeNotifier`) — used when the hub and the saga engine share a
  process.
- **Cross-process** (`SagaChangePollingService`) — the path that actually delivers live updates in
  the deployed topology, since sagas normally run in a separate process (e.g. `OrderProcessing`) from
  the dashboard API. A background timer diffs the store since its last watermark and pushes the
  difference; the watermark only advances after a successful push, so a tick that throws retries the
  same window on its next tick instead of skipping past it.

Pushes arrive as two client methods, with enums serialized as their string names:
`SagaUpdated(summary)`, a `SagaSummary` that reaches both the list group and the specific instance's
group, and `TimelineEntryAdded(sagaType, correlationId, entry)`, a `SagaLogEntry` with the saga type as
a leading argument, sent to the instance's group only. Only the in-process path sends
`TimelineEntryAdded`; the cross-process poller pushes `SagaUpdated` alone, so in the deployed topology
a client re-fetches the timeline and map when an instance's `SagaUpdated` arrives (as the SPA's detail
page does).

## The SPA

`dashboard-web` (Angular 21) is a saga-type-agnostic client: a list view (paginated,
filterable by status/type/kind/search, sortable by Status/Updated — sorting and paging are both
pushed to the backend query, not applied client-side to whatever page happens to be loaded) and a
detail view with three tabs — Map (first and the one it opens on), then Timeline and Data. The detail
page also resolves its own
correlation id through `GET /api/correlations/{id}` and, when more than one saga instance shares it,
renders an "Also tracking this correlation id" strip linking to each sibling (a snapshot, refreshed
only when the current instance itself updates — not independently live-pushed). It also links
sub-saga composition in both directions: a "Started by" link to the parent, read straight off the
instance's own summary, and a "Started N sub-sagas" strip from
`GET /api/sagas/{sagaType}/{correlationId}/children`, refreshed on the same snapshot terms as the
sibling strip.

### How it is served

The SPA runs as the compose service `dashboard-web` (see ["Run the demo"](../README.md#run-the-demo)
in the root README): an nginx container built from `dashboard-web/Dockerfile`, published on
`127.0.0.1:4200` in the base stack and on the dashboard API's host port minus 880 in every overlay
(4300 to 4800, see [`transports/index.md`](transports/index.md#running-an-adapters-own-overlay)). It
starts once `dashboard-api` reports healthy; its own healthcheck (`/healthz`, answered by nginx)
reports on nginx alone.

**Same origin.** The SPA's URLs are relative (`API_BASE_URL` is `''`, so the hub is `/hubs/saga`), so
the browser only ever talks to the origin that served the page. nginx proxies `/api/` and `/hubs/`
(WebSocket upgrades included) to `dashboard-api:8080`, and serves everything else from the build:
`index.html` revalidates on every load, content-hashed bundles are cached for a year, a missing hashed
bundle is a `404` rather than `index.html`, and any other path falls back to `index.html`, so deep
links work. Nothing is cross-origin, so no CORS policy is involved and compose leaves
`Dashboard:WebOrigin` unset. The API's own `/health` is not proxied; read it on the API port.

A few properties of the proxy that matter when changing it (the template,
`dashboard-web/nginx/default.conf.template`, says the same next to each rule):

- **The upstream is resolved per request** (Docker's DNS, cached 10 s), not once at start. Each compose
  project's nginx reaches its own `dashboard-api`, nginx starts while the API is still down, and the UI
  recovers by itself when the API container is recreated with a new address.
- **The request line is forwarded untouched** (`proxy_pass` has no URI part), so an encoded saga type
  such as `Order%2FSaga` reaches the API still encoded. The CI smoke test compares such a request
  through port 4200 with the same request on port 5080.
- **The `Host` header is passed through** as the browser sent it (`localhost:4200`, port included).
- **`X-Forwarded-For` and `X-Forwarded-Proto`** are added; compose's
  [`Dashboard:TrustedProxies`](configuration.md#dashboardtrustedproxies) makes the API honour them.
- **No query string is logged.** The access log records the path only, so the API key that SignalR
  clients send as `?access_token=` on the hub upgrade, the SPA's own connection included, never reaches
  it, and nginx's error log is raised to `crit`, because its
  request-time error lines (an unreachable API, a timeout) quote the full request line; the access log
  still records those requests by path with their `502` or `504`.

**Content Security Policy.** Every response carries `default-src 'self'; script-src 'self'; style-src
'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self' ws://<host>
wss://<host>; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'` (`<host>`
is the request's `Host`), plus `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY` and
`Referrer-Policy: same-origin`; the `Server` header carries no version. So the SPA must not use
inline `<script>`, `on*=` handler attributes, `eval` or any third-party origin for scripts, styles,
fonts, images or requests. Inline styles are allowed because Angular injects component styles as
`<style>` elements. The production build turns critical-CSS inlining off, because it writes an
`onload` handler onto the stylesheet link that this policy would block, and CI fails if the built
`index.html` contains an inline script or handler. `Referrer-Policy: same-origin` rather than
`no-referrer`, because under `no-referrer` browsers send `Origin: null` on same-origin `POST`s.

**The dev server.** `npx ng serve` in `dashboard-web/` runs on http://localhost:4201, beside the compose
UI, and proxies `/api` and `/hubs` the same way through `dashboard-web/proxy.conf.mjs`, to
`VSAGA_API_URL` (default `http://localhost:5080`, the compose stack's API). It has neither the CSP nor
nginx's caching and routing rules, so check a UI change in the container before relying on it; see
[`dashboard-web/README.md`](../dashboard-web/README.md#run-it).

### Behind your own proxy or TLS

The demo binds the dashboard ports to `127.0.0.1`. To put the UI behind your own reverse proxy or TLS
terminator:

- **Forward WebSocket upgrades on `/hubs/`** (HTTP/1.1 with the `Upgrade` and `Connection` headers, and
  read timeouts long enough for a connection that stays open). Without them the hub falls back to
  server-sent events or long polling, or fails.
- **Pass `Host` through unchanged.** nginx forwards it to the API and builds the CSP's `connect-src`
  from it, so the browser's WebSocket to `wss://<host>` is allowed only when `Host` is the name the
  browser used.
- **`X-Forwarded-Proto`.** The image sends the API its own scheme (`http`) and ignores a client's
  header, so a client that reaches the container directly cannot claim `https`. Set
  `DASHBOARD_OUTER_PROXY=true` on `dashboard-web` when a TLS terminator sits in front and the container
  is reachable only through it; nginx then passes that proxy's `http` or `https` value on. See
  [`configuration.md`](configuration.md#the-dashboard-uis-container-and-dev-server).
- **`Dashboard:TrustedProxies`** on the API must cover the address the API sees nginx connect from, and
  nothing wider than you need: any peer in that list can assert a client address and scheme. The API
  honours only the last hop, which nginx appends, so behind a further proxy the client address the API
  sees is that proxy's. See [`configuration.md`](configuration.md#dashboardtrustedproxies).

## Saga Map

The saga detail page's first tab — the one it opens on, alongside Timeline/Data — renders an Azure-App-Map-style service
graph for one saga instance: nodes are the services involved (Initiator, Orchestrator, Participant, or
Unresolved), edges are the messages that flowed between them, plus a scrubber/replay animation that
steps through the saga's timeline at adjustable speed.

`SagaMapBuilder` (`dotnet/src/VSaga.Dashboard.Api/SagaMapBuilder.cs`) is a pure, unit-testable function
from a saga's raw event log plus a topology registry to nodes/edges/a replay script — it has no
dependency on any specific saga definition, matching the dashboard's saga-type-agnostic design.

**How service identity is tracked.** `MessageEnvelope.From` stamps `x-vsaga-source-service` and
`x-vsaga-causation-id` on every outbound message; `SagaOrchestrator` reads both back off a received
message and stamps them onto `SagaLogEntry.SourceService`/`CausationId` for both `SagaStarted` and
`MessageReceived` entries. `SagaMapBuilder` stitches an edge by matching an outbound entry's
`MessageId` to a later inbound entry's `CausationId`. An outbound message with no matching reply
resolves its destination from `IServiceTopologyStore` (populated by `TopologyRecordingTransport`
observing real `SubscribeAsync` calls across the fleet) — or renders as an "unresolved" placeholder if
even that doesn't know it — and is marked **unanswered** rather than dropped, since a hung downstream
service is often the most useful thing the map can show.

**Topology recording is opt-in, per process.** `IServiceTopologyStore` is filled only by hosts that call
`services.AddVSagaTopologyRecording()` (`VSaga.Core`). Call it in every process that subscribes, saga
hosts and participant hosts alike, after that process's `AddVSaga<Transport>` call (it throws
`InvalidOperationException` when there is no transport registration to wrap yet — see
[`transports/index.md`](transports/index.md#the-two-decorators-every-adapter-is-wrapped-in)) and in a
process with a persistence provider registered (without one it records into a no-op store). The
dashboard API does not call it; it only reads the store. A service whose host skips it never becomes a
known destination, so a message sent to it that gets no reply renders as unresolved.

**Failure detection covers two shapes:** a `StepFailed` entry (an action threw), and a business
failure reached through a normal, successful step transition with no exception at all (e.g. "payment
declined") — detected as the last inbound message before a `SagaCompleted` entry on a saga that ended
`Failed`/`TimedOut`.

**Compensation edges are flagged.** Both outbound and inbound edges on the map carry an
`isCompensation` flag, so a compensating REST call's reply (which a fire-and-forget broker
compensation never produces, but a `.CallHttp`/`ctx.CallHttpAsync` compensating call does) renders
distinctly from a forward-flow edge.

**`.CallHttp`/`ctx.CallHttpAsync` hops get their own map entries**, written directly through the
internal `ISagaContextLogSink` naming the called host as the service — a naive loopback via
`ctx.PublishAsync` would stamp the *inbound* message's causation id rather than the outbound call's
own, missing the stitch and showing a bogus self-loop instead of the REST endpoint actually called.

Live verification against the real `docker compose up` stack — not just unit tests with a hand-seeded
`SagaLogEntry`, which pass even if the orchestrator never actually reads a header back — caught two
real gaps during the Saga Map's own development: `MessageReceived`/`SagaStarted` entries were stamped
with `SourceService` but never `CausationId`, so nothing ever stitched a reply back to its request;
and the business-failure-without-exception case above had no detection path at all until added.

See [`history/`](history/) for the live-verification history behind each of these mechanisms — most
of them were built once, found wrong by a real `docker compose up` run (not a unit test with a
hand-seeded `SagaLogEntry`), and fixed.
