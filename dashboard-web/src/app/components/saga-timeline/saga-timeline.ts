import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  effect,
  inject,
  input,
  model,
  output,
  signal,
} from '@angular/core';
import { SagaLogEntry } from '../../models/saga.model';
import { entryTypeLabel } from '../../util/entry-type-label';
import {
  PENDING_SNAPSHOT_MS,
  SagaHistory,
  SagaTransition,
  SnapshotState,
  TimelineRow,
  effectiveSnapshotState,
  stepContaining,
} from '../../util/saga-transitions';
import { RecordedAt, formatRecordedAt, timezoneLabel, toMillis } from '../../util/time-format';
import { SagaDataInspector } from '../saga-data-inspector/saga-data-inspector';

/** Past the pending window, so the re-check after it never lands a millisecond early. */
const PENDING_RECHECK_SLACK_MS = 50;

const OUTCOME_LABELS: Record<Exclude<SagaTransition['outcome'], 'in-flight' | 'requested'>, string> = {
  succeeded: 'succeeded',
  failed: 'failed',
  unhandled: 'not handled',
  exhausted: 'dead-lettered',
};

/**
 * The saga's timeline as steps (see util/saga-transitions): each step names what started it and
 * how it ended, and lists its entries with the time the engine recorded each one, in the viewer's
 * local time, UTC on hover and the offset from the saga's first entry. Snapshots are never rows.
 *
 * Every row is a native button that asks for the map as of that entry, and a step's title asks for
 * the map as of the step's last entry, which is the state after the step.
 *
 * Given the retry plan's entries, the step the saga failed in reads "Failed here" in error styling,
 * and the step a retry would re-run, when it is another one (a timeout), "Re-run starts here".
 *
 * With `canViewData`, each step header has a Data toggle that opens the step's data inspector below
 * its rows. Which steps are open is `openKeys` (step keys, stable across refreshes), a model the
 * page holds, so open inspectors survive a refresh and a trip to the map and back.
 */
@Component({
  selector: 'app-saga-timeline',
  imports: [SagaDataInspector],
  templateUrl: './saga-timeline.html',
  styleUrl: './saga-timeline.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
  // The spaces between a row's parts are part of its text: without them it reads (and copies, and
  // is announced) as "#4MessageReceivedRecorded at14:03:07.140".
  preserveWhitespaces: true,
})
export class SagaTimeline {
  readonly history = input.required<SagaHistory>();
  /** The sequence number of the entry to highlight. */
  readonly focusedSequence = input<number | null>(null);
  /** Whether the viewer may see saga data; the per-step data toggle depends on it. */
  readonly canViewData = input(true);
  /** Whether the saga is still running, so its final step without an outcome is in progress. */
  readonly live = input(false);
  /** The keys of the steps whose data inspector is open. */
  readonly openKeys = model<ReadonlySet<number>>(new Set());
  /** The retry plan's failure entry: its step is marked "Failed here". */
  readonly failureSequence = input<number | null>(null);
  /** The retry plan's replayed entry: its step is marked "Re-run starts here" when it is another step. */
  readonly replaySequence = input<number | null>(null);

  /** The viewer picked an entry to see on the map; carries its sequence number. */
  readonly entrySelected = output<number>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  /** The focus this view last scrolled to, so a refresh of the same focus never steals focus. */
  private scrolledTo: number | null = null;
  /** Bumped when a pending snapshot's window runs out, so its step reads missing without a refresh. */
  private readonly tick = signal(0);

  /** The clock the snapshot states are judged by: read again on every input change and every tick. */
  private readonly now = computed(() => {
    this.history();
    this.live();
    this.tick();
    return Date.now();
  });

  /** Each step's snapshot state as of `now` (see effectiveSnapshotState), by step key. */
  readonly dataStates = computed(() => {
    const now = this.now();
    const live = this.live();
    const states = new Map<number, SnapshotState>();
    for (const step of this.history().transitions) {
      states.set(step.key, effectiveSnapshotState(step, live, now));
    }
    return states;
  });

  constructor() {
    // A pending step stops being pending once its newest row is PENDING_SNAPSHOT_MS old: look again
    // then, since nothing else may happen to this saga to trigger a refresh.
    effect((onCleanup) => {
      const now = this.now();
      let due: number | null = null;
      for (const step of this.history().transitions) {
        if (this.dataStates().get(step.key) !== 'pending') continue;
        const at = toMillis(step.lastOccurredAtUtc) ?? now;
        const wait = at + PENDING_SNAPSHOT_MS - now + PENDING_RECHECK_SLACK_MS;
        due = due === null ? wait : Math.min(due, wait);
      }
      if (due === null) return;
      const timer = setTimeout(() => this.tick.update((n) => n + 1), Math.max(0, due));
      onCleanup(() => clearTimeout(timer));
    });

    // Once per new focus, and only once its row exists (the history may arrive after the focus).
    afterRenderEffect(() => {
      const sequence = this.focusedSequence();
      this.history();
      if (sequence === null) {
        this.scrolledTo = null;
        return;
      }
      if (sequence === this.scrolledTo) return;

      const row = this.host.nativeElement.querySelector<HTMLElement>(`.tl-row[data-seq="${sequence}"]`);
      if (!row) return;
      this.scrolledTo = sequence;
      // scrollIntoView is missing in some environments (jsdom); focusing still works there.
      row.scrollIntoView?.({ block: 'center' });
      row.focus({ preventScroll: true });
    });
  }

  /** Every row's labelled time, measured from the saga's first entry. */
  readonly times = computed(() => {
    const history = this.history();
    const times = new Map<TimelineRow, RecordedAt>();
    for (const step of history.transitions) {
      for (const row of step.rows) {
        times.set(row, formatRecordedAt(row.entry.occurredAtUtc, history.firstOccurredAtUtc));
      }
    }
    return times;
  });

  /** The key of the step the saga failed in; null when there is no plan or it names no row. */
  readonly failedKey = computed(() => stepContaining(this.history(), this.failureSequence())?.key ?? null);

  /**
   * The key of the step a retry re-runs, when it is not the failed step itself: for a timeout, the
   * step that entered the timed-out state.
   */
  readonly replayKey = computed(() => {
    const key = stepContaining(this.history(), this.replaySequence())?.key ?? null;
    return key === this.failedKey() ? null : key;
  });

  /** The viewer's zone as of the saga's first entry: `UTC+02:00`. */
  readonly zone = computed(() => {
    const first = toMillis(this.history().firstOccurredAtUtc);
    return timezoneLabel(first === null ? new Date() : new Date(first));
  });

  label(entryType: string): string {
    return entryTypeLabel(entryType);
  }

  at(row: TimelineRow): RecordedAt {
    return this.times().get(row) ?? formatRecordedAt(row.entry.occurredAtUtc, null);
  }

  isOpen(key: number): boolean {
    return this.openKeys().has(key);
  }

  /** Opens or closes a step's data inspector. */
  toggleData(key: number): void {
    this.openKeys.update((open) => {
      const next = new Set(open);
      if (!next.delete(key)) next.add(key);
      return next;
    });
  }

  dataState(step: SagaTransition): SnapshotState {
    return this.dataStates().get(step.key) ?? step.snapshotState;
  }

  /** Names the snapshot a step's changes are measured against. */
  baselineLabel(step: SagaTransition): string | null {
    const baseline = step.baseline;
    return baseline ? `the state after step ${baseline.ordinal} (${baseline.title})` : null;
  }

  /** The sequence number of the step's last row, which the step title jumps to; null without rows. */
  lastSequence(step: SagaTransition): number | null {
    return step.rows.at(-1)?.entry.sequenceNumber ?? null;
  }

  outcomeLabel(step: SagaTransition): string {
    switch (step.outcome) {
      case 'requested':
        return step.actor ? `requested by ${step.actor}` : 'requested';
      case 'in-flight':
        return step.isLast && this.live() ? 'in progress' : 'no outcome recorded';
      case 'exhausted':
        // A step the dead letter opened is already titled "<type> dead-lettered": say instead that
        // the saga never got to handle the message.
        return step.trigger === 'delivery' ? 'never handled' : OUTCOME_LABELS.exhausted;
      default:
        return OUTCOME_LABELS[step.outcome];
    }
  }

  /** Where an outbound message went, or where an inbound one came from. */
  service(entry: SagaLogEntry): string | null {
    if (entry.destinationService) return `to ${entry.destinationService}`;
    if (entry.sourceService) return `from ${entry.sourceService}`;
    return null;
  }
}
