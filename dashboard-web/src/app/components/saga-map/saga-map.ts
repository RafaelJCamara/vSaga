import { Component, computed, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { SagaMap as SagaMapModel, SagaMapEvent } from '../../models/saga.model';
import { computeEdgeStates, computeLayout, computeNodeStates, LayoutEdge, LayoutNode, pointOnCubic, ReplayVisualState, stepDelayMs } from './saga-map-layout';

const SPEEDS = [0.5, 1, 2, 4] as const;

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
  styleUrl: './saga-map.scss',
})
export class SagaMap {
  readonly map = input.required<SagaMapModel>();

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
    if (this.atEnd()) this.restart();

    this.playing.set(true);
    this.lastFrameTime = null;
    requestAnimationFrame(this.loop);
  }

  pause(): void {
    this.playing.set(false);
  }

  restart(): void {
    this.currentIndex.set(0);
    this.progress.set(0);
  }

  stepForward(): void {
    this.playing.set(false);
    if (!this.atEnd()) {
      this.currentIndex.update((i) => i + 1);
      this.progress.set(0);
    }
  }

  scrubTo(index: number): void {
    this.playing.set(false);
    this.progress.set(0);
    this.currentIndex.set(Math.max(0, Math.min(index, this.map().events.length - 1)));
  }

  onScrubInput(event: Event): void {
    const value = Number((event.target as HTMLInputElement).value);
    this.scrubTo(value);
  }

  setSpeed(speed: (typeof SPEEDS)[number]): void {
    this.speed.set(speed);
  }
}
