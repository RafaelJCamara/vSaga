# Compose packaging and same-origin serving (improvement 5)

## Summary

- A new `dashboard-web` image (Node 22 build stage, then `nginxinc/nginx-unprivileged:1.30-alpine-slim`) serves the built SPA and proxies `/api/` and `/hubs/` to `dashboard-api:8080`. Compose runs it as a fifth service on host port 4200; the six overlays override only that port (4300 to 4800).
- The SPA moves to relative URLs. `ng serve` gains a proxy and moves to port 4201, so it runs beside the compose UI. Nothing is cross-origin any more, so `Dashboard:WebOrigin` becomes optional with an empty default.
- nginx resolves the upstream per request through Docker's DNS: each compose project reaches its own API by service name, and the UI survives that API being recreated.
- The API gains opt-in trust for `X-Forwarded-For` and `X-Forwarded-Proto` (`Dashboard:TrustedProxies`), a named identity volume, the seeded-administrator environment and a non-root user.
- CI gains a third job: validate every overlay and the port rule, build the images, smoke-test the UI origin up to a WebSocket upgrade.
- README "Run the demo" becomes one command.
- Two hazards for other assignments: every stack on `localhost` shares one cookie jar, and the image's CSP does not exist under `ng serve`.

## Design

### 1. Image: `dashboard-web/Dockerfile` (context `./dashboard-web`)

```dockerfile
FROM node:22-bookworm-slim AS build
WORKDIR /src
ENV CI=true NG_CLI_ANALYTICS=false
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY angular.json tsconfig*.json ./
COPY public/ public/
COPY src/ src/
RUN npx ng build

FROM nginxinc/nginx-unprivileged:1.30-alpine-slim AS runtime
ENV DASHBOARD_API_UPSTREAM=dashboard-api:8080 \
    NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1 \
    NGINX_ENVSUBST_FILTER="^(DASHBOARD_API_UPSTREAM|NGINX_LOCAL_RESOLVERS)"
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /src/dist/dashboard-web/browser/ /srv/dashboard-web/
COPY --from=build /src/dist/dashboard-web/3rdpartylicenses.txt /usr/share/licenses/dashboard-web/
EXPOSE 8080
STOPSIGNAL SIGTERM
```

- Both tags exist on Docker Hub for amd64 and arm64 (checked 2026-09-28; 1.30 is nginx's stable line). They float on the patch, like `postgres:16-alpine`.
- The build stage is glibc, the path CI's ubuntu `npm ci` already proves. `package-lock.json` carries linux x64 and arm64 gnu bindings for esbuild, rolldown, rollup and lmdb.
- Manifests are copied before sources, as `dotnet/src/VSaga.Dashboard.Api/Dockerfile:4-19` does, so `npm ci` stays cached and template edits never rebuild the SPA. `ng build` is production by default (`angular.json:58`) and writes `dist/dashboard-web/browser` (confirmed on disk).
- Base-image behaviour relied on, read from its Dockerfile and entrypoint scripts: uid 101, port 8080, `/etc/nginx` writable by that uid; `/etc/nginx/templates/*.template` is rendered into `conf.d` at start, substituting only variables whose names match `NGINX_ENVSUBST_FILTER`; `NGINX_LOCAL_RESOLVERS` is exported from `/etc/resolv.conf` when `NGINX_ENTRYPOINT_LOCAL_RESOLVERS` is set.
- No custom shell script: this machine has `core.autocrlf=true` and the repo has no `.gitattributes`, so context files arrive with CRLF. nginx treats CR as whitespace; a `#!/bin/sh` script would not start.
- `STOPSIGNAL SIGTERM`: the base image's SIGQUIT makes `docker compose down` wait out open WebSockets.
- `dashboard-web/.dockerignore`: `node_modules/`, `dist/`, `.angular/`, `coverage/`, `out-tsc/`, `src/**/*.spec.ts` (the exclusion `tsconfig.app.json:12-14` already applies).

### 2. `dashboard-web/nginx/default.conf.template`

```nginx
map $http_upgrade $connection_upgrade { default upgrade; '' close; }
map $http_x_forwarded_proto $forwarded_proto { default $http_x_forwarded_proto; '' $scheme; }
log_format dashboard '$remote_addr [$time_local] "$request_method $uri $server_protocol" '
                     '$status $body_bytes_sent $request_time';

server {
    listen 8080;
    server_name _;
    server_tokens off;
    absolute_redirect off;
    access_log /var/log/nginx/access.log dashboard;
    root /srv/dashboard-web;
    index index.html;

    resolver ${NGINX_LOCAL_RESOLVERS} valid=10s ipv6=off;
    set $dashboard_api http://${DASHBOARD_API_UPSTREAM};

    gzip on;
    gzip_vary on;
    gzip_min_length 1024;
    gzip_types text/css text/javascript application/javascript application/json image/svg+xml;

    add_header X-Content-Type-Options "nosniff" always;
    add_header X-Frame-Options "DENY" always;
    add_header Referrer-Policy "same-origin" always;
    add_header Content-Security-Policy "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self' ws://$http_host wss://$http_host; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'" always;

    proxy_http_version 1.1;
    proxy_set_header Host $http_host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $forwarded_proto;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_buffer_size 16k;
    proxy_buffers 8 16k;

    location = /healthz { access_log off; default_type text/plain; return 200 "ok\n"; }
    location ^~ /api/ { proxy_pass $dashboard_api; }
    location ^~ /hubs/ {
        proxy_pass $dashboard_api;
        proxy_buffering off;
        proxy_read_timeout 1h;
        proxy_send_timeout 1h;
    }
    location = /index.html { expires -1; }
    location ~ "^/[^/]+-[A-Z0-9]{8}\.(?:js|css)$" { expires 1y; try_files $uri =404; }
    location ^~ /media/ { expires 1y; try_files $uri =404; }
    location / { try_files $uri /index.html; }
}
```

Rules the implementer must keep:

1. **Upstream.** `dashboard-api` is a compose service name, resolved inside each project's own network, so `-p vsaga-wolverine`'s nginx reaches only that project's API and no overlay sets anything. `DASHBOARD_API_UPSTREAM` is for use outside compose (for example `host.docker.internal:5275`).
2. **Variable plus `resolver`.** A literal `proxy_pass http://dashboard-api:8080` resolves once at start. `docker compose up -d --build` recreates dashboard-api and order-processing together (same build context), their addresses can swap, and both listen on 8080, so nginx would proxy the UI to the sample host. The variable form re-resolves every 10 s and lets nginx start without the API.
3. **No URI part on `proxy_pass`.** nginx then forwards the request line untouched, so `Order%2FSaga` (the SPA encodes the saga type, `saga-api.service.ts:35-37`) stays encoded.
4. **Headers at server level only.** `proxy_set_header` and `add_header` reach only locations that declare none of their own, so caching uses `expires`, never `add_header Cache-Control`.
5. **`Host $http_host`** keeps the browser's host and port (`$host` drops the port). `X-Forwarded-Proto` passes an outer TLS terminator's value through.
6. **CSP.** `script-src 'self'` blocks inline handlers, and today's build emits one: `dist/dashboard-web/browser/index.html` contains `<link … media="print" onload="this.media='all'">`, which would leave `styles.css` unapplied. Section 4 turns that off. `'unsafe-inline'` for styles is required because Angular injects component styles as `<style>` elements. WebSocket sources are spelled out because `'self'` covering `ws:` has varied between browsers.
7. **`Referrer-Policy: same-origin`, not `no-referrer`.** Under `no-referrer` the Fetch standard serialises `Origin` as `null` on same-origin unsafe requests, which would defeat an Origin check.
8. **Caching.** `index.html` revalidates; content-hashed bundles get one year, and a missing one is a 404 rather than `index.html`.
9. **`$uri` in the log format**: a machine client's `?access_token=` (the API key, `ApiKeyAuthenticationHandler.cs:50-52`) never reaches the log.
10. **`/hubs/`**: buffering off for the SSE fallback; SignalR's long poll holds a request for 90 s, past nginx's 60 s default.

### 3. Compose

New service in `docker-compose.yml`, after `dashboard-api` (`:29-51`):

```yaml
  dashboard-web:
    build:
      context: ./dashboard-web
    ports:
      - "4200:8080" # http://localhost:4200. In every stack: dashboard-api's host port minus 880.
    depends_on:
      dashboard-api:
        condition: service_healthy
    healthcheck:
      # 127.0.0.1, not localhost: BusyBox wget may try ::1 first and nginx listens on IPv4 only.
      test: ["CMD", "wget", "-q", "-O", "/dev/null", "http://127.0.0.1:8080/healthz"]
      interval: 5s
      timeout: 5s
      retries: 10
```

The healthcheck reports on nginx alone; the CI smoke test covers the proxy path. `dashboard-api`: delete `Dashboard__WebOrigin` (`:39`) and add:

```yaml
      Dashboard__ApiKeyRole: "Viewer"
      Dashboard__Identity__Provider: "Sqlite"
      Dashboard__Identity__Sqlite__Path: "/var/lib/vsaga-dashboard/identity.db"
      Dashboard__Admin__Username: "admin"
      Dashboard__Admin__Password: "dev-local-only-change-me"
      Dashboard__TrustedProxies: "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16"
    volumes:
      - vsaga-dashboard-identity:/var/lib/vsaga-dashboard
```

plus `vsaga-dashboard-identity:` under top-level `volumes` (`:77-78`). Volumes are per compose project, so every overlay has its own users and keys.

Overlays: add a `dashboard-web:` entry after each `dashboard-api` block.

| File | Line under `dashboard-web:` | Header comment |
| --- | --- | --- |
| `docker-compose.wolverine.yml` | `ports: !override ["4300:8080"]` | `:12` port list becomes `(5433/5672/15672/5080/4200)`; add `# Dashboard: http://localhost:4300` under the command (`:8`) |
| `docker-compose.masstransit.yml` | `ports: !override ["4400:8080"]` | `:16` likewise; dashboard line under `:9` |
| `docker-compose.brighter.yml` | `ports: !override` then `- "4500:8080"` (block form, as that file writes them) | `:13` likewise; dashboard line under `:7` |
| `docker-compose.http.yml` | `ports: !override ["4600:8080"]` | `:27-28` list gains `dashboard-web 4600`; dashboard line under `:5` |
| `docker-compose.mongo.yml` | `ports: !override ["4700:8080"]` | `:26-27` gains `plus dashboard-web 4700 (the API port minus 880, as in every stack)`; under `:5` |
| `docker-compose.redis.yml` | `ports: !override ["4800:8080"]` | `:20-21` likewise with 4800; under `:5` |

`docker-compose.chaos.yml` remaps nothing; add one comment line saying the dashboard stays on 4200.

### 4. Angular

- `src/app/api-config.ts:7-8`: `API_BASE_URL = ''`; `HUB_URL` stays derived and becomes `/hubs/saga`. `@microsoft/signalr` resolves a relative URL against the page (`HttpConnection.js:449-466`). `DASHBOARD_API_KEY` stays until the authentication assignment removes it, so this change is green on its own.
- New `dashboard-web/proxy.conf.mjs`:

```js
// VSAGA_API_URL points ng serve at another stack: an overlay's API port (5180 … 5680) or
// http://localhost:5275 for `dotnet run`.
const target = process.env.VSAGA_API_URL ?? 'http://localhost:5080';
export default {
  '/api/': { target },
  '/hubs/': { target, ws: true },
};
```

  `changeOrigin` stays unset so the API sees `Host: localhost:4201`. The loader accepts `.mjs` (`@angular/build/src/utils/load-proxy-config.js:77-88`).
- `angular.json:60-71`, serve: `"options": { "proxyConfig": "proxy.conf.mjs", "port": 4201 }`. The dev server is started with `strictPort`, so on 4200 it would fail whenever the compose UI is up. On 4201 both run against the same API, and with relative URLs no port has to match any server setting. To run without the container: `docker compose up -d --build --scale dashboard-web=0`.
- `angular.json:37-51`, production:

```json
"optimization": {
  "scripts": true,
  "styles": { "minify": true, "inlineCritical": false, "removeSpecialComments": true },
  "fonts": true
}
```

  Every key is written out: `normalizeOptimization` (`@angular/build/src/utils/normalize-optimization.js:11-29`) coerces omitted keys to false, which would ship unminified JS and CSS and break both budgets.

### 5. API edge: CORS and forwarded headers

New `dotnet/src/VSaga.Dashboard.Api/Hosting/DashboardEdge.cs`:

```csharp
internal sealed record DashboardEdgeSettings(string? WebOrigin, IReadOnlyList<System.Net.IPNetwork> TrustedProxies);

internal static class DashboardEdge
{
    internal const string CorsPolicy = "Dashboard";
    internal static DashboardEdgeSettings Read(IConfiguration configuration);
    internal static IServiceCollection AddDashboardEdge(this IServiceCollection services, DashboardEdgeSettings settings);
    internal static WebApplication UseDashboardEdge(this WebApplication app);
}
```

- `Read`: an empty `Dashboard:WebOrigin` means none; otherwise it must be an absolute http or https URI with no path, stored without a trailing slash, else `InvalidOperationException` (the convention at `Program.cs:69-70`). `Dashboard:TrustedProxies` is a comma-separated list of addresses or CIDR networks; a malformed entry throws and names it.
- `AddDashboardEdge`: registers the settings; adds today's policy (`Program.cs:33-34`) only when an origin is set; configures `ForwardedHeadersOptions` only when proxies are listed: `XForwardedFor | XForwardedProto`, `ForwardLimit = 1`, both known lists cleared, networks added to `KnownIPNetworks` (`KnownNetworks` is obsolete in .NET 10 and fails the warnings-as-errors build).
- `UseDashboardEdge`: `UseForwardedHeaders()` when a proxy is trusted, then `UseCors(CorsPolicy)` when an origin is set.
- `Program.cs:31-34` becomes `var edge = DashboardEdge.Read(builder.Configuration); builder.Services.AddDashboardEdge(edge);`. Line 147 becomes `app.UseDashboardEdge();` and stays the first middleware. `appsettings.json:15-18`: `"WebOrigin": ""`, `"TrustedProxies": ""`.

Forwarded headers are needed for scheme and client address only (Host is passed through): decision D sets `Secure` cookies when the request is HTTPS and rate-limits login, and behind nginx every browser arrives over HTTP from one container address. CORS goes opt-in because the fallback origin `http://localhost:4200` (`Program.cs:32`) would grant credentialed cross-origin reads to whatever runs on 4200, which for an overlay is another stack's UI.

Trust is explicit and off by default. Compose lists private ranges because the proxy's address is dynamic, so a peer in those ranges that reaches port 5080 directly can assert a client address. Per-account lockout, not the per-address limiter, is the control that must hold.

### 6. API image and volume

`dotnet/src/VSaga.Dashboard.Api/Dockerfile:21-29`, after `COPY --from=build`: `RUN mkdir -p /var/lib/vsaga-dashboard && chown $APP_UID /var/lib/vsaga-dashboard`, then `USER $APP_UID` (the `app` user the aspnet image ships, uid 1654). A new named volume takes the image directory's ownership. curl is installed earlier as root; port 8080 needs no privilege.

### 7. CI

Add job `compose` ("Compose build & smoke") to `.github/workflows/ci.yml`, independent of the other two:

1. `docker compose config -q` for the base file and the chaos overlay. For each of `wolverine:5180 masstransit:5280 brighter:5380 http:5480 mongo:5580 redis:5680`, render with `config --format json` and assert with `jq` that `.services["dashboard-api"].ports | map(.published) | join(",")` equals the number and the same expression for `dashboard-web` equals it minus 880. A forgotten `!override` shows up as two ports.
2. `docker compose build`.
3. `docker compose run --rm --no-deps dashboard-web nginx -t`.
4. `docker compose up -d --wait --wait-timeout 240`.
5. Through `http://localhost:4200` with `X-Api-Key: dev-local-only-change-me`: a deep link returns `<app-root`; `/api/saga-types` returns 200; `/gone-ABCDEFGH.js` returns 404; `POST /hubs/saga/negotiate?negotiateVersion=1` yields a `connectionToken`; a `curl --http1.1 --max-time 3` upgrade request to `/hubs/saga?id=<token>` answers `101`.
6. `docker compose logs` on failure; `docker compose down -v` always.

The API-key calls work before and after the authentication work lands. In the `angular` job, after Build: `! grep -Eq '<script>| on[a-z]+=' dist/dashboard-web/browser/index.html`, which guards the CSP assumption.

### 8. README "Run the demo" (`README.md:64-131`)

1. Intro sentence names the dashboard UI.
2. One command, `docker compose up -d --build   # Postgres + RabbitMQ + dashboard API + dashboard UI + OrderProcessing sample`, then "open http://localhost:4200 and sign in as `admin` / `dev-local-only-change-me`". Note that the first build runs `npm ci` and `ng build`.
3. "From a terminal": the two existing curl lines and their PowerShell callout (`:72-77`).
4. Mongo and Redis paragraph: each overlay serves its own UI (4700, 4800).
5. Delete `:88-100` (the `API_BASE_URL` paragraph, "Then serve the dashboard UI", the `&&` callout).
6. Table rows: `Dashboard UI | http://localhost:4200 | Sign in as admin / dev-local-only-change-me, seeded by Dashboard__Admin__* in docker-compose.yml; the guide toggle in the top bar walks through it`, and `Dashboard API | http://localhost:5080 | For curl and machine clients: API key dev-local-only-change-me, read-only (Viewer). The UI does not use this port; its nginx proxies /api and /hubs`. Other rows unchanged.
7. Overlay table (`:115-119`): new first row `Dashboard UI | localhost:4700 | localhost:4800`.
8. Callout: the credentials are public; change `Dashboard__Admin__Password` and `Dashboard__ApiKey` before exposing the ports. If `up` reports port 4200 as allocated, a dev server is still running.
9. Volume note (`:129-131`): the identity volume also persists and the seed applies only to an empty one.
10. One line on developing the SPA (`ng serve` on 4201, see `dashboard-web/README.md`). Repository layout (`:160-165`) mentions the image.

### 9. Other documents

- `docs/transports/index.md`: `:143-146` add 4200 to the ports and URLs named; the table at `:158-165` gains a "Dashboard UI" column (4300, 4400, 4500, 4600, Redis 4800, Mongo 4700); `:170-175` rewritten: open the overlay's UI port, nothing to edit, each project has its own users, plus the cookie caveat.
- `docs/dashboard.md`: "The SPA" `:141-148` replaced by "How it is served" (compose service, same origin, dev server on 4201) and a short "Behind your own proxy or TLS" list (WebSocket upgrade on `/hubs/`, Host passed through, `X-Forwarded-Proto`, `Dashboard:TrustedProxies`). `:82-89` must stop describing URL constants.
- `docs/configuration.md`, Dashboard section: `Dashboard:WebOrigin` row and `:374-386` rewritten (optional, default empty, validated at startup, not needed by the bundled UI); new `Dashboard:TrustedProxies` row and subsection; a table for the container's `DASHBOARD_API_UPSTREAM` and the dev server's `VSAGA_API_URL`.
- `dashboard-web/README.md`: Prerequisites (Node only for development), "Run it" (4201, proxy, `VSAGA_API_URL`), Commands, "How it reaches the API" rewritten, new "The container image" (stages, template, CSP rules), layout adds `nginx/`, `Dockerfile`, `proxy.conf.mjs`.
- `CONTRIBUTING.md`: `:8-9` Node is for SPA development only; `:51-67` each command also serves that stack's UI, and UI changes are verified in the container because the CSP and proxy exist only there; `:98-106` add an image check; `:108-118` three CI jobs.
- `docs/persistence.md:372,602` and `docs/chaos.md:83-91` name the UI port; `docs/README.md:25-26` mentions how the dashboard is served.

## Files

Paths are relative to `C:/Users/rafae/Documents/Projects/vSaga`. Most critical: `C:/Users/rafae/Documents/Projects/vSaga/docker-compose.yml`, `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/nginx/default.conf.template`, `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/Dockerfile`, `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/angular.json`, `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Program.cs`.

Create:
- `dashboard-web/Dockerfile`: two-stage image.
- `dashboard-web/.dockerignore`: context exclusions.
- `dashboard-web/nginx/default.conf.template`: static serving, proxy, headers.
- `dashboard-web/proxy.conf.mjs`: dev proxy.
- `dashboard-web/src/app/api-config.spec.ts`: pins the relative URLs.
- `dotnet/src/VSaga.Dashboard.Api/Hosting/DashboardEdge.cs`: CORS and forwarded-header setup.
- `dotnet/tests/VSaga.Dashboard.Api.Tests/DashboardEdgeTests.cs`: its tests.

Modify:
- `docker-compose.yml`: service, environment, volume.
- `docker-compose.{wolverine,masstransit,brighter,http,mongo,redis}.yml`: UI port override and header comments; `docker-compose.chaos.yml`: one comment line.
- `dashboard-web/angular.json`: serve options, production optimization.
- `dashboard-web/src/app/api-config.ts`: relative URLs and a new comment.
- `dotnet/src/VSaga.Dashboard.Api/Program.cs`, `appsettings.json`, `Dockerfile`: edge wiring, defaults, volume directory and user.
- `.github/workflows/ci.yml`: `compose` job and the index.html guard.
- `README.md`, `CONTRIBUTING.md`, `dashboard-web/README.md`, `docs/dashboard.md`, `docs/configuration.md`, `docs/transports/index.md`, `docs/persistence.md`, `docs/chaos.md`, `docs/README.md`: as listed in sections 8 and 9.

## Tests

`dashboard-web`
- New `api-config.spec.ts`: `API_BASE_URL` is `''`; `HUB_URL` is `'/hubs/saga'`.
- No existing spec changes: `saga-api.service.spec.ts:29,45,74,83,110,118,126,134,142,151` and `saga-hub.service.spec.ts:125` build their expectations from the imported constants. `saga-hub.service.spec.ts:126` (the key) changes with the authentication assignment, not here.

`VSaga.Dashboard.Api.Tests`, new `DashboardEdgeTests.cs`
- `Read_WithNoEdgeKeys_ReturnsNoOriginAndNoProxies`
- `Read_WithATrailingSlashOnWebOrigin_NormalisesIt`
- `Read_WithAWebOriginThatIsNotAnOrigin_Throws` (theory: `localhost:4200`, `http://localhost:4200/app`, `ftp://x`)
- `Read_WithAddressesAndNetworks_ParsesBoth`; `Read_WithAMalformedTrustedProxy_ThrowsNamingTheEntry`
- `Cors_WhenWebOriginIsUnset_SendsNoCorsHeaders`, `Cors_ForTheConfiguredOrigin_AllowsItWithCredentials`, `Cors_ForAnotherOrigin_SendsNoCorsHeaders`: `DashboardApiFactory` with `UseSetting`, because the keys are read while composing (`HealthEndpointTests.cs:56-58`).
- `ForwardedHeaders_FromATrustedProxy_ReplaceSchemeAndRemoteAddress`, `_FromAnUntrustedPeer_AreIgnored`, `_WithSeveralHops_HonourOnlyTheLast`: a minimal `WebApplication` on `TestServer` whose first middleware sets `Connection.RemoteIpAddress`, then `UseDashboardEdge`, then a probe endpoint. No dependency on the authentication work.
- Existing tests: none change (no test mentions CORS or `WebOrigin`); the summary comment at `HealthEndpointTests.cs:8` drops "CORS".

The nginx configuration is covered by `nginx -t` and the CI smoke test.

## Commit sequence

1. Make the SPA same-origin: relative URLs, `proxy.conf.mjs`, serve options, `api-config.spec.ts`, the "How it reaches the API" section of `dashboard-web/README.md`.
2. Make the dashboard's CORS policy opt-in: `DashboardEdge` (CORS half), `Program.cs`, `appsettings.json`, remove `Dashboard__WebOrigin` from compose, tests, the `docs/configuration.md` subsection.
3. Add the dashboard-web image: Dockerfile, `.dockerignore`, template, `inlineCritical` off, the index.html guard in the `angular` job.
4. Run the dashboard UI from compose: base service, six overrides, header comments.
5. Honour forwarded headers from trusted proxies: `DashboardEdge` (second half), the compose key, tests, the docs row.
6. Persist the identity database on a volume, seed the demo administrator, run the API as non-root: API Dockerfile and compose in one commit. Lands with or after the identity-store commits; the environment keys are inert before them.
7. Build the compose images and smoke-test the UI origin in CI; CONTRIBUTING's CI section.
8. Document the one-command demo: README and the remaining documents.

Commits 1 to 5 and 7 do not depend on the authentication work. The README sign-in rows in commit 8 do.

## Verification

- `cd dashboard-web && npm ci && npx ng build && npx ng test --watch=false`: the main bundle is still about 403 kB (minification intact) and `dist/dashboard-web/browser/index.html` has a plain stylesheet link with no `onload`.
- `dotnet build dotnet/VSaga.slnx` with zero warnings; `dotnet test dotnet/tests/VSaga.Dashboard.Api.Tests`.
- `docker compose up -d --build --wait`, then `docker compose ps`: five services, `dashboard-web` healthy on `4200->8080`.
- `curl -sI http://localhost:4200/`: 200, `Cache-Control: no-cache`, the four security headers, `Server: nginx` with no version. `curl -sI http://localhost:4200/main-<hash>.js`: `Cache-Control: max-age=31536000`.
- Request `/api/sagas/Order%2FSaga/00000000-0000-0000-0000-000000000000` with the API key on port 4200 and on port 5080: status and body are identical, so the encoded slash survives the proxy.
- Browser on http://localhost:4200: the list fills and updates live; devtools shows `ws://localhost:4200/hubs/saga` with status 101; no CSP violation in the console on any page, including login, administration and the guide.
- `docker compose up -d --force-recreate dashboard-api`: the UI reconnects within about 10 s without restarting `dashboard-web`.
- `docker compose -p vsaga-mongo -f docker-compose.yml -f docker-compose.mongo.yml up -d --build --wait`: http://localhost:4700 shows the MongoDB stack while 4200 shows the Postgres one.
- `npx ng serve`: http://localhost:4201 works with the compose UI still up; with `VSAGA_API_URL=http://localhost:5580` it shows the MongoDB stack.
- `docker compose exec dashboard-api id` prints uid 1654 and `docker compose exec dashboard-web id` prints uid 101. `docker compose down` returns promptly with a tab open. After `down` and `up` the session survives; after `down -v` the administrator is seeded again.
- `docker compose logs dashboard-web` shows no query strings.

## Cross-assignment contracts

- **Paths.** Browser-facing API surface lives only under `/api/` and `/hubs/`. `/healthz` belongs to nginx and `/health` is not proxied. SPA routes must not start with `api/`, `hubs/` or `healthz`.
- **SPA URLs** stay relative. The authentication assignment removes `DASHBOARD_API_KEY`, the interceptor and `accessTokenFactory`; this assignment leaves them in place.
- **API down.** nginx answers `/api/` with 502 or 504 and an HTML body; the SPA must show "API unreachable", not the login page.
- **Header names** must not contain underscores (nginx drops them).
- **CSP** (binds the tour, login and administration pages): no inline `<script>`, no inline handler attributes in `index.html`, no `eval`, no third-party origin for scripts, styles, fonts, images or requests. Inline styles and `data:` images are allowed; links to GitHub are plain navigations.
- **`src/styles.scss`** now loads as an ordinary stylesheet. `angular.json` serve options and production `optimization` are owned here; budgets are untouched.
- **Pipeline.** `app.UseDashboardEdge()` stays first, before rate limiting, authentication and antiforgery.
- **Host fidelity.** `Request.Host` is the browser's `host:port` behind both nginx and the dev proxy, and the scheme follows `X-Forwarded-Proto`. Recommended use: reject a `/hubs/` handshake whose `Origin` is present and differs from `{scheme}://{host}` (or `Dashboard:WebOrigin`). WebSockets bypass CORS, and SameSite treats every `localhost` port as the same site.
- **Cookie names must be per stack.** Cookies ignore ports, so the base stack (4200) and an overlay (4300) share one jar. With equal names, signing in to one replaces the other's session and XSRF cookies, and each API rejects the other's (separate key rings). Proposed rule, computable on both sides without configuration: append `-<port>` when the request's Host has an explicit port (`Request.Host.Port` on the server; `location.port` in `withXsrfConfiguration({ cookieName })`). ASP.NET's antiforgery cookie name is fixed at startup, so the backend needs a token design or renaming layer that allows this. If the lead declines, the README states that one browser profile holds one signed-in stack, and that a second can be opened as `http://127.0.0.1:<port>`.
- **`Cache-Control: no-store`** on `/api/` responses is the API's job; nginx adds no cache headers to proxied responses.
- **Configuration.** New key: `Dashboard:TrustedProxies`. Changed: `Dashboard:WebOrigin` is optional, default empty. Compose values: identity path `/var/lib/vsaga-dashboard/identity.db`, `admin` / `dev-local-only-change-me`. That password has 24 characters: if `Dashboard:Password:MinLength` defaults higher, or seeding fails at startup for any other reason, dashboard-api never turns healthy and order-processing never starts. The README assumes a configuration-seeded administrator is not forced to change password.
- **API Dockerfile.** The identity assignment adds its two csproj COPY lines (`:5-15`); the runtime-stage lines are owned here. The default for an unset `Dashboard:Identity:Sqlite:Path` must resolve outside `/app` and the source tree (the process is uid 1654).
- **API key through the UI port** keeps working with the Viewer role: the CI smoke test depends on `/api/saga-types` and hub negotiate accepting it.
- **Ports.** UI 4200, 4300, 4400, 4500, 4600, 4700, 4800; dev server 4201.
- **Documents.** The design document gets a "Packaging and same-origin serving" section and no separate ADR (the authentication ADR cites same-origin serving as its precondition). The history entry records the verification list above.

## Objections

None. The fixed decisions are workable as written.

## Risks
- Shared cookie jar: cookies ignore ports, so the base stack on localhost:4200 and an overlay on localhost:4300 overwrite each other's session and XSRF cookies when the names are equal, and each API rejects the other's cookies. Mitigation: per-stack cookie names (the '-<port>' rule under Cross-assignment contracts); the fallback is a README sentence that one browser profile holds one signed-in stack, with http://127.0.0.1:<port> as the way to open a second.
- The CSP exists only in the nginx image, so a page that works under ng serve can break in the container (an inline script, eval, a third-party font). Mitigation: critical-CSS inlining is turned off, the Angular CI job greps the built index.html for inline scripts and handlers, the contract forbids those constructs, and verification requires a browser console check against the container for every new page.
- angular.json 'optimization' written as an object: normalizeOptimization coerces omitted keys to false, so a partial object would ship unminified JS and CSS and fail both budgets. Mitigation: every key is written out, and verification compares the main bundle size with today's 403 kB.
- Stale upstream address: nginx normally resolves a proxied host name once, and recreating dashboard-api and order-processing together can swap their addresses while both listen on 8080. Mitigation: variable proxy_pass plus 'resolver ... valid=10s'; verified by force-recreating dashboard-api and watching the UI recover.
- A later edit that adds a path or trailing slash to proxy_pass would make nginx decode %2F in saga type names and break those routes without any error. Mitigation: the rule is stated in the template, and verification compares an encoded-slash request through port 4200 with the same request on 5080.
- The demo publishes its ports on all interfaces with a published administrator password and API key. Mitigation: the values are named dev-local-only, the README carries a warning, and seeding applies only to an empty identity store; binding to 127.0.0.1 is raised as the open question.
- Dashboard__TrustedProxies in compose lists whole private ranges because the proxy address is dynamic, so a peer that reaches port 5080 directly can forge X-Forwarded-For and dodge a per-address limiter. Mitigation: the key is off by default outside compose, the documentation says to narrow it for real deployments, and per-account lockout (decision D) must be the control that holds.
- Port 4200 is the Angular default: a dev server left running, from this or another project, makes 'docker compose up' fail with 'port is already allocated'. Mitigation: this project's dev server moves to 4201 and the README names the cause.
- Explicit COPY lists drift when files or projects are added (the SPA Dockerfile's config files, the API Dockerfile's csproj list, which the identity projects will extend). Mitigation: the new CI job builds every image on each push, so drift fails loudly.
- The new CI job adds Docker Hub pulls and roughly six to eight minutes. Mitigation: it is a separate job, so it does not slow the other two; if it proves flaky it can be cut back to compose config validation plus 'docker compose build dashboard-web'.
- Windows checkouts are CRLF on this machine (core.autocrlf=true, no .gitattributes); a shell script copied into a Linux image would not start. Mitigation: the image adds no script and relies on the base image's own envsubst step; if a script is ever added, add a .gitattributes rule with eol=lf first.
- The identity volume and the non-root user must land together: a volume first created by a root-run container stays root-owned, the app user cannot open the database, dashboard-api never turns healthy and order-processing never starts. Mitigation: one commit creates the directory, chowns it, switches user and adds the volume; verification runs 'id' in the container and a down/up cycle.
- The seeded password is fixed at first start: editing Dashboard__Admin__Password later has no effect while the volume holds users. Mitigation: the README volume note says so; 'docker compose down -v' or removing the identity volume re-seeds.

## Open questions
- The demo publishes the dashboard UI (4200) and API (5080) on every network interface, and it will now ship a published administrator login (admin / dev-local-only-change-me). Keep that, as today's API key and RabbitMQ guest/guest already do, or bind the dashboard ports to 127.0.0.1 so only the local machine can reach them? The blueprint assumes the first, with a README warning.
