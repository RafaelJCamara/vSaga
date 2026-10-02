# Saga detail page: labelled times, jump to map, transitions and data inspector (improvements 2, 3 and 4, client side)

All paths are under `C:/Users/rafae/Documents/Projects/vSaga/`. `APP` stands for `dashboard-web/src/app`.

## Summary

- The Timeline tab becomes a list of **steps** (decision C's transitions), produced by a pure fold over `SagaLogEntry[]`. `StatePersisted` entries become snapshots and are never rendered as rows.
- Every entry row carries a labelled time, `Recorded at 14:03:07.140 +1.224 s`: viewer-local, UTC on hover, offset from the saga's first entry, one timezone hint per list. The summary card's Created and Updated use the same formatter.
- Every entry row is a native button that goes to `?tab=map&entry=<sequenceNumber>`. `SagaMap` gains a `focusSequence` input that pins the replay there, shows an "as of entry" banner and lights the orchestrator for plain events.
- Each step has a `Data` disclosure: changes against the previous recorded snapshot, the full state, and the message payload where one was recorded.
- The Data tab is removed. A "Saga data" bar under the summary card opens At start, At end and Compare.
- Live updates are coalesced (250 ms) and also re-fetch the detail, so "At end" stays current.
- No new npm dependency. Four new components and five pure modules, each with a spec; `saga-detail.scss` shrinks.

## Design

### 1. Model: `APP/models/saga.model.ts`

- `SagaEntryType` (12-33): append `| 'StatePersisted'`.
- `SagaLogEntry` (54-68): add `sourceService?`, `destinationService?` and `causationId?`, each `string | null`. The API already sends them (`SagaLogEntry.cs:20-22`); optional keeps `makeEntry` (`saga-detail.spec.ts:40-57`) and every other spec literal compiling.
- `payloadJson` and `dataJson` keep their types. New code tests `== null`, because the API leaves them out without `sagas.data`.

### 2. Pure modules (new folder `APP/util/`)

**`state-json.ts`** replaces the `prettyDataJson` getter (`saga-detail.ts:227-251`).

```ts
export const SAGA_KINDS = ['Orchestrated', 'Choreographed'] as const;   // index = C# SagaKind
export const SAGA_STATUSES = ['Running', 'Completed', 'Failed', 'Compensating', 'Compensated', 'TimedOut', 'Cancelled'] as const;
export type ParsedJson =
  | { kind: 'empty' } | { kind: 'value'; value: unknown } | { kind: 'invalid'; raw: string }
  | { kind: 'omitted'; bytes: number | null; limit: number | null };
export function parseStateJson(json: string | null | undefined): ParsedJson; // top-level numeric Kind/Status become names
export function formatStateJson(json: string | null | undefined): string;    // '' | 2-space pretty | raw text when invalid
export function prettyJson(json: string | null | undefined): string;         // no enum mapping: message payloads
```

`formatStateJson` keeps the getter's exact behaviour. A value is an omission marker when it is an object with an own key matching `/^\$vsaga\w*Omitted$/` set to `true`. That covers MongoDB's `{"$vsagaPayloadOmitted":true,"bytes":N,"limit":L}` (`MongoSagaEventLogStore.cs:51`) and the engine's oversize marker.

**`json-diff.ts`**

```ts
export interface JsonChange { path: string; kind: 'added' | 'removed' | 'changed'; before?: unknown; after?: unknown; }
export interface JsonDiff { changes: JsonChange[]; truncated: boolean; }
export function diffJson(before: unknown, after: unknown, max = 200): JsonDiff;
export function previewValue(value: unknown, maxChars = 120): string;   // compact JSON, ellipsis when cut
```

The walk is recursive and depth-first, and stops at `max`:

1. Two plain objects: walk the keys of `after` in order, then the keys only in `before`. A key missing on one side is one `added` or `removed` change carrying the whole subtree.
2. Two arrays: compare index by index, then one `added` or `removed` per surplus index. There is no move detection.
3. Anything else (primitives, or different JSON types): `changed` unless `Object.is`-equal.

Paths read `Order.Lines[2].Quantity`; a key that is not an identifier prints as `["odd key"]`. The inspector diffs `parseStateJson` values, so a status change reads `Running → Failed`. It lists top-level `Version` and `UpdatedAtUtc`, which change on every persist, in one muted "engine bookkeeping" line.

**`time-format.ts`**

```ts
export interface RecordedAt { local: string; utc: string; offset: string; }
export function formatRecordedAt(iso: string, originIso: string | null, timezone?: string): RecordedAt;
export function formatLocal(iso: string, format: 'time' | 'datetime', timezone?: string): string;
export function formatOffset(ms: number): string;   // +0.000 s, +1.204 s, +2:05.300, +1:02:03, +2d 01:02:03
export function timezoneLabel(at?: Date): string;   // UTC+02:00, UTC
```

`local` is `HH:mm:ss.SSS`, prefixed with `MMM d, ` when its local day differs from the origin's. `utc` is `yyyy-MM-dd HH:mm:ss.SSS UTC`. Both use `formatDate` from `@angular/common`; unparseable input returns the raw text. The optional `timezone` keeps specs deterministic.

**`entry-type-label.ts`**: `entryTypeLabel(type)`, moved from `saga-detail.ts:260-262`. The map uses it too; today it prints the raw type (`saga-map.html:78`).

**`saga-transitions.ts`**

```ts
export type TransitionTrigger = 'start' | 'message' | 'timeout' | 'retry' | 'delivery' | 'detached';
export type TransitionOutcome = 'succeeded' | 'failed' | 'unhandled' | 'exhausted' | 'requested' | 'in-flight';
export type SnapshotState = 'recorded' | 'omitted' | 'withheld' | 'not-persisted' | 'missing' | 'pending';
export interface TimelineRow { entry: SagaLogEntry; ordinal: number; }      // ordinal = the map's "#i"
export interface SagaTransition {
  key: number; ordinal: number; isLast: boolean;                            // key = first entry's sequenceNumber
  trigger: TransitionTrigger; outcome: TransitionOutcome; title: string;
  fromState: string | null; toState: string | null; errorMessage: string | null; actor: string | null;
  rows: TimelineRow[];
  message: { label: string; json: string } | null;                          // SagaStarted or StepFailed payload
  snapshotJson: string | null; snapshotState: Exclude<SnapshotState, 'pending'>;
  baseline: { json: string; ordinal: number; title: string } | null;       // nearest earlier recorded snapshot
}
export interface SagaHistory {
  transitions: SagaTransition[]; rowCount: number; firstOccurredAtUtc: string | null;
  initiating: { messageType: string | null; json: string | null } | null;
  firstRecorded: SagaTransition | null;
}
export function foldTimeline(entries: readonly SagaLogEntry[]): SagaHistory;
export function effectiveSnapshotState(t: SagaTransition, live: boolean): SnapshotState;
```

The fold keeps the API's order (ascending sequence by contract) and never parses a snapshot, apart from a prefix test for markers. Pass 1 collects `outcomeIds`: every `messageId` on a `StepSucceeded`, `StepFailed`, `UnexpectedEvent` or `StatePersisted`. Pass 2 tracks `awaiting` (messageId to a step with no snapshot yet), `byMessage` (messageId to its latest step), `keyless` (the latest timeout or retry step with no snapshot) and `current` (the step touched last).

| Entry | Lands in |
|---|---|
| `SagaStarted` | Opens `start` and registers its `messageId`. |
| `MessageReceived` | The `start` step awaiting the same `messageId` and still without a `MessageReceived`, because the engine logs both for one message (`SagaOrchestrator.cs:420-423, 612-614`). Otherwise it opens `message` when its id is in `outcomeIds`, or `current` is missing or already has an outcome. Otherwise it joins `current`: it is a `.CallHttp` reply logged mid-step under a fresh id (`HttpCallDefinition.cs:60-62`). |
| `TimeoutFired` | Opens `timeout`, which becomes `keyless`. |
| `ManualRetryRequested` | Opens `retry`, which becomes `keyless`. `actor` is `sourceService` without the `dashboard:` prefix. Its `messageId` is not registered, because the in-process retry reuses the failed message's id (`SagaOrchestrator.cs:239-245`). |
| `StepSucceeded`, `StepFailed`, `UnexpectedEvent` | `awaiting[messageId]`. With no `messageId` (a timeout's outcome, `SagaOrchestrator.cs:280-281`) `keyless`, else `current`. |
| `StatePersisted` | The same lookup. It becomes the step's snapshot and is never a row. |
| `DeliveryExhausted` with a `messageId` | `awaiting[messageId]`, else it opens `delivery` (a dead-letter before any `MessageReceived`, `SagaOrchestrator.cs:154-161`), so the Failed snapshot that follows is not charged to the previous step. |
| Outbound (`MessagePublished`, `MessageSent`, `ChildSaga*`) | `byMessage[causationId]`, the inbound id (`SagaContext.cs:220-222`), which is also right for publishes drained after the snapshot. Else `current`. |
| Everything else | `current`. |

An entry with nothing to join opens a leading `detached` step.

`snapshotState` per step: a null payload is `withheld`, a marker is `omitted`, anything else is `recorded`. With no snapshot, these are `not-persisted`: an `unhandled` outcome, a `retry` or `detached` step, and a non-final `timeout` with no outcome. The rest are `missing`. `effectiveSnapshotState` reports the final step's `missing` as `pending` while the saga is Running or Compensating.

How the required edge cases come out:

- **No snapshots** (older saga, or `RecordStateSnapshots` off): every step is `missing` and `firstRecorded` is null.
- **Oversize or MongoDB marker**: `omitted`, and never used as a baseline.
- **Lost race**: `missing`. Later steps diff against the last recorded snapshot and name it.
- **Manual retry**: its own step. With a reset it carries the reset snapshot.
- **Interleaved handlers**: ids decide. Entries with no id (`TimeoutScheduled`, `SagaCompleted`, `Compensation*`) follow `current`, which is best effort.

### 3. Components

**`LocalTime`** (`app-local-time`): inputs `value` and `format`; renders `<time [attr.datetime] [attr.title]="utc">`. No stylesheet.

**`SagaTimeline`** (`app-saga-timeline`): inputs `history`, `focusedSequence`, `canViewData`, `live`; output `entrySelected: number`. It owns `openKeys = signal<ReadonlySet<number>>`, so open inspectors survive refreshes. Row times come from one `computed` map.

```html
<p class="tl-hint">Times show when the engine recorded each entry, in your local time ({{ tz }}).
  Hover a time for UTC. The offset counts from the saga's first entry.</p>
<ol class="tl" data-tour="detail-timeline">
  @for (t of history().transitions; track t.key) {
    <li class="tl-step" [class]="'tl-step--' + t.outcome">
      <div class="tl-head">
        <span class="micro-label">Step {{ t.ordinal }}</span> {{ t.title }} · {{ outcomeLabel(t) }}
        @if (canViewData()) {
          <button type="button" class="btn-quiet" [attr.aria-expanded]="isOpen(t.key)"
                  [attr.aria-controls]="'step-data-' + t.key"
                  [attr.aria-label]="'Data after step ' + t.ordinal + ': ' + t.title"
                  (click)="toggleData(t.key)">Data</button>
        }
      </div>
      <ol>
        @for (row of t.rows; track row.entry.sequenceNumber) {
          <li><button type="button" class="tl-row" [class.tl-row--focused]="row.entry.sequenceNumber === focusedSequence()"
                      [attr.data-seq]="row.entry.sequenceNumber" title="Show the map as of this entry"
                      (click)="entrySelected.emit(row.entry.sequenceNumber)">
            <span class="mono">#{{ row.ordinal }}</span>
            <span class="entry-type">{{ label(row.entry.entryType) }}</span>
            <!-- from → to, messageType, source or destination service, errorMessage -->
            <span class="micro-label">Recorded at</span>
            <time [attr.datetime]="row.entry.occurredAtUtc" [attr.title]="at(row).utc">{{ at(row).local }}</time>
            <span title="Since the saga's first entry">{{ at(row).offset }}</span>
            <span class="sr-only">Show on map</span>
          </button></li>
        }
      </ol>
      @if (isOpen(t.key)) { <app-saga-data-inspector [id]="'step-data-' + t.key" … /> }
    </li>
  }
</ol>
```

- The row is a `<button>` holding only spans and a `<time>`, so Enter and Space work natively and its text is its accessible name.
- The Data toggle is a sibling in the step header, never nested in a row.
- An `afterRenderEffect` scrolls to and focuses `[data-seq]` once per new `focusedSequence`, guarding `scrollIntoView?.`. A refresh never steals focus.

**`SagaDataInspector`** (`app-saga-data-inspector`): inputs `state: SnapshotState`, `afterJson`, `beforeJson`, `beforeLabel`, `message`.

```html
@if (state() !== 'recorded') { <p class="insp-note">{{ note() }}</p> } @else {
  <div role="group" aria-label="Data view">
    <button type="button" [attr.aria-pressed]="view() === 'changes'" [disabled]="!diff()" (click)="view.set('changes')">Changes</button>
    <!-- same pattern: Full state, Message (when message() is set), Copy JSON -->
  </div>
  @if (view() === 'changes' && diff()) {
    <p class="insp-note">Compared with {{ beforeLabel() }}</p>
    <table class="insp-diff">
      <thead><tr><th>Field</th><th>Before</th><th>After</th></tr></thead>
      <tbody>@for (c of business(); track c.path) {
        <tr><th scope="row" class="mono">{{ marker(c) }} {{ c.path }}</th>
          <td class="mono">{{ preview(c.before) }}</td><td class="mono">{{ preview(c.after) }}</td></tr> }</tbody>
    </table>
    <p class="insp-note muted">Engine bookkeeping also changed: {{ bookkeepingText() }}</p>
  } @else { <pre class="json-block" tabindex="0">{{ view() === 'message' ? messagePretty() : pretty() }}</pre> }
}
```

- `view` is a `linkedSignal` that defaults to Changes when a baseline exists.
- `marker(c)` prints `+`, `−` or `~` with screen-reader text, so colour is not the only signal.
- Parsing and diffing sit in `computed`, so they run only for open inspectors.
- Copy JSON copies the raw payload and hides when `navigator.clipboard` is missing.

| `state` | Text of `note()` |
|---|---|
| `omitted` | The state was too large to snapshot at this step (N bytes, limit L). |
| `withheld` | Saga data is hidden for your role. It needs the `sagas.data` permission. |
| `not-persisted` | This step did not persist a new state, so the data is unchanged. |
| `pending` | Not recorded yet. The step may still be committing; this view refreshes by itself. |
| `missing` | No snapshot was recorded for this step: the saga ran before snapshots existed, they are switched off, or the step lost a concurrent update and never committed. |

**`SagaDataOverview`** (`app-saga-data-overview`): inputs `history`, `currentJson`, `summary`, `canViewData`, `view`; output `viewChange`.

```html
<section data-tour="detail-data">
  <div role="group" aria-label="Saga data">
    <span class="micro-label">Saga data</span>
    <button type="button" class="btn-quiet" [disabled]="!canViewData()" [attr.aria-pressed]="view() === 'start'"
            (click)="toggle('start')">At start</button>
    <!-- same pattern: 'end' (text "Current" until the status is terminal), 'compare' (disabled without a snapshot) -->
    @if (!canViewData()) { <span class="muted">Saga data is hidden for your role. It needs the sagas.data permission.</span> }
  </div>
  @if (canViewData() && view(); as v) { <div class="data-panel"> … </div> }
</section>
```

- **At start**: the initiating message (`SagaStarted.payloadJson` through `prettyJson`) and "State after step N", taken from `history.firstRecorded`.
- **At end**: `detail.dataJson` through the inspector.
- **Compare**: the inspector with `beforeJson` set to the first recorded snapshot and `afterJson` to `detail.dataJson`.

Without `sagas.data` these buttons are disabled beside the sentence above, the timeline shows no Data toggles and one hint line, and the rows and the map are unchanged.

**`SagaMap`**

```ts
readonly focusSequence = input<number | null>(null);
readonly focusCleared = output<void>();
readonly timelineRequested = output<number>();
private readonly activeFocus = linkedSignal(() => this.focusSequence());
readonly focus = computed(() => resolveFocusIndex(this.map().events, this.activeFocus()), { equal: sameFocus });
constructor() { effect(() => { const f = this.focus(); if (f) untracked(() => this.scrubTo(f.index)); }); }
```

- `saga-map-layout.ts` gains `resolveFocusIndex(events, sequence): { index; exact; requested } | null`. It returns the event with an equal `sequenceNumber`, otherwise the last one with a smaller number, otherwise index 0.
- The effect runs before the first render, so a recreated map (`saga-detail.html:109-114`) opens on the entry. A refreshed map re-resolves, which repairs a fallback once the map catches up.
- `play`, `restart`, `stepForward` and `onScrubInput` (`saga-map.ts:127-162`) first release the focus with `activeFocus.set(null)` and `focusCleared.emit()`. `scrubTo` does not, because the effect uses it.
- `computeNodeStates` (`saga-map-layout.ts:205-230`) treats an event with neither `edgeId` nor `nodeId` as touching the `Orchestrator` node. `StepSucceeded` and the other plain events (`SagaMapBuilder.cs:184-190`) therefore light it as active.
- A banner with `role="status"` shows while focused: "As of entry #12 of 34: StepSucceeded, recorded at 14:03:07.140 (+1.224 s)". A plain event adds "Nothing moved between services at this entry, so OrderSaga is highlighted". A fallback adds "Entry 57 is not on the map yet; showing the closest earlier entry". It ends with a button, "Back to this entry in the timeline".
- The status line (`saga-map.html:76-83`) gains the recorded-at time.

**`SagaDetail`** keeps loading, hub subscriptions, retry and URL state.

```ts
type Tab = 'map' | 'timeline';
readonly focusedSequence = signal<number | null>(null);
readonly dataView = signal<'start' | 'end' | 'compare' | null>(null);
readonly timelineLoaded = signal(false); readonly timelineError = signal(false); readonly mapError = signal(false);
readonly history = computed(() => foldTimeline(this.timeline()));
showOnMap(sequence: number): void;      // tab 'map', focus, syncUrl(); reloads the map when it lacks the sequence
showInTimeline(sequence: number): void; // tab 'timeline', focus, syncUrl()
clearFocus(): void;                     // focus null, syncUrl(true)
```

- **Summary card** (`saga-detail.html:31-34`): labels become `Created (UTC+02:00)` and `Updated (UTC+02:00)`, with values in `<app-local-time format="datetime">` (`MMM d, y, HH:mm:ss`, UTC on hover).
- **URL.** `route.queryParamMap` is subscribed next to `paramMap` (`saga-detail.ts:58`) and validated like `readFiltersFromUrl` (`saga-list.ts:104-130`): `tab` accepts only `timeline`, `entry` only a positive safe integer, `data` only its three values. Setters update the signals first, then call `router.navigate([], { relativeTo, queryParams: { tab, entry, data }, queryParamsHandling: 'merge', replaceUrl })`, writing defaults as `null` (`saga-list.ts:135-147`). Tab and entry changes push history, so Back returns to the timeline. Clearing the focus replaces.
- **Live.** `sagaUpdated$` (89-102) still patches the summary at once, then feeds a `Subject` piped through `auditTime(250)`. One refresh runs `loadTimeline`, `loadMap`, `loadRelated`, `loadChildren` and a new `refreshDetail()`. `refreshDetail()` never touches `loading`, and keeps the summary with the higher `version` when responses cross.
- **Pushed entries.** `timelineEntryAdded$` (103-107) feeds the same subject instead of appending. Pushed entries carry sequence 0 (`SagaLogEntry.cs:40`) and, under decision D, no payload.
- **Late snapshot.** After a push-triggered refresh whose last step has an outcome but no snapshot, one follow-up `loadTimeline` runs after 1500 ms.
- **Errors.** `loadTimeline` and `loadMap` get error handlers. With nothing loaded, the tab shows `.banner--error` and a Try again button. With stale content, it shows `.banner--warning` above that content. The reconnect branch (74-88) also retries these two.
- A saga change resets `timeline`, `map` and the flags.

### 4. Styles

- `src/styles.scss` is global and outside the component budget. It gains `.sr-only`, `.muted` (dead today in `saga-detail.html:18,46,63,78`), `.micro-label`, `.json-block` (a mono `<pre>`) and `.btn-quiet`.
- `saga-detail.scss` loses lines 187-246 and gains nothing, leaving about 2.4 kB.
- New stylesheets stay under these ceilings: timeline 2.5 kB, inspector 2 kB, overview 1 kB. `saga-map.scss` grows by about 0.5 kB.

## Files

Create, under `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/`:

- `util/state-json.ts`, `util/json-diff.ts`, `util/time-format.ts`, `util/entry-type-label.ts`, `util/saga-transitions.ts`, each with a `.spec.ts`: the pure modules of section 2.
- `testing/timeline-fixtures.ts`: entry and step builders for the new specs.
- `components/local-time/local-time.ts` and `.spec.ts`.
- `components/saga-timeline/saga-timeline.ts`, `.html`, `.scss`, `.spec.ts`.
- `components/saga-data-inspector/saga-data-inspector.ts`, `.html`, `.scss`, `.spec.ts`.
- `components/saga-data-overview/saga-data-overview.ts`, `.html`, `.scss`, `.spec.ts`.

Modify:

- `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/models/saga.model.ts`: entry type and three optional fields.
- `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-detail/saga-detail.ts`, `.html`, `.scss`, `.spec.ts`: tabs, URL state, live refresh, errors, children wired in.
- `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/components/saga-map/saga-map.ts`, `.html`, `.scss`, `.spec.ts`: focus input, banner, outputs.
- `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/components/saga-map/saga-map-layout.ts`, `.spec.ts`: `resolveFocusIndex`, orchestrator attribution.
- `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/styles.scss`: five shared classes.

## Tests

**`saga-detail.spec.ts`, existing tests that change**

- 110-115, 193, 254: each `ActivatedRoute` stub gains `queryParamMap: of(convertToParamMap({}))`. `setup()` takes an optional query map and spies `Router.navigate`, following `saga-list.spec.ts:611-612`.
- 388-392: deleted, because the method is gone. 376-386 is unchanged and passes because the row keeps the class `entry-type`.
- 394-411: moved to `state-json.spec.ts`, against `formatStateJson`.
- 413-420: `setTab('data')` stops compiling. It becomes `setTab('timeline')` plus a `navigate` assertion.
- 509-516, 587-594, 598-605, 607-614: `vi.useFakeTimers()` and `vi.advanceTimersByTime(250)` before the call-count assertion, with `afterEach(() => vi.useRealTimers())`.
- 561-568: rewritten. The push leaves `timeline()` alone and causes one `getTimeline` call after the delay. 552-559 and 570-577 also assert no extra call.

**`saga-detail.spec.ts`, new cases**

- Restores `?tab=timeline&entry=5&data=end`; ignores `tab=bogus`, `entry=0` and `entry=abc`.
- Clicking a row calls `navigate` with `{ tab: null, entry: 7 }` and passes `focusSequence` to `app-saga-map`.
- `focusCleared` navigates with `replaceUrl`; `timelineRequested` selects the timeline tab.
- Exactly two tab buttons and no `.data-json`; a `StatePersisted` entry renders no row.
- A live update re-fetches the detail without `loading` and updates `dataJson`; three pushes within 250 ms cause one refresh; an older-version detail response does not regress the summary.
- Map and timeline failures show the error, and Try again reloads; the follow-up fetch fires once.

**`saga-map.spec.ts`** (new cases, none changed): `focusSequence` positions on an exact match, falls back to the nearest preceding event, and re-resolves when `map` is replaced. Banner text for exact, fallback and plain events. `play()` and scrubber input emit `focusCleared`, and a later map refresh does not re-pin. The back button emits `timelineRequested`.

**`saga-map-layout.spec.ts`**: `resolveFocusIndex` for null, empty, exact, between, before the first and after the last. A plain current event marks the orchestrator `active`; a failed orchestrator stays failed.

**`saga-transitions.spec.ts`**, one case per rule:

- happy path with `SagaStarted` merged; two steps and their baselines; snapshots never in `rows`;
- no snapshots; both markers; withheld; an in-flight final step and `pending`;
- lost race; interleaved handlers attributed by `messageId` and `causationId`;
- a `.CallHttp` hop inside a message step and inside a timeout;
- handled, unhandled and interleaved timeouts;
- retry with and without a reset; an in-process retry reusing the failed id;
- a dead-letter step; `UnexpectedEvent` before the start; a deferred publish after the snapshot;
- sparse sequence numbers; input order kept.

**Other new specs**

- `json-diff`: equal, primitive, added, removed, nested, arrays, type change, quoted key, truncation.
- `state-json`: the three moved tests, enum mapping, out-of-range integers, nested `Kind` untouched, both markers.
- `time-format`: offset forms, exact UTC text, an explicit `+0200`, the day prefix, invalid input, zone labels.
- `saga-timeline`: label, title and offset; row click and Enter emit; the toggle sets `aria-expanded`; no toggles without `canViewData`; the focus class.
- `saga-data-inspector`: each state text; the default view; a status change shown as names; the bookkeeping line; copy uses the raw text.
- `saga-data-overview`: three views; Current versus At end; Compare disabled; withheld.
- `entry-type-label`, `local-time`.

## Commit sequence

1. Pure helpers `state-json`, `json-diff`, `time-format`, `entry-type-label` with specs; `prettyDataJson` and `entryTypeLabel` delegate to them.
2. Model fields, `StatePersisted`, `saga-transitions` and the fixtures.
3. Improvement 2: `LocalTime`, `SagaTimeline` (steps and labelled times, rows not yet clickable), summary card times, the global classes, timeline styles out of `saga-detail.scss`.
4. Improvement 3: map focus, banner and orchestrator highlight; row buttons; `Router` and query parameters; spec route stubs.
5. `SagaDataInspector` and the per-step Data toggle.
6. `SagaDataOverview`; the Data tab, `prettyDataJson` and `.data-json` removed; the `data` query parameter.
7. Live refresh: audit window, `refreshDetail`, invalidation instead of append, follow-up fetch, load errors.
8. `canViewData` wired to the session service, once the authentication assignment's SPA service exists. Until then it is a constant `true`.

## Verification

Nothing below was run in this read-only pass.

- `cd dashboard-web && npx ng test --watch=false`: green, and the 148-test baseline grows.
- `npx ng build`: no `anyComponentStyle` warning and no `initial` warning. Record the `main-*.js` size.
- `npm audit --audit-level=low` passes and `git diff package-lock.json` is empty.
- Live, on `docker compose up -d --build` with the UI at `http://localhost:4200` (or `npx ng serve` until the packaging assignment lands):
  - a `LoyaltyLookupSaga` shows two steps, with the `POST …/loyalty/lookup` hop and its reply inside step 1;
  - every row reads "Recorded at …", and hovering shows UTC;
  - clicking a `StepSucceeded` row gives `?entry=N`, the banner and a highlighted orchestrator, and Back returns to the timeline;
  - Data on step 2 lists the changed fields; At start, At end and Compare open under the summary card;
  - retrying a failed `OrderSaga` adds a "Manual retry" step within about 1.5 s, and the Current data changes without a reload;
  - reloading `…?tab=timeline&entry=N` restores the highlighted row.
- With `MaxStateSnapshotBytes` set low on the sample host, a step shows the `omitted` text.

## Cross-assignment contracts

- **Engine and API (decision C).** `StatePersisted` arrives as `entryType: "StatePersisted"`. Its `messageId` equals the step's `MessageReceived.messageId` for step success, step failure and delivery exhaustion, and is null for the timeout snapshot and the retry-reset snapshot. If that does not hold, the fold degrades to "the step touched last". The retry endpoint appends it right after `ManualRetryRequested`, before the redrive publish. Proposed oversize marker: `{"$vsagaStateOmitted":true,"bytes":N,"limit":L}`.
- **Map.** `SagaMapBuilder` must skip `StatePersisted`, so `#i/n` matches the rows shown.
- **Ordering.** Commits 2 and 3 should ship no later than the engine change; an older SPA would list `StatePersisted` as a row.
- **Access (decision D).** Without `sagas.data` the timeline keeps its entries with `payloadJson` null or absent, and `dataJson` is null. This design needs a root service with `can(permission, sagaType): boolean` backed by a signal. The retry-row gate (`saga-detail.html:85`) belongs to that assignment and should land after commit 6.
- **Hub.** The SPA no longer reads the body of `TimelineEntryAdded`.
- **Retry attribution.** A `ManualRetryRequested.sourceService` of `dashboard:<name>` renders as "Requested by <name>".
- **Routes.** The detail route owns the query parameters `tab`, `entry` and `data`. A login `returnUrl` must keep the query string.
- **Tour anchors added here:** `detail-summary`, `detail-data`, `detail-tabs`, `detail-timeline`, `detail-step-data` (first step only), `detail-map`, `map-focus`, `detail-retry`.
- **Global classes added to `styles.scss`:** `.sr-only`, `.muted`, `.micro-label`, `.json-block`, `.btn-quiet`.
- **File ownership:** `saga-detail.*`, `saga-map*`, the new component and `util` folders, and the `SagaLogEntry` and `SagaEntryType` parts of `saga.model.ts`.
- **Docs (F):** "three tabs" in `docs/dashboard.md:131,152` and `dashboard-web/README.md:4,72` becomes two tabs plus the data bar.

## Objections

One refinement, not a blocker. Read literally, "a transition starts at … MessageReceived" splits steps in the default demo:

- `.CallHttp` logs a mid-step `MessageReceived` under a fresh id (`HttpCallDefinition.cs:60-62`), and `LoyaltyLookupSaga.cs:71` and `MixedFulfilmentSaga.cs:98,139` use it.
- The engine logs `SagaStarted` and `MessageReceived` for the same message.

The fold therefore merges those two, keeps reply hops inside their step, and adds a `delivery` step for a dead-letter that never reached `MessageReceived`. The four listed triggers stay the only normal step starts.

### Critical Files for Implementation

- `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-detail/saga-detail.ts`
- `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-detail/saga-detail.html`
- `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/components/saga-map/saga-map.ts`
- `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/components/saga-map/saga-map-layout.ts`
- `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-detail/saga-detail.spec.ts`

## Risks
- Timeline payload growth: every live refresh downloads the whole timeline with every snapshot (up to MaxStateSnapshotBytes each), and the map endpoint re-reads it on the server. Mitigation: the 250 ms audit window collapses the duplicate list-group and instance-group pushes into one refresh, and snapshots are parsed only for open inspectors. If payloads prove heavy, a later change adds a snapshots=false query flag plus a lazy per-entry fetch; that needs a new store method, so it is outside decision C.
- Step attribution depends on ids the engine assignment must stamp (StatePersisted.messageId equal to the step's inbound id, null for timeout and retry-reset snapshots). Mitigation: the fold falls back to the step touched last, which is correct for every non-interleaved timeline; the contract is listed; saga-transitions.spec.ts covers both the keyed and the fallback path.
- Under truly interleaved handlers, entries that carry no id (TimeoutScheduled, SagaCompleted, the Compensation entries) can land in the neighbouring step. Mitigation: outcomes and snapshots, which drive the data inspector, are attributed by id; each row keeps its global ordinal and time, so the real order stays readable; the limitation goes into the user guide.
- A refresh can land between the engine's persist commit and its StatePersisted append, leaving the final step without a snapshot until the next change, which for a finished saga never comes. Mitigation: one follow-up timeline fetch 1500 ms after a push-triggered refresh, the pending wording, and the Current view, which always shows the stored blob.
- Existing detail specs assert a synchronous re-fetch; moving to auditTime needs fake timers, which can leak between tests. Mitigation: only the tests that push use vi.useFakeTimers, an afterEach restores real timers, and the delay is an exported constant.
- Budgets: three new stylesheets and roughly 15 kB of script (an estimate, not measured) draw on the 96 kB of initial headroom shared with the authentication and guidance work. Mitigation: a ceiling per stylesheet, shared rules in styles.scss, and ng build in verification. If the initial budget gets tight, the inspector and overview can load behind @defer (on interaction).
- Local-time assertions depend on the machine's time zone (CI runs in UTC, developer machines do not). Mitigation: the pure formatters take an explicit timezone, and component specs assert the datetime attribute and the UTC title, not the local text.
- afterRenderEffect and scrollIntoView under jsdom: scrollIntoView is not implemented there, and after-render hooks may run after detectChanges returns. Mitigation: an optional call, assertions after fixture.whenStable(), and an effect that acts only when focusedSequence changes.
- URL state can loop or flood history. Mitigation: setters write signals first and the queryParamMap echo is a no-op by value equality; only user tab and entry changes push history; clearing the focus uses replaceUrl; every parameter is validated before use.
- Re-serialised state loses precision for integers above 2^53 and drops trailing zeros in decimals (already true of prettyDataJson today). Mitigation: Copy JSON copies the stored text unchanged, and the user guide says so.
- Merge conflicts: the authentication and guidance assignments also touch saga-detail.html, the detail spec's setup() and styles.scss. Mitigation: ownership and ordering are stated in the contracts, and child components take canViewData as an input, so one line of SagaDetail depends on the session service.
- If the API emits StatePersisted before the SPA hides it, the current timeline lists it as a plain row. Mitigation: ship commits 2 and 3 no later than the engine change; the effect is cosmetic.
- The index-wise array diff reports an insertion at the head of a list as many changes. Mitigation: the 200-change cap with a truncation notice, and the Full state view one click away.
- The session may say data is allowed while the API withholds it (stale session, or an API-key role without sagas.data). Mitigation: the fold's withheld state is driven by the data itself, so the inspector still explains the missing payload.

## Open questions

