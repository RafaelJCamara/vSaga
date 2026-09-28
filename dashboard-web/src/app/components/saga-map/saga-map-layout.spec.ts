import { SagaMap } from '../../models/saga.model';
import { computeEdgeStates, computeLayout, computeNodeStates, pointOnCubic, stepDelayMs } from './saga-map-layout';

function makeMap(): SagaMap {
  return {
    summary: {
      correlationId: 'c1',
      sagaType: 'OrderSaga',
      kind: 'Orchestrated',
      currentState: 'Failed',
      status: 'Failed',
      createdAtUtc: '2026-01-01T00:00:00.000Z',
      updatedAtUtc: '2026-01-01T00:00:31.000Z',
      version: 1,
      parentSagaType: null,
      parentCorrelationId: null,
    },
    nodes: [
      { id: 'OrderSaga', displayName: 'OrderSaga', kind: 'Orchestrator', status: 'failed', messagesIn: 1, messagesOut: 2 },
      { id: 'OrderSubmitter', displayName: 'OrderSubmitter', kind: 'Initiator', status: 'ok', messagesIn: 0, messagesOut: 1 },
      { id: 'InventoryService', displayName: 'InventoryService', kind: 'Participant', status: 'ok', messagesIn: 1, messagesOut: 1 },
      { id: 'PaymentService', displayName: 'PaymentService', kind: 'Participant', status: 'unanswered', messagesIn: 1, messagesOut: 0 },
    ],
    edges: [
      {
        id: 'e1',
        fromNodeId: 'OrderSubmitter',
        toNodeId: 'OrderSaga',
        messageType: 'OrderSubmitted',
        messageId: 'm0',
        isCompensation: false,
        failed: false,
        unanswered: false,
        occurredAtUtc: '2026-01-01T00:00:00.000Z',
      },
      {
        id: 'e2-InventoryService',
        fromNodeId: 'OrderSaga',
        toNodeId: 'InventoryService',
        messageType: 'ReserveInventory',
        messageId: 'out-1',
        isCompensation: false,
        failed: false,
        unanswered: false,
        occurredAtUtc: '2026-01-01T00:00:00.300Z',
      },
      {
        id: 'e3',
        fromNodeId: 'InventoryService',
        toNodeId: 'OrderSaga',
        messageType: 'InventoryReserved',
        messageId: 'm1',
        isCompensation: false,
        failed: false,
        unanswered: false,
        occurredAtUtc: '2026-01-01T00:00:00.700Z',
      },
      {
        id: 'e4-PaymentService',
        fromNodeId: 'OrderSaga',
        toNodeId: 'PaymentService',
        messageType: 'ChargePayment',
        messageId: 'out-2',
        isCompensation: false,
        failed: false,
        unanswered: true,
        occurredAtUtc: '2026-01-01T00:00:01.000Z',
      },
    ],
    events: [
      { sequenceNumber: 1, edgeId: 'e1', nodeId: null, entryType: 'SagaStarted', messageType: 'OrderSubmitted', errorMessage: null, occurredAtUtc: '2026-01-01T00:00:00.000Z' },
      { sequenceNumber: 2, edgeId: 'e2-InventoryService', nodeId: null, entryType: 'MessagePublished', messageType: 'ReserveInventory', errorMessage: null, occurredAtUtc: '2026-01-01T00:00:00.300Z' },
      { sequenceNumber: 3, edgeId: 'e3', nodeId: null, entryType: 'MessageReceived', messageType: 'InventoryReserved', errorMessage: null, occurredAtUtc: '2026-01-01T00:00:00.700Z' },
      { sequenceNumber: 4, edgeId: 'e4-PaymentService', nodeId: null, entryType: 'MessagePublished', messageType: 'ChargePayment', errorMessage: null, occurredAtUtc: '2026-01-01T00:00:01.000Z' },
      { sequenceNumber: 5, edgeId: null, nodeId: 'OrderSaga', entryType: 'TimeoutFired', messageType: null, errorMessage: 'Payment gateway timed out', occurredAtUtc: '2026-01-01T00:00:31.000Z' },
    ],
    failureEventIndex: 4,
  };
}

describe('computeLayout', () => {
  it('is deterministic for a fixed input', () => {
    const map = makeMap();
    expect(computeLayout(map)).toEqual(computeLayout(map));
  });

  it('anchors the orchestrator at a fixed left column and places the initiator above it', () => {
    const layout = computeLayout(makeMap());

    const orchestrator = layout.nodes.find((n) => n.id === 'OrderSaga')!;
    const initiator = layout.nodes.find((n) => n.id === 'OrderSubmitter')!;

    expect(initiator.x).toBe(orchestrator.x);
    expect(initiator.y).toBeLessThan(orchestrator.y);
  });

  it('stacks participants to the right in first-contact order', () => {
    const layout = computeLayout(makeMap());

    const inventory = layout.nodes.find((n) => n.id === 'InventoryService')!;
    const payment = layout.nodes.find((n) => n.id === 'PaymentService')!;
    const orchestrator = layout.nodes.find((n) => n.id === 'OrderSaga')!;

    expect(inventory.x).toBeGreaterThan(orchestrator.x);
    expect(payment.x).toBe(inventory.x);
    expect(inventory.y).toBeLessThan(payment.y); // InventoryReserved happens before ChargePayment
  });

  it('produces one Bézier path per edge, each starting and ending at a node boundary', () => {
    const layout = computeLayout(makeMap());
    expect(layout.edges).toHaveLength(4);
    for (const edge of layout.edges) {
      expect(edge.d.startsWith('M ')).toBe(true);
      expect(edge.p0).not.toEqual(edge.p3);
    }
  });
});

describe('pointOnCubic', () => {
  const p0 = { x: 0, y: 0 };
  const c1 = { x: 10, y: 20 };
  const c2 = { x: 30, y: 20 };
  const p3 = { x: 40, y: 0 };

  it('returns p0 at t=0 and p3 at t=1', () => {
    expect(pointOnCubic(p0, c1, c2, p3, 0)).toEqual(p0);
    expect(pointOnCubic(p0, c1, c2, p3, 1)).toEqual(p3);
  });

  it('is symmetric at t=0.5 for this symmetric curve', () => {
    const mid = pointOnCubic(p0, c1, c2, p3, 0.5);
    expect(mid.x).toBeCloseTo(20);
    expect(mid.y).toBeGreaterThan(0); // curves toward the control points, not a straight line
  });
});

describe('computeEdgeStates / computeNodeStates', () => {
  it('at index 0, only the first edge is active and the rest are pending', () => {
    const map = makeMap();
    const edgeStates = computeEdgeStates(map, 0);

    expect(edgeStates.get('e1')).toBe('active');
    expect(edgeStates.get('e2-InventoryService')).toBe('pending');
    expect(edgeStates.get('e4-PaymentService')).toBe('pending');
  });

  it('mid-replay, earlier edges are done and the current one is active', () => {
    const map = makeMap();
    const edgeStates = computeEdgeStates(map, 2);

    expect(edgeStates.get('e1')).toBe('done');
    expect(edgeStates.get('e2-InventoryService')).toBe('done');
    expect(edgeStates.get('e3')).toBe('active');
    expect(edgeStates.get('e4-PaymentService')).toBe('pending');
  });

  it('at the failure index, the orchestrator node is marked failed', () => {
    const map = makeMap();
    const nodeStates = computeNodeStates(map, map.failureEventIndex!);

    expect(nodeStates.get('OrderSaga')).toBe('failed');
  });

  it('a node marked failed stays failed even if touched again afterward', () => {
    const map = makeMap();
    const nodeStates = computeNodeStates(map, map.events.length - 1);

    expect(nodeStates.get('OrderSaga')).toBe('failed');
  });

  it('marks both endpoints of a failed edge as failed, even with no failureEventIndex (a business failure with no StepFailed entry)', () => {
    const map = makeMap();
    map.failureEventIndex = null;
    map.edges = map.edges.map((e) => (e.id === 'e4-PaymentService' ? { ...e, failed: true } : e));

    const nodeStates = computeNodeStates(map, map.events.length - 1);

    expect(nodeStates.get('OrderSaga')).toBe('failed');
    expect(nodeStates.get('PaymentService')).toBe('failed');
  });
});

describe('stepDelayMs', () => {
  it('clamps a real gap below the minimum up to the minimum', () => {
    const map = makeMap();
    // e2 occurs 300ms after e1 — below the [1000, 5000] floor, so it clamps up to 1000 at speed 1.
    expect(stepDelayMs(map, 1, 1)).toBe(1000);
  });

  it('clamps a very large gap (the TimeoutFired step) to the maximum', () => {
    const map = makeMap();
    expect(stepDelayMs(map, 4, 1)).toBe(5000);
  });

  it('divides by speed', () => {
    const map = makeMap();
    expect(stepDelayMs(map, 1, 2)).toBe(500);
  });
});
