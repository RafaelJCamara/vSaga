# ADR 0006: Dashboard users sign in with a session cookie; access is role and saga-type scoped; identity lives in a dashboard-owned SQLite store

**Status:** **Accepted** — 2026-10-02. **Implemented** — 2026-10-05.
**Date:** 2026-10-02
**Supersedes:** the shared-API-key position recorded in
[`../history/project-origins-and-hardening-pass.md`](../history/project-origins-and-hardening-pass.md)
("Dashboard API authentication") and described in [`../dashboard.md`](../dashboard.md#authentication).
The history file stays as written; this record replaces the decision, not the account of it.
**Relates to:** [`0008-dashboard-retry-reruns-the-failed-step.md`](0008-dashboard-retry-reruns-the-failed-step.md).
A retry that re-drives only the retried saga type is what makes a scoped `sagas.retry` grant (decision 3)
a real boundary; without it a scoped retry reaches every saga type that consumes the replayed message.
**Implementation plan:** [`../design/dashboard-usability-and-access.md`](../design/dashboard-usability-and-access.md)
**Implemented by** (the numbers are those of the design's §12, which lists every follow-up too): the
identity project and services, `e9eea61` and `69c993f` (C31, C32); start-up, migrations and the `identity`
health check, `e6228c3` (C33); the policy scheme, the fallback policy and `no-store`/`nosniff` on `/api`,
`8b7a39a` (C34); sign-in, antiforgery, rate limits and lockout, `ed8f939` (C35); the first administrator,
`82c4797` (C36); the volume, the non-root image and the seeded demo administrator, `f25b1ec` (C37);
saga permissions, scoped lists, redaction and retry attribution, `e69deb8` (C38), on the redaction seam of
`4fce969` (C18); the administration endpoints, `8d69429` (C39); the hub, `852a901` (C40) and, closing
connections for reconnect, `e913282`; the sign-in checks in CI, `64ba0ac` (C41); and in the SPA the hub
service `fabc67c` (C43), the session `0f3bb28` (C44), the login, setup and account pages `d78d7cc` (C45),
the session requirement and the removal of the key from the bundle `193e679` (C46), permission-gated pages
`731b724` (C47) and the administration area `b3b67e1`, `cec2a40` and `7e61f16` (C48 to C50). The design's
§12 also records where the build departed from the plan, and §13 what is still open.

---

## Context

The dashboard API is protected by one shared key, `Dashboard:ApiKey`. It was chosen over JWT, OIDC or
basic auth as the right fit for an internal operations dashboard with no identity infrastructure, and
its limitation was accepted in writing at the time: the key is compiled into the SPA
(`DASHBOARD_API_KEY` in `dashboard-web/src/app/api-config.ts`, sent by
`dashboard-web/src/app/interceptors/api-key.interceptor.ts` and by the hub connection's
`accessTokenFactory`), so it "closes off unauthenticated direct API access; it is not per-user
authentication or authorization". The compose file commits the same value
(`Dashboard__ApiKey` in `docker-compose.yml`).

That trade was sound while the dashboard only read. It no longer holds, for three reasons.

1. **Anyone who can load the page can retry a saga, and a retry has business effects.**
   `POST /api/sagas/{sagaType}/{correlationId}/retry` republishes a recorded message through
   `IMessageTransport.PublishRawAsync` (`dotnet/src/VSaga.Dashboard.Api/Endpoints/SagaEndpoints.cs`).
   The step runs again with whatever it does: a charge, a reservation, an email. Loading the SPA hands
   the browser the key, so every visitor holds the retry capability, and the timeline cannot say who
   used it.
2. **Saga data is business data, and the key cannot show it to some people only.** The detail
   response carries the state blob; the timeline carries message payloads and the exception text thrown
   by saga code. A support engineer who should see statuses but not customer records gets either
   everything or nothing.
3. **Nothing can be revoked short of changing the key**, which means rebuilding the SPA and
   redistributing a secret to every machine client.

Two facts shape the options. First, the same work moves the SPA into compose behind an nginx container
that proxies `/api/` and `/hubs/` to the API (see the design document), so the browser and the API share
one origin and an HttpOnly cookie becomes usable. Second, the retry redrive is addressed by correlation
id: today every saga type subscribed to the message type processes it again (the comment above the
`PublishRawAsync` call says so). A per-saga-type retry permission is therefore only a boundary together
with ADR 0008, which targets the redrive at one saga type.

---

## Decision

**Browser users sign in with a cookie session. What a signed-in caller may do is decided by roles
built from four permissions, granted per saga type. Users, teams, roles, grants and the Data Protection
key ring live in a store the dashboard owns, behind `IDashboardIdentityStore`, implemented first on
SQLite. The API key stays, as a machine credential with a fixed role.**

The eleven sub-decisions below settle how.

### 1. Cookie sessions, with antiforgery on every unsafe request

- ASP.NET Core cookie authentication: HttpOnly, `SameSite=Strict`, non-persistent, sliding expiry of
  `Dashboard:Session:IdleTimeoutMinutes` (default 480). The cookie name is
  `Dashboard:Session:CookieName` (default `vsaga.session`); compose sets it per project, because
  browsers do not scope cookies by port and stacks side by side on `localhost` would otherwise overwrite
  each other's session.
- `SecurePolicy` is `SameAsRequest`. `Dashboard:Session:RequireHttps` (default false) switches to
  `Always`, the `__Host-` name prefix and HSTS, and makes start-up fail when forwarded headers are not
  configured, so a TLS terminator that hides the scheme cannot silently produce cookies without
  `Secure`.
- The cookie holds the user id, the username and the user's **security stamp**, 128 random bits rotated
  on a password change, an administrator reset, disable and enable. Every request reloads the user and
  rejects the cookie when the user is missing, disabled, or the stamp differs. Revocation is
  immediate; there is no cache.
- A policy scheme sends each request to the API-key handler when it carries an API-key credential and to
  the cookie handler otherwise. The fallback authorization policy requires an authenticated caller on
  every endpoint; the anonymous set is exactly `/health`, `GET /api/auth/session`, login, logout and
  setup (plus the OpenAPI document in Development). Both handlers write the same `401` problem body,
  which keeps naming `X-Api-Key` and pointing at `docs/dashboard.md#authentication`.
- **Antiforgery.** Tokens are bound to the signed-in identity, so they are issued after
  `HttpContext.User` is replaced: the session, login, logout, setup and password endpoints set a
  script-readable `XSRF-TOKEN` cookie and the SPA echoes it as `X-XSRF-TOKEN`, Angular's default.
  Enforcement is decided by endpoint metadata after routing, not by path prefix: every unsafe method on
  a matched endpoint requires a valid token unless the endpoint carries an explicit exemption, and only
  the hub carries one (SignalR's negotiate is a POST with no header). A failure is `400` with code
  `antiforgery`; the SPA refetches the session and retries once. `XSRF-TOKEN` keeps its fixed name, so
  two stacks on one host share it, and that refetch-and-retry is what heals a token issued by the other
  stack.
- **Passwords.** The framework's `PasswordHasher<T>`, rehashing on sign-in when its parameters change.
  The policy is a length of `Dashboard:Password:MinLength` (default 12) to 128 and inequality with the
  username; no composition rules. A new password equal to the current one is refused.
- **Throttling.** Login and password change are rate limited per client address plus hashed,
  normalised username (`Dashboard:RateLimit:AuthPerMinute`, default 20); setup per address. A global
  concurrency limiter around password verification answers `429` when full, so hashing cannot starve the
  API. After `Dashboard:Lockout:MaxFailedAttempts` failures (default 5; 0 disables) an account is
  locked for `Dashboard:Lockout:Minutes` (default 15). A wrong current password on a password change
  counts against the same counter. Every failed sign-in, whether the username is unknown, the password
  wrong, the account locked or disabled, answers the same `401 invalid_credentials` and completes no
  earlier than a fixed floor, so neither body nor timing says which. Over-long usernames and passwords
  are rejected before any lookup or hashing, with the same answer.
- **Absolute lifetime.** The sign-in time is a ticket property that survives sliding renewal, and a
  ticket older than `Dashboard:Session:AbsoluteTimeoutHours` is rejected. Without it, a detail page that
  refetches on every live push would keep one session alive indefinitely.

### 2. A dashboard-owned identity store behind `IDashboardIdentityStore`, on SQLite first

- Two new non-packable projects. `VSaga.Dashboard.Identity` holds the store-neutral records, the
  `IDashboardIdentityStore` and key-ring interfaces, the EF Core implementation and the services
  (credential verification, access evaluation, administration, first administrator, start-up).
  `VSaga.Dashboard.Identity.Sqlite` holds only the generated migrations, with analyzers off, as
  `VSaga.Persistence.EFCore.Postgres` does.
- The interface is aggregate-shaped: users, teams and roles go in and out as whole records, grants
  included; no `IQueryable` and no joins leave it. Uniqueness goes through normalised name columns a
  non-relational store could use as keys. Invariant-checking writes run in one exclusive scope (on
  SQLite, `BEGIN IMMEDIATE`), so check-then-write is serialised.
- `Dashboard:Identity:Provider` selects the store. `Sqlite` is the only value; anything else fails at
  start, as the `Persistence:Provider` switch does.
- Identity is independent of saga persistence. Whatever `Persistence:Provider` a deployment uses, the
  dashboard's users live in their own file, and engine hosts never read it.
- `Dashboard:Identity:Sqlite:Path` locates the file. The images set
  `/var/lib/vsaga-dashboard/identity.db` on a named volume owned by the non-root API user; directory
  mode 0700, file mode 0600 on Linux. Inside a container there is no fallback: an unset path is an
  error reported by the `identity` health check, because a database in the container layer would lose
  every user and the key ring on recreate. Outside a container the default is under the user's local
  application data folder.
- An unusable store never blocks start-up. `/health` stays `200` with `identity` reported `Degraded`
  (so compose's `service_healthy` gates still open), the `/api/auth/*` endpoints answer `503
  identity_unavailable`, cookie sessions get `401`, and an API key mapped to a built-in role keeps
  working, because built-in roles resolve from code.
- Dashboard security settings are read once into plain singletons and validated at composition, like
  the provider switches; there is no options binding for vSaga-owned settings
  ([`../configuration.md`](../configuration.md)).

### 3. Built-in and custom roles over four permissions, granted per saga type

| Permission | Allows | Scope |
| --- | --- | --- |
| `sagas.view` | Lists, the detail summary, the timeline and the map without payloads or error text, sub-sagas, correlations, saga types, the retry plan, live updates | per saga type |
| `sagas.data` | The state blob, message payloads and error messages; implies `sagas.view` | per saga type |
| `sagas.retry` | Manual retry; implies `sagas.view` | per saga type |
| `access.manage` | Everything under `/api/admin/*` | all saga types only |

| Built-in role | Permissions |
| --- | --- |
| Administrator | all four |
| Operator | `sagas.view`, `sagas.data`, `sagas.retry` |
| Viewer | `sagas.view`, `sagas.data` |

- Built-in roles are defined in code with fixed ids and cannot be edited or deleted. Custom roles are
  any non-empty subset of the four permissions; a view-only role is the way to hide data.
- A **grant** pairs a role with a scope: all saga types, or a list of exact saga-type names (trimmed,
  non-blank, no control characters, at most 200 characters, the `SagaType` column length). A user holds
  grants directly and through **teams**; effective access is the union. `access.manage` counts only
  from an all-saga-types grant. Team membership is written only through the team payload.
- **The last administrator cannot be removed.** Every administrative change is applied to a snapshot
  inside the exclusive scope and refused with `409 last_administrator` if no enabled user would still
  hold unscoped `access.manage`.
- Per-instance routes answer `403` before any read when the route's saga type is out of scope; the
  saga-types, correlations and children responses are filtered to visible types.
- The administration API's records are the wire contract (`isEnabled`, `isBuiltIn`,
  `lastSignInAtUtc`, arrays never null). Request records reject unknown JSON members, so a client that
  drifts gets a `400` instead of a silent no-op, and golden JSON fixtures are asserted from both the
  .NET and the Angular tests.
- A retry records its actor in the `ManualRetryRequested` entry's `SourceService`:
  `dashboard:<username>`, or `dashboard:api-key` for the key. The username `api-key` is reserved,
  ignoring case, so no user can be recorded as the key. `SagaMapBuilder` already ignores
  `SourceService` for this entry type.

### 4. The API key stays, as a machine credential that can never manage access

- `Dashboard:ApiKey` authenticates scripts and probes, and is removed from the SPA:
  `DASHBOARD_API_KEY` and the API-key interceptor are deleted, and the hub authenticates with the
  session cookie.
- The key's access is the role named by `Dashboard:ApiKeyRole` (default `Viewer`), unscoped. A
  built-in name resolves from code, any other name from the store; an unknown role fails
  authentication.
- The key never holds `access.manage`, whatever its role: the permission is removed from its effective
  access, `/api/admin/*` answers it `403` as the password endpoint does, and start-up logs a warning when
  the configured role contains it. Start-up also warns when the key is shorter than 24 characters,
  since key guesses are not rate limited.
- The key is accepted from `X-Api-Key` and `Authorization: Bearer` on any endpoint, and from the
  `access_token` query string on hub endpoints only. A request is exempt from antiforgery only when the
  key arrived in a header, which a cross-site form cannot set.
- The default role changes behaviour for existing scripts: a retry with the key needs
  `Dashboard:ApiKeyRole=Operator`.

### 5. The first administrator: seeded, or claimed with a one-time code; seeding fails closed

- **Seed.** When the store has no users and both `Dashboard:Admin:Username` and
  `Dashboard:Admin:Password` are set and pass the password policy, start-up creates that user with an
  unscoped Administrator grant. Seeding never touches a store that already has users. The demo compose
  file seeds a committed development password and binds the dashboard ports to `127.0.0.1`, because a
  known login on every interface would hand the dashboard to the local network.
- **Seeding fails closed.** When either `Dashboard:Admin` key is set, the setup screen is never
  available. A seed that cannot be applied (one key missing, a password the policy rejects) leaves setup
  required but unavailable, reports `identity` as `Degraded` with the reason, and gives the SPA a code
  to display. An operator who configured a seed never gets an open claim instead.
- **Setup.** With no users and no seed keys, start-up generates a one-time setup code and logs it once
  at Warning; `Dashboard:Setup:Code` presets it. `POST /api/auth/setup` requires the code (fixed-time
  comparison, inside the auth rate limiter) and re-checks that no users exist inside the exclusive scope,
  so two visitors cannot both succeed. There is no time window: the code, which only someone with the
  API's log or configuration has, is the boundary, and an operator is not forced to race a clock.
- **Break-glass.** `Dashboard:Admin:ResetOnStart=true`, with the seed username and password, resets
  that user's password at start, re-enables the account, clears its lockout and restores an unscoped
  Administrator grant; the reset rotates the security stamp like any administrator reset and is logged
  at Warning. It is the recovery for a lost or locked-out administrator that does not cost every other
  user, team and role, which deleting the volume would.

### 6. The Data Protection key ring lives in the identity store

Cookies and antiforgery tokens are protected by ASP.NET Core Data Protection. Its key ring is stored in
a `DataProtectionKeys` table through an `IXmlRepository` over the store, with the application name
`VSaga.Dashboard`, so sessions survive an API restart and a container recreate without a second
volume. The repository throws while the store is not ready, and the auth endpoints and cookie events
check readiness before any Data Protection call, so an unavailable store yields `503` or `401` and never
a fresh in-memory key whose cookies die at the next restart. Nothing that needs Data Protection runs
inside an exclusive write scope, because the key ring uses its own connection. The ring is stored
unencrypted. vSaga ships no configuration key or code to protect it with a certificate (ASP.NET Core
logs "No XML encryptor configured" at start); a deployment that wants that adds the framework's
`ProtectKeysWithCertificate` to the API's composition itself, as `docs/dashboard.md` says, and that
path is not exercised in this repository.

### 7. Scoped lists are merged in the API, within bounds

`ISagaSummaryReader.ListAsync` filters by at most one saga type, and this decision does not change the
persistence contracts. A caller scoped to several types is served by a `ScopedSagaLister` in the API:

- Unscoped callers get one `ListAsync` call, as today. A `sagaType` filter in scope is one call; out of
  scope, an empty page. A blank `sagaType` is treated as not supplied, because every provider reads a
  blank filter as "all types".
- Otherwise the lister runs one stream per visible type, sequentially (the reader is scoped, and an EF
  context cannot run queries in parallel), and merges them k-way in the order each sort arm uses, with
  ties across types broken by saga type, ordinal. Rows of one type keep the provider's own order.
  Repeated `(sagaType, correlationId)` pairs are dropped, and every row is checked against the scope
  again before it is returned.
- Bounds: for the shape Redis serves from a rank (the `UpdatedAt` sort with no status or kind filter and
  no search), at most 50 types and `page × pageSize` at most 10,000; for every other shape (a `Status`
  sort, a status or kind filter, or a search), on every provider, at most 10 types and one 500-row chunk,
  because on Redis they read every member of every visible type. Scoped names beyond a shape's type
  bound are narrowed to the types that have run (read through a short cache). Beyond a bound, `400` with
  an error that names the `sagaType` filter and the last reachable page.
- `TotalCount` is the sum of separate per-type counts, as loose under concurrent writes as offset paging
  already is.

A multi-type filter on `SagaListFilter`, which would let each provider serve this in one query, is a
recorded follow-up.

### 8. Without `sagas.data`, payloads and error messages are redacted

The detail response omits the state blob (and skips reading it); the timeline nulls `PayloadJson` and
`ErrorMessage` on every entry, which covers state snapshots (ADR 0007) and the message bodies the engine
records on `MessageReceived` (ADR 0008); the map nulls each event's `ErrorMessage`. Exception text
thrown by saga code often carries business values, so it is data, not metadata. SignalR's
`TimelineEntryAdded` pushes drop both fields for every caller, and the SPA refetches the timeline.
Entry types stay, so a view-only caller still sees that and where a step failed.

### 9. The hub checks access per call, groups by saga type, guards its origin, and drops stale connections

- Browsers authenticate the hub with the session cookie on negotiate and on the WebSocket; no token
  travels in a URL.
- `SubscribeToList` and `SubscribeToSaga` resolve access afresh and return `false` on denial instead of
  throwing. An unscoped caller joins `saga:list`; a scoped caller joins one `saga-list:{sagaType}` group
  per type in scope, including types with no instances yet. Pushes go to both kinds of group and to the
  instance group.
- **Origin guard.** WebSockets bypass CORS and `SameSite` treats every `localhost` port as one site, so
  on endpoints marked as hubs the API accepts a handshake only with no `Origin`, an `Origin` equal to
  the request's own scheme and host, or the configured `Dashboard:WebOrigin` (empty by default, owned by
  the edge configuration, `Hosting/DashboardEdge.cs`). `Origin: null` is a mismatch. A rejection is
  logged at Warning with the received and expected values.
- **Close on change.** A registry records each connection's user. The API closes that user's
  connections on every security-stamp rotation (self-service password change, administrator reset,
  disable, enable), on delete, on sign-out and on any change to their grants or teams, and every
  connection on a role change or deletion (the API key may act as a custom role). A connection is closed
  through `IConnectionLifetimeNotificationFeature.RequestClose()`, the path SignalR itself takes for an
  expired ticket, so the client receives a close message that allows it to reconnect: it reconnects,
  authenticates again and resubscribes, so group membership always reflects current access, and a
  revoked user's negotiate gets `401`. `HubCallerContext.Abort()` sends a close message that forbids
  reconnecting, which would stop the SPA's client until the page is reloaded, so it is only the fallback
  for a connection that offers no such feature or whose close failed. A socket whose cookie ticket has
  expired is closed, and the expiry the hub sees is capped at the sign-in time plus the absolute
  lifetime.

### 10. Every `/api` response is `no-store` and `nosniff`

One middleware sets `Cache-Control: no-store` and `X-Content-Type-Options: nosniff` on every `/api`
response. Responses now carry per-user data, and port 5080 can be reached without nginx in front.

### 11. Sign-in and access changes are audited

The auth endpoints and the administration service emit structured log events with stable event ids:
sign-in success and failure, lockout, setup, seeding, `ResetOnStart`, password changes and resets, and
every user, team, role and grant change, each with actor, target, action, outcome and client address.
Passwords and hashes are never logged; a submitted username is logged only when it passes the username
rule. The log category is documented so operators can ship it. Retry attribution stays in the saga log
(decision 3).

---

## Options considered

### A. Two shared keys, one to read and one to retry

Keeps the current mechanism. Rejected: for the SPA to offer Retry, the retry key must reach the browser,
which is the problem being solved. It names no one, cannot hide data from some readers, cannot be
scoped to a saga type, and revoking it still means redistributing a secret.

### B. External OpenID Connect only

The right end state for an organisation with an identity provider, and sessions would still be cookies.
Rejected for now: the demo and any team without an identity provider would have to run one (another
container, client registration, redirect URIs) before they could open the dashboard, and the
authorization model (permissions, saga-type grants, the last-administrator rule) would still be needed,
fed from claims instead of a local store. Single sign-on is a follow-up, and a requirement for it is
listed below as a reason to revisit.

### C. ASP.NET Core Identity with its EF Core stores

Brings `UserManager`, `SignInManager`, lockout and a tested schema. Rejected: its seven-table schema and
its store interfaces are relational-shaped and much wider than this needs; its roles are flat names
with no notion of a saga-type scope, so grants would become claims with a private convention on top;
and a second store implementation would have to satisfy that interface family rather than one
aggregate-shaped interface. The decision still uses the framework's `PasswordHasher<T>`, cookie
authentication, antiforgery and Data Protection, which are the parts with real security value.

### D. Cookie sessions over `IDashboardIdentityStore`, SQLite first (chosen)

Same-origin serving makes an HttpOnly, `SameSite=Strict` cookie plus antiforgery the simplest sound
browser credential; the framework supplies the hashing, cookies, antiforgery and key ring; the store is
small enough to implement again on another database; SQLite needs no new service in compose, only a
volume.

### E. Identity inside each saga persistence provider

Users and grants in the saga store the deployment already runs. Rejected: four implementations and
conformance cases for something unrelated to sagas; the in-memory provider would lose every user at
restart, and Redis's documented loss window is wrong for credentials; and engine hosts that share the
saga store would share the password hashes and key ring. `IDashboardIdentityStore` leaves room to add a
store on one of those databases later as a separate implementation.

### F. Bearer tokens held in browser storage

A token in `localStorage` is readable by any script that runs on the page; revocation needs short
lifetimes plus refresh tokens or a denylist; and a WebSocket cannot carry a header, so the token would
travel in the hub's query string and appear in proxy logs. Rejected: with one origin, a cookie the page
cannot read is strictly better.

---

## Consequences

### Positive

- Every action has a named actor. A retry records who asked, and a person's access can be removed
  without touching anyone else's.
- Retry needs `sagas.retry`, and saga data needs `sagas.data`, each per saga type. With ADR 0008 a
  scoped retry stays inside its saga type.
- No secret ships in the SPA bundle.
- Revocation is immediate: the stamp is checked on every request, and live connections are closed,
  with a close message that lets the client reconnect under its new access, when access changes.
- Sessions survive API restarts and container recreation, because the key ring is persisted.
- No persistence contract, schema or engine behaviour changes for this decision; engine hosts are
  unaffected apart from ADR 0008's header.

### Negative

- **One API instance per SQLite file.** The file cannot be shared between replicas, and SQLite's
  locking is unreliable on network file systems.
- **A volume to back up, and that backup is a credential backup.** It holds password hashes, security
  stamps and the key ring.
- **Password handling becomes this project's responsibility:** policy, lockout, resets, recovery. There
  is no multi-factor authentication and no single sign-on.
- **The key ring is stored unencrypted next to the hashes** unless the deployer adds a certificate to the
  API's composition, which vSaga does not ship. Anyone who can read the file can forge a session for any
  user.
- **Scoped lists cost more than unscoped ones.** At the bounds a page can take on the order of 70
  sequential `ListAsync` calls and tens of thousands of summaries on EF Core or MongoDB. On Redis, list
  shapes that are not served from a rank (a `Status` sort, a status or kind filter, or a search) read
  every member of every visible type on each call, and Redis is single-threaded while it does.
- **A store read per request**, and per hub subscription. A local SQLite read is sub-millisecond; a
  short cache keyed by security stamp is the remedy if measurement ever says otherwise.
- The API key defaults to Viewer, so scripts that retry with it receive `403` until
  `Dashboard:ApiKeyRole` is set.
- The demo ships a known administrator login; binding to `127.0.0.1` is what keeps it local.
- Stacks side by side on one host share `XSRF-TOKEN`; the first unsafe request after switching stacks
  costs one antiforgery `400` and a retry.

### Neutral

- The dashboard API gains its own persistent state for the first time, independent of
  `Persistence:Provider`.
- `/health` gains an `identity` check that reports `Degraded`, never `Unhealthy`.
- CORS becomes opt-in: with the SPA served same-origin, `Dashboard:WebOrigin` defaults to empty. CORS
  and forwarded headers are owned by the edge configuration, not by this decision.
- Two `DbContext`s are registered in the API, so every `dotnet ef` command needs `--context`.

---

## Accepted residual risks

1. **Lockout can be used to lock a known username out.** Five wrong passwords every fifteen minutes keep
   an account locked, and the demo's administrator name is public. A known-device cookie that lets a
   previously used browser bypass the lock is deferred. What ends a lock is an administrator unlocking
   the account or `Dashboard:Admin:ResetOnStart` (either at once), or waiting out
   `Dashboard:Lockout:Minutes`; the user guide's troubleshooting section lists them. A restart with
   `Dashboard:Lockout:MaxFailedAttempts=0` only stops new locks: the credential check refuses an account
   whose lock is still in force whatever the setting says.
2. **Usernames are visible on retry entries** to anyone with `sagas.view` on that saga type.
3. **What `sagas.view` exposes.** Without `sagas.data` a caller still sees correlation ids, statuses and
   timestamps, state names and message type names, service names, the parent saga type and correlation
   id, which steps failed, and the actor on a retry entry. Only payloads, error text and the state blob
   are withheld.
4. **Consumers that are not sagas still receive a retry's replay.** The target header in ADR 0008 is
   honoured by the saga engine; a plain message handler subscribed to the same message type processes
   the replay again. The user guide and the retry confirmation say so.
5. **An engine host older than the targeted-retry change ignores the target header**, and a retry then
   reaches every saga type on that host that consumes the message, as it does today. A scoped
   `sagas.retry` grant is a boundary only when every engine host on the bus runs at least the release
   that ships ADR 0008's engine change; `docs/dashboard.md` ("Manual retry") states that minimum.
6. **A temporary password set by an administrator does not expire** if the user never signs in. An
   expiry is a recorded follow-up.

---

## What would invalidate this decision later

1. **Several API replicas.** A SQLite file cannot serve them. That needs another
   `IDashboardIdentityStore` on a shared database, a shared key ring, and a SignalR backplane for the
   hub groups; the store interface was shaped so the first of these is an addition, not a rewrite.
2. **A single-sign-on requirement.** Sign-in would move to an external identity provider, local
   passwords would become a fallback or disappear, and sub-decisions 1, 2 and 5 would be revisited.
   The permission and grant model (3) would likely survive, mapped from the provider's claims.
3. **Per-instance rather than per-type scoping.** Grants are keyed by saga type because routes,
   provider indexes and hub groups all are. Access to individual saga instances would need an access
   list per instance and a list query that filters by it, which is a persistence contract change and a
   different design.
