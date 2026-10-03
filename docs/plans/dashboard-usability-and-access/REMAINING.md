# Remaining work: dashboard usability and access

**Status:** paused 2026-10-03 on branch `dashboard-usability-and-access`, HEAD `852a901` (C40) before this
file was committed. Implementation runs to C40; C41 to C58 remain, together with the finishing steps of
C40 and the slice-level mutation checks.

This file is a handoff. It lets a person, or a new session with no access to the notes the earlier work
used, finish the implementation. It lives in `docs/plans/dashboard-usability-and-access/` and is deleted
with that folder by the final commit (C58), like `PLAN.md` and the blueprints beside it.

Read it together with:

- [`PLAN.md`](PLAN.md): the reconciled plan. It wins over everything else.
- [`blueprints/`](blueprints/): the workstream blueprints and the three reviews (security, consistency,
  feasibility). Where a blueprint disagrees with a blocker or major review finding, the review wins.
- [`docs/design/dashboard-usability-and-access.md`](../../design/dashboard-usability-and-access.md): the
  durable design, and ADRs 0006, 0007 and 0008 under `docs/adr/`. Where the design is more specific than a
  blueprint (names, defaults, wire shapes), follow the design.

Commit numbers (C01 to C58) are the ones in design §12.

---

## 1. What is done

Gates at `852a901`: `dotnet build` 0 warnings, `dotnet test` 1613 passed, `npm audit` 0
vulnerabilities, `ng build` initial total 481.34 kB, `ng test` 427 passed.

`git log --oneline main..HEAD`, oldest first, grouped by slice:

**Prerequisite outside the plan's numbering**

- `22f04bf` Override piscina to 5.3.2 and patch brace-expansion for npm audit. `main`'s audit gate was
  red. The piscina override was later removed by `a8bbac3`.

**Records (C01)**

- `6f44c8b` Add the dashboard usability and access design and ADRs 0006, 0007 and 0008

**Packaging (C02 to C09)**

- `c99a547` C02 Make the dashboard SPA same-origin: relative URLs, a dev proxy and the dev server on port 4201
- `9d79b93` C03 Make the dashboard API's CORS policy opt-in through DashboardEdge
- `d242292` C04 Add the dashboard-web container image and guard the built index.html against inline handlers in CI
- `2fd08a9` C05 Run the dashboard UI from compose, with every overlay on its own UI port and the dashboard ports bound to loopback
- `899ce75` C06 Honour forwarded headers from the dashboard's trusted proxies
- `736a76d` C07 Build the compose images and smoke-test the dashboard UI origin in CI
- `ef9e09a` C08 Document the one-command demo and how the dashboard UI is served
- `509ad0f` C09 Record the dashboard UI joining compose (history file `docs/history/dashboard-ui-in-compose.md`)

**Timeline, map jump, snapshots and targeted retry (C10 to C30)**

- `f2cbe38` C10 Extract the detail page's pure helpers: state JSON, JSON diff, time formatting and entry-type labels
- `50c0583` C11 Fold the saga timeline into steps with their state snapshots, and exclude test helpers from the app build
- `feae3d1` C12 Show the timeline as steps with labelled local times
- `b38752d` C13 Jump from a timeline entry to the saga map positioned on that entry
- `bbf3dcf` C14 Add direct unit tests for SagaMapBuilder
- `3eda0ea` C15 Detach a failed event-log append from EF Core's change tracker
- `e3379f0` C16 Add the StatePersisted entry type and the shared snapshot entry builder
- `46dbce4` C17 Skip StatePersisted entries in the saga map
- `4fce969` C18 Add a timeline and map redaction seam and stop pushing payloads and error messages over SignalR
- `129115e` C19 Stamp the stored sequence number on timeline entries pushed to the dashboard
- `899dd9f` C20 Stamp the inbound message id on step bookkeeping entries and on entries logged through the saga context
- `507f661` C21 Record a StatePersisted snapshot after every committed saga transition
- `1f1e056` C22 Record the state a dashboard retry reset leaves behind
- `417a64e` C23 Target a redriven message at one saga type and record every inbound message's payload
- `3a817e6` C24 Retry a failed saga from the step that failed, addressed to that saga type only
- `f8e91f0` C25 Show each step's saga data: what changed and the full state after the step
- `25385b6` C26 Replace the Data tab with a Saga data bar: data at start, at end, and a comparison
- `c633055` C27 Coalesce live updates into one refresh that also re-reads the saga, and show load errors
- `f28636e` C28 Mark the failed step in the timeline and on the map, and name it in the retry confirmation
- `61354b9` C29 Document state snapshots, the labelled timeline, the map jump and the targeted retry
- `73cbce6` follow-up to C29: Correct the retry, budget and upgrade-order wording in the snapshot docs
- `231688a` C30 Record the timeline, map jump, state snapshot and targeted retry slice (history file
  `docs/history/timeline-labels-map-jump-and-state-snapshots.md`)

**Authentication, so far (C31 to C40)**

- `e9eea61` C31 Add the dashboard identity project: model, store contract and EF Core store
- `69c993f` C32 Add the dashboard identity services: passwords, sign-in verification, access evaluation and administration
- `e6228c3` C33 Create the identity database at start-up and report it in the health check
- `1d6b87f` follow-up to C33: Make the stale-lock test fail when the health check ignores the probe's token
- `8b7a39a` C34 Authenticate dashboard requests through a cookie-or-key policy scheme and protect every endpoint by default
- `83d99eb` follow-up to C34: Read identity readiness before resolving a session and pin the sign-in time across sliding renewal
- `ed8f939` C35 Add dashboard sign-in: session, login, logout and password change, with antiforgery, rate limits and lockout
- `82c4797` C36 Create the first dashboard administrator from configuration or a one-time setup code
- `a8bbac3` prerequisite: Move the dashboard SPA to Angular 22 for the http-cache-semantics advisory. A high
  advisory (GHSA-ch52-4w7c-c8xp) in every version of `http-cache-semantics`, reached only through the
  Angular 21 CLI, failed the audit gate; the maintainer chose the major upgrade (design §12 note).
- `f25b1ec` C37 Persist the dashboard identity on a volume, run the API as non-root and seed the demo administrator
- `39a622a` follow-up to `a8bbac3`: Pin the Node 22 patch Angular 22 needs and keep generated components eager
- `c0a1ca4` follow-up found by the C37 live check: Leave framing headers to nginx: stop antiforgery adding X-Frame-Options to API responses
- `e69deb8` C38 Enforce saga permissions on the dashboard endpoints: scoped lists, redaction and retry attribution
- `8d69429` C39 Add the dashboard access administration endpoints
- `5e1bd67` follow-up to C38 (second review round, five minor findings): Name a blank route type in the 403 and correct the scoped-list bounds wording
- `852a901` C40 Check access on hub subscriptions, guard the hub's origin and drop connections when access changes

History files already written for this branch: `docs/history/dashboard-ui-in-compose.md` (C09) and
`docs/history/timeline-labels-map-jump-and-state-snapshots.md` (C30). The two still to write are
`dashboard-sign-in-and-access.md` (C52) and `dashboard-guide-mode-and-user-guide.md` (C57).

---

## 2. How each remaining commit is done

### 2.1 Gates (every commit, all green before `git commit`)

Run from the repository root and read real exit codes (never pipe a test command through `grep` or `tail`
without capturing the exit code):

- `dotnet build dotnet/VSaga.slnx -nologo`: `0 Warning(s)` and `0 Error(s)` (warnings are errors).
- `dotnet test dotnet/VSaga.slnx --no-build -nologo`: exit 0. About 75 s; Docker must be running for the
  Testcontainers suites. If one testhost aborts with "Internal CLR error (0x80131506)", rerun that project
  alone, then the full suite: a known transient.
- In `dashboard-web/`: `npm audit --audit-level=low` (0 vulnerabilities), `npx ng build` (no `WARNING`, no
  budget warning; note the Initial total), `npx ng test --watch=false` (all pass).

Run all of them on every commit, whatever it touches (about three minutes). At the end of each slice (after
C50 and after C56) run the SPA gates again under Node 22, which CI uses:
`npx -y -p node@22 -- npm ci`, then `npx -y -p node@22 -- npx ng build` and
`npx -y -p node@22 -- npx ng test --watch=false`. Angular CLI 22 refuses Node below 22.22.3.

### 2.2 Commit conventions (`CONTRIBUTING.md`)

- One logical change per commit. Subject in the imperative present tense and specific (match
  `git log --oneline`). The body explains what and, above all, why: trade-offs and constraints.
- Last line of every message, after a blank line: the `Co-Authored-By:` trailer of the agent that did the
  work, as the earlier commits carry.
- Write the message to a file and run `git commit -F <file>`. Stage paths explicitly (`git add <paths>`),
  never `git add -A`. Never `--no-verify`; if a gate is red, fix the cause.
- `docs/plans/` is touched only by this handoff and by C58, which deletes it.
- The maintainer asked on 2026-10-03 for the branch to be pushed, so from the commit that adds this file it
  is on the remote (this replaces PLAN.md's and design §12's "nothing pushed"). Never amend or rewrite a
  pushed commit: a fix to an earlier commit is a follow-up commit whose subject names what it corrects and
  whose body lists the findings it addresses. Amend only the unpushed commit you just made, and only when
  fixing it; if the permission system denies the amend (it has denied `git commit --amend` before), do not
  retry or work around it: make the follow-up commit. Push again only when the maintainer asks.

### 2.3 The per-commit loop

Each commit goes through the same steps:

1. **Implement.** Start from a clean tree (`git status`, `git log --oneline -5`). Read the commit's
   subsection below, the blueprint and review sections it names, the design sections, and the code you
   change. Implement it completely with the tests listed. New tests must fail without the change; where
   cheap, prove it with a one-token mutation (see 2.4). Never weaken or delete an existing test unless the
   commit says it is replaced, and say so in the body. If a PLAN decision proves unworkable, stop and report
   it instead of changing the decision; small refinements that keep a decision's intent are fine and are
   stated in the body. Run every gate, commit one commit, check `git status` is clean.
2. **Two read-only reviews** of the commit (no build, test, edit or commit while reviewing):
   - SPEC lens: every item of the commit's scope and every named review finding is implemented as the
     reconciled rules and the design say (not a superseded blueprint); no scope from later commits (major);
     docs and comments touched are accurate; the message follows the conventions and its claims are true.
   - CORRECTNESS lens: logic errors, edge cases, concurrency, security, error handling, analyzer-sensitive
     code; tests exercise the behaviour (would they fail if the change were reverted?), assertions are
     specific, no timing flakiness; existing tests are not weakened.
   - Severity: blocker (wrong behaviour, security hole, a PLAN or review decision violated, or a later
     commit would break); major (a required piece or its test missing or ineffective, a mandatory
     convention broken); minor (everything else worth fixing). Each finding names severity, file, problem
     and the exact fix.
3. **Fix.** Apply every blocker and major finding and each minor one you agree with (say why you skip
   any). Run every gate. Commit the fix, updating the message body if a claim in it changed (2.2: amend
   only an unpushed HEAD, else a follow-up commit).
4. **Live**, where the commit has a live check: perform every step against the current HEAD and record
   every command and the relevant part of its real output in an evidence file (2.5). Facts only: never
   write an expected result as observed. Bring stacks down afterwards (no `-v` unless the check requires
   it). Browser checks use Playwright; Playwright saves only inside the repository (`.playwright-mcp/` is
   ignored by git), so copy screenshots to the evidence folder. A failed check is reported with what was
   observed and the suspected cause, then fixed through steps 1 to 3.
5. **Mutation checks** at the end of a slice (sections 5.3 and 5.19): see 2.4.

### 2.4 Mutation checks

For each mutation: apply it with a one-token replacement (`sed -i` or a PowerShell `-replace`) or
`git stash push -- <file>`, never a lasting hand edit; rebuild; run the named test projects (or the whole
suite); record exactly which tests fail; restore with `git checkout -- <file>`; rebuild. A mutation passes
when exactly its own tests fail. Never run `--no-build` after a mutation. Restoring by copying a backup
back keeps the old timestamp and MSBuild then reuses the mutated DLL: restore with `git checkout`, or touch
the file (`(Get-Item f).LastWriteTime = Get-Date`) before rebuilding. At the end `git status` is clean and a
final build plus the affected tests are green. Record everything in `mutations-<slug>.md` in the evidence
folder. Mutation runs are not committed.

### 2.5 Evidence

History files (C52, C57) are written only from evidence of live runs and mutation checks, plus commit
bodies. Keep evidence outside the repository, in a scratch folder of your choosing: one file per live check
(`C41.md`, `C46.md`, ...) and one per mutation pass (`mutations-auth.md`, `mutations-guidance.md`), with
screenshots beside them. The evidence gathered before the pause for this slice (C33, C37, and the C38 and
C39 mutation runs) is not in the repository; Appendix A preserves its facts for C52.

### 2.6 Tooling quirks that still matter on this checkout (Windows)

- Git Bash has had no coreutils and no `git` on `PATH` in recent sessions. Use PowerShell for `git`,
  `dotnet`, `npm` and `docker`.
- Docker Desktop must be running for `dotnet test` and every live check. If it is not running, start it
  (`Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe"`) and poll `docker info` until it
  answers.
- `python - <<EOF` hangs in the agent's Bash; use node or the Edit tool.
- `jq` is not installed locally (CI's Ubuntu runner has it). Exercise a `jq` expression with
  `docker run --rm -i ghcr.io/jqlang/jq <filter>` or with node.
- Most source files are CRLF. Use an editor or the Edit tool; `perl -pi` or `sed` with `$` or `\n` miss on
  CRLF and mangle Windows paths. `perl -CSD` corrupts non-ASCII text. Bash heredocs containing an
  apostrophe fail; write commit messages to a file instead.
- A literal backslash-u sequence typed into some tools is decoded to the character before it reaches disk;
  for golden JSON, build it from a character code and check the bytes.
- Analyzers: TreatWarningsAsErrors with Sonar, Meziantou and AsyncFixer. MA0051 fails a method over 60
  lines (`SagaOrchestrator.HandleStepFailureAsync` and `SagaChangePollingService.PollOnceAsync` sit at 59:
  extract helpers, never add lines). A growing minimal-API lambda becomes a named static handler. Culture
  rules (CA1304, CA1311, CA1862, MA0011) on EF expression trees: pragma-disable that one line with a
  comment. Sonar S125 flags a `//` comment line ending in `;`. Sonar wants public exception types (S3871).
  Credential and cookie rules (S2068, S2092, S3330): neutral names or a justified pragma, as in
  `ApiKeyAuthenticationHandler.cs`. XML comments must never contain `--`.
- `WebApplicationFactory`: a setting `Program.cs` reads while composing is set with `UseSetting`, not
  `ConfigureAppConfiguration`.
- Foreground `sleep` is blocked in the agent's Bash; poll with a timeout or run in the background.
- Compose ports: the base stack uses 5433 (Postgres), 5672 and 15672 (RabbitMQ), 5080 (API) and 4200
  (UI); overlays use 5180 to 5680 (API) and 4300 to 4800 (UI), all dashboard ports on `127.0.0.1`.
  `docker compose down` your stacks when a check ends; never `down -v` a stack you did not start unless the
  check requires it. Unrelated containers on the machine (a `postgres` on 5432 and a `pgadmin` on 5050)
  have been left alone.

---

## 3. Constraints that bind the remaining SPA work

**Angular 22.** The SPA is on Angular 22.2.1 and TypeScript 6.0 since `a8bbac3`; the blueprint's Angular
21.2.24 citations are superseded.

- Angular 22 defaults components to OnPush. Every existing component declares
  `changeDetection: ChangeDetectionStrategy.Eager`, and every new component must declare it explicitly too,
  so the specs' `detectChanges` assumptions hold everywhere. Angular 22's `ng generate component` defaults
  to OnPush; `39a622a` set `"changeDetection": "Eager"` for the component schematic in `angular.json`, but
  check what was generated all the same.
- The HTTP client keeps the XHR backend: `provideHttpClient(..., withXhr())` stays.
- `CanMatchFn` takes a required third argument, `currentSnapshot`. Guard specs that call a guard directly
  must pass one.
- The XSRF interceptor behaves as before: same-origin requests only, cookie `XSRF-TOKEN`, header
  `X-XSRF-TOKEN`. Only its file lines moved.
- `@angular/forms/signals` is public API now; template-driven forms (`FormsModule`) are still the plan.
- `saga-list.html` still uses the deprecated `*ngIf`/`*ngFor`. It compiles; leave it unless the commit
  rewrites that template anyway.
- Node: Angular CLI 22 exits on Node below 22.22.3. The web image builds on `node:22.23-bookworm-slim`
  and CI's `setup-node` uses `check-latest: true`.

**Bundle budget.** The initial bundle is 481.34 kB against a 500 kB warning budget (about 18.7 kB of
headroom). Keep new pages, the admin area and the guide overlay in lazy chunks (`loadComponent`,
`loadChildren`, `@defer`), keep eager additions minimal (the auth service, interceptor, guards, top bar and
guide toggle are the eager pieces), put shared CSS in `src/styles.scss`, and report the Initial total in
every commit body. Do not raise the budget; if a required eager piece cannot fit, stop and report it. Each
component stylesheet also has a 4 kB warning budget (`anyComponentStyle`).

**No new npm dependency.** The plan allows none (the tour is hand-rolled).

**No `data-tour` attributes outside the guidance commits.** C42 to C52 add none. C53 to C55 add them, using
only the vocabulary in design §9.1, defined once in `GUIDE_ANCHORS`.

**Shared names (one name each).** `.topbar-end`; `.btn` with `.btn--quiet` (not `.btn-quiet`) and
`.btn--danger`; `banner banner--warning` and `banner banner--error` (the base `.banner` carries the
padding); pure helpers under `src/app/util/`; the data gate is a `canViewData` input; `PermissionKey` is the
one permission type (no separate guide or admin permission type). `src/app/testing/` is excluded from the
app build and from the web image (`.dockerignore`).

**Wire contract.** The API records are canonical: `isEnabled`, `isBuiltIn`, `lastSignInAtUtc`, arrays always
present and never null, no `teamIds` on user write payloads (team membership is written only through the
team payload). Request bodies under `/api/auth` and `/api/admin` reject unknown and duplicate members (400
naming the path). Validation errors are keyed by camelCase request paths (`grants[0].sagaTypes`). The golden
JSON fixtures C39 checked in live in `dashboard-web/src/app/testing/contracts/admin/`
(`create-user.request.json`, `update-user.request.json`, `reset-password.request.json`,
`role.request.json`, `team.request.json`, `user.response.json`, `role.response.json`,
`team.response.json`, `permissions.response.json`, `validation-problem.response.json`,
`conflict-problem.response.json`). The .NET tests already assert them (`AdminApi.cs` in
`VSaga.Dashboard.Api.Tests`); C48's `admin-api.service.spec.ts` must assert the same files. Problem codes
are the ones in design §8.9.

**Compose credentials.** `docker-compose.yml` sets `Dashboard__ApiKeyRole: "Operator"` so the SPA's embedded
key can still retry until C46, which sets it back to `Viewer`. The demo administrator is `admin` /
`dev-local-only-change-me`, seeded only into an empty identity volume.

---

## 4. Order of the remaining work

1. C40: finish (correctness review, fixes, live check).
2. C41, with its live check.
3. Authentication mutation checks (server side), section 5.3.
4. C42 to C50 (SPA), with the live checks of C46, C47 and C50, then the SPA-side authentication mutation
   checks and the Node 22 run.
5. C51 and C52 (documentation and history of the authentication slice).
6. C53 to C56 (guide mode and user guide), with the live check of C55, then the guidance mutation checks
   and the Node 22 run.
7. C57 and C58.

---

## 5. Remaining work, commit by commit

### 5.1 C40: finish "Check access on hub subscriptions, guard the hub's origin and drop connections when access changes"

Committed as `852a901`. What it implemented is in its commit body and design §8.6. Its spec, for reviewing:
design §8.6; `blueprint-auth-backend.md` section 9 and its commit 8 (one commit); `review-security.md`
findings 7 and 9 (origin check by hub endpoint marker; `Origin: null` is a mismatch; Warning on rejection
with the received and expected values); `review-consistency.md` finding 9 (`HubOriginGuardTests`: no
Origin, same origin, another localhost port, the configured WebOrigin, https through a trusted forwarded
header). Scope: `SagaHub.SubscribeToList`/`SubscribeToSaga` returning `Task<bool>`, resolving access
afresh and never throwing on denial; groups `saga:list`, `saga-list:{sagaType}` (scoped callers, including
types not yet run) and `saga:{type}:{id}`; the joined names kept in `Context.Items` for
`UnsubscribeFromList`; `PushSagaUpdatedAsync` as the one push path of the notifier and the poller;
`MapHub(...).RequireAuthorization()` with `CloseOnAuthenticationExpiration` plus the antiforgery
exemption and hub marker; `HubOriginGuard` (WebOrigin through `DashboardEdgeSettings` only when set);
`HubConnectionRegistry` as the `IAccessChangeObserver` that aborts the affected users' connections (all on
a role change); `SagaHubAccessTests` (group choice per scope; a denied subscribe returns false; abort on
access change; abort after a password change and after logout of another session), `HubOriginGuardTests`
(the five cases plus `Origin: null` and an upper-case path), and updates to `SagaHubTests`,
`SignalRFakes`, `SignalRSagaChangeNotifierTests` and `SagaChangePollingServiceTests`.

Remaining steps:

1. **Re-run the CORRECTNESS review** of `852a901`. It was stopped mid-run when work paused. The SPEC review
   is done.
2. **Fix**, in one follow-up commit (C40 is pushed, so no amend), applying the correctness findings and the
   two code minors from the SPEC review:
   - `HubOriginGuardTests`: add a direct WebSocket connect (a `GET /hubs/saga` upgrade that skips
     negotiate) with a foreign `Origin`: it must be refused with 403 (under TestServer this surfaces as
     `InvalidOperationException` "Incomplete handshake, status code: 403") and log exactly one Warning
     with EventId 7311.
   - `SagaHubTests`: the malformed-id test must `Assert.False` on the returned boolean; add a case with a
     null `sagaType` that returns false and adds no group.
   - The third SPEC minor is documentation: `docs/dashboard.md` "Live updates (SignalR)" is stale. It is
     not fixed here; C51 rewrites that section (5.13).
3. **Live check** (curl and node, evidence `C40.md`):
   - Base stack up: `docker compose up -d --build --wait`.
   - Through `http://localhost:4200` with a cookie jar and the `X-XSRF-TOKEN` header taken from the
     `XSRF-TOKEN` cookie: sign in as admin; create a Viewer user scoped to `OrderSaga` with
     `POST /api/admin/users`; sign that user in with a second jar (if `mustChangePassword` is true, change
     the password first through `POST /api/auth/password`).
   - Negotiate `/hubs/saga` with each cookie and `Origin: http://localhost:4200`: a `connectionToken`.
     With `Origin: http://localhost:9999`: 403 and a Warning in the API log naming both origins. With
     `Origin: null`: 403.
   - With a small node script (the repository's `dashboard-web/node_modules/@microsoft/signalr` works under
     node) or `npx wscat`, connect as the scoped user: `SubscribeToList` returns true, `SubscribeToSaga`
     for another saga type returns false, and `SagaUpdated` pushes arrive only for `OrderSaga`.
   - Disable the scoped user through the admin API: the socket closes and a new negotiate gets 401.
   - Record the outputs; `docker compose down` (no `-v`).

Read: design §8.6, §10 (rows on access changes and TLS), §11 authentication row.

### 5.2 C41: "Smoke-test dashboard sign-in, the hub origin guard and identity health in CI"

Sources: `review-consistency.md` finding 5; design §8.8 last bullet (CI asserts `identity` is healthy) and
§11.

Build: extend the `compose` job ("Compose build & smoke") in `.github/workflows/ci.yml`, after the existing
smoke steps, keeping the existing API-key checks:

- `/health` (through `docker compose exec dashboard-api curl -s localhost:8080/health` or the API port)
  lists `identity` as Healthy.
- The configured `Dashboard__Identity__Sqlite__Path` lies under the mounted volume's target path, read from
  `docker compose config`.
- Through `http://localhost:4200`: `GET /api/auth/session` into a cookie jar; `POST /api/auth/login` with
  the `X-XSRF-TOKEN` header and the seeded credentials; `GET /api/sagas` with the cookie answers 200;
  negotiating the hub with the cookie and `Origin: http://localhost:4200` returns a `connectionToken`; the
  same with `Origin: http://localhost:9999` answers 403.
- Run the job's steps locally (`jq` through the container, 2.6) and record the outputs.

Live check (evidence `C41.md`): re-run the whole compose job locally from the committed `ci.yml` steps
against a fresh stack, record each assertion's output, and finish with `docker compose down -v` as the job
does.

Read: design §8.8, §11; the existing `compose` job.

### 5.3 Authentication mutation checks (server side, after C41)

Design §11, authentication row. Each must fail only its own tests (2.4). Record in `mutations-auth.md`.

1. Remove the retry policy. Read this as the `sagas.retry` authorization requirement on
   `POST .../retry` (`review-consistency.md` lists it beside the other security controls). The C38 run
   ("retry needs only view") failed exactly `ARetryOfATypeOutOfScope_Is403EvenForAScopedOperator_WhileItsOwnTypeRetries`,
   `AViewer_CannotRetry` and `TheDefaultViewerApiKey_ReadsEveryTypeWithData_ButCannotRetry`. If you read
   it as the SPA hub's reconnect retry policy instead, run that one after C43 too, and say which reading
   you took.
2. Remove the origin guard (C40 checked "the guard skipping hubs").
3. Remove the security-stamp comparison (C34 checked it).
4. Remove payload and `errorMessage` redaction (C38: "no redaction" failed the two redaction tests in
   `SagaAccessEnforcementTests`).
5. Remove `JsonUnmappedMemberHandling.Disallow` from the request records.
6. Let the API key keep `access.manage` (C34 checked "the access.manage strip").
7. Drop the scope filter in the lister (C38: failed
   `ScopedSagaListerTests.RowsOutsideTheScope_AreFilteredOut_EvenWhenTheProviderReturnsThem`).
8. Skip the setup-code check (C36: "accepting any code" failed the four wrong-code and rate-limit tests).
9. Skip the abort on password change (C40: "the registry aborting nothing").

Several were run once per commit, as noted; the slice pass runs all nine again against the finished slice
and records the failing tests of each. The SPA-side half of the slice's tests (auth, guards, the interceptor's
antiforgery retry, hub and admin specs) gains no listed mutation; where a C43 to C50 test is cheap to prove
with a one-token mutation, prove it during that commit.

### 5.4 C42: "Promote the shared banner, button, form and table styles to the global stylesheet"

Sources: `blueprint-auth-frontend.md` section 13 and its commit 1; `review-consistency.md` finding 12; design
§8.11 last bullet.

Build:

- `dashboard-web/src/styles.scss` gains the remaining shared primitives the auth and admin pages will use:
  `.empty`; `.banner` with `--error`, `--warning`, `--info` and `--success` (where not already global);
  `.btn--danger`; `.field`, `.label`, `.input`, `.field-hint`, `.field-error`; `.card`, `.auth-card`;
  `.data-table`; `.chip` with `--muted` and `--danger`; `.page-header`; `.toolbar`; `.subtabs`; `.menu`;
  and a `:focus-visible` outline. Copy the values from `saga-list.scss` and `saga-detail.scss`.
- Remove the now-duplicated local copies, and update `saga-list.html` and `saga-detail.html` class names so
  every banner uses the base class plus a modifier.
- No visual change is intended: compare computed styles where feasible. Run the gates; the initial bundle
  must stay within budget, and the commit reports the global styles size.
- No `data-tour` attributes.

### 5.5 C43: "Let the hub service stop and reset, resume, and give up when the session is gone"

Sources: `blueprint-auth-frontend.md` section 7 and its commit 2; design §8.11. The API key is still sent
in this commit: `accessTokenFactory` stays until C46.

Build, in `SagaHubService`:

- `generation`, `active` and `sessionProbe` state; `setSessionProbe`; `resume()`; `stopAndReset()`, which
  never rejects.
- The session probe in `startWithRetry` and in the reconnect retry policy, which moves into the class: when
  `previousRetryCount > 0` it runs the probe in the background and calls `stopAndReset()` on false; it
  still always returns a delay.
- Generation checks after every `await`.
- An inline `try/catch` around each `invoke`, with no async helper: `saga-hub.service.spec.ts` waits
  exactly two ticks.
- Since C40 the hub's subscribe methods return booleans. A false result is not an error: keep the
  subscription record only when true, or keep today's behaviour if that is simpler, and explain the choice
  in the body.

Tests: the existing 22 hub specs pass unchanged. Add the blueprint's cases: `stopAndReset`; `resume`; a
probe answering false stops after one start; `stopAndReset` during back-off ends the loop (fake timers); the
policy's probe answering false stops; a rejected invoke does not reject; events from a replaced connection
are ignored.

### 5.6 C44: "Add the SPA session model, the auth service and an auth test helper"

Sources: `blueprint-auth-frontend.md` sections 3 (only `models/auth.model.ts`; the admin models come in
C48 with the API's canonical names), 4 and 12, and its commit 3; design §8.9 (`SessionResponse` carries
`passwordMinLength`; the problem codes) and §8.11; `review-consistency.md` findings 10 (a 503 on the session
means "sign-in is unavailable"; codes exactly as the API defines them) and 12 (`PermissionKey`). The
blueprint's `shared/http-error.ts` becomes `util/http-error.ts` (pure helpers live under `src/app/util/`).

Build (not wired into the app yet):

- `models/auth.model.ts`.
- `services/auth.service.ts`, with signals:
  - `bootstrap` never rejects and has an 8 s timeout;
  - refresh is single-flight; a failed refresh keeps the last known session and marks the state
    "unreachable" only from "unknown"; a 503 marks the session unavailable;
  - `login`, `logout`, `setup` and `changePassword` POST, then reload the session; `setup` takes the
    one-time code;
  - logout order: `hub.stopAndReset()`, then local anonymous state, then the POST (errors swallowed), then
    reload, then navigate to `/login`;
  - `handleUnauthorized` is idempotent and carries a `returnUrl`; `noteForbidden` is throttled to once per
    5 s; a `visibilitychange` refresh is throttled to once per 60 s; a different user id after a refresh
    reloads the page;
  - `can`, `canAny`, `canManageAccess`;
  - the constructor sets the hub's session probe and calls `hub.resume()` when a refresh yields an
    authenticated session.
- `util/http-error.ts`: `problemOf` returning status, code, message (from `error`, else `detail`, else
  `title`, else a fallback), `fieldErrors` from `errors` with camelCase keys, and `retryAfterSeconds`.
- `testing/auth-mock.ts`: `createAuthMock` and `provideAuthMock`, granting every permission by default.
- Specs: `auth.service.spec.ts` and `http-error.spec.ts` with every case the blueprint lists, plus the 503
  path.

### 5.7 C45: "Add the login, setup and account pages as lazy routes"

Sources: `blueprint-auth-frontend.md` sections 2 (only the `/login`, `/setup` and `/account` routes and their
guards; the saga routes stay open until C46), 5, 10 and 12, and its commit 4; design §8.8 (setup requires
the one-time code; there is no time window, so the "setup closed" notice says an administrator already
exists or seed keys are set, and how to find the code in the API log) and §8.11; `review-consistency.md`
finding 10 (`setup_unavailable`; a wrong current password shows `errors.currentPassword`; a 503 shows
"sign-in is unavailable").

Build:

- `guards/auth.guards.ts`: `authGuard`, `anonymousGuard`, `setupGuard`, `adminGuard` (used in C48) and
  `safeReturnUrl`.
- `pages/login`; `pages/setup` (username, display name, password, confirmation, setup code); `pages/account`
  (identity, change-password form, "Your access" through `components/access-summary` built from
  `session.access`, and the forced-change banner).
- Template-driven forms (`FormsModule`); `minlength` from `passwordMinLength`; a uniform sign-in failure
  text; the 429 text from `Retry-After`; polling every 3 s while unreachable; an expired-session notice.
- Lazy routes in `app.routes.ts`; `withNavigationErrorHandler` reloading at most once a minute on a stale
  chunk.
- Specs per the blueprint: login, setup, account and guards, including `safeReturnUrl` rejecting `//evil`,
  `/\evil`, `https://x` and `/login`. Guard specs pass the third `CanMatchFn` argument (section 3).
- No `data-tour` attributes.

### 5.8 C46: "Require a dashboard session in the SPA and remove the API key from the bundle"

Sources: `blueprint-auth-frontend.md` sections 2 (`authGuard` on the saga routes), 6, 7 (hub without a
token factory) and 8 (top bar with `.topbar-end`, no `data-tour` attributes), and its commit 5;
`review-consistency.md` findings 4 (the antiforgery retry is tested on the SPA side) and 16 (compose
`Dashboard__ApiKeyRole` back to `Viewer` in this commit) and its "Conditions that keep the sequence green"
(update `saga-hub.service.spec.ts`'s key test and `app.spec.ts` in this commit); `blueprint-packaging.md`
section 8 items 6 and 9.

Build:

- `app.config.ts`: the `authInterceptor`, `provideAppInitializer` calling `AuthService.bootstrap`, and the
  stale-chunk handler if C45 did not add it.
- `interceptors/auth.interceptor.ts` with a spec: a 401 outside the auth endpoints calls
  `handleUnauthorized`; a 403 calls `noteForbidden`; a 400 with code `antiforgery` refreshes the session and
  retries once with the new `X-XSRF-TOKEN`; it never sends `X-Api-Key` or `Authorization`.
- Delete `interceptors/api-key.interceptor.ts` and `DASHBOARD_API_KEY`.
- `SagaHubService` uses `.withUrl(HUB_URL)` with no `accessTokenFactory`.
- `app.html`, `app.ts`, `app.scss`: a top bar with primary navigation (Administration only with
  `canManageAccess`) and `.topbar-end` holding `components/user-menu` (Account, Sign out).
- `authGuard` on the `/sagas` routes.
- Update `app.spec.ts`, the key test in `saga-hub.service.spec.ts`, and every page spec that now needs
  `provideAuthMock`.
- `docker-compose.yml`: `Dashboard__ApiKeyRole: "Viewer"`.
- `README.md` only (`docs/` is C51): the sign-in rows ("Sign in as admin / dev-local-only-change-me,
  seeded by Dashboard__Admin__* in docker-compose.yml"; the API row says the key is a read-only Viewer);
  the identity volume persists users and the seed applies only to an empty volume; a callout that the
  credentials are public and how to expose the dashboard deliberately. That callout must say the seeded
  password applies only to an empty identity volume: after the first start, change the admin password by
  signing in and changing it (or set `Dashboard__Admin__ResetOnStart=true` for one start, or `down -v`),
  because editing `Dashboard__Admin__Password` then changes nothing.
- Verify that `dev-local-only-change-me` appears nowhere in `dashboard-web/src` or the built `dist`, and
  that `X-Api-Key` appears in no non-spec file under `dashboard-web/src` and nowhere in `dist` (the
  interceptor spec names it in its assertion).

Live check (Playwright and curl, evidence `C46.md`):

- `docker compose up -d --build --wait` with fresh images.
- At `http://localhost:4200`: an anonymous visit to a saga detail URL lands on `/login?returnUrl=...`;
  signing in as admin / `dev-local-only-change-me` returns to that saga.
- The list and live updates work: `ws://localhost:4200/hubs/saga` answers 101 with no `access_token` in the
  URL; in the network view the session cookie is HttpOnly, `XSRF-TOKEN` is readable, and a POST retry
  carries `X-XSRF-TOKEN`.
- Retry of a Failed saga works as admin.
- User menu, Sign out: the socket closes and no further negotiate requests appear.
- `docker compose restart dashboard-api`: the UI shows reconnecting, then recovers without a new sign-in.
- `docker compose down -v && docker compose up -d --wait` with the tab open: the tab ends on `/login`, not a
  permanent "Reconnecting".
- With curl and the API key (now Viewer): `GET /api/sagas` 200; `POST .../retry` 403.
- Search the built JS served at 4200 for `dev-local-only-change-me`: absent.
- Console clean (no CSP violations) on the login, list and detail pages. Screenshots.
- `docker compose down` (no `-v`).

### 5.9 C47: "Show retry and saga data only to users permitted for that saga type, with no-access states"

Sources: `blueprint-auth-frontend.md` section 9 and its commit 6, which is detail-ux's commit 8
(`canViewData` wired to `auth.can('sagas.data', sagaType)`); `review-consistency.md` finding 13 (detail-ux
owns the data components: the buttons are disabled beside "Saga data is hidden for your role. It needs the
sagas.data permission."; auth only supplies the boolean) and finding 10 with feasibility finding 6 (the list
shows the server's error text for a 400 and returns to the last good page; a 403 shows "You do not have
access to these sagas."); design §8.11.

Build:

- `saga-detail`:
  - a `canRetry()` gate on the retry row, otherwise "You do not have permission to retry <type> sagas.";
  - `canViewData` bound to the auth service;
  - a forbidden state for a 403 load: no error banner, no reload on reconnect, and the text "You do not
    have access to <type> sagas. Ask an administrator for sagas.view on this saga type.";
  - retry errors through `problemOf`;
  - "Started by" as plain text with "(no access)" when the parent's type is not viewable;
  - the retry-plan request (C28) only when permitted: `sagas.view` is enough for the plan; the confirmation
    needs `sagas.retry`.
- `saga-list`:
  - a no-access state when `!canAny('sagas.view')`, with no API calls;
  - the 403 and 400 messages;
  - on a 400, return to the last good page, using `maxPage` from the body when present. `maxPage` 0 means
    no page can be served at all (too many visible saga types for that list shape): show the error and ask
    for a saga-type filter, never navigate to page 0.
- Specs per the blueprint's additions.

Live check (Playwright, evidence `C47.md`): base stack up. As admin, through the SPA or with curl on the
admin API, create user `viewer1` with a Viewer grant scoped to `OrderSaga`, and user `nodata1` with a custom
role holding only `sagas.view` for all types (create the role with `POST /api/admin/roles`). Sign in as
`viewer1` (change the password if forced): the list shows only `OrderSaga` rows and the type filter offers
only `OrderSaga`; an `OrderSaga` detail has no retry button and shows the permission hint; another type's
detail URL shows the no-access state; `/admin` redirects to `/sagas`. Sign in as `nodata1`: the data buttons
are disabled with the sentence, timeline rows show no payloads or error text, the map banner still works.
Console clean. Screenshots. `docker compose down` (no `-v`).

### 5.10 C48: "Add the administration area: shell, API service, store and role management"

Sources: `blueprint-auth-frontend.md` sections 2 (`ADMIN_ROUTES`), 3 (`admin.model.ts`, with the API's
canonical names), 11 (`AdminApiService`, `AdminStore`, `AdminShell`, roles list and edit, `ConfirmButton`)
and 12, and its commit 7; `review-consistency.md` finding 1 (a blocker: the golden fixtures from C39 are
asserted from `admin-api.service.spec.ts`); design §8.9.

Build:

- `pages/admin/admin.routes.ts` with `canMatch: adminGuard` on `/admin` (the admin chunk loads only for
  managers); child routes `users`, `teams` and `roles`, each with `new` and `:id` (C48 fills the role
  pages; users and teams may route to placeholders until C49 and C50).
- `admin.model.ts`: `isEnabled`, `isBuiltIn`, `lastSignInAtUtc`, arrays never null, no `teamIds` on user
  write payloads.
- `admin-api.service.ts` and its spec asserting the golden fixtures in
  `src/app/testing/contracts/admin/`: the request bodies the service sends equal the request fixtures, and
  the response fixtures parse into the models.
- `admin.store.ts`.
- `admin-shell` with Users, Teams and Roles sub-navigation (Users and Teams may render a placeholder until
  C49 and C50).
- Roles list (Built-in or Custom, permission chips, "used by N grants") and role edit (built-ins read-only
  with "Duplicate as custom role"; one checkbox per catalogue entry with its description; Delete disabled
  while in use; confirmation through `components/confirm-button`).
- Server error mapping per blueprint section 12: field errors, the `last_administrator` banner that keeps
  the draft, `role_in_use`, 404, 403.
- Specs for the store, the service, the shell, the role pages and the confirm button. No `data-tour`
  attributes.

### 5.11 C49: "Add user management with a grants editor and an effective-access preview"

Sources: `blueprint-auth-frontend.md` section 11 (users list and edit, `GrantsEditor`, `explainAccess`,
`AccessSummary`) and its commit 8; `review-consistency.md` finding 1 (team membership is read-only on the user
page; the preview applies `implies` and `scopable` from `GET /api/admin/permissions`; one grant per role);
design §8.9.

Build:

- Users list: username link, display name, status chips (Disabled, Locked, Must change password), teams
  read-only, access summary, last sign-in through `LocalTime`, a client-side filter, New user.
- User edit: username on create only; display name; `isEnabled`; on create, password, confirmation and
  "require a change at next sign-in" (on by default); teams shown read-only with a link to each team; the
  grants editor with an "exact saga type name" input; an effective-access preview computed from the draft;
  Reset password inline, Unlock, and Delete with confirmation. Enabled and Delete are disabled on the
  signed-in user's own record.
- `pages/admin/grants-editor`: model-bound grants; the role select offers only roles not already used;
  All or Selected saga types; checkboxes for the known types plus already-granted unknown types; the
  exact-name input trims and rejects blank names; inline "Pick at least one saga type" blocks Save; inline
  "access.manage is ignored in a scoped grant".
- `access-explain.ts` (pure): applies `implies` and `scopable` from the permissions catalogue and gives the
  origins of each permission; its spec mirrors the server evaluator's cases.
- Specs per the blueprint.

### 5.12 C50: "Add team management to the administration area"

Sources: `blueprint-auth-frontend.md` section 11 (team list and edit) and its commit 9;
`review-consistency.md` finding 1 (membership written only through the team payload).

Build: teams list (name, member count, access summary) and team edit (name, description, member checkboxes
with a filter, the grants editor, Delete with confirmation; the `last_administrator` banner keeps the
draft). Specs.

Live check (evidence `C50.md`):

1. Base stack up with fresh images. As admin in Playwright: create team `payments` with member `viewer1`
   (create `viewer1` if absent) and a team grant Operator scoped to `OrderSaga`. `viewer1`'s user page shows
   the team read-only, and the effective-access preview lists retry for `OrderSaga` from "team payments".
   Sign in as `viewer1` in a second browser context: an `OrderSaga` detail now offers retry. Try to remove
   admin's own Administrator grant, or to disable admin: the `last_administrator` banner appears and the
   draft is kept.
2. Two stacks side by side in one browser profile: also run
   `docker compose -p vsaga-wolverine -f docker-compose.yml -f docker-compose.wolverine.yml up -d --build --wait`.
   Sign in to `http://localhost:4200` and `http://localhost:4300` in tabs of the same context. Both stay
   signed in (distinct session cookie names) and unsafe requests succeed in each. An antiforgery 400 caused
   by the shared `XSRF-TOKEN` is healed by the single retry: check the network log for a 400 followed by a
   successful retry, and record whether it happened.
3. `docker compose down`, then `up -d --wait`: the session survives. `docker compose down -v`, then up: the
   old session is gone and admin is seeded again.
4. A keyboard-only pass over the admin users list and a user edit form; console clean. Screenshots.

Bring every stack down (no `-v` except in step 3).

After C50: the SPA gates under Node 22 (2.1), and any SPA-side mutation checks you choose to add to
`mutations-auth.md`.

### 5.13 C51: "Document dashboard sign-in, access control and the identity store"

Sources: design §8 (authoritative), ADR 0006, `blueprint-guidance-docs.md` sections 6 (the structure of
`docs/dashboard.md`) and 7, `blueprint-auth-backend.md` section 11, and the evidence for C33 to C50. This is
the slice's one documentation commit.

Files:

- `docs/dashboard.md`:
  - `## Authentication` rewritten, keeping the heading verbatim (other files link to its anchor), with
    `### Signing in`; `### The first administrator` (seed, setup code in the API log, `ResetOnStart`);
    `### Passwords, lockout and rate limits`; `### CSRF protection`; `### API key` (machine clients send
    the `X-Api-Key` header, not `access_token`; default role Viewer; never `access.manage`);
    `### Failure responses` (the codes).
  - A new `## Access control`: `### Permissions`, `### Roles`, `### Grants and saga-type scope` (with a
    worked example and the targeted-retry boundary), `### What a scoped caller sees`, and
    `### The identity store` (the SQLite file, the volume, a backup is a credential backup,
    `ProtectKeysWithCertificate`).
  - `## Live updates (SignalR)` rewritten. It is stale since C40 (the C40 SPEC review's third minor): the
    cookie on negotiate and on the WebSocket; access checks in the subscribe methods, which return booleans;
    the per-type `saga-list:{sagaType}` groups; payload- and error-free pushes; the origin guard, including
    that non-browser clients send no `Origin` and are allowed; connections aborted on access changes.
  - The API endpoint table gains a Permission column and the `/api/auth` and `/api/admin` groups. State
    that `PUT /api/admin/users/{id}` updates only the fields sent, while `PUT` on teams and roles replaces
    the whole record, members included; that a 404 problem carries no code; and that request bodies reject
    unknown and duplicate members.
  - The audit log category.
  - A "Deploying beyond localhost" checklist: TLS and `RequireHttps`, `TrustedProxies` narrowed, the bind
    address, change the seed password and the API key, `WebOrigin` only for read-only cross-origin use,
    back up the volume.
- `docs/configuration.md`: one Dashboard table listing every `Dashboard:*` key with its default (identity
  store, first administrator, sessions and passwords, rate limits, API key, browser origin and proxies,
  state snapshots), keeping the `### Dashboard:ApiKey` heading for its anchor, and stating that
  framework-owned options (cookie, antiforgery, forwarded headers, key management) are the one exception to
  "no options binding".
- `docs/transports/index.md`: one sentence on two stacks in one browser (per-project session cookies; the
  shared `XSRF-TOKEN` healed by one retry).
- `docs/README.md`; `README.md` (the documentation list mentions sign-in); `dashboard-web/README.md` (the
  auth service, guards, interceptor, the key removed, the admin area); `CONTRIBUTING.md` (the two
  `dotnet ef` commands, each with `--context`, are already there since C33 `e6228c3`: check them, do not
  duplicate them).
- Per design §9.3, also check `docs/observability.md` (the audit log category and EventIds, the `identity`
  health check), `docs/persistence.md` (the identity store is a separate SQLite file on its own volume, not
  the saga store), `docs/concepts.md` and `docs/testing.md`, and update any passage that describes
  dashboard access or its health checks.
- Grep for `DASHBOARD_API_KEY`, "api-key interceptor", "shared key" and "deliberately not part of", and fix
  stale mentions outside `docs/history` and `docs/plans`. All links and anchors resolve.

Read: design §8 and §9.3; ADR 0006.

### 5.14 C52: "Record the dashboard sign-in and access slice"

Write `docs/history/dashboard-sign-in-and-access.md` (new; shaped like
`docs/history/redis-persistence-provider.md`) for C31 to C51, only from the evidence (Appendix A for C33,
C37 and the C38 and C39 mutation runs; your own files for C40, C41, C46, C47, C50 and `mutations-auth.md`)
and the commit bodies: what changed and why; real commands and observed values from the live runs,
including the two-stacks-in-one-browser result and whether the antiforgery retry fired; the mutation
verdicts; problems found and fixed (the follow-up commits in section 1 are such problems); and anything
unverified, stated as such. No edits to existing history files. Update the history list and count in
`docs/README.md`. Gates.

### 5.15 C53: "Add guide mode to the dashboard: a top-bar toggle and the saga list area"

Sources: design §9.1 (authoritative: seven areas, namely list, detail summary, map, timeline, data, retry
and administration, each explaining itself once while Guide is on, with its own trigger; this replaces the
blueprint's three page tours) and §9.2; PLAN.md's second-round decision "Guide mode";
`blueprint-guidance-docs.md` sections 1 to 4 (service, toggle, overlay, geometry, modality, the list tour's
copy) and its commit 5; `review-consistency.md` findings 7 (one `data-tour` vocabulary in `GUIDE_ANCHORS`,
added only by guidance commits), 12 (`PermissionKey`, no separate `GuidePermission` type), 14 (list sort
headings become buttons; rows open on Enter and Space or through a link in the first cell) and 19 (filter
steps at begin; the centred fallback only for an anchor that disappears mid-tour; `GUIDE_PERMISSION_CHECK`
provided from `AuthService.can` in this commit).

Build:

- `models/guide.model.ts`.
- `services/guide-areas.ts`: `USER_GUIDE_URL` and the area table (id, version, how it is triggered: route
  match for list and admin; tab shown for map and timeline; inspector or data bar opened for data; retry
  row shown for retry; the detail page for summary), `readyAnchor`, `requires` and `docsAnchor`.
- `services/guide.service.ts`: state; storage key `vsaga.guide` with the v1 shape
  (`{"v":1,"enabled":true,"seen":{"list":1},"hintDismissed":true}`; corrupt JSON or an unknown `v` reads as
  defaults; without storage, state stays in memory); `seen[area] = version`, so bumping an area's version
  shows the changed tour once more; Replay (`canReplay`) repeats the current area; Guide is off by default
  and switching it on starts the current page's area; state is per origin (per compose stack), not per
  user. `guide.service.spec.ts` covers the version bump, replay, the default and corrupt or unknown-`v`
  storage. The service also has the `GUIDE_STORAGE` and `GUIDE_PERMISSION_CHECK` tokens; request,
  started, ended and abandoned; a way for pages to announce that an area became visible (for example
  `guide.areaShown('timeline')`).
- `components/guide-toggle`: in `.topbar-end` before the user menu, only when signed in; a
  `<button aria-pressed>` "Guide"; "Replay tour"; a "User guide" link; the one-time non-modal hint.
- `components/guide-overlay` with `guide-geometry.ts` (`spotlightBox`, `placePopover`) and
  `guide-tours.ts`, loaded through `@defer (when guide.enabled())` as the last child of the app template,
  per blueprint sections 3 and 4 with the reconciled rule: wait for the area's ready anchor (250 ms polls,
  20 tries); at begin drop steps whose permission fails or whose anchor, reveal control and fallback are all
  missing, so "Step n of m" is true; a centred popover only for an anchor that disappears mid-tour; a
  `requestAnimationFrame` loop tracks the anchor; modal while active (siblings `inert`, a full-screen layer
  cancels `mousedown`, Escape ends, arrows move, Tab wraps, focus returns to where it was); no animation
  under `prefers-reduced-motion`.
- This commit ships only the list area's steps (the blueprint's list tour, its copy adjusted to the shipped
  UI and to sign-in) and the `topbar-guide` anchor. The other areas' step lists are empty or absent until
  C54 and C55; the area table may list them.
- `saga-list`: anchors `list-filters`, `list-table`, `list-sort`, `list-row`, `list-pagination`. The Status
  and Updated sort headings become `<button>`s inside the `th` (`aria-sort` stays on the `th`). Each row
  opens on Enter and Space (or through a link in the first cell). Existing list specs stay green; add cases.
- `app.config.ts` provides `GUIDE_PERMISSION_CHECK` from `AuthService.can`.
- A rule comment at the top of `guide-tours.ts`: a change to the dashboard UI updates the guide
  (`docs/dashboard-guide.md`, arriving in C56) and the tour in the same change.
- Specs: `guide-geometry`; `guide.service`; `guide-tours` (every anchor, fallback and reveal is in
  `GUIDE_ANCHORS`; unique ids; valid `PermissionKey` values; length limits); `guide-overlay` with fake
  anchors and timers (waits for the ready anchor; abandons or starts; drops unpermitted and unanchorable
  steps at begin and counts the rest; Next, Back, Done; Escape and Done remember; arrows; Tab wraps; the
  primary button is focused; siblings inert, then restored; the centred fallback when an anchor disappears
  mid-tour; Guide switched off mid-tour does not remember; destroy cancels timers and the frame);
  `guide-toggle`; the list page's anchor-contract case and keyboard row-open case; an `app.spec` anchor
  case.
- Budgets: no warning. The overlay must land in a lazy chunk; report the chunk sizes.

### 5.16 C54: "Explain the saga detail page in guide mode: summary, map, timeline, data and retry"

Sources: design §9.1 (areas and triggers; copy written against the shipped labels and §7's retry
semantics); `blueprint-guidance-docs.md` section 4's detail steps (adapted into the five detail areas; its
step 8 retry copy is wrong now: a retry re-runs the step that failed, for this saga only, and other services
consuming that message still receive it) and its commit 6; `review-consistency.md` findings 7 (anchor
mapping) and 13 (labels).

Build:

- Anchors: `detail-summary`, `detail-data` (the Saga data group), `detail-retry`, `detail-tab-map` and
  `detail-tab-timeline` in `saga-detail.html`; `map-canvas` and `map-controls` in `saga-map.html`;
  `timeline`, `timeline-entry` (every clickable entry) and `timeline-step-data` (every step's Data toggle)
  in the timeline component.
- The five areas' step lists in `guide-tours.ts`, with permission requirements: data needs `sagas.data`;
  retry needs `sagas.retry` and shows only on `Failed` or `TimedOut` sagas. Copy uses the shipped labels
  ("Recorded at", "At start", "At end", "Compare", "Re-run step N").
- The pages announce area visibility: the map or timeline tab shown, an inspector or the data bar opened,
  the retry row rendered.
- Specs: anchor-contract cases in the `saga-detail`, `saga-map` and `saga-timeline` specs; `guide-tours`
  checks; overlay cases for an area triggered by a tab switch, and for a Viewer (no `sagas.retry`) never
  getting the retry area.

### 5.17 C55: "Explain the administration area in guide mode"

Sources: design §9.1; `blueprint-guidance-docs.md` section 4's admin steps and its commit 7 (step 5's grant
copy must match the shipped grants editor; the last-administrator step stays).

Build: anchors `admin-nav`, `admin-nav-users`, `admin-nav-teams` and `admin-nav-roles` (the admin shell's
links) and `admin-list` (each admin page's main table); the administration area's steps (requires
`access.manage`); anchor-contract cases in the admin specs.

Live check (container and Playwright, evidence `C55.md`): `docker compose up -d --build --wait`; at
`http://localhost:4200`, signed in as admin:

- the hint shows once; switching Guide on starts the list area; Escape returns focus to the toggle; a
  reload does not restart it;
- opening a saga explains the summary; switching to the Map tab explains the map once; switching to the
  Timeline tab explains the timeline once; switching back to a tab already explained does not explain it
  again; opening a step's data explains data; a Failed saga's retry row explains retry; `/admin/users`
  runs the administration area;
- keyboard only (no mouse) through one whole area: focus never reaches the page behind (siblings inert);
- with `prefers-reduced-motion` emulated (Playwright `emulateMedia`): no fade;
- signed in as a Viewer scoped user (create one if needed): no retry and no administration area;
- console: no CSP violations or errors on any page; a screenshot of each area's first step.

`docker compose down` (no `-v`).

### 5.18 C56: "Add the dashboard user guide and link it from the app, the README and the docs index"

Sources: design §9.2 (headings and contents, authoritative); `blueprint-guidance-docs.md` section 5 and its
commit 8; PLAN.md's second-round decisions "User guide" (text and tables, no screenshots; the written rule
in `CONTRIBUTING.md`, a header note in `docs/dashboard-guide.md` and the tour step definitions) and "Guide
mode".

Build:

- New `docs/dashboard-guide.md`, written against the shipped UI (read the components and templates and
  describe what is really there): labels, statuses, kinds, banners, the retry plan and confirmation, the
  202, 409, 422 and 502 outcomes in user terms, attribution, grants with a worked scope example, the lockout
  escape, two stacks in one browser, the minimum engine version for a targeted retry, and that a business
  failure decided by the message alone fails again after a retry. Under Signing in: the seeded
  administrator, the setup code, lockout, forced password change, idle and absolute timeouts and sign out.
  Under Administration: users, teams, the built-in roles against the four permissions and the
  last-administrator rule.
- Fixed H2 headings matching the `docsAnchor` values in `guide-areas.ts` (design §9.2 lists them: Opening
  the dashboard; Signing in; Guide mode; The saga list; The saga detail page; Administration; Your account;
  Troubleshooting). Verify that each anchor slug resolves.
- The header note with the rule. `CONTRIBUTING.md` gains the rule and a PR-checklist line.
- `README.md` and `docs/README.md` link the guide. The in-app User guide link already uses
  `USER_GUIDE_URL`; check every `docsAnchor`. Check every relative link resolves.

### 5.19 Guidance mutation checks (after C56)

Design §11, guidance row; record in `mutations-guidance.md`:

1. Rename `data-tour="list-table"`: exactly the list page's anchor-contract case (and the
   `guide-tours` check, if it reads templates) fails.
2. Delete the `requires` filter in the overlay; expected to fail: the `guide-overlay` case that drops
   unpermitted steps at begin (and the C54 case where a Viewer never gets the retry area).
3. Delete the `inert` toggle in the overlay; expected to fail: the `guide-overlay` case "siblings inert,
   then restored".

For SPA mutations, apply the one-token edit, run `npx ng test --watch=false` (the whole suite, so you see
every failure), record the failing spec names, restore with `git checkout -- <file>` and rerun
`npx ng test --watch=false`.

Each fails exactly its own case. Then the SPA gates under Node 22 (2.1).

### 5.20 C57: "Record guide mode and the dashboard user guide"

Write `docs/history/dashboard-guide-mode-and-user-guide.md` (new; shaped like the other history files) for
C53 to C56, only from the evidence (`C55.md`, `mutations-guidance.md` and others) and the commit bodies. No
edits to existing history files. Update the history list and count in `docs/README.md`. Gates.

### 5.21 C58: "Mark the dashboard usability and access design and ADRs implemented, and delete the working plan"

The final commit:

1. `docs/design/dashboard-usability-and-access.md`: Status becomes Implemented, with the date and the
   commit range (the first and last implementation commits from `git log`). §12's Progress line is
   rewritten to list what landed: every commit, by hash and subject, grouped by slice; it notes the
   prerequisite `22f04bf` (and `a8bbac3`, the Angular 22 move) and states every deviation from the plan
   recorded in the commit bodies or history files (read them: for example the recency rule in the retry
   planner, defaults chosen, the follow-up commits). Also correct §12's opening sentence ("One local
   commit per logical change ...; nothing pushed"): the branch was pushed at the maintainer's request.
   Update §13 to what is really still open. Do not rewrite the design's reasoning.
2. ADRs 0006, 0007 and 0008: Status gains "Implemented" with the date and the commits that implemented
   each, keeping "Accepted".
3. Delete `docs/plans/dashboard-usability-and-access/` entirely (`git rm -r`), this file included, and
   remove every link to it from tracked files: grep the repository for `docs/plans` outside `docs/history`;
   the design document and the ADRs must not link to the deleted folder.
4. `docs/README.md`: record the statuses if it lists them.
5. Gates. Commit body: the implementation is complete; the plan folder is deleted as the plan itself
   required; the durable records are the design document, the ADRs, the reference docs and the four history
   files.

---

## 6. Notes for the final summary, and open points

Tell the maintainer, in the final summary after C58:

- **A retry of a business failure usually fails again.** A retry re-runs the step that failed with the
  same message. When the failure was decided by the message alone, as in the sample's `OrderSaga` payment
  failure, the saga returns to `Failed` (observed in the C37 live check: version 1 to 3, back to `Failed`).
  The design states it (§7.5, §10) and the user guide must say it (C56). The new step is visible in the
  timeline.
- **A stray file deleted after a refused delete.** During C38 an untracked 0-byte file appeared in the
  working tree (the C38 mutation log shows it as `dashboard-web/SagaAccessEnforcementTests.cs`). The
  permission system refused a delete; the agent then removed the file another way. Nothing tracked was
  affected, but the maintainer should know a refused action was worked around.
- **Angular 22.** The SPA moved from Angular 21 to 22.2.1 (`a8bbac3`, with `39a622a`) because the audit
  gate failed on GHSA-ch52-4w7c-c8xp and only CLI 22 drops the vulnerable chain. The maintainer chose the
  major upgrade. Design §12 carries a dated note; C58 must list it among the prerequisites and deviations.
- **`docs/dashboard.md` "Live updates (SignalR)"** is stale since C40 and must be rewritten in C51 (5.13).
- **Duplicate `nosniff`.** The C37 live check saw two `X-Frame-Options` headers (fixed by `c0a1ca4`) and
  `X-Content-Type-Options: nosniff` twice (nginx and the API, same value), which was not changed.

Open questions (design §13): only one is still open. The in-app "User guide" link opens
`docs/dashboard-guide.md` on GitHub's `main` branch, so a fork or an installation without GitHub access gets
upstream content or nothing; serving it from the dashboard would need a Markdown renderer or a pre-rendered
page in the image. The link stays one constant (`USER_GUIDE_URL`). Design §14 lists what is deliberately
deferred (snapshot retention, a multi-type list filter, a Postgres index, a payload-free timeline read, the
device-cookie lockout bypass, temporary-password expiry, single sign-on, RabbitMQ channel-open wrapping);
none of it is part of the remaining commits.

An open point of interpretation: design §11's "remove the retry policy" mutation (5.3, item 1).

---

## Appendix A. Evidence gathered before the pause (for C52)

These are facts from the live runs and mutation runs of this slice whose evidence files are not in the
repository. They were observed, not expected.

**C33 live check, 2026-10-02, HEAD `1d6b87f`.** `docker compose build dashboard-api` exit 0; with
`--no-cache` the restore layer restored `VSaga.Dashboard.Identity` and `VSaga.Dashboard.Identity.Sqlite`
(the two csproj `COPY` lines at Dockerfile lines 15 and 16). `docker compose up -d --build --wait` exit 0,
all containers healthy. The container held no `Dashboard__Identity__*` variable at that commit, so
`GET http://127.0.0.1:5080/health` answered `200` with
`{"status":"degraded",...,{"name":"identity","status":"degraded","description":"Dashboard:Identity:Sqlite:Path is not set. In a container it must name the identity database file on a volume (the image sets /var/lib/vsaga-dashboard/identity.db); there is no default inside the container."}]}`.
The API log had `IdentityStartup[7202]` (store not ready, retrying at most once every 10 s) and
`KeyRingProvider[48]` with `IdentityUnavailableException` from `IdentityStoreXmlRepository.ThrowIfNotReady`
(no in-memory key fallback); no unhandled exception. `docker inspect vsaga-dashboard-api-1` showed
`health=healthy restarts=0 running=true` while `identity` was Degraded; `docker compose ps` showed all five
containers up, the API and web Healthy. No identity directory was created; the process ran as
root (C37 adds `USER`). The UI at 4200 still worked (list, a Failed saga's detail, hub negotiate 200, no
console errors). Observation: the message's "(the image sets ...)" was untrue until C37 added the ENV.

**C37 live check, 2026-10-03, HEAD `f25b1ec`.** All six checks passed.

1. Fresh volume `vsaga_vsaga-dashboard-identity`; image `User=1654` with
   `Dashboard__Identity__Sqlite__Path=/var/lib/vsaga-dashboard/identity.db`; `id` in the API container
   `uid=1654(app) gid=1654(app)`; `/var/lib/vsaga-dashboard` mode `drwx------` owned by `app`,
   `identity.db` mode `-rw-------` `app:app`, no `-wal`/`-shm` file; `/health` 200 with `identity` healthy.
   Log: `IdentityStartup[7200]` with the path, an audit line `user.seed ... 'admin' ... succeeded`,
   `FirstAdministratorService[7212] Created the first dashboard administrator 'admin' from
   Dashboard:Admin:Username and Dashboard:Admin:Password`, `IdentityStartup[7201]` ready; no setup code
   logged; a framework Warning `XmlKeyManager[35] No XML encryptor configured`.
2. Through 4200 with curl: `GET /api/auth/session` set `vsaga.session.vsaga.af` (HttpOnly) and
   `XSRF-TOKEN` (readable, `samesite=strict`) and answered `authenticated:false`, `setupRequired:false`,
   `passwordMinLength:12`; `POST /api/auth/login` with `X-XSRF-TOKEN` and admin's credentials answered 200,
   set `vsaga.session.vsaga` (HttpOnly, `samesite=strict`), rotated `XSRF-TOKEN`, and returned the four
   permissions unscoped; `GET /api/sagas` with the cookie 200, without credentials 401. Side observation:
   the session response through nginx carried `X-Frame-Options` twice (SAMEORIGIN and DENY; fixed by
   `c0a1ca4`) and `X-Content-Type-Options: nosniff` twice.
3. `docker compose up -d --force-recreate --wait dashboard-api` (new container id): the same jar still
   authenticated (session 200 as admin, sagas 200); the new container logged no seed line and no new-key
   warning (keys read from the store).
4. `docker compose down` then `up -d --wait`: session kept. `docker compose down -v` then up: both volumes
   removed and recreated, admin seeded again with a new id; the old jar got 401 on `/api/sagas` and
   `authenticated:false` on the session; a fresh sign-in worked.
5. `docker compose -p vsaga-wolverine -f docker-compose.yml -f docker-compose.wolverine.yml config`:
   cookie name `vsaga.session.vsaga-wolverine`, volume `vsaga-wolverine_vsaga-dashboard-identity`, ports
   `127.0.0.1:5180` (API) and `127.0.0.1:4300` (UI), `Dashboard__ApiKeyRole: Operator`. The base project
   renders `vsaga.session.vsaga`; the brighter, chaos, http, masstransit, mongo and redis overlays each
   render `vsaga.session.vsaga-<name>`.
6. The SPA with its embedded key (Playwright): a Failed `OrderSaga`'s retry confirmation read "Re-run step 2
   (PaymentFailed, Gathering) for this saga only?" and "Other services that consume PaymentFailed still
   receive it."; `POST .../retry` answered 202 and carried `x-api-key`; the saga went from version 1 to 3
   and back to `Failed` (the sample's business failure is deterministic); console 0 errors, 0 warnings.

**C38 mutation runs (against the C38 working tree).** First run: "retry needs only view" failed exactly
`ARetryOfATypeOutOfScope_Is403EvenForAScopedOperator_WhileItsOwnTypeRetries`, `AViewer_CannotRetry` and
`TheDefaultViewerApiKey_ReadsEveryTypeWithData_ButCannotRetry`; "no redaction" failed
`WithoutSagasData_PayloadsErrorMessagesAndStateAreRedacted_OnEveryPath` and
`WithSagasData_ForTheSagasType_PayloadsErrorMessagesAndStateAreServed`; "blank type is a filter" failed
`TheList_ShowsOnlyVisibleTypes_AndABlankTypeFilterIsNoFilter` and
`ScopedSagaListerTests.AGrantOfASingleSpace_CannotWidenTheScope`; "no dedupe" failed
`ARowThatMovesBetweenTwoReads_IsNotEmittedTwice`; "culture-free tie-break lost" failed
`EverySortArm_WalkedPageByPage_EqualsAFullSortOfTheVisibleRows` and
`TiesAcrossTypes_BreakBySagaTypeOrdinal`. Three mutations ("drop the scope filter", "no attribution",
"lookups unfiltered") did not build and were redone as one-token variants in a rerun: "drop the scope
filter" failed `RowsOutsideTheScope_AreFilteredOut_EvenWhenTheProviderReturnsThem`; "attribution by bare
username" failed `ARetry_IsAttributedToTheApiKey` and `ARetry_IsAttributedToTheSignedInUser`; "lookups
unfiltered" failed `SagaTypesCorrelationsAndChildren_OnlyListVisibleTypes`. The restore build exited 0.

**C39 fix mutation runs, 2026-10-03, against `8d69429`.** `AllowDuplicateProperties = true` in
`JsonRequestBody.cs`: of 77 Admin and AuthEndpoints tests, exactly
`AdminUsersEndpointsTests.AnUnknownMemberOrAWrongValue_Is400_NamingItsPath_AndChangesNothing` (the
`{"isEnabled":false,"IsEnabled":true}` case) failed. The audit logger moved out of
`DashboardAudit.CategoryName` in `AdminEndpoints.cs`: of 76 Admin tests, exactly
`EveryUserChange_IsAudited_UnderTheAdministratorsName_AndARefusalToo` failed. Both restored with
`git checkout`; build 0 warnings; Admin and AuthEndpoints tests 106 passed.

The per-commit mutation checks of C31 to C36, C38 (beyond the runs above: counting a search as
rank-served, a blank route type falling back to any type, an unreduced TotalCount) and C40 are recorded in
their commit bodies.
