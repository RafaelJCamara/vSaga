# Dashboard

`VSaga.Dashboard.Api` (ASP.NET Core Minimal API + SignalR) and `dashboard-web` (Angular 22 SPA) together
form a saga-type-agnostic ops dashboard: sign in, list/filter/search the saga instances you may see across
every registered saga type, drill into one instance's timeline, a visual service map and its data after
each step, and manually retry a failed or timed-out saga — all against provider-neutral contracts every
persistence provider implements (`ISagaSummaryReader`, `ISagaEventLogStore`, `ISagaAdminStore`,
`IServiceTopologyStore`), plus a raw `IMessageTransport` republish for retry, so the dashboard needs no
knowledge of any specific saga definition. People sign in with a username and password and see and do only
what their roles allow, per saga type ([Authentication](#authentication), [Access control](#access-control));
scripts and probes use an API key.

## API endpoints

Every route needs a signed-in session or the API key (see [Authentication](#authentication)), except the
ones marked anonymous. What a caller may do on a route is its **Permission** column (see
[Access control](#access-control)): a route that names a saga type in its path checks the permission for
that saga type and answers `403` before it reads anything when the caller's grants do not cover it. Every
response under `/api`, errors included, carries `Cache-Control: no-store` and `X-Content-Type-Options:
nosniff`: the bodies hold per-user and business data, and the API port can be reached without nginx in
front.

### Saga routes

| Method | Route | Permission | Purpose |
| --- | --- | --- | --- |
| `GET` | `/api/sagas` | `sagas.view` for at least one saga type | Paginated, filterable saga list, limited to the saga types the caller may view (see [What a scoped caller sees](#what-a-scoped-caller-sees)). Query params: `status`, `sagaType`, `kind`, `search`, `page` (default 1), `pageSize` (default 25, clamped to a maximum of 500 — the response's `pageSize` reports the size applied), `sortBy` (`UpdatedAt` or `Status`), `sortDescending`. `400` under `Persistence:Provider=Redis` when a `search` would scan more index members than `Redis:MaxSearchScanMembers` allows — narrow it with a `sagaType`/`status`/`kind` filter; see [`persistence.md`](persistence.md#search). `400` `{ error, maxPage }` for a caller scoped to several saga types who asks for a page deeper than a merged list can reach. |
| `GET` | `/api/sagas/{sagaType}/{correlationId}` | `sagas.view` for `{sagaType}` | One instance's summary plus its raw state `DataJson`, which is `null` (and never read) without `sagas.data`. `404` if not found. |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/timeline` | `sagas.view` for `{sagaType}` | The full, ordered `SagaLogEntry` history for one instance, `StatePersisted` snapshots included — see [State snapshots](#state-snapshots). Without `sagas.data` every entry loses its `payloadJson` and `errorMessage` (no entry is dropped). |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/map` | `sagas.view` for `{sagaType}` | The Saga Map for one instance — see [below](#saga-map). `errorMessage` is `null` on every event without `sagas.data`. `404` if not found. |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/children` | `sagas.view` for `{sagaType}` | Every saga this instance started via `StartChildAsync`, of the types the caller may view. Empty (not `404`) for both "no children" and "no such saga" — the caller already has the plain `GET` above to tell those apart. |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/retry-plan` | `sagas.view` for `{sagaType}` | What a retry of this instance would re-run, or why it cannot — see [Manual retry](#manual-retry). Read-only. `404` if not found; otherwise `200` with the plan, also when it says `retryable: false` and gives the `reason`; never `409`/`422`. |
| `POST` | `/api/sagas/{sagaType}/{correlationId}/retry` | `sagas.retry` for `{sagaType}` | Re-runs the step a `Failed`/`TimedOut` instance failed in, for that saga type only — see [Manual retry](#manual-retry). `202` once the redrive is published, `404` if no such instance, `409` for any other status or when the saga changed between the read and the reset (reload and try again), `422` with the plan's `reason` as `{ error }` when the plan is not retryable, `502` (a problem with a `restored` boolean) when the transport republish fails. The `ManualRetryRequested` entry records who asked. |
| `GET` | `/api/saga-types` | `sagas.view` for at least one saga type | Every distinct saga type currently known to the store that the caller may view, for populating filter dropdowns. |
| `GET` | `/api/correlations/{correlationId}` | `sagas.view` for at least one saga type | Every saga instance — of any type the caller may view — currently tracking this correlation id. The one route that still takes a bare correlation id, since two saga types (an orchestrated one and a choreography observing the same transaction — see [`concepts.md`](concepts.md#saga-instances-and-identity)) may both track it. Does **not** include sub-saga children, which have their own correlation ids and are reached via `/children` instead. |
| `GET` | `/health` | anonymous | Three real checks, `persistence`, `rabbitmq` and `identity` — `503` with a per-check breakdown when `persistence` or `rabbitmq` is unreachable, not a hardcoded `200`. `persistence` is named for the role, not the store: under `Persistence:Provider=Postgres` it is a `CanConnect` probe; under `Redis` it is the provider's own `RedisPersistenceHealthCheck`, which re-verifies `appendonly`, `maxmemory-policy`, cluster mode, the primary role, memory pressure, the schema marker, server-side Lua and the torn-write sentinel on every call and reports Unhealthy naming the guarantee (or that it could not be verified) — see [`persistence.md`](persistence.md#redis); under `MongoDb` it is `MongoPersistenceHealthCheck`, which re-verifies the replica-set topology, the primary, the server version, the indexes and the schema marker on every call, and reports any connection-string setting that contradicts the ones it pins — see [`persistence.md`](persistence.md#mongodb). The Postgres and RabbitMQ checks degrade to a pass when their dependency isn't registered at all ("No relational database configured." / "No message broker configured."), so under `Transport:Provider=Http` — where no RabbitMQ connection manager is registered — the broker check is an unconditional pass rather than a real probe. `identity` reports the [identity store](#the-identity-store) and is registered to fail as **Degraded**, never Unhealthy, so an unusable identity database keeps `/health` at `200` (the saga views, the API key with a built-in role and compose's `service_healthy` gates do not depend on it): its description says why, and it is also Degraded while a configured first administrator could not be created. |
| `GET` | `/openapi/v1.json` | anonymous | Mapped only in the `Development` environment (which `dotnet run` gets from `launchSettings.json`; the compose containers don't set it). The generated OpenAPI document. |

Every per-instance route is keyed by `(sagaType, correlationId)`, not correlation id alone — see
[`concepts.md`](concepts.md#saga-instances-and-identity) for why. A saga type in a route is matched against
grants exactly (ordinal, case-sensitive), as the route gave it.

### Sign-in routes (`/api/auth`)

All five are described under [Authentication](#authentication). Each of them answers `503
identity_unavailable` while the identity store is not ready, and each answers the caller's session (below) except
the failures.

| Method | Route | Who | Purpose |
| --- | --- | --- | --- |
| `GET` | `/api/auth/session` | anonymous | Who the caller is and what they may do, always `200` (an anonymous caller gets `authenticated: false`); also issues the `XSRF-TOKEN` cookie. The SPA reads it before it routes anywhere. |
| `POST` | `/api/auth/login` | anonymous | `{ username, password }`. `200` with the session and the session cookie; `401` `invalid_credentials` for every failure; `429` past the rate limit. |
| `POST` | `/api/auth/logout` | anonymous | Deletes the session cookie and closes the user's live hub connections. `200` with an anonymous session, also when there was no session. |
| `POST` | `/api/auth/setup` | anonymous, only while no user exists | `{ username, displayName, password, code }`: creates the first administrator with the one-time setup code and signs them in. `400` `invalid_credentials` (`errors.code`) for a missing or wrong code, `409` `setup_unavailable` when setup is not open. |
| `POST` | `/api/auth/password` | a signed-in user | `{ currentPassword, newPassword }`. `200` with a new session (the user's other sessions end); `400` `invalid_credentials` (`errors.currentPassword`) for a wrong current password or a locked or disabled account, `400` `validation` (`errors.newPassword`) for a new password that breaks the policy. The API key gets `403`. |

The session, `GET /api/auth/session` and every other answer above, is:

```json
{
  "authenticated": true,
  "setupRequired": false,
  "setupAvailable": false,
  "setupProblem": null,
  "user": { "id": "5b0c6f0e-2f8a-4d5f-9a37-c1e0b2d4a6c8", "username": "dana", "displayName": "Dana", "mustChangePassword": false },
  "access": {
    "permissions": [],
    "scoped": [ { "sagaType": "OrderSaga", "permissions": ["sagas.view", "sagas.data", "sagas.retry"] } ]
  },
  "passwordMinLength": 12
}
```

`access.permissions` holds the permissions the caller has for **every** saga type; `access.scoped` lists, per
saga type, what the caller has for that type alone. `user` is `null` for an anonymous caller and for the API
key, and `access` is `null` when anonymous. `setupRequired` is true while no user exists; `setupAvailable`
says the one-time setup can be completed now, and `setupProblem` (`{ code: "setup_unavailable", detail }`)
says why it cannot when it is required but not available.

### Administration routes (`/api/admin`)

All of them need `access.manage` **for all saga types**. The API key never holds it, so it answers `403`
here, as does a user whose `access.manage` grant is scoped to named saga types.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/admin/permissions` | The permission catalogue: key, name, description, `scopable` and `implies`, which the role editor and the SPA's effective-access preview read. |
| `GET` / `POST` | `/api/admin/users` | List the users; create one (`201` with a `Location`). The password must meet the [password policy](#passwords-lockout-and-rate-limits); `mustChangePassword` defaults to `true`. |
| `GET` / `PUT` / `DELETE` | `/api/admin/users/{id}` | One user; update (below); delete (`204`; also removes the user's grants and team memberships). |
| `POST` | `/api/admin/users/{id}/password` | An administrator sets a user's password. Ends the user's sessions; `mustChangePassword` defaults to `true`. |
| `POST` | `/api/admin/users/{id}/unlock` | Clears the failed-sign-in count and any lockout. |
| `GET` / `POST` | `/api/admin/teams` | List the teams; create one. |
| `GET` / `PUT` / `DELETE` | `/api/admin/teams/{id}` | One team; replace it (below); delete it. |
| `GET` / `POST` | `/api/admin/roles` | List the roles, the built-in ones included; create a custom one. |
| `GET` / `PUT` / `DELETE` | `/api/admin/roles/{id}` | One role; replace a custom role; delete one that no user or team holds. |

- **`PUT /api/admin/users/{id}` updates only the fields it is sent.** Its members are `displayName`,
  `isEnabled` and `grants`; one that is left out (or `null`) stays as it is, and `grants` replaces every
  grant the user holds directly. The username never changes, a password goes through the route above, and
  `teamIds` in a response is read-only: team membership is written only through the team.
- **`PUT` on teams and roles replaces the whole record**, members included: send every field. A team sent
  without `memberIds` or `grants` ends up with none, and a role's `permissions` must be the complete list.
- **A `404` from these routes is a problem with no `code`** (`title` "Not found", the `detail` names the
  user, team or role id). Every other refusal carries one: `400` `validation` (with `errors` keyed by the
  camelCase request path, such as `grants[0].sagaTypes`), and `409` `username_taken`, `name_taken`,
  `role_in_use`, `role_immutable` or `last_administrator` (see [Failure responses](#failure-responses)).
- **Request bodies reject unknown and duplicate members.** Every request under `/api/auth` and `/api/admin`
  is read strictly: a member the request does not have, a member named twice (case variants included), a value
  of the wrong type or a body that is not a JSON object is a `400` `validation` problem naming the member,
  not a silent no-op that answers `200` having changed nothing. A body over 16 KiB (`/api/auth`) or 4 MiB
  (`/api/admin`) is a `400` too.
- **The wire contract is pinned from both sides.** The admin records (`isEnabled`, `isBuiltIn`,
  `lastSignInAtUtc`, arrays always present and never `null`) are checked in as golden JSON files under
  `dashboard-web/src/app/testing/contracts/admin/`, which the .NET endpoint tests and the SPA's
  `admin-api.service.spec.ts` both assert.

`GET /hubs/saga` is the live-update hub, not a REST route: see [Live updates](#live-updates-signalr).

### Manual retry

A dashboard retry re-runs **one step, the one the saga failed in, for this saga type only**. It puts the
saga's `CurrentState` and `Status` back to what they were before that step and republishes that step's
message addressed to this saga type alone. The decision and the alternatives it replaced (replaying the
initiating message from the initial state, and a republish every subscribed saga type processed) are in
[ADR 0008](adr/0008-dashboard-retry-reruns-the-failed-step.md). A retry needs `sagas.retry` for the saga's
type (`403` otherwise, before anything is read), and is a boundary for that permission only because the redrive
is targeted: see [Grants and saga-type scope](#grants-and-saga-type-scope).

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
   message's type and original id, and recording who asked in its `sourceService`: `dashboard:<username>`,
   or `dashboard:api-key` for a retry made with the [API key](#api-key) (a name no user can take).
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
and is disabled beside the plan's reason when the plan is not retryable. The button is shown only to a user who
holds `sagas.retry` for that saga type; anyone else sees "You do not have permission to retry `<type>`
sagas." instead, on a saga a retry would accept.

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
caller. The detail, timeline and map endpoints pass whether the caller holds `sagas.data` for the saga's type;
the detail's `dataJson` is `null`, and is not even read, without it. The SignalR push of a single entry carries
neither field, for everyone (see [Live updates](#live-updates-signalr)).

Upgrade the dashboard API before the engine hosts: an API older than `StatePersisted` returns the entry
type as `21` and serves snapshot payloads unredacted (compose builds both together).

## Authentication

Two credentials reach the API, and both end in the same access model ([Access control](#access-control)):

- **A session cookie**, for people. A user signs in with a username and password and the API sets an
  HttpOnly cookie. The SPA and the SignalR hub it connects use nothing else.
- **The API key** (`Dashboard:ApiKey`), for scripts and probes: see [API key](#api-key).

A policy scheme picks the handler per request: one that carries `X-Api-Key` or `Authorization: Bearer` (or
`access_token`, on a hub endpoint) goes to the API-key handler, every other one to the cookie handler. Every
endpoint requires an authenticated caller unless it opts out explicitly, so an endpoint added without
thinking about access is protected, not open. The anonymous set is exactly `/health`,
`GET /api/auth/session`, `POST /api/auth/login`, `/logout` and `/setup`, and, in the Development environment
only, `/openapi/v1.json`.

### Signing in

A browser user opens the dashboard; with no session the SPA sends them to `/login`, and a successful
`POST /api/auth/login` answers the [session](#sign-in-routes-apiauth) and sets the session cookie.

- **The cookie.** Its name is `Dashboard:Session:CookieName` (default `vsaga.session`; compose sets
  `vsaga.session.<compose project>`, so stacks side by side on `localhost` do not overwrite each other's
  sign-in: browsers scope cookies by host, not port). It is HttpOnly, `SameSite=Strict`, has no expiry
  (the browser drops it when it closes) and is `Secure` on an HTTPS request. With
  `Dashboard:Session:RequireHttps=true` it is `Secure` always, gets the `__Host-` name prefix, and the API
  sends HSTS; the API then refuses to start unless [`Dashboard:TrustedProxies`](configuration.md#dashboardtrustedproxies)
  is set, because behind a TLS terminator the API sees plain HTTP unless it trusts the proxy's
  `X-Forwarded-Proto`.
- **What it holds.** The user's id, username and **security stamp**, never what the user may do. On every
  request the API reloads the user, their teams and the roles from the identity store and evaluates access
  afresh, so a revoked grant, a disabled account or a deleted user takes effect on the next request: there is
  no cache to expire. A session whose user is missing or disabled, or whose stamp no longer matches, gets
  `401` and its cookie is deleted. The stamp (128 random bits) rotates on a password change, an
  administrator's password reset, disabling and enabling the account, and when wrong current passwords on a
  password change lock it. Signing out deletes the browser's cookie and closes the user's live hub
  connections; it does not invalidate a copy of the cookie held elsewhere. Rotate the stamp (change the
  password, or disable the account) to end every session.
- **Timeouts.** `Dashboard:Session:IdleTimeoutMinutes` (default 480) slides: a request made once more than
  half of the window has passed since the session was last issued renews it for the whole window, so a
  session that has been idle for less than half of it never ends and one idle for all of it always does.
  `Dashboard:Session:AbsoluteTimeoutHours` (default 24) does not slide: the sign-in time is kept in the
  session ticket through every renewal, and a session older than that is rejected however active it was.
  Without it a detail page that refetches on every live push would keep one session alive indefinitely.
- **Must change password.** A user an administrator creates, and one whose password an administrator sets,
  has to choose their own password at the next sign-in unless the request said `mustChangePassword: false`.
  Until they do, their access is empty: every saga route answers `403` `password_change_required`, and
  `POST /api/auth/password` is the way out (the SPA sends them to the Account page). The seeded and the
  setup administrator do not have to.
- **From a script.** Read the session first: it issues the `XSRF-TOKEN` cookie that every unsafe request must
  echo (see [CSRF protection](#csrf-protection)), then post the credentials, then read the token again, since
  sign-in issues a new one:

  ```bash
  ui=http://localhost:4200
  jar=$(mktemp)
  curl -sS -c "$jar" "$ui/api/auth/session" >/dev/null
  token=$(awk '$6 == "XSRF-TOKEN" { print $7 }' "$jar" | tail -n1)
  curl -sS -b "$jar" -c "$jar" -X POST "$ui/api/auth/login" \
    -H "X-XSRF-TOKEN: $token" -H 'Content-Type: application/json' \
    -d '{"username":"admin","password":"dev-local-only-change-me"}'
  curl -sS -b "$jar" "$ui/api/sagas?pageSize=1"      # the session cookie authenticates this
  ```

  That is the sequence CI runs against the compose stack. A script that only reads is simpler with the
  [API key](#api-key).
- **While the identity store is down.** `/api/auth/*` answers `503` `identity_unavailable`, and a cookie
  session gets `401` without losing its cookie, so it works again when the store returns. The API key keeps
  working if it acts as a built-in role. See [The identity store](#the-identity-store).

### The first administrator

An empty identity store has no users, so the API needs a way to create the first one. There are three, and
which applies depends on configuration:

- **Seed.** When `Dashboard:Admin:Username` **and** `Dashboard:Admin:Password` are both set and valid (a
  username is 3 to 64 of `A-Z a-z 0-9 . _ @ + -`, starting with a letter or a digit, and not `api-key`; the
  password must meet the [policy](#passwords-lockout-and-rate-limits)), and the store has no users, start-up
  creates that user with the Administrator role for all saga types (no forced
  password change). It never touches a store that already has users, so changing the password setting later
  changes nothing. While **either** key is set, first-run setup is never offered, even when the seed cannot be
  applied (a missing counterpart, an invalid username, a password the policy rejects): an operator who
  configured a seed never gets an open claim instead. A seed that cannot be applied leaves the dashboard
  without a user; the `identity` health check reports `Degraded` with the reason (it names the setting to fix,
  never its value), `GET /api/auth/session` carries `setupProblem` (`code` `setup_unavailable`) and
  `POST /api/auth/setup` answers `409` with the same code and detail.
- **Setup code.** With no users and neither seed key set, start-up generates a one-time **setup code** and
  logs it once, at Warning, as `…with this one-time setup code: K7QD-M2XH-9TPA-W4RC…` (event id 7210; in a
  container it is in `docker logs` or `docker compose logs dashboard-api`, which only shows it once you drop
  the seed keys that `docker-compose.yml` sets). The SPA's `/setup` page asks for a username,
  display name, password and that code, and `POST /api/auth/setup` creates the administrator and signs them in.
  A generated code is 16 characters in four groups of four from an alphabet without look-alikes (no `0`, `O`,
  `1` or `I`: 80 random bits); codes are compared in constant time, ignoring case, spaces and hyphens.
  `Dashboard:Setup:Code` presets it instead (at least 16 characters, not counting spaces and hyphens, and at
  most 128; it is never logged, the log line only says where it came from) so an unattended install knows the
  code in advance. There is no time window: the code works until a user exists, and then never again. A
  restart before that issues a new generated code, and a preset code stays. Setup is rate limited per client
  address.
- **Reset.** `Dashboard:Admin:ResetOnStart=true`, together with both seed keys, is the way back in when the
  only administrator's password is lost or the account is locked out. At every start while it is `true` it sets
  the seed user's password to `Dashboard:Admin:Password`, enables the account, unlocks it, clears a forced
  change, rotates its stamp (ending its sessions) and restores an Administrator grant for all saga types in place
  of any narrower one (its other grants stay; the user is created if missing). It logs a Warning (event id
  7214) and an audit event each time: set it back to `false` once you can sign in. A password the policy
  rejects resets nothing and degrades `identity`. `ResetOnStart=true` with neither seed key stops the API at
  start, since there would be no account to reset.

In the compose stack, `docker-compose.yml` seeds `admin` / `dev-local-only-change-me`. The seed applies only to
an **empty identity volume**: the account is created on the first start and never touched again, so editing
`Dashboard__Admin__Password` afterwards changes nothing. Once the volume exists, change the password by signing
in and changing it on the Account page, or set `Dashboard__Admin__ResetOnStart=true` for one start together with
the new password in `Dashboard__Admin__Password`, or start over with `docker compose down -v`, which removes
every user.

### Passwords, lockout and rate limits

- **Policy.** A password is `Dashboard:Password:MinLength` characters (default 12; the key accepts 8 to 128) to
  128, is not the username (ignoring case) and, on a change the user makes, differs from the current one.
  There are no composition rules: length is what resists guessing, and the lockout and rate limits bound online
  attempts. Passwords are hashed with ASP.NET Core's `PasswordHasher<T>`, rehashed at sign-in when its
  parameters change. There is no multi-factor authentication and no single sign-on.
- **Lockout.** After `Dashboard:Lockout:MaxFailedAttempts` wrong passwords (default 5; `0` never locks, the
  key accepts up to 100) since the last successful sign-in, the account is locked for
  `Dashboard:Lockout:Minutes` (default 15, up to 1440). A wrong *current* password on a password change counts
  against the same counter, and the failure that locks the account also ends its sessions, the one that made
  the attempt included. On an account that is already locked or disabled the current password is not checked:
  the change is refused with `400` `invalid_credentials` and the session is left alone, so an outsider's failed
  sign-ins cannot end the owner's session. An administrator clears a lock with
  `POST /api/admin/users/{id}/unlock`.
- **Lockout can be used against you.** Anyone who knows a username (the demo's `admin` is public) can keep that
  account locked with five wrong passwords every fifteen minutes. Setting `Dashboard:Lockout:MaxFailedAttempts`
  to `0` and restarting stops *new* locks, but a lock already in force lasts until it ends: an administrator's
  unlock or [`ResetOnStart`](#the-first-administrator) ends it at once. A known-device cookie that would let a
  browser that has signed in before bypass a lock is not built ([ADR 0006](adr/0006-dashboard-authentication-and-identity-store.md),
  accepted residual risks).
- **Every failure looks the same.** An unknown username, a wrong password, a locked account and a disabled
  account all answer `401` `invalid_credentials` with the same body, and none answers sooner than about 300 ms
  after the request started (plus up to 50 ms of jitter), so neither the body nor the timing tells them apart.
  Unknown, disabled and locked accounts verify a dummy hash and count nothing, so the work is the same too. A
  username over 64 characters or a password over 128 is refused before any lookup with that same `401`. The
  reason is in the [audit log](#audit-log), never in the response.
- **Rate limits.** Sign-in and password change are limited to `Dashboard:RateLimit:AuthPerMinute` attempts
  (default 20, up to 1000) per one-minute window, per client address **and** username (the username normalised
  and hashed, so the limiter holds no submitted text); first-run setup gets the same number per client address.
  Password hashing is also limited across the whole API: at most `max(2, processors / 2)` verifications run at
  once and as many again wait, so a flood of sign-ins cannot starve the rest of the API. Past a limit the
  answer is `429` `rate_limited` with a `Retry-After` in seconds. Behind a proxy that
  [`Dashboard:TrustedProxies`](configuration.md#dashboardtrustedproxies) does not list, every browser arrives
  from the proxy's address and shares one bucket per username; the API logs a Warning about forwarded headers
  from an untrusted peer for that reason.
- **Request size.** `/api/auth` reads at most 16 KiB of body, refusing a larger one with `400` `validation`
  without reading past the cap: the login body is read before any limit applies, since the limit is keyed on
  the username in it.

### CSRF protection

The session cookie is `SameSite=Strict`, but cookies are not scoped by port, so another site on the same host
could still ride a session. Every **unsafe** request therefore has to carry an antiforgery token in the
`X-XSRF-TOKEN` header, which only script on the dashboard's own origin can read.

- **Where it is decided.** Per endpoint, after routing: every method except `GET`, `HEAD`, `OPTIONS` and `TRACE`
  on a matched endpoint is validated unless the endpoint carries an explicit exemption, and only the hub has
  one. No path prefix is involved, so `/API/...` is checked like `/api/...`. A request authenticated with the
  API key is exempt only when the key arrived in a header (`X-Api-Key` or `Authorization`), which a cross-site
  form cannot set.
- **Where the token comes from.** `GET /api/auth/session`, login, logout, setup and password change set it as
  the readable cookie `XSRF-TOKEN` (Angular's default name, which the SPA's HTTP client copies into
  `X-XSRF-TOKEN` by itself). A second, HttpOnly cookie named after the session cookie plus `.af` (for example
  `vsaga.session.vsaga.af`) carries the cookie half; it exists before sign-in and survives sign-out. The token
  is bound to the signed-in identity, so one read before sign-in fails after it: read the session again after
  every sign-in, sign-out, setup or password change. The SPA does.
- **A refused request.** `400` `antiforgery`. The SPA reads `GET /api/auth/session` again, which re-issues the
  cookie, and sends the request **once** more with the new token, so the user sees nothing: a live check
  showed `POST …/retry` answered `400`, the session read, the same `POST` answered `202`, and exactly one retry
  ran. If the token is still the one the request carried, or absent, it does not retry and the caller gets the
  `400`.
- **Two stacks in one browser.** The cookie name `XSRF-TOKEN` is the same in every stack, because browsers key
  cookies by host and not by port, so one stack can overwrite the token the other issued. A token from the
  other stack (a different key ring) is just another token that fails validation, so the same
  read-the-session-and-retry-once handles it. The session cookies do not collide, since compose names them per
  project.
- **Not for the hub.** A browser cannot add a header to a WebSocket upgrade, so the hub is exempt and an
  [origin check](#live-updates-signalr) guards it instead.

### API key

`Dashboard:ApiKey` is a machine credential for scripts, health probes and monitoring. It is not what the SPA
uses: the dashboard UI signs in with a username and password, and no key is in its bundle (`DASHBOARD_API_KEY`
and the SPA's API-key interceptor are gone).

- **Where to send it.** `X-Api-Key: <key>` is the form for machine clients. `Authorization: Bearer <key>` is
  accepted too. The `?access_token=` query string is accepted on the hub endpoints (`/hubs/...`) only, because
  anywhere else a cross-site link or form could supply it; that is what a SignalR client's
  `accessTokenFactory` falls back to for the WebSocket upgrade, which cannot carry a header. The key is
  compared in constant time.
- **Fails closed.** An unconfigured (empty) `Dashboard:ApiKey` denies every request that presents a key
  rather than silently disabling authentication.
- **What it may do.** The key acts as the role `Dashboard:ApiKeyRole` names, for every saga type. The default
  is **Viewer**: it can list and read sagas and their data, and it cannot retry one (`403`). Name `Operator`, or
  a custom role, for a machine client that must retry. A built-in role resolves from code; any other name from
  the identity store, and a name that matches no role makes every request with the key `401` and logs a Warning
  at start.
- **It never manages access.** `access.manage` is removed from the key's access whatever its role holds:
  `/api/admin/*` answers it `403` ("The API key never holds access.manage; sign in as a user who does."), and
  so does `POST /api/auth/password`. A role that contains `access.manage` logs a Warning at start. The username
  `api-key` is reserved (in any case), so no user can be recorded as the key.
- **Handle it as a password.** A key shorter than 24 characters logs a Warning at start, because key guesses
  are not rate limited and there is no lockout. A retry made with the key is attributed in the saga log to
  `dashboard:api-key`.

### Failure responses

Every failure is an `application/problem+json` body; most carry a `code` member the SPA and scripts can
switch on, and every 401 and 403 `detail` ends with "See docs/dashboard.md#authentication."

| Status | `code` | When |
| --- | --- | --- |
| `401` | `unauthenticated` | No credential the API accepts: no cookie, an expired one (idle or absolute), a revoked one (user disabled or deleted, stamp rotated), the identity store not ready, a missing or wrong API key, an unconfigured key, or an unknown `Dashboard:ApiKeyRole`. The body is **identical for every reason**, so a response cannot be used to probe which credential was close; the reason is logged server-side only. It names the three accepted key forms. |
| `401` | `invalid_credentials` | `POST /api/auth/login`: unknown user, wrong password, locked, disabled and over-long input alike. |
| `400` | `invalid_credentials` | A wrong current password (`errors.currentPassword`), a password change on a locked or disabled account (`errors.currentPassword`, the password was not checked), or a wrong setup code (`errors.code`). Not `401`: the session is fine, and the SPA signs out on a `401`. |
| `400` | `validation` | A body the API cannot accept: an unknown or duplicate member, a wrong type, too large, or values that break a rule. `errors` maps camelCase request paths (`grants[0].sagaTypes`) to messages. |
| `400` | `antiforgery` | The `X-XSRF-TOKEN` header is missing or does not match the session. See [CSRF protection](#csrf-protection). |
| `403` | `forbidden` | The caller lacks a permission: `permission` and `sagaType` members name what was checked (either can be `null`), and the `detail` says it in words ("This needs the sagas.retry permission for saga type 'OrderSaga'."). The hub's origin guard answers `403` `forbidden` too. |
| `403` | `password_change_required` | The user must change their password first; their access is empty until they do. |
| `404` | none | A user, team or role id that does not exist (`/api/admin`), as a problem with no `code`. An unknown saga answers a bare `404`. |
| `409` | `username_taken`, `name_taken` | Another user has that username, or another team or role that name, ignoring case. |
| `409` | `role_immutable`, `role_in_use` | A built-in role cannot be edited or deleted; a role still granted to a user or team cannot be deleted. |
| `409` | `last_administrator` | The change would leave no enabled user holding `access.manage` for all saga types. |
| `409` | `setup_unavailable` | First-run setup is not open: a user exists, seed keys are set, the seed could not be applied, or no code is in force. |
| `429` | `rate_limited` | A rate limit; `Retry-After` says when to try again. |
| `503` | `identity_unavailable` | The identity store is not ready, so nobody can sign in; the `identity` check on `/health` says why. |

The saga routes' own answers (`409` and `422` as `{ error }`, `502` as a problem with `restored`) are under
[Manual retry](#manual-retry) and carry no `code`.

## Access control

What a signed-in caller may do is decided by **roles** built from four **permissions**, handed out in
**grants** that cover all saga types or named ones, to users directly and through teams. Everything below is
enforced by the API on every request; the SPA only hides what the API would refuse.

### Permissions

| Permission | Label | Allows | Scope |
| --- | --- | --- | --- |
| `sagas.view` | View sagas | The saga list, an instance's summary, its timeline and map without payloads, state or error text, children, correlations, saga types, the retry plan and live updates. | per saga type |
| `sagas.data` | View saga data | The state blob, message payloads and error messages. Implies `sagas.view` for the same scope. | per saga type |
| `sagas.retry` | Retry sagas | `POST …/retry`. Implies `sagas.view` for the same scope. | per saga type |
| `access.manage` | Manage access | Everything under `/api/admin`. | all saga types only: it counts only in a grant for all saga types |

"Implies" means a role with `sagas.retry` alone can still open the saga it retries. `GET
/api/admin/permissions` serves this catalogue, including each permission's `scopable` and `implies`.

### Roles

Three roles are defined in code, with fixed ids so a grant written by one version names the same role in the
next. They are written to the store again at every start and cannot be edited or deleted
(`409` `role_immutable`).

| Role | Id | Permissions |
| --- | --- | --- |
| Administrator | `a0000000-0000-0000-0000-000000000001` | `sagas.view`, `sagas.data`, `sagas.retry`, `access.manage` |
| Operator | `a0000000-0000-0000-0000-000000000002` | `sagas.view`, `sagas.data`, `sagas.retry` |
| Viewer | `a0000000-0000-0000-0000-000000000003` | `sagas.view`, `sagas.data` |

A **custom role** is any non-empty subset of the four permissions under a name of 1 to 64 characters, unique
ignoring case (the built-in names are taken) and an optional description of up to 256. A role with
`sagas.view` alone is the way to show a person statuses without business data. A custom role cannot be
deleted while any user or team holds it (`409` `role_in_use`), and changing or deleting one closes every live
hub connection, because anyone, and the API key, may hold it. A role is managed through `/api/admin/roles`
or the SPA's Administration area.

### Grants and saga-type scope

A **grant** pairs one role with a scope: **all saga types** (every type, including types that have not run
yet and types added later), or **1 to 100 named saga types**. A saga type name is 1 to 200 characters,
trimmed, not blank and free of control characters, and it is matched exactly (ordinal, so `ordersaga` does not
cover `OrderSaga`). A user or a team holds at most 20 grants, one per role.

A user's **effective access** is the union of their own grants and the grants of every team they belong to:
for each permission, the saga types it is held for. A disabled user has none, and so does a user who must
change their password. `access.manage` in a grant for named saga types confers nothing: it is not scopable,
and only a grant for all saga types gives it. The invariant "at least one enabled user holds `access.manage`
for all saga types" is checked inside every change, on the proposed result, and a change that would break it
is refused with `409` `last_administrator` (disabling or deleting the last administrator, removing their
grant or their membership of the team that gave it, editing or deleting such a team).

**A worked example.** The team `payments` holds **Operator** for `InvoiceFollowUpSaga`, and Dana, a member
of it, also holds **Viewer** directly for `OrderSaga`. Her session says:

```json
"access": {
  "permissions": [],
  "scoped": [
    { "sagaType": "InvoiceFollowUpSaga", "permissions": ["sagas.view", "sagas.data", "sagas.retry"] },
    { "sagaType": "OrderSaga",           "permissions": ["sagas.view", "sagas.data"] }
  ]
}
```

- Her list, `GET /api/saga-types` and `GET /api/correlations/{id}` show those two saga types and no other.
- `GET /api/sagas/PostShipmentChoreography/{id}` answers `403` (`code` `forbidden`, `permission` `sagas.view`,
  `sagaType` `PostShipmentChoreography`) even for an id she knows, before anything is read.
- She can retry a timed-out `InvoiceFollowUpSaga`. Retrying a failed `OrderSaga` answers `403`: "This needs the
  `sagas.retry` permission for saga type 'OrderSaga'."
- Remove the team's grant and her next request already has no access to `InvoiceFollowUpSaga`; the API closes
  her live connection and her client reconnects under the new access (see [Live updates](#live-updates-signalr)).

**The targeted-retry boundary.** A per-saga-type `sagas.retry` grant is a real boundary only because a
dashboard retry is **targeted**. It republishes the failed step's message, and in the sample
`PostShipmentChoreography` handles the same `InvoiceIssued` message as `InvoiceFollowUpSaga` under the same
correlation id. The redrive therefore carries the header `x-vsaga-target-saga-type: InvoiceFollowUpSaga`, and
every other saga type acknowledges the message and ignores it: nothing is deserialised, no instance is looked
up, no entry is written. Dana's grant does not let her drive `PostShipmentChoreography`. The boundary has the
limits [Manual retry](#manual-retry) and ADR 0008 record: a host running an engine older than the targeted
redrive ignores the header and processes the replay as a new delivery, and a consumer that is not a saga
receives it as a new message. A scoped `sagas.retry` grant holds only when every engine host on the bus runs an
engine that contains it.

### What a scoped caller sees

- **Lists and lookups are filtered, not refused.** `GET /api/sagas` returns only instances of the saga types
  the caller may view, and `GET /api/saga-types`, `GET /api/correlations/{id}` and a saga's `/children`
  leave out the types they may not. A `sagaType` filter for a type outside the scope is an empty page, not a
  `403`; a blank `sagaType` counts as no filter. A caller scoped to one type is served by one provider query,
  so a page past the end is an empty `200`.
- **A caller scoped to several types is served by a merge in the API.** `ISagaSummaryReader.ListAsync`
  filters by at most one saga type and the persistence contracts do not change, so the API reads one stream
  per visible type and merges them in the requested order (ties between types break by saga type). The cost of
  that is bounded: a list ordered by `UpdatedAt` with no status or kind filter and no search covers at most 50
  types and reaches a depth (`page × pageSize`) of 10,000 rows; every other shape (a `Status` sort, a status or
  kind filter, a search) covers at most 10 types and reaches 500 rows, because on Redis those shapes read every
  member of every visible type. Past a bound the answer is `400` `{ error, maxPage }`, and the error
  names the saga-type filter, which always works. Scoped names beyond a shape's type bound are narrowed to the
  types that have run, read through a short cache. `totalCount` is the sum of separate per-type counts, as loose
  under concurrent writes as offset paging already is.
- **Per-instance routes answer `403` before any read** when the route's saga type is out of scope.
- **Without `sagas.data` the business data is withheld** (see
  [Redaction seam](#state-snapshots)): the detail's `dataJson` is `null` and is not read, every timeline entry
  loses its `payloadJson` and `errorMessage`, and every map event its `errorMessage`. No entry is dropped, so
  sequence numbers and the timeline's steps are the same for every caller. Exception text thrown by saga code
  often names an order, a customer or a card, which is why it counts as data.
- **What `sagas.view` still exposes**: correlation ids, statuses and timestamps, state names and message type
  names, service names, the parent saga type and correlation id, which steps failed, and the actor
  (`dashboard:<username>`) on a retry entry. Grant `sagas.view` knowing that.
- **Live updates follow the same scope**: see [Live updates](#live-updates-signalr).
- **In the SPA**, a saga type the user cannot view shows "You do not have access to `<type>` sagas. Ask an
  administrator for sagas.view on this saga type."; a user with no view permission at all sees "Your account has
  no access to any saga type yet. Ask an administrator for sagas.view."; Retry is replaced by "You do not
  have permission to retry `<type>` sagas." and the Saga data bar and the per-step Data buttons give way to
  "Saga data is hidden for your role. It needs the sagas.data permission."

### The identity store

Users, teams, roles, grants and the key ring that protects sessions live in a store the dashboard owns,
behind `IDashboardIdentityStore`, implemented on **SQLite** (the only value of `Dashboard:Identity:Provider`;
anything else stops the API at start). It is independent of `Persistence:Provider`: whichever store holds the
sagas, the dashboard's users are in a separate file, and the engine hosts never read it. The code is two
projects that are not published as packages, `VSaga.Dashboard.Identity` and `VSaga.Dashboard.Identity.Sqlite`
(the generated migrations only); the tables are `Users`, `Teams`, `TeamMembers`, `Roles`, `UserGrants`,
`TeamGrants` and `DataProtectionKeys`.

- **Where the file is.** `Dashboard:Identity:Sqlite:Path`. In a container (`DOTNET_RUNNING_IN_CONTAINER=true`,
  which the official images set) it **must** be set: there is deliberately no default, because a database in
  the container layer would lose every user and the key ring when the container is recreated, and an unset path
  is reported by the `identity` health check instead. Outside a container the default is
  `{LocalApplicationData}/vSaga/dashboard/identity.db`. The resolved path is logged at start (event id 7200).
  The dashboard API image sets `/var/lib/vsaga-dashboard/identity.db`, runs as the aspnet image's non-root `app`
  user (uid 1654) and creates that directory owned by it; compose mounts the named volume
  `vsaga-dashboard-identity` there, so users and sessions survive `docker compose up`, a rebuild and a
  recreate, and `docker compose down -v` removes them (the seeded administrator is then created again). A
  volume first created by a container running as root stays root-owned, and the non-root API then cannot open
  it.
- **How it starts.** On start the API creates the directory (mode `0700` on Linux) and the file (`0600`; SQLite
  gives any `-wal` or `-shm` side file the main file's mode), applies the migrations, writes the three built-in
  roles from code and then seeds the first administrator or opens setup. This never throws and gives up after 30
  s: an unusable store (an unset path, a read-only volume, a file that is not a SQLite database, a migration
  lock left behind by a killed process) leaves the saga views and the API key working, and is retried at most
  every 10 s when `/health` or a sign-in endpoint asks. Meanwhile `identity` is `Degraded` with the reason
  (the path is in the log, not in the anonymous `/health`), cookie sessions get `401`, and `/api/auth/*` answers
  `503`.
- **One API instance per file.** SQLite cannot serve several API replicas, and its locking is unreliable on
  network file systems. Several replicas would need another `IDashboardIdentityStore` on a shared database, a
  shared key ring and a SignalR backplane ([ADR 0006](adr/0006-dashboard-authentication-and-identity-store.md)).
- **A backup of the volume is a credential backup.** The file holds password hashes, security stamps, the
  lockout counters and the Data Protection key ring, which is stored **unencrypted** next to them (ASP.NET Core
  warns about that at start): anyone who can read the file can forge a session for any user. Keep the volume and
  its backups where you keep secrets, and take a consistent copy (stop the API, or use SQLite's own backup
  command; include any `-wal` and `-shm` files if you copy the files themselves).
- **Encrypting the key ring at rest** is the framework's `ProtectKeysWithCertificate` on the Data Protection
  builder. vSaga ships no configuration key for it: `AddDashboardIdentity` registers Data Protection with the
  application name `VSaga.Dashboard` and the store-backed key ring, and a deployment that wants encryption adds
  the certificate call to the API's composition itself. That path is not shipped or exercised in this
  repository. Losing the certificate then costs every session (the keys cannot be read, so users sign in
  again), not the passwords.
- **Changing the schema** takes a generated migration with `dotnet ef … --context DashboardIdentityDbContext`;
  the exact commands are in [`CONTRIBUTING.md`](../CONTRIBUTING.md#test).

## Audit log

Sign-ins and access changes are logged as structured events under one log category, `VSaga.Dashboard.Audit`,
so an operator can route them on their own (for example with a `Logging:LogLevel` entry or a log filter on
that category). Each event carries the **actor**, the **action**, the **target** and the **outcome**, and the
client address when there is a request; the actor is `dashboard:<username>`, `dashboard:api-key`,
`vsaga:configuration` (seeding and `ResetOnStart`) or `vsaga:setup` (first-run setup), names no username can
produce. Passwords, hashes and setup codes are never logged, and a *submitted* username only when it passes
the username rule (3 to 64 of `A-Z a-z 0-9 . _ @ + -`, starting with a letter or a digit, not `api-key`).

| Event id | Name | Level | What |
| --- | --- | --- | --- |
| 7100 | `AccessChanged` | Information | An access change was committed: `user.create`, `.update`, `.delete`, `.reset-password`, `.unlock`, `.change-password`, `.seed`, `.reset-on-start`, `.setup`; `team.create`, `.update`, `.delete`; `role.create`, `.update`, `.delete`. |
| 7101 | `AccessChangeRejected` | Warning | An access change was refused; the outcome is the problem code (`validation`, `not_found`, `invalid_credentials`, `last_administrator` and the other rule codes). |
| 7102 | `AccessChangeNotificationFailed` | Warning | The change stands, but telling live hub connections failed: they keep their old access until they reconnect. |
| 7110 | `SignedIn` | Information | A sign-in (login or setup) succeeded. |
| 7111 | `SignInFailed` | Warning | A sign-in failed, with the reason the response never gives: `UnknownUser`, `WrongPassword`, `Disabled`, `LockedOut`, `Malformed` or `PasswordChanged`. |
| 7112 | `AccountLockedOut` | Warning | A failed sign-in or a wrong current password locked the account, until when. |
| 7113 | `SignedOut` | Information | A user signed out. |
| 7114 | `AuthRateLimited` | Warning | A sign-in, password change or setup was refused by a rate limit. |
| 7115 | `SessionEnded` | Warning | A session ended because a password change locked the account. |

A retry's attribution is in the saga log instead: the `ManualRetryRequested` entry's `sourceService` is the
actor. The dashboard's other log events (the identity store's start-up, first-run setup, the hub, antiforgery
and the API key warnings) are listed in [`observability.md`](observability.md#dashboard-log-events).

## Live updates (SignalR)

`SagaHub` is mapped at `/hubs/saga` (see `dotnet/src/VSaga.Dashboard.Api/Program.cs`). It needs an
authenticated caller like every other route, checks access in its own methods, and is the one endpoint exempt
from the antiforgery check, guarded by an origin check instead (below).

**Authentication.** A browser connection is authenticated by the session cookie, on the negotiate request and
on the WebSocket upgrade: same origin, so the browser sends it, and no token is in any URL or in the bundle (the
SPA's WebSocket URL carries no `access_token`). A script authenticates with the [API key](#api-key): `X-Api-Key`
or `Authorization: Bearer` on negotiate, and, for the upgrade that cannot carry a header, `?access_token=`,
which is accepted on hub endpoints only. A SignalR client's `accessTokenFactory` does exactly that.

**Subscriptions.** Every push targets a group, and a connection receives nothing until it joins one by
invoking a hub method. Access is resolved **afresh from the identity store at each call**, not as it was when
the connection opened. A refusal is an answer, not an exception: the two subscribe methods return a boolean,
`false` meaning nothing was joined.

| Method | Returns | Joins |
| --- | --- | --- |
| `SubscribeToList()` | `bool` | `saga:list` when the caller may view every saga type; otherwise one `saga-list:{sagaType}` group per saga type the caller may view (a type that has not run yet included). `false`, and no group, when the caller may view none. The connection also leaves list groups that its access no longer covers. |
| `UnsubscribeFromList()` | | Leaves the list groups. |
| `SubscribeToSaga(sagaType, correlationId)` | `bool` | `saga:{sagaType}:{correlationId}`, only when the caller holds `sagas.view` for that saga type. `false` otherwise, and for a malformed `correlationId` (passed as a string, so a stale or hand-edited URL joins no group instead of failing the invocation). |
| `UnsubscribeFromSaga(sagaType, correlationId)` | | Leaves that instance's group. |

A connection that joins only an instance's group receives updates for that instance alone. (The SPA shares one
hub connection across pages, so its detail page also filters incoming `SagaUpdated` pushes by
`(sagaType, correlationId)` client-side.)

**Pushes.** Two paths push into the groups:

- **In-process** (`SignalRSagaChangeNotifier`), used when the hub and the saga engine share a process.
- **Cross-process** (`SagaChangePollingService`), the path that actually delivers live updates in the deployed
  topology, since sagas normally run in a separate process (e.g. `OrderProcessing`) from the dashboard API. A
  background timer diffs the store since its last watermark and pushes the difference; the watermark only
  advances after a successful push, so a tick that throws retries the same window on its next tick instead of
  skipping past it.

Pushes arrive as two client methods, with enums serialized as their string names. `SagaUpdated(summary)`, a
`SagaSummary`, reaches `saga:list`, the saga type's `saga-list:{sagaType}` group and the instance's group, so a
scoped caller gets exactly the updates for the types they may view.
`TimelineEntryAdded(sagaType, correlationId, entry)`, a `SagaLogEntry` with the saga type as a leading
argument, goes to the instance's group only. A push carries no business data: the summary has no state blob, and
the entry carries its stored `sequenceNumber` but **never its `payloadJson` or `errorMessage`**, for anyone,
because a group is joined per saga, not per permission, so a push cannot be redacted per caller. A client that
wants the data refetches the timeline, which is redacted for the caller's own access (see
[State snapshots](#state-snapshots)). Only the in-process
path sends `TimelineEntryAdded`; the poller pushes `SagaUpdated` alone, so in the deployed topology a client
refetches the timeline and map when an instance's `SagaUpdated` arrives. The SPA's detail page treats both
pushes the same way: it patches the summary from a `SagaUpdated` at once, and coalesces every push for its saga
within 250 ms into one refresh of the timeline, the map, the related and child sagas and the saga itself. Every
reconnect after the first connection runs one such refresh too, because pushes sent while the hub was down are
lost.

**Origin guard.** WebSockets bypass CORS, and `SameSite` treats every `localhost` port as one site, so a page on
another origin (another site, or anything else on another port of this host) must not open a connection that
rides the user's session. On the hub's endpoints (negotiate and connect) the API accepts a request only when it
has:

- **no `Origin` header.** A browser always sends one on a WebSocket and on a `POST`, so a request without one
  is not a browser: `curl`, a script or a SignalR client running outside a browser send none and are allowed
  (they authenticate with the API key or a cookie of their own);
- an `Origin` equal to the request's own `{scheme}://{Host}`, the scheme being the one a trusted proxy
  forwarded; or
- an `Origin` equal to [`Dashboard:WebOrigin`](configuration.md#dashboardweborigin), when that is set.

Anything else is `403` `forbidden`, `Origin: null` and several `Origin` headers included, and the API logs a
Warning with the received and expected values (event id 7311) so an operator whose proxy rewrites the scheme
or host can see why. A live check against the compose stack: `Origin: http://localhost:4200` through port 4200
negotiated, `http://localhost:9999` and `null` answered `403`, and no `Origin` negotiated.

**When access changes while a socket is open.** A WebSocket is authenticated once, when it connects, and its
groups were joined under the access the caller had then, so the API closes the connections an access change
affects: a user's own on a password change, a sign-out, an administrator's password reset, disabling,
enabling and deleting the account, and any change to their grants or to a team they belong to (members before
and after); and **every** connection, the API key's included, when a role is changed or deleted. The close is
**not an abort**. The connection is closed with a close message that allows the client to reconnect (the way
SignalR itself closes a socket whose cookie ticket has expired), so the client reconnects, authenticates
again and resubscribes, which resolves access afresh. A user who was disabled or deleted, or whose session's
security stamp was rotated, gets `401` on that negotiate, and a client that sees its session gone stops. An
abort would tell the client *not* to reconnect and leave the dashboard without live updates until the page is
reloaded, so the API aborts only as a fallback, for a connection that cannot be closed the other way (event
id 7321, Warning; 7322, Error, if even the abort fails and the connection may stay open). The API logs `Closed N
live hub connection(s) because …` at Information (event id 7320).

Checked live with the real `@microsoft/signalr` client and `withAutomaticReconnect`, through the nginx
container: a role change closed the connection, the client reconnected within 100 ms (`RECONNECTING` 62 ms after
the `PUT`, `RECONNECTED` 28 ms later), resubscribed, and pushes continued; disabling the user closed the
connection, the client's four reconnect attempts failed, and a negotiate with that cookie answered `401`. The
SPA's hub client keeps retrying through an API restart (it shows "Reconnecting to live updates…" and recovered
about 8 s after the restart in a live check), and asks the session whether it is still alive after a failed
reconnect, stopping only when the answer is "anonymous" so that a restart never ends live updates.

**Expiry.** The hub closes a socket whose cookie ticket has expired (`CloseOnAuthenticationExpiration`, with
the same close message). The API caps the expiry it reports for the ticket at the sign-in time plus
`Dashboard:Session:AbsoluteTimeoutHours`, not only the sliding idle expiry, so no socket outlives the session's
absolute lifetime; the client's reconnect then meets a `401` if the session has ended.

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

### Signing in, access and the administration area

The SPA reads the session (`GET /api/auth/session`) before it routes anywhere, and what it shows follows what
that says. Its pages are lazy routes where the bundle budget asks for it (the saga detail page, the sign-in
pages and the whole administration area), and a route's guard runs before its chunk is requested, so a
visitor who is turned away never downloads it.

| Route | For |
| --- | --- |
| `/login` | A visitor with no session. One who is already signed in is sent on to the page the sign-in was for. |
| `/setup` | Only while no user exists: username, display name, password and the one-time setup code. |
| `/account` | A signed-in user: change password. The one page a user who must change their password may use; the guards send them there. |
| `/sagas`, `/sagas/:sagaType/:id` | A signed-in user. |
| `/admin/users`, `/admin/teams`, `/admin/roles`, each with `new` and `:id` | A user holding `access.manage` for all saga types; anyone else is sent to the saga list. |

- A visit without a session goes to `/login?returnUrl=<page>` and the sign-in returns to it. `returnUrl` is
  honoured only as an absolute path of the app: `//host`, a full URL, `/login` and `/setup` all fall back to
  the saga list. While no user exists every guarded page goes to `/setup`. An API that cannot be reached, or
  whose identity store is down, is said so on the login page, which asks again by itself.
- A `401` on any API call means the session ended behind the SPA's back (it expired, or an administrator
  disabled the user): the SPA signs out locally, stops the live connection and goes to
  `/login?returnUrl=…&reason=expired` with "Your session expired. Sign in again to continue." A `403` makes it
  read the session again (at most every 5 s), since access may have changed. An API restart does not sign the
  UI out: a failed read of the session keeps the last known one, and the hub reconnects by itself.
- The user menu in the top bar offers Account and Sign out, and the Administration link appears only with
  `access.manage`. Retry, saga data and whole saga types are withheld as described under
  [What a scoped caller sees](#what-a-scoped-caller-sees); the SPA hides what the API would refuse and never
  decides access itself.
- The administration area lists users, teams and roles, each with its edit page. A **user** has a display name,
  an enabled switch, grants, the teams they are in (read-only: membership is written through the team), and an
  effective-access preview that shows what the draft grants amount to and which grant each permission comes from;
  an administrator can also reset the password, unlock the account or delete it. A **team** has a name, a
  description, members (checkboxes with a filter) and grants; a **role** has a name, a description and
  permissions. The grants editor offers one grant per role, either all saga types or a selection, and takes
  the exact name of a saga type that has not run yet. A refusal under the `last_administrator` rule shows a
  banner and keeps what was typed. On a user's own record the SPA turns Enabled and Delete off.

### The saga detail page

**Summary card.** Type, correlation id, kind, status, current state and version, and `Created` and
`Updated` in the browser's local time with the zone in the label (`Created (UTC+02:00)`; hover for
UTC). A `Failed` or `TimedOut` saga gets the Retry button for a user who may retry it (see
[Manual retry](#manual-retry)).

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
name is "Data at start". Viewing data needs the `sagas.data` permission for the saga's type; without
it the buttons are disabled beside "Saga data is hidden for your role. It needs the sagas.data
permission." and the steps show no Data buttons.

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
- **No query string is logged.** The access log records the path only, so the API key that a machine
  SignalR client sends as `?access_token=` on the hub upgrade never reaches it (the SPA's own connection
  sends no token at all: its session cookie authenticates it), and nginx's error log is raised to `crit`, because its
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
Framing is nginx's policy alone: the API sends no `X-Frame-Options` of its own (ASP.NET antiforgery's
default `SAMEORIGIN` is switched off), so a proxied API response carries the single `DENY`.

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
- **`Dashboard:Session:RequireHttps=true`** once TLS is in front, so the session cookie is `Secure` always and
  HSTS is sent. The whole list, with the passwords and keys to change, is under
  [Deploying beyond localhost](#deploying-beyond-localhost).

## Deploying beyond localhost

The compose stack is a demo. It binds the dashboard to `127.0.0.1` because it ships public credentials (an
administrator password and an API key committed in `docker-compose.yml`), so only the machine it runs on can
reach it. Before anyone else can:

- **Put TLS in front, and set `Dashboard:Session:RequireHttps=true`.** Without TLS, passwords and the session
  cookie cross the network in clear. With the setting the cookie is `Secure` always, carries the `__Host-`
  name prefix and the API sends HSTS. The API refuses to start with it unless `Dashboard:TrustedProxies` is
  set, since behind a TLS terminator it sees plain HTTP unless it trusts the proxy's `X-Forwarded-Proto`.
- **Make the proxy pass what the API needs**: WebSocket upgrades on `/hubs/`, the browser's `Host` unchanged, and
  `X-Forwarded-Proto` and `X-Forwarded-For` (see [Behind your own proxy or TLS](#behind-your-own-proxy-or-tls));
  set `DASHBOARD_OUTER_PROXY=true` on the `dashboard-web` container when a TLS terminator sits in front of it.
  A proxy that rewrites the host or scheme makes the hub's origin check refuse the browser: the Warning
  (event id 7311) shows the received and expected values.
- **Narrow `Dashboard:TrustedProxies` to your proxy's address or network.** Compose trusts every private range
  for the demo, and any peer in the list can assert a client address and scheme. See
  [`Dashboard:TrustedProxies`](configuration.md#dashboardtrustedproxies).
- **Choose the bind address deliberately.** Change the `127.0.0.1:` prefix of the ports in the compose file only
  for what must be reachable: normally the UI, behind the TLS proxy, and the API's own port only for machine
  clients that need it directly, also behind TLS.
- **Change the administrator's password.** `dev-local-only-change-me` is public. The seed applies only to an
  empty identity volume, so editing `Dashboard__Admin__Password` on a stack that already has one changes
  nothing: sign in and change it on the Account page, or set `Dashboard__Admin__ResetOnStart=true` for one
  start together with the new `Dashboard__Admin__Password`, or start over with `docker compose down -v`. Or
  seed nothing: leave `Dashboard__Admin__*` unset and claim the first administrator with the
  [setup code](#the-first-administrator) from the API log (turn `ResetOnStart` back off afterwards).
- **Change the API key**, to a long random value (the API warns below 24 characters), and keep
  `Dashboard:ApiKeyRole` at `Viewer` unless a machine client must retry. The key is not rate limited and has no
  lockout.
- **Set `Dashboard:WebOrigin` only for a cross-origin, read-only client.** The bundled UI is same-origin and
  needs no CORS. The antiforgery check assumes a same-origin page that can read the `XSRF-TOKEN` cookie and
  echo it (Angular's own XSRF handling, which the bundled SPA relies on, skips cross-origin requests, and a
  page on another host cannot read the cookie), so treat the setting as read-only cross-origin access: a
  request from that page that changes something is refused with `400` `antiforgery`. The hub's origin check
  admits the origin too.
- **Back up the identity volume, and treat the backup as credentials** ([The identity store](#the-identity-store)).
  Run **one** API instance against it.
- **Ship the [audit log](#audit-log)** and watch its Warnings: failed sign-ins (7111), lockouts (7112), rate
  limits (7114), and the hub's rejected origins (7311).
- **Check `/health`**: `identity` should be `healthy`, and an administrator should be able to sign in.

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
