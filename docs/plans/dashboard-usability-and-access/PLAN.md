# Implementation plan: dashboard usability and access

**Status:** Temporary working plan, written 2026-10-02. Not yet implemented.
**Delete this folder when the work is done:** the final commit of the implementation removes
`docs/plans/dashboard-usability-and-access/` entirely. The durable records are the ones the plan itself
creates: `docs/design/dashboard-usability-and-access.md`, ADRs 0006 and 0007, the reference docs and the
`docs/history/` entries.

Detailed blueprints and their reviews are in [`blueprints/`](blueprints/): six `blueprint-*.md` files (one
per workstream) and three `review-*.md` files (security, consistency, feasibility). This file is the
reconciled summary. Where a blueprint and this file disagree, this file wins; where two blueprints
disagree, the review findings say which one wins (summarised under "Reconciliation rules" below).

## Context

The vSaga dashboard (`dashboard-web` Angular 21 SPA + `VSaga.Dashboard.Api`) has six gaps:

1. Nothing explains the UI: no user guide, no in-app guidance.
2. The time on each timeline entry is unlabelled (`entry.occurredAtUtc | date:'HH:mm:ss.SSS'`).
3. Timeline entries are not linked to the Map tab, although the map replays the same entries.
4. The Data tab shows only the current state blob; the state after each step is overwritten and lost.
5. The UI is outside docker compose, so the demo needs two commands and Node on the host.
6. The only protection is one API key compiled into the SPA bundle, so anyone who loads the page can retry
   sagas, and a retry republishes real messages.

## Decisions

| Topic | Decision | Source |
| --- | --- | --- |
| Identity storage | SQLite only for now, behind `IDashboardIdentityStore` so another store can be added | user |
| Access model | Built-in Administrator / Operator / Viewer, custom roles from a fixed permission list, grants scoped by saga type | user |
| First administrator | Seeded from `Dashboard:Admin:*` when set; otherwise a first-run setup screen | user |
| UI packaging | Separate `dashboard-web` nginx container that proxies `/api/` and `/hubs/` to `dashboard-api`; SPA uses relative URLs | lead |
| Ports | UI port = API port − 880 (4200 base; 4300–4800 overlays). Dashboard UI and API ports bind to `127.0.0.1` because the demo now ships a known admin login | lead (review S4) |
| Per-step data | New `SagaEntryType.StatePersisted` entry appended after each committed persist, state in `PayloadJson`; no persistence contract or schema change; on by default with opt-out, 256 KiB per-snapshot cap and 1 MiB per-saga budget | lead (review F1) |
| Sessions | ASP.NET Core cookie auth + antiforgery; Data Protection keys in the identity store | lead |
| API key | Kept as a machine credential mapped to `Dashboard:ApiKeyRole` (default Viewer); can never hold `access.manage`; removed from the SPA | lead (review S10) |
| Scoped lists | API merges per-type `ListAsync` results; no persistence contract change; a multi-type store filter is a recorded follow-up | lead (review F2) |
| Guidance | Hand-rolled tour (no npm dependency) plus `docs/dashboard-guide.md` | lead |

Second round of user decisions (2026-10-02):

| Topic | Decision |
| --- | --- |
| Retry | A retry must re-drive only the retried saga, from the step that failed onwards. The redrive carries a target-saga-type header (`MessageEnvelope`) and `SagaOrchestrator.HandleCoreAsync` ignores a targeted message addressed to another saga type. The failed step must be obvious in the UI: marked in the timeline and on the map, and named in the retry confirmation. How "the failed step" is re-driven when no step threw (business failure, timeout) is being confirmed with the user. |
| Retry without an exception | Re-run the failing step. The engine records `PayloadJson` on every `MessageReceived` entry. For a business failure the failing step is the last handled inbound message before the terminal entry; for a timeout it is the step that entered the timed-out state. Retry resets `CurrentState`/`Status` to what they were before that step (`ISagaAdminStore.ResetStateAsync`, unchanged contract; business fields are not rolled back) and replays only that message, targeted at that saga type. A saga recorded before payloads existed is refused (422) with an explanation. The reset-to-start path is removed. |
| Commits | Feature branch off `main`, one local commit per logical change, nothing pushed. |
| Setup screen | Requires a one-time code the API logs at start when no users exist (`Dashboard:Setup:Code` may preset it). No time window. |
| User guide | Text and tables, no screenshots. A written rule in `CONTRIBUTING.md` (and a header note in `docs/dashboard-guide.md` and the tour step definitions): a change to dashboard UI must update the guide and the tour in the same change; anchor-contract specs fail when a tour anchor disappears. |
| Guide mode | When switched on it follows the user: each page or area they open explains itself once (list, detail, map, timeline, data, retry, administration), with Replay available. |
| Lost admin | `Dashboard:Admin:ResetOnStart=true` with the seed username/password resets that user's password, re-enables the account and restores an unscoped Administrator grant at start; logged as a warning. |

## What changes for the user

- `docker compose up -d --build` starts everything; open `http://localhost:4200` and sign in.
- Timeline is grouped into steps; every entry shows "Recorded at 14:03:07.140 +1.224 s" (local time, UTC
  on hover). Clicking an entry opens the Map positioned on that entry, with an "as of entry" banner.
- Each step has a Data control: what changed and the full state after that step. A "Saga data" bar under
  the summary card offers At start, At end and Compare. The Data tab is gone.
- Login page, user menu, and an Administration area (users, teams, roles, grants per saga type). Retry and
  data are shown only to those permitted; a retry records who asked.
- A Guide toggle in the top bar walks through the list, detail and administration pages.

## Workstreams

### 1. Packaging (improvement 5) — [`blueprints/blueprint-packaging.md`](blueprints/blueprint-packaging.md)
- New: `dashboard-web/Dockerfile` (node:22 build → `nginxinc/nginx-unprivileged`), `.dockerignore`,
  `nginx/default.conf.template` (SPA fallback, proxy with WebSocket upgrade, per-request DNS resolution of
  `dashboard-api`, CSP and security headers), `dashboard-web/proxy.conf.mjs`.
- Modify: `docker-compose.yml` (service, identity volume, env), six overlays (`!override` UI port),
  `dashboard-web/src/app/api-config.ts` (relative URLs), `angular.json` (proxy, dev port 4201,
  `inlineCritical: false` for the CSP), `Program.cs` via new `Hosting/DashboardEdge.cs` (opt-in CORS,
  trusted forwarded headers — sole owner of both), API `Dockerfile` (non-root, identity directory),
  `.github/workflows/ci.yml` (compose config check, image build, smoke test through port 4200).

### 2. Timeline, map jump, data UI (improvements 2–4, client) — [`blueprints/blueprint-detail-ux.md`](blueprints/blueprint-detail-ux.md)
- New pure modules under `dashboard-web/src/app/util/`: `saga-transitions.ts` (fold entries into steps,
  joined by `messageId`/`causationId`), `json-diff.ts`, `state-json.ts` (replaces `prettyDataJson`),
  `time-format.ts`, `entry-type-label.ts`.
- New components: `saga-timeline`, `saga-data-inspector`, `saga-data-overview`, `local-time`.
- Modify: `pages/saga-detail/*` (two tabs, `?tab=&entry=&data=` query params, coalesced live refresh that
  also refetches the detail, load-error states, open inspectors kept across tab switches),
  `components/saga-map/*` (`focusSequence` input, banner, separate `node--focus` highlight),
  `models/saga.model.ts`, `styles.scss` (shared classes).
- Failed step made obvious: the step that failed gets a "Failed here" marker and error styling in the
  timeline, the map opens focused on it for a Failed/TimedOut saga, and the retry confirmation reads
  "Re-run step N (<message type>, <from state>) for this saga only" using the retry plan from the API.
  Until authentication lands the retry button stays where it is; the wording changes with the retry work.

### 3. State snapshots (improvement 4, server) — [`blueprints/blueprint-engine-snapshots.md`](blueprints/blueprint-engine-snapshots.md)
- `VSaga.Abstractions`: `SagaEntryType.StatePersisted` (appended last), `Persistence/SagaStateSnapshot.cs`.
- `VSaga.Core/Runtime/SagaOrchestrator.cs`: `PersistAndSnapshotAsync` at the four committing persists
  (step success, step failure, timeout final persist, delivery exhaustion), best-effort with its own short
  deadline; `LogAsync` stamps the stored sequence number; `TimeoutScheduled`/`SagaCompleted` carry the
  inbound message id and the log sink fills `CausationId` (review F9).
  `SagaOrchestratorOptions`: `RecordStateSnapshots`, `MaxStateSnapshotBytes`, `MaxStateSnapshotBytesPerSaga`.
- `VSaga.Persistence.EFCore/EfCoreSagaEventLogStore.cs`: detach the entity when an append's save throws.
- `VSaga.Dashboard.Api`: `SagaMapBuilder` skips snapshots; `Endpoints/SagaTimelineRedaction.cs`;
  `Endpoints/SagaResetSnapshotRecorder.cs` (snapshot after a retry reset); payload-free SignalR pushes.
- Samples: OrderProcessing binds an `Orchestrator` config section; persistence samples describe the entry.
- Retry from the failed step (second-round decision; not in the blueprint, design it in the design doc first):
  - `VSaga.Abstractions/Transport/MessageEnvelope.cs`: `TargetSagaTypeHeader`. `SagaOrchestrator.HandleCoreAsync`
    acks and returns, logging nothing, when the header names another saga type.
  - `SagaOrchestrator.RunStepAsync`: the `MessageReceived` entry carries the message body in `PayloadJson`
    (MongoDB's existing payload guard applies; redaction already covers it).
  - `Endpoints/SagaEndpoints.cs` `RetrySagaAsync`: pick the failed step — last `StepFailed`; else the last
    handled inbound message before the terminal entry (same rule as `SagaMapBuilder.ResolveFailedMessageIds`);
    else, for a timeout, the step whose `ToState` is the timed-out state. Reset `CurrentState`/`Status` to
    that step's `FromState`/`Running` via `ISagaAdminStore.ResetStateAsync`, republish only that message with
    the target header. 422 with an explanation when the step has no recorded payload. The reset-to-start
    branch and its tests are replaced; the in-process `SagaOrchestrator.RetryAsync` is unchanged.
  - New `GET /api/sagas/{sagaType}/{correlationId}/retry-plan` (or a field on the detail response) returning
    the step a retry would re-run, so the SPA can name it before the user confirms.
  - `docs/dashboard.md` "Manual retry" is rewritten; ADR 0007 or a short ADR 0008 records the change.

### 4. Authentication and access, server (improvement 6) — [`blueprints/blueprint-auth-backend.md`](blueprints/blueprint-auth-backend.md)
- New projects: `dotnet/src/VSaga.Dashboard.Identity` (models, `IDashboardIdentityStore`, EF Core store,
  `CredentialVerifier`, `AccessEvaluator`, `AccessAdministrationService`, key-ring repository),
  `dotnet/src/VSaga.Dashboard.Identity.Sqlite` (generated migrations, analyzers off, per the
  `VSaga.Persistence.EFCore.Postgres` precedent), `dotnet/tests/VSaga.Dashboard.Identity.Tests`.
- `VSaga.Dashboard.Api`: policy scheme (cookie or API key), fallback authorization policy, antiforgery
  enforced by endpoint metadata, rate limiting keyed on address + username, `Endpoints/AuthEndpoints.cs`,
  `AdminEndpoints.cs`, `ScopedSagaLister.cs`; `SagaEndpoints.cs` gains permission checks, list filtering
  and redaction (payloads **and** `errorMessage`, review S8); `Hubs/SagaHub.cs` gains per-type list groups,
  per-call checks, an origin guard and connection abort on any stamp rotation or sign-out (S7).
- Permissions: `sagas.view`, `sagas.data`, `sagas.retry`, `access.manage` (unscoped grants only).
- Hardening adopted from review: absolute session lifetime; seeding fails closed (setup never opens when
  seed keys are set); explicit identity path required in containers; structured audit log events;
  `Cache-Control: no-store` + `nosniff` on `/api`; saga-type names in grants validated; `api-key` reserved
  as a username; admin request records reject unknown JSON members.

### 5. Authentication and access, SPA — [`blueprints/blueprint-auth-frontend.md`](blueprints/blueprint-auth-frontend.md)
- `AuthService` (signals, session loaded in an app initializer), guards, one `authInterceptor` (401, 403,
  antiforgery retry); `apiKeyInterceptor` and `DASHBOARD_API_KEY` deleted.
- `SagaHubService`: no token factory, `stopAndReset()`/`resume()`, stops retrying when the session is gone.
- Lazy routes `/login`, `/setup`, `/account`, `/admin/{users,teams,roles}` with grants editor and
  effective-access preview. Wire contract = the API records (`isEnabled`, `isBuiltIn`, `lastSignInAtUtc`),
  pinned by shared golden JSON fixtures asserted from both test suites (review C1/S6).

### 6. Guidance and documentation (improvement 1) — [`blueprints/blueprint-guidance-docs.md`](blueprints/blueprint-guidance-docs.md)
- `GuideService`, `GuideToggle`, deferred `GuideOverlay`; one `data-tour` vocabulary owned by this
  workstream (review C7). List rows and sort headers become keyboard-operable.
- Docs: `docs/design/dashboard-usability-and-access.md`, ADR 0006 (authentication and identity store),
  ADR 0007 (state snapshots), `docs/dashboard-guide.md`, updates to `README.md`, `docs/dashboard.md`
  (keeping the `#authentication` anchor), `docs/configuration.md`, `docs/observability.md`,
  `docs/persistence.md`, `docs/transports/index.md`, `CONTRIBUTING.md`, `dashboard-web/README.md`; one new
  `docs/history/` file per slice after live verification (existing history files are never edited).

## Order of work

The consistency review's 54-commit sequence ([`blueprints/review-consistency.md`](blueprints/review-consistency.md)) is the working order, in five slices,
each leaving `dotnet build`, `dotnet test`, `ng build` and `ng test` green:

1. Records: design document and both ADRs.
2. Packaging (commits 2–9).
3. Timeline, map jump, snapshots (10–26): SPA fold lands before the engine emits snapshots.
4. Authentication (27–48): identity projects → sign-in → enforcement → hub → SPA switch-over → admin UI.
5. Guidance (49–54).

Work happens on the feature branch `dashboard-usability-and-access` (already created off `main`, no
commits yet), one local commit per logical change following `CONTRIBUTING.md`; nothing is pushed.
The retry-from-failed-step commits slot into slice 3 after the snapshot commits (engine header and payload,
then the endpoint rewrite, then the SPA marker and confirmation text).

## Verification

- Per commit: `dotnet build dotnet/VSaga.slnx` (zero warnings), `dotnet test dotnet/VSaga.slnx`,
  and in `dashboard-web`: `npm audit --audit-level=low`, `npx ng build` (no budget warnings),
  `npx ng test --watch=false`.
- Mutation checks per slice (repo convention): e.g. snapshot before the persist, no EF detach, no map
  filter, retry policy removed, origin guard removed, redaction removed — each must fail only its own tests.
- Live, `docker compose up -d --build`: UI on 4200 signs in with the seeded admin; a Completed `OrderSaga`
  has one `StatePersisted` per handled message and the last equals the detail's `dataJson`; `/map` has no
  snapshot events; a Viewer cannot retry; a scoped user sees and receives pushes only for their saga
  types; recreating `dashboard-api` keeps the session. Repeat the snapshot checks on the mongo and redis
  overlays and record measured storage for `docs/persistence.md`. Two stacks side by side in one browser.
- Browser pass on the timeline, map jump, data views, admin screens and the tour (keyboard only, reduced
  motion, CSP console clean).

## Out of scope, recorded as follow-ups

- Retention or erasure of recorded snapshots (needs a persistence-contract change).
- A multi-type filter on `SagaListFilter` (makes scoped lists efficient on Redis) and a
  `(SagaType, UpdatedAtUtc)` Postgres index.
- A payload-free timeline read for the engine's visited-states lookup.
- Device-cookie lockout bypass, expiry of temporary passwords, single sign-on.

## Reconciliation rules

Apply every blocker and major finding in the three review files. The ones that settle a conflict between
blueprints:

- **Edge settings have one owner.** `Hosting/DashboardEdge.cs` (packaging) is the only code that reads
  `Dashboard:WebOrigin` and `Dashboard:TrustedProxies`. `Dashboard:WebOrigin` defaults to empty (CORS off).
  The auth blueprint's own forwarded-headers step, including `*`, is dropped.
- **Identity path.** `/var/lib/vsaga-dashboard/identity.db`, set as ENV in the API image and in compose.
  Packaging owns the API Dockerfile runtime lines and the compose block; the auth work adds only the two
  csproj COPY lines. No fallback under the application directory: an unset path in a container is an error
  reported by the `identity` health check.
- **Cookie names.** `Dashboard:Session:CookieName`, set per compose project. `XSRF-TOKEN` stays shared; an
  antiforgery 400 makes the SPA refetch the session and retry once, and that path is tested on both sides.
- **Admin wire contract.** The API records are canonical (`isEnabled`, `isBuiltIn`, `lastSignInAtUtc`, arrays
  never null). Team membership is written only through the team payload. Request records reject unknown
  JSON members. Golden JSON fixtures are asserted from both the .NET and the Angular tests.
- **Settings style.** Dashboard settings are read once into plain singletons and validated at composition,
  like the provider switches; no options binding for vSaga-owned settings.
- **Tour anchors.** The guidance blueprint's `data-tour` list is the only vocabulary; the other workstreams
  add no `data-tour` attributes.
- **Shared names.** `.topbar-end`; `.btn` with `.btn--quiet`; `banner banner--warning` / `banner banner--error`;
  pure helpers under `src/app/util/`; the data gate is a `canViewData` input; `PermissionKey` is the one
  permission type. `src/app/testing/` is excluded from the app build by the first commit that creates it.
- **Docs ownership.** Each file under `docs/` is written once: ADR 0006 is
  `docs/adr/0006-dashboard-authentication-and-identity-store.md`, ADR 0007 is
  `docs/adr/0007-state-snapshots-in-the-event-log.md`. The retry-from-failed-step change is recorded in
  the design document and in ADR 0007 (or a short ADR 0008 if it outgrows it).
- **Decisions made after the blueprints were written** (they override the blueprints): the setup screen
  needs a one-time code and has no time window; `Dashboard:Admin:ResetOnStart`; loopback port bindings in
  compose; the per-saga snapshot budget; retry re-runs the failing step and is targeted at one saga type
  (the reset-to-start path and the "check every saga type" alternative in the reviews are not used);
  `errorMessage` is redacted together with payloads.

## Finishing

- The last commit deletes `docs/plans/dashboard-usability-and-access/` and marks the design document and
  both ADRs as implemented.
- Nothing is pushed unless the maintainer asks.
