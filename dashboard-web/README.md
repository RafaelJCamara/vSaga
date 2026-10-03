# vSaga dashboard (Angular SPA)

The web UI for the vSaga ops dashboard: a saga list with filtering and live updates, a per-instance
detail page with two tabs, a service map and a timeline of numbered steps, a Saga data bar (the data at
start, at end, and a comparison) and each step's data, and a retry that re-runs the step a failed saga
failed in (see [`docs/dashboard.md`](../docs/dashboard.md#the-saga-detail-page)). It is
a thin client over the Dashboard API — every screen here is backed by an endpoint documented in
[`docs/dashboard.md`](../docs/dashboard.md).

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
export const DASHBOARD_API_KEY = 'dev-local-only-change-me';
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
has to match any server setting. A `dotnet run` API ships an empty `Dashboard:ApiKey`, which denies
every request, so start it with `Dashboard__ApiKey` set to `DASHBOARD_API_KEY`'s value.

The key is attached to every request by an HTTP interceptor
([`src/app/interceptors/api-key.interceptor.ts`](src/app/interceptors/api-key.interceptor.ts)) as the
`X-Api-Key` header, and passed to the SignalR hub via `accessTokenFactory`.

Two consequences worth knowing before you change anything:

- **Changing the server's key means editing this file too.** The API reads `Dashboard:ApiKey` from
  its own configuration (`Dashboard__ApiKey` in `docker-compose.yml`); the two are not wired
  together, so they have to be changed in both places, and the image rebuilt.
- **The key ships in the bundle.** It is a build-time constant in client-side JavaScript, so anyone
  who can load the page can read it. That is an accepted trade-off for an internal ops dashboard on
  a trusted network — see [`docs/dashboard.md#authentication`](../docs/dashboard.md#authentication)
  for the reasoning and what deploying this beyond that setting would require.

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
  pages/saga-list/          The filterable, sortable, paged saga list
  pages/saga-detail/        One instance: summary, Saga data bar, map/timeline tabs, retry, URL state
  components/saga-map/      The service-graph renderer, its replay scrubber and the as-of-entry banner
  components/saga-timeline/ The timeline as numbered steps, with Recorded at times and the failed-step marker
  components/saga-data-inspector/  One state: changes against an earlier one, full state, message, copy
  components/saga-data-overview/   The Saga data bar: At start, At end (or Current), Compare
  components/local-time/    A <time> in the browser's local zone, UTC on hover
  components/kind-badge/    The Orchestrated/Choreographed pill
  components/status-badge/  The saga-status pill
  services/                 HTTP client and the SignalR hub client
  interceptors/             The HTTP interceptor that adds the X-Api-Key header
  models/                   DTOs mirroring the API's response shapes
  util/                     Pure helpers: the step fold, JSON diff, state JSON and markers, time formats,
                            entry-type labels
  testing/                  Spec-only fixtures (timeline entries and steps), excluded from the app build
```

Generated with Angular CLI 21.2.10 and since moved to Angular 22; `npx ng generate component <name>`
still works as usual for adding to it. Angular 22 generates `OnPush` components by default, so
`angular.json` sets the component schematic to `Eager`, the change detection every existing component
declares.
