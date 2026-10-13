# History: a live run over the paths the dashboard work had never exercised (2026-10-13)

> Written fresh. A verification pass over the paths the "Unverified and open" and "Not verified" lists of
> the four dashboard history files (`dashboard-sign-in-and-access.md`,
> `dashboard-guide-mode-and-user-guide.md`, `dashboard-ui-in-compose.md`,
> `timeline-labels-map-jump-and-state-snapshots.md`) and the last paragraph of §13 of
> [`../design/dashboard-usability-and-access.md`](../design/dashboard-usability-and-access.md) name as
> never run, starting from `14b1f7f`, followed by the two performance items §14 deferred. Every value
> below was observed in the run it is listed under. The defects the run found each have a commit with a
> spec and a one-token mutation that breaks it; those commits' messages carry the detail.

---

## How it was run

Real stacks from the repository's own compose files, each under its own `-p` project and ports, one at a
time or side by side: the base stack (`docker compose up -d --build --wait`, default RabbitMQ, port 4200),
and the `http`, `brighter`, `masstransit`, `wolverine`, `redis` and `mongo` overlays on their documented
ports (4600, 4500, 4400, 4300, 4800, 4700). The browser was the Playwright MCP's Chromium, which is also
what everything below says "browser" about; a few checks used a fresh browser context made with
`ignoreHTTPSErrors`, because the TLS proxy's certificate comes from Caddy's own CA. A saga was always
driven through what the sample produces on its own (an order every 8 seconds, 20% of cards declined, 5%
of payments hung, about one in eight loyalty lookups failing, one in ten archivals failing); a few paths
needed a fault the sample never produces, and they are listed with how the fault was made.

Small throwaway tools, kept in the session's scratch directory and not in the repository, did the
repetitive part: a Node client with a cookie jar that signs in, lists, reads a correlation id's sagas and
their timelines, posts a retry and compares every saga type on the correlation id before and after; a
console host that publishes through the real MassTransit, Wolverine, Brighter or RabbitMQ adapter, and
seeds orders at a set rate; and `docker exec` into the Postgres, Redis and MongoDB containers for their own
counters.

## 1. First-run setup on a stack that was not seeded

Stack: the base compose with `Dashboard__Admin__Username` and `Dashboard__Admin__Password` set to empty
strings in an overlay (an empty value is not a seed: `FirstAdministratorSettings` reads it as unset), ports
4200 and 5080.

- The API logged, at Warning, `FirstAdministratorService[7210]`: "No dashboard user exists and
  Dashboard:Admin:* is not set, so first-run setup is open. Open the dashboard and create the first
  administrator with this one-time setup code: LR4V-AEHX-DELG-3GYR . It works until a user exists; a
  restart before then issues a new one".
- `GET /` went to `/setup`: "Set up the dashboard", with username, display name, password (hint: at least
  12 characters), repeat, and the setup code, whose hint names `docker compose logs dashboard-api`.
- A wrong code (`AAAA-BBBB-CCCC-DDDD`) stayed on `/setup` with "The setup code is not correct. Copy it from
  the API log (it was logged at start), or from Dashboard:Setup:Code when that is set." The audit log has
  `user.setup ... invalid_credentials`.
- The right code typed as `lr4v aehx-delg 3gyr` (lower case, spaces, hyphens) was accepted: the session
  that came back belongs to `Setup Admin`, with `sagas.view`, `sagas.data`, `sagas.retry` and
  `access.manage`, `mustChangePassword` false, and the list on `/sagas` showed 15 rows. The first-run
  guide overlay opened over the page by itself and swallowed pointer events until Escape.
- While signed in, `/setup` went to `/sagas`. After signing out it went to `/login`, and
  `GET /api/auth/session` said `setupRequired: false, setupAvailable: false`. Signing in again through
  `/login` worked; the audit log has the sign-in, the sign-out and the second sign-in.
- A second `POST /api/auth/setup` with the same code answered `409 setup_unavailable`, "First-run setup is
  over: a dashboard user already exists. Sign in, or ask an administrator for an account." (My first try
  sent the member `setupCode`, the contract's member is `code`, and got `400 validation`: that was my
  request, not a defect.)
- A restart of `dashboard-api` after the administrator existed logged no setup code and no 7210.

Not exercised: a preset `Dashboard:Setup:Code`, and `ResetOnStart`, which the .NET tests cover.

## 2. A targeted retry, and a `DeliveryExhausted` retry, over each transport

The subject was the sample's `LoyaltyLookupSaga`. It fails about one lookup in eight on its own, it
shares a correlation id with `OrderSaga`, `PostShipmentChoreography` and `InvoiceFollowUpSaga`, and
`PostShipmentChoreography` also consumes the `LoyaltyPointsAwarded` message a retry of it replays. So a
retry that is not targeted would run the step in a saga that must not move, and every run compared all
four saga types on the correlation id (status, state, version, entry count) before and ten seconds after, picking
only a saga whose siblings had all finished. The retry is `POST .../retry` as
the signed-in administrator through the dashboard's own port.

**RabbitMQ (the default).** A `Failed` `LoyaltyLookupSaga` (a business failure, plan `failureKind`
`BusinessFailure`, step `LoyaltyPointsAwarded` from `Start`): 202; the saga's entries went from 7 to 18
and its version from 0 to 2 (it failed again in the same way, since the lookup does); `OrderSaga`,
`PostShipmentChoreography` and `InvoiceFollowUpSaga` unchanged.

**HTTP** (dashboard host on `Transport__Provider=Http` with a wildcard route to the saga host, as the
overlay configures it): the same, 202; entries 6 to 16, version 0 to 2; the three siblings unchanged.

**Brighter**: two defects, below, found before this passed. After both fixes: 202; entries 18 to 29,
version 2 to 4; siblings unchanged; no instance under `00000000-0000-0000-0000-000000000000`. In the
browser on that stack the saga's detail page showed the Retry button and, on click, "Re-run step 1
(LoyaltyPointsAwarded, Start) for this saga only? Other services that consume LoyaltyPointsAwarded still
receive it."; after "Yes, retry" the status region read "Retry accepted — redriving the failed step.", the
timeline grew from 6 to 15 entries and the saga came back `Failed` at version 2 (the lookup fails again),
and `OrderSaga`, `PostShipmentChoreography` and `InvoiceFollowUpSaga` were as before.

**MassTransit and Wolverine, through the dashboard**: `502` with the problem
"Saga 'LoyaltyLookupSaga' instance '...' could not be retried: Publish of 'LoyaltyPointsAwarded' for
correlation id '...' was returned as unroutable: no queue on exchange 'vsaga.saga.events' is bound to
route 'loyalty-points-awarded' ... The saga was restored to state 'Done' with status 'Failed'.",
`restored: true`, and the saga at version 2 with one more entry (`ManualRetryRequested`) after the reset
and the restore. This is the gap `configuration.md` documents: the dashboard host runs the RabbitMQ
adapter, and these two adapters bind PascalCase routing keys and wrap the message. It was not a surprise,
and the restore path was seen live for the first time. The siblings were unchanged.

**MassTransit and Wolverine, through the adapters directly.** The dashboard host refuses these two
providers, so the redrive the retry endpoint publishes was published by a console host that registers the
real adapter (`AddVSagaMassTransit`, `AddVSagaWolverine`) and calls `PublishRawAsync` the way
`SagaEndpoints.RedriveAsync` does, with only the `x-vsaga-target-saga-type` header, after resetting the saga
to `Start`/`Running` with SQL (what `ResetStateAsync` would have done). Wolverine: `LoyaltyLookupSaga`
entries 7 to 13, siblings unchanged. MassTransit: 7 to 16, siblings unchanged. The control, the same
publish without the header: `PostShipmentChoreography` moved (entries 19 to 24 on both, state `Invoiced` to
`PointsAwarded` on Wolverine), so the comparison can see a leak.

**`DeliveryExhausted`.** The sample never produces one by itself. A `CHECK` constraint on `SagaEventLog`
that rejects the `SagaStarted` and `MessageReceived` entries of `InvoiceIssued` for
`PostShipmentChoreography`, added with SQL and dropped again, makes every attempt fail before the message
is recorded. On every transport (RabbitMQ, HTTP, Brighter, MassTransit, Wolverine) the log showed attempts
1/5 to 5/5 and then "after 5 delivery attempts; dead-lettering", the timeline got a `DeliveryExhausted`
entry, and the saga went to `Failed` when it already existed (on Wolverine the first instance did not
exist yet and stayed `Running`; a second run gave a `Failed` one). The plan for such a saga: `failureKind`
`DeliveryExhausted`, step `InvoiceIssued`, `retryable: false`, reason "This saga was recorded before vSaga
stored the message of every step ... The message may also have been dead-lettered before it was recorded
at all."; `POST .../retry` answered 422 with that reason and wrote nothing; all siblings unchanged.

A first attempt, with the constraint on `SagaInstances` instead (the persist of the step fails), gave no
`DeliveryExhausted`: the `MessageReceived` entry was already committed, the redelivered copy was found
by the duplicate check and acknowledged, and the saga stayed `Running` until its stall timeout. So a
`DeliveryExhausted` whose message body was recorded, which the planner would accept, cannot be produced
from outside: a fault after the entry is deduplicated, a fault before it leaves no body. That path rests
on the planner tests.

### The defects this found

1. **`BrighterTransport` built and disposed an `RmqMessageProducer` per publish** (`40ee6c2`). On the
   brighter overlay `order-processing` exited with 139 on five of six starts, with "Hosting failed to
   start ... Connection close forced" thrown by a participant's `SubscribeAsync`; a start that survived
   logged an "Error polling queue ... retrying" and lost a second per publish. Read from the decompiled
   Brighter gateway: disposing any gateway object removes the pooled AMQP connection every producer and
   consumer shares, so each publish closed the connection under the consumers, and the sample's
   `OrderSubmitter` publishes while the participants are still subscribing. One producer for the
   transport's lifetime, sends serialised, replaced on a connection failure.
2. **The Brighter consumer read only Brighter's own `CorrelationId` header** (`47e28a8`). The dashboard's
   redrive, published by the RabbitMQ adapter, arrived with `Guid.Empty`: the engine ran it against a new
   `LoyaltyLookupSaga` instance under the empty id, and the retried instance stayed `Running` in its reset
   state with no new `MessageReceived`, while the endpoint had answered 202.
3. **A plain-HTTP request to an API running with `RequireHttps`** answered 500 with an empty body and an
   unhandled antiforgery exception per request (`e40451a`), found in section 4.

One thing seen once and not reproduced: on a brighter stack that had processed redrives under the empty id
and was rebuilt in place, the consumers of `vsaga.participant.inventory`, `vsaga.participant.payment` and
`vsaga.saga.OrderSaga` each held one unacknowledged message with about 130 ready behind it, and a heap dump
showed all fourteen consume loops idle in `PullConsumer.DeQueue`. Fresh stacks with both fixes ran three
minutes with empty queues, then through the exhaustion and the retry, with empty queues.

## 3. The scoped list on Redis and MongoDB

Stacks: the redis (4800) and mongo (4700) overlays, with a user `scoped1` holding Viewer for
`OrderSaga`, `LoyaltyLookupSaga` and `InvoiceFollowUpSaga` only (created through `POST /api/admin/users`),
first with the sample's own volume and then with 4,000 orders seeded through the RabbitMQ adapter on each
(about 5,500 sagas on Redis, about 6,600 on MongoDB).

- The saga-type dropdown listed those three types. The default list, the type filter, a status filter, and
  pages 2 and 3 at 25 rows each showed rows of those types only. `sagaType=PostShipmentChoreography` gave
  an empty list.
- With the sample's volume the API answered, as the design says: `sortBy=Status&pageSize=100&page=6` with
  `400 { error: "Page 6 is past the last page (5) a list across several saga types sorted by status,
  filtered by status or kind, or searched can reach at 100 rows per page. Choose a saga type with the
  sagaType filter to page further.", maxPage: 5 }`, and `pageSize=100&page=101` with `maxPage: 100`.
- In the browser a deep link to page 6 of 100 sorted by status showed the server's sentence in a banner
  and moved the URL to the last real page: page 3 with 13 rows on Redis (213 rows then), page 5 with 100
  rows on MongoDB (823 rows). Page 101 of 100 went to page 3 (Redis) and page 9 (MongoDB). The pager never
  offered a page past the real count.
- Live push, recorded as the WebSocket frames the page received in 45 seconds: `scoped1` on Redis 10
  `SagaUpdated` frames, `OrderSaga` 5, `InvoiceFollowUpSaga` 3, `LoyaltyLookupSaga` 2; `admin` on the same
  stack in the same window 52 frames over seven types. `scoped1` on MongoDB 20 frames, those three types.
  The "N new sagas since this page loaded - Refresh" banner appeared for `scoped1`.
- With the sample paused, a walk of the whole scoped list through the API: Redis 285 rows in 12 pages of
  25, MongoDB 823 in 33, no repeats, newest first; a sort by status at 100 per page stopped with the 400
  at page 6 after 500 rows on MongoDB (the bound) and walked all 285 on Redis. With the sample running one
  row repeated in a 33-page walk, as offset paging gives when a row is updated between two reads.

Not reached: `maxPage: 0` (a list that needs more than 10 visible types that have run; the sample has seven)
and the SPA's return to the last good page *with rows on screen* for that case.

## 4. TLS behind a trusted proxy, and the time-based session limits

Stack: the base compose plus Caddy 2 (`--internal-certs`) on `https://localhost:8443` in front of
`dashboard-web`, which had `DASHBOARD_OUTER_PROXY=true`; the API with `Dashboard__Session__RequireHttps=true`
and the compose `Dashboard__TrustedProxies`, `IdleTimeoutMinutes=2` and `AbsoluteTimeoutHours=1`.

- Through the proxy, in Chromium: signed in; the cookies were `__Host-vsaga.session.vsaga-tls` and its
  `.af` antiforgery companion (Secure, HttpOnly, SameSite=Strict, path `/`, host-only) and `XSRF-TOKEN`
  (Secure, SameSite=Strict); the hub opened over `wss://localhost:8443/hubs/saga`.
- **Defect**: a request to the API's own published port over plain HTTP answered `500` with an empty body
  and logged an unhandled `InvalidOperationException` ("AntiforgeryOptions.Cookie.SecurePolicy = Always,
  but the current request is not an SSL request") per request. `e40451a` answers `400 https_required`
  (Warning 7312), and leaves `/health` on plain HTTP alone.
- `Strict-Transport-Security: max-age=2592000` was sent for a host named `vsaga.test` and not for
  `localhost`, which ASP.NET Core's HSTS middleware excludes with `127.0.0.1` and `[::1]`; `dashboard.md`
  says so now.
- **Idle expiry.** Signed in at 14:44:10Z, the sample paused so no request was made: the hub closed with
  code 1000 after 119.57 s and the tab moved to `/login?returnUrl=%2Fsagas&reason=expired`, "Your session
  expired. Sign in again to continue." A second sign-in with the sample running ended the same way after
  120.5 s: the live list applies pushes without a request, so a tab that only receives pushes is idle as
  far as the session is concerned.
- **Absolute lifetime.** A third sign-in at 15:27:16Z, with a request from the page itself every 75 seconds
  (`GET /api/auth/session`, injected into the tab, so the sliding idle window of 2 minutes kept renewing; 48 of
  the 49 answers were `authenticated: true`). The hub closed and reconnected 49 times, about every 75 seconds,
  and the last close came at 16:27:16.338Z, exactly one hour after sign-in and only 29.98 s after that
  connection opened (a connection opened that late is capped at the absolute expiry, not at the idle one). The
  reconnect, 5 ms later, closed with 1006 after 33 ms (the negotiate answered 401), the tab went to
  `/login?returnUrl=%2Fsagas&reason=expired` ("Your session expired. Sign in again to continue."), and the
  keepalive requests after it answered `authenticated: false`. So the absolute lifetime ended an active session
  that the idle window alone would never have ended.
- **Start-up with `RequireHttps` and no trusted proxy**: the API container exited at once with "Dashboard:Session:RequireHttps
  is true but Dashboard:TrustedProxies is empty: behind a TLS-terminating proxy the API sees plain HTTP unless it
  trusts that proxy's X-Forwarded-Proto. ..."

## 5. Part B, the two performance items

Measured on the redis, mongo and base-stack Postgres, before and after, in the commit messages
(`e92a643`, `a040508`); the method is the one `persistence.md` uses for its figures, the stores' own
counters on the overlays (Redis `INFO commandstats`, MongoDB `serverStatus` counters), 30 requests per
shape with the sample paused, and `EXPLAIN (ANALYZE, BUFFERS)` on a 400,400-row copy of `SagaInstances`.

- `SagaListFilter.SagaTypes` and a single provider call for a scoped list: on Redis a page of 100 at row
  3,900 went from 13 range reads and 4,200 hash reads (10.4 ms of server time) to 3 and 100 (1.7 ms), a
  sort by status from 21 `ZINTER` calls (3.3 ms) to 7 range reads (1.5 ms); on MongoDB 6.1 queries per
  request became 2.2 and the same deep page examined 9,213 index keys instead of 69,743. A search on Redis
  is unchanged. One variant measured slower (a server-side `ZUNION` of the type indexes, 2.9 ms against
  1.1 ms for three range reads) and was not kept.
- `(SagaType, UpdatedAtUtc)` on `SagaInstances`: page 100 of one type from 42,860 buffers and 54.7 ms to
  2,533 and 3.9 ms; page 1 from 481 buffers to 38; no change for a list across several common types.

## What was not exercised

- A screen reader, and any browser other than the Playwright MCP's Chromium.
- A retryable `DeliveryExhausted` retry (see section 2).
- A retry through the dashboard over MassTransit and Wolverine that reaches the saga host: the dashboard
  host does not run those adapters; the redrive was exercised through the adapters directly.
- `maxPage: 0` in a browser (section 3), and a preset setup code.
- The age window that turns a "pending" snapshot into "missing", and the 1500 ms follow-up timeline
  fetch, which this run did not touch.
- The three open questions of §13 (the User guide link, the identity key ring, the Vitest `splitting`
  notice), which it was not asked to change.
