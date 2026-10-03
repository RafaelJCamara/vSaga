# Dashboard

`VSaga.Dashboard.Api` (ASP.NET Core Minimal API + SignalR) and `dashboard-web` (Angular 22 SPA) together
form a saga-type-agnostic ops dashboard: list/filter/search every saga instance across every registered
saga type, drill into one instance's timeline, a visual service map and its data after each step, and
manually retry a failed or timed-out saga — all against provider-neutral contracts every persistence
provider implements (`ISagaSummaryReader`, `ISagaEventLogStore`, `ISagaAdminStore`,
`IServiceTopologyStore`), plus a raw `IMessageTransport` republish for retry, so the dashboard needs no
knowledge of any specific saga definition.

## API endpoints

All routes below require authentication (see [Authentication](#authentication)) except `/health` and
the Development-only `/openapi/v1.json`.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/sagas` | Paginated, filterable saga list. Query params: `status`, `sagaType`, `kind`, `search`, `page` (default 1), `pageSize` (default 25, clamped to a maximum of 500 — the response's `pageSize` reports the size applied), `sortBy` (`UpdatedAt` or `Status`), `sortDescending`. `400` under `Persistence:Provider=Redis` when a `search` would scan more index members than `Redis:MaxSearchScanMembers` allows — narrow it with a `sagaType`/`status`/`kind` filter; see [`persistence.md`](persistence.md#search). |
| `GET` | `/api/sagas/{sagaType}/{correlationId}` | One instance's summary plus its raw state `DataJson`. `404` if not found. |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/timeline` | The full, ordered `SagaLogEntry` history for one instance, `StatePersisted` snapshots included — see [State snapshots](#state-snapshots). |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/map` | The Saga Map for one instance — see [below](#saga-map). `404` if not found. |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/children` | Every saga this instance started via `StartChildAsync`. Empty (not `404`) for both "no children" and "no such saga" — the caller already has the plain `GET` above to tell those apart. |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/retry-plan` | What a retry of this instance would re-run, or why it cannot — see [Manual retry](#manual-retry). Read-only. `404` if not found; otherwise `200` with the plan, also when it says `retryable: false` and gives the `reason`; never `409`/`422`. |
| `POST` | `/api/sagas/{sagaType}/{correlationId}/retry` | Re-runs the step a `Failed`/`TimedOut` instance failed in, for that saga type only — see [Manual retry](#manual-retry). `202` once the redrive is published, `404` if no such instance, `409` for any other status or when the saga changed between the read and the reset (reload and try again), `422` with the plan's `reason` as `{ error }` when the plan is not retryable, `502` (a problem with a `restored` boolean) when the transport republish fails. |
| `GET` | `/api/saga-types` | Every distinct saga type currently known to the store, for populating filter dropdowns. |
| `GET` | `/api/correlations/{correlationId}` | Every saga instance — of any type — currently tracking this correlation id. The one route that still takes a bare correlation id, since two saga types (an orchestrated one and a choreography observing the same transaction — see [`concepts.md`](concepts.md#saga-instances-and-identity)) may both track it. Does **not** include sub-saga children, which have their own correlation ids and are reached via `/children` instead. |
| `GET` | `/health` | Unauthenticated. Two real connectivity checks, `persistence` and `rabbitmq` — `503` with a per-check breakdown when either is unreachable, not a hardcoded `200`. `persistence` is named for the role, not the store: under `Persistence:Provider=Postgres` it is a `CanConnect` probe; under `Redis` it is the provider's own `RedisPersistenceHealthCheck`, which re-verifies `appendonly`, `maxmemory-policy`, cluster mode, the primary role, memory pressure, the schema marker, server-side Lua and the torn-write sentinel on every call and reports Unhealthy naming the guarantee (or that it could not be verified) — see [`persistence.md`](persistence.md#redis); under `MongoDb` it is `MongoPersistenceHealthCheck`, which re-verifies the replica-set topology, the primary, the server version, the indexes and the schema marker on every call, and reports any connection-string setting that contradicts the ones it pins — see [`persistence.md`](persistence.md#mongodb). The Postgres and RabbitMQ checks degrade to a pass when their dependency isn't registered at all ("No relational database configured." / "No message broker configured."), so under `Transport:Provider=Http` — where no RabbitMQ connection manager is registered — the broker check is an unconditional pass rather than a real probe. |
| `GET` | `/openapi/v1.json` | Unauthenticated, and mapped only in the `Development` environment (which `dotnet run` gets from `launchSettings.json`; the compose containers don't set it). The generated OpenAPI document. |

Every per-instance route is keyed by `(sagaType, correlationId)`, not correlation id alone — see
[`concepts.md`](concepts.md#saga-instances-and-identity) for why.

### Manual retry

A dashboard retry re-runs **one step, the one the saga failed in, for this saga type only**. It puts the
saga's `CurrentState` and `Status` back to what they were before that step and republishes that step's
message addressed to this saga type alone. The decision and the alternatives it replaced (replaying the
initiating message from the initial state, and a republish every subscribed saga type processed) are in
[ADR 0008](adr/0008-dashboard-retry-reruns-the-failed-step.md).

**Which step re-runs.** `SagaRetryPlanner` (`Endpoints/SagaRetryPlanner.cs`), a pure function of the
saga's summary and timeline that both retry endpoints use, ignores `StatePersisted` entries and takes the
**latest** failure entry in the timeline: a `StepFailed`, a `SagaCompleted`, a `TimeoutFired`, or a
`DeliveryExhausted` that carries a message id. Latest, not a fixed precedence, so a saga that failed
technically, was retried, and then failed for a business reason re-runs the later step. (A
`DeliveryExhausted` without a message id records a deferred publish that failed or was discarded after
the step itself failed, so it is skipped.) Then, by that entry's kind:

| Failure entry | `failureKind` | The message replayed | The state the saga is reset to |
| --- | --- | --- | --- |
| `StepFailed` (an action threw) | `StepFailed` | the failed message, body taken from the entry itself | the entry's `FromState`, which is also the current state |
| `DeliveryExhausted` (a message dead-lettered after its redeliveries) | `DeliveryExhausted` | the dead-lettered message, body taken from its `MessageReceived` (or `SagaStarted`) | the current state: the dead-lettered step never committed |
| `SagaCompleted` (a business failure, such as a declined payment) | `BusinessFailure` | the message whose step ended the saga, matched by the message id the entry carries | the `FromState` of that step's `StepSucceeded` |
| `TimeoutFired` (a state timed out) | `TimedOut` | the message whose step entered the timed-out state | the `FromState` of that step |

A saga that reached `TimedOut` through a message step (a `SagaCompleted`) is a `BusinessFailure`; only
a timeout that fired is `TimedOut`.

**When it cannot be retried.** The plan says `retryable: false`, with a reason in plain words, when the
saga is not `Failed` or `TimedOut`, when no failure entry exists, when the timed-out state was entered by
another timeout (no message to replay), or when the step's message body was never recorded. That last
case is a saga recorded before `MessageReceived` carried the message body (see
[`observability.md`](observability.md#the-persisted-event-log)): such a saga stays retryable only when
the failing step was a `StepFailed` one or its first step, whose body is on `SagaStarted`. On MongoDB a
body that was replaced by the size marker (above `MaxPayloadJsonBytes`) counts as missing, because
replaying the marker would run the step on a message whose every field is default.

**The plan** (`GET .../retry-plan`), camelCase, with `failureKind` as its name like `entryType`:

```json
{
  "retryable": true,
  "reason": null,
  "failureKind": "TimedOut",
  "failureSequenceNumber": 11293,
  "step": {
    "sequenceNumber": 11055,
    "messageType": "InvoiceIssued",
    "messageId": "a4dcb202457743bf97e1f9d4fb793d48",
    "fromState": "Requested"
  }
}
```

`failureKind` and `failureSequenceNumber` describe the failure entry and are present whenever one was
found, retryable or not, so the UI can mark the failed step either way; both are null only when there
is none. `step` is present whenever the step was identified, even when its body is missing; its
`sequenceNumber` is the step's inbound entry (its `MessageReceived`, else its `SagaStarted`, else the
failure entry itself or the step's `StepSucceeded`), which the SPA maps to a step of its timeline. For
a timeout the two point at different steps: the timeout step failed, and the step that entered the
timed-out state is the one re-run. The plan carries no message body.

**What `POST .../retry` does**, in order:

1. `404` for an unknown saga, `409` when its status is not `Failed`/`TimedOut`, `422 { error: reason }`
   when the plan is not retryable. Nothing is written in those cases.
2. Appends `ManualRetryRequested` from the current state to the step's `fromState`, naming the replayed
   message's type and original id.
3. Resets `CurrentState` to the step's `fromState` and `Status` to `Running` with
   `ISagaAdminStore.ResetStateAsync`, always, even when the state does not change (a `StepFailed` or
   `DeliveryExhausted` retry). The version the API read is the concurrency guard: a saga that moved since
   answers `409` ("modified concurrently with this retry; reload and try again"), and the saga leaves
   `Failed` at once, so a second click meets the status check. **Business fields inside the stored state
   are not rolled back**; the step re-runs against them, so a step that appends or increments does so
   twice.
4. Records the reset state as a [state snapshot](#state-snapshots), when the saga already has snapshots.
5. Publishes the step's message with `IMessageTransport.PublishRawAsync`, under the saga's correlation
   id with a **fresh message id** (so the duplicate check lets it through) and the header
   `x-vsaga-target-saga-type` set to the saga type. Answers `202`.

Everything after the reset runs without the request's cancellation token, so a client that disconnects
cannot leave the saga reset with no redrive.

**When the publish fails** (any exception, a `MessageTransportPublishException` or otherwise), the API
puts the saga's state and status back as it found them, version-checked against the reset, records that
state as a snapshot too (for a saga that has snapshots), and answers `502` as a problem whose `detail`
names the cause and says whether the saga was restored, with a `restored` boolean beside it. When the
saga moved on since the reset, the restore loses, the newer state stands and `restored` is `false`. The
`ManualRetryRequested` entry stays in the timeline either way, with no re-run after it; so does it after
a `409` from the reset.

**What other consumers see.** The redrive is still a publish of that message type, so everything
subscribed to it receives it:

- **Other saga types** acknowledge it and ignore it: the first thing `SagaOrchestrator.HandleCoreAsync`
  does is compare the header with its own saga type (ordinal), and a mismatch, an empty value or a
  different case included, is logged at Debug and acknowledged with no entry written and no instance
  looked up. A message without the header is processed as before. The header is not copied to anything
  the re-run step publishes. In the sample, retrying a timed-out `InvoiceFollowUpSaga` replays
  `InvoiceIssued`, and `PostShipmentChoreography`, which handles the same message under the same
  correlation id, ignores it, so no second `InvoiceDeliverySaga` starts.
- **Minimum engine version.** Only an engine that contains this change reads the header. A host running
  an older engine ignores it and processes the replay as a new delivery, and the dashboard cannot tell.
  A targeted retry therefore needs every host that runs a saga type subscribed to the replayed message
  type to run an engine that includes the targeted redrive (the change that added
  `MessageEnvelope.TargetSagaTypeHeader`; the repository has no release tags yet).
- **Consumers that are not sagas** (participants subscribed through the transport directly) receive the
  replay as a new message. The SPA's confirmation says so.
- **The retried saga** runs the step again, with all its side effects: its publishes, `.CallHttp`
  calls, child sagas (a step that calls `StartChildAsync` starts another child) and compensations. A
  step whose outcome depends only on the message reaches the same outcome again (replaying `OrderSaga`'s
  `PaymentFailed` fails it again). A retry helps when the outcome depends on something that has since
  changed, such as a participant, a lookup, configuration or a fixed bug, or when the step was
  interrupted by an exception, a dead-letter or a timeout waiting for a reply that can now arrive.

**In the SPA.** For a `Failed` or `TimedOut` saga the detail page loads the plan, marks the step holding
`failureSequenceNumber` "Failed here" and, when it is a different step, the one holding
`step.sequenceNumber` "Re-run starts here". The Retry button asks "Re-run step N (<message type>,
<from state>) for this saga only?", adds "Other services that consume <message type> still receive it.",
and is disabled beside the plan's reason when the plan is not retryable.

**In-process retry, without the dashboard.** `AddVSagaEngine` also registers `ISagaRetryDispatcher`
(`VSaga.Core.Runtime`), whose `RetryAsync(sagaType, correlationId)` redrives an instance from a process
that runs its saga's engine. It replays only a `StepFailed` entry, and runs the replayed step directly
in that process instead of republishing it, so it involves no header and no other saga type. It accepts
only a `Failed` instance, throwing `SagaRetryNotAllowedException` otherwise (including for `TimedOut`);
it throws `SagaNotFoundException` for an unknown instance, and `InvalidOperationException` when there
is no `StepFailed` entry to replay or the saga type isn't registered in that process.
`SagaTestHarness.RetryAsync` is a thin wrapper over it (see [`testing.md`](testing.md)).

### State snapshots

Every committed step leaves a `StatePersisted` entry in the timeline whose `payloadJson` is the saga's
state as stored right after it; see [`observability.md`](observability.md#state-snapshots) for when the
engine writes one and when it does not, and [`configuration.md`](configuration.md#sagaorchestratoroptions)
for the options. In `/timeline` it is an ordinary entry (`entryType: "StatePersisted"`, `fromState`
and `toState` null, `messageType`/`messageId` naming the step's inbound message). Its payload is either
the state's JSON or a marker in its place:

- `{"$vsagaStateOmitted":true,"bytes":N,"limit":L}`: the state was larger than the per-snapshot cap.
- `{"$vsagaStateOmitted":true,"bytes":N,"budget":B}`: recording the state would have taken the saga's
  snapshots past its per-saga budget (a successful step or a timeout; failure snapshots are kept in
  full).

The SPA folds each snapshot into the step it follows and treats any object with a `$vsaga…Omitted`
key set to `true` as a marker (MongoDB's `$vsagaPayloadOmitted` included). `/map` skips them
(see [Saga Map](#saga-map)).

**After a retry reset.** The API appends one snapshot itself, straight after the reset and before the
redrive, describing the state the reset left (and one more for the restored state after a `502`). It has
no message identity. It is written only when the timeline already holds a `StatePersisted`, so a host
with `RecordStateSnapshots = false` or a saga older than snapshots gets none; it is skipped when a step
has already moved the saga past the reset's version; and its cap is the smaller of
[`Dashboard:StateSnapshots:MaxBytes`](configuration.md#dashboard) and the `limit` of the saga's latest
per-snapshot size marker (budget markers do not count), so the dashboard never records more of a state
than the engine host chose to. It never fails the retry.

**Redaction seam.** `Endpoints/SagaTimelineRedaction.cs` is the one place that decides whether a caller
may read a saga's data. For a caller who may not, `payloadJson` (message bodies and snapshots) and
`errorMessage` are nulled together on every entry of `/timeline` (and `errorMessage` on every event of
`/map`), and no entry is dropped, so sequence numbers and the timeline's steps are the same for every
caller. Today every caller holds the one API key and every endpoint passes "include data"; per-caller
gating arrives with sign-in, as the `sagas.data` permission. The SignalR push of a single entry already
carries neither field, for everyone (see [Live updates](#live-updates-signalr)).

Upgrade the dashboard API before the engine hosts: an API older than `StatePersisted` returns the entry
type as `21` and serves snapshot payloads unredacted (compose builds both together).

## Authentication

A single shared API key (`Dashboard:ApiKey` in configuration) — chosen over JWT/OIDC or basic auth as
the right fit for an internal ops dashboard with no existing identity infrastructure.
`ApiKeyAuthenticationHandler` checks, in order:

1. The `X-Api-Key` header.
2. An `Authorization: Bearer <key>` header.
3. The `?access_token=` query string, on the hub endpoints (`/hubs/...`) only. Anywhere else a cross-site
   link or form could supply it, so machine clients send the key as `X-Api-Key`.

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
a leading argument, sent to the instance's group only. The pushed entry carries its stored
`sequenceNumber` but never its `payloadJson` or `errorMessage`: a hub group is joined per saga, not per
permission, so a push cannot be redacted per caller, and a client that wants the data refetches the
timeline (see [State snapshots](#state-snapshots)). Only the in-process path sends
`TimelineEntryAdded`; the cross-process poller pushes `SagaUpdated` alone, so in the deployed topology
a client re-fetches the timeline and map when an instance's `SagaUpdated` arrives. The SPA's detail page
treats both pushes the same way: it patches the summary from a `SagaUpdated` at once, and coalesces
every push for its saga within 250 ms into one refresh of the timeline, the map, the related and child
sagas and the saga itself. Every reconnect after the first connection runs one such refresh too,
because pushes sent while the hub was down are lost.

## The SPA

`dashboard-web` (Angular 22) is a saga-type-agnostic client: a list view (paginated,
filterable by status/type/kind/search, sortable by Status/Updated — sorting and paging are both
pushed to the backend query, not applied client-side to whatever page happens to be loaded) and a
detail view with a summary card, a Saga data bar and two tabs, Map (the one it opens on) and Timeline
(see [The saga detail page](#the-saga-detail-page)). The detail page also resolves its own
correlation id through `GET /api/correlations/{id}` and, when more than one saga instance shares it,
renders an "Also tracking this correlation id" strip linking to each sibling (a snapshot, refreshed
only when the current instance itself updates — not independently live-pushed). It also links
sub-saga composition in both directions: a "Started by" link to the parent, read straight off the
instance's own summary, and a "Started N sub-sagas" strip from
`GET /api/sagas/{sagaType}/{correlationId}/children`, refreshed on the same snapshot terms as the
sibling strip.

### The saga detail page

**Summary card.** Type, correlation id, kind, status, current state and version, and `Created` and
`Updated` in the browser's local time with the zone in the label (`Created (UTC+02:00)`; hover for
UTC). A `Failed` or `TimedOut` saga gets the Retry button (see [Manual retry](#manual-retry)).

**Timeline tab: steps, not raw rows.** The SPA folds the entries into numbered steps
(`util/saga-transitions.ts`): one per saga start, received message, fired timeout, manual retry or
dead-letter, each holding the entries it caused, matched by message id first, then by causation id, then
by position. Each step's header reads "Step N", a title ("Started by OrderSubmitted", the message type,
"Timeout in AwaitingShipment", "Manual retry of …", "… dead-lettered") and its outcome. Each entry row
reads `#i` (its position on the map as well, since both skip snapshots), the entry type (`SagaCompleted`
reads `SagaFinalized`), the states, message type, service, any error, and **Recorded at**: when the
engine recorded the entry, in local `HH:mm:ss.SSS` (the date too when it differs from the first
entry's), with UTC on hover and the offset from the saga's first entry (`+1.204 s`). A hint line above
the steps says so. `StatePersisted` entries are never rows: each becomes the data of the step it
follows.

**Data after each step.** A step's **Data** button opens its inspector: **Changes** against the nearest
earlier recorded snapshot (named, with `Version` and `UpdatedAtUtc` on one muted "engine bookkeeping"
line), **Full state**, **Message** where the step's message body was recorded, and **Copy JSON** where
the browser allows it. When there is no snapshot to show, the inspector says why: the state was over the
per-snapshot cap or the saga's budget (with the sizes from the marker), the step did not persist a new
state (an unhandled message, a retry request, a timeout with no outcome), the snapshot is not recorded
yet (for up to 5 seconds after the step's newest entry; the page fetches the timeline once more 1.5 s
after a refresh that found it missing), or no snapshot exists (a saga older than snapshots, snapshots
switched off, or a step that lost a concurrent update and never committed).

**Saga data bar**, under the summary card: a "Saga data" group with **At start** (the message that
started the saga and the state after the first step that recorded one), **At end** (the stored state;
the button reads **Current**, with the accessible name "Current data", until the saga reaches a terminal
status, and "Data at end" after) and **Compare** (the first recorded state against the stored one;
disabled, with a tooltip naming the missing side, when either is absent). The start button's accessible
name is "Data at start". Viewing data will need the `sagas.data` permission once sign-in lands; without
it the buttons are disabled beside "Saga data is hidden for your role. It needs the sagas.data
permission." and the steps show no Data buttons. Until then everyone sees the data.

**From the timeline to the map.** Clicking an entry row, or a step's title (which stands for the step's
last entry, the state after it), opens the Map tab focused on that entry: the map shows the saga as of
that entry, and a banner reads "As of entry #12 of 34: StepSucceeded, recorded at 14:03:07.140
(+1.224 s)" with a **Back to this entry in the timeline** button (see [Saga Map](#saga-map)).

**The failed step.** For a `Failed` or `TimedOut` saga the page loads the
[retry plan](#manual-retry) and marks the step that failed "Failed here", and the step a retry would
re-run "Re-run starts here" when that is a different one (a timeout). Opened without an explicit entry,
the map is focused on the failure entry; playing, stepping or scrubbing the replay releases that focus.

**The URL holds the view.** `?tab=timeline` (the map is the default and is written as no `tab`),
`?entry=<sequence number>` for the focused entry, and `?data=start|end|compare` for the open Saga data
view, so a link or a reload lands on the same view. Anything else in those parameters is ignored.
Switching tab or focusing an entry is a browser history step, so Back returns to where you came from;
dropping the focus or changing the data view is not.

**Live updates and load errors.** Pushes for the saga refresh the page as described under
[Live updates](#live-updates-signalr). A tab whose fetch failed with nothing loaded shows an error with
**Try again**; over content loaded earlier it shows a warning and keeps the stale content readable. A
lost hub connection shows "Reconnecting to live updates…".

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

The saga detail page's first tab — the one it opens on, beside Timeline — renders an Azure-App-Map-style
service graph for one saga instance: nodes are the services involved (Initiator, Orchestrator,
Participant, or Unresolved), edges are the messages that flowed between them, plus a scrubber/replay
animation that steps through the saga's timeline at adjustable speed.

**State snapshots are not on the map.** `SagaMapBuilder` drops `StatePersisted` entries before it does
anything else, so snapshots add no event, node or edge and do not move `FailureEventIndex`, and the
map's `#i` for an entry is the same as the timeline's.

**As of an entry.** The map can be focused on one timeline entry (a click in the timeline, `?entry=` in
the URL, or, for a `Failed`/`TimedOut` saga opened without one, the failure entry of its
[retry plan](#manual-retry)). It then shows the replay stopped at that entry, outlines the node it
concerns, and shows a banner: "As of entry #12 of 34: StepSucceeded, recorded at 14:03:07.140
(+1.224 s)", with "Nothing moved between services at this entry" when the entry is a plain event (the
orchestrator is highlighted), "The selected entry is not on the map yet; showing the closest earlier
entry" when a map fetched earlier does not hold it (the page fetches the map again), and a **Back to
this entry in the timeline** button. The focus is an outline of its own, so a failed node stays marked
failed. Play, restart, step and scrub release it.

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
`Failed`/`TimedOut`. That rule colours the map's failed edge only; which step failed and which one a
retry re-runs is decided by the [retry plan](#manual-retry), which the timeline's "Failed here" marker
and the map's default focus follow.

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
