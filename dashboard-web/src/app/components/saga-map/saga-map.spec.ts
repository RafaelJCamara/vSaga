import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { SagaMap as SagaMapModel } from '../../models/saga.model';
import { GUIDE_ANCHORS, GUIDE_TOURS } from '../guide-overlay/guide-tours';
import { guideAreaOf } from '../../services/guide-areas';
import { formatRecordedAt } from '../../util/time-format';
import { SagaMap } from './saga-map';

const text = (el: Element | null | undefined) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

function makeMap(): SagaMapModel {
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
        id: 'e2',
        fromNodeId: 'OrderSaga',
        toNodeId: 'InventoryService',
        messageType: 'ReserveInventory',
        messageId: 'out-1',
        isCompensation: false,
        failed: false,
        unanswered: false,
        occurredAtUtc: '2026-01-01T00:00:00.300Z',
      },
    ],
    events: [
      { sequenceNumber: 1, edgeId: 'e1', nodeId: null, entryType: 'SagaStarted', messageType: 'OrderSubmitted', errorMessage: null, occurredAtUtc: '2026-01-01T00:00:00.000Z' },
      { sequenceNumber: 2, edgeId: 'e2', nodeId: null, entryType: 'MessagePublished', messageType: 'ReserveInventory', errorMessage: null, occurredAtUtc: '2026-01-01T00:00:00.300Z' },
      { sequenceNumber: 3, edgeId: null, nodeId: 'OrderSaga', entryType: 'StepFailed', messageType: null, errorMessage: 'boom', occurredAtUtc: '2026-01-01T00:00:00.600Z' },
    ],
    failureEventIndex: 2,
  };
}

function createComponent(map: SagaMapModel = makeMap(), focusSequence?: number | null) {
  const fixture = TestBed.createComponent(SagaMap);
  fixture.componentRef.setInput('map', map);
  if (focusSequence !== undefined) fixture.componentRef.setInput('focusSequence', focusSequence);
  fixture.detectChanges();
  return fixture;
}

describe('SagaMap', () => {
  it('starts at index 0 with all edges pending except the first', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;

    expect(component.currentIndex()).toBe(0);
    expect(component.edgeViews().find((e) => e.id === 'e1')?.state).toBe('active');
    expect(component.edgeViews().find((e) => e.id === 'e2')?.state).toBe('pending');
  });

  it('tick() advances progress and rolls over into the next step once the delay elapses', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.playing.set(true);

    component.tick(150); // e2's real gap clamps to the 1000ms minimum — this only advances progress
    expect(component.currentIndex()).toBe(0);
    expect(component.progress()).toBeGreaterThan(0);

    component.tick(1000); // enough to roll over
    expect(component.currentIndex()).toBe(1);
    expect(component.progress()).toBe(0);
  });

  it('force-pauses once it reaches the failure index', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.playing.set(true);
    component.currentIndex.set(1);

    component.tick(5000); // far more than any clamped delay — guarantees rollover to index 2 (the failure index)

    expect(component.currentIndex()).toBe(2);
    expect(component.playing()).toBe(false);
    expect(component.failureReached()).toBe(true);
  });

  it('scrubTo jumps directly to an index and stops playback', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.playing.set(true);

    component.scrubTo(2);

    expect(component.currentIndex()).toBe(2);
    expect(component.playing()).toBe(false);
    expect(component.failureReached()).toBe(true);
  });

  it('scrubTo clamps out-of-range indices', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;

    component.scrubTo(999);
    expect(component.currentIndex()).toBe(2);

    component.scrubTo(-5);
    expect(component.currentIndex()).toBe(0);
  });

  it('restart resets to the beginning', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.scrubTo(1);

    component.restart();

    expect(component.currentIndex()).toBe(0);
    expect(component.progress()).toBe(0);
  });

  it('applies the failed class to the orchestrator node once the failure step is reached', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.scrubTo(2);
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    const failedNode = Array.from(el.querySelectorAll('.node--failed'));
    expect(failedNode.length).toBeGreaterThan(0);
  });

  it('renders the error card with the failure message once reached', () => {
    const fixture = createComponent();
    fixture.componentInstance.scrubTo(2);
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.error-card')?.textContent).toContain('boom');
  });

  // A viewer without sagas.data gets the failure entry with its error message nulled: the card still
  // names the entry, with no text under it.
  it('renders the error card without a message when the API withheld the error text', () => {
    const map = makeMap();
    map.events = map.events.map((e) => ({ ...e, errorMessage: null }));
    const fixture = createComponent(map);
    fixture.componentInstance.scrubTo(2);
    fixture.detectChanges();

    const card = (fixture.nativeElement as HTMLElement).querySelector('.error-card');
    expect(card?.querySelector('strong')?.textContent).toBeTruthy();
    expect(card?.querySelector('p')).toBeNull();
  });

  // The API nulls the text for a viewer without sagas.data, but the map a page holds can be older than the
  // session (the permission was revoked since): the card must not print what it still carries.
  it('prints no error text without canViewData, whatever the map carries, and keeps naming the entry', () => {
    const fixture = TestBed.createComponent(SagaMap);
    fixture.componentRef.setInput('map', makeMap());
    fixture.componentRef.setInput('canViewData', false);
    fixture.detectChanges();
    fixture.componentInstance.scrubTo(2);
    fixture.detectChanges();

    const card = (fixture.nativeElement as HTMLElement).querySelector('.error-card');
    expect(card?.querySelector('strong')?.textContent).toBeTruthy();
    expect(card?.textContent).not.toContain('boom');

    fixture.componentRef.setInput('canViewData', true);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('.error-card')?.textContent).toContain('boom');
  });

  it('prints no error text without canViewData on a map with nothing to draw either', () => {
    const fixture = TestBed.createComponent(SagaMap);
    fixture.componentRef.setInput('map', makeBareFailedMap());
    fixture.componentRef.setInput('canViewData', false);
    fixture.detectChanges();

    const card = (fixture.nativeElement as HTMLElement).querySelector('.error-card');
    expect(card?.textContent).toContain('This saga failed with nothing to map');
    expect(card?.textContent).not.toContain('unroutable');
  });

  it('does not render the error card before the failure step', () => {
    const fixture = createComponent();
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.error-card')).toBeNull();
  });

  // A saga that fails on its very first outbound publish -- an unroutable-publish exception, thrown
  // before any edge is ever logged -- has one bare node and nothing else to draw. Without this, the
  // map tab (the default tab on the detail page) shows nothing indicating failure until the user
  // presses Play/scrubs there themselves, with nothing on screen to prompt that.
  function makeBareFailedMap(): SagaMapModel {
    return {
      summary: { ...makeMap().summary, currentState: 'AwaitingApproval' },
      nodes: [{ id: 'OrderApprovalSaga', displayName: 'OrderApprovalSaga', kind: 'Orchestrator', status: 'failed', messagesIn: 1, messagesOut: 0 }],
      edges: [],
      events: [
        { sequenceNumber: 1, edgeId: null, nodeId: null, entryType: 'SagaStarted', messageType: 'SubmitOrder', errorMessage: null, occurredAtUtc: '2026-01-01T00:00:00.000Z' },
        { sequenceNumber: 2, edgeId: null, nodeId: null, entryType: 'MessageReceived', messageType: 'SubmitOrder', errorMessage: null, occurredAtUtc: '2026-01-01T00:00:00.050Z' },
        { sequenceNumber: 3, edgeId: null, nodeId: 'OrderApprovalSaga', entryType: 'StepFailed', messageType: 'SubmitOrder', errorMessage: 'unroutable', occurredAtUtc: '2026-01-01T00:00:00.080Z' },
      ],
      failureEventIndex: 2,
    };
  }

  it('flags failedWithNothingToShow for a saga that failed with no edges ever logged', () => {
    const fixture = createComponent(makeBareFailedMap());

    expect(fixture.componentInstance.failedWithNothingToShow()).toBe(true);
  });

  it('renders the error card immediately (no scrubbing needed) for a bare failed map', () => {
    const fixture = createComponent(makeBareFailedMap());

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.error-card')?.textContent).toContain('unroutable');
  });

  it('does not flag failedWithNothingToShow for a map with real edges', () => {
    const fixture = createComponent();

    expect(fixture.componentInstance.failedWithNothingToShow()).toBe(false);
  });

  it('shows when the current entry was recorded in the status line', () => {
    const fixture = createComponent();
    fixture.componentInstance.scrubTo(1);
    fixture.detectChanges();

    const at = formatRecordedAt('2026-01-01T00:00:00.300Z', '2026-01-01T00:00:00.000Z');
    const line = text(fixture.nativeElement.querySelector('.status-line'));
    expect(line).toBe(`#2/3 — MessagePublished · ReserveInventory · recorded at ${at.local} (+0.300 s)`);
    expect(fixture.nativeElement.querySelector('.status-line time')?.getAttribute('title')).toBe('2026-01-01 00:00:00.300 UTC');
  });

  describe('focused on a timeline entry', () => {
    /** makeMap plus the saga finishing after the failure: an entry that moves nothing. */
    function makeFinishedMap(): SagaMapModel {
      const map = makeMap();
      return {
        ...map,
        events: [
          ...map.events,
          { sequenceNumber: 5, edgeId: null, nodeId: null, entryType: 'SagaCompleted', messageType: null, errorMessage: null, occurredAtUtc: '2026-01-01T00:00:00.900Z' },
        ],
      };
    }

    const banner = (fixture: { nativeElement: HTMLElement }) => fixture.nativeElement.querySelector('.focus-banner');
    const orchestrator = (fixture: { nativeElement: HTMLElement }) =>
      fixture.nativeElement.querySelector('.node--orchestrator') as SVGGElement;

    afterEach(() => vi.unstubAllGlobals());

    it('opens on the entry itself, before the first render', () => {
      const fixture = createComponent(makeMap(), 2);

      expect(fixture.componentInstance.currentIndex()).toBe(1);
      expect(fixture.componentInstance.playing()).toBe(false);
      const at = formatRecordedAt('2026-01-01T00:00:00.300Z', '2026-01-01T00:00:00.000Z');
      const b = banner(fixture);
      expect(b?.getAttribute('role')).toBe('status');
      expect(text(b?.querySelector('.focus-title'))).toBe(
        `As of entry #2 of 3: MessagePublished, recorded at ${at.local} (+0.300 s)`,
      );
      expect(text(b)).not.toContain('not on the map yet');
      expect(text(b)).not.toContain('Nothing moved');
    });

    it('falls back to the closest earlier entry and says so', () => {
      const fixture = createComponent(makeMap(), 4);

      expect(fixture.componentInstance.currentIndex()).toBe(2);
      expect(text(banner(fixture))).toContain('As of entry #3 of 3: StepFailed');
      expect(text(banner(fixture))).toContain('The selected entry is not on the map yet; showing the closest earlier entry.');
      // The stored sequence number (global on a shared store) never appears; the title's #N is the ordinal.
      expect(text(banner(fixture))).not.toContain('Entry 4');
      expect(text(banner(fixture))).not.toContain("not on this saga's map");
    });

    it('shows the first entry for an entry earlier than every event, without promising it will appear', () => {
      const map = makeMap();
      const shifted = { ...map, events: map.events.map((e) => ({ ...e, sequenceNumber: e.sequenceNumber + 1200 })) };
      const fixture = createComponent(shifted, 3);

      expect(fixture.componentInstance.currentIndex()).toBe(0);
      expect(fixture.componentInstance.focusFallback()).toBe('first');
      expect(text(banner(fixture))).toContain('As of entry #1 of 3: SagaStarted');
      expect(text(banner(fixture))).toContain("The selected entry is not on this saga's map; showing its first entry.");
      expect(text(banner(fixture))).not.toContain('closest earlier');
      expect(text(banner(fixture))).not.toContain('not on the map yet');
    });

    it('moves onto the entry once a refreshed map holds it', () => {
      const fixture = createComponent(makeMap(), 5);
      expect(fixture.componentInstance.currentIndex()).toBe(2);

      fixture.componentRef.setInput('map', makeFinishedMap());
      fixture.detectChanges();

      expect(fixture.componentInstance.currentIndex()).toBe(3);
      expect(text(banner(fixture))).not.toContain('not on the map yet');
    });

    it('outlines the orchestrator for an entry that moved nothing between services, and names it', () => {
      const fixture = createComponent(makeFinishedMap(), 5);

      expect(orchestrator(fixture).classList).toContain('node--focus');
      expect(orchestrator(fixture).querySelector('.focus-ring')).not.toBeNull();
      expect(fixture.nativeElement.querySelectorAll('.node--focus').length).toBe(1);
      expect(text(banner(fixture))).toContain('As of entry #4 of 4: SagaFinalized');
      expect(text(banner(fixture))).toContain('Nothing moved between services at this entry, so OrderSaga is highlighted.');
    });

    it('keeps a failed orchestrator failed while it is outlined for the focus', () => {
      const fixture = createComponent(makeFinishedMap(), 5);

      expect(orchestrator(fixture).classList).toContain('node--failed');
      expect(orchestrator(fixture).classList).toContain('node--focus');
    });

    it('outlines nothing for an entry that moved a message', () => {
      const fixture = createComponent(makeFinishedMap(), 2);

      expect(fixture.nativeElement.querySelector('.node--focus')).toBeNull();
      expect(fixture.nativeElement.querySelector('.focus-ring')).toBeNull();
    });

    it('shows no banner and no outline without a focus', () => {
      const fixture = createComponent(makeFinishedMap(), null);

      expect(banner(fixture)).toBeNull();
      expect(fixture.nativeElement.querySelector('.node--focus')).toBeNull();
      expect(fixture.componentInstance.currentIndex()).toBe(0);
    });

    it.each<[string, (fixture: ReturnType<typeof createComponent>) => void]>([
      [
        'play',
        (fixture) => {
          vi.stubGlobal('requestAnimationFrame', vi.fn(() => 0));
          fixture.componentInstance.play();
        },
      ],
      ['restart', (fixture) => fixture.componentInstance.restart()],
      ['step forward', (fixture) => fixture.componentInstance.stepForward()],
      [
        'the scrubber',
        (fixture) => {
          const scrubber = fixture.nativeElement.querySelector('.scrubber') as HTMLInputElement;
          scrubber.value = '0';
          scrubber.dispatchEvent(new Event('input'));
        },
      ],
    ])('releases the focus once on %s, and a later map refresh does not pin it again', (_, act) => {
      const fixture = createComponent(makeFinishedMap(), 2);
      const cleared = vi.fn();
      fixture.componentInstance.focusCleared.subscribe(cleared);

      act(fixture);
      fixture.detectChanges();
      const index = fixture.componentInstance.currentIndex();

      expect(cleared).toHaveBeenCalledTimes(1);
      expect(fixture.componentInstance.focus()).toBeNull();
      expect(banner(fixture)).toBeNull();

      fixture.componentInstance.pause();
      fixture.componentRef.setInput('map', { ...makeFinishedMap() });
      fixture.detectChanges();
      expect(fixture.componentInstance.currentIndex()).toBe(index);
      expect(fixture.componentInstance.focus()).toBeNull();
      expect(banner(fixture)).toBeNull();
      expect(cleared).toHaveBeenCalledTimes(1);
    });

    it('tells the page about a take-over even when no focus is active', () => {
      const fixture = createComponent(makeFinishedMap(), null);
      const cleared = vi.fn();
      fixture.componentInstance.focusCleared.subscribe(cleared);

      fixture.componentInstance.stepForward();
      fixture.componentInstance.restart();

      expect(cleared).toHaveBeenCalledTimes(2);
    });

    it('keeps the focus when the replay is positioned through scrubTo', () => {
      const fixture = createComponent(makeFinishedMap(), 2);
      const cleared = vi.fn();
      fixture.componentInstance.focusCleared.subscribe(cleared);

      fixture.componentInstance.scrubTo(1);

      expect(cleared).not.toHaveBeenCalled();
      expect(fixture.componentInstance.focus()).not.toBeNull();
    });

    it('asks for the requested entry in the timeline from the banner', () => {
      const fixture = createComponent(makeMap(), 4);
      const requested = vi.fn();
      fixture.componentInstance.timelineRequested.subscribe(requested);

      const button = banner(fixture)?.querySelector('button') as HTMLButtonElement;
      expect(text(button)).toBe('Back to this entry in the timeline');
      button.click();

      expect(requested).toHaveBeenCalledWith(4);
    });

    it('pins the focus again when the page focuses another entry', () => {
      const fixture = createComponent(makeFinishedMap(), 2);
      fixture.componentInstance.restart();
      fixture.detectChanges();

      fixture.componentRef.setInput('focusSequence', 5);
      fixture.detectChanges();

      expect(fixture.componentInstance.currentIndex()).toBe(3);
      expect(banner(fixture)).not.toBeNull();
    });
  });

  describe('the anchors of the guide tour', () => {
    const anchors = (el: Element) => Array.from(el.querySelectorAll('[data-tour]'), (e) => e.getAttribute('data-tour')).sort();

    it('marks the canvas and the controls, once each, and nothing else', () => {
      const el: HTMLElement = createComponent().nativeElement;

      expect(anchors(el)).toEqual(['map-canvas', 'map-controls']);
      expect(el.querySelector('svg.canvas')?.getAttribute('data-tour')).toBe('map-canvas');
      expect(el.querySelector('.controls')?.getAttribute('data-tour')).toBe('map-controls');
    });

    it('keeps them through a focus on an entry and the banner that comes with it', () => {
      const el: HTMLElement = createComponent(makeMap(), 2).nativeElement;

      expect(el.querySelector('.focus-banner')).not.toBeNull();
      expect(anchors(el)).toEqual(['map-canvas', 'map-controls']);
    });

    it('uses names of the vocabulary, and has every element the map tour and the map area point at', () => {
      const el: HTMLElement = createComponent().nativeElement;
      const wanted = [guideAreaOf('map')?.readyAnchor, ...GUIDE_TOURS.map.flatMap((s) => [s.anchor, s.fallbackAnchor, s.reveal])];

      for (const name of anchors(el)) expect(GUIDE_ANCHORS).toContain(name);
      for (const name of new Set(wanted.filter((n) => !!n))) expect(el.querySelector(`[data-tour="${name}"]`), `${name}`).not.toBeNull();
    });

    it('names the controls the tour names: Restart, Play, Step forward, the slider and the speeds', () => {
      const el: HTMLElement = createComponent().nativeElement;
      const controls = el.querySelector('.controls') as HTMLElement;

      expect(Array.from(controls.querySelectorAll('button[aria-label]'), (b) => b.getAttribute('aria-label'))).toEqual(['Restart', 'Play', 'Step forward']);
      expect(controls.querySelector('input[type="range"]')).not.toBeNull();
      expect(Array.from(controls.querySelectorAll('.speeds button'), (b) => b.textContent?.trim())).toEqual(['0.5×', '1×', '2×', '4×']);
    });
  });
});
