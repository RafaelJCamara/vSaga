# Guide mode in the SPA, the user guide, and the documentation set (improvement 1 and the records)

## Summary

- Guide mode is an eager `GuideService` (state, routing, persistence), a `GuideToggle` in the top bar, and a `GuideOverlay` loaded with `@defer` that runs one tour per page (`list`, `detail`, `admin`) against `data-tour` anchors. No npm dependency; about 4 kB eager, 10 kB lazy.
- Off by default, with a one-time hint at the toggle. Switching Guide on starts the current page's tour; while on, each page's tour starts once by itself; "Replay tour" repeats it.
- A missing anchor falls back to a second anchor, then to a centred popover. A missing permission removes the step before the tour starts, so "Step n of m" stays true.
- New `docs/dashboard-guide.md` (text and tables, no screenshots), linked from the app, the README and the docs index. `docs/dashboard.md` is restructured but keeps the four headings that code and other docs link to.
- Records: `docs/design/dashboard-usability-and-access.md`, ADR 0006 (sign-in, access, identity store), ADR 0007 (`StatePersisted`), four history files written only after live verification.

## Design

### 1. State and routing (eager)

`src/app/models/guide.model.ts`:

```ts
export type GuideTourId = 'list' | 'detail' | 'admin';
export type GuidePermission = 'sagas.view' | 'sagas.data' | 'sagas.retry' | 'access.manage';
export type GuidePlacement = 'bottom' | 'top' | 'right' | 'left';
export interface GuideRoute { id: GuideTourId; version: number; match: RegExp; readyAnchor: string;
  requires: GuidePermission; docsAnchor: string }
export interface GuideStep { id: string; title: string; body: string; anchor: string | null;
  fallbackAnchor?: string; reveal?: string; requires?: GuidePermission; placement?: GuidePlacement }
export type GuidePermissionCheck = (permission: GuidePermission, sagaType?: string) => boolean;
```

`src/app/services/guide-routes.ts`: `USER_GUIDE_URL = 'https://github.com/RafaelJCamara/vSaga/blob/main/docs/dashboard-guide.md'` (the base the package READMEs use, `dotnet/src/VSaga.Core/README.md:45`) and `GUIDE_ROUTES`, each `version: 1`, matched against the path without its query:

- `list`: `^/sagas/?$`, ready `list-table`, requires `sagas.view`, docs `#the-saga-list`.
- `detail`: `^/sagas/[^/]+/[^/]+/?$`, ready `detail-summary`, requires `sagas.view`, docs `#the-saga-detail-page`.
- `admin`: `^/admin(/|$)`, ready `admin-nav`, requires `access.manage`, docs `#administration`.

`src/app/services/guide.service.ts`:

```ts
export const GUIDE_STORAGE_KEY = 'vsaga.guide'; // {"v":1,"enabled":true,"seen":{"list":1},"hintDismissed":true}
export const GUIDE_STORAGE = new InjectionToken<Storage | null>('GUIDE_STORAGE', { factory: safeLocalStorage });
export const GUIDE_PERMISSION_CHECK =
  new InjectionToken<GuidePermissionCheck>('GUIDE_PERMISSION_CHECK', { factory: () => () => true });

@Injectable({ providedIn: 'root' })
export class GuideService {
  readonly enabled: Signal<boolean>;
  readonly route: Signal<GuideRoute | null>;   // tour for the current URL; null without its permission
  readonly sagaType: Signal<string | null>;    // decoded from /sagas/:sagaType/:id
  readonly request: Signal<{ route: GuideRoute; nonce: number } | null>;
  readonly running: Signal<GuideTourId | null>;
  readonly canReplay: Signal<boolean>;         // enabled && route && !running && !request
  readonly showHint: Signal<boolean>;          // !enabled && !hintDismissed && route
  setEnabled(on: boolean): void; toggle(): void; replay(): void; dismissHint(): void;
  started(id: GuideTourId): void; ended(id: GuideTourId, remember: boolean): void; abandoned(): void;
}
```

Rules:
- `setEnabled(true)` clears `seen`, sets `hintDismissed`, persists and requests the current route's tour. `setEnabled(false)` clears `request`; the overlay then ends a running tour without remembering it.
- `NavigationEnd` to another page (route id plus path) sets `request = enabled && route && seen[id] !== version ? route : null`. The list's query-only navigations (`saga-list.ts:135-147`) change nothing.
- `replay()` requests the current tour regardless of `seen`; `ended(id, true)` stores `seen[id] = version`; bumping a route's `version` shows a changed tour once more.
- `vsaga.guide` is the only key and the app's first browser storage: per origin (so per compose stack), not per user. Corrupt JSON or an unknown `v` reads as defaults; writes are try/catch; without storage, state stays in memory.

### 2. Toggle (eager)

`<app-guide-toggle>` goes in a new right-hand group of the top bar (`app.html:2-7`; `.topbar-actions` with `margin-left: auto` in `app.scss`), between the administration link and the user menu, only when signed in. Its root carries `data-tour="topbar-guide"`. It renders a `<button aria-pressed>` "Guide"; "Replay tour" when `canReplay()`; a "User guide" link (`target="_blank" rel="noopener noreferrer"`); and, when `showHint()`, a `role="status"` callout: "New here? Turn on Guide for a walkthrough of each page." with "Start the tour" and "No thanks".

### 3. Overlay (lazy)

`app.html` ends with `@defer (when guide.enabled()) { <app-guide-overlay /> }`, a sibling of the element holding all page content.

- **Begin.** An effect on `request()` queries `[data-tour="<readyAnchor>"]` at once, then every 250 ms, until found or 20 tries have passed. Then `steps = GUIDE_TOURS[id].filter(available)`: the permission check passes (given `sagaType()` on the detail page) and the step is centred by design or one of `anchor`, `reveal`, `fallbackAnchor` is in the DOM. If no kept step has an anchor (a saga that failed to load), `abandoned()`; otherwise `started(id)`.
- **Show a step** (a token discards late continuations): query `anchor`; if missing and `reveal` is set, lift `inert`, click the reveal control, restore `inert` and poll every 50 ms for up to 1 s; then try `fallbackAnchor`; failing that, show the popover centred with no spotlight. A step is never skipped silently. An element outside the viewport gets `el.scrollIntoView?.({ block: tallerThanViewport ? 'start' : 'center', inline: 'nearest', behavior: reducedMotion ? 'auto' : 'smooth' })`. Then the primary button is focused with `preventScroll`.
- **Geometry** is pure, in `guide-geometry.ts` (the `saga-map-layout.ts` precedent):

```ts
export interface Box { top: number; left: number; width: number; height: number }
export interface Size { width: number; height: number }
export function spotlightBox(anchor: Box, viewport: Size, pad = 6): Box | null; // inflated, clipped; null off-screen
export function placePopover(spot: Box | null, popover: Size, viewport: Size, preferred: GuidePlacement = 'bottom',
  gap = 12, margin = 12): { top: number; left: number; placement: GuidePlacement | 'inside' | 'center' };
```

  `placePopover` tries the preferred side, its opposite, then the other two; the first that fits inside the margin wins, cross axis clamped. Nothing fits (the table fills the viewport): bottom-right corner. No spotlight: centre.
- **Tracking.** While a step shows, a `requestAnimationFrame` loop re-queries a disconnected anchor, reads its rect and writes `top/left/width/height` directly onto the spotlight and popover when a rounded value changed. That covers resize, any scroll container, live rows shifting the layout and `@if` re-creation, with no per-frame change detection. `reposition()` is public for specs (the `SagaMap.tick()` precedent, `saga-map.ts:103-104`). jsdom 28 lacks ResizeObserver, `scrollIntoView` and `matchMedia`, hence the guards.
- **Modality and keyboard.** While active, each sibling of the overlay host gets `inert` (`toggleAttribute`), and a fixed full-viewport layer cancels `mousedown` so a click on the dimmed page cannot take focus. A document `keydown` listener exists only while active: Escape ends, ArrowRight and ArrowLeft move, Tab wraps inside the popover. Markup: `<section role="dialog" aria-modal="true" aria-labelledby aria-describedby>` with title and body in an `aria-live="polite"` region, "Step n of m", and "User guide" (`USER_GUIDE_URL + docsAnchor`), "Skip tour", "Back", "Next"/"Done".
- **End.** Remove `inert`, call `ended(id, remember)`, return focus to the element focused at begin, else the toggle. Done, Skip and Escape remember; Guide switched off, navigation to another page and destroy do not.
- **Motion and style.** `reducedMotion` is read as in `saga-map.ts:33`; geometry never animates, and the popover's 140 ms fade is removed under `prefers-reduced-motion` (as `saga-map.scss:175-179`). Spotlight: `box-shadow: 0 0 0 200vmax rgba(5, 8, 14, .72)`, `pointer-events: none`, a 2 px accent outline that survives forced colours. `position: fixed`, `z-index` 1000 to 1002. Two stylesheets of about 1 kB, using only the tokens in `styles.scss:5-25`.

### 4. Tours (`guide-tours.ts`, lazy)

Ids come from one `GUIDE_ANCHORS` constant. Notation: `r` reveal, `f` fallback, `p` permission.

**list**
1. Centred. **Welcome to the saga dashboard.** This page lists every saga instance you may see, across all saga types. The tour takes about a minute. Press Esc to leave; Guide in the top bar brings it back.
2. `list-filters`. **Narrow the list.** Filter by status, kind or saga type, or search by saga type or correlation id. Filters, sort and page live in the address bar, so a view can be bookmarked or shared.
3. `list-table`. **One row per saga instance.** Each row shows a saga's correlation id, type, kind, current state and status. Rows update live; a banner appears if live updates disconnect.
4. `list-sort`, f `list-table`. **Sort by status or last update.** Select Status or Updated to sort, and again to reverse. The server sorts the whole result, not only this page. Status follows the lifecycle, Running to Cancelled.
5. `list-row`, f `list-table`. **Open a saga.** Select a row to open that saga: its summary, service map, timeline and the data it held at each step.
6. `list-pagination`, f `list-table`. **Page through results.** Choose the page size, move between pages or jump to one. When new sagas arrive while you are past page 1, a banner offers a refresh instead of moving the rows you are reading.
7. `topbar-guide`. **Guide mode.** While Guide is on, each page shows its tour the first time you open it. Replay tour runs it again; User guide opens the full documentation. Switch Guide off here when you are done.

**detail**
1. `detail-summary`. **The saga at a glance.** Type, correlation id, kind, status, current state and version, with created and updated times in your local time. Links below lead to sagas sharing this correlation id, the parent saga and any sub-sagas.
2. `map-canvas`, r `detail-tab-map`. **Service map.** Boxes are services: whoever started the saga, the saga itself and each participant. Arrows are messages: dashed got no reply, dotted is a compensation, red is where the saga failed.
3. `map-controls`, r `detail-tab-map`. **Replay the saga.** Play, step or drag the slider to move through the history one recorded entry at a time, at 0.5x to 4x. The line underneath names the entry shown. Playback pauses at the failure.
4. `timeline`, r `detail-tab-timeline`. **Timeline.** The engine's record, grouped into steps. A step begins when the saga starts, receives a message, fires a timeout or is retried, and lists what followed: state changes, messages sent, compensations.
5. `timeline-entry`, r `detail-tab-timeline`, f `timeline`. **Recorded at, and jump to the map.** Recorded at is when the engine wrote the entry, in your local time; hover for the UTC timestamp and the time since the first entry. Select an entry to open the map positioned on it.
6. `timeline-step-data`, r `detail-tab-timeline`, f `timeline`, p `sagas.data`. **Data after each step.** Open a step's data to see the saga's data exactly as saved when that step finished, and what changed since the previous step: what a retry or redelivered message starts from.
7. `detail-data`, f `detail-summary`, p `sagas.data`. **Data at the start and at the end.** Data at start shows the first saved data beside the message that started the saga. Data at end shows it as it is now. Compare puts the two side by side.
8. `detail-retry`, f `detail-summary`, p `sagas.retry`. **Retry a failed saga.** Shown when a saga is Failed or TimedOut. A step that threw is replayed with the message that failed; a business failure or timeout resets the saga and replays its first message. Participants receive it again; the timeline records who retried.

**admin**
1. `admin-nav`. **Access administration.** This area controls who can sign in and what each person can see and do. Only accounts holding Manage access can open it.
2. `admin-nav-users`. **Users.** A user is one person's sign-in. Create accounts, reset passwords and disable accounts here. Disabling a user or changing their password ends their open sessions.
3. `admin-nav-teams`. **Teams.** A team is a named group of users. Access granted to a team applies to every member, on top of what each holds directly.
4. `admin-nav-roles`. **Roles and permissions.** A role is a set of permissions: View sagas, View saga data, Retry sagas, Manage access. Administrator, Operator and Viewer are built in and fixed; custom roles combine the same four.
5. `admin-list`. **Grants and scope.** Access comes from grants. A grant pairs a role with a scope: all saga types, or named saga types only. Manage access counts only when granted for all saga types.
6. Centred. **You cannot lock everyone out.** The dashboard refuses to delete, disable or demote the last enabled user who can manage access.

### 5. User guide `docs/dashboard-guide.md`

H2 headings are link targets from the app; their wording is fixed.

- `## Opening the dashboard`: one command; UI port per stack (4200, then 4300 to 4800).
- `## Signing in`: the first administrator (seeded, or the setup screen and its window), lockout, forced password change, sign out, idle timeout.
- `## Guide mode`: toggle, when tours start, Replay, keys, stored per browser.
- `## The saga list`: columns; the seven statuses and two kinds in plain words; filters and search; sort; paging; the three banners; what a scoped user sees.
- `## The saga detail page`: `### Summary`; `### Map` (node kinds, legend, replay, as-of-entry banner); `### Timeline` (steps, entry-type glossary, Recorded at, jump to map); `### Saga data` (step inspector, start, end, compare; why a snapshot can be missing); `### Retrying a saga` (two shapes, side effects, the 202/409/422/502 outcomes in user terms, attribution).
- `## Administration`: users, teams, the built-in roles against the four permissions, grants with a worked scope example, the last-administrator rule.
- `## Your account`; `## Troubleshooting`.

Screenshots: not now. `docs/` has no image or convention, every screen changes during this sequence, and a stale image fails silently whereas the tour is tested. If wanted later: `docs/images/dashboard-guide/`, captured after live verification.

### 6. `docs/dashboard.md`

- New `## Running it` (compose service, UI ports, dev server); the intro points users at the guide.
- `## API endpoints`: a Permission column and three groups (saga routes, `/api/auth/*`, `/api/admin/*`). `### Manual retry` gains attribution and the snapshot appended after a reset. New `### State snapshots`: `StatePersisted` in `/timeline`, the size marker, omission without `sagas.data`.
- `## Authentication`, rewritten: `### Signing in`, `### The first administrator`, `### Passwords, lockout and rate limits`, `### CSRF protection`, `### API key (machine clients)`, `### Failure responses`.
- New `## Access control`: `### Permissions`, `### Roles`, `### Grants and saga-type scope`, `### What a scoped caller sees`, `### The identity store`.
- `## Live updates (SignalR)`, rewritten: cookie on negotiate and WebSocket, access checks in the subscribe methods, per-saga-type list groups, payload-free `TimelineEntryAdded`.
- `## The SPA`, rewritten (routes, two tabs, timeline steps, guide mode, same-origin serving, dev proxy; lines 141-148 go). `## Saga Map` gains the as-of-entry banner and that `StatePersisted` is skipped.

Four headings must survive verbatim because they are link targets:
- `## Authentication`: `ApiKeyAuthenticationHandler.cs:86` (the 401 body), `README.md:105`, `configuration.md:363`, `dashboard-web/README.md:60`.
- `### Manual retry`: `transports/index.md:38`, `saga-dsl.md:165`, `testing.md:35`.
- `## Saga Map`: `observability.md:18`. `## The SPA`: `configuration.md:386`.

### 7. Other documents

- `README.md`: intro (7-12), layout block (157-166) and Documentation list (168-191) mention sign-in, per-step data, the UI image and the guide. "Run the demo" belongs to the packaging assignment.
- `docs/configuration.md`: `SagaOrchestratorOptions` (36-53) lists three tunables (docs C# blocks are compiled, commit `34c835d`). `## Dashboard` (351-386) becomes one table grouped as identity store, first administrator, sessions and passwords, API key, browser origin. `### Dashboard:ApiKey` stays because line 6 links `#dashboardapikey`. `Dashboard:WebOrigin` matters only when the SPA is served from another origin.
- `docs/observability.md`: the entry-type list (20-24) gains `StatePersisted`; a paragraph says the engine never reads it and that it shares the log's no-retention rule (6-11).
- `docs/concepts.md:63`: "Data tab" becomes the step data views.
- `docs/transports/index.md`: the port table (158-165) gains a Dashboard UI column; "Viewing an overlay's dashboard" (170-175) becomes "open that stack's UI port".
- `docs/persistence.md`: Redis capacity (329-345) and MongoDB storage (626) re-measured with snapshots on; a pointer that dashboard identity is not saga persistence.
- `docs/README.md`: three entries reworded (14-26); the guide, design document, both ADRs and the history count added (85-92).
- `dashboard-web/README.md`: "Run it" (8-22) and "How it reaches the API" (39-65) rewritten; "data" tab wording (3-4, 72); layout (67-79); a new "Guide mode" section.
- `CONTRIBUTING.md`: live verification (51-72) goes through the stack's UI port, since only compose exercises the proxy; the PR checklist (98-106) gains a line on keeping `guide-tours.ts` and the guide current.
- Dated bracketed notes only: `docs/design/production-readiness.md:78-81`, `docs/adr/0002-redis-persistence-provider.md:241`, ADR 0005.

### 8. Records

**Design document** `docs/design/dashboard-usability-and-access.md`, with a Status line: 1 What it is, the three recorded positions it reverses (shared key, UI outside compose, dashboard writes out of scope) and Decisions already taken; 2 What already exists; 3 Constraints found by tracing the code; 4 to 8 one section per area; 9 Failure modes; 10 Tests, mutation checks, live verification; 11 Commit sequence and Progress; 12 Open questions; 13 Explicitly deferred.

**ADR 0006**, `0006-dashboard-sign-in-access-and-identity-store.md`: "Dashboard users sign in with a session cookie; access is role and saga-type scoped; identity lives in a dashboard-owned SQLite store". One record with numbered sub-decisions, as ADR 0003 did. Supersedes the shared-key position in `docs/history/project-origins-and-hardening-pass.md`. Options: A two shared keys; B external OIDC; C ASP.NET Core Identity; D cookie sessions plus `IDashboardIdentityStore` on SQLite (chosen); E identity in each saga persistence provider; F bearer tokens in browser storage. Negative consequences: one API instance per SQLite file, a volume to back up, password handling becomes ours, a bounded merge for scoped lists. Invalidated by API replicas, an SSO requirement, per-instance scoping.

**ADR 0007**, `0007-state-snapshots-in-the-event-log.md`: "Per-step saga state is recorded as `StatePersisted` entries in the existing event log". Relates to 0005, 0003, 0002. Options: A a new field on `SagaLogEntry`; B a separate history store; C capture before the persist; D a post-commit entry with the state in `PayloadJson` (chosen); E rebuild state in the dashboard; F store diffs. Negative consequences: a state copy per committed transition in a log that cannot be pruned and that every step reads in full; not atomic with the persist; business data duplicated. Invalidated by log retention, large state, native state storage.

Both follow the template of ADRs 0001 to 0005, say "Accepted" once the user approves this plan, and gain "Implemented" in the last commit.

**History**: four new files shaped like `redis-persistence-provider.md`, each written from the real run in the commit after its slice's live verification: `dashboard-ui-in-compose.md`, `timeline-labels-map-jump-and-state-snapshots.md`, `dashboard-sign-in-and-access.md`, `dashboard-guide-mode-and-user-guide.md`. No existing history file is edited.

## Files

Create under `dashboard-web/src/app/`: `models/guide.model.ts`; `services/guide-routes.ts`; `services/guide.service.ts`; `components/guide-toggle/guide-toggle.{ts,html,scss}`; `components/guide-overlay/guide-overlay.{ts,html,scss}`, `guide-geometry.ts` and `guide-tours.ts`; a spec beside each except the model and the route table.

Create under `docs/`: `dashboard-guide.md`, `design/dashboard-usability-and-access.md`, the two ADRs, four history files.

Modify:
- `app.html`, `app.ts`, `app.scss`, `app.spec.ts`: toggle, deferred overlay, `.topbar-actions`.
- `saga-list.html`, `saga-detail.html`, `saga-map.html`, the timeline component and the admin templates, with their specs: anchors.
- `docs/dashboard.md` and the nine documents in section 7; three records get dated notes.

Most critical, under `C:/Users/rafae/Documents/Projects/vSaga/`: `dashboard-web/src/app/services/guide.service.ts`, `dashboard-web/src/app/components/guide-overlay/guide-overlay.ts`, `dashboard-web/src/app/components/guide-overlay/guide-tours.ts`, `docs/dashboard-guide.md`, `docs/dashboard.md`.

## Tests

No existing test changes; about 60 additions. A fake `Storage` and permission check are injected through the two tokens.

- `guide-geometry.spec.ts` (8): inflate and clip; null off-screen; placement fallbacks in order; clamping; corner when the anchor fills the viewport; centre without a spotlight.
- `guide.service.spec.ts` (14): defaults, restore, corrupt JSON, unknown `v`, storage that throws; both `setEnabled` directions; URL mapping; request on navigation when unseen, none when seen, again after a version bump; query-only navigation keeps the nonce; no route without the permission; `replay()`; `ended` with and without `remember`.
- `guide-tours.spec.ts` (6): one tour per route; unique step ids; every anchor, fallback and reveal is in `GUIDE_ANCHORS`; `reveal` is only ever a detail tab; length limits; valid permissions.
- `guide-overlay.spec.ts` (17, fake anchors and timers): idle renders nothing; waits for the ready anchor; after 5 s starts with what can be anchored, or abandons; drops unpermitted steps and counts the rest; Next, Back, Done; Done and Escape remember; arrows; Tab wraps; primary button focused; siblings inert, then restored; reveal clicked once, only when needed; fallback, then centred; Guide off mid-tour does not remember; destroy cancels timers and the frame.
- `guide-toggle.spec.ts` (7): `aria-pressed`; toggling; Replay only when `canReplay()`; link attributes; the hint.
- Anchor contract: `saga-list.spec.ts` +1 (`setup`, 34-57); `saga-detail.spec.ts` +3 (`setup`, 76-122); `saga-map.spec.ts` +1 (`createComponent`, 57-62); each admin page spec +1; `app.spec.ts` +1 beside 19-24.

## Commit sequence

1. Add the dashboard usability and access design and accept ADRs 0006 and 0007 (first overall).
2. After packaging: Document the dashboard UI as a compose service.
3. After the timeline and snapshot slices: Document state snapshots, the labelled timeline and jump to map.
4. After authentication: Document dashboard sign-in, access control and the identity store.
5. Add guide mode: a top-bar toggle and a tour of the saga list.
6. Add the saga detail tour.
7. Add the administration tour.
8. Add the dashboard user guide and link it from the app, the README and the docs index.
9. After each slice's live verification: Record it (that slice's history file; the snapshot one carries the re-measured storage figures).
10. Mark the design and ADRs 0006 and 0007 implemented.

## Verification

- `cd dashboard-web && npm ci && npm audit --audit-level=low && npx ng build && npx ng test --watch=false`: 0 vulnerabilities; no budget warning; a lazy chunk for the overlay; initial total under 500 kB; about 60 more tests pass.
- Mutation checks (`CONTRIBUTING.md:74-78`): rename `data-tour="list-table"`; delete the `requires` filter; delete the `inert` toggle. Each fails exactly its own case.
- `docker compose up -d --build`, sign in at http://localhost:4200. Expect: the hint once; Guide starts the list tour; Escape returns focus to the toggle; a reload does not restart it; opening a saga starts the detail tour, whose step 4 switches to Timeline; the spotlight follows scrolling and resizing; keyboard focus never reaches the page behind; no fade under emulated reduced motion; a Viewer gets no retry step; `/admin/users` runs the admin tour.
- Docs: `grep -rn "Data tab\|DASHBOARD_API_KEY\|deliberately not part of" README.md CONTRIBUTING.md dashboard-web/README.md docs/*.md docs/transports` returns nothing; the four headings of section 6 are still present; every relative link and anchor resolves; `git diff --name-status <base>..HEAD -- docs/history` shows only `A`.

## Cross-assignment contracts

1. **Shell owner.** Render `<app-guide-toggle />` in `.topbar-actions` between the administration link and the user menu, only when signed in. Keep the `@defer` overlay as the last child of the outermost template, a sibling of whatever wraps the page content.
2. **Authentication.** Provide `GUIDE_PERMISSION_CHECK` in `app.config.ts` from the session's `access` (unscoped permissions plus the scoped entry for the given saga type). Unprovided, it allows everything, so guide commits build before or after authentication.
3. **Anchors.** Added in my commits; owners keep the elements.
   - `topbar-guide`: toggle root.
   - `list-filters`, `list-table`, `list-sort`, `list-row`, `list-pagination`: `saga-list.html` 6, 56, 63 (Status heading), 79 (every row), 91.
   - `detail-summary`, `detail-retry`, `detail-tab-map`, `detail-tab-timeline`: `saga-detail.html` 14, 86, 104, 105. `detail-data`: the group holding Data at start, Data at end and Compare.
   - `map-canvas`, `map-controls`: `saga-map.html` 2, 50.
   - `timeline`, `timeline-entry`, `timeline-step-data`: timeline root, every clickable entry, every step's data toggle.
   - `admin-nav`, `admin-nav-users`, `admin-nav-teams`, `admin-nav-roles`, `admin-list`: administration navigation, its three links, each page's main table.
4. **Copy depends on**: tabs labelled Map and Timeline; "Recorded at" with UTC and offset on hover; selecting an entry opens the map on it; controls labelled "Data at start", "Data at end", "Compare"; `ManualRetryRequested` entries show who asked; retry hidden without `sagas.retry`, data controls hidden without `sagas.data`; permission labels "View sagas", "View saga data", "Retry sagas", "Manage access".
5. **Packaging.** Compose publishes 4200, so `ng serve` needs another port; my documents assume `"port": 4201` in `angular.json`'s serve options.
6. **Authentication and packaging.** Browsers scope cookies by host, not port, so the base stack and every overlay share one cookie jar on localhost: the session, antiforgery and `XSRF-TOKEN` cookies of side-by-side stacks overwrite each other. Either cookie names become per stack, or `transports/index.md` says "one stack per hostname or browser profile".
7. **Authentication.** Every 401 and 403 problem body keeps "See docs/dashboard.md#authentication.", pinned by a test (today only `X-Api-Key` is asserted, `SagaEndpointsTests.cs:638-647`). Defaults for the ten new `Dashboard:*` keys come from that blueprint; read them from `IConfiguration` or a plain singleton, because `configuration.md:3-8` states that nothing uses options binding. `VSaga.Dashboard.Api.http` gains a sign-in example.
8. **Engine.** Re-measure `persistence.md:329-345` (Redis) and `:626` (MongoDB) with snapshots on, during live verification.
9. **Ownership.** Mine: everything under `docs/`, the README outside "Run the demo", `dashboard-web/README.md`, `CONTRIBUTING.md`, every `data-tour` attribute, the key `vsaga.guide`, and the guide anchors the SPA links to (`#the-saga-list`, `#the-saga-detail-page`, `#administration`).

## Objections

None. Contracts 5 and 6 need a companion choice, not a change to a fixed decision.

## Risks
- Anchor drift: a later template refactor renames or drops a data-tour element and the tour silently degrades to centred popovers. Mitigation: anchor ids exist only in GUIDE_ANCHORS, each page spec gains an anchor-contract case, guide-tours.spec.ts checks every reference, and the rename mutation check must fail exactly one test.
- Copy drift: the tour and user-guide text describe labels and behaviours that four other assignments are still shaping (tab names, 'Recorded at', the data controls, showing who retried). Mitigation: guide commits land after those slices, contract 4 lists every UI fact the copy relies on, the CONTRIBUTING checklist gains a line, and a tour's version is bumped when its copy changes.
- Browser behaviour that jsdom cannot prove: jsdom 28.1 has no layout, no inert semantics, no scrollIntoView, matchMedia or ResizeObserver (verified in node_modules), so focus return, the inert background, the programmatic tab click and positioning are only partly covered by specs. Mitigation: geometry is pure and unit-tested, optional APIs are guarded, inert is lifted around the reveal click, and live verification requires a keyboard-only pass and a reduced-motion pass in a real browser.
- A modal tour could get in an operator's way during an incident. Mitigation: Guide is off by default and never starts a tour unless it is on, Escape and Skip always end the tour and are remembered, and the first-run hint is non-modal and shown once.
- Cookies are scoped by host, not port, so the base stack on localhost:4200 and overlays on 4300 to 4800 share one cookie jar; signing in to one stack can sign the user out of another and break its XSRF token. Mitigation: per-stack cookie names in the authentication assignment, otherwise a documented 'one stack per hostname or browser profile' caveat in docs/transports/index.md and the guide's Troubleshooting section.
- Port clash: compose publishes the UI on 4200, the default ng serve port, so the documented dev flow (compose API plus ng serve) fails to bind. Mitigation: the packaging assignment sets the dev server port to 4201 in angular.json; dashboard-web/README.md and CONTRIBUTING.md document that URL.
- Published storage figures go stale: docs/persistence.md states about 5 to 10 KB per saga and 100,000 to 180,000 sagas per GB for Redis (lines 329-345) and a MongoDB figure (line 626), both measured before StatePersisted adds a state copy per committed transition; ADR 0002 line 241 repeats the Redis figure. Mitigation: re-measure under both overlays during live verification, update persistence.md, add a dated note to ADR 0002, and name the growth as a negative consequence in ADR 0007.
- Broken link targets: the API's 401 body and seven documents point at four docs/dashboard.md headings, and the SPA points at three dashboard-guide.md headings on GitHub's main branch (a fork or an offline install gets upstream content or nothing). Mitigation: the headings are kept verbatim and checked in verification, a test pins the 401 string, and the guide URL is one constant.
- Record integrity: writing a history file before a real run, or editing an existing one, would break the repository's provenance convention. Mitigation: history files are created only in commits that follow live verification and are filled from observed output; verification checks that docs/history shows additions only; older design documents and ADRs receive dated bracketed notes, never rewrites.
- ADRs 0006 and 0007 would claim 'Accepted' for decisions the maintainer has not yet approved as a whole. Mitigation: they are written 'Accepted' only after the user approves this plan, and 'Proposed' otherwise.
- Bundle and style budgets: guide code and copy add to an initial bundle with about 96 kB of headroom shared with sign-in, the timeline and data views. Mitigation: the overlay, geometry and all step copy load through @defer, each of the two new stylesheets is about 1 kB, and verification requires a build with no budget warning. If @defer proves awkward in specs, loading eagerly still fits.
- Browser storage unavailable or corrupt (private mode, blocked storage, hand-edited value). Mitigation: storage is injected through a token, reads and writes are wrapped in try/catch with an in-memory fallback, and the stored object carries a schema version that falls back to defaults when unknown.

## Open questions
- Do you want screenshots in the user guide? The plan ships text and tables only, because docs/ has no images or image convention today and every screen changes during this work. Adding them means committing PNG files under docs/images/dashboard-guide/ and refreshing them whenever the UI changes.
- The in-app 'User guide' link opens github.com/RafaelJCamara/vSaga on the main branch. Is that acceptable wherever the dashboard will run, or should the guide be served by the dashboard itself for networks without GitHub access (which needs a Markdown renderer or a pre-rendered page in the image)?
