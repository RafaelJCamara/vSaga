# vSaga dashboard (Angular SPA)

The web UI for the vSaga ops dashboard: sign-in, a saga list with filtering and live updates, a per-instance
detail page with two tabs, a service map and a timeline of numbered steps, a Saga data bar (the data at
start, at end, and a comparison) and each step's data, a retry that re-runs the step a failed saga
failed in (see [`docs/dashboard.md`](../docs/dashboard.md#the-saga-detail-page)), and, for users who may
manage access, an administration area for users, teams and roles. A **Guide** switch in the top bar turns on
[guide mode](#guide-mode), a short tour of each area the first time it is shown; the user-facing description of
every page is the [dashboard user guide](../docs/dashboard-guide.md). The app is
a thin client over the Dashboard API — every screen here is backed by an endpoint documented in
[`docs/dashboard.md`](../docs/dashboard.md) — and decides nothing about access itself: it shows what the API
would allow and hides what it would refuse.

## Prerequisites

To use the dashboard you need only Docker: `docker compose up -d --build` from the repository root
builds this app into the `dashboard-web` image and serves it on http://localhost:4200, next to the API
it reads (see ["Run the demo"](../README.md#run-the-demo)).

Node.js 22 (22.22.3 or later), 24 (24.15.0 or later) or 26+ (this package's `engines` field, Angular
22's own range; CI builds with the latest 22) is needed only to develop it: the dev server, the unit tests, and builds outside
Docker. The dev server has no data of its own, so an API has to be running first, by default the
compose stack's:

```bash
docker compose up -d --build      # from the repository root: Postgres, RabbitMQ, dashboard API and UI, sample
```

Every page needs a signed-in user, so sign in as the stack's administrator: the compose stack seeds
`admin` / `dev-local-only-change-me` into its empty identity volume (see
[The first administrator](../docs/dashboard.md#the-first-administrator)). Browsers scope cookies by host, not
port, and each compose project names its session cookie differently (`vsaga.session.<project>`), so the dev
server on http://localhost:4201 shares its sign-in with the compose UI of the stack whose API it proxies to,
and needs its own sign-in against another stack's.

## Run it

```bash
npm install
npx ng serve                      # http://localhost:4201
```

Both run from `dashboard-web/`, which has its own lockfile and Angular CLI toolchain. CI installs it
with `npm ci`, audits it, then builds and tests it as its own job (`angular` in
`.github/workflows/ci.yml`); the `npm audit --audit-level=low` step fails that job on any known
advisory rated `low` through `critical`, dev dependencies included.

The dev server listens on http://localhost:4201, so it runs beside the compose UI on 4200, and proxies
`/api` and `/hubs` to the compose stack's API on http://localhost:5080 (see
[How it reaches the API](#how-it-reaches-the-api)). To browse another stack, set `VSAGA_API_URL` before
starting it, here the MongoDB overlay's API. In Git Bash or WSL:

```bash
VSAGA_API_URL=http://localhost:5580 npx ng serve
```

In PowerShell:

```powershell
$env:VSAGA_API_URL = 'http://localhost:5580'; npx ng serve
```

The PowerShell variable stays set for the rest of that session; `Remove-Item Env:VSAGA_API_URL`
clears it.

The dev server is for iterating. It has none of the container's Content Security Policy, caching or
routing rules (see [The container image](#the-container-image)), so a page that works on 4201 can still
break on 4200. Before calling a UI change done, rebuild the container from the repository root and
check the page there with the browser's devtools console open:

```bash
docker compose up -d --build dashboard-web     # then http://localhost:4200
```

## Commands

| Command | What it does |
| --- | --- |
| `npx ng serve` | Dev server with hot reload on http://localhost:4201, proxying the API (`VSAGA_API_URL`, default http://localhost:5080) |
| `npx ng build` | Production bundle into `dist/` |
| `npx ng test` | Unit tests (vitest), interactive watch mode |
| `npx ng test --watch=false` | Same, single run — what CI actually runs |
| `npm audit --audit-level=low` | Checks the lockfile against known advisories; fails on any rated `low` or above, dev dependencies included — what CI runs |
| `docker compose build dashboard-web` | From the repository root: builds the container image (`npm ci` and `ng build` inside it, then nginx) |
| `docker compose run --rm --no-deps dashboard-web nginx -t` | From the repository root: renders the nginx template and checks the configuration, as CI does |

## How it reaches the API

The app calls the API on its own origin. [`src/app/api-config.ts`](src/app/api-config.ts) holds no
host or port:

```ts
export const API_BASE_URL = '';
export const HUB_URL = `${API_BASE_URL}/hubs/saga`;   // '/hubs/saga'
```

So every request goes to whatever served the page, which forwards `/api` and `/hubs` to the API:

- **In the container** (compose, http://localhost:4200 and each overlay's UI port), nginx proxies both
  paths to `DASHBOARD_API_UPSTREAM`, by default `dashboard-api:8080`: the compose service name, which
  each compose project resolves to its own API.
- **Under `ng serve`** the dev server does it, configured by [`proxy.conf.mjs`](proxy.conf.mjs): it
  sends both paths to `VSAGA_API_URL` (default http://localhost:5080, the compose stack's API; an
  overlay's API port, 5180 to 5680, or http://localhost:5275 for an API started with `dotnet run`).

Both pass WebSocket upgrades through for the SignalR hub and leave the `Host` header as the browser
sent it, so the API sees `localhost:4200` or `localhost:4201`. Because the browser only ever talks to
its own origin, the API's CORS setting (`Dashboard__WebOrigin`) plays no part, and no port in this app
has to match any server setting. A `dotnet run` API has no user yet: it logs a one-time setup code at start,
which the app's `/setup` page asks for, or start it with `Dashboard__Admin__Username` and
`Dashboard__Admin__Password` set to be seeded (see
[The first administrator](../docs/dashboard.md#the-first-administrator)).

**There is no API key in this app.** `DASHBOARD_API_KEY` and `api-key.interceptor.ts` are gone: the browser
authenticates with the HttpOnly session cookie the API sets at sign-in, which no script can read, and the
SignalR connection is built without an access token (the same cookie authenticates its negotiate and its
WebSocket). `Dashboard:ApiKey` is a credential for scripts and probes only — see
[API key](../docs/dashboard.md#api-key). A change to the server's key therefore touches nothing here, and a
search of the served bundle finds neither the demo's key nor `X-Api-Key`.

## Signing in: the auth service, guards and interceptor

Sign-in is one service, four guards and one interceptor, wired in
[`src/app/app.config.ts`](src/app/app.config.ts) and [`src/app/app.routes.ts`](src/app/app.routes.ts). What
the API does on its side is in [`docs/dashboard.md`](../docs/dashboard.md#authentication).

- **`AuthService`** ([`services/auth.service.ts`](src/app/services/auth.service.ts)) holds the session as
  signals (`status`, `isAuthenticated`, `user`, `access`, `setupRequired`, `setupAvailable`, `setupProblem`,
  `passwordMinLength`, `canManageAccess`, `signInUnavailable`) and the flows as promises (`login`, `logout`,
  `setup`, `changePassword`, `refresh`); the HTTP services elsewhere stay Observable. `can(permission,
  sagaType?)` and `canAny(permission)` answer from the access the server computed, and `PermissionKey` is the
  one permission type. Its rules: a failed read of the session never signs anyone out (it keeps the last known
  session, and only an app with none yet is marked `unreachable`), so an API restart does not end the UI's
  session; after every sign-in, sign-out, setup and password change the session is read again, which also
  re-issues the `XSRF-TOKEN` cookie, because the API binds antiforgery tokens to the identity; a different user
  id after a read (another tab signed in as someone else) reloads the page; and the dependency on the hub is
  one way: the service stops, resumes and probes it, the hub knows nothing of the service.
- **The app initializer** (`provideAppInitializer(() => inject(AuthService).bootstrap())`) reads
  `GET /api/auth/session` before the first navigation, because the first page is chosen from the session. It
  never rejects, and a session request that does not answer within 8 s counts as failed, so the app starts
  anyway. Until then `index.html` shows a "Loading the dashboard…" placeholder, written as plain markup because
  the container's Content Security Policy allows no inline script.
- **Guards** ([`guards/auth.guards.ts`](src/app/guards/auth.guards.ts)): `authGuard` sends a visitor with no
  session to `/login?returnUrl=…`, to `/setup` while no user exists, and a user who must change their password
  to `/account`; `anonymousGuard` guards `/login` (a signed-in visitor goes on to the page the sign-in was
  for); `setupGuard` guards `/setup`; `adminGuard` guards `/admin` as a **`canMatch`** guard, so the admin chunk
  is never requested for a user without `access.manage` for every saga type. `safeReturnUrl` accepts only an
  absolute path of the app as a return address: not `//host`, not a full URL, not `/login` or `/setup`. A
  guard runs before its route's chunk is requested, so a visitor who is turned away downloads nothing.
- **`authInterceptor`** ([`interceptors/auth.interceptor.ts`](src/app/interceptors/auth.interceptor.ts))
  adds no credential. Angular's built-in XSRF interceptor, which runs before it, copies the `XSRF-TOKEN` cookie
  into `X-XSRF-TOKEN` on same-origin unsafe requests. The interceptor reacts to what the API answers, for
  `/api/` URLs on the page's own origin only: a **401** outside the sign-in endpoints means the session ended
  behind the app's back, so it signs out locally, stops the hub and goes to `/login` (`reason=expired`); a
  **403** reads the session again (at most every 5 s), since access may have changed; a **400** with the code
  `antiforgery` reads the session again, which re-issues the cookie, and sends the request **once** more with
  the new token. A failure always reaches the caller unchanged. This is also what two stacks in one browser
  rely on, since they share the one `XSRF-TOKEN` cookie: with the base stack and the Wolverine overlay in two
  tabs, each of ten alternating writes was refused once with `400` `antiforgery`, then succeeded on this retry,
  with nothing shown to the user.
- **The identity-epoch contract.** `AuthService` counts the moments the identity behind the cookie may have
  changed (`identityEpoch`: a local sign-out, and every sign-in, sign-out, setup and password-change request
  settling, whether it succeeded or not). A 401 to a request that was sent before the latest change says
  nothing about the session there is now, so the interceptor reads `currentIdentityEpoch()` **when it sends**
  the request (and again for the antiforgery retry) and passes it to `handleUnauthorized(epoch)`, which
  ignores a stale one; `noteForbidden` takes none. A session read that was sent before a change and answers
  after it is dropped for the same reason. A change to either side of this contract needs the other changed
  with it.
- **A tab left open across a rebuild** asks for lazy chunks whose hashed names no longer exist: the router's
  navigation error handler (`stale-chunk-reload.ts`) reloads the page once, and never more than once a minute.

## Routes, lazy chunks and the bundle budget

| Route | Chunk | Guard |
| --- | --- | --- |
| `/sagas` | initial | `authGuard` |
| `/sagas/:sagaType/:id` | lazy (`saga-detail`) | `authGuard` |
| `/login`, `/setup`, `/account` | lazy, one each | `anonymousGuard`, `setupGuard`, `authGuard` |
| `/admin/{users,teams,roles}` with `new` and `:id` | lazy, the area (`admin-routes`) and each of its pages | `adminGuard` (`canMatch`) |

The production build warns when the **initial** bundle passes 500 kB and fails at 1 MB, and warns at 4 kB per
component style and fails at 8 kB (the `budgets` in [`angular.json`](angular.json)). `npx ng build` prints the
Initial total; the project keeps it under the warning, so a new page is a lazy route and eager code stays
minimal. The saga detail page, the heaviest in the app (the timeline, the map and the data inspector), is lazy
for that reason: the initial bundle had no room for it beside the session code. The sign-in pages and the whole
administration area are lazy too, and the area's pages are lazy in turn, so a manager downloads only the ones
they open. `@angular/forms` is in the initial bundle (the saga list's filters use it), which is why the
forms of the lazy pages are built as described below.

## The administration area

[`src/app/pages/admin/`](src/app/pages/admin) is the biggest lazy tree of the app. `app.routes.ts` loads it with
`loadChildren` under `canMatch: [adminGuard]`; `AdminShell` is its frame (the heading, the Users / Teams / Roles
tabs) and provides `AdminStore` from the route, so the store lives and dies with the area. Around them:

- **`AdminApiService`** is one method per `/api/admin` endpoint, with no state. Its request bodies carry only the
  members the API reads, because the API rejects any other. The golden JSON files under
  [`src/app/testing/contracts/admin/`](src/app/testing/contracts/admin) pin both sides of the wire contract: the
  .NET endpoint tests and `admin-api.service.spec.ts` assert the same files.
- **`AdminStore`** loads users, teams, roles, the permission catalogue and the saga types once for the shell;
  every change awaits the API and then reads the lists again, so what the pages show is what the server stores.
  It never rejects a read (a failure becomes the sentence to show, with a way to try again) and a page seeds
  its draft from it only after it has loaded.
- **The pages** (users, teams and roles, each a list and an edit page) share the pieces under `components/` and
  `pages/admin/`: the `GrantsEditor` (one grant per role, all saga types or a selection, and an "exact saga
  type name" field for a type that has not run yet), `explainAccess` (the browser's copy of the server's access
  rule, driven by the catalogue's `scopable` and `implies`, for the effective-access preview) and
  `confirm-button`. A `409 last_administrator` shows a banner and keeps the draft. Team membership is written
  only through the team: a user page shows teams read-only and sends no `teamIds`.

## Guide mode

Guide mode explains the pages from inside the app, in tours of a few steps, and `docs/dashboard-guide.md` is its
long form. Its parts:

- **`GuideService`** ([`services/guide.service.ts`](src/app/services/guide.service.ts), eager, created with the
  shell) holds the state: whether Guide is on, the area **Replay tour** repeats, the tour being asked for and the
  one-time hint. It keeps it in one `localStorage` key, `vsaga.guide`
  (`{"v":1,"enabled":true,"seen":{"list":1},"hintDismissed":true}`), read through the `GUIDE_STORAGE` token, so it
  is per origin (per compose stack), not per user; corrupt JSON, an unknown `v` or a storage that throws reads as
  the defaults, and without storage the state stays in memory. `seen[area]` is the version of the tour that was
  gone through, so bumping an area's `version` shows a changed tour once more; switching Guide on clears `seen`,
  so everything is explained again. `GUIDE_PERMISSION_CHECK` is provided from `AuthService.can` in
  `app.config.ts`, so an area the session may not use never starts, and a step it may not use is left out of its tour.
- **Pages announce themselves.** A page tells the service that a part of it is on screen with
  `guide.areaShown(id)` (the detail page's map, timeline, saga data and retry row, the administration shell). Call
  it from an effect or `afterNextRender`, never from a constructor or `ngOnInit`: the service forgets what it
  knew of a page when a navigation ends, which is after a component is created.
- **`GUIDE_AREAS`** ([`services/guide-areas.ts`](src/app/services/guide-areas.ts)) is the area table: each area's
  id, `version`, what triggers it (a route, or the page announcing it), the `readyAnchor` its tour waits for, the
  permission it `requires` and the `docsAnchor`, a heading of the user guide that the tour's **User guide** link
  opens. `USER_GUIDE_URL` is the one constant for the guide's address.
- **`GuideToggle`** ([`components/guide-toggle/`](src/app/components/guide-toggle)) is the **Guide** switch, **Replay
  tour**, the **User guide** link and the hint, in the top bar for a signed-in user.
- **`GuideOverlay`** ([`components/guide-overlay/`](src/app/components/guide-overlay)) draws the tour on screen
  (modal while it runs: the rest of the page is `inert`, Escape ends it). It is a lazy chunk, loaded with
  `@defer (when guide.enabled())` as the last child of the app template, so the eager bundle carries only the
  service, the area table and the toggle. `guide-geometry.ts` is its pure placement code, and
  [`guide-tours.ts`](src/app/components/guide-overlay/guide-tours.ts) holds the `GUIDE_ANCHORS` vocabulary of
  `data-tour` attribute values and the copy of every area's steps.
- **Anchors are a contract.** A template carries a `data-tour` attribute only with a name from `GUIDE_ANCHORS`;
  the specs of the pages and components that carry them have an anchor-contract case, and `guide-tours.spec.ts`
  checks that every anchor, fallback and reveal control a step names is in the vocabulary, so a removed or
  renamed anchor fails a test. A stale sentence fails nothing, hence the rule: a change to what a page shows or
  does updates [`docs/dashboard-guide.md`](../docs/dashboard-guide.md) and the tour steps in the same change, and
  bumps the area's `version` when the copy changes (see [`CONTRIBUTING.md`](../CONTRIBUTING.md#test)).

## Forms: the `ngNoForm` pattern

The sign-in, account and administration forms are template-driven (`FormsModule`, `ngModel` bound to signals)
with `ngNoForm` on the `<form>`: the fields are standalone controls and `submit` is the native event, and the
checks (required, lengths, the confirmation) are made in the component instead of by Angular's validator
directives. `@angular/forms` is already in the initial bundle, so `NgForm` and its validators, which nothing
else uses, would add about 7 kB to it. Follow the pattern in a new form (see `pages/login/login.ts`).

## Unit tests and `splitting: false`

`npx ng test` runs Vitest through `@angular/build:unit-test`, and [`angular.json`](angular.json) sets
`"splitting": false` on the test target. Without it a spec's `vi.mock` could be bypassed: the builder runs with
`isolate: false` and code splitting on, so modules several spec files share become one chunk per worker, and a
worker that has already evaluated that chunk for one spec (the real `saga-hub.service`, say, and the real
`@microsoft/signalr` it imports) never applies the next spec's mock to it. Without the option 12 of 20 full runs
failed all 39 hub specs with `Cannot resolve '/hubs/saga'`; with it, 24 consecutive runs passed. The builder marks the option deprecated ("No longer needed with Vitest 5") and says so at the start of
every run; remove it when the project moves to Vitest 5, or use `"isolate": true`, the more thorough but about
9 s slower alternative measured at the time.

## The container image

[`Dockerfile`](Dockerfile) builds the image compose runs as `dashboard-web`, with this directory as its
context:

1. **Build stage**, `node:22.23-bookworm-slim`: `npm ci`, then `npx ng build`. The tag pins a 22
   minor at or above 22.22.3, below which Angular CLI 22 refuses to run; a floating `node:22` would
   let a machine reuse an older cached image. The manifests are copied before the sources, so the `npm ci` layer stays cached while only the app or the nginx template
   changes. [`.dockerignore`](.dockerignore) keeps `node_modules/`, `dist/`, `.angular/`, coverage,
   spec files and `src/app/testing/` out of the context.
2. **Runtime stage**, `nginxinc/nginx-unprivileged:1.30-alpine-slim`: nginx as uid 101 on port 8080,
   serving `dist/dashboard-web/browser/` from `/srv/dashboard-web`, with the third-party licence file
   under `/usr/share/licenses/dashboard-web/`. It stops on `SIGTERM` rather than the base image's
   graceful `SIGQUIT`, which would make `docker compose down` wait out open WebSockets.

[`nginx/default.conf.template`](nginx/default.conf.template) is the whole server configuration. The
base image's entrypoint renders it into `conf.d` at start, substituting only `DASHBOARD_API_UPSTREAM`,
`DASHBOARD_OUTER_PROXY` and the resolver it reads from the container; every other `$` is nginx's own.
The image adds no shell script of its own: on a Windows checkout with `core.autocrlf` the file would
arrive with CRLF line endings and not start. What the template does, and the rules that keep it
working (the upstream as a variable plus a resolver, no URI part on `proxy_pass`, headers only at
server level, `Host` passed through, the logs), are described in
[`docs/dashboard.md`](../docs/dashboard.md#how-it-is-served) and commented next to each rule. Its
variables are listed in
[`docs/configuration.md`](../docs/configuration.md#the-dashboard-uis-container-and-dev-server).

**The Content Security Policy binds this app.** Every response carries `script-src 'self'`,
`connect-src 'self' ws://<host> wss://<host>` and `'self'` for every other source, except that inline
styles and `data:` images are allowed. So:

- no inline `<script>` and no `on*=` handler attributes in `index.html`, no `eval`;
- no third-party origin for scripts, styles, fonts, images or requests (links to other sites are plain
  navigations and are fine);
- Angular's own inline component styles are fine (`style-src 'unsafe-inline'`).

That is why the production configuration in [`angular.json`](angular.json) turns critical-CSS inlining
off (`"inlineCritical": false`, with every other `optimization` key written out, since an omitted key
is read as `false`): inlining writes `<link … media="print" onload="this.media='all'">`, an inline
handler the policy blocks, which would leave `styles.css` unapplied. CI's `angular` job fails if the
built `index.html` contains `<script>` or an `on*=` attribute; `ng serve` has no CSP to show it.

## Project layout

```
Dockerfile                  The two-stage container image (npm ci + ng build, then nginx)
.dockerignore               What the image build leaves out of its context
nginx/default.conf.template The nginx configuration: static files, the /api and /hubs proxy, headers
proxy.conf.mjs              The dev server's proxy for /api and /hubs (VSAGA_API_URL)
src/app/
  guards/                   authGuard, anonymousGuard, setupGuard, adminGuard (canMatch), safeReturnUrl
  interceptors/             The auth interceptor: 401 / 403 handling and the antiforgery retry
  pages/login/, setup/, account/   The sign-in pages, lazy routes
  pages/admin/              The administration area, a lazy tree: shell, store, API service, users, teams, roles
  pages/saga-list/          The filterable, sortable, paged saga list
  pages/saga-detail/        One instance (lazy): summary, Saga data bar, map/timeline tabs, retry, URL state
  components/user-menu/     The signed-in user's menu in the top bar
  components/access-summary/ What a session, user or team may do, as sentences
  components/confirm-button/ An inline "are you sure?" for destructive actions
  components/saga-map/      The service-graph renderer, its replay scrubber and the as-of-entry banner
  components/saga-timeline/ The timeline as numbered steps, with Recorded at times and the failed-step marker
  components/saga-data-inspector/  One state: changes against an earlier one, full state, message, copy
  components/saga-data-overview/   The Saga data bar: At start, At end (or Current), Compare
  components/guide-toggle/  The Guide switch, Replay tour, the User guide link and the one-time hint
  components/guide-overlay/ The tour on screen (a lazy chunk), its geometry, the tour copy and GUIDE_ANCHORS
  components/local-time/    A <time> in the browser's local zone, UTC on hover
  components/kind-badge/    The Orchestrated/Choreographed pill
  components/status-badge/  The saga-status pill
  services/                 AuthService, GuideService and its area table, the HTTP client and the SignalR hub client
  models/                   DTOs mirroring the API's response shapes (sessions, sagas)
  util/                     Pure helpers: the step fold, JSON diff, state JSON and markers, time formats,
                            entry-type labels, the API's problem bodies, session access checks
  testing/                  Spec-only fixtures (timeline entries and steps, the admin API's golden JSON),
                            excluded from the app build
  stale-chunk-reload.ts     Reloads once when a lazy chunk of a rebuilt image no longer exists
```

Generated with Angular CLI 21.2.10 and since moved to Angular 22; `npx ng generate component <name>`
still works as usual for adding to it. Angular 22 generates `OnPush` components by default, so
`angular.json` sets the component schematic to `Eager`, the change detection every existing component
declares.
