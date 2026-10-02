/**
 * Timeline entry and step builders for specs. Test-only: tsconfig.app.json excludes this folder
 * from the app build (and .dockerignore from the image), so app code must never import it.
 *
 * Entry builders take the ids that decide where an entry folds, and the defaults the engine would
 * write; `extra` overrides anything. `numbered` gives a list its sequence numbers and times, in the
 * order given.
 */

import { SagaLogEntry } from '../models/saga.model';
import { SagaTransition } from '../util/saga-transitions';

/** The default time of the first entry. */
export const T0 = '2026-01-01T00:00:00.000Z';

export function makeEntry(overrides: Partial<SagaLogEntry> = {}): SagaLogEntry {
  return {
    sequenceNumber: 1,
    correlationId: 'saga-1',
    sagaType: 'OrderSaga',
    entryType: 'MessageReceived',
    fromState: null,
    toState: null,
    messageType: null,
    messageId: null,
    payloadJson: null,
    errorMessage: null,
    traceId: null,
    spanId: null,
    occurredAtUtc: T0,
    sourceService: null,
    destinationService: null,
    causationId: null,
    ...overrides,
  };
}

export interface NumberingOptions {
  /** The first sequence number (default 1). */
  first?: number;
  /** The gap between sequence numbers (default 1; more for a sparse timeline). */
  gap?: number;
  /** The first entry's time (default T0). */
  at?: string;
  /** The time between entries in milliseconds (default 100). */
  spacingMs?: number;
}

/** Numbers `entries` in the order given and spaces their times evenly. */
export function numbered(entries: SagaLogEntry[], options: NumberingOptions = {}): SagaLogEntry[] {
  const { first = 1, gap = 1, at = T0, spacingMs = 100 } = options;
  const origin = Date.parse(at);
  return entries.map((entry, index) => ({
    ...entry,
    sequenceNumber: first + index * gap,
    occurredAtUtc: new Date(origin + index * spacingMs).toISOString(),
  }));
}

type Extra = Partial<SagaLogEntry>;

export function started(id: string, toState = 'Submitted', extra: Extra = {}): SagaLogEntry {
  return makeEntry({
    entryType: 'SagaStarted',
    messageId: id,
    toState,
    messageType: 'OrderSubmitted',
    payloadJson: '{"OrderId":"o-1"}',
    ...extra,
  });
}

export function received(
  id: string,
  messageType = 'PaymentCaptured',
  extra: Extra = {},
): SagaLogEntry {
  return makeEntry({ entryType: 'MessageReceived', messageId: id, messageType, ...extra });
}

export function succeeded(
  id: string | null,
  fromState: string,
  toState: string,
  extra: Extra = {},
): SagaLogEntry {
  return makeEntry({ entryType: 'StepSucceeded', messageId: id, fromState, toState, ...extra });
}

export function failed(id: string, fromState: string, extra: Extra = {}): SagaLogEntry {
  return makeEntry({
    entryType: 'StepFailed',
    messageId: id,
    fromState,
    messageType: 'PaymentCaptured',
    payloadJson: '{"Amount":10}',
    errorMessage: 'boom',
    ...extra,
  });
}

export function unexpected(id: string, fromState: string | null, extra: Extra = {}): SagaLogEntry {
  return makeEntry({
    entryType: 'UnexpectedEvent',
    messageId: id,
    fromState,
    messageType: 'Stray',
    ...extra,
  });
}

/** A snapshot: `json` null is a withheld payload. */
export function persisted(
  id: string | null,
  json: string | null = '{"Status":0}',
  extra: Extra = {},
): SagaLogEntry {
  return makeEntry({ entryType: 'StatePersisted', messageId: id, payloadJson: json, ...extra });
}

export function published(id: string, causationId: string | null, extra: Extra = {}): SagaLogEntry {
  return makeEntry({
    entryType: 'MessagePublished',
    messageId: id,
    causationId,
    messageType: 'ShipOrder',
    sourceService: 'OrderSaga',
    ...extra,
  });
}

/** A `.CallHttp` request: a MessagePublished under a fresh call id. */
export function httpCall(callId: string, causationId: string | null = null): SagaLogEntry {
  return published(callId, causationId, {
    messageType: 'POST http://loyalty/lookup',
    destinationService: 'loyalty',
  });
}

/** A `.CallHttp` reply: a MessageReceived under a fresh id, caused by the call. */
export function httpReply(id: string, callId: string): SagaLogEntry {
  return received(id, '200 OK', { causationId: callId, sourceService: 'loyalty' });
}

export function timeoutFired(state: string): SagaLogEntry {
  return makeEntry({ entryType: 'TimeoutFired', fromState: state });
}

/** Without `id` the shape engines wrote before the inbound id was stamped on it. */
export function timeoutScheduled(toState: string, id: string | null = null): SagaLogEntry {
  return makeEntry({ entryType: 'TimeoutScheduled', toState, messageId: id });
}

/** Without `id` the shape engines wrote before the inbound id was stamped on it. */
export function sagaCompleted(toState: string, id: string | null = null): SagaLogEntry {
  return makeEntry({ entryType: 'SagaCompleted', toState, messageId: id });
}

export function retryRequested(id: string | null, extra: Extra = {}): SagaLogEntry {
  return makeEntry({
    entryType: 'ManualRetryRequested',
    messageId: id,
    messageType: 'PaymentCaptured',
    ...extra,
  });
}

export function deliveryExhausted(id: string | null, extra: Extra = {}): SagaLogEntry {
  return makeEntry({
    entryType: 'DeliveryExhausted',
    messageId: id,
    messageType: id == null ? null : 'PaymentCaptured',
    errorMessage: 'dead-lettered',
    ...extra,
  });
}

/**
 * The timed-out InvoiceFollowUpSaga of the design's worked example (sequence numbers 60-68): step 1
 * started by InvoiceIssued (e5) entered AwaitingArchival, step 2 is the timeout that fired there
 * (#66). Its retry plan fails at 66 and replays the step whose inbound entry is #61.
 */
export function timedOutInvoice(): SagaLogEntry[] {
  return numbered(
    [
      started('e5', 'Requested', { messageType: 'InvoiceIssued' }),
      received('e5', 'InvoiceIssued'),
      makeEntry({ entryType: 'ChildSagaStarted', messageId: 'child-1', causationId: 'e5' }),
      succeeded('e5', 'Requested', 'AwaitingArchival'),
      timeoutScheduled('AwaitingArchival', 'e5'),
      persisted('e5'),
      timeoutFired('AwaitingArchival'),
      succeeded(null, 'AwaitingArchival', 'Abandoned'),
      persisted(null, '{"Status":5}'),
    ],
    { first: 60 },
  );
}

/** A step for component specs: one succeeded message step with a recorded snapshot. */
export function makeTransition(overrides: Partial<SagaTransition> = {}): SagaTransition {
  const entry = received('m1', 'PaymentCaptured');
  return {
    key: entry.sequenceNumber,
    ordinal: 1,
    isLast: true,
    trigger: 'message',
    outcome: 'succeeded',
    title: 'PaymentCaptured',
    fromState: 'Submitted',
    toState: 'Paid',
    errorMessage: null,
    actor: null,
    rows: [{ entry, ordinal: 1 }],
    message: null,
    snapshotJson: '{"Status":0}',
    snapshotState: 'recorded',
    baseline: null,
    lastOccurredAtUtc: entry.occurredAtUtc,
    ...overrides,
  };
}
