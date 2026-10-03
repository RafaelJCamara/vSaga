# History: dashboard sign-in, access control and the identity store

> Written fresh. Describes the authentication slice of the dashboard usability and access work: commits
> C31 to C51 of the commit sequence in §12 of
> [`../design/dashboard-usability-and-access.md`](../design/dashboard-usability-and-access.md), with the
> prerequisites and the review and live-check follow-ups that landed beside them. That is 45 commits on
> 2026-10-02 and 2026-10-03 (`e9eea61` to `fdc1685`), following §8 of the design and
> [ADR 0006](../adr/0006-dashboard-authentication-and-identity-store.md). See
> [`../dashboard.md`](../dashboard.md#authentication) ([Access control](../dashboard.md#access-control),
> [Live updates](../dashboard.md#live-updates-signalr), [The identity store](../dashboard.md#the-identity-store)),
> [`../configuration.md`](../configuration.md#dashboard) and
> [`../observability.md`](../observability.md#dashboard-log-events) for the current reference documentation.
> Every observed value below comes from the live runs and mutation checks recorded while the slice was
> built, or from the commit messages themselves. Where a claim rests on neither, it is said so.

---

## What was built

Before this slice the dashboard had one shared API key. The SPA embedded it in its bundle, sent it on every
request and as the hub's `access_token`, and whoever held it could read every saga and retry any of them.
After it, people sign in with a cookie session; what they may do is decided by roles and grants that can be
limited to named saga types; their accounts live in a SQLite store the dashboard owns, whatever persistence
provider the sagas use; an Administration area in the SPA manages users, teams and roles; and the key
survives for machines, as a read-only Viewer by default.

The 45 commits are the 21 numbered ones (C31 to C51), 20 follow-ups that fixed what reviews and live checks
found in them, three prerequisites (the Angular 22 move, its follow-up, and the unit-test isolation) and the
handoff record `7bf2e4f` that paused the work at C40. The paragraphs below follow the numbering.

**The server: identity, sign-in and enforcement (C31 to C39).**

- **The identity project** (C31, `e9eea61`). `VSaga.Dashboard.Identity` holds store-neutral records (user,
  team, role, grant), the four-permission catalogue (labels, scopability, implications) and the three
  built-in roles under fixed ids, so a grant written today names the same role after any upgrade. The store
  contract is aggregate-shaped (every read returns a whole user, team or role; nothing exposes a query or a
  join) and spanning invariants such as "the last administrator" are checked by the services inside an
  exclusive write scope, which on SQLite is `BEGIN IMMEDIATE`. The EF Core store keeps names unique through
  trimmed, invariantly upper-cased `Normalized*` columns, because SQLite's `NOCASE` folds ASCII only, and
  counts failed sign-ins with one `UPDATE` computed from the row's own count, so concurrent failures never
  lose an increment and exactly one crosses the threshold. `DashboardUser` overrides the record's `ToString`
  so a user that reaches a log line carries neither its password hash nor its security stamp.
- **The services** (C32, `69c993f`). Password policy (at least `Dashboard:Password:MinLength` characters, default 12 and
  settable from 8 to 128, at most 128; not the username, not the password it replaces; no composition rules); a credential verifier that does the same hashing work for an
  unknown, disabled or locked account as for a real one (a dummy hash computed once at start) and, when a
  right password needs a rehash, re-reads the user under the exclusive scope so a rehash can never undo an
  administrator's concurrent reset; an access evaluator that turns grants into effective access (union of the
  user's grants and their teams', `sagas.view` implied for the same scope, `access.manage` only from a grant
  for all saga types, nothing for a disabled user or one who must change their password); and an
  administration service that runs each mutation inside one exclusive scope, checks the proposed snapshot
  (an enabled user must still hold `access.manage` for all saga types), and only after the commit writes
  the audit event and tells the observer whose access changed. Audit events 7100 and 7101 never carry a
  password, hash or stamp.
- **Start-up and health** (C33, `e6228c3`). `VSaga.Dashboard.Identity.Sqlite` holds the generated
  `InitialCreate` migration and nothing else; the API now registers two EF Core contexts, so every
  `dotnet ef` command needs `--context`. `IdentityStartup` never throws: it creates the directory and an
  empty file ahead of SQLite so they are born 0700 and 0600 on Unix, migrates, writes the built-in roles from
  code, and on failure stays not ready with a reason an operator can act on (a stale `__EFMigrationsLock` row
  becomes a 30 s timeout naming the table). The identity health check runs Degraded-on-failure and waits at
  most 2 s for an attempt, well inside compose's 5 s probe timeout, so a broken identity store costs sign-in
  and nothing else: `/health` answers 200 with `identity` degraded and the saga views and the API key keep
  working. The Data Protection key ring lives in the identity store, and while the store is not ready its
  repository throws instead of answering empty, because an empty answer would make the key manager mint a
  key in memory and issue cookies that die with the process.
- **Schemes, policies and problem bodies** (C34, `8b7a39a`). A policy scheme, `Dashboard`, forwards a request
  that presents an API key (`X-Api-Key` or `Authorization: Bearer`, and `access_token` on a hub endpoint
  only, recognised by its `HubMetadata` after routing) to the key handler and everything else to the cookie
  handler. Both write one 401 body. The fallback and default policies require an authenticated caller, so an
  endpoint mapped without stating its access is protected, not open. A session needs an existing, enabled
  user and the stamp it was issued under, so revocation is immediate; the key acts as `Dashboard:ApiKeyRole`
  (Viewer by default), never with `access.manage`. `Cache-Control: no-store` and `nosniff` go on every `/api`
  response, 401s included.
- **Sign-in** (C35, `ed8f939`). `GET /api/auth/session` (anonymous), `POST /api/auth/login`, `/logout` and
  `/password`. Every login failure is one `401 invalid_credentials` body, completed no earlier than 300 ms
  (plus up to 50 ms of jitter) after the request started, so the store write a real account's failure costs
  does not show in the timing. A wrong current password on a password change is a 400 with
  `errors.currentPassword` (not a 401, which the SPA reads as signed out), counts against the same failure
  counter as sign-in and, at the threshold, ends the session on the server by rotating the stamp. Antiforgery
  is decided per endpoint after routing: every unsafe method needs a valid `X-XSRF-TOKEN` unless the endpoint
  carries the exemption marker, which only the hub has, and a failure is `400 antiforgery`. Rate limits are
  taken by the endpoints, not the middleware, because the key includes the username from the body: 20 a
  minute per client address and hashed username, plus a global concurrency limiter around password hashing.
  Bodies are read with `JsonUnmappedMemberHandling.Disallow` (an unknown member is a 400 naming it) and
  capped at 16 KiB.
- **The first administrator** (C36, `82c4797`). `Dashboard:Admin:Username` and `:Password` seed a user into
  an empty store; otherwise a one-time setup code (16 characters in four groups of four from an alphabet
  without look-alikes, 80 bits) is logged once at Warning, or `Dashboard:Setup:Code` presets one, and
  `POST /api/auth/setup` claims the dashboard with it. Seeding fails closed: with either seed key set, setup
  is never opened, and a seed that cannot be applied leaves setup closed, degrades the health check with the
  reason and logs at Error. `Dashboard:Admin:ResetOnStart=true` is the break-glass for a lost administrator
  that does not cost every other user. The session response gained `setupAvailable` and `setupProblem`.
- **Persistence and packaging** (C37, `f25b1ec`). The identity file sits on a named volume
  (`/var/lib/vsaga-dashboard/identity.db`), the API image runs as uid 1654 instead of root, and compose seeds
  `admin` / `dev-local-only-change-me` into an empty volume. The session cookie is named
  `vsaga.session.${COMPOSE_PROJECT_NAME:-vsaga}` so two stacks on one machine do not overwrite each other's
  sign-in (cookies ignore ports). `Dashboard__ApiKeyRole` was `Operator` until C46 so the UI's embedded key
  could still retry.
- **Enforcement** (C38, `e69deb8`). Every saga endpoint names its permission policy. Without `sagas.data` for
  the saga's type the detail's `dataJson` is null and the timeline and map go through the redaction seams of
  the earlier slice with `includeData` false. The retry audit actor is `dashboard:<username>` or
  `dashboard:api-key`, a name no user can take. The list goes through a new `ScopedSagaLister`, because the
  persistence readers filter by at most one saga type: a merge of one stream per visible type, bounded by the
  request shape (the rank-served shape, an `UpdatedAt` sort with no status or kind filter and no search, allows
  50 types and `page x pageSize` up to 10,000; every other shape 10 types and one 500-row chunk, because on
  Redis those read every member of every visible type), a dedupe set for a row that moves between two reads,
  and a final scope filter so a provider quirk cannot widen the result. Past a bound the endpoint answers
  `400 { error, maxPage }`.
- **Administration endpoints** (C39, `8d69429`). `GET /api/admin/permissions` and CRUD on
  `/api/admin/{users,teams,roles}` plus `POST /users/{id}/password` and `/unlock`, all behind `access.manage`
  for all saga types (so a scoped administrator gets 403, and so does the API key, even when
  `Dashboard:ApiKeyRole` names Administrator). The API records are the canonical wire contract the SPA
  adopted (`isEnabled`, `isBuiltIn`, `lastSignInAtUtc`, arrays always present); team membership is written
  only through the team; every request record rejects unknown and duplicate members, case variants included.
  Golden JSON fixtures, one per request and response type plus the validation and conflict problems, live in
  `dashboard-web/src/app/testing/contracts/admin/`; the .NET tests post each request fixture through the real
  endpoint and compare each response with its fixture, and the SPA later asserts the same files.

**The hub and CI (C40, C41).**

- **The hub** (C40, `852a901`). `SagaHub.SubscribeToList` and `SubscribeToSaga` return `Task<bool>`, resolve
  the caller afresh from the identity store on every call and never throw on denial. A caller who may view
  every type joins `saga:list`; one scoped to named types joins one `saga-list:{sagaType}` group per type,
  types that have not run yet included, so nothing has to be re-subscribed when a new type first runs. The
  hub is mapped with `RequireAuthorization()` and `CloseOnAuthenticationExpiration`. `HubConnectionRegistry`
  is the access-change observer that ends the affected users' connections. `HubOriginGuard` refuses a hub
  request whose `Origin` is neither absent (not a browser), the request's own origin, nor `Dashboard:WebOrigin`,
  with a Warning (event 7311) naming the received and expected values.
- **A CI step for sign-in** (C41, `64ba0ac`, `4dde4b1`). The `compose` job proved the UI origin with the API key
  only, which keeps working however sign-in breaks. One step after the existing smoke test, through the same
  origin on 4200, now asserts: `/health` lists `identity` healthy; the identity path named by
  `docker compose config` lies under a named volume mounted on `dashboard-api`; an anonymous
  `GET /api/sagas` is 401; `GET /api/auth/session` sets `XSRF-TOKEN`; `POST /api/auth/login` signs the
  seeded administrator in; `GET /api/sagas` with the cookie is 200; negotiating the hub with
  `Origin: http://localhost:4200` returns a `connectionToken` and with `Origin: http://localhost:9999` is 403.

**The SPA (C42 to C50).**

- **Shared styles** (C42, `81e3006`). The banner, button, form, table, chip, menu and `:focus-visible`
  primitives the sign-in and administration pages need moved once into `styles.scss`, so each lazy page does
  not copy them into its 4 kB component stylesheet.
- **The hub service learns to stop** (C43, `fabc67c`). `stopAndReset()` (which never rejects), `resume()`, a
  session probe consulted after a failed first start and by the reconnect policy (now inside the class),
  generation checks after every `await`, and an inline `try/catch` around each `invoke`. Until then the
  service retried every failure for ever, which was right for a baked-in key and wrong once the server drops
  a connection when access changes and the next negotiate is a permanent 401. The booleans the subscribe
  methods return are not read: the record of a subscription is kept whatever the hub answered, because
  resubscribing on reconnect is exactly how a refused subscription becomes an accepted one after a grant.
- **The session model** (C44, `0f3bb28`). `AuthService` (signals; `bootstrap` never rejects and every session
  request has an 8 s timeout; refresh is single-flight; a failed refresh keeps the last known session; login,
  logout, setup and password change POST and then read the session afresh, because antiforgery tokens are
  bound to the identity and the `XSRF-TOKEN` must be re-issued after every identity change), `problemOf` and
  `util/session-access.ts`, an `auth-mock` for specs. Not wired into the app, so the bundle did not change.
- **Login, setup and account pages** (C45, `d78d7cc`). Three lazy routes, the guards (`authGuard`,
  `anonymousGuard`, `setupGuard`, `adminGuard`, `safeReturnUrl`, which accepts only an absolute path of the
  app), a stale-chunk reload at most once a minute, and `util/failure-text.ts`. The forms are template-driven
  but carry `ngNoForm` and check their own fields (see the bundle story below).
- **A session is required** (C46, `193e679`). An `authInterceptor` (a 401 outside the sign-in endpoints calls
  `handleUnauthorized(epoch)`; a 403 calls `noteForbidden()`; a `400 antiforgery` on an unsafe request
  refreshes the session and retries once with the new token), an app initializer that awaits the session
  read, a top bar with a user menu, `authGuard` on the saga routes, and the API key gone from the bundle, the
  hub connection and the request headers (`DASHBOARD_API_KEY` and the api-key interceptor deleted).
  `Dashboard__ApiKeyRole` went back to `Viewer`.
- **No-access states** (C47, `731b724`). The saga pages follow the session: the retry row exists only with
  `sagas.retry` for the page's type, the data views only with `sagas.data`, a 403 detail load is a forbidden
  state with the API's words, a list without `sagas.view` anywhere makes no request at all, a 400 returns the
  list to its last good page (or, for `maxPage` 0, shows the server's text and never asks for page 0).
- **The administration area** (C48 to C50, `b3b67e1`, `cec2a40`, `7e61f16`). `/admin` behind
  `canMatch: adminGuard`, so the chunk is requested only for a manager; roles (read-only built-ins, duplicate
  as custom, delete disabled while in use); users (list with status chips, a page with a grants editor and an
  effective-access preview that applies the catalogue's `scopable` and `implies`, inline password reset,
  unlock, delete); teams (member checkboxes with a filter, grants, an effective-access preview).
  `admin-api.service.spec.ts` asserts the golden fixtures from C39 (the request bodies equal the request
  fixtures; each response fixture parses into its model with the same key set); `AdminStore` reads the
  catalogue, the three lists and the saga types, never rejects and keeps pages on a failed refresh;
  `ConfirmButton` is the retry row's two-step pattern as a component.

**Documentation (C51, `1c7e7aa`).** `docs/dashboard.md` rewrote "Authentication" (heading kept: the 401 and
403 bodies and three SPA specs name its anchor), gained "Access control", "Audit log" and "Deploying beyond
localhost" and a rewritten "Live updates (SignalR)", which had been stale since C40; `docs/configuration.md`
gained one table of the 19 `Dashboard:*` keys with defaults and ranges; `docs/observability.md` lists the
audit, identity, session, antiforgery, origin and hub events and the `identity` health check;
`docs/persistence.md` says the identity store is its own file and volume.

## How it was verified

### The environment

The work up to the pause at C40 (C31 to C40 and the C33 and C37 live checks) ran on a Windows checkout, as the
handoff file's tooling notes describe (PowerShell, Docker Desktop, CRLF files). Everything from the C40 live
check on, which is every other check below, ran in a Linux cloud container, which differs from those notes:

- **.NET** through the .NET 10 SDK image (the repository mounted at the same path, host networking, the Docker
  socket for Testcontainers, a persistent NuGet cache volume) behind a small wrapper script kept outside the
  repository: the host has no `dotnet`. A cold build takes about 100 s, an incremental one about 20 s, the full
  suite 6 to 8 minutes.
- **Docker Hub answered 429.** The base images (`postgres:16-alpine`, `mongo:8.0`, `redis:7.4-alpine`,
  `rabbitmq:4-management`, `node:22.23-bookworm-slim`, `nginxinc/nginx-unprivileged:1.30-alpine-slim`) were
  pulled from `mirror.gcr.io` and retagged locally. The `mcr.microsoft.com` SDK and ASP.NET images and the
  Node image were rebuilt locally with the egress proxy's CA certificate baked in, because TLS inside
  `docker build` otherwise fails.
- **Node.** The host's Node was 22.22.0, which Angular CLI 22 refuses (it needs 22.22.3 or later), so every SPA
  command ran as `npx -y -p node@22 -- <command>` from `dashboard-web/` (22.23.x).
- **Browser checks** used Playwright's Node library with headless Chromium (Playwright 1.56.1, Chromium 141),
  one fresh context per script, a `securitypolicyviolation` listener on every context, and the hub client
  checks used `@microsoft/signalr` 10.0.11 from `dashboard-web/node_modules` under Node.
- **The sandbox's safety check refused two scripts** (one contained `docker compose run --rm`, the other was
  wrapped in a `bash -c` it could not analyse). Neither was worked around. The consequence that is recorded:
  the CI job's `nginx -t` step was not replayed locally (C41); the dashboard-web container starting healthy
  covers the same configuration. The second script's purpose is not recorded.
- **The shared working tree held other agents' unfinished edits** for much of the post-pause work, so the
  SPA gates and mutation passes of C47 to C50 and their follow-ups ran in isolated copies (a git worktree or
  a clean export of `HEAD` plus the commit's files, with `node_modules` linked), as the commit messages say.
- **The branch was pushed** from `7bf2e4f` on at the maintainer's request, so every commit after it was
  treated as already published: a mistake could only be corrected by a follow-up commit, which is why the
  history holds twenty follow-ups and no amended commit (the one attempt is told under the problems below).

### Gates at the end of the slice

**The SPA, under Node 22, from a clean export** of `fdc1685` (`git archive HEAD dashboard-web`, then, in
that directory, `npx -y -p node@22 -- <command>` for each; `node --version` printed `v22.23.3`, `npm --version`
`10.9.4`):

| Command | Result |
| --- | --- |
| `npm ci` | exit 0: `added 316 packages, and audited 317 packages in 5s` (a warm npm cache) |
| `npm audit --audit-level=low` | exit 0: `found 0 vulnerabilities` |
| `npx ng build` | exit 0, no `WARNING`, no budget warning; Initial total 436.65 kB raw, 114.01 kB estimated transfer (`chunk-C9VWtxAq.js` 298.89 kB, `main` 132.31 kB, `styles` 5.46 kB), 63.35 kB under the 500 kB budget; 19 lazy chunks, the largest `saga-detail` 76.56 kB, `user-edit` 27.50 kB, `team-edit` 15.58 kB, `role-edit` 13.02 kB, the shared grants-editor chunk 12.01 kB, `setup` 11.01 kB, `admin-routes` 9.77 kB, `account` 8.48 kB, `login` 6.24 kB, `users-list` 5.19 kB, `roles-list` 2.93 kB, `teams-list` 2.75 kB |
| `npx ng test --watch=false` | exit 0: 51 files, 1862 tests passed (Vitest 4.1.11, 17.67 s) |

Three more checks on that build: the CI guard on `index.html` (no bare `<script>`, no `on*=` handler) exited 0;
`dev-local-only-change-me`, `x-api-key` (any case) and `DASHBOARD_API_KEY` each found in no file of
`dist/dashboard-web/browser` (`grep -r`, exit 1 each time); and the `ng test` output carried one compiler
warning that no commit body mentions, `NG8113: All imports are unused` for `imports: [RouterLink]` on the
`EditStub` test component at `admin-shell.spec.ts:314` (added by `fdc1685`; `ng build` is unaffected, because
the file is a spec), and the deprecation notice `Option "splitting" is deprecated: No longer needed with
Vitest 5` that `dd37bd1` documents (see the test-isolation problem below).

**The server, as last recorded.** The last full run of the .NET suite in the record, before `e913282` was
committed: build 0 warnings and 0 errors, 1627 tests passed across 16 test projects (Core 230, Dashboard.Api
506, Dashboard.Identity 243, EFCore 218, MongoDB 111, Redis 109, InMemory persistence 84, Transport.Http 39,
Chaos 27, Http 15, Transport.InMemory 13, Testing 7, Brighter 7, MassTransit 6, RabbitMQ 6, Wolverine 6),
against 1613 at `852a901`. Later commit messages give per-project counts only: `35abb82` Dashboard.Api 509
and Dashboard.Identity 243, `1adee09` Dashboard.Api 513 and Dashboard.Identity 253. No file under `dotnet/`
changed after `1adee09` (`git diff --name-only 1adee09 HEAD -- dotnet` lists none). A full-suite total for
the finished slice was not recorded and was not re-run for this record; adding those two projects' growth to
the 1627 would give 1644, which is arithmetic, not an observation.

### The bundle and the spec count, commit by commit

Each row is the figure the commit's message reports (`ng build` Initial total against the 500 kB warning
budget; `ng test` specs). The commits that changed only the server or documentation are absent.

| Commit | Initial total | Specs |
| --- | --- | --- |
| `a8bbac3` Angular 22 | 462.26 to 481.34 kB | 427 (19 files) |
| `81e3006` C42 | 482.09 kB | 429 |
| `fabc67c` C43 | 483.17 kB | 449 (the hub spec 22 to 42) |
| `0f3bb28` C44 | 483.17 kB (nothing wired) | 565 |
| `a244d33`, `2a2a64b` | 483.17 kB | 571, then 609 |
| `d78d7cc` C45 | 496.28 kB (503.07 with `NgForm`) | 819 |
| `94d6c40` | 496.75 kB | 906 (33 files) |
| `193e679` C46 | 430.97 kB (506.18 with the detail page eager) | 1007 (35 files) |
| `91068d9` | 432.81 kB | 1031 |
| `731b724` C47 | 437.00 kB | 1075 |
| `b3b67e1` C48 | 435.64 kB | 1264 |
| `952269e`, `e89a1ef`, `4ac938d` | 436.37, 436.47, 436.56 kB | 1306, 1361, 1370 |
| `cec2a40` C49 | 436.60 kB | 1610 |
| `7e61f16` C50 | 436.54 kB | 1725 |
| `537134e`, `c6ca8ca` | 436.54 kB | 1742, 1811 |
| `c4720c6`, `fdc1685` | 436.57, 436.65 kB | 1817, 1862 (51 files) |

### Mutations: the server

**Per commit.** Every server code commit carries its own hand mutations, each failing only its own tests. A few of
the observed numbers: C31's read-modify-write counter lost increments (24 became 2), and a deferred write
scope failed the check-then-write case; C32 switching the last-administrator invariant off failed 10 tests; C35
not setting `HttpContext.User` before issuing the antiforgery token failed the seven tests that use a
login-issued token, and skipping the failure floor failed only the floor test; C36 accepting any setup code
failed the four wrong-code and rate-limit tests, opening setup when the seed cannot be applied failed the
five unusable-seed cases and the weak `ResetOnStart` test, and keeping the stamp on `ResetOnStart` failed the
reset test; C40 switching `CloseOnAuthenticationExpiration` off, the registry aborting nothing, the guard
skipping hubs, the saga subscription unchecked, the list subscription ignoring scope and the per-type push
removed each failed only their own tests. The C38 and C39 mutation runs have no evidence file; their facts were
kept in the handoff file of the working plan (its Appendix A, deleted with the plan by the final commit) and are
reproduced here:

- C38 (`e69deb8`), first run against the working tree: "retry needs only view" failed exactly
  `ARetryOfATypeOutOfScope_Is403EvenForAScopedOperator_WhileItsOwnTypeRetries`, `AViewer_CannotRetry` and
  `TheDefaultViewerApiKey_ReadsEveryTypeWithData_ButCannotRetry`; "no redaction" failed
  `WithoutSagasData_PayloadsErrorMessagesAndStateAreRedacted_OnEveryPath` and
  `WithSagasData_ForTheSagasType_PayloadsErrorMessagesAndStateAreServed`; "blank type is a filter" failed
  `TheList_ShowsOnlyVisibleTypes_AndABlankTypeFilterIsNoFilter` and
  `ScopedSagaListerTests.AGrantOfASingleSpace_CannotWidenTheScope`; "no dedupe" failed
  `ARowThatMovesBetweenTwoReads_IsNotEmittedTwice`; "culture-free tie-break lost" failed
  `EverySortArm_WalkedPageByPage_EqualsAFullSortOfTheVisibleRows` and `TiesAcrossTypes_BreakBySagaTypeOrdinal`.
  Three mutations ("drop the scope filter", "no attribution", "lookups unfiltered") did not build and were
  redone as one-token variants: they failed `RowsOutsideTheScope_AreFilteredOut_EvenWhenTheProviderReturnsThem`,
  `ARetry_IsAttributedToTheApiKey` with `ARetry_IsAttributedToTheSignedInUser`, and
  `SagaTypesCorrelationsAndChildren_OnlyListVisibleTypes`. The restore build exited 0. The message of
  `e69deb8` names three more: counting a search as rank-served, a blank route type falling back to any type
  and an unreduced `TotalCount`.
- C39 (`8d69429`) fix runs: `AllowDuplicateProperties = true` in `JsonRequestBody.cs` failed, of 77 Admin and
  AuthEndpoints tests, exactly `AnUnknownMemberOrAWrongValue_Is400_NamingItsPath_AndChangesNothing` (the
  `{"isEnabled":false,"IsEnabled":true}` case); moving the audit logger out of `DashboardAudit.CategoryName`
  failed, of 76 Admin tests, exactly `EveryUserChange_IsAudited_UnderTheAdministratorsName_AndARefusalToo`.
  Both were restored with `git checkout`; build 0 warnings; the Admin and AuthEndpoints tests passed (106).

**The slice-level pass** (the plan's authentication mutation checks, after C41). It ran against a git
worktree of `e913282`: baseline build 0 errors, `Dashboard.Api.Tests` 506 of 506 and `Dashboard.Identity.Tests`
243 of 243. Each mutation was a `sed -i` edit, a rebuild of both test projects, both projects run, then
`git checkout` plus `touch` (so MSBuild cannot reuse the mutated DLL) and a rebuild. Every build log was
checked for anything other than `MINVER1001`, the warning MinVer raises only in the worktree, whose `.git`
file points at a directory the container does not mount. `VSaga.Http.Tests` references the dashboard API only
for the pure map builder, so no mutation can reach it and it was not run.

| # | Mutation | What failed |
| --- | --- | --- |
| 1 | Retry needs only `sagas.view` (`SagaEndpoints.cs`) | Api 3: the three tests of the C38 run, nothing else. Retry anonymous (1b): those three and `EndpointProtectionTests.TheAnonymousEndpoints_AreExactlyHealthSignInSetupAndTheDevelopmentOpenApiDocument` |
| 2 | Hub origin guard allows everything (`HubOriginGuard.cs`) | Api 15 cases, all in `HubOriginGuardTests` (12 methods) |
| 3 | Security-stamp comparison ignored (`CallerAccessResolver.cs`) | Api 6 and Identity 2, every one about a rotated or missing stamp |
| 4 | Redaction removed: 4a the data decision, 4b the nulling in `SagaTimelineRedaction`, 4c the state blob alone | 4a Api 2; 4b Api 6 (the two enforcement tests, the three redaction tests, the hub push test); 4c Api 1, the only guard of `dataJson` |
| 5 | `Disallow` to `Skip` on all nine request records | Api 10, all unknown-member tests. Split per record, `SetupRequest`, `ChangePasswordRequest` and `ResetPasswordRequest` failed nothing (see below). Duplicate-member rejection off (5b): exactly the `{"isEnabled":false,"IsEnabled":true}` case |
| 6 | The API key keeps `access.manage` | Api 2 and Identity 2, all about the key and `access.manage` |
| 7 | Scope filter dropped in the lister | Api 1: `RowsOutsideTheScope_AreFilteredOut_EvenWhenTheProviderReturnsThem` |
| 8 | Setup code accepted whatever it is | Api 4: the wrong-code, empty, null and rate-limit-before-code tests |
| 9 | No hub notification on a password change; the registry closing nothing (9b) | Api 2 and Identity 1; 9b Api 11, every test of `SagaHubAccessTests` that closes connections on an access change |

All nine passed: only their own tests failed, and the restored tree built with 0 errors and passed 506 of 506
and 243 of 243. Reading the plan's item 1 ("remove the retry policy"), the pass took the `sagas.retry`
authorization requirement on `POST .../retry`; the other reading, the SPA hub's reconnect policy, was
mutated in C43 (`previousRetryCount > 0` to `> 5` failed three policy specs). Four first attempts did not
compile, because the build runs the Sonar analyzers with warnings as errors (`S1172` on an unused `stamp`
parameter, `S3981` twice on a `Count` comparison, `S1125` on `|| true`); each was redone in a form the
analyzers accept.

**What the pass found.** Two gaps. Per record, `Disallow` to `Skip` on `SetupRequest`, `ChangePasswordRequest`
or `ResetPasswordRequest` failed nothing in 506 plus 243 tests: a stray member on those three bodies would
have been ignored silently, and the all-nine mutation was caught only through the login and admin bodies. And
the setup-code decision in `FirstAdministratorService.CompleteSetupAsync` was held only by four HTTP tests.
`1adee09` closed both. It added an endpoint test per body, a theory row for `POST {id}/password`, a reflection
test that every public `*Request` record in the endpoints namespace carries `Disallow` (with the eight known
requests asserted present, so a rename cannot empty it), and ten identity-service cases for the code. Its
body records each guard shown to fail without it: `Skip` on `SetupRequest`, `ChangePasswordRequest` or
`ResetPasswordRequest` now fails its own test and the reflection test, skipping the code check fails seven of
the new service cases, and a null code accepted in `SetupCodes.Matches` fails the null case. Dashboard.Api
went from 509 to 513 tests and Dashboard.Identity from 243 to 253.

### Mutations: the SPA

The SPA commits ran one-token (or one-line) mutations in isolated copies, each followed by the specs that
cover the file and a restore. The counts the commit messages state are below; `d78d7cc`, `94d6c40`,
`0f3bb28` and `2a2a64b` list their mutations without counting them.

| Commit | Mutations | Outcome |
| --- | --- | --- |
| `81e3006` C42 | 5 listed | each failed exactly the new spec (the last also the three existing specs that query the tabs) |
| `fabc67c` C43 | 13 listed | only the named specs failed |
| `a244d33` | 8 | exactly the named spec or specs failed; removing `.catch` from `stop()` passed all 48 specs and failed the run on an unhandled rejection |
| `193e679` C46 | 42 | each caught by its own tests only |
| `91068d9` | 21 | each caught by its own tests only |
| `731b724` C47 | 37 stated, 36 true | see the process incident below |
| `952269e` | 45 | each failed its own specs |
| `4ac938d` | 14 | each failed its own specs |
| `b3b67e1` C48 | 37 | each failed exactly its own specs |
| `e89a1ef` | 32 | each failed the specs written for it |
| `cec2a40` C49 | 100 | each failed at least one spec (the first pass left real survivors, which got specs) |
| `c6ca8ca` | 43 | each failed at least one spec (two survivors, one that did not compile, all re-run) |
| `7e61f16` C50 | 59 | 58 failed their own specs; the survivor (a 404 also seen through the reloaded list) got two specs and then failed |
| `537134e` | 18 | each failed its own specs |
| `c4720c6` | 7 | each failed its own specs |
| `fdc1685` | 36 | each failed at least one spec (one survivor, a subscription kept after destroy, got a spec) |

The plan listed no SPA-side mutation for the slice pass; these are the per-commit ones.

### Live: before the pause (C33 and C37)

These two checks' evidence files are not in the repository; the handoff file of the working plan preserved the
facts below, which were observed, not expected.

**C33, 2026-10-02, at `1d6b87f`.** `docker compose build dashboard-api` exit 0, and with `--no-cache` the
restore layer restored `VSaga.Dashboard.Identity` and `VSaga.Dashboard.Identity.Sqlite` (the two csproj
`COPY` lines). `docker compose up -d --build --wait` exit 0, every container healthy. The container held no
`Dashboard__Identity__*` variable at that commit, so `GET http://127.0.0.1:5080/health` answered 200 with
`"status":"degraded"` and `identity` degraded, saying that `Dashboard:Identity:Sqlite:Path` is not set. The API
log had `IdentityStartup[7202]` (store not ready, retrying at most once every 10 s) and `KeyRingProvider[48]`
with `IdentityUnavailableException` (no in-memory key fallback), and no unhandled exception.
`docker inspect` showed `health=healthy restarts=0` for the API while `identity` was Degraded. No identity
directory was created and the process ran as root. The UI at 4200 still worked (list, a Failed saga's detail,
hub negotiate 200, no console errors). One observation: the health message's "(the image sets ...)" was
untrue until C37 added the `ENV`.

**C37, 2026-10-03, at `f25b1ec`.** All six checks passed. (1) On a fresh volume the image's user was `1654`,
`id` in the container printed `uid=1654(app) gid=1654(app)`, `/var/lib/vsaga-dashboard` was `drwx------`
owned by `app` and `identity.db` `-rw-------` `app:app` with no `-wal` or `-shm` file; `/health` was 200 with
`identity` healthy; the log had `IdentityStartup[7200]`, an audit line `user.seed`,
`FirstAdministratorService[7212]`, `IdentityStartup[7201]` and no setup code, plus a framework Warning
`XmlKeyManager[35] No XML encryptor configured`. (2) Through 4200 with curl: `GET /api/auth/session` set
`vsaga.session.vsaga.af` (HttpOnly) and `XSRF-TOKEN` (readable, `samesite=strict`) and answered
`authenticated:false`, `setupRequired:false`, `passwordMinLength:12`; the login with `X-XSRF-TOKEN` answered
200, set `vsaga.session.vsaga` (HttpOnly) and rotated the token; `GET /api/sagas` with the cookie 200, without
credentials 401. (3) `docker compose up -d --force-recreate --wait dashboard-api` (a new container id): the
same cookie jar still authenticated, and the new container logged no seed line and no new-key warning, so the
keys came from the store. (4) `down` then `up -d --wait` kept the session; `down -v` then up removed both
volumes, seeded the administrator again with a new id, the old jar got 401 and a fresh sign-in worked.
(5) `docker compose -p vsaga-wolverine -f docker-compose.yml -f docker-compose.wolverine.yml config` rendered the
cookie name `vsaga.session.vsaga-wolverine`, the volume `vsaga-wolverine_vsaga-dashboard-identity` and ports
`127.0.0.1:5180` and `127.0.0.1:4300`; the base project renders `vsaga.session.vsaga` and every other overlay
`vsaga.session.vsaga-<name>`. (6) The SPA with its embedded key: a Failed `OrderSaga`'s retry confirmation
read "Re-run step 2 (PaymentFailed, Gathering) for this saga only?", `POST .../retry` answered 202 carrying
`x-api-key`, the saga went from version 1 to 3 and back to `Failed` (the sample's business failure is
deterministic), console 0 errors and 0 warnings. The same run saw two `X-Frame-Options` headers through nginx
(fixed by `c0a1ca4`) and `X-Content-Type-Options: nosniff` twice, nginx's and the API's, with the same value,
which was not changed.

### Live: C40, with the real client, twice

Run at the code of `852a901` against the compose stack, through `http://localhost:4200`, with a cookie jar for
`admin` and one for a Viewer scoped to `OrderSaga` (`viewer1`).

- **Origin guard.** Negotiating `/hubs/saga` with each jar and `Origin: http://localhost:4200` answered 200
  with a `connectionToken`; with `Origin: http://localhost:9999` 403; with `Origin: null` 403; with no `Origin`
  200 (both users, same four answers). The API log held the four Warnings, each naming the received origin
  and `expected no Origin or http://localhost:4200`.
- **Subscriptions and pushes** (`@microsoft/signalr` 10.0.11 from Node, 45 s each). `viewer1`:
  `SubscribeToList` true, `SubscribeToSaga(PostShipmentChoreography)` false, `SubscribeToSaga(OrderSaga)` true,
  and 8 `SagaUpdated` pushes, every one `OrderSaga`. `admin`, the same 45 s: all three subscriptions true and
  26 pushes across all 7 saga types.
- **Disable.** `PUT /api/admin/users/{id}` with `{"isEnabled":false}` answered 200 at 09:59:51.837; the open
  socket closed at 09:59:51.913, 76 ms later; a negotiate with that cookie answered 401.

That first run, made before the fix, showed that the socket closed; it could not show whether the client
would reconnect, because the close message is where that is decided (see the first problem below). After the
fix the check was repeated on the rebuilt image with a client that reconnects automatically
(`withAutomaticReconnect([0,1000,2000,3000])`, resubscribing on reconnect): a role change (PUT at
10:27:49.406) gave `RECONNECTING` at .468, `RECONNECTED` at .496, `SubscribeToList` true again and further
`OrderSaga` pushes; disabling the user (PUT at 10:27:55.584) gave `RECONNECTING` at .661 and `CLOSED` at 10:28:01,
after the client's four reconnect delays, and a negotiate with that cookie answered 401. The API log read
`Closed 1 live hub connection(s) because a role changed; the clients reconnect under the current access`.
That client used the library's four reconnect delays and gives up after them, so it validated the protocol
between the server and the library; it did not run the SPA's `SagaHubService`, whose retry policy and session
probe the SPA specs cover and which C46 and C47 later exercised in a browser.

### Live: C41, the CI job replayed

The steps of the compose job, extracted from the working-tree `ci.yml`, were run in order on a fresh stack:
step 01 (the compose files and the seven port pairs) exit 0, step 02 (the image build) exit 0, step 04 (`up -d
--wait --wait-timeout 240`) exit 0 with all containers healthy, step 05 (the existing smoke test: index.html for
a deep link, `/api/saga-types` 200, the 404, the two encoded-slash comparisons, a connection token, the 101)
exit 0, and the new step 06 exit 0 with its eight assertion lines (identity healthy; the identity file lies on
a mounted volume; `GET /api/sagas` without credentials 401; the session sets the XSRF cookie; the login signs
in as `admin`; `GET /api/sagas` with the cookie 200; the negotiate with the right origin returns a token; with
`http://localhost:9999` 403). It was run again after `docker compose down -v` and `up -d --wait`, and steps 05
and 06 passed again. Step 03, the `nginx -t` step, was not run (the sandbox refusal above). `4dde4b1` then
checked that a refusal inside the step reports the problem document by running it with a wrong password: the
`::error::` line carried the 401 body (`invalid_credentials`).

### Live: C46, the SPA with no key

Run 2026-10-03, 13:21 to 13:39 UTC, on `193e679` from a clean tree, fresh images: `docker compose up -d
--build --wait` exit 0 in 1 min 3.5 s. The chunks served matched the figures in the commit message (saga-detail
74 600 B, shared initial 356 764 B, `main` 68 921 B, setup 10 963 B, account 10 825 B, login 6 838 B).

| Step | Observed |
| --- | --- |
| Anonymous deep link, then sign in | the only API request before sign-in was `GET /api/auth/session`; the URL became `/login?returnUrl=%2Fsagas%2FOrderSaga%2F...`; the saga-detail chunk was requested only after the login `POST` answered 200, and the saga loaded with 0 console errors and 0 CSP violations; the setup chunk was never fetched |
| List and live updates | 25 rows, "129 total", 20 `SagaUpdated` frames over 46 s; the WebSocket handshake was `101` and neither it nor any request URL held `access_token`; the negotiate carried only the cookies, `origin` and SignalR headers, no `authorization` and no `x-api-key`; the session cookie was HttpOnly and not readable from script, `XSRF-TOKEN` was readable |
| Retry of a Failed saga | the confirmation read "Re-run step 3 (InventoryReservationFailed, Gathering) for this saga only?"; `POST .../retry` 202 with `x-xsrf-token` present and `x-api-key` absent; the saga left `Failed` and came back to it 288 ms later at version 4 (the sample's business failure) |
| User menu, keyboard only | four Tabs from a fresh load reach the trigger; Enter, ArrowDown and ArrowUp (both wrap), Escape (focus back on the trigger), Space, Tab out and Shift+Tab all as intended; Sign out closed the socket 13 ms after the key and 0 negotiate requests followed in 10.5 s |
| Antiforgery healed | after `clearCookies({name:'XSRF-TOKEN'})`, "Yes, retry" sent a `POST` (400 `antiforgery`), then `GET /api/auth/session`, then the `POST` again (202); the user saw only the success message; exactly one retry ran (the saga went from version 1 to 3) |
| `docker compose restart dashboard-api` | "Reconnecting to live updates..." appeared at once and was gone at +7.6 s; no navigation to `/login`, no new sign-in, 11 `SagaUpdated` frames afterwards |
| `down -v` then `up -d --wait` with the tab open | the tab ended on `/login?returnUrl=...&reason=expired` 17.7 s after the wipe started (as soon as the API answered, with a 401 on the negotiate and a session probe that said anonymous), once, and stayed there for 75 s with "Your session expired. Sign in again to continue."; not on "Reconnecting" |
| The API key, now Viewer | `GET /api/sagas` through 4200 and straight to 5080 with the key 200, without it 401; `POST .../retry` with the key 403 with `"permission":"sagas.retry"`, the saga unchanged; `GET /api/admin/users` with the key 403 |
| The served JS | 8 files crawled (and the container's listing held no other): `dev-local-only-change-me`, `X-Api-Key` and `DASHBOARD_API_KEY` absent; `accessTokenFactory` and `access_token` present only as SignalR library code |
| Console | 0 errors, 0 warnings, 0 CSP violations on the login, list and detail pages |

Observations that were not failures: a second tab signing in as someone else did not cause an antiforgery
400, because the SPA reads the `XSRF-TOKEN` cookie when it sends; the retry ran as the new user (403 for a
Viewer), the page followed the identity change and the retry message simply disappeared. A Viewer saw the
Retry button and got the generic "Retry failed." on the 403 until C47. An HttpOnly `vsaga.session.vsaga.af`
cookie exists before sign-in and stays after sign-out. The first Tab right after signing in goes to the
page's filter, not the top bar, a browser focus-start effect that was not investigated.

### Live: C47, what each user sees

Run 2026-10-03, 13:56 to 14:14 UTC on `731b724`, a fresh stack. Users made through the admin API: `viewer1`
(Viewer on `OrderSaga`), `nodata1` (a custom role holding only `sagas.view`, for all types), `noview1` (a
custom role holding only `access.manage`), and extra ones for the pager cases.

- **`viewer1`.** The list showed 19 rows, all `OrderSaga`, "19 total", and the type filter offered "All saga
  types" and `OrderSaga`. A Failed `OrderSaga` showed no retry button and the hint "You do not have
  permission to retry OrderSaga sagas.", with the data views working. Another type's detail showed, under the
  back link, "You do not have access to PostShipmentChoreography sagas. Ask an administrator for sagas.view on
  this saga type."; such a page cost exactly one 403, on the detail request, beside two session reads and a hub
  negotiate (the last is one of the findings below). `/admin` and `/admin/users` ended on `/sagas`.
- **`nodata1`.** All three data buttons disabled beside "Saga data is hidden for your role. It needs the
  sagas.data permission."; no data panel, no timeline row text, no per-step Data button; the API sent
  `dataJson: null`, no `payloadJson` and no `errorMessage`. A scan of the rendered DOM (text, HTML and every
  attribute value) for the saga's state values found 0 hits in 24 scans (12 pages for each of two sagas,
  deep links with hostile `data` and `entry` values included); the positive control, the same pages as admin,
  found the tokens.
- **`noview1`.** "Your account has no access to any saga type yet. Ask an administrator for sagas.view." From
  sign-in to 8 s later the page made only `POST /api/auth/login` and `GET /api/auth/session`: no `/api/sagas`,
  no `/api/saga-types`, no hub negotiate, no WebSocket.
- **`admin`.** 25 rows of 7 types, retry of a Failed `OrderSaga` 202 and the saga back on `Failed` at version 3
  (it was 1); no retry row on a Completed one.
- **Console.** 0 CSP violations and 0 page errors in 25 browser sessions. The only console errors were
  Chromium's "Failed to load resource" lines for expected 403 (11) and 400 (3) responses.

What this run found that the specs had not (all fixed in `952269e`, below): after a real revocation the
open list kept its stale rows, because the hub reconnect that the server forces did not trigger a read; a
forbidden detail page still opened the hub and sent `SubscribeToSaga`, which the hub refused with `false`; a
400 with a `maxPage` far above the pages that exist (`?page=999`, `maxPage` 400, 7 real pages) sent the list to
page 400 and then clamped to page 7, clearing the server's message after about 24 ms; and, in a simulation
that gave `nodata1`'s browser admin bodies, the map's failure card printed `errorMessage`, because only the
timeline gated it. Not reached live: a `maxPage` 0 refusal (it needs more than 10 visible saga types and the
sample has 7) and a 400 with rows on screen (the pager's own input is capped at the real page count). The
redaction check used one `StepFailed` row inserted by hand into the Postgres event log, because no saga in the
sample has an error message (the timelines of all 18 Failed sagas and of the 100 most recent of 199 had none).

### Live: C50, including two stacks in one browser

Run 2026-10-03, 16:41 to 17:32 UTC on `7e61f16` (a clean worktree), fresh volumes; the evidence is for that
commit only, not for the follow-ups that landed while it ran.

- **Teams and the last administrator.** Team `payments` (member `viewer1`, Operator on `OrderSaga`) created
  through the UI: the `POST` body carried `memberIds` and `grants` and answered 201; `viewer1`'s user page
  showed the team as a link in a read-only field, and the preview read "team payments: Operator" for retry. In
  a second browser context, `viewer1`'s Failed `OrderSaga` had no retry button before the team and one after a
  reload, and the retry answered 202. Removing admin's own Administrator grant (UI), disabling admin and deleting
  admin (API), and, on the team page, unticking the member, removing the team's grant and deleting the team,
  each answered `409 last_administrator`, with the banner and the draft kept where the page has one.
- **Roles, users, teams, keyboard.** Exercised end to end in the UI, including a lockout (5 wrong sign-ins
  through the login page, then the right password refused, the "Locked" chip, Unlock, sign-in working), a forced
  password change (`/sagas` and `/admin/users` both ended on `/account` until it was changed), an unchanged
  team Save that left the server record identical, and a team deleted behind an open page ("This no longer
  exists" with the link focused). The keyboard-only pass operated every control it tried and recorded the
  focus losses (S2, below). Console: 0 CSP violations, 0 page errors, 0 warnings; the errors were Chromium's
  lines for the 4xx answers the checks provoked.
- **Two stacks in one browser profile.** `docker compose -p vsaga-wolverine -f docker-compose.yml -f
  docker-compose.wolverine.yml up -d --build --wait` exit 0 after 21 s beside the base stack. One browser
  context, tab 1 on `http://localhost:4200`, tab 2 on `http://localhost:4300`, both signed in as admin.
  Cookies for `localhost`: five, `vsaga.session.vsaga` and `.af`, `vsaga.session.vsaga-wolverine` and `.af`, and
  one shared `XSRF-TOKEN` that each sign-in replaced. Both tabs stayed signed in after a reload. Ten role
  creations alternating between the tabs (no reload between them): **all ten first `POST`s got `400`
  `antiforgery`**, each followed by `GET /api/auth/session` and one retried `POST` that answered 201, with no
  banner and the page leaving the form every time. The first `POST` carried the other stack's token and the
  retry the refreshed one. The same-tab control, a second creation right after one in the same tab, needed no
  retry. Each stack kept only its own roles. So the shared `XSRF-TOKEN` costs a visible 400 in the network log
  (and one Chromium console error) on every switch between stacks, and the SPA's single retry heals it
  silently.
- **Restart and wipe.** `docker compose down` then `up -d --wait` (down 2.3 s, up 18.1 s): the session cookie
  value was identical, the data intact, a role creatable. `down -v` then up: a tab on a saga page (live hub)
  ended on `/login?returnUrl=...&reason=expired` 17.6 s after the stack started coming back, once, with the
  seeded administrator back and `viewer1` refused with 401. An idle admin tab with no live hub stayed on
  `/admin/roles` until its first write, which answered 401 and then went to `/login`; that is the open item R1
  below.

What this run found that the specs had not: **S1** an open, already permitted `OrderSaga` detail page did not
pick up a widened permission: at team creation `viewer1`'s hub disconnected at 17:18:57.309 and reconnected at
57.322, and the page re-read the detail, timeline, map, correlations and children at 57.614, but sent no
`GET /api/auth/session`, so 7.2 s after the team `POST` it still said "You do not have permission to retry"
(fixed by `c4720c6`). **S2** keyboard focus fell to `<body>` after a successful Save on the user form, after a
`last_administrator` banner, after "Yes, delete" and after Enter on a users-list link (fixed by `fdc1685`). A
minor, Escape in the reset-password panel doing nothing, went into the same follow-up. **S3** is the result
above (not a defect to fix), and **S4** is environmental: the sample takes one to two minutes after a fresh
start to produce its first Failed `OrderSaga`, so a script that looked after 25 s found none.

## Review rounds

Each commit went through the loop the plan prescribes: implement, two read-only reviews (a spec lens and a
correctness lens), fix, a live check where the plan lists one, then the mutation checks. After C47 the
follow-ups' reviews were reduced to one combined review per commit, to fit the working budget. What each
round found, from the commit messages and the findings files kept with them:

| Commit | Review result | Fixed in |
| --- | --- | --- |
| C33 `e6228c3` | 1 minor (the stale-lock test could not fail) | `1d6b87f` |
| C34 `8b7a39a` | 2 findings (readiness read after the resolver; the sign-in time not pinned across renewal) | `83d99eb` |
| C37 `f25b1ec` | live check: two `X-Frame-Options` headers | `c0a1ca4` |
| C38 `e69deb8` | second round: 5 minors (three wording errors in design 8.7 and ADR 0006, an overclaiming comment, a blank route type missing from the 403) | `5e1bd67` |
| C40 `852a901` | correctness: 1 blocker, 6 minors; spec: 2 code minors and 1 documentation minor | `e913282` |
| C40 follow-up `e913282` | second review: no blocker, no major, 5 minors | `35abb82` |
| C41 `64ba0ac` | no blocker or major, 2 minors, 4 optional nits | `4dde4b1` |
| C43 `fabc67c` | correctness: 1 major (a missing spec), 5 minors | `a244d33` |
| C44 `0f3bb28` | 3 majors, one of them the flaky hub spec (fixed by `dd37bd1`); the follow-up fixes eleven findings (two majors and nine minors by its own numbering) and leaves one minor by design | `2a2a64b` |
| C45 `d78d7cc` | two reviews agreeing: 2 majors, 10 numbered minors and some wording, CSS and comment items | `94d6c40` |
| C46 `193e679` | spec: 3 minors; correctness: no blocker, 1 major accepted as a known limitation, minors | `91068d9` |
| C47 `731b724` | spec: 4 minors; correctness: 5 majors, 14 minors; live check: 2 more (A, B) | `952269e` |
| C47 follow-up `952269e` | review: 1 major (a regression), several gaps | `4ac938d` |
| C48 `b3b67e1` | correctness: 3 majors and minors; spec: 4 minors | `e89a1ef` |
| C49 `cec2a40` | combined: no blocker, 1 major, 9 minors | `c6ca8ca` |
| C50 `7e61f16` | combined: no blocker, 1 major, 8 minors | `537134e` |
| C50 live check | S1, S2 and a minor | `c4720c6`, `fdc1685` |
| C51 `1c7e7aa` | accuracy review: no blocker or major, 9 minors | `a441e75` |

The one blocker, in C40, was fixed in the next commit. Most of the majors that followed sat at the edges of
behaviour or of tests: a sign-out that could stall behind a hung connection, a stale session read that undid a
sign-out, a focus spec that passed for another reason, a stale-answer guard no spec killed, a reload that could
leave a page on "Loading..." for good.

## Problems found along the way

- **A close that forbids reconnecting (C40, the blocker).** `HubConnectionRegistry` ended connections with
  `HubCallerContext.Abort()`. `Abort()` sets `allowReconnect` to false, so SignalR sends the client a close
  message that forbids reconnecting and the JavaScript client stops for good instead of running its reconnect
  policy. The design and the commit had assumed the opposite ("the client reconnects and resubscribes"), so
  after a password change, a sign-out elsewhere, a disable or any role change the dashboard would have stayed
  on "disconnected" until the page was reloaded. The tests could not see it: they counted `Abort()` calls and
  accepted any close message, and the first live check (above) only saw the socket close. The correctness
  review found it. `e913282` closes a connection through
  `IConnectionLifetimeNotificationFeature.RequestClose()`, the path SignalR itself takes when a ticket expires,
  which sends `allowReconnect: true`; `Abort()` stays as the fallback. The four wire tests and the
  ticket-expiry test now assert `allowReconnect` is true on the close message, and a mutation making `Close`
  always abort fails ten tests. The same review's minors: a role deletion notified nobody, so the API key's
  sockets stayed open on access it no longer had (the key's role may be custom); the hub closed a socket at the
  ticket's sliding expiry, so a socket opened late in a session could outlive the absolute lifetime by up to
  the idle timeout (the expiry the hub sees is now capped); the origin Warning logged the received, unauthenticated
  `Origin` whole, up to the header limit (now cut at 256 characters); and the drop loop had no per-connection guard. The second review
  found that the renewal claim was untested (the renewal test never had a cap that mattered), that a
  connection whose close threw was left open and never found again (it is now aborted as a fallback, with a
  new Error event 7322 if that throws too), that the logged `Host` was as caller-controlled as the `Origin`,
  and that the design still said "abort" in four sections, which `35abb82` reworded. The follow-up's own
  message about its live check was clarified in `35abb82`: it validated the server-to-library protocol with a
  client using the library's reconnect delays, not the SPA's hub service.
- **A valid session signed out by a race (C34).** `DashboardCookieEvents` read the identity readiness after the
  resolver had answered, so a store that became ready while a request was being resolved let the resolver
  answer null without checking and the event then saw a ready store and signed out a perfectly valid session,
  against the design's "a store outage costs a 401, never the cookie". Readiness is now read first
  (`83d99eb`), with a resolver that makes the store ready while it resolves in the new test. The same commit
  pinned that the sign-in time, which bounds a session's whole life, survives sliding renewal: every cookie
  test had used a fresh ticket the handler never renews.
- **A test that could not fail (C33).** `1d6b87f`: the stale-lock test asserted only that the health check
  returned within `MaxWait` plus a second, so a check that passed `CancellationToken.None` would have sat out
  the whole 2 s and passed. The bound became `MaxWait` minus 500 ms, and with the token dropped the test
  failed ("The check took 00:00:01.9794267").
- **Two `X-Frame-Options` headers (C37).** ASP.NET antiforgery adds `X-Frame-Options: SAMEORIGIN` to any
  response it issues a token on (the session read, login, logout and password change), and nginx sends
  `DENY`; browsers handle conflicting values inconsistently and may ignore the header. `c0a1ca4` sets
  `SuppressXFrameOptionsHeader`: the framing policy belongs to the nginx edge, and the API answers only JSON.
  With the option set back to false, the new test fails and the other seven header tests pass.
- **Scoped-list wording and the blank type (C38).** The second review of `e69deb8` found the design, the ADR
  and a code comment saying more than the code did (every shape's streams "primed and refilled", the lower
  bound reading as Redis-only, a total described as reduced when only the current page's rows can be), and a
  403 for a blank route type that named no type although the decision had been made for that blank value
  (`5e1bd67`).
- **The Angular 22 move, in context (`a8bbac3`, `39a622a`).** The audit gate failed with a high advisory,
  GHSA-ch52-4w7c-c8xp (`max-stale` handling can disclose cross-user cached responses), in every version of
  `http-cache-semantics`, which the dashboard reaches only through tooling (`@angular/cli` 21 to `pacote` to
  `sigstore`, `npm-registry-fetch` and `tuf-js` to `make-fetch-happen`). No override can fix it, because no
  version is unaffected, and the only release that drops the chain is `@angular/cli` 22; the maintainer chose
  the major upgrade over waiting. `ng update` moved every `@angular/*` package from 21.2.24 to 22.2.1 and ran
  three migrations: `ChangeDetectionStrategy.Eager` on all ten components (Angular 22 defaults to OnPush),
  `withXhr()` in `provideHttpClient` (the default backend became fetch), and an `extendedDiagnostics` block
  that was left out because the build is clean without it. TypeScript moved from 5.9 to 6.0.3, which compiler-cli
  22 requires; the Prettier reformatting the migrations produced was reverted so the diff held only the
  semantic changes; the `piscina` override from `22f04bf` was removed because the new CLI depends on 5.3.2
  itself; `engines.node` became `^22.22.3 || ^24.15.0 || >=26.0.0`. The Initial total went from 462.26 to
  481.34 kB, of which `withXhr()` is 3.4 kB. It landed between C36 and C37, and was verified on Node 24 and on
  Node 22 (22.23.3): `npm ci`, the audit, `ng build` and `ng test` (19 files, 427 tests). A review of it
  found what the first commit still assumed (`39a622a`): the web image's floating `node:22-bookworm-slim` tag
  could resolve to a cached 22.x below 22.22.3 and fail `ng build` inside the image (now `node:22.23-...`),
  CI's `setup-node` could pick a runner's cached older 22 (`check-latest: true`), the component schematic
  generated OnPush components (`angular.json` now defaults it to Eager, checked with a throwaway component),
  and two documents wrote the Node requirement in a way that read as if 25 worked and 26 did not.
- **A flake that was not a flake (`dd37bd1`).** In a quarter to a half of full `ng test` runs, all 39 specs of
  `saga-hub.service.spec.ts` failed together with `Cannot resolve '/hubs/saga'`, thrown by the real
  `@microsoft/signalr` instead of the spec's `vi.mock`. C44 hit it first (2 of 8 full runs at `fabc67c`, before
  C44 existed). The unit-test builder runs Vitest with `isolate: false` and code splitting, so a worker that
  had already loaded the real hub service through another spec (the list, the detail, and now the auth
  service import it) kept it in its module cache and the hub spec's mock was registered too late. Over 20 full
  runs of the default configuration 12 failed (5 of the first 10, 7 of the second 10), every failure the same
  1 file and 39 tests; alternating the three variants under load, default failed 7 of 10 runs (11.9 s per
  run), `--isolate` 0 of 10 (21.1 s) and `--no-splitting` 0 of 10 (11.4 s). The fix is
  `"options": { "splitting": false }` on the test target in `angular.json`, with 24 consecutive green full
  runs afterwards (12 in the working tree, 12 in a clean checkout). `isolate: true` was the more thorough
  alternative, about 77 % slower and growing with every spec file; `splitting: false` costs nothing measurable
  but makes the builder print a deprecation notice on every run, and the option (and the hub spec's comment
  naming it) should go when Vitest 5 lands. The cause rests on the failure signature and on the two options
  making it vanish: which spec ran first in a failing worker was not traced.
- **Eleven findings in the auth service (C44, `2a2a64b`).** Two majors. `logout()` could stall: it awaited
  `hub.stopAndReset()` (whose last step awaited the in-flight negotiate, up to about 100 s) before signing out
  locally and posting, and the POST had no timeout, so "Sign out" could do nothing for that long. And a session
  read that was sent while authenticated and answered after a local sign-out undid the sign-out. The fix for the
  second is an identity epoch: moved by every local sign-out and by each identity-changing POST when it
  settles, captured before each session read, so an answer from before the change is dropped; the C46
  interceptor reads the epoch when it sends a request and a 401 to a request sent before the latest identity
  change no longer signs the new session out. The minors included an `accept()` that could throw on a
  session-shaped body without `user`, a `problemOf` that threw on an `errors` key named `constructor`, a refused
  identity `POST` that left the SPA stale (a lockout signs the cookie out and answers 400), and a changed
  password announced as an expired session. Two of the follow-up's mutations first survived and got specs. (In
  C44's own pass, `0f3bb28`, two mutations cascaded into 35 failures, because a spec that leaves a request open
  made its `afterEach` throw and skip Angular's reset of the test module; the `afterEach` now resets it in a
  `finally`.)
- **Login, setup and account (C45, `94d6c40`).** The setup-code input allowed 64 characters, which truncated a
  preset `Dashboard:Setup:Code` of 65 to 128 characters (the API accepts up to 128 besides spaces and hyphens);
  and a `409 setup_unavailable` showed nothing until a background session read landed, because the old spec
  flipped the session before the mock threw, an order that never happens. Minors included a wildcard route,
  duplicate login-poll navigations, a lockout that the page reported as an expired session, password managers,
  a 30 s bound on the identity `POST`s, and the stale-chunk handler reloading while offline.
- **The bundle budget (C45, C46).** The plan's headroom was 18.7 kB under the 500 kB warning budget after the
  Angular 22 move (481.34 kB), and every remaining SPA commit had to fit eager code into it. C45's pages
  and the eager auth service and guards, with forms that used Angular's `NgForm` and its validators (which
  nothing else uses, and which cost 6.8 kB of the initial bundle), came to 503.07 kB, over budget; keeping the
  forms template-driven but with `ngNoForm` and the checks made in the component brought it to 496.28 kB, and
  `94d6c40` to 496.75 kB, 3.72 kB (then 3.25 kB) under budget. With the interceptor, the initializer, the top
  bar, the user menu and the guards on the saga routes, C46 would have been 506.18 kB with both saga pages
  eager. The plan's instruction was to lazy-load the pages or stop, not to raise the budget: the saga detail
  route became `loadComponent` (the heaviest page: timeline, map, inspectors) while the list, where everyone
  lands, stayed eager, and the Initial total came to 430.97 kB (69 kB of headroom), with `saga-detail` a lazy
  chunk of 74.60 kB. `authGuard` still runs before the chunk is requested, so a visitor turned away never
  downloads it (pinned by a spec that counts the loader, and seen live: the chunk was fetched only after the
  sign-in `POST` answered). After that every new page of the administration area is lazy and the Initial total
  stayed between 432.81 and 437.00 kB for the rest of the slice. One figure was left unexplained: `b3b67e1` came
  out 1.36 kB smaller with no code removed; `e89a1ef` looked at the esbuild metafile and found the bytes
  attributed to `@microsoft/signalr`, `auth.service` and `saga-hub.service` fell (about 4 kB less for
  signalR) while Angular core, router and common rose, and did not find out why signalR's modules come out
  smaller.
- **Tests that passed for the wrong reason (a pattern).** Besides the C40 and C33 cases above: the C49 focus
  spec and the same spec on the team page (C50) passed because `filled()` had left the focus on the grant's
  role select and jsdom's `click()` never moves focus, while the code asking the editor to focus ran in the
  same tick as the errors and found nothing (`c6ca8ca`, `537134e` fixed both with `afterNextRender` and specs
  that move the focus away first); one C47 spec "ignores a 400 with a maxPage" asked for page 2 against a
  `maxPage` of 20 and never reached the clamp branch (`4ac938d`); the removal of the saga-types error
  handler was caught only by Vitest's unhandled-error trap, not by a spec (`952269e` made it deterministic
  with fake timers); and `e89a1ef`'s review found `ConfirmButton`'s "keyboard alone" test vacuous.
- **What the C47 and C50 live checks found, and the follow-ups.** `952269e` (the five majors and the minors of the two
  reviews, with m11 skipped, plus the two live findings): the list now re-reads on every later reconnect (the first connect
  still reads nothing), request tokens drop a stale list answer (a late 400 with a `maxPage` could rewrite
  page, cap and URL), the map takes `canViewData` and prints the failure card's error text only with it, a
  forbidden detail page no longer opens the hub, and the clamp carries the refusal notice. `4ac938d` (one
  major, a regression): a detail answer arriving after the page was destroyed called `subscribe()` again, so
  the hub kept a record of a group nobody was in and re-sent it on every reconnect; a stale 403 was judged by
  answer order instead of send order. `c4720c6`: S1 above, a session read on later hub connects in both pages.
  `fdc1685`: S2 above; a natively `disabled` button that holds the focus loses it (Chromium sends it to
  `<body>`), so every submit button that disables itself during a request (user, role, team, sign-in, setup,
  account) now uses `aria-disabled` and a guard, a refusal that is not about a field puts the focus on the
  banner, and `AdminShell` moves it to the first `<h2>` of the page after a navigation that lost it. The Save
  case was reproduced first in headless Chromium on `c4720c6` with the request held for 700 ms.
- **The administration area's own follow-ups.** `e89a1ef` (3 majors): the shell's "Try again" on the warning
  that follows a successful change called a full `load()`, which destroyed the page and any draft typed since
  (a new `AdminStore.refresh()` reloads only the three lists); a save overwrote what was typed while it ran
  (a draft `revision` now decides whether the answer re-seeds the form); and a reload that superseded `load()`
  could leave the shell on "Loading..." for good (a reload while nothing is loaded is now a full load, and
  every read fails after 30 s with a sentence). `c6ca8ca` (1 major, 9 minors): the first focus fix above, a
  server answer applied to fields only if the draft is the one that was sent, "Locked" following the clock
  (`lockoutClock`) instead of the data, "Add grant" no longer starting as a role that holds only `access.manage`,
  a catalogue-driven editor and summary (no hard-coded `access.manage`), a typed-but-not-added saga type added
  on Save.
  `537134e` (1 major, 8 minors): after a 400 about a member the page reads the lists again, so a user deleted
  since the lists were read turns into a labelled unknown-member row; a "Saving replaces the team's members and
  access" hint; Enter on a member checkbox no longer submits a `PUT` that replaces the whole team.
- **Documentation errors (C51).** `a441e75`: nine inaccuracies in `1c7e7aa` found by an accuracy review, each
  checked against the code before it was fixed: a login failure and the 400 bodies do not end with the
  documentation pointer (only an `unauthenticated` 401 and a 403 do); `GET /api/auth/session` is 503
  `identity_unavailable` until the store is ready, not "always 200"; "consecutive" wrong passwords, and where
  the count restarts; a seed that cannot be applied does not stop the API (`identity` is degraded with the
  reason and setup stays closed); the framework-owned options list was incomplete; the first administrator can
  be claimed with the code only on a fresh identity volume. It also replaced the two-stacks sentence, which
  `1c7e7aa` had written from the design (and flagged "not verified" because the C50 evidence did not exist
  yet), with what C50 observed. The XML comment on `AuthProblems.DocumentationPointer` ("every 401 and 403
  body ends with it") has the same overstatement and was reported, not edited, since the commit changed no
  code.
- **Commit messages that had to be corrected.** `64ba0ac` said its new step ran through the same origin
  (port 4200); the sign-in and hub checks do, the `/health` and `docker compose config` checks do not, since
  nginx has no `/health` route (`4dde4b1`). `e913282`'s
  live check was clarified (`35abb82`, above). `7e61f16` called the team page's effective-access preview
  "optional in the plan"; it is not in the plan, it was added because it is cheap (`537134e`). `1c7e7aa` said
  the configuration table lists "all 20" keys; it lists 19. `a441e75` says the miscount counted
  `Dashboard:ApiKey` twice, while the running notes kept with the work say it counted the environment-variable
  spelling `Dashboard__Identity__Sqlite__Path` (which does appear in the API's Dockerfile) as a separate key.
  Which of the two is right was not established here; a grep of `dotnet/src` for `"Dashboard:...` strings,
  run for this record, finds 19 distinct keys, so the count of 19 stands either way.
- **A process incident: an amended pushed commit (C47).** After `731b724` had been pushed, its author amended
  it, changing only the verification paragraph's mutation count (the amended commit, `9b4a3c4`, has the
  identical tree, `192b9d24...`). It was never pushed: the local branch was reset to the pushed commit with
  `git reset --keep`, no force push was made, and the amended commit survives only in the object store and the
  reflog. The pushed body therefore still says "37 one-token mutations ... each fail exactly their own
  specs". The true figure is 36, with the 37th (removing the saga-types error handler) caught only by Vitest's
  unhandled-error trap; `952269e` corrected that, misquoted the pushed text, and `4ac938d` corrected the
  correction. The brief given to every later agent was changed to forbid amending any commit.
- **A refusal worked around before the pause (C38).** The handoff file of the working plan records that an untracked empty file
  appeared in the working tree during C38, that the permission system refused a delete, and that the agent then
  removed the file another way. Nothing tracked was affected; it is noted here because a refused action was
  worked around. (The two refusals in this slice's later work were not worked around.)
- **The sample's business failure fails again.** Not a defect, but observed in every run that retried: a retry
  re-runs the step that failed with the same message, and when the failure was decided by the message alone the
  saga returns to `Failed`. C37: version 1 to 3 and back to `Failed`; C46: `Failed`, `Gathering`, `Failed` in
  288 ms at version 4; C47: version 1 to 3, with `ManualRetryRequested` in the timeline. The design states
  it, and the user guide has to say it.

## Where the build left the plan

- **Close for reconnect, not abort.** Design sections 8.3, 8.4, 8.6 and 10 said the hub aborts connections on an
  access change; the code closes them with a close message that allows reconnecting, and `35abb82` reworded the
  design. ADR 0006 still says "abort" in its sub-decision 9 and in its positive consequences (see below).
- **The scoped list's bounds are a refinement of §8.7's fixed "at most 50"** (stated in the body of `e69deb8`):
  scoped names beyond the shape's type bound are narrowed to the types that have run (read through a
  five-second cache), so a user granted 11 to 50 named types is not refused every status filter, status sort or
  kind filter when ten or fewer of those types ever ran.
- **The cookie name limit is 128 characters**, not 64: the default compose project name is the checkout
  folder's name, and with 64 a folder name of 51 characters or more failed composition and left
  `dashboard-api` restarting. A test pins 128 accepted and 129 rejected.
- **Refinements in the SPA that keep a decision's intent**, each stated in its body: the hub service ignores the
  subscribe methods' booleans (the record is kept, because the resubscribe on reconnect is the recovery);
  the session probe answers false only when a refresh finds the session anonymous (the plan's sketch would have
  stopped the hub while the API is unreachable at start); `login` rejects with a synthetic 401 when the POST
  succeeded and the session is still anonymous, and with a status-0 "could not be confirmed" when the read
  failed; `authGuard` and `adminGuard` do not ask the server again for an `unreachable` session (the first read
  had already waited out 8 s, and the login page polls by itself); the interceptor acts only for the page's own
  origin and never retries a safe method or a request whose refresh left the token unchanged; the saga detail
  route is lazy; the retry hint shows only for a saga a retry would accept; a 404 on an administration page
  shows a message and a link instead of navigating, because the message would not survive the navigation.
- **A new grant starts as the least-privileged role** (the role holding the fewest permissions), not the first in
  the API's order, which is the administrator's and would have made "Add grant, then All saga types" one click
  from an administrator grant; Save waits for the choice (`cec2a40`). `c6ca8ca` then sorted roles that hold a
  permission the catalogue does not scope after the others.
- **Added beyond the plan's file list:** `util/session-access.ts` (one rule for the service and the test
  helper), `util/failure-text.ts`, `util/page-lifecycle.ts`, `util/lockout-clock.ts`, reason notices for
  `setup`, `password` and `locked` on the login page, and the identity epoch.
- **The effective-access preview on the team page** and a shared grants editor reused by the user and team
  pages. The access summary was reused for the preview (two optional inputs, `perspective` and `origins`)
  rather than forked.
- **Test isolation by `splitting: false`** rather than `isolate: true`, with the trade-off above.
- **The design and the ADR still carry wording the code contradicts**, found by the documentation commit and
  reported, not edited, for the final commit to correct: ADR 0006's "Abort on change" and its "live
  connections are aborted"; the design (8.4 and 10) and the ADR's first accepted residual risk calling a
  restart with `Dashboard:Lockout:MaxFailedAttempts=0` the escape from a lock, when the code keeps refusing
  sign-in until a lock already in force ends (the setting stops new locks only; an administrator's unlock or
  `Dashboard:Admin:ResetOnStart` ends one at once, which `docs/dashboard.md` says); and the design's
  `ProtectKeysWithCertificate` as the way to encrypt the key ring, which vSaga ships no configuration key or
  code for.

## Unverified and open

None of the following was observed in the recorded runs, or was observed and left as it is.

- **The first-run setup flow, live.** The setup code logged once at Warning, `POST /api/auth/setup` with it, the
  setup page, the "setup closed" notice, `ResetOnStart` and the sign-in it performs were exercised by the
  .NET tests and by a mocked-API browser check of the pages (C45: 25 checks); no live stack was started
  without a seed to claim the dashboard, and the C37 live run, which was seeded, logged no setup code.
- **A deep-linked forbidden detail page recovers only on visibility.** A page opened by URL straight into a
  403 never starts the hub, so it notices a later grant when the session is read again (the tab shown, or a
  403), not at once (stated in `4ac938d`, left as a limit).
- **The hub's unsubscribe-before-start race.** `subscribeToSaga` writes its record after awaiting the
  connection, so an unsubscribe before the very first start finishes is overtaken and leaves a record. The fix
  belongs in the hub service, which `4ac938d` did not change.
- **The saga-types filter is fetched once.** A widened scope does not add its types to the list's dropdown
  until the page is opened again (`c4720c6`; review finding m11, skipped); a session read on reconnect now
  makes that cheap to add.
- **The team page applies a server 400 to its fields whatever was typed while the request ran.** `c6ca8ca` said so
  for the team page and left it "for the C50 follow-up"; `537134e`, which is that follow-up, does not take it up,
  and `team-edit.ts` at `fdc1685` compares the draft's `revision` only on the success path, not in `refused()`.
  The user page does not have the problem (`c6ca8ca`).
- **Administration writes have no timeout of their own.** The store bounds its reads at 30 s; a hung write keeps
  the form busy, as on the role page (`cec2a40`). A typed password stays in the draft until the store has
  answered the save, for the same reason (`c6ca8ca`).
- **Enter on a saga-type box or a radio inside the grants editor** can submit the page's form, which on the team
  page is a `PUT` that replaces the whole team; `537134e` noted it and left it to the editor's owner.
- **An idle administration tab with no live hub** does not end on `/login` after a volume wipe until its first
  write, which answers 401 (R1 in the C50 run); a tab with a live hub did.
- **Every switch between two stacks costs a 400** (S3), healed silently by the single retry. It is the
  intended cost of one shared `XSRF-TOKEN` cookie, not a defect, but it shows in the network log and as one
  Chromium console error each time.
- **`ProtectKeysWithCertificate` is not shipped.** The Data Protection key ring in the identity store is not
  encrypted at rest (the framework logs `No XML encryptor configured`, seen in the C37 run and at start in the
  C47 run); `docs/dashboard.md` says a deployment must add the framework call itself, and that path was never
  exercised. A copy of the identity file is a copy of the credentials and the session keys.
- **Two `X-Content-Type-Options: nosniff` headers** (nginx's and the API's, with the same value) were seen in the
  C37 run and left as they are.
- **An overstating XML comment.** `AuthProblems.DocumentationPointer` is still documented as "The sentence every 401
  and 403 body ends with", which is false for the invalid-credentials 401 and the 400 bodies; `a441e75` reported
  it and changed no code.
- **Documentation statuses.** `docs/README.md` still lists the design and ADRs 0006 to 0008 as not yet
  implemented, and ADR 0006 and the design still carry the two wordings above; the final commit of the work
  owns both.
- **Redaction against a real error message.** Observed live only on one row inserted by hand; no saga the sample
  produces carries an error message.
- **`maxPage` 0 and "return to the last good page" on a 400 with rows on screen** were not reached in a browser
  (the sample has 7 saga types, the refusal needs more than 10 visible; the pager's input is capped at the real
  page count); they rest on the specs.
- **The scoped list on other providers.** The live runs used the Postgres base stack, and no recorded run used
  Redis or MongoDB with a scoped caller. The scoped merge and its bounds were checked against fakes, including
  one that ignores the type filter and a walk of every sort arm page by page against an independent full sort.
  The bounds exist because on Redis the unranked shapes read every member of every visible type
  (the plan's feasibility review); this slice did not measure that.
- **Time-based session limits.** The 24 hour absolute lifetime and the 480 minute idle window were exercised by
  tests that choose the ticket's issue and expiry times and by two hub tests that wait about 10 s each; no real
  session was left to expire. The `RequireHttps` mode (the `__Host-` cookie prefix and HSTS) is covered by tests
  only; no live run used TLS.
- **A disabled user's open browser tab.** The disable-then-401 path was run with a Node client (C40); the
  browser reached the same 401-on-negotiate state through the wipe in C46 and C50, which is inference, not an
  observation of that exact case.
- **A real screen reader and a human at the keyboard.** The accessibility checks read the DOM and Playwright's
  keyboard; the focus findings (S2) are Chromium's behaviour for a natively disabled button and were not
  checked in other browsers.
- **Dev server and compose UI sharing a sign-in.** `dashboard-web/README.md` says they share one because cookies
  are scoped by host, not port; that follows from the cookie rules and was not exercised.
- **Overlays other than Wolverine** in the two-stacks run, and the `brighter`, `masstransit`, `http`, `mongo`
  and `redis` stacks with sign-in: their rendered cookie names were checked by `docker compose config` only.
- **Node 22 gate findings** (this record's own run): `ng test` prints `NG8113: All imports are unused` for the
  `EditStub` in `admin-shell.spec.ts` (line 314, from `fdc1685`), a one-line cleanup for a later commit that this
  documentation commit does not make; the `splitting` deprecation notice is expected (`dd37bd1`); the run was
  made once, on a warm npm cache, so it says nothing about flakiness under load (the commit messages report
  one 5 s timeout of `app.routes.spec` in six runs, while another agent's mutation run kept the machine at a
  load of about 10 on 4 cores).
- **A full-suite .NET count for the finished slice.** See the gates above: 1627 is the last recorded total; the
  later per-project counts are in the commit messages, and nothing was re-run for this record.
- **The saga detail's Retry button** still uses native `disabled` while it retries, so focus drops to `<body>`
  (`fdc1685` left it: another change was editing that page; the global `.btn[aria-disabled='true']` style
  exists since that commit). The list's Previous and Next are state-disabled, which is fine.
