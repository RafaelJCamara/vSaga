import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { SagaStatus, SagaSummary } from '../../models/saga.model';
import { SagaHistory } from '../../util/saga-transitions';
import { prettyJson } from '../../util/state-json';
import { SagaDataInspector } from '../saga-data-inspector/saga-data-inspector';

/** The three ways the "Saga data" bar looks at a saga's data. */
export type DataView = 'start' | 'end' | 'compare';

/** Every view, in bar order: what the detail page accepts in its `data` query parameter. */
export const DATA_VIEWS: readonly DataView[] = ['start', 'end', 'compare'];

/**
 * Whether Compare has both sides to compare: a recorded state snapshot in the history and the stored state.
 * Pure; the bar enables Compare with it, and the detail page asks it too, to announce the data area only for
 * a view that is really on screen.
 */
export function canCompareData(history: SagaHistory, currentJson: string | null | undefined): boolean {
  return history.firstRecorded?.snapshotJson != null && currentJson != null;
}

/** The view on screen: none without the permission, and none for a Compare with nothing to compare. Pure. */
export function visibleDataView(
  view: DataView | null,
  canViewData: boolean,
  canCompare: boolean,
): DataView | null {
  if (!canViewData) return null;
  return view === 'compare' && !canCompare ? null : view;
}

/** Statuses a saga can still move on from; its stored state is "Current" rather than "At end". */
const LIVE_STATUSES: ReadonlySet<SagaStatus> = new Set<SagaStatus>(['Running', 'Compensating']);

/**
 * The "Saga data" bar under the detail page's summary card: the saga's data at the start, at the
 * end (or now, while it can still change) and the two compared.
 *
 * - At start: the message that started the saga and the first recorded state snapshot, which is
 *   the state after the step that recorded it (step 1 unless that one recorded none).
 * - At end (Current until the status is terminal): the stored state, `detail.dataJson`, which never
 *   depends on a snapshot having been recorded.
 * - Compare: the stored state against that first recorded snapshot; disabled, with a title naming
 *   the missing side, without either.
 *
 * Without `canViewData` the buttons stay, disabled, beside a sentence naming the permission, so the
 * viewer learns the data exists; each button is described by that sentence, since a disabled button takes
 * no focus and a screen reader would otherwise say only that it is dimmed. The open view is the page's
 * (it lives in the URL): a click asks for it through `viewChange`, and clicking the open one asks for none.
 */
@Component({
  selector: 'app-saga-data-overview',
  imports: [SagaDataInspector],
  templateUrl: './saga-data-overview.html',
  styleUrl: './saga-data-overview.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
  // The spaces between a caption's label and its value are part of its text.
  preserveWhitespaces: true,
})
export class SagaDataOverview {
  readonly history = input.required<SagaHistory>();
  /** The saga's stored state (`detail.dataJson`); null when nothing is stored or it was withheld. */
  readonly currentJson = input<string | null>(null);
  readonly summary = input.required<SagaSummary>();
  /** Whether the viewer may see saga data. */
  readonly canViewData = input(true);
  /** The view the page asks for; one that cannot be shown right now shows nothing. */
  readonly view = input<DataView | null>(null);
  /**
   * Whether `history` is the saga's loaded timeline. The page fetches the timeline beside the
   * detail, so until it arrives an empty history means "not here yet", not "nothing recorded":
   * At start says it is loading and Compare gives no reason for being disabled.
   */
  readonly historyLoaded = input(true);

  /** The viewer picked a view, or closed the open one (null). */
  readonly viewChange = output<DataView | null>();

  /** Whether the saga is done moving, so its stored state is the state at its end. */
  readonly terminal = computed(() => !LIVE_STATUSES.has(this.summary().status));

  /** The first recorded state snapshot: the "at start" state and the baseline of Compare. */
  readonly first = computed(() => {
    const step = this.history().firstRecorded;
    return step?.snapshotJson == null ? null : { json: step.snapshotJson, ordinal: step.ordinal, title: step.title };
  });

  /** Compare needs both sides: a recorded snapshot and the stored state. */
  readonly canCompare = computed(() => canCompareData(this.history(), this.currentJson()));

  /** Why Compare is disabled, for its title: the missing side, or null when nothing is missing yet. */
  readonly compareBlockedReason = computed(() => {
    if (!this.canViewData() || this.canCompare() || !this.historyLoaded()) return null;
    return this.first() === null
      ? 'No state snapshot was recorded for this saga, so there is nothing to compare with.'
      : 'No state is stored for this saga, so there is nothing to compare with.';
  });

  /** The view on screen: none without the permission, and none for a Compare with nothing to compare. */
  readonly shown = computed<DataView | null>(() =>
    visibleDataView(this.view(), this.canViewData(), this.canCompare()),
  );

  readonly initiatingPretty = computed(() => prettyJson(this.history().initiating?.json));

  /** "the state after step 1 (OrderSubmitted)": what Compare measures against. */
  readonly baselineLabel = computed(() => {
    const first = this.first();
    return first ? `the state after step ${first.ordinal} (${first.title})` : null;
  });

  /** Opens a view, or closes it when it is the one open. */
  toggle(view: DataView): void {
    this.viewChange.emit(this.shown() === view ? null : view);
  }
}
