import { Component, ElementRef, afterRenderEffect, computed, inject, input, output } from '@angular/core';
import { SagaLogEntry } from '../../models/saga.model';
import { entryTypeLabel } from '../../util/entry-type-label';
import { SagaHistory, SagaTransition, TimelineRow } from '../../util/saga-transitions';
import { RecordedAt, formatRecordedAt, timezoneLabel, toMillis } from '../../util/time-format';

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
 */
@Component({
  selector: 'app-saga-timeline',
  templateUrl: './saga-timeline.html',
  styleUrl: './saga-timeline.scss',
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

  /** The viewer picked an entry to see on the map; carries its sequence number. */
  readonly entrySelected = output<number>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  /** The focus this view last scrolled to, so a refresh of the same focus never steals focus. */
  private scrolledTo: number | null = null;

  constructor() {
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
