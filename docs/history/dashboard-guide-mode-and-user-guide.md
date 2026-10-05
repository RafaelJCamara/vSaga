# History: guide mode and the dashboard user guide

> Written fresh. Describes the guidance slice of the dashboard usability and access work: commits C53 to C56 of the
> commit sequence in §12 of
> [`../design/dashboard-usability-and-access.md`](../design/dashboard-usability-and-access.md), with the review and
> live-check follow-ups that landed beside them. That is nine commits on 2026-10-03 and 2026-10-05 (`de8744f` to
> `425910f`), following [§9 of the design](../design/dashboard-usability-and-access.md#9-guidance-and-the-user-guide).
> See [`../dashboard-guide.md`](../dashboard-guide.md) (the guide itself), [`../dashboard.md`](../dashboard.md#the-spa)
> (its "Guide mode" paragraph) and [`../../dashboard-web/README.md`](../../dashboard-web/README.md#guide-mode) (the
> code) for the current reference documentation. Every observed value below comes from the browser runs and mutation
> checks recorded while the slice was built, or from the commit messages themselves. Where a claim rests on neither,
> it is said so.

---

## What was built

Before this slice the dashboard had no guide mode and there was no user guide: `docs/dashboard.md` is the reference
for the API, the access model and how the UI is served. After it, a **Guide** switch in the top bar (off until someone
turns it on) makes each area that a person opens (the saga list, the parts of a saga's page and the administration
pages; sign-in, setup and Account have no tour) explain itself in a short modal tour; the tours are a lazy chunk that
loads only when Guide is first switched on; and `docs/dashboard-guide.md` is the long form of the same
pages, under a written rule that the guide and the tours change in the same commit as the UI they describe. No npm
dependency was added (`package.json`, `package-lock.json`, `angular.json`, the nginx configuration and the Dockerfile
are untouched by the nine commits: `git diff --stat de8744f^ HEAD` over them, run for this record, prints nothing).

The nine commits are the four numbered ones (C53 to C56) and five follow-ups: four that fixed what a review of one of
them found (`0419f6f` for C53, `2c9ad9e` for C54, `adcf234` for C55, `02451d5` for C56) and `425910f`, which fixed a
finding of the live check on the real compose stack and a false sentence that the review of the user guide found in
C53's tour. The paragraphs below describe the code as it stood at `425910f` and follow the numbering where a piece
arrived in one commit.

**The mechanism (C53, `de8744f`).**

- **State** (`services/guide.service.ts`, eager). Signals for whether Guide is on, the current page, the area Replay
  repeats, the tour being asked for, the tour running, `canReplay` and `showHint`, and the methods `started`, `ended`,
  `abandoned`, `areaShown`, `replay`, `dismissHint`, `setEnabled` and `toggle`. The state is one `localStorage` key,
  `vsaga.guide`, shaped `{"v":1,"enabled":true,"seen":{"list":1},"hintDismissed":true}`. Corrupt JSON, another `v` or
  a storage that throws read as the defaults (a malformed member of a v1 object falls back on its own default);
  without storage, or when it refuses a write, the state stays in memory. `seen[area]` holds the version of the tour
  the person went through, so bumping an area's version shows the changed tour once more. The key is per origin, so per
  compose stack and not per user, and nothing about the session is stored in it. Two injection tokens,
  `GUIDE_STORAGE` and `GUIDE_PERMISSION_CHECK`; `app.config.ts` provides the second from `AuthService`. Asked without a
  saga type it asks "for any type" (a user scoped to one saga type still gets the list tour); with one it asks for
  that type; `access.manage` is always asked as held for every type, because the API never scopes it.
- **The area table** (`services/guide-areas.ts`, eager). The seven areas of design §9.1 as one table: id, version,
  trigger, ready anchor, required permission and `docsAnchor`. It is typed against the anchor vocabulary through a
  type-only import that is erased, so the vocabulary and the copy stay in the lazy chunk. `USER_GUIDE_URL` is the one
  constant that the toggle's link and every tour's link are built from. As shipped at `425910f`:

  | Area | Starts when | Needs | Steps | `docsAnchor` |
  | --- | --- | --- | --- | --- |
  | `list` | the list route opens | `sagas.view` | 7 | `#the-saga-list` |
  | `summary` | the detail page opens (from the route, and announced by the page once the saga shows) | `sagas.view` | 2 | `#the-saga-detail-page` |
  | `map` | the Map tab is shown | `sagas.view` | 2 | `#map` |
  | `timeline` | the Timeline tab is shown | `sagas.view` | 3 (2 without `sagas.data`) | `#timeline` |
  | `data` | a step's inspector or the Saga data bar is opened | `sagas.data` | 2 | `#saga-data` |
  | `retry` | the retry row is shown on a `Failed` or `TimedOut` saga | `sagas.retry` | 2 | `#retrying-a-saga` |
  | `admin` | the administration pages are shown | `access.manage` | 8 (7 where the page has no table) | `#administration` |

  Every area is at version 1. The step counts are the number of entries in `GUIDE_TOURS` (26 steps in all).
- **The toggle** (`components/guide-toggle`, eager), in `.topbar-end` before the user menu and only while signed in: a
  `<button aria-pressed>` "Guide", "Replay tour" while there is a tour to repeat, a "User guide" link (a new tab,
  `noopener noreferrer`) and a one-time hint, a non-modal callout inside an always-present `role="status"` region
  that does not take the keyboard focus. The host carries `data-tour="topbar-guide"`. `.topbar-end` now wraps and its
  labels do not, so the bar holds at 375 px.
- **The overlay** (`components/guide-overlay`, lazy). Loaded with `@defer (when guide.enabled())` as the last child of
  the app template, with the pure placement code in `guide-geometry.ts` (`spotlightBox`, `placePopover`) and the tour
  copy and the anchor vocabulary in `guide-tours.ts`. A request waits for the area's ready anchor (it looks at once,
  then every 250 ms, 20 looks); when the tour begins it drops the steps whose permission fails or whose anchor,
  reveal control and fallback are all missing, so "Step n of m" is true; a centred popover is used only for an
  anchor that disappears while the tour runs. A `requestAnimationFrame` loop writes the spotlight and popover
  positions straight to the elements, and only when a rounded value changed. While a tour runs it is modal: the
  siblings of the overlay are `inert`, a full-screen layer cancels `mousedown`, Escape ends the tour, the arrows
  move, Tab wraps, the primary button has the focus on every step, and focus returns to where it was (else the Guide
  switch). Escape, Skip and Done remember the tour; Guide switched off, another page, a session that loses the
  permission and destroying the overlay end it without remembering. There is no animation under
  `prefers-reduced-motion`. A step's `reveal` control (the blueprint's click on a tab) is implemented and specified;
  no tour uses one.
- **The anchors.** `GUIDE_ANCHORS` in `guide-tours.ts` is the one vocabulary of `data-tour` names, 21 of them, all of
  design §9.1 from the first commit: `topbar-guide`; `list-filters`, `list-table`, `list-sort`, `list-row`,
  `list-pagination`; `detail-summary`, `detail-data`, `detail-retry`, `detail-tab-map`, `detail-tab-timeline`,
  `map-canvas`, `map-controls`, `timeline`, `timeline-entry`, `timeline-step-data`; `admin-nav`, `admin-nav-users`,
  `admin-nav-teams`, `admin-nav-roles`, `admin-list`. Only guidance commits add `data-tour` attributes: a search of the
  repository's history for the string under `dashboard-web` (`git log -S`, run for this record) lists `de8744f`,
  `94d67b6` and `e4e62d6` (the templates), `adcf234` (a new spec) and `02451d5` (README text), and no other commit.
  Each page's spec has an anchor-contract case, and `guide-tours.spec.ts` checks that every anchor, fallback and reveal
  named by an area or a step is in the vocabulary.
- **The list page** (`saga-list`). The anchors `list-filters`, `list-table`, `list-sort` (the Status heading),
  `list-row` (every row) and `list-pagination`. The Status and Updated sort headings became `<button>`s inside the
  `th` (`aria-sort` stays on the `th`, which keeps the click handler: the button's click, Enter and Space included,
  bubbles to it). Rows open through a link in the first cell (the review-consistency finding's second option): one tab
  stop per row, Enter opens it, a modified click is left to the browser, and a click anywhere else in the row still
  opens the saga through a handler that ignores clicks from the link.

**The detail page's five areas (C54, `94d67b6`).** Anchors `detail-summary` (the summary card), `detail-retry` (the
retry row, not the "you do not have permission to retry" row, which has nothing to explain), `detail-tab-map` and
`detail-tab-timeline` in `saga-detail.html`; `detail-data` on the Saga data group, which is
`saga-data-overview.html`'s own markup; `map-canvas` and `map-controls` in `saga-map.html`; `timeline`,
`timeline-entry` (every entry row that opens the map as of that entry) and `timeline-step-data` (every step's Data
button, so none without `sagas.data`) in `saga-timeline.html`. The page announces each part with
`guide.areaShown()` from effects, never from the constructor, computed from what the template shows, so a part is
announced when it appears and not at every live refresh. A step's permission is checked for the page's saga type, so a
Viewer without `sagas.retry` never gets the retry area and someone without `sagas.data` never gets the data area or
the timeline's Data step. The copy was written against the shipped labels and against §7 of the design, not against
the blueprint's step 8, which described the old retry. The same commit made the Retry button `aria-disabled` with a
guard in the page instead of the native `disabled` (the keyboard focus that the removed "Yes, retry" button had held
dropped to the page for the length of the request, and a refused button could not be reached for its reason), the
open item at the end of the sign-in record; and it changed two rules of the service: Escape and Skip no longer start
the next queued area, and switching Guide on from the detail page starts the page's own area (the summary) and then
the part shown last.

**The administration area (C55, `e4e62d6`).** Anchors `admin-nav` on the shell's `nav.subtabs`, `admin-nav-users`,
`admin-nav-teams` and `admin-nav-roles` on its three links, and `admin-list` on the `table.data-table` of the users,
teams and roles lists. An eight-step tour (every step requires `access.manage`): the tabs, Users, Teams, Roles and
permissions, the table ("Open a row to edit it"), and three centred steps about pages the tour does not visit, "Grants
and scope", "Effective access" and "You cannot lock everyone out". The users and teams tables are not rendered with
no rows, so the table step is left out there and the count reads "Step 1 of 7". The area's trigger moved from the
route to "shown", announced by the shell (see the decisions below). The copy is written against the shipped pages,
not the blueprint's five steps, which said "Create accounts" and "Manage access counts only when granted for all saga
types" and nothing of the grants editor.

**The user guide (C56, `f154593`).** `docs/dashboard-guide.md` (866 lines when added, text and tables, no
screenshots) has the H2 sections Opening the dashboard, Signing in, Guide mode, The saga list, The saga detail page,
Administration, Your account and Troubleshooting, with the saga detail page's Summary, Map, Timeline, Saga data and
Retrying a saga under it. It covers every label, status, banner and button text read from `dashboard-web/src/app/**`
and every rule read from `dotnet/src/VSaga.Dashboard.Api` and `VSaga.Dashboard.Identity` (the commit's own words),
among them the retry outcomes (202, 409, 422, 502, 403) in user terms, what a retry does not undo, attribution,
grants with a worked scope example, the lockout escape, two stacks in one browser, the engine version a targeted retry
needs and that a business failure decided by the message alone fails again after a retry. A header note states the
rule. `CONTRIBUTING.md` gained the rule as a paragraph beside the live-verification text and one line in the
pull-request checklist (it names the version bump and the `docsAnchor` coupling); `README.md` links the guide from
"Run the demo" and its documentation list; `docs/README.md` lists it under the core reference. The tours link into the
guide by heading (`docsAnchor`), so the rule also says that renaming a heading the tours use changes
`guide-areas.ts` in the same change.

**The follow-ups.**

- `0419f6f` (the review of C53): a running tour is not queued behind itself; the hover colour of the dialog's primary
  button; the hint's answer keeps the keyboard focus; a popover taller than the screen scrolls its text and keeps its
  buttons in view; the focus return respects the next page; "Step n of m" sits inside the live region; four smaller
  items (specs for the order of the focus return and for the key listener's removal, one for the storage factory's
  `catch`, a misplaced comment and the doc of `areaShown()`).
- `2c9ad9e` (the review of C54): the data area announces only a view that is on screen (two pure functions in
  `saga-data-overview.ts`, `canCompareData` and `visibleDataView`, shared by the bar and the page); the retry answer
  is an always-present `role="status"` in the retry row; the `data-views`, `retry-what`, `retry-effects` and
  `summary-tabs` copy says what the page does; the guide service's two doc comments; a router-based spec
  (`saga-detail-guide.spec.ts`) with the real `GuideService`, `SagaDetail` and router; the three changed tests that
  C54's message had not named.
- `adcf234` (the review of C55): a `shown` trigger may carry `within`, the paths its pages live on, so Replay and the
  hint stay put between two administration pages; `admin-guide.spec.ts` mounts the real `App` and holds "Step 1 of 8";
  the tour's copy is pinned to the copy of the pages it quotes (`testing/admin-tour.ts` and cases in the grants editor,
  role, user and team specs); four corrections to `e4e62d6`'s message and one to a comment.
- `02451d5` (the review of C56): `docs/dashboard.md` and `dashboard-web/README.md` say that guide mode and the guide
  exist (the blueprint had asked for both and `f154593` had left them out of scope), and the guide's wrong statements
  are corrected (below).
- `425910f` (the live check): the overlay's scroll test is two-dimensional (`isInView` in `guide-geometry.ts`), an
  element that is wholly off screen and has not moved for 12 frames (about 0.2 s) is given up for the step's fallback
  anchor, and two sentences of copy no longer rely on a spotlight; the list tour's first step no longer says that
  Guide brings the tour back.

**The commits, in order** (the author dates are all in UTC; sizes are `git show --shortstat`):

| Commit | Date | Subject | Size |
| --- | --- | --- | --- |
| `de8744f` C53 | 2026-10-03 | Add guide mode to the dashboard: a top-bar toggle and the saga list area | 27 files, +4954 -27 |
| `0419f6f` | 2026-10-05 | Follow-up to de8744f: a running tour is not queued behind itself, the hint keeps the keyboard focus, a tall popover scrolls, and the focus return respects the next page | 11 files, +242 -21 |
| `94d67b6` C54 | 2026-10-05 | Explain the saga detail page in guide mode: summary, map, timeline, data and retry | 16 files, +1124 -46 |
| `e4e62d6` C55 | 2026-10-05 | Explain the administration area in guide mode | 15 files, +994 -31 |
| `2c9ad9e` | 2026-10-05 | Follow-up to 94d67b6: the data area announces only a view that is on screen, the retry answer is heard, the retry, data and summary copy say what the page does, and a router-based spec holds the announcements | 8 files, +307 -19 |
| `f154593` C56 | 2026-10-05 | Add the dashboard user guide and link it from the app, the README and the docs index | 4 files, +888 -1 |
| `adcf234` | 2026-10-05 | Follow-up to e4e62d6: Replay and the hint stay put between two administration pages, the tour's copy is pinned to the pages it quotes, a real-app spec holds "Step 1 of 8", and the corrections to e4e62d6's message | 12 files, +382 -8 |
| `02451d5` | 2026-10-05 | Follow-up to f154593: say in dashboard.md and the SPA README that guide mode and the guide exist, and correct ten statements in the guide | 4 files, +92 -26 |
| `425910f` | 2026-10-05 | Follow-up to de8744f: the overlay scrolls an element that is off screen sideways and gives up one that cannot be scrolled to, and the list tour says how to get it back | 6 files, +286 -22 |

The table is in branch order: the C54 follow-up `2c9ad9e` landed after C55, and the C56 commit before the C55
follow-up. One more commit lies in the same range and is not part of the slice: `71965b9` ("Say which authentication
problems point at the documentation", 2026-10-03, between `de8744f` and `0419f6f`) changed an XML comment in
`AuthProblems.cs` and nothing else (its message: comment only, whole-solution build 0 warnings and 0 errors). It
corrects the overstatement that the sign-in record lists as still open under "An overstating XML comment" in
[`dashboard-sign-in-and-access.md`](dashboard-sign-in-and-access.md#unverified-and-open); that record is not edited.

### Why it is built this way

- **A hand-rolled tour, no dependency.** The working plan's decision table says "Hand-rolled tour (no npm
  dependency)"; the table records no further reason, and none is invented here. The cost shows in the budget below.
- **A lazy overlay.** The Initial total stood at 436.65 kB against the 500 kB warning budget when the slice began
  (63.35 kB of headroom, by subtraction), and the plan said to keep the overlay, its geometry and its copy in a lazy
  chunk, to keep eager additions minimal and not to raise the budget. `de8744f` grew the Initial total by 15.98 kB
  (436.65 to 452.63 kB), of which about 6.5 kB is Angular's `@defer` runtime (its message: "which the plan's @defer
  asks for"); the overlay, its geometry, its stylesheet and all the copy (12.87 kB, 4.43 kB transferred) are fetched
  only when Guide is first switched on, which was checked in Chromium (no overlay chunk before, one after). At
  `425910f` the overlay chunk is 20.55 kB and the Initial total 453.55 kB.
- **Per-origin storage, nothing server-side.** The code's comment: stored per origin, so per compose stack and not per
  user, so "a person who signs in as someone else in the same browser keeps their Guide setting, and nothing about the
  session is kept here". `docs/dashboard.md` (added by `02451d5`) says that nothing about guide mode reaches the API;
  `02451d5`'s message says the guide code makes no HTTP call.
- **`seen` holds a version.** So that a tour whose copy was rewritten shows once more to everyone who saw the old one.
  The rule is in the comment at the top of `guide-tours.ts`, in the guide's header note and in `CONTRIBUTING.md`.
- **A queue.** The detail page can announce a summary, a tab and a retry row together; an area announced while a tour
  runs or is asked for waits its turn. The queue is dropped on a page change and when Guide goes off. `94d67b6` made
  Escape and Skip drop it too: with a Failed saga's page queuing the summary, the retry row and the map, a person who
  leaves the first tour would otherwise be given the second at once.
- **Switching Guide on explains everything again.** `setEnabled(true)` clears `seen`: de8744f's message cites the
  blueprint's first section ("switching Guide on is a fresh walkthrough, and it always starts the current area") and
  the service's comment says that a person who switches Guide on wants the walkthrough, whatever an earlier session of
  it covered. So an area explains itself once per switch-on (and per tour version), not once for good; a reload with
  Guide already on repeats nothing. `f154593`'s message records that design §9.1 and its brief read as "once per
  version for good". The design text has not been changed to match (a deviation, below).
- **The administration area starts when its pages show, not on the route.** The plan's table gave it a route trigger.
  With that trigger the tour began on `NavigationEnd`, while the shell still says "Loading..." and the table is not
  there yet, so it always left out the step about the table: checked in Chromium with the route trigger put back,
  "Step 1 of 7" at every read delay from 0 to 1.5 s against "Step 1 of 8" with the shell announcing
  (`evidence/C55/route-trigger-race.txt` repeats the route-trigger half at 0, 40, 150, 400 and 1500 ms: "Step 1 of 7"
  each time). The shell now announces `admin` from an effect once its store has read everything, and again after each
  navigation that ends inside the area, reading the guide untracked (a tracked effect would ask again for a tour the
  overlay had given up on). `e4e62d6` says that the tour still starts when the manager arrives, once the page is there,
  and that the owner of the docs may want design §9.1's sentence ("the list and administration pages on navigation")
  updated.
- **`within`.** The shell announces after every navigation, and the pages are several routes, so a navigation between
  two of them forgot the area until the shell announced it again, which removed and re-created Replay and the hint.
  Letting a `shown` trigger carry the paths its pages live on, and the service keep the area across a navigation that
  stays within them, was "the smallest that keeps one rule" (`adcf234`); the shell still announces after each
  navigation, because that is what asks for the tour of a session that has not seen it.
- **Copy that the specs hold to the pages.** The comment at the top of `guide-tours.ts` says that a stale sentence is
  noticed by nobody. Two follow-ups put specs on the sentences the copy quotes: `2c9ad9e` (the retry, data and summary
  copy against section 7 of the design and the shipped labels) and `adcf234` (every phrase of the administration tour
  that quotes a page, read from the tour by `testing/admin-tour.ts` so that a word changed on either side fails).
- **A guide written from the code.** Not from the blueprint or the design: where they differ the commit follows the
  code and says so (see the deviations).

## How it was verified

### The environment

Everything ran in the same Linux cloud container as the later work of the sign-in slice, with the constraints that
[`dashboard-sign-in-and-access.md`](dashboard-sign-in-and-access.md#the-environment) describes: no `dotnet` on the host,
Docker Hub answering 429 (so the base images were pulled from a mirror and retagged, and the SDK, ASP.NET and Node
images rebuilt with the egress proxy's CA certificate). The evidence files named below
(`evidence/...`) were kept outside the repository, in the working session's scratch folder; they are not in it, and
the messages and this record are what the repository holds. What matters for this slice:

- **Node.** The host's Node was 22.22.0, which Angular CLI 22 refuses (it needs 22.22.3 or later), so every SPA command
  ran as `npx -y -p node@22 -- <command>` from `dashboard-web/` (22.23.x), as in the sign-in record.
- **Isolated copies.** The messages say that the SPA gates and mutation passes ran in an isolated copy of `HEAD` plus
  the commit's files, three consecutive green `ng test` runs each; the live-check file records that the shared working
  tree held other agents' uncommitted edits while it ran.
- **.NET.** The nine commits changed no .NET code. The bodies of the SPA commits report the SPA gates (`npm audit`,
  `ng build`, `ng test`) and no .NET run; `f154593`'s says that no .NET or SPA gate applies to a documentation-only
  change, after a search of `dotnet/tests` and the SPA specs for files that read docs or the README (none read a file
  it edits). The solution was built and tested once at the end of the slice (below).
- **Browsers.** Playwright's Node library with headless Chromium (the live-check file says "Playwright 1.x"; 1.56.1
  is what the container has installed, checked for this record), fresh browser contexts per script. The mock-API runs
  used a small server that answers the API; the C55 one (`evidence/C55/mock55.cjs`) is a node script that selects the
  signed-in user with a `persona` cookie. The real-stack run listened for `securitypolicyviolation` on every context.
- **RabbitMQ, first start.** In the live-check container `docker compose up -d --build --wait` exited 1 because
  RabbitMQ exited on its first start with `Error when reading /var/lib/rabbitmq/.erlang.cookie: eacces`; a second
  `docker compose up -d --wait` without a rebuild brought every service healthy. Not investigated.
- **Leftovers.** The live check ran as compose project `wt55` from a clean detached worktree, and `docker compose
  down` (no `-v`) left the named volumes `wt55_vsaga-postgres-data` and `wt55_vsaga-dashboard-identity` and the
  `wt55-*` images in the container; the worktree was removed.
- **Pushed.** All nine commits were on the remote-tracking branch when this record was written
  (`origin/dashboard-usability-and-access` pointed at `425910f`), so every correction below is a follow-up commit and
  none is an amended commit.

### The bundle and the spec count, commit by commit

Each row is the figure the commit's message reports (`ng build` Initial total against the 500 kB warning budget, no
warning and `npm audit` 0 vulnerabilities in every message; `ng test` specs, three consecutive green runs).
`f154593` and `02451d5` changed documentation only and report no gate.

| Commit | Initial total | Specs (files) | Lazy chunks |
| --- | --- | --- | --- |
| before the slice (`71430ac`, `de8744f`'s parent) | 436.65 kB | 1862 (51) | `saga-detail` 76.56 kB (the sign-in record's figure, at `fdc1685`) |
| `de8744f` C53 | 452.63 kB | 2137 (56) | `guide-overlay` 12.87 kB (4.43 kB transferred), a new 0.85 kB chunk |
| `0419f6f` | 452.93 kB | 2150 | `guide-overlay` 13.26 kB (4.53 kB), `saga-detail` 76.61 kB |
| `94d67b6` C54 | 453.11 kB | 2225 (56) | `guide-overlay` 16.97 kB (5.69 kB), `saga-detail` 77.78 kB (19.32 kB) |
| `e4e62d6` C55 | 453.13 kB | 2288 (56) | `guide-overlay` 19.91 kB (6.54 kB), `admin-routes` 10.10 kB (9.81 kB before) |
| `2c9ad9e` | 453.13 kB | 2304 (57) | `guide-overlay` 20.01 kB (6.58 kB), `saga-detail` 77.84 kB (19.37 kB) |
| `adcf234` | 453.55 kB (the `within` field and method are in the eager service) | 2320 (58) | `guide-overlay` 20.01 kB, `admin-routes` 10.10 kB |
| `425910f` | 453.55 kB | 2344 (58) | `guide-overlay` 20.55 kB (6.75 kB) |

By subtraction from those figures: the specs grew by 482 (2344 less 1862) and the Initial total by 16.90 kB, leaving
46.45 kB under the budget. `de8744f` states the main bundle at 142.08 kB (132.31 before), the shared chunk at 304.25 kB
(298.89) and styles unchanged at 5.46 kB; its two new component stylesheets are 2.1 and 1.0 kB of source (budget 4 kB).
The first row's 436.65 kB and 1862 specs are `de8744f`'s own "436.65 kB at HEAD" and "1862 -> 2137, 51 -> 56 files".

### Browser runs against a mock API (C53 to C55 and the fixes)

The commits' own checks drove the production build in headless Chromium against a mock API. These are the checks the
messages describe; the saved outputs are in `evidence/C53/live-output.txt`, `C54/live-output.txt` and
`C55/chromium-run-3.txt`.

- **C53, 32 checks** (all pass in the saved output). The hint shows on the first visit and does not take the focus; no
  overlay chunk is fetched while Guide is off and one (`chunk-BO1fP_Kq.js`) after the switch; Enter on the switch starts
  the list tour on its welcome step with the primary button focused and the shell inert, storing
  `{"v":1,"enabled":true,"seen":{},"hintDismissed":true}`; 24 Tab and Shift+Tab presses never leave the dialog; a mouse
  click on the dimmed page opens nothing; seven steps in order; Escape returns the focus to the Guide switch and
  stores `seen.list = 1`; a reload does not restart the tour and Replay is offered; no fade under emulated reduced
  motion; the spotlight is the table grown by 6 px, follows a resize and a scrolled pager; the user menu still works by
  keyboard afterwards; a viewer and a user scoped to one saga type get the list tour; no console errors beyond the
  mock's 404s. The same run compared computed styles of the page's buttons, headings and cells before and after (every
  difference `{}`), and the sort buttons: 14 Tabs from the page start reach the Status heading button, Enter sorts
  ascending, Space descending (the requests `Status:false` and `Status:true`), the focus stays on the button; the
  row link's and the heading button's focus rings sit inside the table (15 px from its left edge, 16 px from its
  bottom); Enter on the third row's link navigates once and a click on a kind cell opens the saga.
- **C54, 46 checks, three consecutive runs** (the message says 46; the saved output, of one run, lists 45 `PASS` lines
  and `ALL PASS`). As an administrator on a Failed saga with Guide on, the summary starts with
  the page and Done starts the retry tour and then the map; each area's first step has its spotlight on the right
  element, within a pixel or two of the element grown by 6 px; keyboard only through every area with the shell inert
  and the focus never leaving the dialog; the Timeline tab explains the timeline in three steps, going back to Map
  and to Timeline explains nothing; opening a step's Data explains the data; Escape returns the focus to the Data
  button that was pressed; every area is remembered; Escape on the summary starts nothing else and drops the queue; a
  Viewer gets the summary, the map and a two-step timeline, never the retry or the data; a Running saga gets the
  summary then the map; the retry button keeps the focus through the request. No console errors beyond the mock's
  missing hub.
- **C55, 123 checks, three consecutive runs** (the saved output is the third run, 123 `PASS` lines and `ALL PASS`).
  A fresh load of the users, teams and roles pages with Guide on starts the tour once the pages show, "Step 1 of 8",
  each tab and the table with its spotlight, the three last steps centred, Done remembered, a reload not restarting
  it; with the read slowed by 1.5 s no tour during "Loading..." and eight steps after; with no teams seven steps and
  the table step skipped; the hint shows once the pages have loaded, Guide from the switch starts the tour, Escape
  returns to the switch, Replay and Escape again; following a link puts the focus on the page heading and the tour
  then starts from the switch; a delete returns to the list with the heading focused and nothing inert; moving
  between the tabs starts nothing; a viewer is sent to the saga list and gets no administration tour; at 360 px the
  popover fits and the spotlight is on the table.
- **The fixes.** `0419f6f` was checked in Chromium at 320 x 256 (the popover 12 px from the top, its bottom at 244 of
  256, the text scrolling, the focused Next button inside the popover) and at 568 x 320; the dark label of the hovered
  primary button computes `rgb(11, 14, 20)` at rest and hovered, 6.11:1, where the pushed build computed
  `rgb(230, 233, 240)` hovered (2.60:1 on the accent). `2c9ad9e` was checked against the build before: no geometry of
  the row, the button, the card or the bar below changes (the empty span has no width), the region is `status` in the
  accessibility tree, and after Enter on Retry and "Yes, retry" the text "Retry accepted — redriving the failed step."
  appears in the region with the focus still on "Retry this saga". `425910f` is in the F1 section below.

The mock runs reached what the specs cannot (layout, focus, real `inert`, the production build's lazy chunk). They did
not run against the compose stack, so not under its Content Security Policy and not on the real roles or the sample's
sagas, and `de8744f`, `94d67b6` and `e4e62d6` each say so (`de8744f`: "Not verified here: the tour over the compose
stack and under its CSP (the live check of C55), and with a screen reader"). One difference between the two kinds of
run shows what a mock's users are: the mock's viewer got a two-step timeline tour (no Data buttons), while the real
stack's `viewer1`, on the built-in Viewer role, got three steps, because that role holds `sagas.data` (the live-check
file says so).

### Live: the real compose stack, `e4e62d6`

The plan's live check for guide mode was written into C55 ("Live check (container and Playwright, evidence `C55.md`)").
`e4e62d6` was committed after the mock-API runs above; the real-stack run began afterwards (`docker compose up` at
09:19 on the container clock, `e4e62d6` having been committed at 09:17) and ran while `2c9ad9e` and `f154593` were
being committed. It ran against `e4e62d6` in a clean detached worktree as compose project `wt55` (fresh volumes), at
`http://localhost:4200`, as `admin` / `dev-local-only-change-me`. The evidence file is `evidence/C55-live.md`, with the
raw result of every script and the screenshots beside it. It is evidence for `e4e62d6` only: its own text records that
the repository's HEAD had meanwhile moved on, and no evidence file records a run of a later commit against the real
stack.

- **Setup.** `docker ps` was empty and ports 5080, 4200, 5433, 5672 and 15672 were free: nothing was stopped. The build
  from the worktree succeeded and the first start failed on RabbitMQ (above). The served bundle was confirmed to be this
  commit (the lazy chunk `chunk-CWkPjZkX.js` contains "lock everyone out"). The first Failed `OrderSaga` was created
  at 09:22:00.8, 10 to 15 s after the stack was healthy, and the sample then makes one `OrderSaga` every 8 s (by 09:25
  there were 12 Failed or TimedOut ones); the sign-in record's S4 observed one to two minutes after a fresh start, and
  why this run differed was not established (its volumes were fresh too). Users made through `POST /api/admin/users`:
  `viewer1` (built-in Viewer, scoped to `OrderSaga`, session access `scoped:[{OrderSaga: sagas.view, sagas.data}]`)
  and `oper1` (built-in Operator, scoped to `OrderSaga`); a team `Payments` for the "one team" page. The dashboard has
  a single dark theme (no `prefers-color-scheme` or `data-theme` rule in its CSS).
- **Result.** The file's result table has eight rows (the plan's checks and the extras); all passed except one step of
  the dark-theme and 360 px row. Script totals (assertions): `a-list` 23 pass, `b-detail` 94, `c-keyboard` 50,
  `d-admin` 57, `e-motion` 6, `f-viewer` 37, `g-mobile` 61 pass and 1 fail, `h-console` 5, `i-extra` 5, "0 other
  failures" in the file's words (338 passes by addition; the file gives no overall total).

| Check | Observed |
| --- | --- |
| The hint shows once; Guide on starts the list tour; Escape returns focus; a reload does not restart it | A fresh context signed in as admin: `vsaga.guide` null, exactly one `.guide-hint`, Guide `aria-pressed=false`, no Replay; a reload without answering still showed the one hint. Four Tabs from the page start reached the Guide switch. Enter started the list tour ("Welcome to the saga dashboard", "Step 1 of 7"), the hint went, stored `{"v":1,"enabled":true,"seen":{},"hintDismissed":true}`. Escape: focus on the Guide switch, stored `seen:{"list":1}`, Replay offered. A reload: no `.guide-popover` after 2.5 s, Guide still pressed, no hint. Guide off and reload: no hint. A second context: "No thanks" removed the hint (`enabled:false,hintDismissed:true`) and it did not return after a reload or a sign-out and sign-in |
| Summary, timeline, map, data, retry | On a Failed `OrderSaga` opened by Enter on its row link: the summary tour (Step 1 of 2, the second step on the Map tab); Escape dropped the queue (nothing else started in 2.2 s); the Timeline tab explained the timeline (Step 1 of 3), the Map tab the map (Step 1 of 2); going back to Timeline, to Map and to Timeline again explained nothing; the first of four Data buttons explained the data (Step 1 of 2) and reopening an inspector did not; Replay tour repeated the part shown last (the data, then the map after the Map tab, then the timeline, 3 steps) and Escape left `seen` unchanged. After eight Back presses to the list (every tab switch adds a history entry, see below) Enter on the row link again explained the retry (Step 1 of 2, the spotlight on the "Retry this saga" row). In a fresh context a list tour to Done, then the row link: summary, retry and map in turn, each Done starting the next, stored `{"list":1,"summary":1,"retry":1,"map":1}`. A Completed saga: summary then map, no retry row |
| The administration area | Each page in a fresh context with the v1 state `{"v":1,"enabled":true,"seen":{"list":1},"hintDismissed":true}` seeded after sign-in, then a full load: `/admin` landed on `/admin/users`; users (3 rows) and roles (3 rows): Step 1 of 8; `/admin/teams` with no team: no `admin-list`, Step 1 of 7 and "Open a row to edit it" left out, and after a team was created Step 1 of 8; a user's edit page: 7 steps. Done remembered (`seen.admin=1`) and a reload did not restart it. After Escape on `/admin/users`, moving to Teams, Roles and Users through the sub-nav started nothing and Replay was offered; Replay ran the 8 steps again. A fresh context straight onto `/admin/users` showed the hint and "Start the tour" ran the 8 steps |
| Keyboard only, one whole area | The administration area, 95 key presses and 97 `document.activeElement` snapshots: every one inside `.guide-popover` (never in `.shell`, never `body`); the only `[inert]` element was `div.shell`, on every snapshot. Tab order in the dialog User guide, Skip tour, Back, Next (Done), wrapping last to first and, with Shift+Tab, first to last; the primary button focused at every step. After Done: no `[inert]` element, focus back on the Administration link that opened the area, `seen.admin=1`. The list area (7 steps, 86 snapshots) gave the same, with the focus back on the Guide switch |
| Reduced motion | `emulateMedia({reducedMotion:'reduce'})`: `.guide-popover` and `.guide-spot` computed `animation-name: none`, `animation-duration: 0s`, `transition-duration: 0s`, `getAnimations().length` 0; a per-frame opacity sampler saw 45 frames from the popover's insertion, the first already at opacity 1 and none between 0 and 1. The control with `no-preference`: animation `guide-fade` 0.14 s, 8 of 44 frames with opacity between 0 and 1 |
| Scoped users | `viewer1`: only OrderSaga rows, only "Sagas" in the top bar, `/admin`, `/admin/users`, `/admin/teams` and `/admin/roles` all ended on `/sagas` with no administration tour; on a Failed `OrderSaga` no `detail-retry`, no "Retry this saga" button, the text "You do not have permission to retry OrderSaga sagas."; Guide on ran the summary then the map; the timeline tour had 3 steps; stored `seen` held no `retry` and no `admin`. `oper1`: no Administration link, `/admin/users` redirected to `/sagas`; on the Failed saga the retry row showed and Guide on ran the summary, the retry (2 steps, spotlight on the row, deltas 0) and the map |
| Console, CSP, network | Every script listened for CSP violations, console errors and warnings, page errors, failed requests and responses of 400 and above. With Guide on and every tour run, `/login` (anonymous), `/sagas`, a filtered and sorted list, a Failed and a Completed saga (map and `?tab=timeline`), `/account`, the users, teams and roles pages and their new and edit pages and `/does-not-exist` (which landed on `/sagas`): 0 CSP violations, 0 console errors or warnings, 0 page errors, 0 failed requests, 0 responses of 400 or more. Across all nine scripts the only entry was the deliberate `GET /api/admin/users` as `viewer1` (403). Served CSP: `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; ...`. The server logs held only first-start lines |
| Dark and 360 px | At 1280 x 860 with `colorScheme: dark`, every area: spotlight within 1 px of the target grown by 6 px for 22 anchored steps. At 360 x 740, dark, device scale factor 2: administration 5 anchored steps and the detail areas 11, max delta 1 px; the list 5 of 6 anchored steps within 1 px; the popover inside the viewport on every step. **One step failed: F1** |
| Shutdown | `docker compose down` without `-v`: containers and the network removed, the ports closed afterwards; volumes and images kept; the worktree removed |

First-step screenshots of every area are in the evidence folder (`10-list-step1.png` to `16-admin-step1.png`).

**F1, the one failure.** At 360 px the saga list table is 768 px wide (the page overflows sideways: `scrollWidth` 768,
`clientWidth` 360), so `[data-tour=list-sort]`, step 4 "Sort by status or last update", sat at x 574 to 680, outside
the viewport. The overlay's `scrollIntoView` decided "on screen" on the vertical axis only, so it never scrolled
sideways; `spotlightBox` returns null for an element wholly off screen; and the step's `fallbackAnchor` (`list-table`)
was taken only when the anchor was missing from the page, not when it was there and could not be brought into view.
The page was dimmed with a centred popover and nothing highlighted: readable, and the tour was not blocked.
`425910f` reproduced it in Chromium against the build of `e4e62d6` (at 360 px and at 320 px: spotlight
`display: none`, `scrollX` 0 on that step) and fixed both causes. The check after the fix is in `evidence/C58/`
(its scripts select the user with a `persona` cookie, which is how the node mock selects one; the message says only
"in Chromium"):
at 360 px the page itself scrolls (`scrollX` 255, the sort heading arrives at the right edge, highlighted within a
pixel of the element grown by 6 px, the popover inside the viewport) and at 320 px likewise (`scrollX` 295), steps 5
to 7 keep their spotlight, and at 1280 px nothing changed (`scrollX` 0); going Back from step 4 to the filters scrolls
the page back and highlights them; on a page made unscrollable (`overflow-x: clip` on the content) the heading cannot
be scrolled to, and after a moment the table, the step's fallback, is highlighted instead. No run of the fix against
the real stack is recorded.

Observations of the live check that were not fixed: at 360 px a smooth scroll to a distant target leaves the dimmed
page with a centred popover and no spotlight while it runs (about 1.3 s for list step 6 after clicking Next; the
spotlight is in place when the scroll ends); `425910f`'s message does not mention it, and its new specs say that, for
an element that is off screen sideways, nothing is highlighted while the scroll is on its way. At 360 px the list rows
move as new sagas arrive every 8 s, so the spotlight keeps following the row element it began with (the first-row
anchor then is the second row), which the file calls harmless. The others (a history entry per tab switch, the
retry-plan read, the tables' overflow and the queue at switch-on) are under "Unverified and open".

### Mutations

Every commit with code carries its own one-token or one-line mutations in an isolated copy, each followed by the
specs that cover the file and a restore. The counts the messages state:

| Commit | Mutations | Outcome |
| --- | --- | --- |
| `de8744f` C53 | 184 in the first pass; 179 in the final pass over the final code | first pass: 164 caught, 20 survived, one malformed; each survivor strengthened a spec or exposed dead code, which was removed. Final pass: 178 caught in the full run and the last (the fallback's scroll) after its spec was corrected; the groups are geometry 9, the service and area table 50, the toggle, app and config wiring 20, the overlay 70, the tours 6 and the list page 24. Swapping `release()` and `restoreFocus()` was left to the Chromium run (the message said it needs real `inert` semantics; `0419f6f` showed that it is testable in jsdom and mutated it) |
| `0419f6f` | 16 on the new specs; the 45 earlier mutants of the focus, inert, end, queue, toggle and app-wiring groups re-run | 15 caught and one survivor, an equivalent mutant (the guard in `advance()` that no sequence can reach once `enqueue()` holds); the 45 all still caught (three patterns replaced) |
| `94d67b6` C54 | 67 on the new code and specs | all caught, each by its own specs. The first pass found ten survivors (a spec for a refused click that the template hid, the guards of a failed or forbidden reload of a saga that was shown, an inspector open on the other tab, a map still loading, the summary's fallback anchor, a copy pin that a title satisfied, Skip with a queue); each got a spec, and two conditions that no state can reach were removed |
| `e4e62d6` C55 | 59 | all caught, each by its own specs; the first pass found one survivor (the nav step's "for all saga types"), which got a spec; a spec of the commit's own left the shell's reads unanswered when it failed and took thirty other cases with it, and now answers them first |
| `2c9ad9e` | 23 | all caught, each by its own specs |
| `adcf234` | 31 | all caught, each by its own specs: the real-app announcements, `within` (removed, wrong paths, never kept, kept for any `shown` area, kept for any path) and, for each phrase the tour quotes, one word changed on the page and one in the tour, each failing the page's spec and the tour's |
| `425910f` | 18 | all caught, each by its own specs; the first pass found two survivors, both assignments that reset the count when the fallback is taken, which no state could tell from their absence, so they were removed, and one more spec (the count at a new step on the same element) went in for the reset that does matter |
| `f154593`, `02451d5` | none | documentation only |

The messages give no total across commits.

**The slice-level pass** (the plan's guidance mutation checks, after C56).

The plan's three guidance mutation checks (design §11, the guidance row) and seven more ran on a clean export of
`425910f` (a detached git worktree; the main working tree was not touched), recorded in `evidence/mutations-guidance.md`
with the diff and log of every run. For each mutation: one exact-string replacement in the worktree's file, `git diff
-U0`, the whole of `npx ng test --watch=false` (so every failure shows), `git checkout -- <file>`, `git status --short`
(empty each time), and the whole suite again. SPA commands ran under Node 22.23.3 after `npm ci`. The baseline before
any mutation: exit 0, 58 test files and 2344 tests passed. Ten mutations ran the suite (an eleventh attempt, 2a, did not
compile); after every restore the suite was rerun and passed, exit 0, 58 files and 2344 tests.

| # | Mutation | File | Failed of 2344 | Verdict |
| --- | --- | --- | --- | --- |
| 1 | `data-tour="list-table"` renamed to `"list-tablex"` | `saga-list.html` | 3, all in `saga-list.spec` "the anchors of the guide tour" | as planned; `guide-tours.spec` does not fail (note 1) |
| 2b | the step-level `requires` filter in `available()` deleted | `guide-overlay.ts` | 2 | the overlay case as planned; the C54 retry case does not fail (note 2) |
| 2c | the area-level `requires` check in `start()` deleted | `guide-overlay.ts` | 1 | extra |
| 2d | the area-level check in the effect that ends a running tour deleted | `guide-overlay.ts` | 5 | extra |
| 2e | `GuideService.allowed(area)` always true | `guide.service.ts` | 10 | extra; this is where the C54 retry case bites |
| 3 | the `inert` toggle (`element.setAttribute('inert', '')`) deleted | `guide-overlay.ts` | 11, not 1 | as planned, more cases (note 3) |
| 4 | `isInView` ignores the horizontal axis | `guide-geometry.ts` | 7: 4 in `guide-geometry.spec`, 3 phone-width cases in `guide-overlay.spec` | extra (`425910f`'s specs bite) |
| 5 | the `within` handling removed from `enterPage` | `guide.service.ts` | 3: 2 in `admin-guide.spec` (the hint stays, Replay stays), 1 in `guide.service.spec` | extra (`adcf234`'s specs bite) |
| 6a | announce from the constructor instead of an effect, guarded by `shown()` | `saga-detail.ts` | 14: both cases of `saga-detail-guide.spec` and 12 in `saga-detail.spec` "what the page announces" | extra (`2c9ad9e`'s router spec bites) |
| 6b | the same, an unconditional call at construction | `saga-detail.ts` | 18: both cases of `saga-detail-guide.spec` and 16 in `saga-detail.spec` | extra |

1. **Mutation 1.** The "anchor-contract case" of the list page is a group of three cases in `saga-list.spec.ts` ("the
   anchors of the guide tour": marks the filters, the table, the Status heading, every row and the pager and nothing
   else; uses only names of the vocabulary; has every element the list tour and the list area point at, apart from the
   top bar toggle). All three read the rendered template's `data-tour` set and failed. The fourth case of the group
   (nothing to point at while there is nothing to list) passes, because it renders no table. `guide-tours.spec.ts`
   reads no template (a `grep` of it for `readFile`, `.html` and `fs` finds no match), so it does not fail, as the
   plan's text allows ("if it reads templates").
2. **Mutation 2.** The plan expected the deletion of the overlay's `requires` filter to fail two things: the overlay
   case that drops unpermitted steps when a tour begins and the C54 case that a Viewer never gets the retry area. It
   failed the first, and a second case that is also about steps ("for a viewer > never gets the data area without
   `sagas.data`, and a timeline tour without its Data step"); it did not fail the retry-area case. The overlay has three
   `requires` sites: the step filter in `available()` (line 192), the area check in `start()` (line 172) and the area
   check in the effect that ends a running tour (line 109). The retry area has no step-level `requires` of its own, and
   the retry-area case ("for a viewer > never gets the retry area without `sagas.retry`, although the row is on the page
   and announced") is held one level earlier, by `GuideService.allowed(area)` (`guide.service.ts`, line 287), which
   decides whether the area is ever requested; the case fails only when that is neutralised (2e, which also fails the
   administration-area case "is never offered to a session without `access.manage`, although the pages are on screen"
   and 8 cases of `guide.service.spec`). Neutralising the overlay's own area check (2c) fails exactly one case
   ("waiting for the area to be on screen > abandons the request if the session no longer holds the area permission
   when the page is ready"): the overlay's check is a second line of defence and one case holds it; 2d fails five
   cases about a session that loses the permission while a tour runs. So the Viewer is kept from the retry area, as the
   plan wanted; the check that does it is not the one the plan named. The first attempt at mutation 2 (2a: `step.requires &&`
   replaced by `false &&`) did not compile (`TS2345`, because `step.requires` is no longer narrowed) and was redone by
   deleting the line (2b).
3. **Mutation 3.** The plan names one case ("siblings inert, then restored"); eleven failed, all in
   `guide-overlay.spec.ts`, all asserting that the page is `inert` while a tour runs (`expected false to be true`, at
   lines 736, 809, 1482, 1572 and 2041): "makes the siblings of the overlay inert while the tour runs, and the overlay
   itself not" (the case the plan means), the seven "when it ends with <X> > gives the page back" cases (Done, Escape,
   Skip tour, Guide switched off, leaving the page, the session losing the permission, the overlay being destroyed),
   and three cases that check that the page is inert again after a control click, on a page change and on the
   administration tour. "Leaves a sibling alone that was inert already" passes, since the sibling is inert before the
   tour. The file's reading is that each of the eleven fails for the right reason and none for an unrelated one.

### Gates at the end of the slice

From a clean export of `425910f` (the same worktree), under Node 22.23.3, in `dashboard-web/` after `npm ci`
(`evidence/mutations-guidance.md`, part 2):

| Command | Exit | Result |
| --- | --- | --- |
| `npm ci` | 0 | `added 316 packages, and audited 317 packages in 10s`, `found 0 vulnerabilities` |
| `npm audit --audit-level=low` | 0 | `found 0 vulnerabilities` |
| `npx ng build` | 0 | no `WARNING` and no budget warning (the log has no line containing "warn"; the `initial` budget is 500 kB warning, 1 MB error); `Application bundle generation complete. [7.376 seconds]` |
| the CI guard on the built `index.html` (no bare `<script>`, no `on*=` attribute) | 0 | passes |
| `npx ng test --watch=false`, run 1 | 0 | 58 files, 2344 tests passed, 20.61 s |
| the same, run 2 | 0 | 58 files, 2344 tests passed, 20.39 s |
| the same, run 3 | 0 | 58 files, 2344 tests passed, 20.93 s |

The build's sizes, raw and estimated transfer: Initial total 453.55 kB, 119.06 kB (main 143.00 kB, the shared chunk
304.25 kB, styles 5.46 kB and one 849-byte chunk); lazy `saga-detail` 77.84 kB (19.36 kB), `guide-overlay` 20.55 kB
(6.74 kB), `admin-routes` 10.10 kB (3.07 kB), `user-edit` 27.53 kB, `team-edit` 15.60 kB, `role-edit` 13.04 kB, `setup`
11.04 kB, `account` 8.51 kB, `login` 6.28 kB, `users-list` 5.24 kB, `roles-list` 2.99 kB and `teams-list` 2.80 kB. The
transfer figures differ from the messages' by 0.01 kB in two places (the messages give `guide-overlay` 6.75 kB for
`425910f` and `saga-detail` 19.37 kB for `2c9ad9e`); the raw figures that the messages give for these chunks and for the
Initial total agree. A search of the three test logs and the build log for this record finds no `NG8113` (the sign-in
record's open item about the `EditStub`); the only notice in them is the `splitting` deprecation notice that the
sign-in record expects (`Option "splitting" is deprecated: No longer needed with Vitest 5`).

**The server, once, after the SPA gates, with no `ng test` running.** The nine commits changed no .NET code. Apart from
the whole-solution build that `71965b9`'s message reports (0 warnings, 0 errors), these are the only .NET runs in the
slice's record.

- `dotnet build dotnet/VSaga.slnx -nologo` on the plain worktree: exit 0, `Build succeeded.`, 4 warnings, 0 errors,
  all four `MINVER1001: '<dir>' is not a valid Git working directory`, because the worktree's `.git` pointer file names
  a directory the container did not mount (the sign-in record has the same warning in its worktree builds); 1 min 29 s.
- The same build with the main repository's `.git` mounted read-only at its own path, so the pointer resolves: exit 0,
  `0 Warning(s)`, `0 Error(s)`, no MinVer line; 1 min 15 s. (The main working tree was not used: it held another
  agent's uncommitted documentation edits.)
- `dotnet test dotnet/VSaga.slnx --no-build -nologo` on that build: exit 0, 16 test assemblies, every one `Passed!`
  with `Failed: 0` and `Skipped: 0`, 1644 tests in all, about 3 minutes; no "Internal CLR error", no aborted test host,
  no warning or error line in the log:

  | Assembly | Passed |
  | --- | --- |
  | `VSaga.Chaos.Tests` | 27 |
  | `VSaga.Core.Tests` | 230 |
  | `VSaga.Http.Tests` | 15 |
  | `VSaga.Persistence.InMemory.Tests` | 84 |
  | `VSaga.Dashboard.Identity.Tests` | 253 |
  | `VSaga.Persistence.Redis.Tests` | 109 |
  | `VSaga.Testing.Tests` | 7 |
  | `VSaga.Persistence.EFCore.Tests` | 218 |
  | `VSaga.Transport.Http.Tests` | 39 |
  | `VSaga.Transport.InMemory.Tests` | 13 |
  | `VSaga.Persistence.MongoDB.Tests` | 111 |
  | `VSaga.Dashboard.Api.Tests` | 513 |
  | `VSaga.Transport.MassTransit.Tests` | 6 |
  | `VSaga.Transport.Brighter.Tests` | 7 |
  | `VSaga.Transport.RabbitMQ.Tests` | 6 |
  | `VSaga.Transport.Wolverine.Tests` | 6 |

  The total, 1644, is the figure that the sign-in record could only derive by arithmetic (its 1627 plus the later
  growth of two projects); here it is observed, and the per-project counts are the ones that record lists.

### The documentation checks (C56 and its follow-up)

`f154593` ran `python3 scratchpad/c51/linkcheck.py` (a link checker kept in the session's scratch folder, not in the
repository) over the whole repository before and after: every relative link and `#anchor` in 100 markdown files (659 links) and the
`docs/*.md#anchor` mentions in code, tests and workflows, 0 problems; and with `--only` over `docs/dashboard-guide.md`,
`README.md`, `CONTRIBUTING.md` and `docs/README.md`, 134 links, 0 problems. A script that reads the seven `docsAnchor`
values and the checker's slugger found 7 of 7 resolving to a heading of the guide under GitHub's slug rules with no
duplicate slug (so none gets a `-1` suffix); every in-page link of the guide resolves; a column count over its 28
tables found no bad row. `02451d5` left the headings unchanged, so all seven values still resolve, and the checker
stayed at 0 problems. The claims of the guide were read from the code (the commit lists which files); the ones that
rest on a live run, the two-stacks behaviour and the sign-out after `down -v`, cite the C50 check in
[`dashboard-sign-in-and-access.md`](dashboard-sign-in-and-access.md#live-c50-including-two-stacks-in-one-browser).

## Review rounds

Each commit went through a review; the messages call them "the review" (C53, C56) and "the combined review" (C54,
C55). What each round found, from the messages:

| Commit | Review result | Fixed in |
| --- | --- | --- |
| C53 `de8744f` | no blocker, one major (a running tour queued behind itself) and nine minors (m1 to m9) | `0419f6f` |
| C53 `de8744f`, live check | F1, at 360 px | `425910f` |
| C53 `de8744f`, review of the user guide | a false sentence in the list tour's first step | `425910f` |
| C54 `94d67b6` | combined: no blocker, no major, eight numbered minors | `2c9ad9e` |
| C55 `e4e62d6` | combined: approved, no blocker, no major, four minors | `adcf234` |
| C56 `f154593` | "accepted it with minor edits": ten bullets, one a documentation gap and nine corrections of the guide's statements | `02451d5` |

The messages do not report a review of `2c9ad9e`, `adcf234`, `02451d5` or `425910f`.

What the reviews and the real-stack run caught is worth reading against the checks that passed. By the time of
`0419f6f` the first commit had 2137 passing specs and a mutation pass of 179 mutations that each failed their own
specs, and still a sequence of two calls ran a tour twice. A mutation pass breaks code that exists; it cannot show that
a guard is missing, and it cannot show that a sentence of copy is false. The same holds for the retry answer that no
screen reader heard, the data tour over an empty bar, the churn of Replay and the hint between administration pages
and the false sentences: each was found by a reviewer reading the commit, not by a check that had been written. The
specs run in jsdom, which does no layout (the overlay specs use fake anchors and a stubbed frame queue), so a layout
bug such as F1 can only be found in a browser, and the mock runs, which did run in one, had not walked the list tour
at 360 px (their saved outputs show the top bar and the hint at narrow widths for C53 and the administration tour at
360 px for C55, not the list tour's steps). That is an inference from what the records contain, not something any
commit message says.

## Problems found along the way

- **A tour that ran twice (C53, found by the review, `0419f6f`).** `seen` is written when a tour ends, so
  announcing the tab again while its tour was on screen (`areaShown('map')` while `map` runs) found the area unseen,
  queued it, and the tour ran a second time after Done. `enqueue()` now returns at once for the running area and
  `advance()` skips an area that is seen as well as one the session may no longer see. The specs: the sequence from the
  review (started, announced again, ended remembered: no request) and one that tells the two guards apart. The
  `advance()` guard on its own is defence in depth that no sequence can reach once `enqueue()` holds, so its one-token
  mutation survives as an equivalent mutant; removing both fails the review's spec.
- **Found in a real browser before C53 was committed (`de8744f`).** `saga-list.scss` had a bare `button {...}` rule
  (accent background, white text) that beats the global `.btn` inside the page, so the sort buttons would have looked
  like accent buttons; it is scoped to `.toolbar`, `.banner` and `.pagination`, and computed styles of Refresh,
  Previous, Next, both banner buttons, every `th` and the first cell are identical before and after, with the sort
  buttons computing transparent, the heading's muted colour, 11.52 px 700 uppercase, no border or padding and the
  global focus ring (`evidence/C53/styles.json`). Sorting from the keyboard dropped the focus: the list replaces its
  table by "Loading..." while the sorted list is read, which destroyed the focused button, so Enter sorted once and
  Space did nothing; the page now hands the focus to the new heading button when the list is back, unless the user
  moved it meanwhile, and forgets it when there is no table. The pager's Previous and Next have the same loss, which
  is older and was left alone. Rows are not focusable (the link is), so the `tr:focus-visible` rule that the
  `overflow: hidden` table seemed to need was not needed: the rings were measured and photographed inside the table.
- **Nine minors of the C53 review (`0419f6f`).** The global `.btn:hover` colour put 2.60:1 text on the dialog's
  primary button when hovered (`.guide-primary:hover` keeps the dark label, 6.11:1); answering the hint removes the
  button that was pressed and the keyboard focus with it (the Guide switch takes the focus first, for both answers);
  a popover taller than the screen (400% zoom, a short landscape phone) could not be reached (at most
  `100dvh - 24px`, a flex column whose text scrolls and whose buttons stay in view; the review's `overflow-y: auto` on
  the popover alone left the focused Next button below the scrollport at 320 x 256); `restoreFocus()` moved the focus
  unconditionally, also after a tour ended because the page changed (it now returns early when the focus is somewhere
  that is not the body and not the dialog); the order `release()` then `restoreFocus()` is testable in jsdom after all
  (the message of `de8744f` said it needs real `inert` semantics, which was wrong), and the per-exit-path spec for the
  key listener ended on a Tab press that could not fail on a leaked listener, so it now asserts that the handler added
  when the tour began is the one removed, on each of the seven exit paths; the `GUIDE_STORAGE` factory's `try/catch`
  had no spec; a describe sat under
  another block's comment; `areaShown()`'s doc now says to call it from an effect or `afterNextRender`, never from a
  constructor, because `enterPage()` resets the page's state on `NavigationEnd` and a component is created before
  that (not made robust: carrying calls made during a navigation would also carry the old page's late calls); and
  "Step n of m" went inside the live region.
- **The administration tour lost its table step (C55, found in Chromium, designed out in `e4e62d6`).** The
  decision above; the `shown` trigger and the shell's announcement exist because of it.
- **Replay and the hint were removed and re-created on every navigation inside the administration area (C55,
  `adcf234`).** The review's finding was marked unverified; a spec that mounts the real `App`, observes the top bar and
  moves from Users to Teams or Roles found it real: `enterPage` nulls the current area at `NavigationEnd`, the Guide
  toggle sits earlier in the template than the page and refreshes with no area, which removes Replay and the hint
  (the hint is in a `role="status"` region, so a screen reader announces it again when it comes back), and the shell's
  effect then announces the area and they return. The check is the identity of the element before and after the move.
  Fixed with `within`. The same spec, with the real `App`, the real routes, shell and pages and only the HTTP answered
  by the spec, also holds that no tour starts while the shell reads (not even at the overlay's 250 ms poll), that
  "Step 1 of 8" and "Administration" show once the reads are answered, "Step 1 of 7" on a teams page with no teams, and
  that a seen tour starts nothing; four mutations each fail it (announcing from the constructor, before the pages
  show, the trigger back to the route, never announcing).
- **The retry answer was silent to a screen reader (C54, `2c9ad9e`), and the fix's message was wrong
  (`adcf234`).** The `<span class="retry-message">` was inserted together with its text and the focus stays on the
  Retry button when the answer comes, so a screen reader heard neither "Retry accepted" nor a refusal. It is now an
  always-present `role="status"` in the retry row, empty until there is something to say, the text put into the same
  element, and not an alert; `aria-busy` was left out because it defers a live region's announcement, the opposite
  of what is wanted when the answer lands in the same tick. `2c9ad9e`'s message then said the answer is lost to a
  reader only when the saga starts running again and the row goes, "the success case, announced by the status badge
  change". **That is wrong, and `adcf234`'s message says so as an erratum:** the status badge is not a live region,
  so when the saga's own update removes the row before or with the answer, nothing is said at all; the answer is heard
  whenever the row stays, which is every refusal. It was left as it is, because moving the message out of the row would
  keep a stale "Retry accepted" under a saga that failed again. No screen reader was used for any of this: what was
  checked is the accessibility tree and focus in Chromium.
- **A data tour over a bar that showed nothing (C54, `2c9ad9e`).** `dataShown` tested only `dataView() !== null`, so
  `?data=compare` on a saga with nothing to compare (no recorded snapshot, or no stored state) announced the data tour
  over a bar that shows nothing for it. It asks what the bar asks (`canCompareData`, `visibleDataView`) and a Compare
  whose missing side arrives later is announced then. The `data-views` step is anchored on the Saga data bar but
  described Changes, Full state, Message and Copy JSON, which live in an open panel or a step's inspector; it now says
  "Once a view is open", and that Message is only under a step's Data button (the Saga data panels give the inspector
  no message). C54's copy, as it stood at `e4e62d6`, had said that every view offers Message; `f154593` noticed that
  while writing the guide, and the follow-up under way at the time reworded it.
- **Copy that did not match the page.** The review of C54 found that `retry-effects` said the side effects of the other
  services that consume the message "can repeat", at odds with design §7.5 (other saga types receive and ignore the
  replay, so the side effects are the participants'); it now says "so a participant that handles it again repeats its
  side effects" (`2c9ad9e`). `retry-what` now says what a refused retry looks like (the button is dimmed, with its
  reason beside it). `summary-tabs` said "The Map tab, highlighted here, and the Timeline tab" when only the Map tab is
  spotlighted. After `425910f` a step may be shown with no spotlight at all, so that sentence and the list tour's
  "Switch Guide off here" became "The Map tab and the Timeline tab next to it" and "in the top bar", and a spec fails on
  "highlight", "spotlight" or a "here" that points (the page's own labels "Failed here" and "Re-run starts here" are
  exempt). And the list tour's first step said "Press Esc to leave it; Guide in the top bar brings it back", which is
  false: after Escape the area is seen and Guide stays on, so pressing Guide would switch it off; it now says "Replay
  tour in the top bar runs it again", pinned by a spec with the old sentence asserted absent. That one was found by the
  review of the user guide, in the list tour that `de8744f` wrote; the area's `version` was not bumped, because the
  branch was unmerged, nobody had seen the old text and the bump rule is for shipped tours.
- **The guide's nine corrections (C56, `02451d5`).** A document written from the code, with every label read from the
  components, still needed nine corrections after review, each checked against the code before the text changed.
  The guide said an administrator can read your Account page; it now says you read it there and an administrator reads
  it for any user in the Effective access preview. Reset password does not clear a lock
  (`AccessAdministrationService.ResetPasswordAsync` leaves the count and the lockout to `UnlockUserAsync`), so the Users
  section and the troubleshooting paragraph now say to select Unlock too. The `ResetOnStart` row said it "gives it
  back" the Administrator grant; `FirstAdministratorService.Restored` gives the named account an Administrator grant for
  all saga types whatever it held (other grants stay, a missing account is created) and clears a forced password
  change, and a password the policy rejects resets nothing and degrades the `identity` entry of `/health`, so the row
  now says that whoever is named becomes an administrator. Guide mode and `README.md` no longer say that each part of
  the dashboard explains itself: the list, a saga's page and the administration pages do, and sign-in, setup and
  Account have no tour. Sorting: the first selection of a column sorts ascending, so Status starts with Running and
  Updated with the oldest, the opposite of the default. The map: a box gets a red border and not a red fill, the
  replay controls are icon buttons with the tooltips Restart, Play, Pause and Step, and the as-of-entry banner has up
  to two further lines from three possible sentences (all three are listed), not "two sentences". "Signing out..." is
  the label of the name (menu) button while it works, not of the Sign out item. The in-app User guide link opens the
  copy on GitHub's `main` branch (`USER_GUIDE_URL`), so it needs internet access and can be newer than the build in
  use; the guide's two User guide rows now say so. The row for being signed out after `down -v` now gives both ways a
  tab with no live connection finds out: its first change (a 401) or a return to the tab (the session read of
  `auth.service.ts`, at most once a minute). The commit's subject says "ten statements"; its message has ten bullets,
  of which nine are these corrections and one is the gap that gave `docs/dashboard.md` and `dashboard-web/README.md`
  their mention of guide mode, and some of the nine cover more than one sentence (the map's three are one bullet).
- **F1 (`425910f`).** Told above. It was found by the run on the real stack, after the mock runs had passed.
- **Commit messages that were corrected or checked later.** None was amended; each was dealt with in a later message.
  `de8744f`: "swapping the order of `release()` and `restoreFocus()` needs real `inert` semantics" (`0419f6f`: it is
  testable in jsdom). `94d67b6` named three changed specs (the Retry button's `button.disabled` assertions) and said
  "nothing else was weakened", but C54 had also changed three existing tests without naming them (`guide-overlay.spec`
  "abandons an area that has no tour at once", retargeted from the summary to `admin`, the one area still empty;
  `guide.service.spec` "switching on explains what is shown", which now expects `list` and then `timeline`;
  `guide-tours.spec` "ships the list tour ... and no other yet", which dropped its pin that every other area is
  empty). `2c9ad9e` names them and finds all three legitimate, because the area each relied on now has tours; it also
  verifies that `e4e62d6` dealt with the two pins that depended on `admin` having no steps, and says that it changed
  one detail-page test itself ("announces the data when a Saga data view is opened, and again when it is reopened"
  reopened with a Compare that its saga cannot show; it reopens with At start, and Compare has cases of its own).
  `2c9ad9e`: the erratum above. `e4e62d6`, four items in `adcf234`: (a) the message said "the page says so for a
  reset"; that was checked, and the page says "The password of X was reset and their sessions have ended" for any user
  reset (and, on one's own form, "its sessions end when the password is set") and nothing of a disabled account, whose
  sentence in the tour comes from the server (the security stamp is rotated when the enabled flag changes and on a
  reset), not from the page; (b) its per-file spec counts mixed `it` call sites with `it.each` expansions, so `adcf234`
  re-counted by running each file at `94d67b6` and `e4e62d6` (guide.service 92 to 97, guide-tours 49 to 71,
  guide-overlay 115 to 126, admin-shell 22 to 35, users-list 31 to 36, teams-list 10 to 14, roles-list 9 to 12; the
  total, +63, was right); (c) two tests it renamed without naming them; (d) "Step 1 of 7 at every read delay with the
  route trigger put back" had a script and no saved output, so it was run again (0, 40, 150, 400 and 1500 ms, all
  "Step 1 of 7") and the output kept. The comment in `admin-shell.ts` that said the router had "already finished the
  navigation that created this shell" was wrong (the shell is created while the router activates the route, and that
  navigation's `NavigationEnd` follows and the shell sees it; a probe in a spec counted one) and was corrected.
  `02451d5`: the subject's "ten statements" (above).

## Where the build left the plan

- **The administration tour's trigger is "shown", not the route** (the plan's list of triggers for C53 gave "route
  match" for the list and the administration; design §9.1 says "on navigation"); the reasons are under "Why". The
  design's sentence has not been changed.
- **Switching Guide on explains everything again** (`setEnabled(true)` clears `seen`); design §9.1 and the plan say an
  area explains itself "the first time" or "once", which `f154593` records as reading like "once per version for
  good".
- **Seven `docsAnchor` values, not one per H2.** Design §9.2 and the plan say fixed H2 headings the app links to; the
  code links to seven headings, three H2 (`#the-saga-list`, `#the-saga-detail-page`, `#administration`) and four H3
  (`#map`, `#timeline`, `#saga-data` and `#retrying-a-saga`: `de8744f` pointed the four detail-page areas at the H3
  headings of design §9.2 and said that C56 must have them). Opening the dashboard, Signing in, Guide mode, Your account
  and Troubleshooting have no tour that opens them; the toggle's own User guide link opens the top of the document.
- **`GUIDE_ANCHORS` holds all 21 names from the first commit**, not only those with markup yet (`de8744f`; the plan said
  the commit ships the list area's steps and the `topbar-guide` anchor, and that the area table may list the other
  areas); the other pages' attributes and contract cases came with `94d67b6` and `e4e62d6`; `guide-tours.spec` pins
  the list.
- **A queue of announced areas, and `startNext`** (Escape and Skip drop it; Done starts the next): the plan's list for
  the service names request, started, ended, abandoned and a way to announce, not a queue.
- **Switching Guide on from the detail page** starts the page's own area and then the part shown last (`94d67b6`),
  instead of the current area only; Replay still repeats the part shown last.
- **`detail-data` is on the Saga data group in `saga-data-overview.html`**, not in `saga-detail.html` as the plan
  listed it (its message: the host element would box the bar and whatever panel is open under it).
- **The Retry button became `aria-disabled`** in C54 (the open item at the end of the sign-in record; not in the plan's
  list for C54), with three existing specs replaced and three more tests changed, as above.
- **Rows open through a link in the first cell.** The plan's wording of the review-consistency finding lets a row open
  on Enter and Space or through a link in the first cell; the link was chosen (the finding's second option), and
  `de8744f`'s message names Enter for it, a modified click left to the browser, and a click anywhere else in the row
  still opening the saga.
- **The C55 live check was run after C55, not as part of it, and the real stack only then.** The plan put it under C55
  with evidence `C55.md`; `e4e62d6` was committed with the mock-API run and a stated "Not verified" for the compose
  stack, and the real-stack run is `evidence/C55-live.md` (there is no `C55.md`; the mock run's output is in
  `evidence/C55/`). Every item of the plan's list was run, plus an Operator user, a Guide-already-on script and the
  dark-theme and 360 px steps; its one finding was fixed by `425910f`.
- **No minimum engine version number.** The plan asked the guide to state one; there is none to find: the repository
  has no release tags (`git tag` was empty when `f154593` was written), and the dashboard cannot check the engine's
  version (`docs/dashboard.md`, ADR 0008). The guide says the minimum is the first release containing the targeted
  redrive (`MessageEnvelope.TargetSagaTypeHeader`, `x-vsaga-target-saga-type`, introduced by `417a64e`) and how to see
  it on a host: an engine that has the check logs "Ignoring <type> <id> for saga <saga>: it is targeted at saga type
  <target>" at Debug and writes nothing to the timeline.
- **The lockout escape.** Design §8.4 and §10 name a restart with `Dashboard:Lockout:MaxFailedAttempts=0` as the way
  out; `CredentialVerifier.Refusal` refuses a user whose `LockoutEndUtc` is ahead whatever the setting says, and
  `RecordFailedSignInAsync` with 0 only stops locking. The guide lists Unlock, `Dashboard:Admin:ResetOnStart` and
  waiting, as `docs/dashboard.md` already did; the design and ADR 0006 still carry the old wording, which the sign-in
  record lists.
- **First-run setup has no time window** in the guide (the blueprint wrote "the setup screen and its window"): the
  code works until a user exists, and case, spaces and hyphens do not matter when the code is typed.
- **Other places where the code won over the blueprint, the design or the brief** (each in `f154593`'s message and in
  the guide as the code has it): Replay and the hint depend on a permitted area (`canReplay`, `showHint`), not on an
  area having steps, which is an invariant a spec pins and not a run-time check; the step-data inspector offers Message
  only under a step's Data button; the map shows no visual difference between an Initiator and a Participant box
  (only the orchestrator, an unresolved "?" box and the replay states are styled), so the guide does not list them; a
  Manual retry step reads "requested by <actor>" for a dashboard retry, "requested by api-key" for one made with the
  key and plain "requested" when the entry has no source service (an in-process retry: the engine's `RetryAsync`
  records none); a 409 from the status check writes nothing, while a 409 from the version-checked reset and a 502
  leave a Manual retry step with nothing after it (design §7.3), and a 502 that could not restore the saga says why in
  two different sentences, only one of which names the state it is stuck in.
- **Dead defences were removed instead of tested** (`de8744f`: the overlay's generation and show tokens, a redundant
  `stopWaiting`, two checks in the queue advance that no sequence of calls reaches), and two unreachable conditions in
  `94d67b6`; `0419f6f` then added a guard to `advance()` that its message calls defence in depth.
- **An existing test changed in `de8744f`:** `app.spec`'s "every link of the top bar leads to a route of the app" now
  looks at the app's own links (hrefs that start with `/`), and the toggle's one external link is pinned by a new case.
- **The mentions of guide mode in `docs/dashboard.md` and `dashboard-web/README.md`** were left out of `f154593` (its
  message calls them outside its scope) and added by `02451d5`; the blueprint had asked for them.
- **Area versions were not bumped** for the copy corrections of `2c9ad9e` and `425910f`, by the rule that the bump is
  for shipped tours and the branch is unmerged.
- **The plan's guidance mutation checks did not all behave as the plan expected** (under "Mutations", the slice-level
  pass). Mutation 1 failed the three cases of the list page's anchor-contract group and not `guide-tours.spec`, which
  reads no template. Mutation 2, deleting the overlay's `requires` filter, failed the overlay case that drops
  unpermitted steps and a second step case, and did not fail the C54 case that a Viewer never gets the retry area:
  that case is held by `GuideService.allowed(area)`, which the extra mutations 2c to 2e located. Mutation 3, deleting
  the `inert` toggle, failed 11 cases and not one, all asserting that the page is inert while a tour runs. The plan's
  expected results for 2 and 3 were therefore wrong about where the guard sits and how many cases hold it, not a sign
  of an unguarded behaviour; seven extra mutations were added (2c to 2e to locate the guard, 4 to 6b for the follow-ups' specs), and each failed the specs written for it.

## Unverified and open

None of the following was observed in the recorded runs, or was observed and left as it is.

- **A screen reader.** No screen reader was used. The retry answer's `role="status"` region, the hint's region and the
  step's `aria-live` region were checked as roles in Chromium's accessibility tree and by focus and text, which is not
  what a person using one hears.
- **Browsers other than headless Chromium.** Every browser check, mock and real, used it. The focus behaviours that the
  slice depends on (a button that is removed, or natively disabled, while it holds the focus sends it to `<body>`) were
  found in Chromium and not checked elsewhere.
- **The two-origin variant of two stacks.** The guide says that `127.0.0.1` beside `localhost` keeps two stacks apart,
  because cookies are scoped by host name, as `docs/dashboard.md` states; it was not exercised in a browser.
- **The queueing rules beyond what the live check drove.** `f154593`'s message says that the tours' queueing rules (what
  Guide does on a page that is already showing parts, the retry row appearing again) were read from `guide.service.ts`
  and `guide-overlay.ts` and not driven live; the live check at `e4e62d6` then drove several sequences (summary, retry
  and map in turn after Done; Escape dropping the queue; Guide switched on at a page that is already showing; Replay
  after a tab), and no evidence file records a run of `2c9ad9e`, `adcf234` or `425910f` on the real stack.
- **Switching Guide on from the detail page queues only the summary and the part shown last.** A Failed saga whose retry
  row, or whose open Saga data bar, is on screen does not explain retry or data in that session until that part is
  shown again (opening the saga again, or another failed one). Observed live (D2 of the live check) and stated in the
  guide.
- **Replay repeats only the part shown last, and nothing says a part was hidden.** After the Data bar closes or a
  Failed saga starts running, Replay waits five seconds for the anchor (20 looks at 250 ms) and does nothing; a queued
  retry for a saga that stops being retryable waits five seconds and is dropped, so the next queued tour is that much
  later. This is `2c9ad9e`'s statement of the behaviour; no run in the evidence exercises it. The optional `GuideService.areaHidden(id)` was not built, and the reasons are in that message: the summary must be
  excluded (its effect is false until the saga loads, and it would cancel the request the route already made),
  Replay's current area needs a rule for what it falls back to, the overlay's polling has to be shown to stop when
  its request is cleared, and each of four parts needs a hide-and-show spec with the live-refresh flicker of the map
  and the timeline in mind; five seconds in two rare cases did not justify that in a follow-up.
- **A tab changed mid-tour leaves the tour centred.** Browser Back to the other tab leaves the tour centred over it
  until Done, as for any anchor that disappears (`2c9ad9e`; no run in the evidence exercises it).
- **`reveal` is used by no tour.** It is implemented and specified, and nothing would restore a tab selected for one.
- **A page that announces an area from its constructor loses the announcement.** `enterPage()` resets on
  `NavigationEnd` after the component is created; `areaShown()`'s doc says to use an effect or `afterNextRender`, and
  specs hold it for the detail page (`saga-detail-guide.spec.ts`) and the administration shell (`admin-guide.spec.ts`),
  but nothing stops a later page doing it wrong (`0419f6f` m8, not made robust).
- **The retry success message may not be announced.** When the saga's own update removes the retry row before or with
  the answer, the message goes with the row and the status badge is not a live region (the erratum above).
- **The in-app "User guide" link opens GitHub's `main` copy.** `USER_GUIDE_URL` is
  `https://github.com/RafaelJCamara/vSaga/blob/main/docs/dashboard-guide.md`, so a fork or an installation without GitHub
  access gets upstream content or nothing, and the copy can be newer than the build in use (the guide says so, since
  `02451d5`). Serving the guide from the dashboard would need a Markdown renderer or a pre-rendered page in the image;
  the link stays one constant so the choice can change without touching the tours. This is the design's one remaining
  open question (§13), a decision for the maintainer.
- **At 360 px the saga list, users and roles tables are wider than the viewport with Guide off**, so those pages
  scroll sideways on their own (`425910f`; the teams page does not). Pre-existing; the tour does not cause it. The
  list tour now scrolls the page sideways to the sort heading, which is the document's own scroll because the table has
  no scroll container of its own (`scrollWidth` 768 at 360 px).
- **Each Map or Timeline tab switch adds a history entry.** Eight Back presses were needed to leave a page after four
  tab switches (live check). Pre-existing behaviour of the detail page, not part of the slice.
- **A viewer without `sagas.retry` still gets 200 from `GET .../retry-plan`** (the timeline's "Failed here" markers use
  it). Read-only and pre-existing; not guide-related.
- **The spotlight is absent while a smooth scroll to a distant target runs** (about 1.3 s at 360 px for list step 6),
  and at 360 px it follows the row element it began with when new sagas push the rows down; both are in the live-check
  file as observations and neither was changed.
- **The tour copy is held to the pages' own wording only where a spec reads both.** That is the administration copy
  (`adcf234`) and the retry, data and summary sentences (`2c9ad9e`); the other specs pin the labels a tour names and
  the anchors, and the rule's own comment in `guide-tours.ts` says that a stale sentence is noticed by nobody. This
  slice found several false or misleading sentences in the tours by review (above), none by a test that existed then.
- **The guide was reviewed once.** The review of the user guide led to nine corrections in a document written from the
  code; the messages do not report a second review of the corrected text, and the guide's claims were read from the
  code and not driven in a browser, apart from those that the C50 and C55 runs cover.
- **Design wording the code contradicts.** Design §9.1 says the list and administration pages explain themselves "on
  navigation" and an area "the first time" (a switch-on explains again, and the administration tour starts once its
  pages show), and §9.2 says fixed H2 headings the app links to (seven headings, four of them H3). This record does not
  edit the design; its final update is the plan's last commit.
- **RabbitMQ's first-start failure** in the live-check container (`.erlang.cookie: eacces`) was environmental and not
  investigated; a second `up -d --wait` passed.
