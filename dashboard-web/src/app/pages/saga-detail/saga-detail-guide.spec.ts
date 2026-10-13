import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { BehaviorSubject, Subject, of } from 'rxjs';
import { vi } from 'vitest';
import { GuideService } from '../../services/guide.service';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubConnectionState, SagaHubService } from '../../services/saga-hub.service';
import {
  SagaDetail as SagaDetailModel,
  SagaLogEntry,
  SagaMap,
  SagaRetryPlan,
  SagaSummary,
} from '../../models/saga.model';
import { createAuthMock, provideAuthMock } from '../../testing/auth-mock';
import { createGuideStorage, provideGuideStorage } from '../../testing/guide';
import { SagaDetail } from './saga-detail';

// The detail page with the guide that is really there, through the router: what saga-detail.spec checks against
// a stand-in guide (which area the page announces, and when) must also hold when a real navigation ends after the
// page was created, since the guide forgets what it knew of a page then. A page that announced itself from its
// constructor would be forgotten by the time it was on screen.

const SUMMARY: SagaSummary = {
  correlationId: 'saga-1',
  sagaType: 'OrderSaga',
  kind: 'Orchestrated',
  currentState: 'Failed',
  status: 'Failed',
  createdAtUtc: '2026-01-01T00:00:00Z',
  updatedAtUtc: '2026-01-01T00:00:01Z',
  version: 2,
  parentSagaType: null,
  parentCorrelationId: null,
};
const DETAIL: SagaDetailModel = { summary: SUMMARY, dataJson: null };
const MAP: SagaMap = {
  summary: SUMMARY,
  nodes: [],
  edges: [],
  events: [],
  failureEventIndex: null,
};
const PLAN: SagaRetryPlan = {
  retryable: true,
  reason: null,
  failureKind: 'StepFailed',
  failureSequenceNumber: 6,
  step: {
    sequenceNumber: 5,
    messageType: 'PaymentCaptured',
    messageId: 'm1',
    fromState: 'Submitted',
  },
};

describe('SagaDetail with the real guide, through the router', () => {
  let guide: GuideService;
  let harness: RouterTestingHarness;
  let page: SagaDetail;
  let sagaUpdated: Subject<SagaSummary>;

  async function open(stored: unknown) {
    sagaUpdated = new Subject<SagaSummary>();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'sagas/:sagaType/:id', component: SagaDetail }]),
        provideAuthMock(createAuthMock()),
        provideGuideStorage(createGuideStorage(stored)),
        {
          provide: SagaApiService,
          useValue: {
            get: vi.fn().mockReturnValue(of(DETAIL)),
            getTimeline: vi.fn().mockReturnValue(of([] as SagaLogEntry[])),
            getMap: vi.fn().mockReturnValue(of(MAP)),
            retry: vi.fn(),
            getRetryPlan: vi.fn().mockReturnValue(of(PLAN)),
            findByCorrelationId: vi.fn().mockReturnValue(of([SUMMARY])),
            getChildren: vi.fn().mockReturnValue(of([])),
          },
        },
        {
          provide: SagaHubService,
          useValue: {
            sagaUpdated$: sagaUpdated,
            timelineEntryAdded$: new Subject(),
            connectionState$: new BehaviorSubject<SagaHubConnectionState>('connected'),
            subscribeToSaga: vi.fn().mockResolvedValue(undefined),
            unsubscribeFromSaga: vi.fn().mockResolvedValue(undefined),
            connect: vi.fn().mockResolvedValue(undefined),
          },
        },
      ],
    });
    guide = TestBed.inject(GuideService); // the app creates it first thing, before any page
    harness = await RouterTestingHarness.create();
    page = await harness.navigateByUrl('/sagas/OrderSaga/saga-1', SagaDetail);
    harness.detectChanges();
  }

  /** The saga's own update: it runs again (no retry row), or fails again. */
  const pushStatus = (status: 'Running' | 'Failed', version: number) => {
    sagaUpdated.next({ ...SUMMARY, status, currentState: status, version });
    harness.detectChanges();
  };
  const everything = { v: 1, enabled: true, seen: { summary: 1, retry: 1, map: 1, timeline: 1, data: 1 }, hintDismissed: true };

  const on = { v: 1, enabled: true, seen: {}, hintDismissed: true };

  it('explains the summary, then the retry row, then the map, each after Done on the one before', async () => {
    await open(on);

    expect(guide.request()?.area.id).toBe('summary');
    guide.started('summary');
    guide.ended('summary', true); // Done
    expect(guide.request()?.area.id).toBe('retry');
    guide.started('retry');
    guide.ended('retry', true);
    expect(guide.request()?.area.id).toBe('map');
    guide.started('map');
    guide.ended('map', true);

    expect(guide.request()).toBeNull();
    for (const id of ['summary', 'retry', 'map'] as const) expect(guide.isSeen(id), id).toBe(true);
  });

  it('leaves the page announced to Replay and the hint: the map is the part shown last', async () => {
    await open({ ...on, enabled: false, hintDismissed: false });

    expect(guide.request()).toBeNull(); // Guide is off
    expect(guide.area()?.id).toBe('map'); // announced after the navigation ended, not wiped by it
    expect(guide.showHint()).toBe(true);
  });

  // A part that goes is withdrawn: the guide stops asking for it and Replay falls back on what is still on screen.
  describe('when a part of the page goes', () => {
    it('drops the retry tour that was queued when the saga runs again, so the next one is not held up by it', async () => {
      await open(on);
      expect(guide.request()?.area.id).toBe('summary');

      pushStatus('Running', 3); // before the summary's tour is done: the retry row is gone
      guide.started('summary');
      guide.ended('summary', true);

      expect(guide.request()?.area.id).toBe('map'); // not the retry row that is no longer there
      guide.started('map');
      guide.ended('map', true);
      expect(guide.request()).toBeNull();
      expect(guide.isSeen('retry')).toBe(false); // it was not explained: it will be when the row is shown again
    });

    it('asks for the retry tour again when the saga has failed again and the row is back', async () => {
      await open(on);
      guide.started('summary');
      guide.ended('summary', true); // the retry row is asked for now
      expect(guide.request()?.area.id).toBe('retry');

      pushStatus('Running', 3); // the saga runs: the request that was waiting for the row is dropped
      expect(guide.request()?.area.id).toBe('map');
      pushStatus('Failed', 4);

      guide.started('map');
      guide.ended('map', true);
      expect(guide.request()?.area.id).toBe('retry');
    });

    it('lets Replay fall back: the part shown last goes, then the one before it, then the page', async () => {
      await open(everything);
      expect(guide.area()?.id).toBe('map');

      page.setTab('timeline'); // no entries: nothing on the Timeline tab to explain, and the map goes
      harness.detectChanges();
      expect(guide.area()?.id).toBe('retry');

      pushStatus('Running', 3);
      expect(guide.area()?.id).toBe('summary');
      expect(guide.canReplay()).toBe(true);
      guide.replay();
      expect(guide.request()?.area.id).toBe('summary');
    });

    it('is not the retry row any more for Replay, after the saga runs and the map is all that is left', async () => {
      await open(everything);

      pushStatus('Running', 3);

      expect(guide.area()?.id).toBe('map');
    });

    it('explains every part on screen when Guide is switched on, and not the one that has gone', async () => {
      await open({ ...on, enabled: false });
      expect(guide.request()).toBeNull();

      guide.setEnabled(true);
      const order: string[] = [];
      while (guide.request()) {
        const id = guide.request()!.area.id;
        order.push(id);
        guide.started(id);
        guide.ended(id, true);
      }
      expect(order).toEqual(['summary', 'retry', 'map']);

      guide.setEnabled(false);
      pushStatus('Running', 3);
      guide.setEnabled(true);
      const again: string[] = [];
      while (guide.request()) {
        const id = guide.request()!.area.id;
        again.push(id);
        guide.started(id);
        guide.ended(id, true);
      }
      expect(again).toEqual(['summary', 'map']);
    });
  });
});
