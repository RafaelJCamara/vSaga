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

  async function open(stored: unknown) {
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
            sagaUpdated$: new Subject<SagaSummary>(),
            timelineEntryAdded$: new Subject(),
            connectionState$: new BehaviorSubject<SagaHubConnectionState>('connected'),
            subscribeToSaga: vi.fn().mockResolvedValue(undefined),
            unsubscribeFromSaga: vi.fn().mockResolvedValue(undefined),
          },
        },
      ],
    });
    guide = TestBed.inject(GuideService); // the app creates it first thing, before any page
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sagas/OrderSaga/saga-1', SagaDetail);
    harness.detectChanges();
  }

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
});
