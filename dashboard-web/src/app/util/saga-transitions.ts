/**
 * Folds a saga's timeline into steps ("transitions"): what one inbound message, timeout, retry or
 * dead-letter did to the saga, with the state it left behind. Pure.
 *
 * A step starts when the saga starts, receives a message, fires a timeout, is retried, or
 * dead-letters a message that never reached MessageReceived. Every other entry joins a step: by its
 * `messageId` first (the step that inbound id started), then by its `causationId` (the step holding
 * the inbound or outbound message it names), and only then by adjacency ("the step touched last").
 * Ids decide whenever the engine stamped them, so concurrent handlers of one instance stay apart;
 * entries recorded without ids (by engines before the inbound id was stamped on TimeoutScheduled,
 * SagaCompleted and the context's own log entries) fall back to adjacency, which is exact for
 * sequential handling and best effort under interleaving.
 *
 * StatePersisted entries are the state each step committed. They become the step's snapshot and
 * are never rows. The fold keeps the API's order (ascending sequence number by contract) and never
 * parses a snapshot, apart from a prefix test for the omission markers.
 */

import { SagaEntryType, SagaLogEntry } from '../models/saga.model';
import { entryTypeLabel } from './entry-type-label';
import { toMillis } from './time-format';

/** How long the final step of a live saga may lack its snapshot before that reads as missing. */
export const PENDING_SNAPSHOT_MS = 5000;

export type TransitionTrigger = 'start' | 'message' | 'timeout' | 'retry' | 'delivery' | 'detached';

export type TransitionOutcome =
  'succeeded' | 'failed' | 'unhandled' | 'exhausted' | 'requested' | 'in-flight';

/**
 * - `recorded`: a snapshot of the state is stored;
 * - `omitted`: an omission marker stands in for a state too large to keep (or past the budget);
 * - `withheld`: a snapshot exists but the API left its payload out (no `sagas.data`);
 * - `not-persisted`: the step never commits state (an unhandled message, a retry request, entries
 *   outside any step, a timeout nobody handled);
 * - `missing`: the step should have a snapshot and has none (an older saga, snapshots off, a lost
 *   persist race, a failed append);
 * - `pending`: as `missing`, while the step is young enough that its snapshot may still be landing.
 */
export type SnapshotState =
  'recorded' | 'omitted' | 'withheld' | 'not-persisted' | 'missing' | 'pending';

/** One rendered timeline entry. `ordinal` is its 1-based position among rows, the map's "#i". */
export interface TimelineRow {
  entry: SagaLogEntry;
  ordinal: number;
}

export interface SagaTransition {
  /** The sequence number of the entry that opened the step; stable across refreshes. */
  key: number;
  /** 1-based step number. */
  ordinal: number;
  isLast: boolean;
  trigger: TransitionTrigger;
  outcome: TransitionOutcome;
  title: string;
  fromState: string | null;
  toState: string | null;
  /** The error of the entry that decided the outcome, when it carries one. */
  errorMessage: string | null;
  /** Who asked for a retry: the ManualRetryRequested source without its `dashboard:` prefix. */
  actor: string | null;
  rows: TimelineRow[];
  /** The step's inbound message payload, where one was recorded and not withheld. */
  message: { label: string; json: string } | null;
  /** The newest snapshot's payload; null when there is none or it was withheld. */
  snapshotJson: string | null;
  snapshotState: Exclude<SnapshotState, 'pending'>;
  /** The nearest earlier recorded snapshot, the "before" for this step's changes. */
  baseline: { json: string; ordinal: number; title: string } | null;
  /** When the step's newest row was recorded: the age test behind `pending`. */
  lastOccurredAtUtc: string;
}

export interface SagaHistory {
  transitions: SagaTransition[];
  /** Entries shown as rows: every entry but the snapshots. */
  rowCount: number;
  /** The first row's time, the origin of every "+offset". */
  firstOccurredAtUtc: string | null;
  /** The message that started the saga, from SagaStarted. */
  initiating: { messageType: string | null; json: string | null } | null;
  firstRecorded: SagaTransition | null;
  /** How many StatePersisted entries the timeline holds (0 for an older saga or snapshots off). */
  snapshotCount: number;
}

const OUTCOME_TYPES: ReadonlySet<SagaEntryType> = new Set<SagaEntryType>([
  'StepSucceeded',
  'StepFailed',
  'UnexpectedEvent',
  'StatePersisted',
]);

const OUTBOUND_TYPES: ReadonlySet<SagaEntryType> = new Set<SagaEntryType>([
  'MessagePublished',
  'MessageSent',
  'ChildSagaStarted',
  'ChildSagaFinished',
]);

const OUTCOME_BY_TYPE: Partial<Record<SagaEntryType, TransitionOutcome>> = {
  StepSucceeded: 'succeeded',
  StepFailed: 'failed',
  UnexpectedEvent: 'unhandled',
};

/** `{"$vsagaStateOmitted":true,...}` (engine) and `{"$vsagaPayloadOmitted":true,...}` (MongoDB). */
const MARKER_PREFIX = /^\s*\{\s*"\$vsaga\w*Omitted"\s*:\s*true\b/;

const RETRY_ACTOR_PREFIX = 'dashboard:';

interface Draft {
  trigger: TransitionTrigger;
  opening: SagaLogEntry;
  rows: TimelineRow[];
  /** Has a MessageReceived of its own (a start step merges the one for its initiating message). */
  hasReceived: boolean;
  /** The entry that decided the outcome; null while in flight. */
  outcomeEntry: SagaLogEntry | null;
  outcome: TransitionOutcome;
  /** The latest StepSucceeded/StepFailed/UnexpectedEvent: where the step's states come from. */
  stateEntry: SagaLogEntry | null;
  snapshot: SagaLogEntry | null;
}

class TimelineFold {
  readonly drafts: Draft[] = [];
  rowCount = 0;
  snapshotCount = 0;

  /** Inbound message id to the latest step it started. */
  private readonly inbound = new Map<string, Draft>();
  /** Outbound message id to the step that sent it. */
  private readonly outbound = new Map<string, Draft>();
  /** The latest timeout or retry step: where entries with no id of their own belong. */
  private keyless: Draft | null = null;
  /** The step touched last. */
  private current: Draft | null = null;

  constructor(private readonly outcomeIds: ReadonlySet<string>) {}

  add(entry: SagaLogEntry): void {
    switch (entry.entryType) {
      case 'SagaStarted':
        this.register(this.open('start', entry), entry.messageId);
        break;
      case 'MessageReceived':
        this.received(entry);
        break;
      case 'TimeoutFired':
        this.keyless = this.open('timeout', entry);
        break;
      case 'ManualRetryRequested':
        // Not registered: the in-process retry replays the failed message under its own id, and
        // that MessageReceived must open the re-run step, not join this one.
        this.keyless = this.open('retry', entry);
        break;
      case 'StepSucceeded':
      case 'StepFailed':
      case 'UnexpectedEvent':
        this.outcome(entry);
        break;
      case 'StatePersisted':
        this.snapshot(entry);
        break;
      case 'DeliveryExhausted':
        this.deliveryExhausted(entry);
        break;
      default:
        this.other(entry);
    }
  }

  private received(entry: SagaLogEntry): void {
    const id = entry.messageId;
    const started = id == null ? undefined : this.inbound.get(id);
    // The engine logs SagaStarted and MessageReceived for the initiating message.
    if (started?.trigger === 'start' && !started.hasReceived) {
      started.hasReceived = true;
      this.join(started, entry);
      return;
    }
    if (id != null && this.outcomeIds.has(id)) {
      this.openMessage(entry);
      return;
    }
    // No outcome of its own: a .CallHttp reply, logged mid-step under a fresh id with the request's
    // id as its cause. It belongs to the step that made the call, still running at that moment.
    const caller = entry.causationId == null ? undefined : this.outbound.get(entry.causationId);
    if (caller && caller.outcomeEntry === null) {
      this.join(caller, entry);
      return;
    }
    // Adjacency only for an entry with no cause at all: an older .CallHttp reply's shape. One that
    // names a cause and still has no outcome is another handler's message in flight (or one whose
    // outcome never came, a redelivery skipped as a duplicate), never part of the running step.
    if (
      this.current === null ||
      this.current.outcomeEntry !== null ||
      entry.causationId != null
    ) {
      this.openMessage(entry);
      return;
    }
    this.join(this.current, entry);
  }

  private openMessage(entry: SagaLogEntry): void {
    const draft = this.open('message', entry);
    draft.hasReceived = true;
    this.register(draft, entry.messageId);
  }

  private outcome(entry: SagaLogEntry): void {
    // An id no step started (an UnexpectedEvent for a message whose instance was not found) is a
    // step of its own; joining the step touched last would overwrite that step's outcome.
    const draft = this.attach(
      entry,
      entry.messageId == null ? (this.keyless ?? this.current) : this.byId(entry),
    );
    draft.outcomeEntry = entry;
    draft.outcome = OUTCOME_BY_TYPE[entry.entryType] ?? draft.outcome;
    draft.stateEntry = entry;
  }

  private snapshot(entry: SagaLogEntry): void {
    this.snapshotCount++;
    const draft = this.byId(entry) ?? this.byKeylessOrCurrent(entry) ?? this.openEmpty(entry);
    draft.snapshot = entry;
    this.current = draft;
  }

  private deliveryExhausted(entry: SagaLogEntry): void {
    if (entry.messageId == null) {
      // A deferred publish that failed after its step committed: not the step's outcome.
      this.other(entry);
      return;
    }
    let draft = this.byId(entry);
    if (draft) {
      this.join(draft, entry);
    } else {
      // Dead-lettered before any MessageReceived: its own step, so the Failed snapshot that follows
      // is not charged to the previous one.
      draft = this.open('delivery', entry);
      this.register(draft, entry.messageId);
    }
    draft.outcomeEntry = entry;
    draft.outcome = 'exhausted';
  }

  private other(entry: SagaLogEntry): void {
    const outbound = OUTBOUND_TYPES.has(entry.entryType);
    // An outbound entry's own id is the message it sent, never an inbound id of this saga.
    const draft = this.attach(
      entry,
      (outbound ? undefined : this.byId(entry)) ?? this.byCause(entry) ?? this.current,
    );
    if (outbound && entry.messageId != null) this.outbound.set(entry.messageId, draft);
  }

  private byId(entry: SagaLogEntry): Draft | undefined {
    return entry.messageId == null ? undefined : this.inbound.get(entry.messageId);
  }

  private byCause(entry: SagaLogEntry): Draft | undefined {
    const cause = entry.causationId;
    if (cause == null) return undefined;
    return this.inbound.get(cause) ?? this.outbound.get(cause);
  }

  /** A timeout's outcome and snapshot, and a retry's reset snapshot, carry no message id. */
  private byKeylessOrCurrent(entry: SagaLogEntry): Draft | null {
    return entry.messageId == null ? (this.keyless ?? this.current) : this.current;
  }

  /** Joins `entry` to `draft`; with nothing to join, opens a (leading) detached step for it. */
  private attach(entry: SagaLogEntry, draft: Draft | null | undefined): Draft {
    if (!draft) return this.open('detached', entry);
    this.join(draft, entry);
    return draft;
  }

  private register(draft: Draft, id: string | null): void {
    if (id != null) this.inbound.set(id, draft);
  }

  private open(trigger: TransitionTrigger, entry: SagaLogEntry): Draft {
    const draft = this.openEmpty(entry, trigger);
    this.join(draft, entry);
    return draft;
  }

  /** A step with no rows yet; on its own only for a snapshot with nothing before it. */
  private openEmpty(entry: SagaLogEntry, trigger: TransitionTrigger = 'detached'): Draft {
    const draft: Draft = {
      trigger,
      opening: entry,
      rows: [],
      hasReceived: false,
      outcomeEntry: null,
      outcome: trigger === 'retry' ? 'requested' : 'in-flight',
      stateEntry: null,
      snapshot: null,
    };
    this.drafts.push(draft);
    this.current = draft;
    return draft;
  }

  private join(draft: Draft, entry: SagaLogEntry): void {
    draft.rows.push({ entry, ordinal: ++this.rowCount });
    this.current = draft;
  }
}

/** Folds a timeline (in the API's ascending order) into steps. */
export function foldTimeline(entries: readonly SagaLogEntry[]): SagaHistory {
  const outcomeIds = new Set<string>();
  for (const entry of entries) {
    if (entry.messageId != null && OUTCOME_TYPES.has(entry.entryType)) {
      outcomeIds.add(entry.messageId);
    }
  }

  const fold = new TimelineFold(outcomeIds);
  for (const entry of entries) fold.add(entry);

  const transitions: SagaTransition[] = [];
  let baseline: SagaTransition | null = null;
  for (const draft of fold.drafts) {
    const transition = toTransition(draft, transitions.length, fold.drafts.length, baseline);
    transitions.push(transition);
    if (transition.snapshotState === 'recorded') baseline = transition;
  }

  const started = entries.find((entry) => entry.entryType === 'SagaStarted');
  const firstRow = entries.find((entry) => entry.entryType !== 'StatePersisted');
  return {
    transitions,
    rowCount: fold.rowCount,
    firstOccurredAtUtc: firstRow?.occurredAtUtc ?? null,
    initiating: started
      ? { messageType: started.messageType, json: started.payloadJson ?? null }
      : null,
    firstRecorded: transitions.find((t) => t.snapshotState === 'recorded') ?? null,
    snapshotCount: fold.snapshotCount,
  };
}

/**
 * The step whose rows include the entry with this sequence number: how the retry plan's failure
 * and replay entries become steps. Null for no number, or one that is not a row (a snapshot, an
 * entry the loaded timeline does not hold yet).
 */
export function stepContaining(history: SagaHistory, sequence: number | null | undefined): SagaTransition | null {
  if (sequence == null) return null;
  return (
    history.transitions.find((step) => step.rows.some((row) => row.entry.sequenceNumber === sequence)) ?? null
  );
}

function toTransition(
  draft: Draft,
  index: number,
  count: number,
  baseline: SagaTransition | null,
): SagaTransition {
  const { opening, stateEntry } = draft;
  const fromState = stateEntry?.fromState ?? opening.fromState;
  const toState = stateEntry ? stateEntry.toState : opening.toState;
  const isLast = index === count - 1;
  const snapshotJson = draft.snapshot?.payloadJson ?? null;
  const lastRow = draft.rows.at(-1)?.entry ?? opening;
  return {
    key: opening.sequenceNumber,
    ordinal: index + 1,
    isLast,
    trigger: draft.trigger,
    outcome: draft.outcome,
    title: titleOf(draft.trigger, opening, fromState),
    fromState,
    toState,
    errorMessage: draft.outcomeEntry?.errorMessage ?? null,
    actor: draft.trigger === 'retry' ? actorOf(opening.sourceService) : null,
    rows: draft.rows,
    message: messageOf(draft),
    snapshotJson,
    snapshotState: snapshotStateOf(draft, snapshotJson),
    baseline: baseline?.snapshotJson
      ? { json: baseline.snapshotJson, ordinal: baseline.ordinal, title: baseline.title }
      : null,
    lastOccurredAtUtc: lastRow.occurredAtUtc,
  };
}

function snapshotStateOf(draft: Draft, json: string | null): SagaTransition['snapshotState'] {
  if (draft.snapshot) {
    if (json == null) return 'withheld';
    return MARKER_PREFIX.test(json) ? 'omitted' : 'recorded';
  }
  const notPersisted =
    draft.outcome === 'unhandled' ||
    draft.trigger === 'retry' ||
    draft.trigger === 'detached' ||
    (draft.trigger === 'timeout' && draft.outcomeEntry === null);
  return notPersisted ? 'not-persisted' : 'missing';
}

function titleOf(
  trigger: TransitionTrigger,
  opening: SagaLogEntry,
  fromState: string | null,
): string {
  const type = opening.messageType;
  switch (trigger) {
    case 'start':
      return type ? `Started by ${type}` : 'Started';
    case 'message':
      return type ?? 'Message received';
    case 'timeout':
      return fromState ? `Timeout in ${fromState}` : 'Timeout';
    case 'retry':
      return type ? `Manual retry of ${type}` : 'Manual retry';
    case 'delivery':
      return type ? `${type} dead-lettered` : 'Dead-lettered';
    case 'detached':
      // A snapshot's messageType names the state, not a message.
      if (opening.entryType === 'StatePersisted') return entryTypeLabel(opening.entryType);
      return type ?? entryTypeLabel(opening.entryType);
  }
}

function actorOf(source: string | null | undefined): string | null {
  if (!source) return null;
  return source.startsWith(RETRY_ACTOR_PREFIX) ? source.slice(RETRY_ACTOR_PREFIX.length) : source;
}

/**
 * The opening entry's payload (SagaStarted always, MessageReceived from engines that record it),
 * else the StepFailed payload kept for a retry. Null when none was recorded or it was withheld. A
 * step opened by a snapshot has no message: that payload is the state.
 */
function messageOf(draft: Draft): SagaTransition['message'] {
  const opening = draft.opening.entryType === 'StatePersisted' ? null : draft.opening;
  const carrier =
    opening?.payloadJson != null
      ? opening
      : draft.rows.find(
          (row) => row.entry.entryType === 'StepFailed' && row.entry.payloadJson != null,
        )?.entry;
  if (carrier?.payloadJson == null) return null;
  return {
    label: carrier.messageType ?? entryTypeLabel(carrier.entryType),
    json: carrier.payloadJson,
  };
}

/**
 * The snapshot state to show. The final step of a live (Running or Compensating) saga that has no
 * snapshot yet reads `pending` while its newest row is younger than PENDING_SNAPSHOT_MS: the
 * poller can push between the engine's persist and its StatePersisted append. Past that the
 * snapshot is not coming (a lost persist race, a timeout claimed but unhandled), so the fold's own
 * state stands.
 */
export function effectiveSnapshotState(
  t: SagaTransition,
  live: boolean,
  nowMs: number,
): SnapshotState {
  const awaitingSnapshot =
    t.snapshotState === 'missing' ||
    (t.snapshotState === 'not-persisted' && (t.trigger === 'timeout' || t.trigger === 'retry'));
  if (!t.isLast || !live || !awaitingSnapshot) return t.snapshotState;
  const at = toMillis(t.lastOccurredAtUtc);
  if (at === null) return t.snapshotState;
  // The browser's clock can trail the API host's, so a fresh row may look slightly in the future:
  // that reads as young too, within the same window.
  const age = nowMs - at;
  return Math.abs(age) < PENDING_SNAPSHOT_MS ? 'pending' : t.snapshotState;
}
