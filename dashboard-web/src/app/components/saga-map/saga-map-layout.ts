import { SagaMap, SagaMapNode } from '../../models/saga.model';

/** Zero-Angular-import geometry/layout/replay-derivation module — the pure, DOM-free part of the map. */

export interface Point {
  x: number;
  y: number;
}

export interface LayoutNode {
  id: string;
  displayName: string;
  kind: SagaMapNode['kind'];
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface LayoutEdge {
  id: string;
  fromNodeId: string;
  toNodeId: string;
  d: string;
  p0: Point;
  c1: Point;
  c2: Point;
  p3: Point;
}

export interface SagaMapLayout {
  nodes: LayoutNode[];
  edges: LayoutEdge[];
  width: number;
  height: number;
}

const NODE_WIDTH = 168;
const NODE_HEIGHT = 52;
const ROW_GAP = 88;
const PADDING = 40;
const ORCHESTRATOR_X = PADDING;
const PARTICIPANT_X = ORCHESTRATOR_X + NODE_WIDTH + 220;
const CURVE_BASE = 34;
const CURVE_FAN = 18;

/**
 * Order nodes by first appearance in the replay script (Events), not the API's alphabetical node
 * list — this is what makes the layout deterministic for a given saga instance while still reading
 * left-to-right in the order things actually happened.
 */
function firstContactOrder(map: SagaMap): string[] {
  const seen = new Set<string>();
  const order: string[] = [];
  const add = (id: string | null | undefined) => {
    if (id && !seen.has(id)) {
      seen.add(id);
      order.push(id);
    }
  };

  const edgeById = new Map(map.edges.map((e) => [e.id, e]));
  for (const event of map.events) {
    add(event.nodeId);
    const edge = event.edgeId ? edgeById.get(event.edgeId) : undefined;
    if (edge) {
      add(edge.fromNodeId);
      add(edge.toNodeId);
    }
  }
  for (const node of map.nodes) add(node.id);

  return order;
}

/** Deterministic hub-and-spoke layout: orchestrator anchored left-centre, initiator(s) above it, participants stacked to the right in first-contact order. */
export function computeLayout(map: SagaMap): SagaMapLayout {
  const order = firstContactOrder(map);
  const byId = new Map(map.nodes.map((n) => [n.id, n]));

  const initiatorIds = order.filter((id) => byId.get(id)?.kind === 'Initiator');
  const participantIds = order.filter((id) => {
    const kind = byId.get(id)?.kind;
    return kind === 'Participant' || kind === 'Unresolved';
  });
  const orchestrator = map.nodes.find((n) => n.kind === 'Orchestrator');

  const rowCount = Math.max(initiatorIds.length + 1, participantIds.length, 1);
  const height = rowCount * ROW_GAP + PADDING * 2;
  const width = PARTICIPANT_X + NODE_WIDTH + PADDING;

  const nodes: LayoutNode[] = [];
  const positions = new Map<string, Point>();

  initiatorIds.forEach((id, i) => {
    const node = byId.get(id);
    if (!node) return;
    const center = { x: ORCHESTRATOR_X + NODE_WIDTH / 2, y: PADDING + (i + 0.5) * ROW_GAP };
    positions.set(id, center);
    nodes.push(toLayoutNode(node, center));
  });

  if (orchestrator) {
    const center = { x: ORCHESTRATOR_X + NODE_WIDTH / 2, y: PADDING + (initiatorIds.length + 0.5) * ROW_GAP };
    positions.set(orchestrator.id, center);
    nodes.push(toLayoutNode(orchestrator, center));
  }

  participantIds.forEach((id, i) => {
    const node = byId.get(id);
    if (!node) return;
    const center = { x: PARTICIPANT_X + NODE_WIDTH / 2, y: PADDING + (i + 0.5) * ROW_GAP };
    positions.set(id, center);
    nodes.push(toLayoutNode(node, center));
  });

  const directionCounts = new Map<string, number>();
  const edges: LayoutEdge[] = map.edges
    .map((edge) => {
      const from = positions.get(edge.fromNodeId);
      const to = positions.get(edge.toNodeId);
      if (!from || !to) return null;

      const key = `${edge.fromNodeId}->${edge.toNodeId}`;
      const fanIndex = directionCounts.get(key) ?? 0;
      directionCounts.set(key, fanIndex + 1);

      return buildEdge(edge.id, edge.fromNodeId, edge.toNodeId, from, to, fanIndex);
    })
    .filter((e): e is LayoutEdge => e !== null);

  return { nodes, edges, width, height };
}

function toLayoutNode(node: SagaMapNode, center: Point): LayoutNode {
  return {
    id: node.id,
    displayName: node.displayName,
    kind: node.kind,
    x: center.x - NODE_WIDTH / 2,
    y: center.y - NODE_HEIGHT / 2,
    width: NODE_WIDTH,
    height: NODE_HEIGHT,
  };
}

function buildEdge(id: string, fromNodeId: string, toNodeId: string, fromCenter: Point, toCenter: Point, fanIndex: number): LayoutEdge {
  const dx = toCenter.x - fromCenter.x;
  const dy = toCenter.y - fromCenter.y;
  const horizontal = Math.abs(dx) >= Math.abs(dy);
  const curve = (CURVE_BASE + fanIndex * CURVE_FAN) * (dx + dy >= 0 ? 1 : -1);

  const p0 = horizontal
    ? { x: fromCenter.x + Math.sign(dx || 1) * (NODE_WIDTH / 2), y: fromCenter.y }
    : { x: fromCenter.x, y: fromCenter.y + Math.sign(dy || 1) * (NODE_HEIGHT / 2) };
  const p3 = horizontal
    ? { x: toCenter.x - Math.sign(dx || 1) * (NODE_WIDTH / 2), y: toCenter.y }
    : { x: toCenter.x, y: toCenter.y - Math.sign(dy || 1) * (NODE_HEIGHT / 2) };

  const c1 = horizontal
    ? { x: p0.x + (p3.x - p0.x) * 0.4, y: p0.y + curve }
    : { x: p0.x + curve, y: p0.y + (p3.y - p0.y) * 0.4 };
  const c2 = horizontal
    ? { x: p0.x + (p3.x - p0.x) * 0.6, y: p3.y + curve }
    : { x: p0.x + curve, y: p3.y - (p3.y - p0.y) * 0.4 };

  const d = `M ${p0.x} ${p0.y} C ${c1.x} ${c1.y}, ${c2.x} ${c2.y}, ${p3.x} ${p3.y}`;

  return { id, fromNodeId, toNodeId, d, p0, c1, c2, p3 };
}

/** Evaluates a cubic Bézier at t ∈ [0,1] — used to place the replay token without <animateMotion>/offset-path, so a scrubber can drive it in either direction. */
export function pointOnCubic(p0: Point, c1: Point, c2: Point, p3: Point, t: number): Point {
  const mt = 1 - t;
  const a = mt * mt * mt;
  const b = 3 * mt * mt * t;
  const c = 3 * mt * t * t;
  const d = t * t * t;
  return {
    x: a * p0.x + b * c1.x + c * c2.x + d * p3.x,
    y: a * p0.y + b * c1.y + c * c2.y + d * p3.y,
  };
}

export type ReplayVisualState = 'pending' | 'active' | 'done' | 'failed';

/** Per-edge visual state as of currentIndex — pending (not reached yet), active (the current step), done (already happened), or failed (the failing hop). */
export function computeEdgeStates(map: SagaMap, currentIndex: number): Map<string, ReplayVisualState> {
  const states = new Map<string, ReplayVisualState>();
  for (const edge of map.edges) states.set(edge.id, 'pending');

  const lastIndex = Math.min(currentIndex, map.events.length - 1);
  for (let i = 0; i <= lastIndex; i++) {
    const event = map.events[i];
    if (!event.edgeId) continue;
    const edge = map.edges.find((e) => e.id === event.edgeId);
    if (!edge) continue;
    const failed = edge.failed || i === map.failureEventIndex;
    states.set(edge.id, failed ? 'failed' : i === currentIndex ? 'active' : 'done');
  }
  return states;
}

/** Per-node visual state as of currentIndex, derived the same way as edge state — a node is "touched" by any event whose NodeId it is, or whose edge starts/ends there. */
export function computeNodeStates(map: SagaMap, currentIndex: number): Map<string, ReplayVisualState> {
  const states = new Map<string, ReplayVisualState>();
  for (const node of map.nodes) states.set(node.id, 'pending');

  const edgeById = new Map(map.edges.map((e) => [e.id, e]));
  const lastIndex = Math.min(currentIndex, map.events.length - 1);

  for (let i = 0; i <= lastIndex; i++) {
    const event = map.events[i];
    const touched: string[] = [];
    if (event.nodeId) touched.push(event.nodeId);
    const edge = event.edgeId ? edgeById.get(event.edgeId) : undefined;
    if (edge) touched.push(edge.fromNodeId, edge.toNodeId);

    // Covers both failure shapes: an exception/timeout (i === failureEventIndex, no edge to blame)
    // and a business failure carried by a reply message (edge.failed, no StepFailed/TimeoutFired
    // entry exists to set failureEventIndex at all) — see SagaMapBuilder.ResolveFailedMessageIds.
    const failed = i === map.failureEventIndex || (edge?.failed ?? false);
    const nextState: ReplayVisualState = failed ? 'failed' : i === currentIndex ? 'active' : 'done';

    for (const id of touched) {
      if (states.get(id) !== 'failed') states.set(id, nextState);
    }
  }
  return states;
}

const MIN_STEP_DELAY_MS = 1000;
const MAX_STEP_DELAY_MS = 5000;
const DEFAULT_STEP_DELAY_MS = 2000;

/** How long the step landing on `index` should take, from the real gap to the previous event's timestamp, clamped and divided by playback speed. */
export function stepDelayMs(map: SagaMap, index: number, speed: number): number {
  if (index <= 0 || index >= map.events.length) return DEFAULT_STEP_DELAY_MS / speed;

  const prev = Date.parse(map.events[index - 1].occurredAtUtc);
  const curr = Date.parse(map.events[index].occurredAtUtc);
  const gap = Number.isFinite(prev) && Number.isFinite(curr) ? curr - prev : DEFAULT_STEP_DELAY_MS;
  const clamped = Math.min(MAX_STEP_DELAY_MS, Math.max(MIN_STEP_DELAY_MS, gap));

  return clamped / speed;
}
