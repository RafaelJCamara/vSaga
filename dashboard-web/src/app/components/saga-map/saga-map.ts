import { ChangeDetectionStrategy, Component, computed, effect, input, linkedSignal, output, signal, untracked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { SagaMap as SagaMapModel, SagaMapEvent } from '../../models/saga.model';
import { entryTypeLabel } from '../../util/entry-type-label';
import { RecordedAt, formatRecordedAt } from '../../util/time-format';
import {
  computeEdgeStates,
  computeLayout,
  computeNodeStates,
  FocusResolution,
  LayoutEdge,
  LayoutNode,
  pointOnCubic,
  ReplayVisualState,
  resolveFocusIndex,
  stepDelayMs,
} from './saga-map-layout';

const SPEEDS = [0.5, 1, 2, 4] as const;

function sameFocus(a: FocusResolution | null, b: FocusResolution | null): boolean {
  if (a === null || b === null) return a === b;
  return a.index === b.index && a.exact === b.exact && a.requested === b.requested;
}

export interface EdgeView extends LayoutEdge {
  state: ReplayVisualState;
  isCompensation: boolean;
  unanswered: boolean;
}

export interface NodeView extends LayoutNode {
  state: ReplayVisualState;
}

@Component({
  selector: 'app-saga-map',
  imports: [CommonModule],
  templateUrl: './saga-map.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './saga-map.scss',
})
export class SagaMap {
  readonly map = input.required<SagaMapModel>();
  /** The sequence number of the timeline entry to show the map as of; null for a free replay. */
  readonly focusSequence = input<number | null>(null);
  /** Whether the viewer may see saga data: the failure card names the entry either way, but shows the
   *  error text (exception messages, which often name customers or ids) only with it. */
  readonly canViewData = input(true);
  /**
   * The viewer took over the replay (play, restart, step or scrub), whether or not a focus was
   * active: a focus that arrives afterwards (the page's default failure focus) must not jump it.
   */
  readonly focusCleared = output<void>();
  /** The viewer asked to see the focused entry in the timeline; carries its sequence number. */
  readonly timelineRequested = output<number>();

  /** The focus as given, until the viewer releases it; a new focusSequence pins it again. */
  private readonly activeFocus = linkedSignal(() => this.focusSequence());

  /**
   * The event the replay stands on for the focused entry. Re-resolved when the map is refreshed, so
   * a fallback to an earlier event moves onto the entry itself once the map has caught up.
   */
  readonly focus = computed(() => resolveFocusIndex(this.map().events, this.activeFocus()), { equal: sameFocus });

  readonly currentIndex = signal(0);
  readonly progress = signal(0);
  readonly playing = signal(false);
  readonly speed = signal<(typeof SPEEDS)[number]>(1);

  readonly speeds = SPEEDS;
  readonly reducedMotion = typeof window !== 'undefined' && (window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false);

  readonly layout = computed(() => computeLayout(this.map()));

  readonly edgeViews = computed<EdgeView[]>(() => {
    const states = computeEdgeStates(this.map(), this.currentIndex());
    const byId = new Map(this.map().edges.map((e) => [e.id, e]));

    return this.layout().edges.map((edge) => ({
      ...edge,
      state: states.get(edge.id) ?? 'pending',
      isCompensation: byId.get(edge.id)?.isCompensation ?? false,
      unanswered: byId.get(edge.id)?.unanswered ?? false,
    }));
  });

  readonly nodeViews = computed<NodeView[]>(() => {
    const states = computeNodeStates(this.map(), this.currentIndex());
    return this.layout().nodes.map((node) => ({ ...node, state: states.get(node.id) ?? 'pending' }));
  });

  readonly currentEvent = computed<SagaMapEvent | null>(() => this.map().events[this.currentIndex()] ?? null);

  readonly failureReached = computed(() => {
    const failureIndex = this.map().failureEventIndex;
    return failureIndex !== null && this.currentIndex() >= failureIndex;
  });

  readonly failureEvent = computed<SagaMapEvent | null>(() => {
    const failureIndex = this.map().failureEventIndex;
    return failureIndex === null ? null : (this.map().events[failureIndex] ?? null);
  });

  /**
   * A saga that fails on its very first outbound publish (e.g. an unroutable-publish exception, thrown
   * before any edge is ever logged) has nothing at all to draw -- one bare node, no edges, and the
   * scrub-triggered error-card below stays hidden until you've clicked Play/scrubbed to the failure,
   * which there's nothing to prompt you to do. Surfaced unconditionally, not gated on replay position,
   * only when there's essentially nothing else on the canvas already telling that story.
   */
  readonly failedWithNothingToShow = computed(
    () => this.map().failureEventIndex !== null && this.layout().nodes.length <= 1 && this.layout().edges.length === 0,
  );

  readonly atEnd = computed(() => this.currentIndex() >= this.map().events.length - 1);

  /** The current event's labelled time, measured from the map's first event. */
  readonly currentAt = computed<RecordedAt | null>(() => this.recordedAt(this.currentEvent()));

  readonly focusEvent = computed<SagaMapEvent | null>(() => {
    const focus = this.focus();
    return focus === null ? null : (this.map().events[focus.index] ?? null);
  });

  readonly focusAt = computed<RecordedAt | null>(() => this.recordedAt(this.focusEvent()));

  /**
   * How the replay stands in for an entry the map does not hold: 'earlier' on the closest earlier
   * event (a map fetched before the entry was recorded), 'first' on the first event when every event
   * comes after the requested one (a hand-typed or foreign ?entry=), null when the entry is on the map.
   */
  readonly focusFallback = computed<'earlier' | 'first' | null>(() => {
    const focus = this.focus();
    const event = this.focusEvent();
    if (focus === null || focus.exact || event === null) return null;
    return event.sequenceNumber < focus.requested ? 'earlier' : 'first';
  });

  /**
   * Whether the focused entry moved nothing between services (a step outcome, a timeout scheduled,
   * the saga finishing): no edge or node of its own, so the orchestrator stands for it. Drawn as its
   * own class rather than through computeNodeStates, so a failed orchestrator keeps its failed style
   * and ordinary playback is unchanged.
   */
  readonly focusOnOrchestrator = computed(() => {
    const event = this.focusEvent();
    return event !== null && !event.edgeId && !event.nodeId;
  });

  readonly orchestratorName = computed(
    () => this.map().nodes.find((n) => n.kind === 'Orchestrator')?.displayName ?? this.map().summary.sagaType,
  );

  constructor() {
    // Component effects run during change detection before the view is checked, so a map created
    // with a focus (the Map tab opening on a jump) renders on the entry, never on event 1 first.
    effect(() => {
      const focus = this.focus();
      if (focus) untracked(() => this.scrubTo(focus.index));
    });
  }

  label(entryType: string): string {
    return entryTypeLabel(entryType);
  }

  private recordedAt(event: SagaMapEvent | null): RecordedAt | null {
    if (event === null) return null;
    return formatRecordedAt(event.occurredAtUtc, this.map().events[0]?.occurredAtUtc ?? null);
  }

  /** Back to the timeline, on the entry the map was opened for. */
  showInTimeline(): void {
    const focus = this.focus();
    if (focus) this.timelineRequested.emit(focus.requested);
  }

  /**
   * The viewer took over the replay: drop the focus and tell the page on every take-over, even with
   * no focus active, so the page can keep a focus it was about to apply from snapping the replay.
   */
  private releaseFocus(): void {
    this.activeFocus.set(null);
    this.focusCleared.emit();
  }

  readonly tokenPosition = computed(() => {
    if (this.reducedMotion) return null;

    const event = this.currentEvent();
    if (!event?.edgeId) return null;

    const edge = this.layout().edges.find((e) => e.id === event.edgeId);
    if (!edge) return null;

    return pointOnCubic(edge.p0, edge.c1, edge.c2, edge.p3, this.progress());
  });

  private lastFrameTime: number | null = null;

  private readonly loop = (time: number): void => {
    if (!this.playing()) return;

    const delta = this.lastFrameTime === null ? 0 : time - this.lastFrameTime;
    this.lastFrameTime = time;
    this.tick(delta);

    if (this.playing()) requestAnimationFrame(this.loop);
  };

  /** Advances the replay by `deltaMs` of wall-clock time. Public and side-effect-free enough to call directly in tests — no rAF faking needed. */
  tick(deltaMs: number): void {
    const events = this.map().events;
    if (events.length === 0 || this.currentIndex() >= events.length - 1) {
      this.playing.set(false);
      return;
    }

    const nextIndex = this.currentIndex() + 1;
    const delay = stepDelayMs(this.map(), nextIndex, this.speed());
    const nextProgress = this.progress() + deltaMs / delay;

    if (nextProgress < 1) {
      this.progress.set(nextProgress);
      return;
    }

    this.currentIndex.set(nextIndex);
    this.progress.set(0);

    const failureIndex = this.map().failureEventIndex;
    if (failureIndex !== null && nextIndex >= failureIndex) this.playing.set(false);
  }

  play(): void {
    if (this.playing()) return;
    this.releaseFocus();
    if (this.atEnd()) this.restart();

    this.playing.set(true);
    this.lastFrameTime = null;
    requestAnimationFrame(this.loop);
  }

  pause(): void {
    this.playing.set(false);
  }

  restart(): void {
    this.releaseFocus();
    this.currentIndex.set(0);
    this.progress.set(0);
  }

  stepForward(): void {
    this.releaseFocus();
    this.playing.set(false);
    if (!this.atEnd()) {
      this.currentIndex.update((i) => i + 1);
      this.progress.set(0);
    }
  }

  /** Positions the replay; keeps the focus, because the focus itself positions through here. */
  scrubTo(index: number): void {
    this.playing.set(false);
    this.progress.set(0);
    this.currentIndex.set(Math.max(0, Math.min(index, this.map().events.length - 1)));
  }

  onScrubInput(event: Event): void {
    this.releaseFocus();
    const value = Number((event.target as HTMLInputElement).value);
    this.scrubTo(value);
  }

  setSpeed(speed: (typeof SPEEDS)[number]): void {
    this.speed.set(speed);
  }
}
