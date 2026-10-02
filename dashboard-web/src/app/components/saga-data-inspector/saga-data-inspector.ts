import { Component, computed, input, linkedSignal } from '@angular/core';
import { JsonChange, diffJson, previewValue } from '../../util/json-diff';
import { SnapshotState } from '../../util/saga-transitions';
import { formatStateJson, parseStateJson, prettyJson } from '../../util/state-json';

export type InspectorView = 'changes' | 'state' | 'message';

/** Top-level fields the engine rewrites on every persist: listed apart from the saga's own changes. */
const BOOKKEEPING: ReadonlySet<string> = new Set(['Version', 'UpdatedAtUtc']);

const MARKERS: Record<JsonChange['kind'], { symbol: string; text: string }> = {
  added: { symbol: '+', text: 'Added' },
  removed: { symbol: '−', text: 'Removed' },
  changed: { symbol: '~', text: 'Changed' },
};

const NOTES: Record<Exclude<SnapshotState, 'recorded' | 'omitted'>, string> = {
  withheld: 'Saga data is hidden for your role. It needs the sagas.data permission.',
  'not-persisted': 'This step did not persist a new state, so the data is unchanged.',
  pending: 'Not recorded yet. The step may still be committing; this view refreshes by itself.',
  missing:
    'No snapshot was recorded for this step: the saga ran before snapshots existed, they are ' +
    'switched off, or the step lost a concurrent update and never committed.',
};

/** A bookkeeping value without JSON quotes around a string: `2026-01-01T00:00:01Z`, `4`. */
function plain(value: unknown): string {
  return typeof value === 'string' ? value : previewValue(value);
}

/**
 * The saga's data after one step: what the step changed against the nearest earlier recorded
 * snapshot (named, since a step in between may have recorded none), the full state, and the message
 * that ran the step where its payload was recorded. A step with no usable snapshot gets a note
 * saying why instead.
 *
 * Parsing and diffing sit in `computed`, so they run only while an inspector is open, and the views
 * show the stored text re-serialised (with Kind and Status as names); Copy JSON copies the stored
 * text unchanged, so precision the browser would lose (integers past 2^53, trailing zeros) survives.
 */
@Component({
  selector: 'app-saga-data-inspector',
  templateUrl: './saga-data-inspector.html',
  styleUrl: './saga-data-inspector.scss',
  // The spaces between a change's marker and its path, and around "→", are part of the text.
  preserveWhitespaces: true,
})
export class SagaDataInspector {
  /** What the step left behind (see saga-transitions); only `recorded` has a state to show. */
  readonly state = input.required<SnapshotState>();
  /** The step's snapshot payload: the state, or the omission marker that stands in for it. */
  readonly afterJson = input<string | null>(null);
  /** The nearest earlier recorded snapshot, the "before" of the changes; null when there is none. */
  readonly beforeJson = input<string | null>(null);
  /** Names that earlier snapshot: "the state after step 2 (PaymentCaptured)". */
  readonly beforeLabel = input<string | null>(null);
  /** The payload of the message that ran the step, where one was recorded. */
  readonly message = input<{ label: string; json: string } | null>(null);
  /** What the changes view says when none of the saga's own fields differ from the baseline. */
  readonly unchangedNote = input("None of the saga's own fields changed in this step.");

  /** Clipboard access is missing on plain HTTP origins other than localhost, and in old browsers. */
  readonly canCopy = typeof navigator !== 'undefined' && navigator.clipboard != null;

  /** The changes against the baseline; null when either side is not a readable state. */
  readonly diff = computed(() => {
    if (this.state() !== 'recorded' || this.beforeJson() == null) return null;
    const before = parseStateJson(this.beforeJson());
    const after = parseStateJson(this.afterJson());
    if (before.kind !== 'value' || after.kind !== 'value') return null;
    return diffJson(before.value, after.value);
  });

  /** Changes by default when there is something to compare with, else the full state. */
  readonly view = linkedSignal<InspectorView>(() => (this.diff() ? 'changes' : 'state'));

  /** "Copied" for the last copy, until the view changes. */
  readonly copied = linkedSignal(() => {
    this.view();
    return false;
  });

  /** The changes to the saga's own fields. */
  readonly business = computed(() =>
    (this.diff()?.changes ?? []).filter((change) => !BOOKKEEPING.has(change.path)),
  );

  /** `Version 3 → 4, UpdatedAtUtc … → …`; '' when the engine's bookkeeping did not change. */
  readonly bookkeepingText = computed(() =>
    (this.diff()?.changes ?? [])
      .filter((change) => BOOKKEEPING.has(change.path))
      .map((change) => `${change.path} ${plain(change.before)} → ${plain(change.after)}`)
      .join(', '),
  );

  readonly pretty = computed(() => formatStateJson(this.afterJson()));
  readonly messagePretty = computed(() => prettyJson(this.message()?.json));

  /** Why there is no state to show, for every state but `recorded`. */
  readonly note = computed(() => {
    const state = this.state();
    if (state === 'recorded') return '';
    if (state === 'omitted') return this.omittedNote();
    return NOTES[state];
  });

  marker(change: JsonChange): { symbol: string; text: string } {
    return MARKERS[change.kind];
  }

  preview(value: unknown): string {
    return previewValue(value);
  }

  /** Copies the stored text of what the view shows, unchanged. */
  copy(): void {
    const raw = this.view() === 'message' ? this.message()?.json : this.afterJson();
    if (raw == null || !this.canCopy) return;
    navigator.clipboard.writeText(raw).then(
      () => this.copied.set(true),
      () => this.copied.set(false),
    );
  }

  private omittedNote(): string {
    const parsed = parseStateJson(this.afterJson());
    if (parsed.kind !== 'omitted') return 'The state was too large to snapshot at this step.';
    const { bytes, limit, budget } = parsed;
    if (budget !== null) {
      const size = bytes === null ? '' : ` (this state is ${bytes} bytes)`;
      return (
        `The state was not snapshotted at this step: the saga's snapshot budget of ${budget} bytes ` +
        `was used up${size}. Snapshots after a failed step are still recorded in full.`
      );
    }
    const detail = [bytes === null ? null : `${bytes} bytes`, limit === null ? null : `limit ${limit}`]
      .filter((part) => part !== null)
      .join(', ');
    return `The state was too large to snapshot at this step${detail ? ` (${detail})` : ''}.`;
  }
}
