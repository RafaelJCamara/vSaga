# Design: dashboard usability and access

**Status: accepted, not yet implemented, 2026-10-02.** Nothing in this document has been built yet;
every "the API answers", "the SPA shows" or "the engine records" below describes intended behaviour.
§12 tracks progress commit by commit. Three decisions are recorded separately:
[`../adr/0006-dashboard-authentication-and-identity-store.md`](../adr/0006-dashboard-authentication-and-identity-store.md)
(sign-in, access control and the identity store),
[`../adr/0007-state-snapshots-in-the-event-log.md`](../adr/0007-state-snapshots-in-the-event-log.md)
(per-step state as `StatePersisted` entries) and
[`../adr/0008-dashboard-retry-reruns-the-failed-step.md`](../adr/0008-dashboard-retry-reruns-the-failed-step.md)
(a targeted retry that re-runs the failed step). An ADR links here for the reasoning rather than
repeating it.

This document stands alone. It replaces a temporary working plan (six workstream blueprints and three
reviews: security, consistency, feasibility) that is deleted when the work is done. Where a blueprint
and a review disagreed, only the review's answer is written here; where the user decided something
after the blueprints were written, that decision is written here.

Every claim about the current code carries a repo-relative path. Line numbers are accurate at commit
`22f04bf`; re-grep rather than trusting them once the tree moves.

---

## 1. What it is

The vSaga dashboard (`dashboard-web`, an Angular 21 SPA, plus `VSaga.Dashboard.Api`) has six gaps:

1. Nothing explains the UI: no user guide, no in-app guidance.
2. The time on each timeline entry is unlabelled: `entry.occurredAtUtc | date:'HH:mm:ss.SSS'`
   (`dashboard-web/src/app/pages/saga-detail/saga-detail.html`), with no date, zone or meaning.
3. Timeline entries are not linked to the Map tab, although the map replays the same entries.
4. The Data tab shows only the current state blob. The state after each step is overwritten at the
   next persist and lost.
5. The UI is outside docker compose, so the demo needs two commands and Node on the host.
6. The only protection is one API key compiled into the SPA bundle
   (`dashboard-web/src/app/api-config.ts`), so anyone who can load the page can retry sagas, and a
   retry republishes real messages.

### 1.1 Recorded positions this reverses

| Position | Where it is recorded | What replaces it |
| --- | --- | --- |
| One shared API key is the dashboard's authentication | `docs/dashboard.md` "Authentication" ("A single shared API key ... chosen over JWT/OIDC or basic auth as the right fit for an internal ops dashboard with no existing identity infrastructure", and its "Known limitation": "not per-user authentication or authorization"); `docs/history/project-origins-and-hardening-pass.md`, "Production-hardening pass", first bullet | Cookie sessions for people, role- and saga-type-scoped access, a dashboard-owned identity store (§8, ADR 0006). The key stays as a machine credential with a configured role. |
| The UI is not part of compose | `docs/dashboard.md` "The SPA" ("It is not part of `docker-compose.yml`: run it with `npx ng serve`"); `README.md` "Run the demo" ("a dev server, deliberately not part of `docker-compose.yml`") | A `dashboard-web` nginx image in every compose stack, serving the SPA same-origin with the API (§4). |
| Dashboard writes beyond the existing retry are out of scope | `docs/design/production-readiness.md` §1, "Explicitly out of scope" ("dashboard writes beyond the existing retry"). Neither `docs/dashboard.md` nor the project-origins history states it outright; both describe the dashboard as a read-mostly tool whose one write is the manual retry. | The dashboard gains a second write path, access administration (users, teams, roles, grants), and the retry itself changes shape (§7). |

### 1.2 Decisions already taken

First round (the user's three decisions, the rest by the lead with the review that settled them):

| Topic | Decision | Source |
| --- | --- | --- |
| Identity storage | SQLite only for now, behind `IDashboardIdentityStore` so another store can be added | user |
| Access model | Built-in Administrator / Operator / Viewer, custom roles from a fixed permission list, grants scoped by saga type | user |
| First administrator | Seeded from `Dashboard:Admin:*` when set; otherwise a first-run setup screen | user |
| UI packaging | Separate `dashboard-web` nginx container that proxies `/api/` and `/hubs/` to `dashboard-api`; the SPA uses relative URLs | lead |
| Ports | UI port = API port − 880 (4200 base; 4300–4800 overlays). Dashboard UI and API ports bind to `127.0.0.1`, because the demo now ships a known administrator login | lead (security review) |
| Per-step data | New `SagaEntryType.StatePersisted` entry appended after each committed persist, state in `PayloadJson`; no persistence contract or schema change; on by default with an opt-out, a 256 KiB per-snapshot cap and a 1 MiB per-saga budget | lead (feasibility review) |
| Sessions | ASP.NET Core cookie authentication plus antiforgery; Data Protection keys in the identity store | lead |
| API key | Kept as a machine credential mapped to `Dashboard:ApiKeyRole` (default Viewer); can never hold `access.manage`; removed from the SPA | lead (security review) |
| Scoped lists | The API merges per-type `ListAsync` results; no persistence contract change; a multi-type store filter is a recorded follow-up | lead (feasibility review) |
| Guidance | A hand-rolled tour (no npm dependency) plus `docs/dashboard-guide.md` | lead |

Second round (the user, 2026-10-02):

| Topic | Decision |
| --- | --- |
| Retry | A retry re-drives only the retried saga, from the step that failed onwards. The redrive carries a target-saga-type header (`MessageEnvelope`), and `SagaOrchestrator.HandleCoreAsync` ignores a targeted message addressed to another saga type. The failed step is obvious in the UI: marked in the timeline and on the map, and named in the retry confirmation. |
| Retry without an exception | Re-run the failing step. The engine records `PayloadJson` on every `MessageReceived` entry. For a business failure the failing step is the last handled inbound message before the terminal entry; for a timeout it is the step that entered the timed-out state. Retry resets `CurrentState`/`Status` to what they were before that step (`ISagaAdminStore.ResetStateAsync`, contract unchanged; business fields are not rolled back) and replays only that message, targeted at that saga type. A saga recorded before payloads existed is refused (422) with an explanation. The reset-to-start path is removed. |
| Commits | A feature branch off `main`, one local commit per logical change, nothing pushed. |
| Setup screen | Requires a one-time code the API logs at start when no users exist (`Dashboard:Setup:Code` may preset it). No time window. |
| User guide | Text and tables, no screenshots. A written rule in `CONTRIBUTING.md`, a header note in `docs/dashboard-guide.md` and one in the tour step definitions: a change to the dashboard UI updates the guide and the tour in the same change; anchor-contract specs fail when a tour anchor disappears. |
| Guide mode | When switched on it follows the user: each page or area they open explains itself once (list, detail, map, timeline, data, retry, administration), with Replay available. |
| Lost administrator | `Dashboard:Admin:ResetOnStart=true` with the seed username and password resets that user's password, re-enables the account and restores an unscoped Administrator grant at start; logged as a warning. |

### 1.3 What changes for the user

- `docker compose up -d --build` starts everything; open `http://localhost:4200` and sign in.
- The timeline is grouped into steps; every entry shows "Recorded at 14:03:07.140 +1.224 s" (local
  time, UTC on hover). Selecting an entry opens the Map positioned on that entry, with an "as of entry"
  banner.
- Each step has a Data control: what changed and the full state after that step. A "Saga data" bar
  under the summary card offers At start, At end and Compare. The Data tab is gone.
- A login page, a user menu, and an Administration area (users, teams, roles, grants per saga type).
  Retry and data are shown only to those permitted; a retry records who asked.
- The step a failed saga failed in is marked; a retry re-runs that step for that saga only.
- A Guide toggle in the top bar explains each page and area the first time it is opened.

---

## 2. What already exists

**The SPA** (`dashboard-web/`, Angular 21.2.24, standalone components, signals). Two eager routes,
`/sagas` and `/sagas/:sagaType/:id` (`src/app/app.routes.ts`). The list page filters, searches, sorts
and pages on the server and keeps its state in the URL (`pages/saga-list/saga-list.ts`). The detail page
has three tabs, Map (default), Timeline and Data (`pages/saga-detail/saga-detail.html`); the timeline is a
flat list of entries; Data pretty-prints `dataJson` through a `prettyDataJson` getter; a retry row with a
two-step confirmation shows for `Failed` and `TimedOut` sagas. `components/saga-map/` draws the service
map and a replay scrubber over the map endpoint's events. `services/saga-api.service.ts` calls the API;
`services/saga-hub.service.ts` holds one SignalR connection, created lazily, that retries a failed start
forever. `api-config.ts` hard-codes `API_BASE_URL = 'http://localhost:5080'` and
`DASHBOARD_API_KEY = 'dev-local-only-change-me'`; `interceptors/api-key.interceptor.ts` adds the key to
every request and the hub sends it through `accessTokenFactory`. There is no browser storage, no lazy
route and no `src/app/util/` folder. The production build has about 96 kB of headroom under the 500 kB
initial-bundle warning; component styles warn at 4 kB (`angular.json` budgets).

**The API** (`dotnet/src/VSaga.Dashboard.Api/`). Minimal API endpoints in `Endpoints/SagaEndpoints.cs`:
list, detail (`SagaDetail(summary, dataJson)`), timeline, map, children, retry, saga types, and
correlations; `/health` and the Development-only OpenAPI document are anonymous. `Program.cs` adds a
`JsonStringEnumConverter` to both the HTTP and the SignalR JSON options, so enums travel as names, and
property names are camelCase. CORS is one credentialed origin from `Dashboard:WebOrigin`, falling back to
`http://localhost:4200` (`Program.cs`, the `CorsPolicy` block). Authentication is
`Auth/ApiKeyAuthenticationHandler.cs`: `X-Api-Key`, then `Authorization: Bearer`, then `?access_token=`,
failing closed when no key is configured; every 401 is one `application/problem+json` body. The
persistence and transport providers are chosen by `Persistence:Provider` and `Transport:Provider`
switches read once at composition.

**The retry endpoint** (`SagaEndpoints.RetrySagaAsync`). For `Failed` or `TimedOut` only (409
otherwise). Two shapes: the last `StepFailed` entry carrying a payload is replayed against the unchanged
state; otherwise the saga is reset to the `SagaStarted` entry's `ToState` with `ResetStateAsync` and the
initiating message is replayed. It appends `ManualRetryRequested`, then republishes with
`IMessageTransport.PublishRawAsync` under `MessageEnvelope.New(correlationId)`: same correlation id,
fresh message id. Its own comment says it plainly: "every saga type subscribed to this message type sees
it, not only `sagaType`". 422 when neither shape applies, 502 when the publish fails. The in-process
`SagaOrchestrator.RetryAsync` (`dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs`) handles the
`StepFailed` shape only, runs the step directly, and reuses the failed message's id.

**The event log.** `SagaLogEntry` (`dotnet/src/VSaga.Abstractions/Persistence/SagaLogEntry.cs`) carries
type, from/to state, message type and id, `PayloadJson`, `ErrorMessage`, trace ids, time, source and
destination service and causation id. `SagaEntryType` has 21 members, persisted as integers and
append-only by rule. `PayloadJson` is recorded today on `SagaStarted` (the initiating body) and
`StepFailed` (the failed message), not on `MessageReceived`. The engine reads the log as an input in two
places: `IsDuplicateAsync` (dedupe on `SagaStarted`/`MessageReceived` ids) and `GetVisitedStatesAsync`,
which loads the whole timeline, payloads included, before every step and timeout and collects `ToState`
values for compensation.

**Live updates.** `Hubs/SagaHub.cs` has a list group `saga:list` and per-instance groups
`saga:{sagaType}:{correlationId}`; anyone authenticated may join either. Two paths push:
`SignalRSagaChangeNotifier` in-process (`SagaUpdated` and `TimelineEntryAdded`, the entry with its
payload) and `SagaChangePollingService`, which polls the store every second and pushes `SagaUpdated`
only. The SPA's detail page refetches timeline and map on `SagaUpdated` and appends pushed entries,
which carry sequence number 0 because `SagaOrchestrator.LogAsync` notifies with the entry it built, not
the stored one.

**Compose.** `docker-compose.yml` runs postgres, rabbitmq, dashboard-api (port 5080, the committed API key,
`Dashboard__WebOrigin`) and order-processing; six overlays move the API to 5180–5680. The API image runs
as root with no volume (`dotnet/src/VSaga.Dashboard.Api/Dockerfile`).

---

## 3. Constraints found by tracing the code

Each of these shaped a decision below. The section that answers it is named in the last column.

| Constraint | Evidence | Answered in |
| --- | --- | --- |
| Outbox rows are staged before the persist and committed by it; on EF Core any `SaveChangesAsync` in between commits them early. A snapshot append must therefore go after the persist and before the drain, never between staging and persist. | `SagaOrchestrator.PersistAndFinalizeStepSuccessAsync` (stage, persist, drain), `CommitAndDispatchTimeoutAsync` | §6.3 |
| On EF Core a failed append leaves its entity `Added` in the scoped `DbContext`; every later save in the unit of work retries it. | `VSaga.Persistence.EFCore/EfCoreSagaEventLogStore.cs` (`Add` then `SaveChangesAsync`) | §6.4 |
| Meziantou MA0051 fails the build above 60 lines. `SagaOrchestrator.HandleStepFailureAsync` and `SagaChangePollingService.PollOnceAsync` sit at 59. A change there replaces a call; it never adds a line. Minimal-API lambdas that grow become named handlers. | Meziantou's default, under `TreatWarningsAsErrors`; the two methods | §6.3, §7.3 |
| `EntryType` is persisted as an integer; new members are appended, never inserted. | `SagaEntryType.cs` comment | §6.1 |
| JSON on HTTP and SignalR is camelCase with enums as names. A new enum reaches the SPA as its C# member names; an API older than the engine renders an unknown entry type as a number. | `Program.cs` (`ConfigureHttpJsonOptions`, `AddJsonProtocol`) | §6.7, §7.3 |
| vSaga-owned settings are read once at composition and validated by throwing, as the provider switches are; nothing uses options binding. In tests, settings read while composing must be set with `UseSetting`. | `docs/configuration.md` opening paragraph; `Program.cs` switches | §4.4, §8.9 |
| Browsers scope cookies by host, not port: the base stack on `localhost:4200` and an overlay on `localhost:4300` share one cookie jar. SameSite treats every `localhost` port as one site, and WebSockets bypass CORS. | Browser behaviour; `README.md` promises overlays run beside the base stack | §8.3, §8.6 |
| ASP.NET antiforgery tokens are bound to the signed-in identity, so a token issued before sign-in fails after it. Angular's XSRF interceptor fires only for same-origin, non-GET requests and defaults to `XSRF-TOKEN`/`X-XSRF-TOKEN`. SignalR's negotiate is a POST that carries no XSRF header. | `@angular/common` `xsrfInterceptorFn`; `@microsoft/signalr` `HttpConnection` | §8.3, §8.6 |
| A CSP with `script-src 'self'` blocks inline handlers, and today's production `index.html` carries one (`<link ... media="print" onload="this.media='all'">`, from critical-CSS inlining). Angular injects component styles as `<style>` elements. `Referrer-Policy: no-referrer` makes browsers send `Origin: null` on same-origin unsafe requests. | `dist/dashboard-web/browser/index.html`; Fetch standard | §4.2 |
| nginx resolves a literal `proxy_pass` host once at start. `docker compose up -d --build` recreates `dashboard-api` and `order-processing` together, both listen on 8080, and their addresses can swap. A `proxy_pass` with a URI part decodes `%2F`, which saga type names in routes rely on. | nginx semantics; `saga-api.service.ts` encodes the saga type | §4.2 |
| `angular.json` `optimization` written as an object coerces omitted keys to `false` (unminified JS and CSS, both budgets broken). | `@angular/build` `normalizeOptimization` | §4.3 |
| Checkouts on this machine are CRLF (`core.autocrlf=true`, no `.gitattributes`); a shell script copied into a Linux image would not start. | git configuration | §4.1 |
| `GetVisitedStatesAsync` reads every entry, payloads included, before every step. On Redis the timeline is one list read with `LRANGE 0 -1` that blocks the single-threaded server, each entry is JSON inside JSON (every quote in a state blob costs about six bytes, 1.6 to 2 times the raw size), it sits in RAM and counts toward `WriteMemoryThreshold`, above which every persist is refused. | `SagaOrchestrator.GetVisitedStatesAsync`; `VSaga.Persistence.Redis/RedisSagaEventLogStore.cs`, `RedisPersistScripts.cs`; `docker-compose.redis.yml` (`maxmemory 256mb`) | §6.5 |
| On Redis, a list filtered by saga type is rank-served only for the plain `UpdatedAt` sort; with a `Status` sort or a status or kind filter the reader intersects indexes and returns every matching member before paging. A scoped merge issues that once per visible type. | `VSaga.Persistence.Redis/RedisListQuery.cs`, `RedisSagaSummaryReader.cs` | §8.7 |
| `GetSagaTypesAsync` is an unindexed `DISTINCT` on EF Core and a `$group` over the collection on MongoDB. | `EfCoreSagaSummaryReader.cs`, `MongoSagaSummaryReader.cs` | §8.7 |
| A blank `sagaType` means "no filter" in every provider (`IsNullOrWhiteSpace`). | the four list-query implementations | §8.7 |
| Several entries carry no message id: `TimeoutScheduled` and `SagaCompleted` (`HandleStepSuccessAsync`) and compensation entries. A `.CallHttp` request carries its own fresh call id, and its reply is logged as a mid-step `MessageReceived` with another fresh id whose `causationId` names the request. `SagaStarted` and the first `MessageReceived` share one id. The in-process retry reuses the failed message's id. | `SagaOrchestrator.cs`, `CompensationRunner.cs`, `VSaga.Http/HttpCallDefinition.cs` | §5.2, §6.6, §7.3 |
| The timeout path logs `TimeoutFired` with `FromState` = the timed-out state, then a `StepSucceeded` with no message id, and never a `SagaCompleted`, even when the timeout finalises the saga. | `SagaOrchestrator.HandleTimeoutAsync` | §7.3 |
| `DeliveryExhausted` is logged in three places. Only the dead-letter record (`RecordDeliveryExhaustedAsync`) carries the message id. A failed deferred publish (`DrainDeferredPublishesAsync`) and a discarded one (`DiscardDeferredPublishesAsync`, called after `StepFailed` on the failure path) carry no message id. | `SagaOrchestrator.cs` | §7.3 |
| Outbound envelopes are built fresh by `MessageEnvelope.From`, which copies only headers its caller passes; no publish path passes inbound headers. Redelivery after an infrastructure failure copies the inbound headers. | `MessageEnvelope.cs`, `SagaContext.cs`, `SagaOrchestrator.HandleInfrastructureFailureAsync` | §7.4 |
| Brighter and the HTTP transport forward only `x-vsaga-`-prefixed headers (plus the two W3C trace headers). | `BrighterTransport.cs`, `VSaga.Transport.Http/HttpMessageTransport.cs` | §7.4 |
| A choreographed step runs whatever the instance's status, and any saga type with an instance for the correlation id, or able to initiate from the message, runs a step for a new message id. | `ChoreographedSagaDefinition.cs`; `SagaOrchestrator.ResolveInstanceAsync` | §7.1 |
| The sample reproduces cross-type retry fan-out: `InvoiceFollowUpSaga` and `PostShipmentChoreography` both handle `InvoiceIssued` under one correlation id, so today's retry of a timed-out `InvoiceFollowUpSaga` starts a second `InvoiceDeliverySaga` (a second customer email). | `dotnet/samples/VSaga.Samples.OrderProcessing/` | §7.1 |
| `ErrorMessage` is the exception text thrown by saga code and often carries business values; it reaches `/timeline`, `/map` (`SagaMapEvent.ErrorMessage`) and `TimelineEntryAdded`. | `SagaOrchestrator.cs` (`errorMessage: ex.Message`), `SagaMapBuilder.cs` | §6.7, §8.5 |
| SQLite through EF Core: no `DateTimeOffset` ordering, case-sensitive default collation (`NOCASE` is ASCII-only), no rowversion, `BEGIN IMMEDIATE` transactions, WAL side files. Two registered `DbContext`s make `dotnet ef --context` mandatory. | Microsoft.Data.Sqlite 10.0.11; `VSaga.Dashboard.Api.csproj` EF Design reference | §8.1 |
| Behind nginx on Docker Desktop every browser may arrive from the bridge gateway, so a per-address limiter is one shared bucket. | expectation, to confirm live | §8.4 |
| jsdom 28 has no layout, no `inert` semantics, no `scrollIntoView`, `matchMedia` or `ResizeObserver`. | `dashboard-web/node_modules/jsdom` | §9.2 |

---

## 4. Packaging and same-origin serving

### 4.1 The `dashboard-web` image

`dashboard-web/Dockerfile`, context `./dashboard-web`: a `node:22-bookworm-slim` build stage
(`npm ci`, then `npx ng build`, manifests copied before sources so `npm ci` stays cached) and an
`nginxinc/nginx-unprivileged:1.30-alpine-slim` runtime stage (uid 101, port 8080) that copies
`dist/dashboard-web/browser/` and the third-party licence file. The base image renders
`/etc/nginx/templates/*.template` into `conf.d` at start, substituting only variables matched by
`NGINX_ENVSUBST_FILTER`, and exports `NGINX_LOCAL_RESOLVERS` from `/etc/resolv.conf`. No custom shell
script is added (CRLF, §3). `STOPSIGNAL SIGTERM`, because the base image's SIGQUIT makes
`docker compose down` wait out open WebSockets. `dashboard-web/.dockerignore` lists `node_modules/`,
`dist/`, `.angular/`, `coverage/`, `out-tsc/`, `src/**/*.spec.ts` and `src/app/testing`.

### 4.2 `dashboard-web/nginx/default.conf.template`

| Rule | Why |
| --- | --- |
| `location ^~ /api/` and `^~ /hubs/` proxy to `$dashboard_api`, a variable set from `DASHBOARD_API_UPSTREAM` (default `dashboard-api:8080`), with `resolver ${NGINX_LOCAL_RESOLVERS} valid=10s` | Re-resolves per request, so the UI survives a recreated API and never proxies to the sample host; each compose project resolves its own `dashboard-api`; nginx starts without the API |
| No URI part on `proxy_pass` | The request line is forwarded untouched, so `Order%2FSaga` stays encoded |
| `proxy_set_header Host $http_host` | The API sees the browser's `host:port`; the hub origin guard compares with it |
| `X-Forwarded-Proto` is `$scheme` unless the image's outer-proxy switch (`DASHBOARD_OUTER_PROXY=true`, off by default) says a TLS terminator sits in front, in which case the client's header is passed | A client cannot assert `https` to a stack it reaches directly |
| WebSocket upgrade headers; `/hubs/` with buffering off and 1 h read/send timeouts | SSE fallback, and SignalR's long poll holds a request past nginx's 60 s default |
| Headers set at server level only; caching through `expires` | `add_header` and `proxy_set_header` reach only locations that declare none of their own |
| `index.html` revalidates; content-hashed `*.js`/`*.css` and `/media/` cache for a year; a missing hashed asset is 404, not `index.html`; everything else falls back to `index.html` | Stale-tab chunk requests fail as scripts, not as HTML |
| CSP `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self' ws://$http_host wss://$http_host; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`, plus `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: same-origin`, `server_tokens off` | Inline styles are Angular's; WebSocket sources are spelled out; `same-origin` keeps `Origin` non-null for the origin checks |
| Access log format uses `$uri`, not `$request` | A machine client's `?access_token=` never reaches the access log |
| `location = /healthz` returns 200 | The container healthcheck reports on nginx alone; the CI smoke test covers the proxy path |

Browser-facing API surface lives only under `/api/` and `/hubs/`; SPA routes never start with `api/`,
`hubs/` or `healthz`; header names never contain underscores (nginx drops them). nginx adds no cache
headers to proxied responses: the API sets `Cache-Control: no-store` and `X-Content-Type-Options:
nosniff` on every `/api` response through one middleware (§8.9).

### 4.3 Angular

`api-config.ts` becomes `API_BASE_URL = ''`, so `HUB_URL` is `/hubs/saga` (SignalR resolves a relative
URL against the page). A new `dashboard-web/proxy.conf.mjs` proxies `/api/` and `/hubs/` (with `ws`) to
`VSAGA_API_URL`, default `http://localhost:5080`, with `changeOrigin` unset so the API sees the dev
server's host. `angular.json` serve options gain `"proxyConfig": "proxy.conf.mjs"` and `"port": 4201`,
so `ng serve` runs beside the compose UI on 4200. The production `optimization` object turns
`inlineCritical` off with every other key written out (`scripts`, `styles.minify`,
`styles.removeSpecialComments`, `fonts`), which removes the inline `onload` handler. The Angular CI job
greps the built `index.html` for `<script>` or ` on[a-z]+=` and fails on a match.

### 4.4 API edge: `Hosting/DashboardEdge.cs`

`DashboardEdge` is the only code that reads `Dashboard:WebOrigin` and `Dashboard:TrustedProxies`.
`DashboardEdge.Read(IConfiguration)` returns a plain `DashboardEdgeSettings(WebOrigin,
TrustedProxies)` singleton:

- `Dashboard:WebOrigin` defaults to empty, meaning no CORS. Otherwise it must be an absolute http or
  https URI with no path (stored without a trailing slash), or composition throws. There is no `*`.
- `Dashboard:TrustedProxies` is a comma-separated list of addresses or CIDR networks; a malformed entry
  throws and names itself.

`AddDashboardEdge` registers today's credentialed policy only when an origin is set, and configures
`ForwardedHeadersOptions` only when proxies are listed: `XForwardedFor | XForwardedProto`,
`ForwardLimit = 1`, both known lists cleared, networks added to `KnownIPNetworks` (`KnownNetworks` is
obsolete in .NET 10). `app.UseDashboardEdge()` is the first middleware: `UseForwardedHeaders()` when a
proxy is trusted, then `UseCors` when an origin is set. Because the SPA is same-origin, nothing in the
bundled UI needs CORS. A cross-origin SPA could not send the XSRF header anyway, so `WebOrigin` grants
read-only cross-origin access; it is documented as such. A Warning is logged when forwarded headers
arrive from a peer that is not trusted, since the sign-in limiter is then keyed on the proxy.

### 4.5 Compose

A fifth service, `dashboard-web`, `depends_on: dashboard-api: service_healthy`, healthcheck
`wget -q -O /dev/null http://127.0.0.1:8080/healthz` (127.0.0.1 because BusyBox may try `::1` first).
Ports bind to loopback: `127.0.0.1:4200:8080` for the UI and `127.0.0.1:5080:8080` for the API. Each
overlay overrides both with `!override` lists using the same prefix:

| Overlay | API | UI |
| --- | --- | --- |
| base | 5080 | 4200 |
| `docker-compose.wolverine.yml` | 5180 | 4300 |
| `docker-compose.masstransit.yml` | 5280 | 4400 |
| `docker-compose.brighter.yml` | 5380 | 4500 |
| `docker-compose.http.yml` | 5480 | 4600 |
| `docker-compose.mongo.yml` | 5580 | 4700 |
| `docker-compose.redis.yml` | 5680 | 4800 |

`docker-compose.chaos.yml` remaps nothing and says so in a comment. `dashboard-api` loses
`Dashboard__WebOrigin` and gains `Dashboard__TrustedProxies` (the private ranges, because the proxy's
address is dynamic). The identity block (§8.10) is added in its own commit: the
`vsaga-dashboard-identity` volume at `/var/lib/vsaga-dashboard`, `Dashboard__Identity__Sqlite__Path`,
the seeded administrator, `Dashboard__Session__CookieName`, and `Dashboard__ApiKeyRole`. The README
gains one sentence on exposing the dashboard deliberately: change the bind address together with
`Dashboard__Admin__Password` and `Dashboard__ApiKey`, and put TLS in front.

### 4.6 The API image

The runtime stage of `dotnet/src/VSaga.Dashboard.Api/Dockerfile` creates `/var/lib/vsaga-dashboard`
(mode 0700), chowns it to `$APP_UID`, sets `ENV Dashboard__Identity__Sqlite__Path=/var/lib/vsaga-dashboard/identity.db`
and switches to `USER $APP_UID` (uid 1654), all in one commit with the compose volume: a volume first
created by a root-run container stays root-owned and the app user could never open the database. The
identity work adds only its two csproj `COPY` lines to the build stage.

### 4.7 CI

A third job, `compose`: `docker compose config -q` for the base file and the chaos overlay; for each of
the six overlays, the rendered published ports of `dashboard-api` and `dashboard-web` equal the table
above (a forgotten `!override` shows up as two ports), and the host IP is `127.0.0.1`; once the identity
block lands (C37), the rendered `Dashboard__Identity__Sqlite__Path` of `dashboard-api` lies under the
target of its `vsaga-dashboard-identity` volume, in the base file and in every overlay; `docker compose
build`; `nginx -t` in the web image; `up --wait`; then through `http://localhost:4200` a deep link
returns `<app-root`, `/api/saga-types` answers 200 with the API key, a missing hashed asset answers
404, hub negotiate yields a `connectionToken` and the upgrade answers 101. Logs on failure,
`down -v` always. Once sign-in exists (§12, C41) the job also asserts `/health` lists `identity` as
healthy, signs in through 4200 with the seeded credentials and the `X-XSRF-TOKEN` header, lists sagas
with the cookie, negotiates the hub with `Origin: http://localhost:4200`, and expects 403 for another
origin.

### 4.8 Configuration added

| Key or variable | Default | Where |
| --- | --- | --- |
| `Dashboard:WebOrigin` (changed) | empty: CORS off | API |
| `Dashboard:TrustedProxies` | empty: forwarded headers ignored | API |
| `DASHBOARD_API_UPSTREAM` | `dashboard-api:8080` | web image |
| `DASHBOARD_OUTER_PROXY` | `false` | web image |
| `VSAGA_API_URL` | `http://localhost:5080` | `ng serve` |

---

## 5. Timeline, map jump and step data (SPA)

### 5.1 Pure modules under `src/app/util/`

| Module | Exports | Notes |
| --- | --- | --- |
| `state-json.ts` | `parseStateJson`, `formatStateJson`, `prettyJson`, `SAGA_STATUSES`, `SAGA_KINDS` | Replaces `prettyDataJson` with identical output. Top-level numeric `Kind`/`Status` read as names. A value is an omission marker when it is an object with an own key matching `^\$vsaga\w*Omitted$` set to `true`, which covers MongoDB's `$vsagaPayloadOmitted` and the engine's `$vsagaStateOmitted`. |
| `json-diff.ts` | `diffJson(before, after, max = 200)`, `previewValue` | Depth-first: objects by key (after's order, then removed keys), arrays index by index with no move detection, everything else by `Object.is`. Paths like `Order.Lines[2].Quantity`. Truncates at 200 changes with a notice. |
| `time-format.ts` | `formatRecordedAt`, `formatLocal`, `formatOffset`, `timezoneLabel` | Local `HH:mm:ss.SSS` (prefixed with the date when the day differs from the first entry's), UTC `yyyy-MM-dd HH:mm:ss.SSS UTC`, offsets `+1.204 s`, `+2:05.300`, `+1:02:03`, `+2d 01:02:03`. An explicit time zone parameter keeps specs deterministic. |
| `entry-type-label.ts` | `entryTypeLabel` | Moved from the detail page; the map uses it too. |
| `saga-transitions.ts` | `foldTimeline`, `effectiveSnapshotState` | Folds entries into steps (§5.2). |

`src/app/testing/timeline-fixtures.ts` holds entry and step builders. The commit that creates
`src/app/testing/` excludes it from `tsconfig.app.json`.

### 5.2 The fold

A step starts when the saga starts, receives a message, fires a timeout, is retried, or dead-letters a
message that never reached `MessageReceived`. The fold keeps the API's ascending order and never parses
a snapshot beyond a prefix test for markers. An entry attaches to a step by `messageId` first, then by
`causationId`, and only then by adjacency ("the step touched last"):

| Entry | Lands in |
| --- | --- |
| `SagaStarted` | Opens a `start` step and registers its `messageId`. |
| `MessageReceived` | The `start` step awaiting the same id without a `MessageReceived` yet (the engine logs both for the initiating message). Otherwise it opens a `message` step when its id is the id of some outcome entry (`StepSucceeded`, `StepFailed`, `UnexpectedEvent`, `StatePersisted`) or the current step already has an outcome. A `MessageReceived` with no outcome of its own goes to the step holding the outbound entry its `causationId` names (a `.CallHttp` reply); else it joins a current step still running only when it carries no `causationId` at all (an older reply's shape), and opens its own step otherwise. |
| `TimeoutFired` | Opens a `timeout` step. |
| `ManualRetryRequested` | Opens a `retry` step; its actor is `sourceService` without the `dashboard:` prefix. Its `messageId` is not registered (the in-process retry reuses the failed id). |
| `StepSucceeded`, `StepFailed`, `UnexpectedEvent`, `StatePersisted` | The step awaiting that `messageId`; with no id, the latest timeout or retry step without a snapshot; else the current step. An outcome whose id no step started (an `UnexpectedEvent` for a message whose instance was not found) opens a `detached` step instead of joining the current one. `StatePersisted` becomes the step's snapshot and is never a row. |
| `DeliveryExhausted` with a `messageId` | The step awaiting it, else a new `delivery` step, so the Failed snapshot that follows is not charged to the previous step. |
| `TimeoutScheduled`, `SagaCompleted` with a `messageId` (stamped since C20, §6.6) | The step with that id. |
| Outbound (`MessagePublished`, `MessageSent`, `ChildSaga*`) | The step whose inbound id equals `causationId`, else the current step. |
| Anything else, and entries recorded before C20 without ids | The current step; with nothing to join, a leading `detached` step. |

Each step's snapshot state is `recorded`, `omitted` (a marker), `withheld` (null payload: no
`sagas.data`), `not-persisted` (an unhandled outcome, a retry or detached step, a non-final timeout with
no outcome), or `missing`. A step is `pending` only while its newest entry is younger than five seconds
(a named constant; a row up to that much ahead of the browser clock also counts as young); after that it falls back to `missing`, or `not-persisted` for a timeout with no
outcome. This matters because two engine paths leave a final step without a snapshot for good: a step
that lost its persist race (its `MessageReceived` and `StepSucceeded` stay in the log, the redelivery is
skipped as a duplicate) and a timeout that was claimed but not handled.

### 5.3 Components and the detail page

- `LocalTime` renders `<time datetime title="UTC ...">`. The summary card labels become
  `Created (UTC+02:00)` and `Updated (UTC+02:00)`.
- `SagaTimeline` (inputs `history`, `focusedSequence`, `canViewData`, `live`, and `openKeys` as a model
  input; output `entrySelected`). A hint line explains Recorded at. Each step header shows "Step N",
  the title and the outcome; the title is a button that jumps to the step's last entry, which is the
  state after the step. Every entry row is a native `<button>` reading `#ordinal`, the entry label,
  states, message type, service, error, "Recorded at" with the local time (UTC in `title`) and the
  offset. With `canViewData`, a sibling "Data" toggle per step opens the inspector. An
  `afterRenderEffect` scrolls to and focuses the focused row once per new focus.
- `SagaDataInspector`: Changes (against the nearest earlier recorded snapshot, named), Full state,
  Message (where a payload was recorded), Copy JSON (raw text; hidden without `navigator.clipboard`).
  `Version` and `UpdatedAtUtc` changes go on one muted "engine bookkeeping" line. Notes for the other
  states: omitted ("The state was too large to snapshot at this step (N bytes, limit L)", or "the saga's
  snapshot budget of B bytes was used up" for a budget marker, §6.5), withheld, not-persisted, pending,
  missing.
- `SagaDataOverview`, under the summary card: a "Saga data" group with buttons "At start", "At end"
  ("Current" until the status is terminal) and "Compare", aria-labels "Data at start" and "Data at end"
  ("Current data" while the end button reads "Current", so the accessible name contains the visible
  word). At start is the initiating message plus the first recorded snapshot; At end is
  `detail.dataJson`; Compare diffs the two, and while disabled its title names the missing side (no
  snapshot, or no stored state). Without `sagas.data` the buttons are disabled beside "Saga data is hidden for
  your role. It needs the sagas.data permission." and the timeline shows no Data toggles.
- `SagaMap` gains `focusSequence` (input), `focusCleared` and `timelineRequested` (outputs) and a
  `role="status"` banner: "As of entry #12 of 34: StepSucceeded, recorded at 14:03:07.140 (+1.224 s)",
  with "Nothing moved between services at this entry" for a plain event, "Entry 57 is not on the map
  yet; showing the closest earlier entry" for a fallback, and a "Back to this entry in the timeline"
  button. `resolveFocusIndex(events, sequence)` in `saga-map-layout.ts` picks the exact event, else the
  last earlier one, else index 0. Focus renders as its own `node--focus` outline class;
  `computeNodeStates` is unchanged, so a failed node stays failed and ordinary playback is untouched.
  Play, restart, step and scrub release the focus.
- `SagaDetail` has two tabs, Map and Timeline. Query parameters `?tab=&entry=&data=` are validated (tab
  only `timeline`, entry a positive safe integer, data one of three values); tab and entry changes push
  history, clearing the focus replaces it. `openKeys` lives here, so open inspectors survive a jump to
  the map and back, and resets with the other per-saga state when the saga changes.
- Live refresh: `SagaUpdated` patches the summary at once and feeds a subject piped through
  `auditTime(250)`; one refresh reloads timeline, map, related sagas, children and the detail
  (`refreshDetail()` never touches `loading` and keeps the summary with the higher version). Pushed
  timeline entries trigger the same refresh instead of being appended. One follow-up timeline fetch
  1500 ms later runs only when the last step is `pending` and the timeline already holds at least one
  `StatePersisted`.
- Load errors: with nothing loaded, `banner banner--error` and Try again; with stale content,
  `banner banner--warning` above it. Reconnects retry both.
- `canViewData` is a constant `true` until the permission wiring lands (§12, C47).

Shared global classes in `src/styles.scss`: `.sr-only`, `.muted`, `.micro-label`, `.json-block`, `.btn`
with `.btn--quiet`, and `.banner` with `--warning`/`--error` (the base class carries the padding). No
`data-tour` attribute is added by this work (§9.1). New stylesheets stay under 2.5 kB (timeline), 2 kB
(inspector) and 1 kB (overview); `saga-detail.scss` shrinks.

### 5.4 The failed step in the UI

From C28, for a `Failed` or `TimedOut` saga the page loads the retry plan (§7.2) and reloads it when a
refresh changes the status. The step containing `failureSequenceNumber` gets a "Failed here" marker and
error styling; the step containing `step.sequenceNumber` reads "Re-run starts here" when it is a
different step (a timeout). With no explicit `?entry`, the map opens focused on `failureSequenceNumber`.
The retry confirmation reads "Re-run step N (<message type>, <from state>) for this saga only", where N
is the ordinal of the step holding `step.sequenceNumber`, plus one line saying that other services
consuming that message type still receive it. When the plan says not retryable, the button is disabled
and the plan's reason is shown.

---

## 6. State snapshots (engine and API)

The decision and its rejected alternatives are in ADR 0007. This section is the shape.

### 6.1 The entry

`SagaEntryType.StatePersisted`, appended last (value 21). Its `PayloadJson` is the state blob exactly as
the snapshot store wrote it, or a marker. `MessageType`/`MessageId` name the inbound message whose step
it follows (null after a timeout or a dashboard reset). `FromState` and `ToState` are always null,
because compensation order is read from `ToState`. It is not a correctness input: neither dedupe nor
compensation reads it. `VSaga.Abstractions/Persistence/SagaStateSnapshot.cs` is the one definition of
the shape: `DefaultMaxBytes = 262_144`, `CreateEntry(...)`, and `ToPayload(stateJson, maxBytes)`, which
returns the blob or `{"$vsagaStateOmitted":true,"bytes":N,"limit":L}` (UTF-8 bytes; a cap of 0 records
size-only markers). No contract signature, schema or migration changes.

### 6.2 Options

| `SagaOrchestratorOptions` | Default | Meaning |
| --- | --- | --- |
| `RecordStateSnapshots` | `true` | Off records nothing. |
| `MaxStateSnapshotBytes` | `262144` (256 KiB) | A larger state becomes the size marker. |
| `MaxStateSnapshotBytesPerSaga` | `1048576` (1 MiB); 0 = unlimited | Per-instance budget (§6.5). |
| `StateSnapshotTimeout` | 5 seconds | The append's own deadline (§6.4). |

The OrderProcessing sample binds an `Orchestrator` configuration section through
`ConfigureOrchestrator`, so compose can set `Orchestrator__*`; it is the first host to do so.
`Dashboard:StateSnapshots:MaxBytes` (default 262144) caps the snapshot the dashboard writes after a
retry reset (§6.7).

### 6.3 Where the engine records it

One helper, `PersistAndSnapshotAsync`, replaces the persist call at the committing sites, so nothing can
be inserted between the commit and the snapshot and the 59-line methods gain no line:

| Path | Site | Order |
| --- | --- | --- |
| Step success | `PersistAndFinalizeStepSuccessAsync` | stage outbox rows → persist + snapshot → metrics → drain → notifier |
| Step failure | `HandleStepFailureAsync` | `StepFailed` → stage `ChildSagaFinished` → persist + snapshot → discard deferred publishes → notifier → `ChildSagaFinished` |
| Timeout | `CommitAndDispatchTimeoutAsync`, after the final persist's race check | stage → final persist → snapshot → drain → outcome |
| Delivery exhaustion | `RecordDeliveryExhaustedAsync` | `DeliveryExhausted` → persist + snapshot → notifier |

Before the drain because the in-memory transport dispatches synchronously from inside the publish, so a
nested step's higher-version snapshot would otherwise precede this one; on brokers a reply can only exist
after the drain. Nothing is recorded for the timeout claim, the business-key reservation insert,
`UnexpectedEvent`, duplicates, an unhandled timeout or a lost race. Core serialises with the same
generic `JsonSerializer.Serialize(state)` every provider uses, on the same object with nothing between
the store returning and the call, so the snapshot text equals the stored blob; a golden-text test and a
blob-equality test pin it.

### 6.4 Best effort, bounded in time

The helper swallows every exception, cancellation included, and logs a Warning naming saga, correlation
id and version; the only effect is a step with no snapshot. It runs under its own deadline: a linked
`CancellationTokenSource` with `CancelAfter(StateSnapshotTimeout)`. Without one a stalled append would
hold back every deferred publish and the ack, and past `DispatchGracePeriod` (30 s) the recovery poller
would republish rows the inline drain then sends again. Five seconds matches the Redis client's own
timeout (the Redis append ignores the token) and sits well under the 30 s grace period and Npgsql's 30 s
command timeout. A timeout is treated like any other swallowed failure.

`EfCoreSagaEventLogStore.AppendAsync` detaches its entity when the save throws, so a failed or
cancelled append cannot be re-sent by a later save in the same unit of work. Staged outbox rows are
untouched. This lands before the engine records anything.

### 6.5 Cost and the per-saga budget

Each committed transition costs one more serialisation and one more append. `GetVisitedStatesAsync`
reads every snapshot on every message: with S transitions of B bytes, a late message reads about S·B
more and the saga about B·S²/2 over its life. A per-snapshot cap does not bound that, so there is a
per-instance budget. `GetVisitedStatesAsync` also sums the UTF-8 length of `StatePersisted` payloads
already in the timeline; the sum rides on `SagaContext` to the persist sites. A snapshot is recorded in
full when the sum plus its own size stays within `MaxStateSnapshotBytesPerSaga`. Past the budget,
success-path and timeout snapshots become a budget marker, `{"$vsagaStateOmitted":true,"bytes":N,"budget":B}`,
so the UI does not report a per-snapshot limit that was not exceeded. Step-failure and exhaustion
snapshots are always recorded in full (up to the per-snapshot cap), because those are the ones an
investigation needs. Early snapshots are kept, so At start still works; At end never depends on a
snapshot.

| Steps × blob | Extra read per late message | Over the saga's life |
| --- | --- | --- |
| 8 × 0.5 KB (the sample) | 4 KB | 16 KB |
| 30 × 4 KB | 120 KB | 1.8 MB |
| 200 × 32 KB, without the budget | 6.4 MB | about 640 MB |

The budget caps the third row at about 1 MiB of full snapshots plus markers. Redis is the exposed
provider (§3); the redis and mongo overlays are measured live and the figures go into
`docs/persistence.md`. The real fix, a payload-free timeline read for visited states and the map, is a
contract addition and is deferred (§14).

### 6.6 Sequence numbers and message ids

`LogAsync` notifies with `entry with { SequenceNumber = <stored> }`, so pushed entries carry their real
sequence number instead of 0. In `HandleStepSuccessAsync`, `TimeoutScheduled` and `SagaCompleted` carry
the inbound message id, and the `SagaContext` log sink fills a null `CausationId` with the inbound
message id (C20). None of these feeds dedupe (only `SagaStarted` and `MessageReceived` count), visited
states or the map's stitching.

### 6.7 Dashboard API

- `SagaMapBuilder.Build` drops `StatePersisted` before ordering, so snapshots add no event, node or edge
  and do not move `FailureEventIndex`; the timeline's `#i` and the map's agree.
- `Endpoints/SagaTimelineRedaction.Apply(timeline, includeData)` returns the list, or a copy with
  `PayloadJson` and `ErrorMessage` nulled. Entries are never dropped, so sequence numbers and the fold
  are the same for every caller. The timeline lambda becomes a named handler; authentication (§8.5)
  supplies the boolean.
- `SignalRSagaChangeNotifier.TimelineEntryAddedAsync` pushes every entry with `PayloadJson` and
  `ErrorMessage` nulled, for everyone; the SPA refetches anyway. This lands before the engine emits
  snapshots.
- `Endpoints/SagaResetSnapshotRecorder.RecordAsync(sagaType, correlationId, resetVersion, ct)` never
  throws. After a retry reset it reads `GetDataJsonAsync`, skips when the blob is null or its `Version`
  is not `resetVersion` (a step already moved the saga on and recorded its own snapshot), and appends a
  snapshot with no message identity. It runs only when the timeline already holds a `StatePersisted`, so
  the dashboard never writes a saga's first snapshot and a host with `RecordStateSnapshots = false` is
  honoured. Its cap is the smaller of `Dashboard:StateSnapshots:MaxBytes` and the `limit` of the
  most recent `$vsagaStateOmitted` marker that carries a `limit` (budget markers, §6.5, are ignored for
  this), so a host that set `MaxStateSnapshotBytes = 0` to keep state out
  of the log is not overridden by the dashboard.
- Version skew: an API older than C16 serialises entry type 21 as a number and returns snapshot
  payloads unredacted. Deploy the dashboard before the engine hosts; compose builds both together.

### 6.8 Other consumers

The four persistence samples print every timeline entry; `CheckoutDemo.cs` gains a `StatePersisted` arm
("state saved, N bytes") and `dotnet/samples/Persistence/README.md` mentions the entry. `SagaTestHarness`
records snapshots by default; tests opt out with
`new SagaOrchestratorOptions { RecordStateSnapshots = false }`. `docs/testing.md` says so.

---

## 7. Retry from the failed step

This is the authoritative design of the retry change; ADR 0008 records the decision and its
alternatives. No blueprint covered it.

### 7.1 Why

Today's retry has two problems. First, a business failure or timeout resets the saga to its initial
state and replays the initiating message, re-running every step, with their side effects, to reach the
one that failed. Second, the redrive is a republish by message type under the saga's correlation id, so
every saga type subscribed to that type processes it as a new delivery. A user allowed to retry one saga
type can drive steps in others: in the sample, retrying a timed-out `InvoiceFollowUpSaga` replays
`InvoiceIssued`, `PostShipmentChoreography` handles it again and a second `InvoiceDeliverySaga` sends a
second customer email. Saga-type scoping of `sagas.retry` (§8) is only a boundary if the redrive is
targeted.

The new rule: a dashboard retry re-runs exactly one step, the one that failed, for exactly one saga
type, after resetting `CurrentState`/`Status` to what they were before that step. The in-process
`SagaOrchestrator.RetryAsync` (`ISagaRetryDispatcher`, `SagaTestHarness.RetryAsync`) is unchanged: it
already replays only the failed step in the one orchestrator it runs in.

### 7.2 The retry plan

`VSaga.Dashboard.Api/Endpoints/SagaRetryPlanner.cs`: a pure static
`SagaRetryPlan Plan(SagaSummary summary, IReadOnlyList<SagaLogEntry> timeline)`, unit-tested directly
and used by both endpoints. It ignores `StatePersisted` entries.

1. Status not `Failed` or `TimedOut`: not retryable, reason "Only Failed or TimedOut sagas can be
   retried."
2. Find the failure entry F: walk the timeline backwards and take the latest of `StepFailed`,
   `SagaCompleted`, `TimeoutFired`, and `DeliveryExhausted` **with a message id**. Recency, not a fixed
   precedence: a saga that failed technically, was retried, and later failed for a business reason
   re-runs the later step. A `DeliveryExhausted` without a message id is skipped: it records a deferred
   publish that failed or was discarded (§3), and the failure path logs one after `StepFailed` whenever
   the step had queued a publish before throwing. Counting it would hide the step that failed.
3. Classify F and find the step:

| F | `failureKind` | The step's message | `fromState` |
| --- | --- | --- | --- |
| `StepFailed` | `StepFailed` | `F.MessageType` / `F.MessageId`, body `F.PayloadJson` | `F.FromState` |
| `DeliveryExhausted` (with id) | `DeliveryExhausted` | `F.MessageType` / `F.MessageId`; body from the latest `MessageReceived` or `SagaStarted` before F with that id and a payload | `summary.CurrentState`: the dead-lettered step never committed |
| `SagaCompleted` | `BusinessFailure` | The step's inbound id is `F.MessageId` (stamped since C20), else the id of the latest `StepSucceeded` before F; body from the latest `MessageReceived`/`SagaStarted` before F with that id | the `FromState` of the latest `StepSucceeded` before F with that id |
| `TimeoutFired` | `TimedOut` | S = `F.FromState`, the timed-out state. The step is the latest `StepSucceeded` before F with `ToState == S` and `FromState != S`; it must carry a message id, or the plan is not retryable ("The timed-out state was entered by a timeout, which has no message to replay."); with no such `StepSucceeded` at all, not retryable, reason "No failed step could be identified in this saga's timeline." (`step` null); body from the latest `MessageReceived`/`SagaStarted` before F with that id | that `StepSucceeded`'s `FromState` |
| none | — | not retryable: "No failed step could be identified in this saga's timeline." | — |

   Using the step's own id for a business failure avoids picking a `.CallHttp` reply logged mid-step
   as a `MessageReceived` with a fresh id. A saga that reached `TimedOut` through a message step (for
   example `InvoiceFollowUpSaga`'s `ChildSagaFinished` branch) ends in `SagaCompleted` and is classified
   `BusinessFailure`; only a timeout that fired is `TimedOut`.

4. No recorded body for the step's message: not retryable, with a reason in plain words: "This saga was
   recorded before vSaga stored the message of every step, so the `<MessageType>` message that ran the
   step to re-run cannot be replayed." (the step's message type, for example "so the PaymentFailed
   message that ran the step to re-run"). For `DeliveryExhausted` the reason adds that the message may
   have been dead-lettered before it was recorded at all. That is the usual case: a `MessageReceived`
   with the dead-lettered id exists only when its append succeeded on the final delivery attempt,
   because a durable `MessageReceived` on any earlier attempt makes the next redelivery a duplicate,
   which `HandleCoreAsync` acks rather than dead-letters. `StepFailed` always carries its body, and a
   step run by the initiating message finds it on `SagaStarted`, so those stay retryable for sagas
   recorded before C23.

   MongoDB's size marker (`{"$vsagaPayloadOmitted":true,"bytes":N,"limit":L}`, stored in place of a
   payload above `MaxPayloadJsonBytes`) is not a body either: replayed, it would deserialise into a
   message whose every field is default, and the step would re-run, side effects included, on that. A
   payload starting with that key counts as no body. The body search prefers an entry with a real body
   over one with the marker, a `StepFailed` whose own payload is the marker falls back to its
   `MessageReceived`, and when only the marker exists the reason is "The message that ran this step was
   too large to be recorded, so it cannot be replayed."

**Wire shape** of `GET /api/sagas/{sagaType}/{correlationId}/retry-plan` (camelCase, `failureKind`
as the C# member name through the global `JsonStringEnumConverter`, like `entryType`):

```json
{
  "retryable": true,
  "reason": null,
  "failureKind": "BusinessFailure",
  "failureSequenceNumber": 142,
  "step": {
    "sequenceNumber": 138,
    "messageType": "PaymentFailed",
    "messageId": "5b0c6f0e2f8a4d5f9a37c1e0b2d4a6c8",
    "fromState": "Gathering"
  }
}
```

- `failureKind` (`StepFailed`, `BusinessFailure`, `DeliveryExhausted`, `TimedOut`) and
  `failureSequenceNumber` are F's, and are present whenever F was found, retryable or not, so the UI can
  mark the failed step even when it cannot offer a retry. Both are null only when no F exists.
- `step` is present whenever the step was identified, even when its body is missing. Its
  `sequenceNumber` is the step's inbound entry: the latest `MessageReceived` before F with the step's
  id, else the latest `SagaStarted` with it, else a fallback by kind: F itself for `StepFailed` and
  `DeliveryExhausted`, and the step's `StepSucceeded` for `BusinessFailure` and `TimedOut`. The SPA maps
  it to its step ordinal.
- `reason` is null when `retryable` is true.
- The endpoint answers 404 for an unknown saga and 200 with a plan otherwise, never 409 or 422. It is
  read-only and, once authentication lands, requires `sagas.view` on the route's saga type.

Worked examples follow (§7.6).

### 7.3 `POST .../retry`

`RetrySagaAsync` becomes:

1. Read the summary: 404 when absent; 409 `{ error }` when not `Failed`/`TimedOut`.
2. Read the timeline and run the planner. Not retryable: 422 `{ error: reason }`.
3. Append `ManualRetryRequested(fromState: summary.CurrentState, toState: step.fromState,
   messageType: step.messageType, messageId: <the replayed message's original id>)`; once
   authentication lands, `sourceService` is the caller's audit actor (`dashboard:<username>` or
   `dashboard:api-key`).
4. **Always** `ResetStateAsync(sagaType, correlationId, step.fromState, Running, summary.Version, now)`,
   even when the state does not change (a `StepFailed` or `DeliveryExhausted` retry). The version check
   is the concurrency guard: a saga that moved since the caller read it answers 409 ("modified
   concurrently; reload and try again"). The saga also leaves `Failed` at once, so a second click meets
   the status guard. Business fields inside the blob are not rolled back; only `CurrentState`, `Status`,
   `Version` and `UpdatedAtUtc` change.
5. Record the reset snapshot (§6.7) for version `summary.Version + 1`.
6. Publish: `PublishRawAsync(step.messageType, body, MessageEnvelope.New(correlationId,
   { ["x-vsaga-target-saga-type"] = summary.SagaType }))`. The fresh message id gets past dedupe.
7. Success: 202.
8. On any exception from the publish: best-effort restore with
   `ResetStateAsync(sagaType, correlationId, summary.CurrentState, summary.Status, summary.Version + 1, now)`.
   A `SagaConcurrencyException` here is logged as a Warning and the request continues. When the restore
   commits, record a snapshot of the restored state (version `summary.Version + 2`) with the same
   recorder. Answer 502 as a problem whose detail says whether the saga was restored to its previous
   state and status, with a `restored` boolean extension. A `MessageTransportPublishException`'s message
   goes into the detail; any other exception is logged as an Error and the detail names only its type,
   because `IMessageTransport` does not promise to wrap every failure (`RabbitMqTransport` lets a
   `TaskCanceledException` out of opening a channel on a broker that stopped answering). Restoring after
   an exception whose publish outcome is unknown is still safe: the restore is version-checked, so if
   the redrive did go out and moved the saga on first, the restore loses and the 502 says so.

Steps 5 to 8 run with `CancellationToken.None`, not the request's token. The reset is committed by then,
and a client that disconnected or aborted between the reset and the publish would otherwise leave the
saga `Running` in the step's from-state with no redrive, where the status guard refuses every further
dashboard retry with 409.

The `ManualRetryRequested` entry is appended before the reset, as today, so a 409 or a 502 leaves it in
the timeline with no step after it; the retry step in the UI then shows no snapshot and no re-run. The
reset-to-start branch, the conditional reset and their tests are removed. `SagaMapBuilder` is unchanged:
its failed-edge rule stays, and the planner is the authority for the retry and the UI marker.

### 7.4 The target header

`MessageEnvelope.TargetSagaTypeHeader = "x-vsaga-target-saga-type"`. The `x-vsaga-` prefix is what makes
every adapter round-trip it, including Brighter and the HTTP transport, which forward only that prefix;
each adapter's header round-trip test is extended with it.

The first statement of `SagaOrchestrator.HandleCoreAsync`:

| Inbound header | Behaviour |
| --- | --- |
| absent | unchanged |
| present, equal to `SagaType` (ordinal) | unchanged |
| present, any other value, including empty or a different case | log at Debug, return. `HandleAsync` then acks the message. Nothing is deserialised, no instance is looked up or created, no timeline entry is written. |

The header never propagates: outbound envelopes are built by `MessageEnvelope.From`, which copies only
the headers its caller passes, and no publish path passes inbound headers. A redelivery after an
infrastructure failure copies the inbound headers, so a retry that hits a transient store failure stays
targeted on its redelivery. The in-process `RetryAsync` passes no headers and is unaffected.

`RunStepAsync`'s `MessageReceived` entry carries the message body in `PayloadJson`
(`JsonSerializer.Serialize(message, message.GetType())`, as `StepFailed` already does), on every step,
the first included. MongoDB's existing payload guard (`MaxPayloadJsonBytes`, 12 MiB) applies; redaction
(§6.7, §8.5) already covers payloads. This is what makes a business failure or timeout retryable, and it
also lets a retried saga be retried again.

### 7.5 What other consumers still see

- **Other saga types** subscribed to the message type receive the replay and ignore it, provided their
  host runs an engine that contains C23. The dashboard cannot detect an older engine: an engine older
  than C23 ignores the header and processes the replay as a new delivery, exactly as today. The minimum
  engine version for a targeted retry is therefore the first release containing C23, for every host
  running a saga type subscribed to a replayed message type. The repository has no release tags yet, so
  the docs name the change rather than a version number. ADR 0006, ADR 0008 and `docs/dashboard.md`
  "Manual retry" state this.
- **Consumers that are not sagas** (participants subscribed through the transport directly) receive the
  replay as a new message. The retry confirmation says so in one line; the user guide's retry section
  says so too.
- **The retried saga** runs the step from the reset state. Every side effect the step has (publishes,
  `.CallHttp` calls, child sagas, compensations) happens again. A step whose outcome is decided only by
  the message reaches the same outcome again: replaying `OrderSaga`'s `PaymentFailed` fails the saga
  again. The retry is useful when the step's outcome depends on something that has since changed (a
  participant, a lookup, configuration, a fixed bug) or when the step was interrupted (an exception, a
  dead-letter, a timeout waiting for a reply that now arrives).

### 7.6 Worked examples

Sequence numbers are illustrative; ids are shortened.

**Technical failure.** An `OrderSaga` step in `Gathering` queues a publish, then throws on
`PaymentCharged`.

| # | Entry |
| --- | --- |
| 30 | `MessageReceived` `PaymentCharged` id `a1`, payload |
| 31 | `StepFailed` from `Gathering`, `PaymentCharged` id `a1`, payload, error |
| 32 | `StatePersisted` id `a1` (`"Status":2`) |
| 33 | `DeliveryExhausted` "Deferred publish discarded ..." (no id) |

F is #31 (#33 has no id). Plan: `StepFailed`, failure 31, step `{30, PaymentCharged, a1, Gathering}`.
Retry: `ManualRetryRequested(Gathering → Gathering, PaymentCharged, a1)`, reset to
`Gathering`/`Running` at the read version, reset snapshot, publish `PaymentCharged` with a fresh id and
`x-vsaga-target-saga-type: OrderSaga`. `OrderSaga` logs `MessageReceived` (fresh id, payload) and runs
the step; any other saga type ignores the message.

**Business failure.** `OrderSaga` in `Gathering` receives `PaymentFailed`.

| # | Entry |
| --- | --- |
| 138 | `MessageReceived` `PaymentFailed` id `c3`, payload |
| 139–140 | compensation entries |
| 141 | `StepSucceeded` `Gathering → Failed`, id `c3` |
| 142 | `SagaCompleted` to `Failed`, id `c3` |
| 143 | `StatePersisted` id `c3` |

Plan: `BusinessFailure`, failure 142, step `{138, PaymentFailed, c3, Gathering}`. Retry resets to
`Gathering`/`Running` and replays `PaymentFailed`, which fails the saga again (§7.5). Recorded before
C23, #138 has no payload: 422 with the plain-words reason.

**Delivery exhausted.** `InventoryReserved` (id `d4`) fails with an infrastructure error on every
delivery attempt; on the final attempt the `MessageReceived` append succeeds before the failure (on any
earlier attempt a durable `MessageReceived` would make the next redelivery a duplicate, acked rather
than dead-lettered).

| # | Entry |
| --- | --- |
| 50 | `MessageReceived` `InventoryReserved` id `d4`, payload (appended on the final attempt) |
| 51 | `DeliveryExhausted` `InventoryReserved` id `d4`, error |
| 52 | `StatePersisted` id `d4` (`"Status":2`, `CurrentState` `Gathering`) |

Plan: `DeliveryExhausted`, failure 51, step `{50, InventoryReserved, d4, Gathering}`, `fromState` taken
from the summary. Usually the store failed before anything was recorded, no `MessageReceived` with `d4`
exists, and the plan is not retryable.

**Timeout.** `InvoiceFollowUpSaga` waits in `AwaitingArchival` past `ArchivalWaitTimeout`.

| # | Entry |
| --- | --- |
| 60 | `SagaStarted` `InvoiceIssued` id `e5`, payload, to `Requested` |
| 61 | `MessageReceived` `InvoiceIssued` id `e5`, payload |
| 62 | `ChildSagaStarted` (causation `e5`) |
| 63 | `StepSucceeded` `Requested → AwaitingArchival`, id `e5` |
| 64 | `TimeoutScheduled` `AwaitingArchival`, id `e5` |
| 65 | `StatePersisted` id `e5` |
| 66 | `TimeoutFired` from `AwaitingArchival` |
| 67 | `StepSucceeded` `AwaitingArchival → Abandoned` (no id) |
| 68 | `StatePersisted` (no id, `"Status":5`) |

Plan: `TimedOut`, failure 66, step `{61, InvoiceIssued, e5, Requested}`. The timeline marks step 2
(the timeout step opened by #66) "Failed here" and step 1 (the start step, #60–#65) "Re-run starts
here"; the confirmation reads "Re-run step 1 (InvoiceIssued, Requested) for this saga only". Retry resets to `Requested`/`Running` and replays
`InvoiceIssued` targeted at `InvoiceFollowUpSaga`: `PostShipmentChoreography`, which handles the same
message under the same correlation id, ignores it, so no second `InvoiceDeliverySaga` starts. The step
itself runs again, so a second `InvoiceArchivalSaga` child starts and a new timeout is scheduled. Had #63
been missing because `AwaitingArchival` was entered by a timeout transition (a `StepSucceeded` with no
id), the plan would be not retryable.

### 7.7 Tests

| Area | Cases |
| --- | --- |
| Engine (C23), `VSaga.Core.Tests` | a second saga type ignores a targeted message: no entries, no state, message acked; the addressed type processes it; an absent header changes nothing; the header is absent from the redriven step's outbound envelopes; a redelivery after an infrastructure failure keeps it; `MessageReceived` carries the payload on the first and on later steps |
| Adapters | each wire adapter's header round-trip test includes `x-vsaga-target-saga-type` |
| Planner (C24) | one case per failure kind; recency across a retried earlier failure; a `DeliveryExhausted` without id after `StepFailed` is skipped; a `.CallHttp` mid-step reply is not chosen; a `TimedOut` status reached through `SagaCompleted` is `BusinessFailure`; a timed-out state entered by a timeout is not retryable; missing payload (old saga) is not retryable while a first-step failure stays retryable; a MongoDB `$vsagaPayloadOmitted` marker is no body ("too large to be recorded"); not `Failed`/`TimedOut`; no F; `StatePersisted` ignored |
| Endpoints (C24) | `retry-plan` 404 and 200 shapes; the republished envelope carries the target header, the step's type and body; the reset goes to `step.fromState`/`Running` at the read version (a saga seeded past version 0), also for `StepFailed`; 409 on a raced reset; 422 with the reason text; 502 with `restored` true after a restore, false when the restore races; a non-transport exception from the publish restores too, at the reset's version, and the publish never gets the request's token; the reset snapshot follows `ManualRetryRequested`; the old reset-to-start tests are replaced |
| SPA (C28) | the marker on the step holding `failureSequenceNumber`; "Re-run starts here" only when different; the map opens on the failure without `?entry` and on `?entry` when given; the confirmation text names step, type and state; a non-retryable plan disables the button and shows the reason; the plan reloads after a status change |

---

## 8. Authentication and access

ADR 0006 records the decision, its options and the residual risks. This section is the shape.

### 8.1 Projects and store

- `dotnet/src/VSaga.Dashboard.Identity` (not packable; `Microsoft.AspNetCore.App` framework reference;
  EF Core): store-neutral records `DashboardUser`, `DashboardTeam`, `DashboardRole`, `AccessGrant`;
  `IDashboardIdentityStore` (aggregate-shaped: no `IQueryable`, an exclusive write scope, users, teams,
  roles, atomic sign-in counters) and `IDashboardKeyRingStore`; the EF Core store and context; services
  `PasswordPolicy`, `CredentialVerifier`, `AccessEvaluator`, `CallerAccessResolver`,
  `AccessAdministrationService`, `FirstAdministratorService`, `IdentityStartup`,
  `IdentityStoreXmlRepository`.
- `dotnet/src/VSaga.Dashboard.Identity.Sqlite`: generated migrations only, analyzers off as in
  `VSaga.Persistence.EFCore.Postgres`.
- `dotnet/tests/VSaga.Dashboard.Identity.Tests`.
- Tables `Users`, `Teams`, `TeamMembers`, `Roles`, `UserGrants`, `TeamGrants`, `DataProtectionKeys`.
  Uniqueness through `Normalized*` columns (`Trim().ToUpperInvariant()`); `Permissions` and `SagaTypes` as
  EF primitive collections; a UTC `DateTime` converter for ordering; invariant-checking writes in a
  `BEGIN IMMEDIATE` transaction; lockout counters as single `ExecuteUpdateAsync` statements.
- `CONTRIBUTING.md` records the two `dotnet ef` commands, each with `--context`.

### 8.2 Permissions and roles

| Permission | Label | Meaning |
| --- | --- | --- |
| `sagas.view` | View sagas | list, detail without data, timeline and map without payloads or error text, children, correlations, saga types, retry plan |
| `sagas.data` | View saga data | state blobs, message payloads, error messages; implies `sagas.view` for the same scope |
| `sagas.retry` | Retry sagas | `POST .../retry`; implies `sagas.view` for the same scope |
| `access.manage` | Manage access | `/api/admin/*`; counts only in a grant for all saga types |

Built-in roles, fixed ids, immutable: Administrator (all four), Operator (view, data, retry), Viewer
(view, data). Custom roles combine the same four. A grant pairs one role with "all saga types" or 1 to
100 named saga types; a user or team holds at most 20 grants, one per role. Effective access is the union
of the user's grants and their teams' grants; a disabled user has none; while `MustChangePassword` is
true, access is empty. The invariant "at least one enabled user holds `access.manage` for all saga
types" is checked on the proposed snapshot inside the exclusive scope of every mutation (409
`last_administrator`).

Saga type names in grants are trimmed, non-blank and free of control characters, stored exactly as
validated. `sagas.view` exposes state and message type names, service names, the parent saga type and
correlation id, and the username on a retry entry; ADR 0006 lists this.

### 8.3 Sessions, cookies and CSRF

- A policy scheme sends a request to the API-key handler when it carries `X-Api-Key` or
  `Authorization: Bearer`, or `access_token` on a hub endpoint; otherwise to the cookie handler. Both
  write the shared 401 problem body, which keeps "See docs/dashboard.md#authentication."
- Cookie: HttpOnly, `SameSite=Strict`, `SecurePolicy=SameAsRequest`, sliding idle timeout
  (`Dashboard:Session:IdleTimeoutMinutes`, 480), non-persistent, name from
  `Dashboard:Session:CookieName` (default `vsaga.session`; compose sets `vsaga.session.<project>`). The
  sign-in time is a ticket property that survives renewal; `ValidatePrincipal` rejects a ticket older
  than `Dashboard:Session:AbsoluteTimeoutHours` (24) and one whose security stamp no longer matches.
- `Dashboard:Session:RequireHttps=true` sets `CookieSecurePolicy.Always`, the `__Host-` cookie prefix
  and HSTS, and fails composition unless trusted proxies are configured. `docs/dashboard.md` states that
  TLS is required for anything beyond localhost.
- Antiforgery header `X-XSRF-TOKEN`; the request token is issued as the readable `XSRF-TOKEN` cookie by
  the session, login, logout, setup and password endpoints, each after setting `HttpContext.User` to the
  new principal. Enforcement is decided per endpoint after routing: every unsafe method on a matched
  endpoint is validated unless the endpoint carries an explicit exemption marker, which only the hub has.
  An API-key request is exempt only when its key arrived in a header. A failure is 400 with code
  `antiforgery`.
- `XSRF-TOKEN` stays one name across stacks. A token from another stack fails validation; the SPA then
  refetches the session and retries once. That path is load-bearing for two stacks in one browser and is
  tested on both sides.
- Data Protection keys live in the identity store (`SetApplicationName("VSaga.Dashboard")`), so a
  recreated API keeps sessions. The XML repository throws while the store is not ready, and the auth
  endpoints and cookie events check readiness first, so an unavailable store yields 503 or 401, never a
  new in-memory key. The database file holds hashes, stamps and the unencrypted key ring: the directory
  is 0700, the file 0600, and the docs say a backup of the volume is a credential backup and describe
  `ProtectKeysWithCertificate` as the way to encrypt the ring at rest.

### 8.4 Sign-in, passwords and throttling

- `CredentialVerifier`: unknown, disabled or locked accounts verify a dummy hash and count nothing; a
  wrong password increments the counter and locks at `Dashboard:Lockout:MaxFailedAttempts` (5; 0
  disables) for `Dashboard:Lockout:Minutes` (15); success resets it and rehashes when needed. A username
  over 64 or a password over 128 characters is rejected before any lookup with the same 401. Every
  failed sign-in completes no earlier than about 300 ms after the request started, plus jitter. Login
  failures are one uniform 401 `invalid_credentials`.
- Passwords: `Dashboard:Password:MinLength` (12) to 128, not equal to the username; a new password equal
  to the current one is rejected. A wrong `currentPassword` on `POST /api/auth/password` answers 400
  `invalid_credentials` with `errors.currentPassword`, counts against the account's failure counter, and
  ends the session at the threshold.
- Rate limits: login and password change are keyed on client address plus a hash of the normalised
  username (`Dashboard:RateLimit:AuthPerMinute`, 20); setup has a per-address window. A global
  concurrency limiter around password hashing (permits `max(2, cores/2)`, short queue, 429 when full)
  keeps hashing from starving the API. 429 carries `Retry-After`.
- Lockout stays. An attacker who knows a username can keep it locked; the OWASP device-cookie bypass is a
  follow-up (§14). The residual risk is recorded in ADR 0006 and the guide's Troubleshooting section
  documents the escape: restart with `Dashboard:Lockout:MaxFailedAttempts=0`.
- Security stamp (128 random bits) rotates on password change, administrator reset, disable and enable.
  The hub connections of the affected user are aborted on every rotation and on sign-out.

### 8.5 Enforcement on the saga endpoints

| Endpoint | Policy | Behaviour |
| --- | --- | --- |
| list | `sagas.view`, any scope | `ScopedSagaLister` (§8.7) |
| detail | `sagas.view` on the route type | 403 before any read when out of scope; without `sagas.data`, `dataJson` is null and not read |
| timeline | `sagas.view` | `SagaTimelineRedaction.Apply(..., includeData: has sagas.data)` nulls `payloadJson` and `errorMessage` |
| map | `sagas.view` | `SagaMapEvent.ErrorMessage` nulled without `sagas.data` |
| children, saga types, correlations | `sagas.view` | filtered to visible types |
| retry plan | `sagas.view` | §7.2 |
| retry | `sagas.retry` on the route type | `ManualRetryRequested.sourceService` is the audit actor; the targeted redrive (§7.4) is what makes the route-scoped check a real boundary |

The fallback policy requires an authenticated user everywhere; the anonymous set is exactly `/health`,
the session, login, logout and setup endpoints, and the Development-only OpenAPI document. 403 bodies
carry `code`, `permission` and `sagaType`, and the documentation pointer. One middleware sets
`Cache-Control: no-store` and `X-Content-Type-Options: nosniff` on every `/api` response.

### 8.6 The hub

`SubscribeToList()` and `SubscribeToSaga(...)` return `Task<bool>`, resolve access afresh and never
throw on denial. Unscoped view joins `saga:list`; scoped view joins one `saga-list:{sagaType}` group per
scoped type (types that have not run yet included); none returns false. `SubscribeToSaga` joins only
with `sagas.view` on that type. Pushes go to all three group kinds. An origin guard applies to endpoints
carrying the hub marker: no `Origin`, `Origin == {scheme}://{Host}`, or `Origin == Dashboard:WebOrigin`
when set; `Origin: null` is a mismatch; a rejection logs the received and expected values at Warning.
`HubConnectionRegistry` records each connection's user; `IAccessChangeObserver` aborts the affected
connections on password change, logout, administrator reset, disable, enable, delete, and grant or team
changes, and all connections on a role change. `CloseOnAuthenticationExpiration` closes a socket whose
ticket expired.

### 8.7 Scoped lists

`ScopedSagaLister.ListAsync(filter, scope)`:

1. Unscoped: `reader.ListAsync(filter)`.
2. A blank `sagaType` is treated as not supplied. A supplied type in scope is one call; out of scope, an
   empty page.
3. Otherwise the type list is the caller's scoped names when there are at most `MaxMergedTypes` (50) of
   them, skipping `GetSagaTypesAsync`; otherwise `GetSagaTypesAsync` filtered to the scope, cached for a
   few seconds. One type is a single call with no bound.
4. Bounds: for the rank-served shape (the `UpdatedAt` sort with no status or kind filter), at most 50
   types and `page × pageSize ≤ 10 000`; for every other shape, at most 10 types and
   `page × pageSize ≤ 500` (one chunk, so no refill). Past a bound: 400 `{ error, maxPage }` naming the
   saga-type filter. ADR 0006 says that on Redis these other shapes read every member of every visible
   type.
5. Merge: each type's stream is primed with `pageSize` rows and refilled with doubling chunks up to
   500; the smallest head wins under the provider's comparer for that sort arm; ties across types break
   by saga type, ordinal ascending. `TotalCount` comes from the primes. A set of emitted
   `(sagaType, correlationId)` pairs skips repeats caused by updates between fetches.
6. Every result is filtered by scope (ordinal) before returning, so a provider quirk cannot widen it.
   `RedisSearchScanLimitExceededException` still maps to 400.

The SPA shows the server's error text for a 400 on the list and returns to the last good page.

### 8.8 First administrator

- Seed: when no users exist and both `Dashboard:Admin:Username` and `Dashboard:Admin:Password` are set
  and valid, create an Administrator with `MustChangePassword = false`. It never touches an existing
  database. When either key is set, setup is never available. A seed that cannot be applied leaves
  `setupRequired` true and `setupAvailable` false, marks the `identity` health check Degraded with the
  reason, and returns a problem code the SPA shows.
- `Dashboard:Admin:ResetOnStart=true` with both seed keys resets that user's password, re-enables the
  account, rotates the stamp and restores an unscoped Administrator grant at start, logged at Warning.
- Setup: when no users exist and no seed key is set, the API generates a one-time code at start and
  logs it once at Warning; `Dashboard:Setup:Code` presets it. `POST /api/auth/setup` requires it
  (fixed-time comparison, inside the setup limiter), re-checks inside an exclusive scope, commits, then
  signs in. There is no time window. 409 `setup_unavailable` otherwise.
- In a container (`DOTNET_RUNNING_IN_CONTAINER=true`) `Dashboard:Identity:Sqlite:Path` must be set; an
  unset path is an error reported by the `identity` health check, with no fallback under `/app`.
  Outside a container the default is `{LocalApplicationData}/vSaga/dashboard/identity.db`. The resolved
  path is logged at start.
- `IdentityStartup` never throws: create the directory, migrate, upsert built-in roles, seed, warm the
  key ring, with a 30 s timeout; on failure it retries at most every 10 s when called. While not ready,
  cookie principals get 401 without clearing the cookie, `/api/auth/*` answers 503
  `identity_unavailable`, and an API key mapped to a built-in role keeps working. The `identity` check
  reports Degraded so `/health` stays 200 and `order-processing`'s `service_healthy` gate opens; CI
  asserts it is healthy.

### 8.9 API key, administration and the wire contract

- `Dashboard:ApiKeyRole` (default Viewer) names a built-in or custom role. The API-key principal never
  satisfies `access.manage`: it is stripped from its access, `/api/admin/*` answers 403, and a Warning
  is logged at start when the configured role contains it. The username `api-key` is reserved (any case).
  A key shorter than 24 characters logs a Warning. Compose sets `Operator` from C37 until the SPA
  switch-over (C46), then `Viewer`, so the demo's retry button keeps working in between.
- Endpoints: `GET /api/auth/session`, `POST /api/auth/{login,logout,setup,password}`;
  `GET /api/admin/permissions`; CRUD on `/api/admin/{users,teams,roles}`, plus
  `POST /api/admin/users/{id}/password` and `/unlock`.
- The API records are canonical: `isEnabled`, `isBuiltIn`, `lastSignInAtUtc`, arrays always present
  and never null. `SessionResponse` carries `passwordMinLength`. Team membership is written only through
  the team payload; the user page shows teams read-only. Request records under `/api/auth` and
  `/api/admin` use `JsonUnmappedMemberHandling.Disallow`, so an unknown member is a 400, not a silent
  no-op. Golden JSON fixtures, one per request and response type, are checked in and asserted from both
  the .NET endpoint tests and `admin-api.service.spec.ts`. Validation errors are keyed by camelCase
  request paths (`grants[0].sagaTypes`). The SPA's effective-access preview applies `implies` and
  `scopable` from `GET /api/admin/permissions`, and the grants editor allows one grant per role.
- Problem codes: `unauthenticated`, `forbidden`, `password_change_required`, `antiforgery`,
  `invalid_credentials`, `validation`, `setup_unavailable`, `username_taken`, `name_taken`,
  `role_in_use`, `role_immutable`, `last_administrator`, `rate_limited`, `identity_unavailable`.
- Audit: the auth endpoints and `AccessAdministrationService` emit structured log events with stable
  EventIds (actor, target, action, outcome, client address), never passwords or hashes, and a submitted
  username only when it passes the username rule. `docs/dashboard.md` names the log category and gains a
  "Deploying beyond localhost" checklist.
- Settings are read once into a plain singleton and validated at composition; framework-owned options
  (cookie, antiforgery, forwarded headers, key management) are the documented exception.

### 8.10 Configuration added

| Key | Default |
| --- | --- |
| `Dashboard:ApiKey` (unchanged) | empty: fails closed |
| `Dashboard:ApiKeyRole` | `Viewer` |
| `Dashboard:Identity:Provider` | `Sqlite` (anything else throws) |
| `Dashboard:Identity:Sqlite:Path` | outside a container `{LocalApplicationData}/vSaga/dashboard/identity.db`; required in a container; image and compose `/var/lib/vsaga-dashboard/identity.db` |
| `Dashboard:Admin:Username`, `Dashboard:Admin:Password` | unset; compose `admin` / `dev-local-only-change-me` |
| `Dashboard:Admin:ResetOnStart` | `false` |
| `Dashboard:Setup:Code` | unset: generated when no users exist |
| `Dashboard:Session:CookieName` | `vsaga.session` |
| `Dashboard:Session:IdleTimeoutMinutes` | `480` |
| `Dashboard:Session:AbsoluteTimeoutHours` | `24` |
| `Dashboard:Session:RequireHttps` | `false` |
| `Dashboard:Password:MinLength` | `12` |
| `Dashboard:Lockout:MaxFailedAttempts`, `Dashboard:Lockout:Minutes` | `5` (0 disables), `15` |
| `Dashboard:RateLimit:AuthPerMinute` | `20` |

With §4.8 (`WebOrigin`, `TrustedProxies`) and §6.2 (`StateSnapshots:MaxBytes`), `docs/configuration.md`
lists every `Dashboard:*` key in one table.

### 8.11 The SPA side

- `AuthService` (signals) loads `GET /api/auth/session` in an app initializer (never rejects, 8 s
  timeout); a failed refresh keeps the last known session, so an API restart never signs the UI out; a
  different user id after a refresh reloads the page. `can(permission, sagaType)` and `canAny`;
  `PermissionKey` is the one permission type.
- Guards `authGuard`, `anonymousGuard`, `setupGuard`, `adminGuard` (`canMatch`, so the admin chunk
  loads only for managers), and `safeReturnUrl`. Lazy routes `/login`, `/setup` (with the setup code
  field), `/account`, `/admin/{users,teams,roles}` with `new` and `:id` pages. A failed lazy import
  reloads the page at most once a minute.
- One `authInterceptor` for `/api/` URLs: 401 outside the auth endpoints signs out locally and goes to
  `/login?returnUrl=`; 403 refreshes access; 400 `antiforgery` refetches the session and retries once
  with the new token. `apiKeyInterceptor` and `DASHBOARD_API_KEY` are deleted. A 503 on the session
  shows "sign-in is unavailable".
- `SagaHubService`: no token factory; `stopAndReset()`, `resume()` and a session probe, so a failed
  negotiate stops reconnecting when the session is gone; guarded invokes.
- Top bar: brand, primary nav (Administration only with `access.manage`), and `.topbar-end` holding the
  guide toggle and the user menu. Retry and data render by permission; a 403 detail shows a no-access
  state. Administration: users, teams, roles, a grants editor with "exact saga type name" entry, an
  effective-access preview, inline confirms, and the `last_administrator` banner that keeps the draft.
- Shared styles are promoted to `src/styles.scss` once: `.btn` with `--quiet`/`--danger`, `.banner`
  modifiers, form, table, chip and card classes.

---

## 9. Guidance and the user guide

### 9.1 Guide mode

- `GuideService` (eager): enabled flag, current area, request, running tour, `canReplay`, a one-time
  hint. State lives under one browser storage key, `vsaga.guide`
  (`{"v":1,"enabled":true,"seen":{"list":1},"hintDismissed":true}`), per origin and therefore per
  compose stack, not per user; corrupt JSON or an unknown `v` reads as defaults; without storage, state
  stays in memory. Injected through `GUIDE_STORAGE` and `GUIDE_PERMISSION_CHECK` tokens; the first guide
  commit provides the permission check from `AuthService.can`.
- Areas, each a short tour with a version: list, detail summary, map, timeline, data, retry,
  administration. While Guide is on, an area explains itself the first time the user opens it: the list
  and administration pages on navigation, the map and timeline when their tab shows, data when a step
  inspector or the Saga data bar opens, retry when the retry row shows on a `Failed`/`TimedOut` saga.
  `seen[area] = version`; bumping a version shows a changed tour once more. Replay repeats the current
  area. Switching Guide on starts the current page's area; off by default, with a non-modal hint once.
- `GuideToggle` in `.topbar-end` (`data-tour="topbar-guide"`), a `<button aria-pressed>` "Guide",
  "Replay tour", and a "User guide" link.
- `GuideOverlay`, loaded with `@defer (when guide.enabled())`: waits for the area's ready anchor (250 ms
  polls, 20 tries); drops steps whose permission fails or whose anchor, reveal control and fallback are
  all missing at begin, so "Step n of m" is true; a centred popover is used only for an anchor that
  disappears mid-tour. Pure geometry in `guide-geometry.ts` (`spotlightBox`, `placePopover`); a
  `requestAnimationFrame` loop tracks the anchor. Modal while active: siblings get `inert`, a full-screen
  layer cancels `mousedown`, Escape ends, arrows move, Tab wraps; focus returns to where it was. No
  animation under `prefers-reduced-motion`.
- One `data-tour` vocabulary, defined in `GUIDE_ANCHORS` and added only by the guidance commits:
  `topbar-guide`; `list-filters`, `list-table`, `list-sort`, `list-row`, `list-pagination`;
  `detail-summary`, `detail-data`, `detail-retry`, `detail-tab-map`, `detail-tab-timeline`;
  `map-canvas`, `map-controls`; `timeline`, `timeline-entry`, `timeline-step-data`; `admin-nav`,
  `admin-nav-users`, `admin-nav-teams`, `admin-nav-roles`, `admin-list`. The list's sort headings
  become buttons and rows open on Enter and Space in the list commit.
- Tour copy is written against the shipped labels ("Recorded at", "At start", "At end", "Compare",
  "Re-run step N") and the retry semantics of §7: the retry area says a retry re-runs the step that
  failed, for this saga only, and that other services consuming that message still receive it.

### 9.2 The user guide and the rule

`docs/dashboard-guide.md`, text and tables only, with fixed H2 headings the app links to: Opening the
dashboard; Signing in (the seeded administrator, the setup code, lockout, forced password change, idle
and absolute timeouts, sign out); Guide mode; The saga list; The saga detail page (Summary, Map,
Timeline, Saga data, Retrying a saga: the failed-step marker, what is re-run, side effects, the
202/409/422/502 outcomes in user terms, attribution); Administration (users, teams, built-in roles
against the four permissions, grants with a worked scope example, the last-administrator rule); Your
account; Troubleshooting (two stacks in one browser, lockout escape, the minimum engine version for a
targeted retry). The in-app link uses `USER_GUIDE_URL`, the guide on GitHub's `main` branch.

The rule, written in `CONTRIBUTING.md`, in a header note of `docs/dashboard-guide.md` and in a comment
at the top of the tour step definitions: a change to the dashboard UI updates the guide and the tour in
the same change. Each page spec has an anchor-contract case, and `guide-tours.spec.ts` checks that every
anchor, fallback and reveal is in `GUIDE_ANCHORS`, so a removed anchor fails a test.

### 9.3 Documentation set

`docs/dashboard.md` keeps the headings other files link to (`## Authentication`, `### Manual retry`,
`## Saga Map`, `## The SPA`). The compose service, UI ports and dev server go under `## The SPA`. It
gains Access control and State snapshots, and Manual retry is rewritten; `README.md`, `docs/configuration.md`, `docs/observability.md`, `docs/persistence.md`,
`docs/transports/index.md`, `docs/concepts.md`, `docs/testing.md`, `docs/README.md`, `CONTRIBUTING.md`
and `dashboard-web/README.md` are updated in one documentation commit per slice. Older records get dated
bracketed notes only. Four new history files, each written only from a live run:
`dashboard-ui-in-compose.md`, `timeline-labels-map-jump-and-state-snapshots.md`,
`dashboard-sign-in-and-access.md`, `dashboard-guide-mode-and-user-guide.md`. No existing history file is
edited.

---

## 10. Failure modes

| What fails | What the user sees | What limits the damage |
| --- | --- | --- |
| Identity database unusable (bad path, read-only volume, corrupt file, killed migration lock) | Login page says sign-in is unavailable (503 `identity_unavailable`) | Start-up never throws; `identity` Degraded naming the path; retries every 10 s; API key with a built-in role still works; `order-processing` still starts |
| Identity path unset in a container | Same | No fallback under `/app`; the health check names the missing key |
| Seed password rejected | No setup screen, a problem code | Setup stays closed when seed keys are set; health Degraded with the reason |
| Only administrator's password lost | Cannot sign in | `Dashboard:Admin:ResetOnStart=true` with the seed keys |
| Lockout used against a known username | "Sign-in failed" for 15 minutes | Lockout is temporary; limiter keyed on address and username; restart with `MaxFailedAttempts=0`; device cookie is a follow-up |
| Proxy hides client addresses | Limiter shares one bucket per username | Key includes the username; Warning when forwarded headers arrive from an untrusted peer |
| TLS in front without a forwarded scheme | Hub never connects; cookies not Secure | `RequireHttps` validates forwarded-header trust at start; the origin guard logs both values |
| Two stacks in one browser | First unsafe request on the other stack fails once | Per-project session cookie; antiforgery 400 refetches the session and retries once |
| Session expired or revoked | Redirect to `/login?returnUrl=...` | Hub stops on a dead session; absolute lifetime; stamp check per request |
| Access changed while connected | Live updates pause, then resume under new access | Connections aborted; the client reconnects and resubscribes |
| API down or being recreated | "Reconnecting...", then recovery; no sign-out | nginx re-resolves within 10 s; keys in the store keep sessions |
| Web image rebuilt under an open tab | One page reload | Missing chunks are 404; reload at most once a minute |
| A page that works under `ng serve` violates the CSP | Broken page in the container only | CI greps `index.html`; live browser pass with the console open |
| Snapshot append fails or exceeds `StateSnapshotTimeout` | Step data "missing" | Warning logged; transition, drain and ack proceed; EF entity detached |
| Process dies between commit and snapshot, or a step loses its persist race | Step data "missing" | Best effort by design; At end reads the live blob |
| Poller pushes before the snapshot lands | "pending" for up to five seconds | One follow-up fetch at 1500 ms |
| State larger than the cap, or saga past its budget | "too large" or "budget used up" note | Markers; failure snapshots still recorded in full |
| Redis memory pressure from snapshots | Persists refused, messages dead-letter | Per-saga budget; measured figures in `docs/persistence.md`; `RecordStateSnapshots=false` |
| Dashboard API older than the engine | Entry type `21`, unredacted snapshots | Deploy the dashboard first; compose builds both together |
| Failed saga with no replayable step | Retry disabled with the reason; 422 | Planner explains in plain words |
| Saga changed during retry | 409 "reload and try again" | Version-checked reset |
| Republish fails, whether the transport wraps the failure or not (a paused RabbitMQ broker throws `TaskCanceledException` from opening a channel) | 502 saying whether the saga was restored | Best-effort, version-checked restore on any exception, and its snapshot |
| Client disconnects between the reset and the publish | Nothing (the response is lost); the saga is redriven or restored | The work after the reset ignores the request's token |
| A 409 or 502 retry | A retry step with nothing after it | Visible in the timeline; the saga state says what happened |
| Host runs an engine older than C23 | Other saga types process the replay | Minimum engine version documented in ADRs 0006 and 0008 and `docs/dashboard.md` |
| Participants consume the replayed message type | They act on it again | Stated in the confirmation and the guide |
| Business failure decided by the message alone | Saga fails again after retry | Stated in §7.5 and the guide; the new step is visible |
| A tour anchor removed | Tour step skipped at begin | Anchor-contract specs fail in CI |
| Browser storage blocked | Guide state forgotten on reload | In-memory fallback |
| Scoped list past its bound | 400 with the error text, previous page kept | `maxPage` in the body; ask for a saga-type filter |

---

## 11. Tests, mutation checks and live verification

**Every commit:** `dotnet build dotnet/VSaga.slnx` with zero warnings, `dotnet test dotnet/VSaga.slnx`
(Docker for the Testcontainers suites), and in `dashboard-web`: `npm audit --audit-level=low`,
`npx ng build` with no budget warning, `npx ng test --watch=false`. At the end of each slice the SPA gates
also run under Node 22, which CI uses. A mutation check breaks one thing with a one-token edit,
rebuilds, runs the named tests, confirms that exactly those fail, and restores.

| Slice | New tests | Mutation checks | Live verification |
| --- | --- | --- | --- |
| Packaging (C02–C09) | `api-config.spec.ts` (relative URLs); `DashboardEdgeTests`: settings parsing, CORS off by default, configured origin only, forwarded headers from trusted, untrusted and multi-hop peers | register CORS unconditionally; drop `ForwardLimit = 1`; accept a malformed proxy entry | `up -d --build` on base, one transport overlay and mongo: five services healthy; `curl -sI` shows the headers and caching; an encoded-slash route answers the same on 4200 and 5080; WebSocket 101 in the browser with a clean CSP console; recreate `dashboard-api` and watch the UI recover in about 10 s; 4700 shows MongoDB while 4200 shows Postgres; `ng serve` on 4201 beside it; `id` is 1654 in the API and 101 in the web container; `down` returns promptly; logs carry no query strings; ports bound to 127.0.0.1 |
| Timeline, map, snapshots, retry (C10–C30) | util specs; `saga-transitions.spec.ts` per fold rule (age-based pending, ids before adjacency, `.CallHttp` hops, retry steps, dead-letter step); component specs; detail-page URL, refresh and error cases; `SagaMapBuilderTests`; EF detach tests; `SagaStateSnapshot`, conformance round-trip and the `SagaEntryType` value pin; snapshot ordering, blob equality, golden text, containment, budget, deadline; redaction; notifier strip; reset recorder including the marker cap; §7.7 | snapshot before the persist; snapshot after the drain; no try/catch; no deadline; no budget check; no EF detach; no map filter; no redaction; no notifier strip; reset cap ignores the marker; no target-header check; header not set by the endpoint; fixed precedence instead of recency; conditional reset; no restore | base, chaos, mongo and redis stacks: a Completed `OrderSaga` has one `StatePersisted` per handled message, the last equal to the detail's `dataJson`, none in `/map`; `Orchestrator__MaxStateSnapshotBytes=64` gives markers, `RecordStateSnapshots=false` gives none; measured Redis `MEMORY USAGE` and Mongo `collStats` recorded; retry a technical failure, a business-failed `OrderSaga` and a timed-out `InvoiceFollowUpSaga`: `ManualRetryRequested`, reset snapshot, then `MessageReceived` with a fresh id and payload in the retried saga only, no new entry in `PostShipmentChoreography` and no second `InvoiceDeliverySaga`; browser pass on labelled times, step grouping, jump to map and back, data inspector, At start/At end/Compare, the failed-step marker and the confirmation text |
| Authentication (C31–C52) | identity store contract and migration tests (no pending model changes); services; `AuthEndpointsTests`; `SetupAndSeedingTests` (setup needs the code, 409 with seed keys, weak seed keeps setup closed, `ResetOnStart`); admin endpoint tests with golden fixtures; `PUT /api/admin/users/{id}` with `isEnabled: false` makes that user's next request answer 401 and aborts their hub connection; every `/api` response, including 401, 403 and 404, carries `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`; `SagaAccessEnforcementTests` (scope 403s, redaction of payloads and `errorMessage` on all three paths, API key never `access.manage`, attribution); `ScopedSagaListerTests` (oracle per sort arm, bounds, a single-space grant); `SagaHubAccessTests`; `HubOriginGuardTests` (no Origin, same origin, another localhost port, configured origin, https through a trusted forwarded header); `EndpointProtectionTests` (every unsafe endpoint antiforgery-enforced or the hub; upper-case paths); the documentation pointer in 401 and 403 bodies; absolute lifetime 401; abort after password change; SPA auth, guard, interceptor (antiforgery retry), hub and admin specs | remove the retry policy; remove the origin guard; remove the security-stamp comparison; remove payload and `errorMessage` redaction; remove `JsonUnmappedMemberHandling.Disallow`; let the API key keep `access.manage`; drop the scope filter in the lister; skip the setup-code check; skip the abort on password change | the seeded admin signs in on 4200; session survives `up -d --force-recreate dashboard-api`; a Viewer cannot retry; a scoped user sees and receives pushes only for their types; disabling a user drops the socket; the rendered identity path lies under the mounted `vsaga-dashboard-identity` volume; curl flows for login, antiforgery and the origin guard; two stacks side by side in one browser profile (4200 and 4300, after rendering `-p vsaga-wolverine ... config` to confirm the cookie name); `down`/`up` keeps users; `down -v` re-seeds |
| Guidance (C53–C58) | `guide-geometry`, `guide.service`, `guide-tours`, `guide-overlay`, `guide-toggle` specs; anchor-contract cases per page | rename `data-tour="list-table"`; delete the `requires` filter; delete the `inert` toggle | in the container: the hint once; each area explains itself once; Escape returns focus; reload does not restart; keyboard only; reduced motion; clean CSP console; a Viewer gets no retry area; the admin tour |

---

## 12. Commit sequence and progress

One local commit per logical change on `dashboard-usability-and-access`; nothing pushed. LIVE marks a
commit verified against `docker compose up -d --build` before the next slice starts. Brackets name the
source workstream: P packaging, D detail UX, E engine snapshots, R retry, B auth backend, F auth
frontend, G guidance and docs; "feasibility 9" is that review's finding on message-id stamping.

| # | Commit | Live |
| --- | --- | --- |
| C01 | [G1] Design document, ADRs 0006, 0007 and 0008, and the working plan | |
| C02 | [P1] Same-origin SPA: relative URLs, dev proxy, dev server on 4201; correct the port lines it invalidates | |
| C03 | [P2] Opt-in CORS through `DashboardEdge` | |
| C04 | [P3] `dashboard-web` image and the `index.html` guard in CI | |
| C05 | [P4] UI in compose with six overlay ports, loopback bindings | LIVE |
| C06 | [P5] Forwarded headers from trusted proxies | LIVE |
| C07 | [P7] Compose build and smoke test in CI | LIVE |
| C08 | [P8a, G2] Document the one-command demo | |
| C09 | [G9] History: `dashboard-ui-in-compose.md` | |
| C10 | [D1] Pure helpers | |
| C11 | [D2] Model, fold and fixtures; exclude `src/app/testing` from the app build | |
| C12 | [D3] Labelled times and steps | LIVE (browser) |
| C13 | [D4] Jump from a timeline entry to the map | LIVE (browser) |
| C14 | [E1] `SagaMapBuilder` unit tests | |
| C15 | [E2] Detach a failed event-log append from EF's change tracker | |
| C16 | [E3] `StatePersisted` and `SagaStateSnapshot`, with conformance cases | |
| C17 | [E4] Skip `StatePersisted` in the map | |
| C18 | [E5] Redaction seam; payload-free pushes | |
| C19 | [E6] Stamp the stored sequence number on pushed entries | |
| C20 | [feasibility 9] Stamp inbound message ids on `TimeoutScheduled`/`SagaCompleted`; log-sink causation id | |
| C21 | [E7] Record snapshots, with the per-saga budget, the deadline, the sample binding and the persistence samples | LIVE (base, chaos, mongo, redis; mutation checks) |
| C22 | [E8] Record the state a dashboard retry reset leaves | LIVE (folded into C24, whose retry exercises the reset snapshot) |
| C23 | [R1] Target-saga-type header and `MessageReceived` payload in the engine | |
| C24 | [R2] Retry planner, retry-plan endpoint, targeted retry from the failed step | LIVE |
| C25 | [D5] Data inspector and per-step toggle | |
| C26 | [D6] Data overview; Data tab removed | |
| C27 | [D7] Coalesced live refresh and load errors | LIVE (browser) |
| C28 | [R3] SPA failed-step marker, map focus on failure, retry confirmation from the plan | LIVE (browser) |
| C29 | [E9, G3] Document snapshots, the labelled timeline, the map jump and the targeted retry | |
| C30 | [G9] History: `timeline-labels-map-jump-and-state-snapshots.md`, with measured storage | |
| C31 | [B1] Identity project: model, store contract, EF store | |
| C32 | [B2] Identity services | |
| C33 | [B3] Migrations project, registration, start-up, health check, Dockerfile COPY lines, test factories | |
| C34 | [B4a] Policy scheme, fallback policy, shared 401 and 403 bodies, API-key role mapping; `no-store`/`nosniff` on `/api` | |
| C35 | [B4b] Sign-in: session, login, logout, password; antiforgery; rate limits and lockout | |
| C36 | [B5] First administrator: seed, setup code, `ResetOnStart` | |
| C37 | [P6] Identity volume, non-root API image, seeded demo administrator, per-project cookie name; `ApiKeyRole` Operator for now | LIVE |
| C38 | [B6] Permission checks, redaction, filtered lists, scoped merge, retry attribution | |
| C39 | [B7] Administration endpoints | |
| C40 | [B8] Hub access checks, per-type list groups, origin guard, abort on access change | LIVE (curl) |
| C41 | Smoke-test sign-in, the origin guard and identity health in CI | LIVE |
| C42 | [F1] Promote the remaining shared styles | |
| C43 | [F2] Hub `stopAndReset`, `resume` and session probe | |
| C44 | [F3] Session models, `problemOf`, `AuthService`, auth mock | |
| C45 | [F4] Login, setup and account pages as lazy routes | |
| C46 | [F5] Require a session; remove the key from the SPA; `ApiKeyRole` back to Viewer; README sign-in rows | LIVE |
| C47 | [F6, D8] Gate retry and data by permission; 403 states | LIVE |
| C48 | [F7] Administration shell and roles | |
| C49 | [F8] Users, grants editor, effective access | |
| C50 | [F9] Teams | LIVE (two stacks in one browser; `down`/`up`; `down -v`) |
| C51 | [G4, B10] Document sign-in, access control and the identity store | |
| C52 | [G9] History: `dashboard-sign-in-and-access.md` | |
| C53 | [G5] Guide mode: toggle, list area, permission-check provider | |
| C54 | [G6] Detail areas: summary, map, timeline, data, retry | |
| C55 | [G7] Administration area | LIVE (container: keyboard only, reduced motion, CSP console) |
| C56 | [G8] User guide, linked from the app, the README and the docs index; the CONTRIBUTING rule | |
| C57 | [G9] History: `dashboard-guide-mode-and-user-guide.md` | |
| C58 | [G10] Mark this design and ADRs 0006, 0007 and 0008 implemented; delete the working plan | |

Conditions that keep the sequence green: C11 and C12 land before C21, so the SPA never lists
`StatePersisted` as a row; C17 and C18 land before C21, so snapshots never reach the map or a push with
their payload; C15 lands before C21; C33 carries the csproj `COPY` lines, or C07's image build fails;
C34 registers the cookie scheme with its problem-writing events, so credential-less 401 tests keep
passing; C37 lands the volume, the chown and `USER` together; C46 updates the hub and app specs that
assume the key.

**Progress:** nothing has landed yet beyond `22f04bf` ("Override piscina to 5.3.2 and patch
brace-expansion for npm audit"), which made `npm audit --audit-level=low` pass on `main` again, a gate
every commit above must clear.

---

## 13. Open questions

Most questions the blueprints raised were answered by the second round of decisions:

| Question | Answer |
| --- | --- |
| Bind the demo's dashboard ports to all interfaces or to loopback? | Loopback (§4.5). |
| Ship a seeded administrator or start on the setup screen? | Seeded in compose; setup elsewhere, with a one-time code and no window (§8.8). |
| Recovery when the only administrator's password is lost? | `Dashboard:Admin:ResetOnStart` (§8.8). |
| Per-stack cookie names, or one signed-in stack per browser? | `Dashboard:Session:CookieName` per compose project; `XSRF-TOKEN` shared and healed by one retry (§8.3). |
| Screenshots in the user guide? | No; text and tables (§9.2). |
| How is a retry contained to the saga type it was authorised for? | A targeted redrive (§7.4), not a check of every saga type tracking the correlation id. |
| Erase or retention path for snapshots? | Deferred (§14). |

Still open:

- **Where the in-app "User guide" link points.** It opens `docs/dashboard-guide.md` on GitHub's `main`
  branch. A fork or an installation without GitHub access gets upstream content or nothing. Serving the
  guide from the dashboard itself would need a Markdown renderer or a pre-rendered page in the image.
  The link stays one constant (`USER_GUIDE_URL`) so the choice can change later without touching the
  tours.

---

## 14. Explicitly deferred

- **Retention or erasure of recorded snapshots.** Nothing in vSaga removes event-log entries, and saga
  state may hold personal data. An erase path needs a persistence-contract change across all four
  providers.
- **A multi-type filter on `SagaListFilter`.** It would make scoped lists efficient on Redis, which can
  filter the index member suffix (it already carries the saga type) with no per-type intersection.
- **A `(SagaType, UpdatedAtUtc)` Postgres index** for per-type pages in the scoped merge, following
  `20260924142250_AddSagaInstanceUpdatedAtUtcIndex`.
- **A payload-free timeline read** for the engine's visited-states lookup and the map. It is the real fix
  for the snapshot read cost; the trigger is a saga type whose steps × state size passes about 1 MB.
- **The device-cookie lockout bypass** (a Data-Protected, HttpOnly "known device" cookie that lets a
  browser that has signed in before skip a shared lock). Until then lockout can deny a known username;
  the escape is documented.
- **Expiry of temporary passwords** (an administrator-set password that must be changed stays valid until
  used).
- **Single sign-on.**
- **`RabbitMqTransport` wrapping channel-open failures in `MessageTransportPublishException`.** A broker
  that stops answering makes `CreateChannelAsync` throw `TaskCanceledException` after about 25 seconds,
  outside the adapter's wrapping. The retry endpoint restores on any exception (§7.3), so the dashboard
  no longer depends on it, but the engine's own publish paths and the 502 detail would name the cause
  better with it.
