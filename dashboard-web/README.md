# vSaga dashboard (Angular SPA)

The web UI for the vSaga ops dashboard: a saga list with filtering and live updates, a per-instance
detail page with a service map, timeline, and state data, and manual retry for failed sagas. It is
a thin client over the Dashboard API — every screen here is backed by an endpoint documented in
[`docs/dashboard.md`](../docs/dashboard.md).

## Prerequisites

Node.js `>= 22` (this package's `engines` field; CI builds with 22). The API and its dependencies have
to be running first — this app has no data of its own:

```bash
docker compose up -d --build      # from the repository root: Postgres, RabbitMQ, dashboard API, sample
```

## Run it

```bash
npm install
npx ng serve                      # http://localhost:4200
```

Both run from `dashboard-web/`, which has its own lockfile and Angular CLI toolchain; CI builds and
tests it as its own job (`angular` in `.github/workflows/ci.yml`).

## Commands

| Command | What it does |
| --- | --- |
| `npx ng serve` | Dev server with hot reload on http://localhost:4200 |
| `npx ng build` | Production bundle into `dist/` |
| `npx ng test` | Unit tests (vitest), interactive watch mode |
| `npx ng test --watch=false` | Same, single run — what CI actually runs |

## How it reaches the API

Both values live in [`src/app/api-config.ts`](src/app/api-config.ts):

```ts
export const API_BASE_URL = 'http://localhost:5080';
export const HUB_URL = `${API_BASE_URL}/hubs/saga`;
export const DASHBOARD_API_KEY = 'dev-local-only-change-me';
```

The key is attached to every request by an HTTP interceptor
([`src/app/interceptors/api-key.interceptor.ts`](src/app/interceptors/api-key.interceptor.ts)) as the
`X-Api-Key` header, and passed to the SignalR hub via `accessTokenFactory`.

Two consequences worth knowing before you change anything:

- **Changing the server's key means editing this file too.** The API reads `Dashboard:ApiKey` from
  its own configuration (`Dashboard__ApiKey` in `docker-compose.yml`); the two are not wired
  together, so they have to be changed in both places.
- **The key ships in the bundle.** It is a build-time constant in client-side JavaScript, so anyone
  who can load the page can read it. That is an accepted trade-off for an internal ops dashboard on
  a trusted network — see [`docs/dashboard.md#authentication`](../docs/dashboard.md#authentication)
  for the reasoning and what deploying this beyond that setting would require.

The API also only accepts browser requests from the origin in its `Dashboard__WebOrigin` setting,
which is `http://localhost:4200` in `docker-compose.yml`. Serve this app on a different port and
CORS will reject its calls until that setting matches.

## Project layout

```
src/app/
  pages/saga-list/      The filterable, sortable, paged saga list
  pages/saga-detail/    One instance: summary, map/timeline/data tabs, retry
  components/saga-map/  The service-graph renderer and its replay scrubber
  services/             HTTP client and the SignalR hub client
  models/               DTOs mirroring the API's response shapes
```

Generated with Angular CLI 21.2.10; `npx ng generate component <name>` still works as usual for
adding to it.
