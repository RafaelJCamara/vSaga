import {
  deliveryExhausted,
  failed,
  httpCall,
  httpReply,
  makeEntry,
  makeTransition,
  numbered,
  persisted,
  published,
  received,
  retryRequested,
  sagaCompleted,
  started,
  succeeded,
  T0,
  timeoutFired,
  timeoutScheduled,
  unexpected,
} from '../testing/timeline-fixtures';
import {
  effectiveSnapshotState,
  foldTimeline,
  PENDING_SNAPSHOT_MS,
  SagaHistory,
  SagaTransition,
} from './saga-transitions';

const S1 = '{"Status":0,"Total":10}';
const S2 = '{"Status":1,"Total":10}';
const S3 = '{"Status":1,"Total":12}';

/** Each step as trigger, outcome and the sequence numbers of its rows. */
function shape(history: SagaHistory) {
  return history.transitions.map((t) => ({
    trigger: t.trigger,
    outcome: t.outcome,
    rows: t.rows.map((row) => row.entry.sequenceNumber),
  }));
}

function last(history: SagaHistory): SagaTransition {
  return history.transitions[history.transitions.length - 1];
}

function msAfter(t: SagaTransition, ms: number): number {
  return Date.parse(t.lastOccurredAtUtc) + ms;
}

describe('foldTimeline', () => {
  describe('steps', () => {
    const happyPath = numbered([
      started('m0', 'Submitted'), // 1
      received('m0', 'OrderSubmitted'), // 2
      succeeded('m0', 'Submitted', 'AwaitingPayment'), // 3
      persisted('m0', S1), // 4
      published('p1', 'm0'), // 5
      received('m1', 'PaymentCaptured'), // 6
      succeeded('m1', 'AwaitingPayment', 'Paid'), // 7
      sagaCompleted('Paid'), // 8
      persisted('m1', S2), // 9
    ]);

    it('merges SagaStarted with its MessageReceived and keeps snapshots out of the rows', () => {
      const history = foldTimeline(happyPath);

      expect(shape(history)).toEqual([
        { trigger: 'start', outcome: 'succeeded', rows: [1, 2, 3, 5] },
        { trigger: 'message', outcome: 'succeeded', rows: [6, 7, 8] },
      ]);
      const rowTypes = history.transitions.flatMap((t) => t.rows.map((r) => r.entry.entryType));
      expect(rowTypes).not.toContain('StatePersisted');
      expect(history.rowCount).toBe(7);
      expect(history.snapshotCount).toBe(2);
    });

    it('numbers rows like the map, skipping snapshots, and steps from one', () => {
      const history = foldTimeline(happyPath);

      expect(history.transitions.flatMap((t) => t.rows.map((r) => r.ordinal))).toEqual([
        1, 2, 3, 4, 5, 6, 7,
      ]);
      expect(history.transitions.map((t) => [t.key, t.ordinal, t.isLast])).toEqual([
        [1, 1, false],
        [6, 2, true],
      ]);
    });

    it('describes each step and gives the second its baseline', () => {
      const [first, second] = foldTimeline(happyPath).transitions;

      expect(first).toMatchObject({
        title: 'Started by OrderSubmitted',
        fromState: 'Submitted',
        toState: 'AwaitingPayment',
        snapshotJson: S1,
        snapshotState: 'recorded',
        baseline: null,
        message: { label: 'OrderSubmitted', json: '{"OrderId":"o-1"}' },
      });
      expect(second).toMatchObject({
        title: 'PaymentCaptured',
        fromState: 'AwaitingPayment',
        toState: 'Paid',
        snapshotJson: S2,
        snapshotState: 'recorded',
        baseline: { json: S1, ordinal: 1, title: 'Started by OrderSubmitted' },
        message: null,
        lastOccurredAtUtc: happyPath[7].occurredAtUtc,
      });
    });

    it('reports the initiating message, the first recorded step and the origin time', () => {
      const history = foldTimeline(happyPath);

      expect(history.initiating).toEqual({
        messageType: 'OrderSubmitted',
        json: '{"OrderId":"o-1"}',
      });
      expect(history.firstRecorded).toBe(history.transitions[0]);
      expect(history.firstOccurredAtUtc).toBe(T0);
    });

    it('folds an empty timeline to nothing', () => {
      expect(foldTimeline([])).toEqual({
        transitions: [],
        rowCount: 0,
        firstOccurredAtUtc: null,
        initiating: null,
        firstRecorded: null,
        snapshotCount: 0,
      });
    });

    it('keeps sparse sequence numbers as keys while ordinals stay contiguous', () => {
      const history = foldTimeline(
        numbered([started('m0'), received('m0'), received('m1'), succeeded('m1', 'a', 'b')], {
          first: 10,
          gap: 7,
        }),
      );

      expect(history.transitions.map((t) => t.key)).toEqual([10, 24]);
      expect(history.transitions[1].rows.map((r) => [r.entry.sequenceNumber, r.ordinal])).toEqual([
        [24, 3],
        [31, 4],
      ]);
    });

    it('keeps the input order and never sorts by sequence number', () => {
      const entries = [
        started('m0', 'Submitted', { sequenceNumber: 5 }),
        received('m0', 'OrderSubmitted', { sequenceNumber: 2 }),
        succeeded('m0', 'Submitted', 'Paid', { sequenceNumber: 9 }),
      ];

      expect(shape(foldTimeline(entries))[0].rows).toEqual([5, 2, 9]);
    });
  });

  describe('snapshot states', () => {
    it('marks every step missing when the saga has no snapshots', () => {
      const history = foldTimeline(
        numbered([
          started('m0'),
          received('m0'),
          succeeded('m0', 'Submitted', 'AwaitingPayment'),
          received('m1'),
          succeeded('m1', 'AwaitingPayment', 'Paid'),
        ]),
      );

      expect(history.transitions.map((t) => t.snapshotState)).toEqual(['missing', 'missing']);
      expect(history.transitions.map((t) => t.baseline)).toEqual([null, null]);
      expect(history.firstRecorded).toBeNull();
      expect(history.snapshotCount).toBe(0);
    });

    it('reads both omission markers as omitted and never as a baseline', () => {
      const engine = '{"$vsagaStateOmitted":true,"bytes":300000,"limit":262144}';
      const budget = '{"$vsagaStateOmitted":true,"bytes":2048,"budget":1048576}';
      const mongo = '{ "$vsagaPayloadOmitted" : true, "bytes": 17000000, "limit": 16000000 }';
      const history = foldTimeline(
        numbered([
          started('m0'),
          received('m0'),
          succeeded('m0', 'a', 'b'),
          persisted('m0', S1),
          received('m1'),
          succeeded('m1', 'b', 'c'),
          persisted('m1', engine),
          received('m2'),
          succeeded('m2', 'c', 'd'),
          persisted('m2', mongo),
          received('m3'),
          succeeded('m3', 'd', 'e'),
          persisted('m3', budget),
        ]),
      );

      const [, second, third, fourth] = history.transitions;
      expect([second, third, fourth].map((t) => t.snapshotState)).toEqual([
        'omitted',
        'omitted',
        'omitted',
      ]);
      expect(fourth.baseline?.ordinal).toBe(1);
    });

    it('treats a state that only contains a marker-like key as recorded', () => {
      const nested = '{"Note":{"$vsagaStateOmitted":true}}';
      const history = foldTimeline(
        numbered([
          started('m0'),
          received('m0'),
          succeeded('m0', 'a', 'b'),
          persisted('m0', nested),
        ]),
      );

      expect(history.transitions[0].snapshotState).toBe('recorded');
    });

    it('reads a snapshot whose payload the API left out as withheld', () => {
      const history = foldTimeline(
        numbered([
          started('m0', 'Submitted', { payloadJson: null }),
          received('m0'),
          succeeded('m0', 'a', 'b'),
          persisted('m0', null),
          received('m1'),
          succeeded('m1', 'b', 'c'),
          persisted('m1', null),
        ]),
      );

      expect(history.transitions.map((t) => [t.snapshotState, t.snapshotJson])).toEqual([
        ['withheld', null],
        ['withheld', null],
      ]);
      expect(history.transitions[1].baseline).toBeNull();
      expect(history.transitions[0].message).toBeNull();
      expect(history.initiating).toEqual({ messageType: 'OrderSubmitted', json: null });
    });

    it('marks a lost persist race missing and diffs the next step against the last recorded one', () => {
      const history = foldTimeline(
        numbered([
          started('m0'),
          received('m0'),
          succeeded('m0', 'a', 'b'),
          persisted('m0', S1),
          received('m1'), // lost its persist race: no snapshot, redelivery skipped as a duplicate
          succeeded('m1', 'b', 'c'),
          received('m2'),
          succeeded('m2', 'b', 'd'),
          persisted('m2', S3),
        ]),
      );

      const [, lost, next] = history.transitions;
      expect(lost.snapshotState).toBe('missing');
      expect(lost.outcome).toBe('succeeded');
      expect(next.baseline).toEqual({ json: S1, ordinal: 1, title: 'Started by OrderSubmitted' });
    });

    it('takes the newest snapshot when a step records more than one', () => {
      const history = foldTimeline(
        numbered([
          started('m0'),
          received('m0'),
          failed('m0', 'Submitted'),
          persisted('m0', S1),
          deliveryExhausted('m0'),
          persisted('m0', S2),
        ]),
      );

      expect(history.transitions).toHaveLength(1);
      expect(history.transitions[0].snapshotJson).toBe(S2);
    });
  });

  describe('attachment by id', () => {
    it('keeps interleaved handlers apart by messageId and causationId', () => {
      const history = foldTimeline(
        numbered([
          started('m0'), // 1
          received('m0'), // 2
          succeeded('m0', 'a', 'b'), // 3
          persisted('m0', S1), // 4
          received('m1', 'A'), // 5: step A
          received('m2', 'B'), // 6: step B
          succeeded('m1', 'b', 'c'), // 7 → A
          published('p1', 'm1'), // 8 → A by causation
          makeEntry({ entryType: 'CompensationStarted', causationId: 'm1' }), // 9 → A (log sink)
          succeeded('m2', 'b', 'd'), // 10 → B
          persisted('m2', S3), // 11 → B
          persisted('m1', S2), // 12 → A
          makeEntry({ entryType: 'ChildSagaFinished', messageId: 'p9', causationId: 'm2' }), // 13 → B
        ]),
      );

      expect(shape(history)).toEqual([
        { trigger: 'start', outcome: 'succeeded', rows: [1, 2, 3] },
        { trigger: 'message', outcome: 'succeeded', rows: [5, 7, 8, 9] },
        { trigger: 'message', outcome: 'succeeded', rows: [6, 10, 13] },
      ]);
      expect(history.transitions[1].snapshotJson).toBe(S2);
      expect(history.transitions[2].snapshotJson).toBe(S3);
    });

    it('attaches an entry to the step whose outbound message its causationId names', () => {
      const history = foldTimeline(
        numbered([
          received('m1', 'A'), // 1: A
          published('p1', 'm1'), // 2 → A
          received('m2', 'B'), // 3: B
          succeeded('m2', 'b', 'c'), // 4 → B
          makeEntry({ entryType: 'StateEntered', causationId: 'p1' }), // 5 → A, by the outbound id
          succeeded('m1', 'a', 'b'), // 6 → A
        ]),
      );

      expect(shape(history).map((s) => s.rows)).toEqual([
        [1, 2, 5, 6],
        [3, 4],
      ]);
    });

    it('charges a publish drained after the snapshot to its own step', () => {
      const history = foldTimeline(
        numbered([
          received('m1', 'A'), // 1
          succeeded('m1', 'a', 'b'), // 2
          persisted('m1', S1), // 3
          received('m2', 'B'), // 4
          published('p1', 'm1'), // 5: m1's deferred publish, drained late
          succeeded('m2', 'b', 'c'), // 6
        ]),
      );

      expect(shape(history).map((s) => s.rows)).toEqual([
        [1, 2, 5],
        [4, 6],
      ]);
    });

    it('attaches the stamped TimeoutScheduled and SagaCompleted by id', () => {
      const history = foldTimeline(
        numbered([
          received('m1', 'A'), // 1
          received('m2', 'B'), // 2
          succeeded('m1', 'a', 'b'), // 3 → A
          succeeded('m2', 'a', 'c'), // 4 → B, now the step touched last
          timeoutScheduled('b', 'm1'), // 5 → A by id
          sagaCompleted('b', 'm1'), // 6 → A by id
        ]),
      );

      expect(shape(history).map((s) => s.rows)).toEqual([
        [1, 3, 5, 6],
        [2, 4],
      ]);
    });

    it('lets the unstamped TimeoutScheduled and SagaCompleted of older engines follow the step touched last', () => {
      const sequential = foldTimeline(
        numbered([
          received('m1'), // 1
          succeeded('m1', 'a', 'b'), // 2
          timeoutScheduled('b'), // 3
          sagaCompleted('b'), // 4
          persisted('m1', S1), // 5
          received('m2'), // 6
        ]),
      );
      expect(shape(sequential).map((s) => s.rows)).toEqual([[1, 2, 3, 4], [6]]);

      // Best effort under interleaving: without an id they land in the step touched last.
      const interleaved = foldTimeline(
        numbered([
          received('m1', 'A'), // 1
          received('m2', 'B'), // 2
          succeeded('m1', 'a', 'b'), // 3 → A
          succeeded('m2', 'a', 'c'), // 4 → B
          timeoutScheduled('b'), // 5 → B
        ]),
      );
      expect(shape(interleaved).map((s) => s.rows)).toEqual([
        [1, 3],
        [2, 4, 5],
      ]);
    });
  });

  describe('.CallHttp hops', () => {
    it('keeps the request and its reply inside the message step', () => {
      const history = foldTimeline(
        numbered([
          started('m0'), // 1
          received('m0'), // 2
          httpCall('c1'), // 3: an older engine's request, no causationId
          httpReply('r1', 'c1'), // 4
          succeeded('m0', 'Submitted', 'Looked up'), // 5
          persisted('m0', S1), // 6
          received('m1'), // 7
        ]),
      );

      expect(shape(history)).toEqual([
        { trigger: 'start', outcome: 'succeeded', rows: [1, 2, 3, 4, 5] },
        { trigger: 'message', outcome: 'in-flight', rows: [7] },
      ]);
    });

    it('sends a reply logged after another handler finished to the step that made the call', () => {
      const history = foldTimeline(
        numbered([
          received('m1', 'A'), // 1
          httpCall('c1', 'm1'), // 2 → A
          received('m2', 'B'), // 3
          succeeded('m2', 'a', 'c'), // 4 → B, which now has an outcome
          httpReply('r1', 'c1'), // 5 → A: no outcome of its own, caused by A's call
          succeeded('m1', 'a', 'b'), // 6 → A
        ]),
      );

      expect(shape(history)).toEqual([
        { trigger: 'message', outcome: 'succeeded', rows: [1, 2, 5, 6] },
        { trigger: 'message', outcome: 'succeeded', rows: [3, 4] },
      ]);
    });

    it('keeps a call and its reply inside a timeout step', () => {
      const history = foldTimeline(
        numbered([
          received('m1'), // 1
          succeeded('m1', 'a', 'AwaitingLoyalty'), // 2
          persisted('m1', S1), // 3
          timeoutFired('AwaitingLoyalty'), // 4
          httpCall('c1'), // 5
          httpReply('r1', 'c1'), // 6
          succeeded(null, 'AwaitingLoyalty', 'Standard'), // 7
          persisted(null, S2), // 8
        ]),
      );

      expect(shape(history)[1]).toEqual({
        trigger: 'timeout',
        outcome: 'succeeded',
        rows: [4, 5, 6, 7],
      });
      expect(history.transitions[1].snapshotJson).toBe(S2);
    });

    it('opens a step for a reply to a message the saga published once that step had finished', () => {
      const history = foldTimeline(
        numbered([
          received('m1'), // 1
          succeeded('m1', 'a', 'b'), // 2
          published('p1', 'm1'), // 3
          received('m2', 'Shipped', { causationId: 'p1' }), // 4: in flight, no outcome yet
        ]),
      );

      expect(shape(history).map((s) => s.rows)).toEqual([[1, 2, 3], [4]]);
    });

    it('keeps a message with a cause of its own out of a step still running', () => {
      const history = foldTimeline(
        numbered([
          received('m1', 'A'), // 1: in flight
          received('m2', 'B', { causationId: 'ext-1' }), // 2: another handler, no outcome yet
        ]),
      );

      expect(shape(history).map((s) => s.rows)).toEqual([[1], [2]]);
    });
  });

  describe('timeouts', () => {
    it('folds a handled timeout with its keyless outcome and snapshot', () => {
      const history = foldTimeline(
        numbered([
          received('m1'), // 1
          succeeded('m1', 'a', 'AwaitingPayment'), // 2
          persisted('m1', S1), // 3
          timeoutFired('AwaitingPayment'), // 4
          succeeded(null, 'AwaitingPayment', 'Cancelled'), // 5
          sagaCompleted('Cancelled'), // 6
          persisted(null, S2), // 7
        ]),
      );

      expect(history.transitions[1]).toMatchObject({
        trigger: 'timeout',
        outcome: 'succeeded',
        title: 'Timeout in AwaitingPayment',
        fromState: 'AwaitingPayment',
        toState: 'Cancelled',
        snapshotJson: S2,
        snapshotState: 'recorded',
        baseline: { json: S1, ordinal: 1, title: 'PaymentCaptured' },
      });
      expect(history.transitions[1].rows.map((r) => r.entry.sequenceNumber)).toEqual([4, 5, 6]);
    });

    it('marks a timeout nobody handled as not persisted once a later step follows', () => {
      const history = foldTimeline(
        numbered([timeoutFired('AwaitingPayment'), received('m1'), succeeded('m1', 'a', 'b')]),
      );

      expect(history.transitions[0]).toMatchObject({
        trigger: 'timeout',
        outcome: 'in-flight',
        fromState: 'AwaitingPayment',
        toState: null,
        snapshotState: 'not-persisted',
      });
    });

    it('gives an interleaved timeout its keyless outcome and snapshot, not the current step', () => {
      const history = foldTimeline(
        numbered([
          timeoutFired('AwaitingPayment'), // 1
          received('m1'), // 2: opens its own step, its id has an outcome
          succeeded(null, 'AwaitingPayment', 'Cancelled'), // 3 → timeout
          persisted(null, S1), // 4 → timeout
          succeeded('m1', 'AwaitingPayment', 'Paid'), // 5 → message
          persisted('m1', S2), // 6 → message
        ]),
      );

      expect(shape(history)).toEqual([
        { trigger: 'timeout', outcome: 'succeeded', rows: [1, 3] },
        { trigger: 'message', outcome: 'succeeded', rows: [2, 5] },
      ]);
      expect(history.transitions.map((t) => t.snapshotJson)).toEqual([S1, S2]);
    });
  });

  describe('retries', () => {
    const failure = [
      received('m1', 'PaymentCaptured'),
      failed('m1', 'AwaitingPayment'),
      persisted('m1', S1),
    ];

    it('opens a retry step that carries the reset snapshot and its actor', () => {
      const history = foldTimeline(
        numbered([
          ...failure, // 1-3
          retryRequested('m1', {
            sourceService: 'dashboard:alice',
            fromState: 'AwaitingPayment',
            toState: 'Submitted',
          }), // 4
          persisted(null, S2), // 5: the reset snapshot
          received('m9', 'PaymentCaptured'), // 6: the redrive, under a fresh id
          succeeded('m9', 'Submitted', 'Paid'), // 7
        ]),
      );

      expect(shape(history)).toEqual([
        { trigger: 'message', outcome: 'failed', rows: [1, 2] },
        { trigger: 'retry', outcome: 'requested', rows: [4] },
        { trigger: 'message', outcome: 'succeeded', rows: [6, 7] },
      ]);
      expect(history.transitions[0]).toMatchObject({
        errorMessage: 'boom',
        message: { label: 'PaymentCaptured', json: '{"Amount":10}' },
      });
      expect(history.transitions[1]).toMatchObject({
        title: 'Manual retry of PaymentCaptured',
        actor: 'alice',
        fromState: 'AwaitingPayment',
        toState: 'Submitted',
        snapshotJson: S2,
        snapshotState: 'recorded',
      });
      expect(history.transitions[2].baseline?.json).toBe(S2);
    });

    it('marks a retry without a reset snapshot as not persisted', () => {
      const history = foldTimeline(
        numbered([...failure, retryRequested('m1'), received('m9'), succeeded('m9', 'a', 'b')]),
      );

      expect(history.transitions[1]).toMatchObject({
        trigger: 'retry',
        snapshotState: 'not-persisted',
        actor: null,
      });
    });

    it('re-runs an in-process retry that reuses the failed message id in a step of its own', () => {
      const history = foldTimeline(
        numbered([
          ...failure, // 1-3
          retryRequested('m1'), // 4
          received('m1', 'PaymentCaptured'), // 5: same id
          succeeded('m1', 'AwaitingPayment', 'Paid'), // 6
          persisted('m1', S2), // 7
        ]),
      );

      expect(shape(history)).toEqual([
        { trigger: 'message', outcome: 'failed', rows: [1, 2] },
        { trigger: 'retry', outcome: 'requested', rows: [4] },
        { trigger: 'message', outcome: 'succeeded', rows: [5, 6] },
      ]);
      expect(history.transitions.map((t) => t.snapshotJson)).toEqual([S1, null, S2]);
    });

    it('keeps a retry source without the dashboard prefix as it is', () => {
      const history = foldTimeline([retryRequested('m1', { sourceService: 'ops-console' })]);

      expect(history.transitions[0].actor).toBe('ops-console');
    });
  });

  describe('dead letters and strays', () => {
    it('opens a delivery step for a message dead-lettered before it was received', () => {
      const history = foldTimeline(
        numbered([
          started('m0'), // 1
          received('m0'), // 2
          succeeded('m0', 'Submitted', 'AwaitingPayment'), // 3
          persisted('m0', S1), // 4
          deliveryExhausted('m5'), // 5
          persisted('m5', S2), // 6: the Failed snapshot
        ]),
      );

      expect(shape(history)[1]).toEqual({ trigger: 'delivery', outcome: 'exhausted', rows: [5] });
      expect(history.transitions[1]).toMatchObject({
        title: 'PaymentCaptured dead-lettered',
        errorMessage: 'dead-lettered',
        snapshotJson: S2,
      });
      expect(history.transitions[0].snapshotJson).toBe(S1);
    });

    it('lets a dead letter after a failure end that step as exhausted', () => {
      const history = foldTimeline(
        numbered([received('m1'), failed('m1', 'a'), persisted('m1', S1), deliveryExhausted('m1')]),
      );

      expect(shape(history)).toEqual([
        { trigger: 'message', outcome: 'exhausted', rows: [1, 2, 4] },
      ]);
      expect(history.transitions[0].fromState).toBe('a');
    });

    it('keeps a step succeeded when a publish it queued is dead-lettered after the commit', () => {
      const history = foldTimeline(
        numbered([
          received('m1'),
          succeeded('m1', 'a', 'b'),
          persisted('m1', S1),
          deliveryExhausted(null),
        ]),
      );

      expect(shape(history)).toEqual([
        { trigger: 'message', outcome: 'succeeded', rows: [1, 2, 4] },
      ]);
      expect(history.transitions[0].errorMessage).toBeNull();
    });

    it('opens a leading detached step for an UnexpectedEvent before the start', () => {
      const history = foldTimeline(
        numbered([
          unexpected('x1', null),
          started('m0'),
          received('m0'),
          succeeded('m0', 'a', 'b'),
        ]),
      );

      expect(shape(history)).toEqual([
        { trigger: 'detached', outcome: 'unhandled', rows: [1] },
        { trigger: 'start', outcome: 'succeeded', rows: [2, 3, 4] },
      ]);
      expect(history.transitions[0]).toMatchObject({
        title: 'Stray',
        snapshotState: 'not-persisted',
      });
    });

    it('gives an UnexpectedEvent for an id no step started a step of its own mid-timeline', () => {
      const history = foldTimeline(
        numbered([
          started('m0'), // 1
          received('m0'), // 2
          succeeded('m0', 'Submitted', 'AwaitingPayment'), // 3
          unexpected('x1', null), // 4: instance not found, no MessageReceived
        ]),
      );

      expect(shape(history)).toEqual([
        { trigger: 'start', outcome: 'succeeded', rows: [1, 2, 3] },
        { trigger: 'detached', outcome: 'unhandled', rows: [4] },
      ]);
      expect(history.transitions[0].toState).toBe('AwaitingPayment');
    });

    it('opens a step for a snapshot with nothing to join without showing the state as a message', () => {
      const history = foldTimeline([persisted('m1', S1, { messageType: 'OrderState' })]);

      expect(history.transitions[0]).toMatchObject({
        trigger: 'detached',
        title: 'StatePersisted',
        message: null,
        snapshotJson: S1,
        rows: [],
      });
    });

    it('marks an unhandled message as not persisted', () => {
      const history = foldTimeline(
        numbered([
          received('m1'),
          unexpected('m1', 'Paid'),
          received('m2'),
          succeeded('m2', 'a', 'b'),
        ]),
      );

      expect(history.transitions[0]).toMatchObject({
        outcome: 'unhandled',
        fromState: 'Paid',
        snapshotState: 'not-persisted',
      });
    });

    it('shows the payload a MessageReceived recorded', () => {
      const history = foldTimeline([received('m1', 'PaymentCaptured', { payloadJson: '{"A":1}' })]);

      expect(history.transitions[0].message).toEqual({ label: 'PaymentCaptured', json: '{"A":1}' });
    });
  });
});

describe('effectiveSnapshotState', () => {
  const inFlight = numbered([
    started('m0'),
    received('m0'),
    succeeded('m0', 'a', 'b'),
    persisted('m0', S1),
    received('m1'),
  ]);

  it('reports the final step of a live saga pending while it is young, then missing', () => {
    const step = last(foldTimeline(inFlight));

    expect(step.snapshotState).toBe('missing');
    expect(effectiveSnapshotState(step, true, msAfter(step, 0))).toBe('pending');
    expect(effectiveSnapshotState(step, true, msAfter(step, PENDING_SNAPSHOT_MS - 1))).toBe(
      'pending',
    );
    expect(effectiveSnapshotState(step, true, msAfter(step, PENDING_SNAPSHOT_MS))).toBe('missing');
  });

  it('never reports pending for a saga that is no longer live', () => {
    const step = last(foldTimeline(inFlight));

    expect(effectiveSnapshotState(step, false, msAfter(step, 0))).toBe('missing');
  });

  it('never reports pending for an earlier step', () => {
    const step = foldTimeline(numbered([received('m1'), succeeded('m1', 'a', 'b'), received('m2')]))
      .transitions[0];

    expect(step.isLast).toBe(false);
    expect(effectiveSnapshotState(step, true, msAfter(step, 0))).toBe('missing');
  });

  it('lets a lost persist race on the final step age into missing', () => {
    const step = last(foldTimeline(numbered([received('m1'), succeeded('m1', 'a', 'b')])));

    expect(effectiveSnapshotState(step, true, msAfter(step, 1000))).toBe('pending');
    expect(effectiveSnapshotState(step, true, msAfter(step, 60_000))).toBe('missing');
  });

  it('lets a final timeout nobody handled age into not persisted', () => {
    const step = last(
      foldTimeline(numbered([received('m1'), succeeded('m1', 'a', 'b'), timeoutFired('b')])),
    );

    expect(effectiveSnapshotState(step, true, msAfter(step, 1000))).toBe('pending');
    expect(effectiveSnapshotState(step, true, msAfter(step, 60_000))).toBe('not-persisted');
  });

  it('keeps a recorded, withheld or omitted snapshot as it is', () => {
    for (const snapshotState of ['recorded', 'withheld', 'omitted'] as const) {
      const step = makeTransition({ snapshotState });
      expect(effectiveSnapshotState(step, true, msAfter(step, 0))).toBe(snapshotState);
    }
  });

  it('never reports pending for an unhandled or detached step', () => {
    const unhandled = makeTransition({ outcome: 'unhandled', snapshotState: 'not-persisted' });
    const detached = makeTransition({ trigger: 'detached', snapshotState: 'not-persisted' });

    expect(effectiveSnapshotState(unhandled, true, msAfter(unhandled, 0))).toBe('not-persisted');
    expect(effectiveSnapshotState(detached, true, msAfter(detached, 0))).toBe('not-persisted');
  });

  it('reads a row stamped slightly ahead of the browser clock as young', () => {
    const step = last(foldTimeline(inFlight));

    expect(effectiveSnapshotState(step, true, msAfter(step, -200))).toBe('pending');
    expect(effectiveSnapshotState(step, true, msAfter(step, -PENDING_SNAPSHOT_MS))).toBe(
      'missing',
    );
  });

  it('reads the seven fractional digits .NET writes', () => {
    const step = makeTransition({
      snapshotState: 'missing',
      lastOccurredAtUtc: '2026-01-01T00:00:00.1234567+00:00',
    });

    expect(effectiveSnapshotState(step, true, Date.parse('2026-01-01T00:00:01.123Z'))).toBe(
      'pending',
    );
    expect(effectiveSnapshotState(step, true, Date.parse('2026-01-01T00:01:00.000Z'))).toBe(
      'missing',
    );
  });

  it('treats an unreadable time as old', () => {
    const step = makeTransition({ snapshotState: 'missing', lastOccurredAtUtc: 'not a time' });

    expect(effectiveSnapshotState(step, true, Date.parse(T0))).toBe('missing');
  });
});
