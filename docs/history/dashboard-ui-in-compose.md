# History: the dashboard UI joins compose

> Written fresh. Describes the packaging slice of the dashboard usability and access work, seven
> commits on 2026-10-02 (`c99a547`, `9d79b93`, `d242292`, `2fd08a9`, `899ce75`, `736a76d`, `ef9e09a`),
> following §4 of [`../design/dashboard-usability-and-access.md`](../design/dashboard-usability-and-access.md).
> See [`../dashboard.md`](../dashboard.md#how-it-is-served) and
> [`../configuration.md`](../configuration.md) for the current reference documentation. Every observed
> value below comes from the live runs and mutation checks recorded while the slice was built, or from
> the commit messages themselves.

---

## What was built

Before this slice the SPA baked `http://localhost:5080` into its bundle and called the API
cross-origin, so the demo needed Node on the host, `ng serve` on 4200, an edit to `api-config.ts` to see
an overlay's dashboard, and a credentialed CORS policy that fell back to `http://localhost:4200` when
`Dashboard:WebOrigin` was unset. After it, `docker compose up -d --build --wait` serves the UI on
`http://localhost:4200` from its own container, same-origin with the API it proxies.

- **Same-origin SPA** (`c99a547`). `API_BASE_URL` is `''` and `HUB_URL` is `/hubs/saga`;
  `api-config.spec.ts` pins both. `proxy.conf.mjs` makes `ng serve` forward `/api` and `/hubs` (WebSocket
  upgrades included) to `VSAGA_API_URL`, default `http://localhost:5080`, and the dev server moved to
  4201: it starts with `strictPort`, so on 4200 it would fail whenever the containerised UI is up.
- **CORS opt-in** (`9d79b93`). New `Hosting/DashboardEdge.cs` is the only reader of the edge keys.
  `Dashboard:WebOrigin` defaults to empty (no CORS); otherwise it must be an absolute http or https URI
  with no user info, path, query or fragment, normalised to the form a browser sends in `Origin`, or
  startup fails naming the key. There is no wildcard. The commit gives the reason for dropping the old
  fallback: browsers scope cookies by host, not port, so a credentialed policy for a fixed localhost
  port would let whatever runs there read the API with the user's session once session cookies exist.
- **The `dashboard-web` image** (`d242292`). A `node:22-bookworm-slim` build stage and an
  `nginxinc/nginx-unprivileged:1.30-alpine-slim` runtime stage (uid 101, port 8080), with no shell script
  of our own: the base image's entrypoint renders `nginx/default.conf.template`. `/api/` and `/hubs/` are
  proxied through a variable plus a resolver, so the upstream is resolved per request and the UI survives
  the API being recreated; `proxy_pass` carries no URI part, so an encoded saga type such as
  `Order%2FSaga` reaches the API still encoded. `X-Forwarded-Proto` is nginx's own scheme unless
  `DASHBOARD_OUTER_PROXY=true`. Security headers and a CSP that allows only same-origin scripts sit at
  server level; `index.html` revalidates, hashed bundles cache for a year and a missing bundle is a 404.
  The access log records `$uri`, never the query string. `STOPSIGNAL SIGTERM`, because the base image's
  graceful SIGQUIT makes `docker compose down` wait out open WebSockets.
- **The UI in compose** (`2fd08a9`). `docker-compose.yml` gains `dashboard-web`, healthy on nginx's
  `/healthz` after `dashboard-api` is healthy. Both dashboard ports bind to loopback
  (`127.0.0.1:4200:8080`, `127.0.0.1:5080:8080`), because the demo ships a known credential. Every
  overlay keeps "UI port = API port minus 880" as a `!override` list: wolverine 4300, masstransit 4400,
  brighter 4500, http 4600, mongo 4700, redis 4800; chaos stays on 4200.
- **Forwarded headers** (`899ce75`). `Dashboard:TrustedProxies` is a comma-separated list of addresses or
  CIDR networks, empty by default, with no `*` and no host bits set, and IPv4 entries only in canonical
  dotted-decimal form (the parser would otherwise read `10` as `0.0.0.10` and `10.0.0.010` as octal).
  `UseForwardedHeaders` runs only when a proxy is listed, with `ForwardLimit = 1` and both default lists
  cleared so loopback is not trusted implicitly. A rate-limited Warning names an untrusted peer that
  sends forwarded headers: once per peer per five minutes, at most 20 per window overall. Compose trusts
  `10.0.0.0/8,172.16.0.0/12,192.168.0.0/16`, because nginx's address is assigned dynamically.
- **A compose job in CI** (`736a76d`). "Compose build & smoke" renders the base file and every overlay and
  asserts with jq the two loopback ports per combination, builds the images, runs `nginx -t` through the
  image's entrypoint, brings the stack up with `--wait` and smoke-tests the UI origin on 4200.
- **Documentation** (`ef9e09a`). README "Run the demo" became one command and a URL; `docs/dashboard.md`
  gained "How it is served" and "Behind your own proxy or TLS"; `docs/configuration.md` documents
  `Dashboard:WebOrigin`, `Dashboard:TrustedProxies` and the container environment variables.

## How it was verified

**Tests and mutations.** `DashboardEdgeTests` covers settings parsing, CORS through the real composition
root and forwarded headers on a minimal `TestServer` app that sets the peer address itself; the warning's
limits run on a fake clock. On `ef9e09a`, before any mutation, `VSaga.Dashboard.Api.Tests` passed 111/111
and `VSaga.Http.Tests` 13/13. Each mutation of a tracked file was a `sed -i` (one token, except the CORS
mutation's two edits and the four-line replacement for the malformed-entry case), undone with
`git checkout --`. The .NET mutants were fully rebuilt (0 warnings, 0 errors) and tested without
`--no-build`; the SPA mutant ran `ng test`; the nginx mutants were rebuilt into the image and run through
the CI smoke scripts against a live stack; the overlay mutant edited a scratch copy, run through the CI
port check:

| Mutation | What failed |
| --- | --- |
| CORS policy and `UseCors` always on, with the old default origin | `Cors_WhenWebOriginIsUnset_SendsNoCorsHeaders` (got `Access-Control-Allow-Origin: http://localhost:4200`) |
| `ForwardLimit = 1` → `2` | `ForwardedHeaders_WithSeveralHops_HonourOnlyTheLast` (expected `http 198.51.100.9`, got `https 203.0.113.7`) |
| `KnownIPNetworks.Add` → `Remove` (the middleware then trusts every peer) | three `ForwardedHeaders_FromAnUntrustedPeer_AreIgnored` cases (192.0.2.10, 127.0.0.1, ::1) |
| `KnownProxies.Clear()` deleted | the `::1` case of the same test |
| A malformed proxy entry skipped instead of thrown | all 12 `Read_WithAMalformedTrustedProxy_ThrowsNamingTheEntry` cases |
| The canonical-IPv4 check never applied | the `10`, `10.0/8`, `10.0.0.010`, `192.168.001.010` and `0x0A.0.0.1/32` cases |
| Per-peer warning limit inverted; overall cap never reached; mapped peer not shown as IPv4 | the per-peer and window tests; the cap test; both `LogOneWarningPerPeer` cases |
| `API_BASE_URL = 'http://localhost:5080'` | both `api-config.spec.ts` tests (148 of 150 SPA tests still passed) |
| nginx `proxy_pass $dashboard_api/;` | the CI smoke step, already at `/api/saga-types` (404, expected 200), and its encoded-slash `/timeline` comparison |
| nginx `proxy_pass $dashboard_api$uri;` (only the decoding changes) | only the encoded-slash `/timeline` comparison: `through http://localhost:4200:  \| 404; direct: [] \| 200` |
| `!override` removed from the mongo overlay's `dashboard-web` ports (scratch copy) | the CI port check: `::error::mongo: expected dashboard-api 127.0.0.1:5580 and dashboard-web 127.0.0.1:4700, got 127.0.0.1:5580 127.0.0.1:4200,127.0.0.1:4700` |

Every mutation failed only its own tests or CI step, and the tree was clean after each restore. At the end
of the slice the SPA gates ran under Node 22 (22.23.3 locally), the major version CI uses: `npm ci`,
`npm audit` (0 vulnerabilities), `ng build` with no warning and an initial total of 403.83 kB, the CI
`index.html` guard, and `ng test` (10 files, 150 tests passed).

**The base stack, twice.** The first live run used the compose commit before the nginx `error_log` fix
was folded into it; the second ran on `2fd08a9` as committed. Both brought five services up in about 19
seconds:

```
dashboard-api      Up 11 seconds (healthy)   127.0.0.1:5080->8080/tcp
dashboard-web      Up 5 seconds (healthy)    127.0.0.1:4200->8080/tcp
order-processing   Up 5 seconds
postgres           Up 18 seconds (healthy)   0.0.0.0:5433->5432/tcp, [::]:5433->5432/tcp
rabbitmq           Up 18 seconds (healthy)   0.0.0.0:5672->5672/tcp, ... 0.0.0.0:15672->15672/tcp ...
```

`curl -sI http://localhost:4200/` answered 200 with `Server: nginx` (no version), `Cache-Control:
no-cache`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: same-origin` and
the CSP with `connect-src 'self' ws://localhost:4200 wss://localhost:4200`. The bundle named in
`index.html`, `main-UPBNUSYT.js`, came back with `Cache-Control: max-age=31536000`; `/gone-ABCDEFGH.js`
was a 404; a deep link to `/sagas/OrderSaga/<zero guid>` returned 200 with `<app-root>`. From the
machine's LAN address (192.168.178.40) both 4200 and 5080 refused the connection (curl exit 7).
`docker compose exec dashboard-web id` printed `uid=101(nginx)`.

`/api/sagas/Order%2FSaga/<zero guid>` with the API key answered 404 with the same empty body through 4200
and straight from 5080 (`cmp`: identical). The negotiate through 4200 returned a connection token, and a
WebSocket upgrade with it answered `HTTP/1.1 101 Switching Protocols` and stayed open until curl's
`--max-time 3` cut it.

In Chromium (Playwright) `http://localhost:4200/sagas` listed the existing volume's sagas ("25814 total",
25 rows, the seven saga types in the filter), and a row opened its detail page with the map rendered.
Across both pages the console held two messages, both Information, `Normalizing '/hubs/saga' to
'http://localhost:4200/hubs/saga'` and `WebSocket connected to ws://localhost:4200/hubs/saga?...`: no
error, no warning, no CSP violation. Every API request went to `http://localhost:4200` and answered 200; the
socket's 101 is in nginx's access log (`"GET /hubs/saga HTTP/1.1" 101 5329 23.937`).

`docker compose up -d --force-recreate dashboard-api` with a polling loop on `/api/saga-types` through
4200: one 502, then 200 one second later in the second run (502 at +0.4 s, 200 at +1.4 s in the first).
`dashboard-web` kept its container id, start time and zero restarts. In the first run the open tab's
SignalR client logged 502s on negotiate during the recreate and then had a live socket again, which
closed with 1006 when the stack went down 30 seconds later.

**An overlay, and two stacks side by side.** The Wolverine overlay (`-p vsaga-wolverine`) came up healthy on
`127.0.0.1:5180` and `127.0.0.1:4300`, and `/api/sagas?page=1&pageSize=3` through 4300 and 5180 returned
the same three ids and `"totalCount":37`, while the base stack held over 25 800. Then the MongoDB overlay
ran beside the base stack:

```
4700: 9ec29f41-..., 4c0ae3a6-...  "totalCount":633
5580: 9ec29f41-..., 4c0ae3a6-...  "totalCount":633
4200: c4a42c3b-..., 409f9d53-...  "totalCount":25878
5080: c4a42c3b-..., 409f9d53-...  "totalCount":25878
```

The MongoDB stack's API had `Persistence__Provider=MongoDb`; the base one had none. In the browser 4700
showed "635 total" and 4200 "25887 total" at the same moment, each with a WebSocket to its own origin and a
clean console. Each nginx reached its own project's `dashboard-api` with no overlay configuring it.

**Forwarded headers through compose** (`899ce75`). The rendered `Dashboard__TrustedProxies` was
`10.0.0.0/8,172.16.0.0/12,192.168.0.0/16`. On Docker Desktop every host request, curl and browser alike,
reached nginx from the bridge gateway: the access log's `$remote_addr` was `172.23.0.1` on all 18 lines,
nothing else (network `172.23.0.0/16`, `dashboard-web` at 172.23.0.6, `dashboard-api` at 172.23.0.4).
Both addresses lie in `172.16.0.0/12`. Three configurations of the API, then the committed one again:

- Committed config: requests through 4200, and straight to 5080 with `X-Forwarded-For: 203.0.113.7` and
  `X-Forwarded-Proto: https`, all 200, zero untrusted-peer warnings.
- `Dashboard__TrustedProxies: ""`: exactly two warnings, one per peer, first logged as
  `from ::ffff:172.23.0.6` (nginx) and `from ::ffff:172.23.0.1` (the host on 5080). After the change that
  displays mapped peers in IPv4 form: `from 172.23.0.6, which is not listed in Dashboard:TrustedProxies`.
- `Dashboard__TrustedProxies: "10.0.0.0/8, proxy.internal"`: the container exited at start (code 139) with
  `System.InvalidOperationException: Invalid Dashboard:TrustedProxies entry 'proxy.internal': expected an
  IP address such as 10.0.0.5 or a CIDR network such as 10.0.0.0/8 with no host bits set, ...`.
- Committed config again: healthy, requests through 4200 and a direct one with `X-Forwarded-For` all 200,
  zero warnings.

With the committed config the browser list updated live (26043 to 26056 over 15 seconds without a
reload). A live push on a detail page was not observed: the sample's sagas finish within about a second
and `status=Running` returned none.

**The CI job, run locally** (`736a76d`; Docker 29.6.1, Compose v5.2.0). Each step's `run:` body was
extracted from `ci.yml` and executed as-is, with jq through `docker run --rm -i ghcr.io/jqlang/jq`. The
port check printed the seven expected pairs and exited 0; scratch copies with a missing `!override`, a
missing loopback prefix and a wrong UI port made it exit 1 with, for example, `::error::noloop: expected
dashboard-api 127.0.0.1:5180 and dashboard-web 127.0.0.1:4300, got :5180 127.0.0.1:4300`. The build came
from cache, `nginx -t` through the entrypoint reported `syntax is ok` and `test is successful` with only
the `dashboard-web` run container created, `up --wait` took 18 seconds, and the smoke step exited 0
(index.html for a deep link, 200, 404, the two encoded-slash comparisons, a connection token, 101). Without
the key `/api/saga-types` through 4200 answered 401. A `dashboard-web` container with `proxy_pass`
mutated to `http://dashboard-api:8080/api/;` passed the first encoded-slash comparison and failed the
second (`through 4299: 404; direct: [] | 200`), and an upgrade with `id=bogus` answered 404, so the 101
assertion discriminates.

## Problems found along the way

- **The production build emitted an inline event handler.** Critical-CSS inlining writes the stylesheet
  link with `onload="this.media='all'"`, which the CSP forbids, so styles would not have applied in the
  container. `d242292` turned `inlineCritical` off and spelled out every other optimization key (omitted
  keys are coerced to false and would ship unminified bundles); the main bundle stayed
  `main-UPBNUSYT.js`, 402.95 kB. Because `ng serve` has no CSP, the Angular CI job now fails on an inline
  script or `on*=` handler in the built `index.html`, after first testing that the file exists.
- **nginx's error log leaked the API key.** The first live run of the compose commit found three
  `[error]` lines in `docker compose logs dashboard-web` while `dashboard-api` was recreated, one reading
  `request: "GET /hubs/saga?id=...&access_token=dev-local-only-change-me HTTP/1.1"`. The `$uri` log
  format covers only the access log; the error log quotes the request line and upstream URL on upstream
  failures. The server block now sets `error_log /var/log/nginx/error.log crit;`, folded into `2fd08a9`
  before it was final. Re-checked with 400 rounds of hub, negotiate and `/api` requests carrying
  `access_token` while the API was recreated, stopped and started (770 × 200, 385 × 404, 24 × 502,
  21 timeouts): 1231 log lines, none with a `?` or the key. The failures show as 502 and 499 lines with
  the path alone; the only error-level lines left were 11 resolver messages naming no request. The cost
  is that nginx no longer says why a 502 happened. The second live run found no `?` in 79 log lines.
- **IPv4-mapped peers.** Kestrel reports container peers as `::ffff:a.b.c.d`, and the first warnings
  named them that way; the warning now shows the IPv4 form. `TrustsPeer` had an explicit fallback for
  mapped peers, but replacing it with a no-op failed nothing, and a pwsh check on .NET 10.0.12 showed why:
  `IPNetwork.Parse("172.16.0.0/12").Contains(IPAddress.Parse("::ffff:172.18.0.4"))` is True. The fallback
  was removed as redundant and the mapped-peer test kept as a behavioural pin.
- **The encoded-slash check could not fail on its own.** If nginx decoded `%2F`, the decoded path would
  also answer 404, so comparing that path through both ports passes either way. `736a76d` adds the same
  comparison for the `/timeline` sub-path, which the API answers 200 for any saga type while the decoded
  path matches no route.
- **The CI job's `down -v` is not safe on a contributor's machine.** It would delete the local saga data
  volume, so CONTRIBUTING says to run the job's steps after stopping any demo stack and to finish with a
  plain `docker compose down`. The slice's own local re-run of the committed job on `736a76d` did just
  that: its `down -v` step removed `vsaga_vsaga-postgres-data`, left over from the forwarded-headers run
  (`Volume vsaga_vsaga-postgres-data Removing` / `Removed`; `docker volume ls` showed no `vsaga_` volume
  afterwards).
- **Trusting the private ranges covers the host too.** Because a host request to 5080 also arrives from
  172.23.0.1, inside `172.16.0.0/12`, any process on the machine that can reach 127.0.0.1:5080 can set
  `X-Forwarded-For` and `X-Forwarded-Proto` and have them applied. This was not demonstrated from a
  response (no endpoint echoes the address or scheme); it follows from the range membership and the
  absent warning. It is limited to the local machine by the loopback bindings, and `ef9e09a` says in
  `docs/configuration.md` and the README that a real deployment narrows the list to its proxy.

## Not verified

The design's verification list for this slice also named the following; none of it was observed in the
recorded runs.

- `ng serve` on 4201 beside the containerised UI.
- `id` printing 1654 in the API container. The identity volume and the API runtime user belong to the
  authentication work, not to this slice.
- That `docker compose down` returns promptly in every case. The first run's base-stack `down` took
  13.5 s with a WebSocket open on 4200, and which container used that time was not measured; later, with
  a socket open again, the services stopped one by one in 0.8 s (`dashboard-web`), 0.6 s, 0.9 s, 2.1 s and
  0.7 s, and the second run's `down` took 3.6 s.
- A UI recovery time of "about 10 s" in the browser. The API answered through 4200 again within 1.4 to
  2 seconds of the recreate; the browser's reconnect was seen only indirectly, as a later 1006 close.
- The masstransit, brighter, http, redis and chaos stacks running live. Their rendered port
  configuration was checked by `docker compose config` and the CI port check only.
