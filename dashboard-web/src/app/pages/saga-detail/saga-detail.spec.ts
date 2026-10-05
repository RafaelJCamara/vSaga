import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute, ParamMap, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, EMPTY, Observable, Subject, of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { GUIDE_ANCHORS, GUIDE_TOURS, GuideAnchor } from '../../components/guide-overlay/guide-tours';
import { GuideService } from '../../services/guide.service';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubConnectionState, SagaHubService } from '../../services/saga-hub.service';
import {
  SagaDetail as SagaDetailModel,
  SagaLogEntry,
  SagaMap as SagaMapModel,
  SagaRetryPlan,
  SagaStatus,
  SagaSummary,
} from '../../models/saga.model';
import { AuthMock, AuthMockOptions, createAuthMock, provideAuthMock } from '../../testing/auth-mock';
import { httpError, problem } from '../../testing/http-error';
import { timedOutInvoice } from '../../testing/timeline-fixtures';
import { timezoneLabel } from '../../util/time-format';
import { SagaMap } from '../../components/saga-map/saga-map';
import { PENDING_SNAPSHOT_MS } from '../../util/saga-transitions';
import { REFRESH_AUDIT_MS, SNAPSHOT_FOLLOW_UP_MS, SagaDetail } from './saga-detail';

function makeDetail(overrides: Partial<SagaSummary> = {}): SagaDetailModel {
  return {
    summary: {
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
      ...overrides,
    },
    dataJson: null,
  };
}

function makeMap(overrides: Partial<SagaMapModel> = {}): SagaMapModel {
  return {
    summary: makeDetail().summary,
    nodes: [],
    edges: [],
    events: [],
    failureEventIndex: null,
    ...overrides,
  };
}

function makeEntry(overrides: Partial<SagaLogEntry> = {}): SagaLogEntry {
  return {
    sequenceNumber: 1,
    correlationId: 'saga-1',
    sagaType: 'OrderSaga',
    entryType: 'SagaStarted',
    fromState: null,
    toState: 'Submitted',
    messageType: 'OrderSubmitted',
    messageId: 'm0',
    payloadJson: null,
    errorMessage: null,
    traceId: null,
    spanId: null,
    occurredAtUtc: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('SagaDetail', () => {
  let apiMock: {
    get: ReturnType<typeof vi.fn>;
    getTimeline: ReturnType<typeof vi.fn>;
    getMap: ReturnType<typeof vi.fn>;
    retry: ReturnType<typeof vi.fn>;
    getRetryPlan: ReturnType<typeof vi.fn>;
    findByCorrelationId: ReturnType<typeof vi.fn>;
    getChildren: ReturnType<typeof vi.fn>;
  };
  let hubMock: {
    sagaUpdated$: Subject<SagaSummary>;
    timelineEntryAdded$: Subject<{ sagaType: string; correlationId: string; entry: SagaLogEntry }>;
    connectionState$: BehaviorSubject<SagaHubConnectionState>;
    subscribeToSaga: ReturnType<typeof vi.fn>;
    unsubscribeFromSaga: ReturnType<typeof vi.fn>;
  };
  let navigateSpy: ReturnType<typeof vi.spyOn>;
  /** What setup's /retry-plan answers; by default nothing, so the page shows no plan. */
  let retryPlanResponse: Observable<SagaRetryPlan> = EMPTY;
  /** The session setup() signs in with: by default every permission for every saga type. */
  let authOptions: AuthMockOptions = {};
  let auth: AuthMock;
  /** Stands in for guide mode: the page's announcements are what a spec reads. */
  let guideMock: { areaShown: ReturnType<typeof vi.fn> };

  // Only the tests that push use fake timers (the refresh waits REFRESH_AUDIT_MS); none may leak.
  afterEach(() => {
    vi.useRealTimers();
    retryPlanResponse = EMPTY;
    authOptions = {};
  });

  function setup(
    detail: SagaDetailModel = makeDetail(),
    timeline: SagaLogEntry[] = [],
    map: SagaMapModel = makeMap(),
    // What /api/correlations/{id} returns: every instance under this id, this page's own included.
    related: SagaSummary[] = [makeDetail().summary],
    // What /children returns: the sagas this one started, each under its own correlation id.
    children: SagaSummary[] = [],
    // Defaults to a single static emission; pass a Subject to drive multiple param sets through the
    // same component instance the way Angular's route-reuse does on same-route-config navigation.
    paramMap$: Observable<ParamMap> = of(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' })),
    // The page's query parameters (?tab=&entry=); a Subject drives Back and Forward.
    queryParamMap$: Observable<ParamMap> = of(convertToParamMap({})),
  ) {
    apiMock = {
      get: vi.fn().mockReturnValue(of(detail)),
      getTimeline: vi.fn().mockReturnValue(of(timeline)),
      getMap: vi.fn().mockReturnValue(of(map)),
      retry: vi.fn(),
      getRetryPlan: vi.fn().mockReturnValue(retryPlanResponse),
      findByCorrelationId: vi.fn().mockReturnValue(of(related)),
      getChildren: vi.fn().mockReturnValue(of(children)),
    };
    hubMock = {
      sagaUpdated$: new Subject<SagaSummary>(),
      timelineEntryAdded$: new Subject<{ sagaType: string; correlationId: string; entry: SagaLogEntry }>(),
      connectionState$: new BehaviorSubject<SagaHubConnectionState>('connected'),
      subscribeToSaga: vi.fn().mockResolvedValue(undefined),
      unsubscribeFromSaga: vi.fn().mockResolvedValue(undefined),
    };

    auth = createAuthMock(authOptions);
    guideMock = { areaShown: vi.fn() };
    TestBed.configureTestingModule({
      imports: [SagaDetail],
      providers: [
        provideRouter([]),
        provideAuthMock(auth),
        { provide: GuideService, useValue: guideMock },
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: paramMap$,
            queryParamMap: queryParamMap$,
          },
        },
      ],
    });
    navigateSpy = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    const fixture = TestBed.createComponent(SagaDetail);
    fixture.detectChanges();
    return fixture;
  }

  it('loads the saga detail and timeline for the routed id, and subscribes on the hub', () => {
    const detail = makeDetail();
    const entries = [makeEntry()];
    const fixture = setup(detail, entries);

    expect(apiMock.get).toHaveBeenCalledWith('OrderSaga', 'saga-1');
    expect(apiMock.getTimeline).toHaveBeenCalledWith('OrderSaga', 'saga-1');
    expect(hubMock.subscribeToSaga).toHaveBeenCalledWith('OrderSaga', 'saga-1');
    expect(fixture.componentInstance.detail()).toEqual(detail);
    expect(fixture.componentInstance.timeline()).toEqual(entries);
    expect(fixture.componentInstance.loading()).toBe(false);
  });

  // Angular reuses this component instance when navigating between two routes matched by the same
  // route config (e.g. clicking a sibling-saga chip or a sub-saga link), so ngOnInit does not re-fire
  // on that kind of navigation. Reading route params off the paramMap *observable* rather than a
  // one-shot snapshot is what makes this work.
  it('re-subscribes for the new saga when the route reuses this component instance', () => {
    const paramMap$ = new Subject<ParamMap>();
    setup(makeDetail(), [], makeMap(), [makeDetail().summary], [], paramMap$);

    paramMap$.next(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' }));

    expect(apiMock.get).toHaveBeenLastCalledWith('OrderSaga', 'saga-1');
    expect(hubMock.subscribeToSaga).toHaveBeenLastCalledWith('OrderSaga', 'saga-1');
    expect(hubMock.unsubscribeFromSaga).not.toHaveBeenCalled();

    paramMap$.next(convertToParamMap({ sagaType: 'PostShipmentChoreography', id: 'saga-2' }));

    expect(apiMock.get).toHaveBeenLastCalledWith('PostShipmentChoreography', 'saga-2');
    expect(hubMock.unsubscribeFromSaga).toHaveBeenCalledWith('OrderSaga', 'saga-1');
    expect(hubMock.subscribeToSaga).toHaveBeenLastCalledWith('PostShipmentChoreography', 'saga-2');
  });

  // Regression test: `of(...)` mocks resolve synchronously, so a test built on them can never expose
  // an out-of-order-resolution bug — the first call is always fully processed before the second one
  // is even triggered. This test drives saga A's response through a Subject so it can be left
  // in-flight while the route reuses this component instance for saga B, which resolves first.
  it('does not let a stale in-flight response for the previous saga overwrite the current one', () => {
    const paramMap$ = new Subject<ParamMap>();
    const getA$ = new Subject<SagaDetailModel>();
    const detailB = makeDetail({ correlationId: 'saga-2', sagaType: 'PostShipmentChoreography' });

    apiMock = {
      get: vi.fn(),
      getTimeline: vi.fn().mockReturnValue(of([])),
      getMap: vi.fn().mockReturnValue(of(makeMap())),
      retry: vi.fn(),
      getRetryPlan: vi.fn().mockReturnValue(EMPTY),
      findByCorrelationId: vi.fn().mockReturnValue(of([])),
      getChildren: vi.fn().mockReturnValue(of([])),
    };
    hubMock = {
      sagaUpdated$: new Subject<SagaSummary>(),
      timelineEntryAdded$: new Subject<{ sagaType: string; correlationId: string; entry: SagaLogEntry }>(),
      connectionState$: new BehaviorSubject<SagaHubConnectionState>('connected'),
      subscribeToSaga: vi.fn().mockResolvedValue(undefined),
      unsubscribeFromSaga: vi.fn().mockResolvedValue(undefined),
    };

    // First navigation (saga A) returns the not-yet-resolved Subject; the second (saga B) resolves
    // synchronously, the way a fast/cached response legitimately could.
    apiMock.get.mockReturnValueOnce(getA$).mockReturnValueOnce(of(detailB));

    TestBed.configureTestingModule({
      imports: [SagaDetail],
      providers: [
        provideRouter([]),
        provideAuthMock(),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
        { provide: ActivatedRoute, useValue: { paramMap: paramMap$, queryParamMap: of(convertToParamMap({})) } },
      ],
    });
    const fixture = TestBed.createComponent(SagaDetail);
    fixture.detectChanges();

    // Navigate to saga A — request fires, left unresolved (slow).
    paramMap$.next(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' }));
    // Route reuses this instance to navigate to saga B before A resolves — B's request resolves
    // immediately and correctly renders.
    paramMap$.next(convertToParamMap({ sagaType: 'PostShipmentChoreography', id: 'saga-2' }));

    expect(fixture.componentInstance.detail()).toEqual(detailB);
    expect(fixture.componentInstance.sagaType).toBe('PostShipmentChoreography');
    expect(fixture.componentInstance.correlationId).toBe('saga-2');

    // A's stale response finally arrives — it must be dropped, not applied over B.
    getA$.next(makeDetail());
    getA$.complete();

    expect(fixture.componentInstance.detail()).toEqual(detailB);
    expect(fixture.componentInstance.sagaType).toBe('PostShipmentChoreography');
    expect(fixture.componentInstance.correlationId).toBe('saga-2');
  });

  it('shows an error state when the detail request fails', () => {
    const fixture = setup();
    apiMock.get.mockReturnValue(throwError(() => new Error('404')));

    fixture.componentInstance.load();

    expect(fixture.componentInstance.error()).toContain('Could not load');
    expect(fixture.componentInstance.loading()).toBe(false);
  });

  // A failed initial REST load leaves the error state (and stale/empty timeline/map/related/
  // children) up even after the SignalR hub itself recovers -- reconnecting only proves the push
  // channel is back, not that the failed GETs have been retried.
  it('re-runs the failed initial load when the hub reconnects after a prior failure', () => {
    const detail = makeDetail();
    apiMock = {
      get: vi.fn().mockReturnValueOnce(throwError(() => new Error('network down'))).mockReturnValueOnce(of(detail)),
      getTimeline: vi.fn().mockReturnValue(of([])),
      getMap: vi.fn().mockReturnValue(of(makeMap())),
      retry: vi.fn(),
      getRetryPlan: vi.fn().mockReturnValue(EMPTY),
      findByCorrelationId: vi.fn().mockReturnValue(of([])),
      getChildren: vi.fn().mockReturnValue(of([])),
    };
    hubMock = {
      sagaUpdated$: new Subject<SagaSummary>(),
      timelineEntryAdded$: new Subject<{ sagaType: string; correlationId: string; entry: SagaLogEntry }>(),
      connectionState$: new BehaviorSubject<SagaHubConnectionState>('disconnected'),
      subscribeToSaga: vi.fn().mockResolvedValue(undefined),
      unsubscribeFromSaga: vi.fn().mockResolvedValue(undefined),
    };
    TestBed.configureTestingModule({
      imports: [SagaDetail],
      providers: [
        provideRouter([]),
        provideAuthMock(),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: of(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' })),
            queryParamMap: of(convertToParamMap({})),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(SagaDetail);
    fixture.detectChanges();

    expect(fixture.componentInstance.error()).toContain('Could not load');
    expect(apiMock.get).toHaveBeenCalledTimes(1);

    hubMock.connectionState$.next('connected');

    expect(apiMock.get).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.error()).toBeNull();
    expect(fixture.componentInstance.detail()).toEqual(detail);
  });

  // A first-ever connect must not double up on the load() ngOnInit already fired. A later reconnect
  // with no prior error re-reads everything once, as a live refresh (no "Loading…"): the pushes sent
  // while the hub was down are lost, and a saga that finished meanwhile would never send another.
  it('adds no request on the first connect and one live refresh on a later reconnect', () => {
    vi.useFakeTimers();
    const fixture = setup();
    const calls = () => [apiMock.get, apiMock.getTimeline, apiMock.getMap, apiMock.findByCorrelationId, apiMock.getChildren].map((m) => m.mock.calls.length);

    vi.advanceTimersByTime(REFRESH_AUDIT_MS);
    expect(calls()).toEqual([1, 1, 1, 1, 1]);
    expect(fixture.componentInstance.error()).toBeNull();

    hubMock.connectionState$.next('reconnecting');
    hubMock.connectionState$.next('connected');
    expect(fixture.componentInstance.loading()).toBe(false);
    expect(calls()).toEqual([1, 1, 1, 1, 1]);

    vi.advanceTimersByTime(REFRESH_AUDIT_MS);
    expect(calls()).toEqual([2, 2, 2, 2, 2]);
    expect(fixture.componentInstance.loading()).toBe(false);
  });

  // The page's panel, tabs, empty state and banners are the shared classes of src/styles.scss, which
  // the unit tests do not load, so the class names are all there is to check. The base `.banner`
  // carries the box: a modifier on its own would show a banner with no padding.
  it('builds its summary card, tabs, empty state and banners from the shared global classes', () => {
    const fixture = setup(makeDetail({ status: 'Completed' }));
    const el: HTMLElement = fixture.nativeElement;
    const banners = () => Array.from(el.querySelectorAll('[class*="banner"]'), (b) => b.className);

    expect(el.querySelector('.card.summary-card')).not.toBeNull();
    expect(el.querySelectorAll('.subtabs > button')).toHaveLength(2);

    hubMock.connectionState$.next('reconnecting');
    fixture.detectChanges();
    expect(banners()).toEqual(['banner banner--warning']);

    hubMock.connectionState$.next('disconnected');
    fixture.detectChanges();
    expect(banners()).toEqual(['banner banner--warning']);

    fixture.componentInstance.error.set('Could not load this saga.');
    fixture.detectChanges();
    expect(banners()).toEqual(['banner banner--warning', 'banner banner--error']);

    fixture.componentInstance.loading.set(true);
    fixture.detectChanges();
    expect(el.querySelector('.empty')?.textContent).toBe('Loading…');
  });

  it('unsubscribes from the hub on destroy', () => {
    const fixture = setup();
    fixture.destroy();
    expect(hubMock.unsubscribeFromSaga).toHaveBeenCalledWith('OrderSaga', 'saga-1');
  });

  it.each<SagaStatus>(['Failed', 'TimedOut'])('shows the retry button when status is %s', (status) => {
    const fixture = setup(makeDetail({ status }));
    const button = fixture.nativeElement.querySelector('.retry-row button');
    expect(button).not.toBeNull();
  });

  it.each<SagaStatus>(['Running', 'Completed', 'Compensating', 'Compensated', 'Cancelled'])(
    'hides the retry button when status is %s',
    (status) => {
      const fixture = setup(makeDetail({ status }));
      const button = fixture.nativeElement.querySelector('.retry-row button');
      expect(button).toBeNull();
    },
  );

  it('retry() calls the API and shows a success message', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    apiMock.retry.mockReturnValue(of(undefined));

    fixture.componentInstance.retry();

    expect(apiMock.retry).toHaveBeenCalledWith('OrderSaga', 'saga-1');
    expect(fixture.componentInstance.retrying()).toBe(false);
    expect(fixture.componentInstance.retryMessage()).toContain('Retry accepted');
  });

  it('retry() surfaces the server error message on failure', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    apiMock.retry.mockReturnValue(throwError(() => httpError(409, { error: 'Saga cannot be retried' })));

    fixture.componentInstance.retry();

    expect(fixture.componentInstance.retrying()).toBe(false);
    expect(fixture.componentInstance.retryMessage()).toBe('Saga cannot be retried');
  });

  it('retry() falls back to a generic message when the server gives no error detail', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    apiMock.retry.mockReturnValue(throwError(() => ({})));

    fixture.componentInstance.retry();

    expect(fixture.componentInstance.retryMessage()).toBe('Retry failed.');
  });

  it('the retry button asks for confirmation instead of retrying straight away', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    apiMock.retry.mockReturnValue(of(undefined));

    fixture.nativeElement.querySelector('.retry-row button').click();
    fixture.detectChanges();

    expect(apiMock.retry).not.toHaveBeenCalled();
    expect(fixture.componentInstance.confirmingRetry()).toBe(true);
    expect(fixture.nativeElement.querySelector('.retry-confirm')).not.toBeNull();
  });

  it('confirming the prompt runs the retry and clears the prompt', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    apiMock.retry.mockReturnValue(of(undefined));

    fixture.componentInstance.askRetryConfirmation();
    fixture.detectChanges();
    fixture.nativeElement.querySelector('.retry-confirm').click();

    expect(apiMock.retry).toHaveBeenCalledWith('OrderSaga', 'saga-1');
    expect(fixture.componentInstance.confirmingRetry()).toBe(false);
  });

  it('cancelling the prompt leaves the saga untouched', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    apiMock.retry.mockReturnValue(of(undefined));

    fixture.componentInstance.askRetryConfirmation();
    fixture.detectChanges();
    fixture.nativeElement.querySelector('.retry-cancel').click();
    fixture.detectChanges();

    expect(apiMock.retry).not.toHaveBeenCalled();
    expect(fixture.componentInstance.confirmingRetry()).toBe(false);
    expect(fixture.nativeElement.querySelector('.retry-row button').textContent).toContain('Retry this saga');
  });

  // The prompt swaps the buttons out of the DOM; without a focus move the focused one is removed,
  // focus drops to the body and the next Tab skips the prompt for whatever follows the card.
  describe('keyboard focus through the retry prompt', () => {
    const focused = () => document.activeElement as HTMLElement | null;

    it('moves focus to Cancel when the prompt opens, and back to the Retry button on Cancel', () => {
      const fixture = setup(makeDetail({ status: 'Failed' }));
      const el: HTMLElement = fixture.nativeElement;
      const retryButton = el.querySelector('.retry-row button') as HTMLButtonElement;
      retryButton.focus();

      retryButton.click();
      fixture.detectChanges();

      expect(focused()).toBe(el.querySelector('.retry-cancel'));
      // Both buttons carry the prompt as their description, so it is read with the focused one.
      expect(focused()?.getAttribute('aria-describedby')).toBe('retry-confirm-prompt retry-audience');
      expect(el.querySelector('.retry-confirm')?.getAttribute('aria-describedby')).toBe(
        'retry-confirm-prompt retry-audience',
      );

      (focused() as HTMLButtonElement).click();
      fixture.detectChanges();

      const back = el.querySelector('.retry-row button') as HTMLButtonElement;
      expect(back.textContent).toContain('Retry this saga');
      expect(focused()).toBe(back);
    });

    it.each([
      ['accepted', () => of(undefined)],
      ['refused', () => throwError(() => ({ error: { error: 'Saga cannot be retried' } }))],
    ])('keeps the focus on the Retry button while the POST runs, and once it is %s', (_, answer) => {
      const fixture = setup(makeDetail({ status: 'Failed' }));
      const el: HTMLElement = fixture.nativeElement;
      const response = new Subject<void>();
      apiMock.retry.mockReturnValue(response);
      fixture.componentInstance.askRetryConfirmation();
      fixture.detectChanges();

      const yes = el.querySelector('.retry-confirm') as HTMLButtonElement;
      yes.focus();
      expect(focused()).toBe(yes);
      yes.click();
      fixture.detectChanges();
      // The Retry button is back but busy: aria-disabled, not disabled, so it holds the focus the removed
      // "Yes, retry" had, instead of the focus dropping to the page for the length of the request.
      const busy = el.querySelector('.retry-row button') as HTMLButtonElement;
      expect(busy.getAttribute('aria-disabled')).toBe('true');
      expect(busy.disabled).toBe(false);
      expect(focused()).toBe(busy);

      answer().subscribe({ next: () => response.next(), error: (e: unknown) => response.error(e) });
      fixture.detectChanges();

      const button = el.querySelector('.retry-row button') as HTMLButtonElement;
      expect(button.getAttribute('aria-disabled')).toBeNull();
      expect(focused()).toBe(button);
    });

    it('does nothing when the busy Retry button is pressed again, and asks once', () => {
      const fixture = setup(makeDetail({ status: 'Failed' }));
      const el: HTMLElement = fixture.nativeElement;
      apiMock.retry.mockReturnValue(new Subject<void>());
      fixture.componentInstance.askRetryConfirmation();
      fixture.detectChanges();
      (el.querySelector('.retry-confirm') as HTMLButtonElement).click();
      fixture.detectChanges();

      (el.querySelector('.retry-row button') as HTMLButtonElement).click();
      fixture.detectChanges();
      fixture.componentInstance.retry(); // nor does a second confirmation reach the API

      expect(el.querySelector('.retry-confirm')).toBeNull();
      expect(fixture.componentInstance.confirmingRetry()).toBe(false);
      expect(apiMock.retry).toHaveBeenCalledTimes(1);
    });

    it('lets a keyboard reach the Retry button the plan refuses, and does not open the prompt from it', () => {
      const reason = 'No failed step could be identified in this saga\'s timeline.';
      retryPlanResponse = of({ retryable: false, reason, failureKind: null, failureSequenceNumber: null, step: null });
      const fixture = setup(makeDetail({ status: 'Failed' }));
      const el: HTMLElement = fixture.nativeElement;
      const button = el.querySelector('.retry-row button') as HTMLButtonElement;
      (document.body as HTMLElement).focus();

      button.focus();
      button.click();
      fixture.detectChanges();

      expect(focused()).toBe(button);
      expect(el.querySelector('.retry-confirm')).toBeNull();
      expect(fixture.componentInstance.confirmingRetry()).toBe(false); // the page refused the click itself
    });

    it('leaves focus where the viewer moved it while the POST ran', () => {
      const fixture = setup(makeDetail({ status: 'Failed' }));
      const el: HTMLElement = fixture.nativeElement;
      const response = new Subject<void>();
      apiMock.retry.mockReturnValue(response);
      fixture.componentInstance.askRetryConfirmation();
      fixture.detectChanges();
      (el.querySelector('.retry-confirm') as HTMLButtonElement).click();
      fixture.detectChanges();

      const timelineTab = el.querySelectorAll<HTMLButtonElement>('.subtabs button')[1];
      timelineTab.focus();
      response.next();
      fixture.detectChanges();

      expect(focused()).toBe(timelineTab);
    });

    // A retried saga runs again; the push that says so takes the whole row, focused button and all.
    it('moves focus to the saga heading when the saga runs again and the row goes', () => {
      const fixture = setup(makeDetail({ status: 'Failed' }));
      const el: HTMLElement = fixture.nativeElement;
      (el.querySelector('.retry-row button') as HTMLButtonElement).focus();

      hubMock.sagaUpdated$.next({ ...fixture.componentInstance.detail()!.summary, status: 'Running' });
      fixture.detectChanges();

      expect(el.querySelector('.retry-row')).toBeNull();
      expect(focused()).toBe(el.querySelector('.summary-top h1'));
    });

    it('leaves focus alone when the row goes while it was elsewhere', () => {
      const fixture = setup(makeDetail({ status: 'Failed' }));
      const el: HTMLElement = fixture.nativeElement;
      const timelineTab = el.querySelectorAll<HTMLButtonElement>('.subtabs button')[1];
      timelineTab.focus();

      hubMock.sagaUpdated$.next({ ...fixture.componentInstance.detail()!.summary, status: 'Running' });
      fixture.detectChanges();

      expect(focused()).toBe(timelineTab);
    });

    it('gives focus to the saga heading when the POST answers after the row went', () => {
      const fixture = setup(makeDetail({ status: 'Failed' }));
      const el: HTMLElement = fixture.nativeElement;
      const response = new Subject<void>();
      apiMock.retry.mockReturnValue(response);
      fixture.componentInstance.askRetryConfirmation();
      fixture.detectChanges();
      (el.querySelector('.retry-confirm') as HTMLButtonElement).click();
      fixture.detectChanges();

      hubMock.sagaUpdated$.next({ ...fixture.componentInstance.detail()!.summary, status: 'Running' });
      fixture.detectChanges();
      response.next();
      fixture.detectChanges();

      expect(focused()).toBe(el.querySelector('.summary-top h1'));
    });
  });

  // The engine logs SagaCompleted for any terminal Finalize, so a failed saga's last entry would
  // otherwise read "SagaCompleted" directly beneath a red "Failed" badge.
  it('labels the terminal entry SagaFinalized rather than SagaCompleted', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }), [
      makeEntry({ sequenceNumber: 1, entryType: 'SagaCompleted', toState: 'Undeliverable' }),
    ]);
    fixture.componentInstance.setTab('timeline');
    fixture.detectChanges();

    const entryText = fixture.nativeElement.querySelector('.entry-type').textContent;
    expect(entryText).toContain('SagaFinalized');
    expect(entryText).not.toContain('SagaCompleted');
  });

  it('shows the timeline as steps and never lists a state snapshot as an entry', () => {
    const fixture = setup(makeDetail({ status: 'Completed' }), [
      makeEntry({ sequenceNumber: 1, entryType: 'SagaStarted', messageId: 'm0' }),
      makeEntry({ sequenceNumber: 2, entryType: 'MessageReceived', messageId: 'm0' }),
      makeEntry({ sequenceNumber: 3, entryType: 'StepSucceeded', messageId: 'm0', fromState: 'Initial', toState: 'Submitted' }),
      makeEntry({ sequenceNumber: 4, entryType: 'StatePersisted', messageId: 'm0', payloadJson: '{"Status":0}' }),
    ]);
    fixture.componentInstance.setTab('timeline');
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('app-saga-timeline')).not.toBeNull();
    expect(el.querySelectorAll('.tl-step').length).toBe(1);
    expect(el.querySelectorAll('.tl-row').length).toBe(3);
    expect(el.textContent).not.toContain('StatePersisted');
  });

  // The page decides whether the saga is live; the timeline only words the final step from it.
  it.each<[SagaStatus, string]>([
    ['Running', '· in progress'],
    ['Compensating', '· in progress'],
    ['Completed', '· no outcome recorded'],
    ['Failed', '· no outcome recorded'],
  ])('words the final step without an outcome of a %s saga as "%s"', (status, outcome) => {
    const fixture = setup(makeDetail({ status }), [
      makeEntry({ sequenceNumber: 1, entryType: 'SagaStarted', messageId: 'm0', messageType: 'OrderSubmitted' }),
      makeEntry({ sequenceNumber: 2, entryType: 'StepSucceeded', messageId: 'm0', fromState: 'Initial', toState: 'Submitted' }),
      makeEntry({ sequenceNumber: 3, entryType: 'MessageReceived', messageId: 'm1', messageType: 'PaymentCaptured' }),
    ]);
    fixture.componentInstance.setTab('timeline');
    fixture.detectChanges();

    // The step header's text without its Data toggle.
    const heads = Array.from(fixture.nativeElement.querySelectorAll('.tl-head') as NodeListOf<Element>).map((h) => {
      const copy = h.cloneNode(true) as Element;
      copy.querySelector('.tl-data')?.remove();
      return (copy.textContent ?? '').replace(/\s+/g, ' ').trim();
    });
    expect(heads.length).toBe(2);
    expect(heads[1]).toBe(`Step 2 PaymentCaptured ${outcome}`);
  });

  it('says no events were recorded when the timeline holds nothing but snapshots', () => {
    const fixture = setup(makeDetail(), [makeEntry({ entryType: 'StatePersisted', payloadJson: '{}' })]);
    fixture.componentInstance.setTab('timeline');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('app-saga-timeline')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('No events recorded yet.');
  });

  // The local text depends on the zone of the machine running the spec; the zone label, the
  // datetime attribute and the UTC title do not.
  it('labels Created and Updated with the viewer zone and shows UTC on hover', () => {
    const fixture = setup(makeDetail());
    const el: HTMLElement = fixture.nativeElement;

    const labels = Array.from(el.querySelectorAll('.summary-grid dt')).map((dt) => dt.textContent?.trim());
    expect(labels).toContain(`Created (${timezoneLabel(new Date('2026-01-01T00:00:00Z'))})`);
    expect(labels).toContain(`Updated (${timezoneLabel(new Date('2026-01-01T00:00:01Z'))})`);

    const times = el.querySelectorAll('.summary-grid time');
    expect(times.length).toBe(2);
    expect(times[0].getAttribute('datetime')).toBe('2026-01-01T00:00:00Z');
    expect(times[0].getAttribute('title')).toBe('2026-01-01 00:00:00.000 UTC');
    expect(times[1].getAttribute('title')).toBe('2026-01-01 00:00:01.000 UTC');
  });

  it('setTab switches the active tab and writes it to the URL', () => {
    const fixture = setup();
    expect(fixture.componentInstance.tab()).toBe('map'); // Map is the default tab

    fixture.componentInstance.setTab('timeline');

    expect(fixture.componentInstance.tab()).toBe('timeline');
    expect(navigateSpy).toHaveBeenCalledWith(
      [],
      expect.objectContaining({ queryParams: { tab: 'timeline', entry: null, data: null }, replaceUrl: false }),
    );
  });

  // The saga's data moved from a third tab to the Saga data bar under the summary card.
  it('has exactly two tabs, Map and Timeline, and no Data tab', () => {
    const fixture = setup(makeDetail({ status: 'Completed' }), [], makeMap(), undefined, [], undefined, of(convertToParamMap({ tab: 'timeline' })));
    const el: HTMLElement = fixture.nativeElement;

    const tabs = Array.from(el.querySelectorAll('.subtabs button')).map((b) => b.textContent?.trim());
    expect(tabs).toEqual(['Map', 'Timeline']);
    expect(el.querySelector('.data-json')).toBeNull();
    expect(el.querySelector('app-saga-data-overview [role="group"][aria-label="Saga data"]')).not.toBeNull();
  });

  describe('the Saga data bar', () => {
    const query = (params: Record<string, string>) => of(convertToParamMap(params));
    const routeParams = () => of(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' }));

    function dataButton(fixture: ReturnType<typeof setup>, label: string): HTMLButtonElement {
      const buttons = Array.from(fixture.nativeElement.querySelectorAll('.ov-bar button') as NodeListOf<HTMLButtonElement>);
      return buttons.find((b) => b.textContent?.trim() === label)!;
    }

    it('restores the open view from ?data=end and shows the stored state', () => {
      const detail = { ...makeDetail({ status: 'Completed' }), dataJson: '{"Total":10,"Status":1}' };
      const fixture = setup(detail, [], makeMap(), undefined, [], routeParams(), query({ data: 'end' }));

      expect(fixture.componentInstance.dataView()).toBe('end');
      expect(dataButton(fixture, 'At end').getAttribute('aria-pressed')).toBe('true');
      const panel = fixture.nativeElement.querySelector('.ov-end');
      expect(panel?.querySelector('pre')?.textContent).toBe(JSON.stringify({ Total: 10, Status: 'Completed' }, null, 2));
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it.each([[{ data: 'bogus' }], [{ data: 'End' }], [{ data: '' }], [{ tab: 'data' }]])(
      'ignores an invalid data view or the old data tab in %o',
      (params) => {
        const fixture = setup(makeDetail(), [], makeMap(), undefined, [], routeParams(), query(params));

        expect(fixture.componentInstance.dataView()).toBeNull();
        expect(fixture.componentInstance.tab()).toBe('map');
        expect(fixture.nativeElement.querySelector('.ov-panel')).toBeNull();
      },
    );

    it('writes a data view to the URL without a history step, keeping the tab and the focus', () => {
      const fixture = setup(makeDetail({ status: 'Completed' }), [], makeMap(), undefined, [], routeParams(), query({ tab: 'timeline', entry: '2' }));

      dataButton(fixture, 'At start').click();
      fixture.detectChanges();

      expect(fixture.componentInstance.dataView()).toBe('start');
      expect(navigateSpy).toHaveBeenCalledTimes(1);
      expect(navigateSpy).toHaveBeenCalledWith(
        [],
        expect.objectContaining({ queryParams: { tab: 'timeline', entry: 2, data: 'start' }, queryParamsHandling: 'merge', replaceUrl: true }),
      );
      expect(fixture.nativeElement.querySelector('.ov-start')).not.toBeNull();
      expect(fixture.componentInstance.tab()).toBe('timeline');
    });

    it('closes the open view when its button is clicked again', () => {
      const fixture = setup(makeDetail({ status: 'Completed' }), [], makeMap(), undefined, [], routeParams(), query({ data: 'end' }));

      dataButton(fixture, 'At end').click();
      fixture.detectChanges();

      expect(fixture.componentInstance.dataView()).toBeNull();
      expect(navigateSpy.mock.calls.at(-1)![1]).toEqual(
        expect.objectContaining({ queryParams: { tab: null, entry: null, data: null }, replaceUrl: true }),
      );
      expect(fixture.nativeElement.querySelector('.ov-panel')).toBeNull();
    });

    it('keeps the open view when the tab changes', () => {
      const fixture = setup(makeDetail(), [], makeMap(), undefined, [], routeParams(), query({ data: 'start' }));

      fixture.componentInstance.setTab('timeline');

      expect(navigateSpy.mock.calls.at(-1)![1]).toEqual(
        expect.objectContaining({ queryParams: { tab: 'timeline', entry: null, data: 'start' }, replaceUrl: false }),
      );
    });

    it('tells At start the history is still loading until the timeline arrives', () => {
      const paramMap$ = new Subject<ParamMap>();
      const fixture = setup(makeDetail({ status: 'Completed' }), [], makeMap(), undefined, [], paramMap$, query({ data: 'start' }));
      const timeline$ = new Subject<SagaLogEntry[]>();
      apiMock.getTimeline.mockReturnValue(timeline$);
      paramMap$.next(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' }));
      fixture.detectChanges();

      const notes = () => Array.from(fixture.nativeElement.querySelectorAll('.ov-start .ov-note') as NodeListOf<Element>).map((n) => n.textContent?.trim());
      expect(fixture.componentInstance.timelineLoaded()).toBe(false);
      expect(notes()).toEqual(["Loading the saga's history…"]);

      timeline$.next([makeEntry({ sequenceNumber: 1, entryType: 'SagaStarted', messageType: 'OrderSubmitted', payloadJson: '{"OrderId":"o-1"}' })]);
      fixture.detectChanges();

      expect(fixture.componentInstance.timelineLoaded()).toBe(true);
      expect(notes()).not.toContain("Loading the saga's history…");
      expect(fixture.nativeElement.querySelector('.ov-start pre')?.textContent).toBe(JSON.stringify({ OrderId: 'o-1' }, null, 2));
    });

    it('labels the end view Current while the saga runs, and follows a live status change', () => {
      const fixture = setup(makeDetail({ status: 'Running' }));
      expect(dataButton(fixture, 'Current')).toBeDefined();

      hubMock.sagaUpdated$.next({ ...fixture.componentInstance.detail()!.summary, status: 'Completed' });
      fixture.detectChanges();

      expect(dataButton(fixture, 'Current')).toBeUndefined();
      expect(dataButton(fixture, 'At end').getAttribute('aria-label')).toBe('Data at end');
    });
  });

  function sibling(overrides: Partial<SagaSummary> = {}): SagaSummary {
    return {
      ...makeDetail().summary,
      sagaType: 'PostShipmentChoreography',
      kind: 'Choreographed',
      currentState: 'Invoiced',
      status: 'Completed',
      ...overrides,
    };
  }

  it('lists other saga types tracking the same correlation id, excluding itself', () => {
    const fixture = setup(makeDetail(), [], makeMap(), [makeDetail().summary, sibling()]);

    const related = fixture.componentInstance.related();

    expect(apiMock.findByCorrelationId).toHaveBeenCalledWith('saga-1');
    expect(related).toHaveLength(1);
    expect(related[0].sagaType).toBe('PostShipmentChoreography');

    const link = fixture.nativeElement.querySelector('.related-link');
    expect(link.getAttribute('href')).toBe('/sagas/PostShipmentChoreography/saga-1');
    expect(link.textContent).toContain('PostShipmentChoreography');
  });

  it('renders nothing when this saga is the only one under the correlation id', () => {
    const fixture = setup(makeDetail(), [], makeMap(), [makeDetail().summary]);

    expect(fixture.componentInstance.related()).toEqual([]);
    expect(fixture.nativeElement.querySelector('.related')).toBeNull();
  });

  it('keeps the page usable when the correlation lookup fails', () => {
    const fixture = setup();
    apiMock.findByCorrelationId.mockReturnValue(throwError(() => new Error('boom')));
    fixture.componentInstance.loadRelated();

    expect(fixture.componentInstance.related()).toEqual([]);
    expect(fixture.componentInstance.error()).toBeNull();
  });

  // Sub-saga composition. A child is a separate instance under its own correlation id, so neither
  // direction can come from /api/correlations/{id}: "started" needs its own endpoint, and "started
  // by" comes off this saga's own summary.
  it('links to the sagas this one started', () => {
    const child: SagaSummary = {
      ...makeDetail().summary,
      correlationId: 'child-1',
      sagaType: 'InvoiceDeliverySaga',
      currentState: 'AwaitingDelivery',
      status: 'Running',
      parentSagaType: 'OrderSaga',
      parentCorrelationId: 'saga-1',
    };
    const fixture = setup(makeDetail(), [], makeMap(), [makeDetail().summary], [child]);

    expect(apiMock.getChildren).toHaveBeenCalledWith('OrderSaga', 'saga-1');
    expect(fixture.componentInstance.children()).toEqual([child]);

    const link = fixture.nativeElement.querySelector('.related-link');
    expect(link.getAttribute('href')).toBe('/sagas/InvoiceDeliverySaga/child-1');
  });

  it('links back to the saga that started this one', () => {
    const fixture = setup(makeDetail({ parentSagaType: 'PostShipmentChoreography', parentCorrelationId: 'parent-9' }));

    const link = fixture.nativeElement.querySelector('.related-link');
    expect(link.getAttribute('href')).toBe('/sagas/PostShipmentChoreography/parent-9');
    expect(link.textContent).toContain('PostShipmentChoreography');
  });

  it('shows neither relation for a root saga that started nothing', () => {
    const fixture = setup();

    expect(fixture.componentInstance.children()).toEqual([]);
    expect(fixture.nativeElement.querySelector('.related')).toBeNull();
  });

  it('keeps the page usable when the children lookup fails', () => {
    const fixture = setup();
    apiMock.getChildren.mockReturnValue(throwError(() => new Error('boom')));
    fixture.componentInstance.loadChildren();

    expect(fixture.componentInstance.children()).toEqual([]);
    expect(fixture.componentInstance.error()).toBeNull();
  });

  it('re-fetches children when a live saga update arrives', () => {
    vi.useFakeTimers();
    const fixture = setup();
    expect(apiMock.getChildren).toHaveBeenCalledTimes(1);

    hubMock.sagaUpdated$.next(fixture.componentInstance.detail()!.summary);
    vi.advanceTimersByTime(REFRESH_AUDIT_MS);

    expect(apiMock.getChildren).toHaveBeenCalledTimes(2);
  });

  it('applies a live saga update when the correlation id matches', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    const updated: SagaSummary = { ...fixture.componentInstance.detail()!.summary, status: 'Completed' };

    hubMock.sagaUpdated$.next(updated);

    expect(fixture.componentInstance.detail()!.summary.status).toBe('Completed');
  });

  it('ignores a live saga update for a different correlation id', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    const other: SagaSummary = { ...fixture.componentInstance.detail()!.summary, correlationId: 'other-id', status: 'Completed' };

    hubMock.sagaUpdated$.next(other);

    expect(fixture.componentInstance.detail()!.summary.status).toBe('Failed');
  });

  // The case the composite (sagaType, correlationId) identity exists for: another saga type
  // tracking this same correlation id must not bleed into this instance's view. Matching on
  // correlation id alone — what the old code did — would let both of these through.
  it('ignores a live saga update for the same correlation id under a different saga type', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    const other: SagaSummary = {
      ...fixture.componentInstance.detail()!.summary,
      sagaType: 'ShippingChoreography',
      status: 'Completed',
    };

    hubMock.sagaUpdated$.next(other);

    expect(fixture.componentInstance.detail()!.summary.status).toBe('Failed');
  });

  it('ignores a live timeline entry for the same correlation id under a different saga type', () => {
    vi.useFakeTimers();
    const fixture = setup();
    const entry = makeEntry({ sequenceNumber: 2, sagaType: 'ShippingChoreography' });

    hubMock.timelineEntryAdded$.next({ sagaType: 'ShippingChoreography', correlationId: 'saga-1', entry });
    vi.advanceTimersByTime(REFRESH_AUDIT_MS);

    expect(fixture.componentInstance.timeline()).toEqual([]);
    expect(apiMock.getTimeline).toHaveBeenCalledTimes(1);
  });

  // A pushed entry carries no payload, no error text and possibly no sequence number, so it is
  // never appended: it asks for a refresh that reads the stored timeline whole.
  it('re-fetches the timeline after the audit window instead of appending a live entry', () => {
    vi.useFakeTimers();
    const fixture = setup();
    const entry = makeEntry({ sequenceNumber: 2, entryType: 'StepSucceeded' });

    hubMock.timelineEntryAdded$.next({ sagaType: 'OrderSaga', correlationId: 'saga-1', entry });

    expect(fixture.componentInstance.timeline()).toEqual([]);
    expect(apiMock.getTimeline).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(REFRESH_AUDIT_MS);

    expect(apiMock.getTimeline).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.timeline()).toEqual([]);
  });

  it('ignores a live timeline entry for a different correlation id', () => {
    vi.useFakeTimers();
    const fixture = setup();
    const entry = makeEntry({ sequenceNumber: 2 });

    hubMock.timelineEntryAdded$.next({ sagaType: 'OrderSaga', correlationId: 'other-id', entry });
    vi.advanceTimersByTime(REFRESH_AUDIT_MS);

    expect(fixture.componentInstance.timeline()).toEqual([]);
    expect(apiMock.getTimeline).toHaveBeenCalledTimes(1);
  });

  it('loads the map alongside the timeline', () => {
    const map = makeMap();
    const fixture = setup(makeDetail(), [], map);

    expect(apiMock.getMap).toHaveBeenCalledWith('OrderSaga', 'saga-1');
    expect(fixture.componentInstance.map()).toEqual(map);
  });

  it('re-fetches the map (not incrementally, via a whole re-fetch) when a live saga update arrives', () => {
    vi.useFakeTimers();
    const fixture = setup();
    expect(apiMock.getMap).toHaveBeenCalledTimes(1);

    hubMock.sagaUpdated$.next(fixture.componentInstance.detail()!.summary);
    vi.advanceTimersByTime(REFRESH_AUDIT_MS);

    expect(apiMock.getMap).toHaveBeenCalledTimes(2);
  });

  // SignalR only ever pushes a summary-level SagaUpdated, never an incremental timeline diff, across
  // processes — so after a manual retry the Timeline tab must be re-fetched whole, the same as the map.
  it('re-fetches the timeline (not incrementally, via a whole re-fetch) when a live saga update arrives', () => {
    vi.useFakeTimers();
    const fixture = setup();
    expect(apiMock.getTimeline).toHaveBeenCalledTimes(1);

    hubMock.sagaUpdated$.next(fixture.componentInstance.detail()!.summary);
    vi.advanceTimersByTime(REFRESH_AUDIT_MS);

    expect(apiMock.getTimeline).toHaveBeenCalledTimes(2);
  });

  it('does not re-fetch the map for a live saga update on a different correlation id', () => {
    vi.useFakeTimers();
    const fixture = setup();
    const other: SagaSummary = { ...fixture.componentInstance.detail()!.summary, correlationId: 'other-id' };

    hubMock.sagaUpdated$.next(other);
    vi.advanceTimersByTime(REFRESH_AUDIT_MS);

    expect(apiMock.getMap).toHaveBeenCalledTimes(1);
  });

  it('switching to the Map tab renders the saga-map component once the map has loaded', () => {
    const fixture = setup();
    fixture.componentInstance.setTab('map');
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('app-saga-map')).not.toBeNull();
  });

  describe('jump between the timeline and the map', () => {
    /** One step: rows #1-#3 (sequence 1-3), a snapshot (4) that is never a row. */
    function steps(): SagaLogEntry[] {
      return [
        makeEntry({ sequenceNumber: 1, entryType: 'SagaStarted', messageId: 'm0' }),
        makeEntry({ sequenceNumber: 2, entryType: 'MessageReceived', messageId: 'm0' }),
        makeEntry({ sequenceNumber: 3, entryType: 'StepSucceeded', messageId: 'm0', fromState: 'Initial', toState: 'Submitted' }),
        makeEntry({ sequenceNumber: 4, entryType: 'StatePersisted', messageId: 'm0', payloadJson: '{}' }),
      ];
    }

    function mapOf(...sequences: number[]): SagaMapModel {
      return makeMap({
        nodes: [{ id: 'OrderSaga', displayName: 'OrderSaga', kind: 'Orchestrator', status: 'ok', messagesIn: 0, messagesOut: 0 }],
        events: sequences.map((sequenceNumber) => ({
          sequenceNumber,
          edgeId: null,
          nodeId: null,
          entryType: 'StepSucceeded' as const,
          messageType: null,
          errorMessage: null,
          occurredAtUtc: '2026-01-01T00:00:00Z',
        })),
      });
    }

    function query(params: Record<string, string>): Observable<ParamMap> {
      return of(convertToParamMap(params));
    }

    const routeParams = () => of(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' }));

    function mapComponent(fixture: ReturnType<typeof setup>): SagaMap {
      return fixture.debugElement.query(By.directive(SagaMap)).componentInstance as SagaMap;
    }

    function lastNavigation(): { queryParams: Record<string, unknown>; queryParamsHandling: string; replaceUrl: boolean } {
      return navigateSpy.mock.calls.at(-1)![1] as never;
    }

    it('restores the tab and the focused entry from the URL', () => {
      const fixture = setup(makeDetail(), steps(), mapOf(1, 2, 3), undefined, [], routeParams(), query({ tab: 'timeline', entry: '2' }));

      expect(fixture.componentInstance.tab()).toBe('timeline');
      expect(fixture.componentInstance.focusedSequence()).toBe(2);
      const focused = fixture.nativeElement.querySelectorAll('.tl-row--focused');
      expect(focused.length).toBe(1);
      expect(focused[0].getAttribute('data-seq')).toBe('2');
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it.each([
      [{ tab: 'bogus', entry: '0' }],
      [{ tab: 'Timeline', entry: 'abc' }],
      [{ entry: '-3' }],
      [{ entry: '1.5' }],
      [{ entry: '9007199254740993' }],
    ])('ignores an invalid tab or entry in %o', (params) => {
      const fixture = setup(makeDetail(), steps(), mapOf(1, 2, 3), undefined, [], routeParams(), query(params));

      expect(fixture.componentInstance.tab()).toBe('map');
      expect(fixture.componentInstance.focusedSequence()).toBeNull();
    });

    it('opens the map on an entry when its timeline row is clicked, as a history step', () => {
      const fixture = setup(makeDetail(), steps(), mapOf(1, 2, 3));
      fixture.componentInstance.setTab('timeline');
      fixture.detectChanges();
      navigateSpy.mockClear();

      fixture.nativeElement.querySelector('.tl-row[data-seq="2"]').click();
      fixture.detectChanges();

      expect(navigateSpy).toHaveBeenCalledTimes(1);
      expect(navigateSpy).toHaveBeenCalledWith(
        [],
        expect.objectContaining({ queryParams: { tab: null, entry: 2, data: null }, queryParamsHandling: 'merge', replaceUrl: false }),
      );
      expect(fixture.componentInstance.tab()).toBe('map');
      const map = mapComponent(fixture);
      expect(map.focusSequence()).toBe(2);
      expect(map.currentIndex()).toBe(1);
      expect(fixture.nativeElement.querySelector('.focus-banner')?.textContent).toContain('As of entry #2 of 3');
    });

    it("opens the map on a step's last entry when the step title is clicked", () => {
      const fixture = setup(makeDetail(), steps(), mapOf(1, 2, 3));
      fixture.componentInstance.setTab('timeline');
      fixture.detectChanges();

      fixture.nativeElement.querySelector('button.tl-title').click();

      expect(lastNavigation().queryParams).toEqual({ tab: null, entry: 3, data: null });
      expect(fixture.componentInstance.focusedSequence()).toBe(3);
    });

    it('fetches the map again when it does not hold the entry yet, and not when it does', () => {
      const fixture = setup(makeDetail(), steps(), mapOf(1, 2));
      expect(apiMock.getMap).toHaveBeenCalledTimes(1);

      fixture.componentInstance.showOnMap(2);
      expect(apiMock.getMap).toHaveBeenCalledTimes(1);

      fixture.componentInstance.showOnMap(3);
      expect(apiMock.getMap).toHaveBeenCalledTimes(2);
    });

    it('shows the map unfocused for an entry without a valid sequence number', () => {
      const fixture = setup(makeDetail(), steps(), mapOf(1, 2, 3));

      fixture.componentInstance.showOnMap(0);

      expect(fixture.componentInstance.tab()).toBe('map');
      expect(fixture.componentInstance.focusedSequence()).toBeNull();
      expect(lastNavigation().queryParams).toEqual({ tab: null, entry: null, data: null });
    });

    it('drops the focus without a history step when the map releases it', () => {
      const fixture = setup(makeDetail(), steps(), mapOf(1, 2, 3), undefined, [], routeParams(), query({ entry: '2' }));
      expect(mapComponent(fixture).focusSequence()).toBe(2);

      mapComponent(fixture).restart();
      fixture.detectChanges();

      expect(fixture.componentInstance.focusedSequence()).toBeNull();
      expect(lastNavigation()).toEqual(
        expect.objectContaining({ queryParams: { tab: null, entry: null, data: null }, queryParamsHandling: 'merge', replaceUrl: true }),
      );
      expect(fixture.nativeElement.querySelector('.focus-banner')).toBeNull();
    });

    it("goes back to the entry in the timeline from the map's banner", () => {
      const fixture = setup(makeDetail(), steps(), mapOf(1, 2, 3), undefined, [], routeParams(), query({ entry: '2' }));

      (fixture.nativeElement.querySelector('.focus-banner button') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(fixture.componentInstance.tab()).toBe('timeline');
      expect(fixture.componentInstance.focusedSequence()).toBe(2);
      expect(lastNavigation()).toEqual(
        expect.objectContaining({ queryParams: { tab: 'timeline', entry: 2, data: null }, replaceUrl: false }),
      );
      expect(fixture.nativeElement.querySelector('.tl-row--focused')?.getAttribute('data-seq')).toBe('2');
    });

    it('follows Back and Forward: the URL decides the tab and the focus', () => {
      const query$ = new BehaviorSubject<ParamMap>(convertToParamMap({ tab: 'timeline' }));
      const fixture = setup(makeDetail(), steps(), mapOf(1, 2, 3), undefined, [], routeParams(), query$);
      expect(fixture.componentInstance.tab()).toBe('timeline');

      query$.next(convertToParamMap({ entry: '3' }));
      expect(fixture.componentInstance.tab()).toBe('map');
      expect(fixture.componentInstance.focusedSequence()).toBe(3);

      query$.next(convertToParamMap({ tab: 'timeline' }));
      expect(fixture.componentInstance.tab()).toBe('timeline');
      expect(fixture.componentInstance.focusedSequence()).toBeNull();
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('writes a tab change to the URL as a history step and keeps the focus', () => {
      const fixture = setup(makeDetail(), steps(), mapOf(1, 2, 3), undefined, [], routeParams(), query({ entry: '2' }));

      fixture.componentInstance.setTab('timeline');

      expect(lastNavigation()).toEqual(
        expect.objectContaining({ queryParams: { tab: 'timeline', entry: 2, data: null }, queryParamsHandling: 'merge', replaceUrl: false }),
      );
    });

    it('drops the focus of the previous saga when the route moves to another one', () => {
      const paramMap$ = new Subject<ParamMap>();
      const fixture = setup(makeDetail(), steps(), mapOf(1, 2, 3), undefined, [], paramMap$);
      paramMap$.next(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' }));
      fixture.componentInstance.showOnMap(2);
      expect(fixture.componentInstance.focusedSequence()).toBe(2);

      paramMap$.next(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-2' }));

      expect(fixture.componentInstance.focusedSequence()).toBeNull();
    });
  });

  describe("a step's data inspector", () => {
    /** One step (key 1) with a recorded snapshot. */
    function oneStep(): SagaLogEntry[] {
      return [
        makeEntry({ sequenceNumber: 1, entryType: 'SagaStarted', messageId: 'm0' }),
        makeEntry({ sequenceNumber: 2, entryType: 'StepSucceeded', messageId: 'm0', fromState: 'Initial', toState: 'Submitted' }),
        makeEntry({ sequenceNumber: 3, entryType: 'StatePersisted', messageId: 'm0', payloadJson: '{"Total":10}' }),
      ];
    }

    function openFirstStep(fixture: ReturnType<typeof setup>): void {
      fixture.componentInstance.setTab('timeline');
      fixture.detectChanges();
      (fixture.nativeElement.querySelector('.tl-head .tl-data') as HTMLButtonElement).click();
      fixture.detectChanges();
    }

    const inspector = (fixture: ReturnType<typeof setup>): HTMLElement | null =>
      fixture.nativeElement.querySelector('#step-data-1');

    it('opens from the step header and stays open after a trip to the map and back', () => {
      const fixture = setup(makeDetail({ status: 'Completed' }), oneStep());
      openFirstStep(fixture);
      expect(inspector(fixture)).not.toBeNull();
      expect([...fixture.componentInstance.openKeys()]).toEqual([1]);

      fixture.componentInstance.setTab('map');
      fixture.detectChanges();
      expect(inspector(fixture)).toBeNull();

      fixture.componentInstance.setTab('timeline');
      fixture.detectChanges();
      expect(inspector(fixture)).not.toBeNull();
      expect(fixture.nativeElement.querySelector('.tl-head .tl-data').getAttribute('aria-expanded')).toBe('true');
    });

    it('stays open when a live update refreshes the timeline', () => {
      vi.useFakeTimers();
      const fixture = setup(makeDetail({ status: 'Running' }), oneStep());
      openFirstStep(fixture);
      apiMock.getTimeline.mockReturnValue(
        of([...oneStep(), makeEntry({ sequenceNumber: 4, entryType: 'MessageReceived', messageId: 'm1', messageType: 'PaymentCaptured' })]),
      );

      hubMock.sagaUpdated$.next(fixture.componentInstance.detail()!.summary);
      vi.advanceTimersByTime(REFRESH_AUDIT_MS);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelectorAll('.tl-step').length).toBe(2);
      expect(inspector(fixture)).not.toBeNull();
      expect(inspector(fixture)?.querySelector('pre')?.textContent).toBe(JSON.stringify({ Total: 10 }, null, 2));
    });

    it('closes every inspector when the route moves to another saga', () => {
      const paramMap$ = new BehaviorSubject<ParamMap>(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' }));
      const fixture = setup(makeDetail({ status: 'Completed' }), oneStep(), makeMap(), undefined, [], paramMap$);
      openFirstStep(fixture);
      expect(inspector(fixture)).not.toBeNull();

      paramMap$.next(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-2' }));
      fixture.detectChanges();

      expect(fixture.componentInstance.openKeys().size).toBe(0);
      expect(inspector(fixture)).toBeNull();
    });
  });

  describe('live refresh', () => {
    const T = Date.parse('2026-01-01T00:00:10Z');
    const at = (offsetMs: number) => new Date(T + offsetMs).toISOString();

    /** Step 1 with its snapshot; step 2 committed 400 ms before T, its snapshot not appended yet. */
    function awaitingSnapshot(withSnapshots = true): SagaLogEntry[] {
      return [
        makeEntry({ sequenceNumber: 1, entryType: 'SagaStarted', messageId: 'm0', occurredAtUtc: at(-3000) }),
        makeEntry({ sequenceNumber: 2, entryType: 'StepSucceeded', messageId: 'm0', fromState: 'Initial', toState: 'Submitted', occurredAtUtc: at(-2900) }),
        ...(withSnapshots
          ? [makeEntry({ sequenceNumber: 3, entryType: 'StatePersisted', messageId: 'm0', payloadJson: '{}', occurredAtUtc: at(-2900) })]
          : []),
        makeEntry({ sequenceNumber: 4, entryType: 'MessageReceived', messageId: 'm1', messageType: 'PaymentCaptured', occurredAtUtc: at(-500) }),
        makeEntry({ sequenceNumber: 5, entryType: 'StepSucceeded', messageId: 'm1', fromState: 'Submitted', toState: 'Paid', occurredAtUtc: at(-400) }),
      ];
    }

    function push(fixture: ReturnType<typeof setup>, overrides: Partial<SagaSummary> = {}): void {
      hubMock.sagaUpdated$.next({ ...fixture.componentInstance.detail()!.summary, ...overrides });
    }

    it('re-reads the detail without showing Loading and shows its new data', () => {
      vi.useFakeTimers();
      const fixture = setup(makeDetail({ status: 'Running', version: 2 }));
      const fresh$ = new Subject<SagaDetailModel>();
      apiMock.get.mockReturnValue(fresh$);

      push(fixture, { version: 3, currentState: 'Paid' });
      expect(fixture.componentInstance.detail()!.summary.currentState).toBe('Paid');
      expect(apiMock.get).toHaveBeenCalledTimes(1);

      vi.advanceTimersByTime(REFRESH_AUDIT_MS);
      fixture.detectChanges();

      expect(apiMock.get).toHaveBeenCalledTimes(2);
      expect(fixture.componentInstance.loading()).toBe(false);
      expect(fixture.nativeElement.querySelector('.summary-card')).not.toBeNull();

      fresh$.next({ ...makeDetail({ status: 'Running', version: 3, currentState: 'Paid' }), dataJson: '{"Total":10}' });

      expect(fixture.componentInstance.detail()!.dataJson).toBe('{"Total":10}');
      expect(fixture.componentInstance.loading()).toBe(false);
    });

    it('keeps the newer summary when an older detail response arrives', () => {
      vi.useFakeTimers();
      const fixture = setup(makeDetail({ status: 'Running', version: 2 }));
      apiMock.get.mockReturnValue(of({ ...makeDetail({ status: 'Running', version: 2 }), dataJson: '{"Total":1}' }));

      push(fixture, { version: 3, status: 'Completed' });
      vi.advanceTimersByTime(REFRESH_AUDIT_MS);

      expect(fixture.componentInstance.detail()!.summary).toEqual(expect.objectContaining({ version: 3, status: 'Completed' }));
      expect(fixture.componentInstance.detail()!.dataJson).toBeNull();
    });

    it('keeps the higher version when two refreshes answer out of order', () => {
      vi.useFakeTimers();
      const fixture = setup(makeDetail({ status: 'Running', version: 2 }));
      const first$ = new Subject<SagaDetailModel>();
      const second$ = new Subject<SagaDetailModel>();
      apiMock.get.mockReturnValueOnce(first$).mockReturnValueOnce(second$);

      push(fixture, { version: 3 });
      vi.advanceTimersByTime(REFRESH_AUDIT_MS);
      push(fixture, { version: 4 });
      vi.advanceTimersByTime(REFRESH_AUDIT_MS);

      second$.next({ ...makeDetail({ status: 'Running', version: 4 }), dataJson: '{"v":4}' });
      first$.next({ ...makeDetail({ status: 'Running', version: 3 }), dataJson: '{"v":3}' });

      expect(fixture.componentInstance.detail()!.summary.version).toBe(4);
      expect(fixture.componentInstance.detail()!.dataJson).toBe('{"v":4}');
    });

    it('keeps the newer timeline and map when two refreshes answer out of order', () => {
      vi.useFakeTimers();
      const fixture = setup(makeDetail({ status: 'Running', version: 2 }), [makeEntry({ sequenceNumber: 1 })]);
      const firstTimeline$ = new Subject<SagaLogEntry[]>();
      const secondTimeline$ = new Subject<SagaLogEntry[]>();
      const firstMap$ = new Subject<SagaMapModel>();
      const secondMap$ = new Subject<SagaMapModel>();
      apiMock.getTimeline.mockReturnValueOnce(firstTimeline$).mockReturnValueOnce(secondTimeline$);
      apiMock.getMap.mockReturnValueOnce(firstMap$).mockReturnValueOnce(secondMap$);

      push(fixture, { version: 3 });
      vi.advanceTimersByTime(REFRESH_AUDIT_MS);
      push(fixture, { version: 4 });
      vi.advanceTimersByTime(REFRESH_AUDIT_MS);

      const newer = [makeEntry({ sequenceNumber: 1 }), makeEntry({ sequenceNumber: 2 }), makeEntry({ sequenceNumber: 3 })];
      const newerMap = makeMap({ failureEventIndex: 2 });
      secondTimeline$.next(newer);
      secondMap$.next(newerMap);
      firstTimeline$.next([makeEntry({ sequenceNumber: 1 }), makeEntry({ sequenceNumber: 2 })]);
      firstMap$.next(makeMap());
      firstTimeline$.error(new Error('late failure'));
      firstMap$.error(new Error('late failure'));

      expect(fixture.componentInstance.timeline()).toEqual(newer);
      expect(fixture.componentInstance.map()).toEqual(newerMap);
      expect(fixture.componentInstance.timelineError()).toBe(false);
      expect(fixture.componentInstance.mapError()).toBe(false);
    });

    it('ignores a pushed summary older than the one shown', () => {
      const fixture = setup(makeDetail({ status: 'Running', version: 5 }));

      push(fixture, { version: 4, status: 'Failed' });

      expect(fixture.componentInstance.detail()!.summary).toEqual(expect.objectContaining({ version: 5, status: 'Running' }));
    });

    it('turns three pushes within the audit window into one refresh', () => {
      vi.useFakeTimers();
      const fixture = setup(makeDetail({ status: 'Running' }));
      const calls = () => [apiMock.get, apiMock.getTimeline, apiMock.getMap, apiMock.findByCorrelationId, apiMock.getChildren].map((m) => m.mock.calls.length);
      expect(calls()).toEqual([1, 1, 1, 1, 1]);

      push(fixture);
      vi.advanceTimersByTime(100);
      hubMock.timelineEntryAdded$.next({ sagaType: 'OrderSaga', correlationId: 'saga-1', entry: makeEntry({ sequenceNumber: 0 }) });
      vi.advanceTimersByTime(100);
      push(fixture);
      expect(calls()).toEqual([1, 1, 1, 1, 1]);

      vi.advanceTimersByTime(REFRESH_AUDIT_MS - 200);
      expect(calls()).toEqual([2, 2, 2, 2, 2]);

      vi.advanceTimersByTime(10_000);
      expect(calls()).toEqual([2, 2, 2, 2, 2]);
    });

    it("fetches the timeline once more when a push lands before the final step's snapshot", () => {
      vi.useFakeTimers();
      vi.setSystemTime(T);
      const fixture = setup(makeDetail({ status: 'Completed' }), awaitingSnapshot());

      // The first load alone never schedules it: only a push says the saga just moved.
      vi.advanceTimersByTime(SNAPSHOT_FOLLOW_UP_MS);
      expect(apiMock.getTimeline).toHaveBeenCalledTimes(1);

      push(fixture);
      vi.advanceTimersByTime(REFRESH_AUDIT_MS);
      expect(apiMock.getTimeline).toHaveBeenCalledTimes(2);

      vi.advanceTimersByTime(SNAPSHOT_FOLLOW_UP_MS - 1);
      expect(apiMock.getTimeline).toHaveBeenCalledTimes(2);
      vi.advanceTimersByTime(1);
      expect(apiMock.getTimeline).toHaveBeenCalledTimes(3);

      // Still no snapshot: the follow-up does not schedule another one.
      vi.advanceTimersByTime(10_000);
      expect(apiMock.getTimeline).toHaveBeenCalledTimes(3);
    });

    it.each([
      ['the saga records no snapshots', false, 0],
      ['the final step is older than the pending window', true, PENDING_SNAPSHOT_MS],
    ])('does not fetch the timeline again when %s', (_case, withSnapshots, ageMs) => {
      vi.useFakeTimers();
      vi.setSystemTime(T + ageMs);
      const fixture = setup(makeDetail({ status: 'Completed' }), awaitingSnapshot(withSnapshots));

      push(fixture);
      vi.advanceTimersByTime(REFRESH_AUDIT_MS + SNAPSHOT_FOLLOW_UP_MS + 1000);

      expect(apiMock.getTimeline).toHaveBeenCalledTimes(2);
    });

    it("does not fetch the timeline again once the final step's snapshot is there", () => {
      vi.useFakeTimers();
      vi.setSystemTime(T);
      const recorded = [
        ...awaitingSnapshot(),
        makeEntry({ sequenceNumber: 6, entryType: 'StatePersisted', messageId: 'm1', payloadJson: '{"Paid":true}', occurredAtUtc: at(-400) }),
      ];
      const fixture = setup(makeDetail({ status: 'Completed' }), recorded);

      push(fixture);
      vi.advanceTimersByTime(REFRESH_AUDIT_MS + SNAPSHOT_FOLLOW_UP_MS + 1000);

      expect(apiMock.getTimeline).toHaveBeenCalledTimes(2);
    });
  });

  describe('load errors', () => {
    const routeParams = () => convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' });
    const failing = () => throwError(() => new Error('API down'));

    /** The page with the given tab, its timeline and map requests failing from the first load. */
    function setupFailing(tab: 'map' | 'timeline'): ReturnType<typeof setup> {
      const paramMap$ = new Subject<ParamMap>();
      const fixture = setup(makeDetail(), [], makeMap(), undefined, [], paramMap$, of(convertToParamMap(tab === 'map' ? {} : { tab })));
      apiMock.getTimeline.mockReturnValue(failing());
      apiMock.getMap.mockReturnValue(failing());
      paramMap$.next(routeParams());
      fixture.detectChanges();
      return fixture;
    }

    const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

    it('shows an error with Try again when the timeline cannot be loaded, and Try again loads it', () => {
      const fixture = setupFailing('timeline');
      const el: HTMLElement = fixture.nativeElement;

      const banner = el.querySelector('.banner.banner--error.load-error');
      expect(text(banner)).toBe('Could not load the timeline. Try again');
      expect(el.querySelector('app-saga-timeline')).toBeNull();
      expect(el.textContent).not.toContain('No events recorded yet.');
      expect(el.textContent).not.toContain('Loading timeline…');

      apiMock.getTimeline.mockReturnValue(of([makeEntry()]));
      (banner!.querySelector('button') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(el.querySelector('.load-error')).toBeNull();
      expect(el.querySelector('app-saga-timeline')).not.toBeNull();
    });

    it('shows an error with Try again when the map cannot be loaded, and Try again loads it', () => {
      const fixture = setupFailing('map');
      const el: HTMLElement = fixture.nativeElement;

      const banner = el.querySelector('.banner.banner--error.load-error');
      expect(text(banner)).toBe('Could not load the map. Try again');
      expect(el.querySelector('app-saga-map')).toBeNull();
      expect(el.textContent).not.toContain('Loading map…');

      apiMock.getMap.mockReturnValue(of(makeMap()));
      (banner!.querySelector('button') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(el.querySelector('.load-error')).toBeNull();
      expect(el.querySelector('app-saga-map')).not.toBeNull();
    });

    it('keeps the stale timeline and map under a warning when a refresh fails', () => {
      vi.useFakeTimers();
      const fixture = setup(makeDetail({ status: 'Running' }), [makeEntry()], makeMap(), undefined, [], undefined, of(convertToParamMap({ tab: 'timeline' })));
      const el: HTMLElement = fixture.nativeElement;
      apiMock.getTimeline.mockReturnValue(failing());
      apiMock.getMap.mockReturnValue(failing());

      hubMock.sagaUpdated$.next(fixture.componentInstance.detail()!.summary);
      vi.advanceTimersByTime(REFRESH_AUDIT_MS);
      fixture.detectChanges();

      expect(text(el.querySelector('.banner.banner--warning.load-error'))).toBe(
        'Could not refresh the timeline; it shows the entries as last loaded. Try again',
      );
      expect(el.querySelector('.banner--error')).toBeNull();
      expect(el.querySelector('app-saga-timeline')).not.toBeNull();

      fixture.componentInstance.setTab('map');
      fixture.detectChanges();
      const warning = el.querySelector('.banner.banner--warning.load-error');
      expect(text(warning)).toBe('Could not refresh the map; it shows the saga as last loaded. Try again');
      expect(el.querySelector('app-saga-map')).not.toBeNull();

      apiMock.getMap.mockReturnValue(of(makeMap()));
      (warning!.querySelector('button') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(el.querySelector('.load-error')).toBeNull();
    });

    it('retries the timeline and the map in one live refresh when live updates reconnect', () => {
      vi.useFakeTimers();
      const fixture = setupFailing('timeline');
      const calls = () => [apiMock.get, apiMock.getTimeline, apiMock.getMap, apiMock.findByCorrelationId, apiMock.getChildren].map((m) => m.mock.calls.length);
      expect(fixture.componentInstance.timelineError()).toBe(true);
      expect(fixture.componentInstance.mapError()).toBe(true);
      expect(calls()).toEqual([1, 1, 1, 1, 1]);
      apiMock.getTimeline.mockReturnValue(of([makeEntry()]));
      apiMock.getMap.mockReturnValue(of(makeMap()));

      hubMock.connectionState$.next('reconnecting');
      hubMock.connectionState$.next('connected');
      vi.advanceTimersByTime(REFRESH_AUDIT_MS);

      // The detail loaded fine, so the page re-reads it as a refresh, without "Loading…".
      expect(calls()).toEqual([2, 2, 2, 2, 2]);
      expect(fixture.componentInstance.loading()).toBe(false);
      expect(fixture.componentInstance.timelineError()).toBe(false);
      expect(fixture.componentInstance.mapError()).toBe(false);
    });

    it('drops the timeline, the map and their errors when the route moves to another saga', () => {
      const paramMap$ = new BehaviorSubject<ParamMap>(routeParams());
      const fixture = setup(makeDetail(), [makeEntry()], makeMap(), undefined, [], paramMap$);
      apiMock.getTimeline.mockReturnValue(failing());
      apiMock.getMap.mockReturnValue(failing());
      fixture.componentInstance.retryTimeline();
      fixture.componentInstance.loadMap();
      expect(fixture.componentInstance.timelineError()).toBe(true);
      expect(fixture.componentInstance.mapError()).toBe(true);
      apiMock.getTimeline.mockReturnValue(new Subject<SagaLogEntry[]>());
      apiMock.getMap.mockReturnValue(new Subject<SagaMapModel>());

      paramMap$.next(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-2' }));

      const page = fixture.componentInstance;
      expect(page.timeline()).toEqual([]);
      expect(page.timelineLoaded()).toBe(false);
      expect(page.timelineError()).toBe(false);
      expect(page.map()).toBeNull();
      expect(page.mapError()).toBe(false);
    });
  });

  describe('the failed step', () => {
    /** The design's timed-out InvoiceFollowUpSaga: failed at #66 (step 2), re-runs step 1 (#61). */
    function timeoutPlan(overrides: Partial<SagaRetryPlan> = {}): SagaRetryPlan {
      return {
        retryable: true,
        reason: null,
        failureKind: 'TimedOut',
        failureSequenceNumber: 66,
        step: { sequenceNumber: 61, messageType: 'InvoiceIssued', messageId: 'e5', fromState: 'Requested' },
        ...overrides,
      };
    }

    const timedOut = (overrides: Partial<SagaSummary> = {}) =>
      makeDetail({ status: 'TimedOut', currentState: 'Abandoned', version: 3, ...overrides });

    /** A map holding the timeline's rows (60-64, 66, 67), the snapshots left out. */
    const invoiceMap = () =>
      makeMap({
        nodes: [{ id: 'OrderSaga', displayName: 'OrderSaga', kind: 'Orchestrator', status: 'ok', messagesIn: 0, messagesOut: 0 }],
        events: [60, 61, 62, 63, 64, 66, 67].map((sequenceNumber) => ({
          sequenceNumber,
          edgeId: null,
          nodeId: null,
          entryType: 'StepSucceeded' as const,
          messageType: null,
          errorMessage: null,
          occurredAtUtc: '2026-01-01T00:00:00Z',
        })),
      });

    const mapComponent = (fixture: ReturnType<typeof setup>): SagaMap =>
      fixture.debugElement.query(By.directive(SagaMap)).componentInstance as SagaMap;

    const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

    it.each<SagaStatus>(['Failed', 'TimedOut'])('loads the retry plan for a %s saga', (status) => {
      retryPlanResponse = of(timeoutPlan());
      const fixture = setup(makeDetail({ status }));

      expect(apiMock.getRetryPlan).toHaveBeenCalledTimes(1);
      expect(apiMock.getRetryPlan).toHaveBeenCalledWith('OrderSaga', 'saga-1');
      expect(fixture.componentInstance.retryPlan()).toEqual(timeoutPlan());
    });

    it.each<SagaStatus>(['Running', 'Completed', 'Compensating', 'Compensated', 'Cancelled'])(
      'loads no retry plan for a %s saga',
      (status) => {
        retryPlanResponse = of(timeoutPlan());
        const fixture = setup(makeDetail({ status }));

        expect(apiMock.getRetryPlan).not.toHaveBeenCalled();
        expect(fixture.componentInstance.retryPlan()).toBeNull();
      },
    );

    it('reloads the plan only when a refresh changes the status or version, and drops it for a running saga', () => {
      vi.useFakeTimers();
      retryPlanResponse = of(timeoutPlan());
      const fixture = setup(timedOut());
      const push = (overrides: Partial<SagaSummary>) =>
        hubMock.sagaUpdated$.next({ ...fixture.componentInstance.detail()!.summary, ...overrides });
      expect(apiMock.getRetryPlan).toHaveBeenCalledTimes(1);

      // A refresh that finds the same failed saga asks for nothing new.
      push({});
      vi.advanceTimersByTime(REFRESH_AUDIT_MS);
      expect(apiMock.getRetryPlan).toHaveBeenCalledTimes(1);

      // A retry was accepted: the saga runs again, and the plan goes.
      apiMock.get.mockReturnValue(of(timedOut({ status: 'Running', version: 4 })));
      push({ status: 'Running', version: 4 });
      vi.advanceTimersByTime(REFRESH_AUDIT_MS);
      expect(fixture.componentInstance.retryPlan()).toBeNull();
      expect(apiMock.getRetryPlan).toHaveBeenCalledTimes(1);

      // It failed again, at a later step.
      const later = timeoutPlan({ failureSequenceNumber: 80 });
      apiMock.getRetryPlan.mockReturnValue(of(later));
      apiMock.get.mockReturnValue(of(timedOut({ version: 6 })));
      push({ status: 'TimedOut', version: 6 });
      vi.advanceTimersByTime(REFRESH_AUDIT_MS);
      expect(apiMock.getRetryPlan).toHaveBeenCalledTimes(2);
      expect(fixture.componentInstance.retryPlan()).toEqual(later);
    });

    it('marks the failed step and, for a timeout, the step a retry re-runs', () => {
      retryPlanResponse = of(timeoutPlan());
      const fixture = setup(timedOut(), timedOutInvoice());
      fixture.componentInstance.setTab('timeline');
      fixture.detectChanges();

      const steps = Array.from(fixture.nativeElement.querySelectorAll('.tl-step') as NodeListOf<Element>);
      expect(steps.length).toBe(2);
      expect(steps.map((s) => s.classList.contains('tl-step--failed-here'))).toEqual([false, true]);
      expect(text(steps[1].querySelector('.tl-marker'))).toBe('Failed here');
      expect(text(steps[0].querySelector('.tl-marker'))).toBe('Re-run starts here');
    });

    it('opens the map on the failure entry when the URL names none, without writing it to the URL', () => {
      retryPlanResponse = of(timeoutPlan());
      const fixture = setup(timedOut(), timedOutInvoice(), invoiceMap());

      const map = mapComponent(fixture);
      expect(map.focusSequence()).toBe(66);
      expect(map.currentIndex()).toBe(5);
      expect(fixture.componentInstance.focusedSequence()).toBeNull();
      expect(navigateSpy).not.toHaveBeenCalled();

      // Taking over the replay releases it for good, still without touching the URL.
      map.restart();
      fixture.detectChanges();
      expect(fixture.componentInstance.mapFocus()).toBeNull();
      expect(mapComponent(fixture).focusSequence()).toBeNull();
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('leaves a replay the viewer took over before the plan arrived where it is', () => {
      const plan$ = new Subject<SagaRetryPlan>();
      retryPlanResponse = plan$;
      const fixture = setup(timedOut(), timedOutInvoice(), invoiceMap());
      const map = mapComponent(fixture);
      expect(map.focusSequence()).toBeNull();

      map.stepForward();
      fixture.detectChanges();
      expect(map.currentIndex()).toBe(1);

      plan$.next(timeoutPlan());
      fixture.detectChanges();
      expect(fixture.componentInstance.retryPlan()).toEqual(timeoutPlan());
      expect(fixture.componentInstance.mapFocus()).toBeNull();
      expect(mapComponent(fixture).currentIndex()).toBe(1);
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('opens the map on the entry the URL names', () => {
      retryPlanResponse = of(timeoutPlan());
      const fixture = setup(
        timedOut(),
        timedOutInvoice(),
        invoiceMap(),
        undefined,
        [],
        of(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' })),
        of(convertToParamMap({ entry: '61' })),
      );

      expect(mapComponent(fixture).focusSequence()).toBe(61);
    });

    it('names the step, its message type and its from-state in the retry confirmation', () => {
      retryPlanResponse = of(timeoutPlan());
      const fixture = setup(timedOut(), timedOutInvoice());

      fixture.nativeElement.querySelector('.retry-row button').click();
      fixture.detectChanges();

      const el: HTMLElement = fixture.nativeElement;
      expect(text(el.querySelector('.retry-confirm-prompt'))).toBe(
        'Re-run step 1 (InvoiceIssued, Requested) for this saga only?',
      );
      expect(text(el.querySelector('.retry-audience'))).toBe(
        'Other services that consume InvoiceIssued still receive it.',
      );
    });

    it('marks Retry disabled and shows the reason when the plan refuses a retry', () => {
      const reason =
        'This saga was recorded before vSaga stored the message of every step, so the InvoiceIssued message that ran the step to re-run cannot be replayed.';
      retryPlanResponse = of(timeoutPlan({ retryable: false, reason }));
      const fixture = setup(timedOut(), timedOutInvoice());

      const el: HTMLElement = fixture.nativeElement;
      const button = el.querySelector('.retry-row button') as HTMLButtonElement;
      // aria-disabled, not disabled: the reason is described by the button, which a keyboard can reach.
      expect(button.getAttribute('aria-disabled')).toBe('true');
      expect(button.disabled).toBe(false);
      expect(text(el.querySelector('.retry-refusal'))).toBe(reason);
      expect(button.getAttribute('aria-describedby')).toBe('retry-refusal');

      fixture.componentInstance.askRetryConfirmation();
      fixture.detectChanges();
      expect(el.querySelector('.retry-confirm')).toBeNull();

      // The failed step is still marked: the plan names it, retryable or not.
      fixture.componentInstance.setTab('timeline');
      fixture.detectChanges();
      expect(el.querySelectorAll('.tl-step--failed-here').length).toBe(1);
    });
  });

  // What the session lets the viewer do with the saga's type: the API enforces it, and the page follows
  // the session so a viewer is not offered what the API would refuse.
  describe('access by permission', () => {
    const access = (permissions: string[], scoped: { sagaType: string; permissions: string[] }[] = []) => ({
      permissions,
      scoped,
    });
    const everything = access(['sagas.view', 'sagas.data', 'sagas.retry']);
    const route = () => convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' });
    const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();
    const forbiddenBody = httpError(
      403,
      problem('forbidden', "This needs the sagas.view permission for saga type 'OrderSaga'. See docs/dashboard.md#authentication."),
    );

    describe('the retry row', () => {
      it.each([
        ['sagas.view alone', access(['sagas.view'])],
        ['sagas.retry for another saga type only', access(['sagas.view'], [{ sagaType: 'ShippingSaga', permissions: ['sagas.retry'] }])],
      ])('has no button and says why for a viewer with %s', (_, held) => {
        authOptions = { access: held };
        const fixture = setup(makeDetail({ status: 'Failed' }));
        const el: HTMLElement = fixture.nativeElement;

        expect(el.querySelector('.retry-row button')).toBeNull();
        expect(text(el.querySelector('.retry-hint'))).toBe('You do not have permission to retry OrderSaga sagas.');
      });

      it('gives the button, and no hint, to a viewer holding sagas.retry for this saga type only', () => {
        authOptions = { access: access(['sagas.view'], [{ sagaType: 'OrderSaga', permissions: ['sagas.view', 'sagas.retry'] }]) };
        const fixture = setup(makeDetail({ status: 'TimedOut' }));
        const el: HTMLElement = fixture.nativeElement;

        expect(text(el.querySelector('.retry-row button'))).toBe('Retry this saga');
        expect(el.querySelector('.retry-hint')).toBeNull();
      });

      it('says nothing about retrying a saga that cannot be retried anyway', () => {
        authOptions = { access: access(['sagas.view']) };
        const fixture = setup(makeDetail({ status: 'Completed' }));

        expect(fixture.nativeElement.querySelector('.retry-row')).toBeNull();
      });

      it('follows the session: the button goes when sagas.retry is revoked and comes back when it is granted', () => {
        const fixture = setup(makeDetail({ status: 'Failed' }));
        const el: HTMLElement = fixture.nativeElement;
        expect(el.querySelector('.retry-row button')).not.toBeNull();

        auth.access.set(access(['sagas.view']));
        fixture.detectChanges();
        expect(el.querySelector('.retry-row button')).toBeNull();
        expect(el.querySelector('.retry-hint')).not.toBeNull();

        auth.access.set(everything);
        fixture.detectChanges();
        expect(el.querySelector('.retry-row button')).not.toBeNull();
        expect(el.querySelector('.retry-hint')).toBeNull();
      });

      it('shows the problem detail of a 403 from the retry, and the API text of any other refusal', () => {
        const fixture = setup(makeDetail({ status: 'Failed' }));
        const detail = "This needs the sagas.retry permission for saga type 'OrderSaga'. See docs/dashboard.md#authentication.";
        apiMock.retry.mockReturnValue(throwError(() => httpError(403, problem('forbidden', detail))));

        fixture.componentInstance.retry();
        fixture.detectChanges();

        expect(fixture.componentInstance.retryMessage()).toBe(detail);
        expect(text(fixture.nativeElement.querySelector('.retry-message'))).toBe(detail);

        apiMock.retry.mockReturnValue(throwError(() => httpError(422, { error: 'The step cannot be replayed.' })));
        fixture.componentInstance.retry();
        expect(fixture.componentInstance.retryMessage()).toBe('The step cannot be replayed.');

        apiMock.retry.mockReturnValue(throwError(() => httpError(502, '<html>Bad gateway</html>')));
        fixture.componentInstance.retry();
        expect(fixture.componentInstance.retryMessage()).toBe('Retry failed.');
      });
    });

    describe('the retry plan', () => {
      const plan: SagaRetryPlan = {
        retryable: true,
        reason: null,
        failureKind: 'StepFailed',
        failureSequenceNumber: 3,
        step: { sequenceNumber: 1, messageType: 'OrderSubmitted', messageId: 'm0', fromState: 'Initial' },
      };

      it('is asked for with sagas.view alone, and still marks the failed step', () => {
        authOptions = { access: access(['sagas.view']) };
        retryPlanResponse = of(plan);
        const fixture = setup(makeDetail({ status: 'Failed' }));

        expect(apiMock.getRetryPlan).toHaveBeenCalledWith('OrderSaga', 'saga-1');
        expect(fixture.componentInstance.failureSequence()).toBe(3);
        expect(fixture.nativeElement.querySelector('.retry-row button')).toBeNull();
      });

      it('is not asked for without sagas.view, and is when the session gains it', () => {
        authOptions = { access: access([]) };
        retryPlanResponse = of(plan);
        const fixture = setup(makeDetail({ status: 'Failed' }));
        expect(apiMock.getRetryPlan).not.toHaveBeenCalled();
        expect(fixture.componentInstance.retryPlan()).toBeNull();

        auth.access.set(access(['sagas.view']));
        fixture.detectChanges();

        expect(apiMock.getRetryPlan).toHaveBeenCalledTimes(1);
        expect(fixture.componentInstance.retryPlan()).toEqual(plan);
      });
    });

    describe('a forbidden saga', () => {
      /** The page as the API answers a viewer without sagas.view: 403 to the detail, the timeline and the map. */
      function setupForbidden(): ReturnType<typeof setup> {
        const paramMap$ = new Subject<ParamMap>();
        const fixture = setup(makeDetail(), [], makeMap(), undefined, [], paramMap$);
        apiMock.get.mockReturnValue(throwError(() => forbiddenBody));
        apiMock.getTimeline.mockReturnValue(throwError(() => forbiddenBody));
        apiMock.getMap.mockReturnValue(throwError(() => forbiddenBody));
        paramMap$.next(route());
        fixture.detectChanges();
        return fixture;
      }

      it('shows the no-access text and the way back, with no error banner', () => {
        const fixture = setupForbidden();
        const el: HTMLElement = fixture.nativeElement;

        expect(fixture.componentInstance.forbidden()).toBe(true);
        expect(fixture.componentInstance.error()).toBeNull();
        expect(fixture.componentInstance.loading()).toBe(false);
        expect(text(el.querySelector('.empty'))).toBe(
          'You do not have access to OrderSaga sagas. Ask an administrator for sagas.view on this saga type.',
        );
        expect(el.querySelector('[class*="banner--error"]')).toBeNull();
        expect(el.querySelector('.summary-card')).toBeNull();
        expect(el.querySelector('a.back')?.getAttribute('href')).toBe('/sagas');
        // An alert, because its text arrives with the element; and the page has its heading.
        expect(el.querySelector('.empty')?.getAttribute('role')).toBe('alert');
        expect(el.querySelector('h1')?.textContent?.trim()).toBe('OrderSaga');
      });

      it('is not a load error: any other failure still shows the banner', () => {
        const paramMap$ = new Subject<ParamMap>();
        const fixture = setup(makeDetail(), [], makeMap(), undefined, [], paramMap$);
        apiMock.get.mockReturnValue(throwError(() => httpError(404, null)));
        paramMap$.next(route());
        fixture.detectChanges();

        expect(fixture.componentInstance.forbidden()).toBe(false);
        expect(fixture.componentInstance.error()).toContain('Could not load');
        expect(fixture.nativeElement.querySelector('.banner--error')).not.toBeNull();
      });

      it('reloads nothing when live updates reconnect', () => {
        vi.useFakeTimers();
        const fixture = setupForbidden();
        const calls = () => [apiMock.get, apiMock.getTimeline, apiMock.getMap].map((m) => m.mock.calls.length);
        expect(calls()).toEqual([1, 1, 1]);
        expect(fixture.componentInstance.timelineError()).toBe(true);

        hubMock.connectionState$.next('reconnecting');
        hubMock.connectionState$.next('connected');
        vi.advanceTimersByTime(REFRESH_AUDIT_MS);

        expect(calls()).toEqual([1, 1, 1]);
        expect(fixture.componentInstance.forbidden()).toBe(true);
      });

      // The server closes a connection when the user's access changes, so the reconnect may be the news that
      // a grant arrived: the session is read, and the page asks again if it now holds the permission.
      it('reads the session when live updates reconnect, and asks again once it holds sagas.view', () => {
        vi.useFakeTimers();
        authOptions = { access: access([]) };
        const fixture = setupForbidden();
        auth.refresh.mockImplementation(() => {
          auth.access.set(access(['sagas.view']));
          return Promise.resolve('authenticated' as const);
        });
        apiMock.get.mockReturnValue(of(makeDetail({ status: 'Completed' })));

        hubMock.connectionState$.next('reconnecting');
        hubMock.connectionState$.next('connected');
        vi.advanceTimersByTime(REFRESH_AUDIT_MS);
        fixture.detectChanges();

        expect(auth.refresh).toHaveBeenCalledTimes(1);
        expect(apiMock.get).toHaveBeenCalledTimes(2);
        expect(fixture.componentInstance.forbidden()).toBe(false);
        expect(fixture.nativeElement.querySelector('.summary-card')).not.toBeNull();
      });

      it('asks the API for nothing on a push either', () => {
        vi.useFakeTimers();
        const fixture = setupForbidden();

        hubMock.timelineEntryAdded$.next({ sagaType: 'OrderSaga', correlationId: 'saga-1', entry: makeEntry() });
        vi.advanceTimersByTime(REFRESH_AUDIT_MS);

        expect(apiMock.get).toHaveBeenCalledTimes(1);
        expect(apiMock.getTimeline).toHaveBeenCalledTimes(1);
        expect(fixture.componentInstance.forbidden()).toBe(true);
      });

      it.each([
        ['no permission at all', access([])],
        ['sagas.view for another saga type only', access([], [{ sagaType: 'ShippingSaga', permissions: ['sagas.view', 'sagas.data', 'sagas.retry'] }])],
      ])('asks for the detail alone when the session holds %s, and loads the rest if the API lets it in', (_, held) => {
        authOptions = { access: held };
        const paramMap$ = new Subject<ParamMap>();
        const detail$ = new Subject<SagaDetailModel>();
        const fixture = setup(makeDetail(), [makeEntry()], makeMap(), undefined, [], paramMap$);
        apiMock.get.mockReturnValue(detail$);
        const parts = () => [apiMock.getTimeline, apiMock.getMap, apiMock.findByCorrelationId, apiMock.getChildren].map((m) => m.mock.calls.length);

        paramMap$.next(route());
        expect(apiMock.get).toHaveBeenCalledTimes(1);
        expect(parts()).toEqual([0, 0, 0, 0]);

        detail$.next(makeDetail({ status: 'Completed' }));
        expect(parts()).toEqual([1, 1, 1, 1]);
        expect(fixture.componentInstance.detail()?.summary.status).toBe('Completed');
        expect(fixture.componentInstance.forbidden()).toBe(false);
      });

      it('asks again when the session gains sagas.view', () => {
        authOptions = { access: access([]) };
        const paramMap$ = new Subject<ParamMap>();
        const fixture = setup(makeDetail(), [], makeMap(), undefined, [], paramMap$);
        apiMock.get.mockReturnValue(throwError(() => forbiddenBody));
        paramMap$.next(route());
        fixture.detectChanges();
        expect(fixture.componentInstance.forbidden()).toBe(true);
        expect(apiMock.get).toHaveBeenCalledTimes(1);

        apiMock.get.mockReturnValue(of(makeDetail({ status: 'Completed' })));
        auth.access.set(access(['sagas.view']));
        fixture.detectChanges();

        expect(apiMock.get).toHaveBeenCalledTimes(2);
        expect(fixture.componentInstance.forbidden()).toBe(false);
        expect(fixture.nativeElement.querySelector('.summary-card')).not.toBeNull();
      });

      it.each([
        ['every permission', {}],
        ['sagas.view scoped to the routed type', { access: access([], [{ sagaType: 'OrderSaga', permissions: ['sagas.view'] }]) }],
      ])('asks once for a 403 that is already there when the page first renders, for a session with %s', (_, held) => {
        apiMock = {
          get: vi.fn().mockReturnValue(throwError(() => forbiddenBody)),
          getTimeline: vi.fn().mockReturnValue(throwError(() => forbiddenBody)),
          getMap: vi.fn().mockReturnValue(throwError(() => forbiddenBody)),
          retry: vi.fn(),
          getRetryPlan: vi.fn().mockReturnValue(EMPTY),
          findByCorrelationId: vi.fn().mockReturnValue(of([])),
          getChildren: vi.fn().mockReturnValue(of([])),
        };
        hubMock = {
          sagaUpdated$: new Subject<SagaSummary>(),
          timelineEntryAdded$: new Subject<{ sagaType: string; correlationId: string; entry: SagaLogEntry }>(),
          connectionState$: new BehaviorSubject<SagaHubConnectionState>('connected'),
          subscribeToSaga: vi.fn().mockResolvedValue(undefined),
          unsubscribeFromSaga: vi.fn().mockResolvedValue(undefined),
        };
        TestBed.configureTestingModule({
          imports: [SagaDetail],
          providers: [
            provideRouter([]),
            provideAuthMock(held),
            { provide: SagaApiService, useValue: apiMock },
            { provide: SagaHubService, useValue: hubMock },
            { provide: ActivatedRoute, useValue: { paramMap: of(route()), queryParamMap: of(convertToParamMap({})) } },
          ],
        });
        const fixture = TestBed.createComponent(SagaDetail);

        fixture.detectChanges();
        fixture.detectChanges();

        expect(fixture.componentInstance.forbidden()).toBe(true);
        expect(apiMock.get).toHaveBeenCalledTimes(1);
      });

      it('does not ask again for a 403 the session cannot explain', () => {
        const fixture = setupForbidden();
        expect(apiMock.get).toHaveBeenCalledTimes(1);

        // The session already held sagas.view; refreshing it with the same answer changes nothing.
        auth.access.set(access(['sagas.view', 'sagas.data']));
        fixture.detectChanges();

        expect(apiMock.get).toHaveBeenCalledTimes(1);
        expect(fixture.componentInstance.forbidden()).toBe(true);
      });

      it('shows the no-access state when a live refresh finds that sagas.view is gone', () => {
        vi.useFakeTimers();
        const fixture = setup(makeDetail({ status: 'Running' }));
        expect(fixture.nativeElement.querySelector('.summary-card')).not.toBeNull();
        apiMock.get.mockReturnValue(throwError(() => forbiddenBody));

        hubMock.connectionState$.next('reconnecting');
        hubMock.connectionState$.next('connected');
        vi.advanceTimersByTime(REFRESH_AUDIT_MS);
        fixture.detectChanges();

        expect(fixture.componentInstance.forbidden()).toBe(true);
        expect(fixture.nativeElement.querySelector('.summary-card')).toBeNull();
        expect(text(fixture.nativeElement.querySelector('.empty'))).toContain('You do not have access to OrderSaga sagas.');
      });
    });

    describe('refusals that are not "no access"', () => {
      it("shows another 403 in its own words, and a 403 that is not the API's as a load error", () => {
        const paramMap$ = new Subject<ParamMap>();
        const fixture = setup(makeDetail(), [], makeMap(), undefined, [], paramMap$);
        const change = httpError(403, problem('password_change_required', 'Change your password before using the dashboard.'));
        apiMock.get.mockReturnValue(throwError(() => change));
        paramMap$.next(route());
        fixture.detectChanges();

        const el: HTMLElement = fixture.nativeElement;
        expect(fixture.componentInstance.forbidden()).toBe(false);
        expect(text(el.querySelector('.banner--error'))).toBe('Change your password before using the dashboard.');
        expect(el.querySelector('.banner--error')?.getAttribute('role')).toBe('alert');

        apiMock.get.mockReturnValue(throwError(() => httpError(403, '<html>Forbidden</html>')));
        fixture.componentInstance.load();
        fixture.detectChanges();
        expect(fixture.componentInstance.forbidden()).toBe(false);
        expect(text(el.querySelector('.banner--error'))).toBe('Could not load this saga. It may not exist.');
      });

      it.each<[string, () => unknown]>([
        ['a 500', () => httpError(500, { title: 'Boom' })],
        ['a network failure', () => new Error('offline')],
        ['a 403 that asks for a password change', () => httpError(403, problem('password_change_required', 'Change it.'))],
        ["a 403 that is not the API's", () => httpError(403, '<html>Forbidden</html>')],
      ])('keeps the saga on screen when a live refresh fails with %s', (_, failure) => {
        vi.useFakeTimers();
        const fixture = setup(makeDetail({ status: 'Running' }));
        apiMock.get.mockReturnValue(throwError(() => failure()));

        hubMock.sagaUpdated$.next(fixture.componentInstance.detail()!.summary);
        vi.advanceTimersByTime(REFRESH_AUDIT_MS);
        fixture.detectChanges();

        expect(apiMock.get).toHaveBeenCalledTimes(2);
        expect(fixture.componentInstance.forbidden()).toBe(false);
        expect(fixture.componentInstance.error()).toBeNull();
        expect(fixture.nativeElement.querySelector('.summary-card')).not.toBeNull();
        expect(fixture.nativeElement.querySelector('.banner--error')).toBeNull();
      });
    });

    describe('answers that arrive out of order', () => {
      it('does not let a 403 that was sent before a newer answer forbid the page (live refresh)', () => {
        vi.useFakeTimers();
        const fixture = setup(makeDetail({ status: 'Running' }));
        const late = new Subject<SagaDetailModel>();
        apiMock.get.mockReturnValueOnce(late).mockReturnValue(of(makeDetail({ status: 'Running', version: 3 })));
        const push = () => hubMock.sagaUpdated$.next({ ...fixture.componentInstance.detail()!.summary });

        push();
        vi.advanceTimersByTime(REFRESH_AUDIT_MS); // refresh 1, its answer pending
        push();
        vi.advanceTimersByTime(REFRESH_AUDIT_MS); // refresh 2, answered
        late.error(forbiddenBody);
        fixture.detectChanges();

        expect(fixture.componentInstance.forbidden()).toBe(false);
        expect(fixture.nativeElement.querySelector('.summary-card')).not.toBeNull();
      });

      // Sent order, not answer order: an older request that succeeds late says nothing against a newer request
      // whose 403 is genuine.
      it('forbids the page when a newer load is refused after an older one was answered', () => {
        const fixture = setup(makeDetail());
        const older = new Subject<SagaDetailModel>();
        const newer = new Subject<SagaDetailModel>();
        apiMock.get.mockReturnValueOnce(older).mockReturnValueOnce(newer);
        fixture.componentInstance.load();
        fixture.componentInstance.load();

        older.next(makeDetail());
        newer.error(forbiddenBody);
        fixture.detectChanges();

        expect(fixture.componentInstance.forbidden()).toBe(true);
        expect(fixture.nativeElement.querySelector('.summary-card')).toBeNull();
      });

      it('forbids the page when a live refresh is refused after an older refresh was answered', () => {
        vi.useFakeTimers();
        const fixture = setup(makeDetail({ status: 'Running' }));
        const older = new Subject<SagaDetailModel>();
        const newer = new Subject<SagaDetailModel>();
        apiMock.get.mockReturnValueOnce(older).mockReturnValueOnce(newer);
        const push = () => hubMock.sagaUpdated$.next({ ...fixture.componentInstance.detail()!.summary });

        push();
        vi.advanceTimersByTime(REFRESH_AUDIT_MS);
        push();
        vi.advanceTimersByTime(REFRESH_AUDIT_MS);
        older.next(makeDetail({ status: 'Running', version: 3 }));
        newer.error(forbiddenBody);
        fixture.detectChanges();

        expect(fixture.componentInstance.forbidden()).toBe(true);
      });

      it.each([
        ['an older load is refused after a newer one was answered', 'newer-first'],
        ['an older load is answered after a newer one was refused', 'older-first'],
      ])('shows the saga when %s', (_, order) => {
        const fixture = setup(makeDetail());
        const older = new Subject<SagaDetailModel>();
        const newer = new Subject<SagaDetailModel>();
        apiMock.get.mockReturnValueOnce(older).mockReturnValueOnce(newer);
        fixture.componentInstance.load();
        fixture.componentInstance.load();

        if (order === 'newer-first') {
          newer.next(makeDetail());
          older.error(forbiddenBody);
        } else {
          newer.error(forbiddenBody);
          expect(fixture.componentInstance.forbidden()).toBe(true);
          older.next(makeDetail());
        }
        fixture.detectChanges();

        expect(fixture.componentInstance.forbidden()).toBe(false);
        expect(fixture.nativeElement.querySelector('.summary-card')).not.toBeNull();
      });
    });

    describe('a refresh after the session lost sagas.view', () => {
      const parts = () => [apiMock.getTimeline, apiMock.getMap, apiMock.findByCorrelationId, apiMock.getChildren].map((m) => m.mock.calls.length);

      function loseView() {
        vi.useFakeTimers();
        const fixture = setup(makeDetail({ status: 'Running' }));
        const answer = new Subject<SagaDetailModel>();
        apiMock.get.mockReturnValue(answer);
        auth.access.set(access([]));
        hubMock.sagaUpdated$.next({ ...fixture.componentInstance.detail()!.summary });
        vi.advanceTimersByTime(REFRESH_AUDIT_MS);
        return { fixture, answer };
      }

      it('asks for the detail first, and for the rest only when it is let through', () => {
        const { answer } = loseView();
        expect(apiMock.get).toHaveBeenCalledTimes(2);
        expect(parts()).toEqual([1, 1, 1, 1]);

        answer.next(makeDetail({ status: 'Running', version: 3 }));
        expect(parts()).toEqual([2, 2, 2, 2]);
      });

      it('asks for nothing more after a 403', () => {
        const { fixture, answer } = loseView();

        answer.error(forbiddenBody);
        fixture.detectChanges();

        expect(parts()).toEqual([1, 1, 1, 1]);
        expect(fixture.componentInstance.forbidden()).toBe(true);
      });
    });

    describe('while the session is ending', () => {
      it('shows neither a no-access state nor a permission hint, and the saga again if the session returns', () => {
        authOptions = { access: access(['sagas.view']) };
        const fixture = setup(makeDetail({ status: 'Failed' }));
        const el: HTMLElement = fixture.nativeElement;
        expect(el.querySelector('.retry-hint')).not.toBeNull();
        expect(el.querySelector('.ov-bar .muted')).not.toBeNull();

        auth.status.set('anonymous');
        fixture.detectChanges();

        expect(el.querySelector('.retry-hint')).toBeNull();
        expect(el.querySelector('.ov-bar')).toBeNull();
        expect(el.querySelector('.summary-card')).toBeNull();
        expect(el.querySelector('.empty')).toBeNull();
        expect(el.textContent).not.toContain('hidden for your role');
        expect(el.querySelector('a.back')).not.toBeNull();

        auth.status.set('authenticated');
        fixture.detectChanges();
        expect(el.querySelector('.summary-card')).not.toBeNull();
      });

      it('says nothing about live updates either', () => {
        const fixture = setup(makeDetail({ status: 'Running' }));
        hubMock.connectionState$.next('reconnecting');
        fixture.detectChanges();
        expect(fixture.nativeElement.querySelector('.banner--warning')).not.toBeNull();

        auth.status.set('anonymous');
        fixture.detectChanges();
        expect(fixture.nativeElement.querySelector('.banner--warning')).toBeNull();

        hubMock.connectionState$.next('disconnected');
        fixture.detectChanges();
        expect(fixture.nativeElement.textContent).not.toContain('Live updates disconnected');
      });

      it('does not say "no access" on a forbidden page either', () => {
        const paramMap$ = new Subject<ParamMap>();
        const fixture = setup(makeDetail(), [], makeMap(), undefined, [], paramMap$);
        apiMock.get.mockReturnValue(throwError(() => forbiddenBody));
        paramMap$.next(route());
        fixture.detectChanges();
        expect(fixture.nativeElement.querySelector('.empty')).not.toBeNull();

        auth.status.set('anonymous');
        fixture.detectChanges();

        expect(fixture.nativeElement.querySelector('.empty')).toBeNull();
        expect(fixture.nativeElement.querySelector('h1')).toBeNull();
      });
    });

    describe('a route reuse', () => {
      it('judges the permissions for the saga type that is routed now, not the one loaded first', () => {
        authOptions = {
          access: access([], [
            { sagaType: 'OrderSaga', permissions: ['sagas.view', 'sagas.data', 'sagas.retry'] },
            { sagaType: 'ShippingSaga', permissions: ['sagas.view'] },
          ]),
        };
        const paramMap$ = new BehaviorSubject<ParamMap>(route());
        const original = makeDetail({ status: 'Failed', parentSagaType: 'ParentSaga', parentCorrelationId: 'parent-1' });
        const fixture = setup(original, [], makeMap(), undefined, [], paramMap$);
        const el: HTMLElement = fixture.nativeElement;
        const dataButtons = () =>
          Array.from(el.querySelectorAll<HTMLButtonElement>('.ov-bar button')).filter((b) => b.textContent?.trim() !== 'Compare');
        const startedBy = () => Array.from(el.querySelectorAll('.related')).find((r) => text(r.querySelector('.related-label')) === 'Started by');
        expect(el.querySelector('.retry-row button')).not.toBeNull();
        expect(dataButtons().map((b) => b.disabled)).toEqual([false, false]);
        expect(startedBy()?.querySelector('a')).toBeNull();

        // The router keeps this instance for a sibling's page: the type changes, the viewer's rights do not.
        apiMock.get.mockReturnValue(
          of(makeDetail({ sagaType: 'ShippingSaga', correlationId: 'saga-2', status: 'Failed', parentSagaType: 'OrderSaga', parentCorrelationId: 'order-1' })),
        );
        paramMap$.next(convertToParamMap({ sagaType: 'ShippingSaga', id: 'saga-2' }));
        fixture.detectChanges();

        expect(el.querySelector('.retry-row button')).toBeNull();
        expect(text(el.querySelector('.retry-hint'))).toBe('You do not have permission to retry ShippingSaga sagas.');
        expect(dataButtons().map((b) => b.disabled)).toEqual([true, true]);
        expect(startedBy()?.querySelector('a')?.getAttribute('href')).toBe('/sagas/OrderSaga/order-1');

        apiMock.get.mockReturnValue(of(original));
        paramMap$.next(route());
        fixture.detectChanges();
        expect(el.querySelector('.retry-row button')).not.toBeNull();
        expect(dataButtons().map((b) => b.disabled)).toEqual([false, false]);
        expect(startedBy()?.querySelector('a')).toBeNull();
      });

      it('reads nothing again for the change of type itself', () => {
        authOptions = { access: access([], [{ sagaType: 'ShippingSaga', permissions: ['sagas.view', 'sagas.data'] }]) };
        const paramMap$ = new BehaviorSubject<ParamMap>(route());
        const fixture = setup(makeDetail(), [], makeMap(), undefined, [], paramMap$);
        expect(fixture.componentInstance.forbidden()).toBe(false);
        apiMock.get.mockClear();
        apiMock.getTimeline.mockClear();

        paramMap$.next(convertToParamMap({ sagaType: 'ShippingSaga', id: 'saga-2' }));
        fixture.detectChanges();

        // One load for the new saga, and nothing more for the viewer's rights on it.
        expect(apiMock.get).toHaveBeenCalledTimes(1);
        expect(apiMock.getTimeline).toHaveBeenCalledTimes(1);
      });
    });

    // The server closes a user's connection whenever their access changes, so a later connect reads the
    // session too: a grant that widened what the viewer may do shows up without a reload.
    describe('a reconnect of live updates', () => {
      const reconnect = () => {
        hubMock.connectionState$.next('reconnecting');
        hubMock.connectionState$.next('connected');
        vi.advanceTimersByTime(REFRESH_AUDIT_MS);
      };
      /** What the next session read brings. */
      const grant = (held: ReturnType<typeof access>) =>
        auth.refresh.mockImplementation(() => {
          auth.access.set(held);
          return Promise.resolve('authenticated' as const);
        });

      it('reads the session once, and not on the first connect', () => {
        vi.useFakeTimers();
        const fixture = setup(makeDetail({ status: 'Running' }));
        expect(auth.refresh).not.toHaveBeenCalled();

        reconnect();
        fixture.detectChanges();

        expect(auth.refresh).toHaveBeenCalledTimes(1);
        expect(apiMock.get).toHaveBeenCalledTimes(2);
        reconnect();
        expect(auth.refresh).toHaveBeenCalledTimes(2);
      });

      it('shows the retry row, with no reload, when the session read brings sagas.retry for the type', () => {
        vi.useFakeTimers();
        authOptions = { access: access(['sagas.view']) };
        const fixture = setup(makeDetail({ status: 'Failed' }));
        const el: HTMLElement = fixture.nativeElement;
        expect(el.querySelector('.retry-row button')).toBeNull();
        expect(text(el.querySelector('.retry-hint'))).toContain('You do not have permission to retry');
        grant(access(['sagas.view'], [{ sagaType: 'OrderSaga', permissions: ['sagas.view', 'sagas.retry'] }]));

        reconnect();
        fixture.detectChanges();

        expect(el.querySelector('.retry-row button')).not.toBeNull();
        expect(el.querySelector('.retry-hint')).toBeNull();
        // The page re-read its data once for the reconnect, and the gain did not add a load of its own.
        expect(apiMock.get).toHaveBeenCalledTimes(2);
        expect(fixture.componentInstance.loading()).toBe(false);
      });

      it('asks for the retry plan once when the session read brings sagas.view', () => {
        vi.useFakeTimers();
        authOptions = { access: access([]) };
        const fixture = setup(makeDetail({ status: 'Failed' }));
        expect(apiMock.getRetryPlan).not.toHaveBeenCalled();
        grant(access(['sagas.view']));

        reconnect();
        fixture.detectChanges();

        expect(apiMock.getRetryPlan).toHaveBeenCalledTimes(1);
        expect(apiMock.getRetryPlan).toHaveBeenCalledWith('OrderSaga', 'saga-1');
      });

      it('opens the data bar when the session read brings sagas.data', () => {
        vi.useFakeTimers();
        authOptions = { access: access(['sagas.view']) };
        const fixture = setup(makeDetail({ status: 'Completed' }), [makeEntry()]);
        const el: HTMLElement = fixture.nativeElement;
        const startButton = () => el.querySelector<HTMLButtonElement>('.ov-bar button')!;
        expect(startButton().disabled).toBe(true);
        grant(access(['sagas.view', 'sagas.data']));

        reconnect();
        fixture.detectChanges();

        expect(startButton().disabled).toBe(false);
        expect(el.querySelector('.ov-bar .muted')).toBeNull();
      });
    });

    describe('the hub group', () => {
      it('is not joined for a session without sagas.view for the type, and is once the API lets the page in', () => {
        authOptions = { access: access([]) };
        const paramMap$ = new Subject<ParamMap>();
        const detail$ = new Subject<SagaDetailModel>();
        setup(makeDetail(), [], makeMap(), undefined, [], paramMap$);
        apiMock.get.mockReturnValue(detail$);

        paramMap$.next(route());
        expect(hubMock.subscribeToSaga).not.toHaveBeenCalled();

        detail$.next(makeDetail({ status: 'Completed' }));
        expect(hubMock.subscribeToSaga).toHaveBeenCalledTimes(1);
        expect(hubMock.subscribeToSaga).toHaveBeenCalledWith('OrderSaga', 'saga-1');
      });

      it('is left when the API answers 403 to a session that thought it could, and not left again on destroy', () => {
        const paramMap$ = new Subject<ParamMap>();
        const detail$ = new Subject<SagaDetailModel>();
        const fixture = setup(makeDetail(), [], makeMap(), undefined, [], paramMap$);
        apiMock.get.mockReturnValue(detail$);
        paramMap$.next(route());
        expect(hubMock.subscribeToSaga).toHaveBeenCalledTimes(1);
        expect(hubMock.unsubscribeFromSaga).not.toHaveBeenCalled();

        detail$.error(forbiddenBody);
        expect(hubMock.unsubscribeFromSaga).toHaveBeenCalledTimes(1);
        expect(hubMock.unsubscribeFromSaga).toHaveBeenCalledWith('OrderSaga', 'saga-1');

        fixture.destroy();
        expect(hubMock.unsubscribeFromSaga).toHaveBeenCalledTimes(1);
      });

      // The answer to a load that was in flight when the page was left must not join the group again: the hub
      // would keep a record of it and send it again on every reconnect, for a page that no longer exists.
      it.each([
        ['a session that may view the type', {}, 1],
        ['a session that may not (the API lets it in after leaving)', { access: access([]) }, 0],
      ])('is not joined by a detail that answers after the page was left, for %s', (_, held, joinedBeforeLeaving) => {
        authOptions = held;
        const paramMap$ = new Subject<ParamMap>();
        const detail$ = new Subject<SagaDetailModel>();
        const fixture = setup(makeDetail(), [], makeMap(), undefined, [], paramMap$);
        apiMock.get.mockReturnValue(detail$);
        paramMap$.next(route());
        expect(hubMock.subscribeToSaga).toHaveBeenCalledTimes(joinedBeforeLeaving);

        fixture.destroy();
        detail$.next(makeDetail({ status: 'Completed' }));

        expect(hubMock.subscribeToSaga).toHaveBeenCalledTimes(joinedBeforeLeaving);
        expect(hubMock.unsubscribeFromSaga).toHaveBeenCalledTimes(joinedBeforeLeaving);
      });

      it('is joined when a forbidden page gains sagas.view and loads', () => {
        authOptions = { access: access([]) };
        const paramMap$ = new Subject<ParamMap>();
        const fixture = setup(makeDetail(), [], makeMap(), undefined, [], paramMap$);
        apiMock.get.mockReturnValue(throwError(() => forbiddenBody));
        paramMap$.next(route());
        fixture.detectChanges();
        expect(fixture.componentInstance.forbidden()).toBe(true);
        expect(hubMock.subscribeToSaga).not.toHaveBeenCalled();

        apiMock.get.mockReturnValue(of(makeDetail({ status: 'Completed' })));
        auth.access.set(access(['sagas.view']));
        fixture.detectChanges();

        expect(hubMock.subscribeToSaga).toHaveBeenCalledTimes(1);
        expect(hubMock.subscribeToSaga).toHaveBeenCalledWith('OrderSaga', 'saga-1');
      });

      it('moves with a route reuse: the old group is left, and the new one joined only for a type the session may see', () => {
        authOptions = { access: access([], [{ sagaType: 'OrderSaga', permissions: ['sagas.view'] }]) };
        const paramMap$ = new BehaviorSubject<ParamMap>(route());
        setup(makeDetail(), [], makeMap(), undefined, [], paramMap$);
        expect(hubMock.subscribeToSaga).toHaveBeenCalledTimes(1);
        apiMock.get.mockReturnValue(new Subject<SagaDetailModel>());

        paramMap$.next(convertToParamMap({ sagaType: 'ShippingSaga', id: 'saga-2' }));

        expect(hubMock.unsubscribeFromSaga).toHaveBeenCalledWith('OrderSaga', 'saga-1');
        expect(hubMock.subscribeToSaga).toHaveBeenCalledTimes(1);
      });
    });

    describe('"Started by"', () => {
      const startedBy = () => makeDetail({ parentSagaType: 'ParentSaga', parentCorrelationId: 'parent-1' });
      const startedByRow = (el: HTMLElement) =>
        Array.from(el.querySelectorAll('.related')).find((r) => text(r.querySelector('.related-label')) === 'Started by');

      it('links to a parent whose type the viewer may see', () => {
        const fixture = setup(startedBy());
        const row = startedByRow(fixture.nativeElement)!;

        expect(row.querySelector('a.related-link')?.getAttribute('href')).toBe('/sagas/ParentSaga/parent-1');
        expect(row.textContent).not.toContain('(no access)');
      });

      it('names the parent as plain text, marked "(no access)", when its type is out of the viewer\'s reach', () => {
        authOptions = { access: access([], [{ sagaType: 'OrderSaga', permissions: ['sagas.view'] }]) };
        const fixture = setup(startedBy());
        const row = startedByRow(fixture.nativeElement)!;

        expect(row.querySelector('a')).toBeNull();
        expect(text(row.querySelector('.related-links'))).toBe('ParentSaga parent-1 (no access)');
      });

      it('follows the session when sagas.view for the parent type is granted', () => {
        authOptions = { access: access([], [{ sagaType: 'OrderSaga', permissions: ['sagas.view'] }]) };
        const fixture = setup(startedBy());

        auth.access.set(access([], [{ sagaType: 'OrderSaga', permissions: ['sagas.view'] }, { sagaType: 'ParentSaga', permissions: ['sagas.view'] }]));
        fixture.detectChanges();

        expect(startedByRow(fixture.nativeElement)?.querySelector('a.related-link')).not.toBeNull();
      });
    });

    describe('saga data', () => {
      const dataButtons = (el: HTMLElement) => Array.from(el.querySelectorAll<HTMLButtonElement>('.ov-bar button'));
      /** What the API sends a viewer who holds sagas.data; a stale session must still not print it. */
      const withError = [
        makeEntry({ sequenceNumber: 1, entryType: 'SagaStarted', messageId: 'm0', payloadJson: '{"Total":10}' }),
        makeEntry({ sequenceNumber: 2, entryType: 'StepFailed', messageId: 'm0', errorMessage: 'card declined' }),
      ];

      it('disables the data buttons beside the sentence naming the permission, and opens no view', () => {
        authOptions = { access: access(['sagas.view', 'sagas.retry']) };
        const detail = { ...makeDetail({ status: 'Completed' }), dataJson: '{"Total":10}' };
        const fixture = setup(detail, [], makeMap(), undefined, [], undefined, of(convertToParamMap({ data: 'end' })));
        const el: HTMLElement = fixture.nativeElement;

        expect(dataButtons(el).map((b) => b.disabled)).toEqual([true, true, true]);
        expect(text(el.querySelector('.ov-bar .muted'))).toBe('Saga data is hidden for your role. It needs the sagas.data permission.');
        expect(el.querySelector('.ov-panel')).toBeNull();
      });

      it('enables them for a viewer holding sagas.data for this saga type', () => {
        authOptions = { access: access(['sagas.view'], [{ sagaType: 'OrderSaga', permissions: ['sagas.view', 'sagas.data'] }]) };
        const fixture = setup(makeDetail({ status: 'Completed' }), [makeEntry()]);
        const el: HTMLElement = fixture.nativeElement;

        expect(dataButtons(el).filter((b) => b.textContent?.trim() !== 'Compare').map((b) => b.disabled)).toEqual([false, false]);
        expect(el.querySelector('.ov-bar .muted')).toBeNull();
      });

      it('shows timeline rows without error text, and no data toggle, for a viewer without sagas.data', () => {
        authOptions = { access: access(['sagas.view']) };
        const fixture = setup(makeDetail({ status: 'Failed' }), withError, makeMap(), undefined, [], undefined, of(convertToParamMap({ tab: 'timeline' })));
        const el: HTMLElement = fixture.nativeElement;

        expect(el.querySelectorAll('.tl-row').length).toBe(2);
        expect(el.querySelector('.tl-error')).toBeNull();
        expect(el.textContent).not.toContain('card declined');
        expect(el.querySelector('.tl-data')).toBeNull();
      });

      it('shows the error text and the data toggles with sagas.data', () => {
        const fixture = setup(makeDetail({ status: 'Failed' }), withError, makeMap(), undefined, [], undefined, of(convertToParamMap({ tab: 'timeline' })));
        const el: HTMLElement = fixture.nativeElement;

        expect(text(el.querySelector('.tl-error'))).toBe('card declined');
        expect(el.querySelector('.tl-data')).not.toBeNull();
      });

      it('follows the session when sagas.data is revoked', () => {
        const fixture = setup(makeDetail({ status: 'Failed' }), withError, makeMap(), undefined, [], undefined, of(convertToParamMap({ tab: 'timeline' })));
        const el: HTMLElement = fixture.nativeElement;
        expect(el.querySelector('.tl-error')).not.toBeNull();

        auth.access.set(access(['sagas.view', 'sagas.retry']));
        fixture.detectChanges();

        expect(el.querySelector('.tl-error')).toBeNull();
        expect(el.querySelector('.tl-data')).toBeNull();
        expect(dataButtons(el).every((b) => b.disabled)).toBe(true);
      });

      it('keeps the data buttons disabled for a viewer whose sagas.data is for another saga type', () => {
        authOptions = { access: access(['sagas.view'], [{ sagaType: 'ShippingSaga', permissions: ['sagas.view', 'sagas.data'] }]) };
        const fixture = setup(makeDetail({ status: 'Completed' }), [makeEntry()]);
        const el: HTMLElement = fixture.nativeElement;

        expect(dataButtons(el).every((b) => b.disabled)).toBe(true);
        expect(text(el.querySelector('.ov-bar .muted'))).toBe('Saga data is hidden for your role. It needs the sagas.data permission.');
      });

      // What was loaded without sagas.data was redacted, and nothing but a new read brings the rest.
      it('reads the detail, the timeline and the map again when sagas.data is granted, and not before', () => {
        authOptions = { access: access(['sagas.view']) };
        const fixture = setup(makeDetail({ status: 'Completed' }), [makeEntry()]);
        const calls = () => [apiMock.get, apiMock.getTimeline, apiMock.getMap, apiMock.findByCorrelationId].map((m) => m.mock.calls.length);
        fixture.detectChanges();
        expect(calls()).toEqual([1, 1, 1, 1]);

        auth.access.set(access(['sagas.view', 'sagas.data']));
        fixture.detectChanges();

        expect(calls()).toEqual([2, 2, 2, 1]);
        expect(dataButtons(fixture.nativeElement).filter((b) => b.textContent?.trim() !== 'Compare').every((b) => !b.disabled)).toBe(true);

        // Another read of the same access asks for nothing.
        auth.access.set(access(['sagas.view', 'sagas.data', 'sagas.retry']));
        fixture.detectChanges();
        expect(calls()).toEqual([2, 2, 2, 1]);
      });

      describe("the map's failure card", () => {
        const failedMap = () =>
          makeMap({
            nodes: [{ id: 'OrderSaga', displayName: 'OrderSaga', kind: 'Orchestrator', status: 'failed', messagesIn: 1, messagesOut: 0 }],
            edges: [],
            events: [
              { sequenceNumber: 1, edgeId: null, nodeId: null, entryType: 'SagaStarted', messageType: 'SubmitOrder', errorMessage: null, occurredAtUtc: '2026-01-01T00:00:00.000Z' },
              { sequenceNumber: 2, edgeId: null, nodeId: null, entryType: 'MessageReceived', messageType: 'SubmitOrder', errorMessage: null, occurredAtUtc: '2026-01-01T00:00:00.050Z' },
              { sequenceNumber: 3, edgeId: null, nodeId: 'OrderSaga', entryType: 'StepFailed', messageType: 'SubmitOrder', errorMessage: 'unroutable', occurredAtUtc: '2026-01-01T00:00:00.080Z' },
            ],
            failureEventIndex: 2,
          });

        it('prints the error text only while the viewer holds sagas.data, and still names the entry', () => {
          const fixture = setup(makeDetail({ status: 'Failed' }), [], failedMap());
          const el: HTMLElement = fixture.nativeElement;
          expect(text(el.querySelector('.error-card'))).toContain('unroutable');

          // The map on the page is older than the session: it still carries the text it was sent.
          auth.access.set(access(['sagas.view']));
          fixture.detectChanges();

          expect(el.querySelector('.error-card strong')).not.toBeNull();
          expect(text(el.querySelector('.error-card'))).not.toContain('unroutable');
        });
      });
    });
  });

  // Guide mode points at elements by their data-tour anchor, and the page tells it when a part is on screen.
  describe('guide mode', () => {
    const anchorsIn = (el: Element) => Array.from(el.querySelectorAll('[data-tour]'), (e) => e.getAttribute('data-tour')).sort();
    const shown = () => guideMock.areaShown.mock.calls.map((c) => c[0]);
    /** The anchors the tours of these areas name, apart from the top bar's. */
    const named = (...areas: Array<keyof typeof GUIDE_TOURS>): GuideAnchor[] =>
      [
        ...new Set(
          areas.flatMap((area) =>
            GUIDE_TOURS[area].flatMap((step) => [step.anchor, step.fallbackAnchor, step.reveal]),
          ),
        ),
      ].filter((name): name is GuideAnchor => !!name && name !== 'topbar-guide');
    const timelineEntries = () => timedOutInvoice();
    const invoicePlan = (): SagaRetryPlan => ({
      retryable: true,
      reason: null,
      failureKind: 'TimedOut',
      failureSequenceNumber: 66,
      step: { sequenceNumber: 61, messageType: 'InvoiceIssued', messageId: 'e5', fromState: 'Requested' },
    });

    describe('the anchors', () => {
      it('marks the summary card, the Saga data group, the retry row and the two tabs, and the map on the Map tab', () => {
        const fixture = setup(makeDetail({ status: 'Failed' }));
        const el: HTMLElement = fixture.nativeElement;

        expect(anchorsIn(el)).toEqual(['detail-data', 'detail-retry', 'detail-summary', 'detail-tab-map', 'detail-tab-timeline', 'map-canvas', 'map-controls']);
        expect(el.querySelector('.summary-card')?.getAttribute('data-tour')).toBe('detail-summary');
        expect(el.querySelector('.retry-row')?.getAttribute('data-tour')).toBe('detail-retry');
        expect(el.querySelector('.ov-bar')?.getAttribute('data-tour')).toBe('detail-data');
        const tabs = Array.from(el.querySelectorAll('.subtabs button'));
        expect(tabs.map((t) => [t.textContent?.trim(), t.getAttribute('data-tour')])).toEqual([
          ['Map', 'detail-tab-map'],
          ['Timeline', 'detail-tab-timeline'],
        ]);
      });

      it('marks the timeline, its entries and its Data buttons on the Timeline tab instead of the map', () => {
        const fixture = setup(makeDetail({ status: 'TimedOut' }), timelineEntries());
        fixture.componentInstance.setTab('timeline');
        fixture.detectChanges();
        const el: HTMLElement = fixture.nativeElement;

        const anchors = anchorsIn(el);
        expect(anchors.filter((a) => a === 'timeline')).toHaveLength(1);
        expect(anchors.filter((a) => a === 'timeline-entry').length).toBe(el.querySelectorAll('.tl-row').length);
        expect(anchors.filter((a) => a === 'timeline-step-data').length).toBe(el.querySelectorAll('.tl-step').length);
        expect(anchors).not.toContain('map-canvas');
      });

      it('has no retry row anchor on a saga that is not retryable, nor for a viewer without sagas.retry', () => {
        expect(anchorsIn(setup(makeDetail({ status: 'Running' })).nativeElement)).not.toContain('detail-retry');
        TestBed.resetTestingModule();
        authOptions = { access: { permissions: ['sagas.view', 'sagas.data'], scoped: [] } };
        const el: HTMLElement = setup(makeDetail({ status: 'Failed' })).nativeElement;

        expect(el.querySelector('.retry-hint')).not.toBeNull(); // the row that says why, with nothing to explain
        expect(anchorsIn(el)).not.toContain('detail-retry');
      });

      it('uses only names of the vocabulary', () => {
        const fixture = setup(makeDetail({ status: 'TimedOut' }), timelineEntries());
        for (const tab of ['map', 'timeline'] as const) {
          fixture.componentInstance.setTab(tab);
          fixture.detectChanges();
          for (const name of anchorsIn(fixture.nativeElement)) expect(GUIDE_ANCHORS).toContain(name);
        }
      });

      it('has every element the tours of the five areas point at, on the tab the part is on', () => {
        const fixture = setup(makeDetail({ status: 'TimedOut' }), timelineEntries());
        const el: HTMLElement = fixture.nativeElement;
        const has = (name: string) => el.querySelector(`[data-tour="${name}"]`) !== null;

        const onEveryTab = named('summary', 'data', 'retry');
        expect(onEveryTab.length).toBeGreaterThan(0);
        for (const name of onEveryTab) expect(has(name), name).toBe(true);
        for (const name of named('map')) expect(has(name), name).toBe(true);

        fixture.componentInstance.setTab('timeline');
        fixture.detectChanges();
        for (const name of named('timeline')) expect(has(name), name).toBe(true);
      });

      it('has no Data buttons to point at without sagas.data, which is why the tours drop those steps', () => {
        authOptions = { access: { permissions: ['sagas.view'], scoped: [] } };
        const fixture = setup(makeDetail({ status: 'TimedOut' }), timelineEntries());
        fixture.componentInstance.setTab('timeline');
        fixture.detectChanges();

        expect(anchorsIn(fixture.nativeElement)).not.toContain('timeline-step-data');
        expect(GUIDE_TOURS.timeline.find((s) => s.anchor === 'timeline-step-data')?.requires).toBe('sagas.data');
      });

      it('names the labels the tours name: the tabs, the data group, the timeline and the retry row', () => {
        retryPlanResponse = of(invoicePlan());
        const fixture = setup(makeDetail({ status: 'TimedOut' }), timelineEntries());
        const el: HTMLElement = fixture.nativeElement;
        const label = (e: Element | null) => (e?.textContent ?? '').replace(/\s+/g, ' ').trim();

        expect(Array.from(el.querySelectorAll('.subtabs button'), label)).toEqual(['Map', 'Timeline']);
        expect(Array.from(el.querySelectorAll('.ov-bar button'), label)).toEqual(['At start', 'At end', 'Compare']);
        expect(label(el.querySelector('.retry-row button'))).toBe('Retry this saga');
        (el.querySelector('.retry-row button') as HTMLButtonElement).click();
        fixture.detectChanges();
        expect(Array.from(el.querySelectorAll('.retry-row button'), label)).toEqual(['Yes, retry', 'Cancel']);
        expect(label(el.querySelector('.retry-confirm-prompt'))).toMatch(/^Re-run step \d+ \(InvoiceIssued, Requested\) for this saga only\?$/);

        // The end button reads Current until the saga is finished.
        TestBed.resetTestingModule();
        const running: HTMLElement = setup(makeDetail({ status: 'Running' })).nativeElement;
        expect(Array.from(running.querySelectorAll('.ov-bar button'), label)).toEqual(['At start', 'Current', 'Compare']);
        TestBed.resetTestingModule();
        retryPlanResponse = of(invoicePlan());
        const again = setup(makeDetail({ status: 'TimedOut' }), timelineEntries());
        const el2: HTMLElement = again.nativeElement;

        again.componentInstance.setTab('timeline');
        again.detectChanges();
        expect(label(el2.querySelector('.tl-marker--failed'))).toBe('Failed here');
        expect(label(el2.querySelector('.tl-marker:not(.tl-marker--failed)'))).toBe('Re-run starts here');
        expect(el2.textContent).toContain('Recorded at');
        expect(label(el2.querySelector('.tl-data'))).toBe('Data');
      });
    });

    describe('what the page announces', () => {
      it('says nothing while it is being created: an announcement belongs to an effect, not to a constructor', () => {
        setup(makeDetail({ status: 'Failed' }));
        guideMock.areaShown.mockClear();

        const another = TestBed.createComponent(SagaDetail);
        expect(guideMock.areaShown).not.toHaveBeenCalled(); // created, not yet checked

        another.detectChanges();
        expect(shown()).toEqual(['summary', 'retry', 'map']); // announced from its effects
      });

      it('announces the summary, the retry row under it and the map, in that order, for a Failed saga', () => {
        setup(makeDetail({ status: 'Failed' }));

        expect(shown()).toEqual(['summary', 'retry', 'map']);
      });

      it('announces no retry row for a saga that cannot be retried, or to a viewer who may not retry', () => {
        setup(makeDetail({ status: 'Running' }));
        expect(shown()).toEqual(['summary', 'map']);

        TestBed.resetTestingModule();
        authOptions = { access: { permissions: ['sagas.view', 'sagas.data'], scoped: [] } };
        setup(makeDetail({ status: 'Failed' }));
        expect(shown()).toEqual(['summary', 'map']);
      });

      it('announces the retry row for a retry permission held for this saga type only', () => {
        authOptions = { access: { permissions: ['sagas.view'], scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.retry'] }] } };
        setup(makeDetail({ status: 'Failed' }));

        expect(shown()).toContain('retry');
      });

      it('announces nothing while the saga loads', () => {
        setup(makeDetail(), [], makeMap(), undefined, [], new Subject<ParamMap>()); // the route has not answered

        expect(guideMock.areaShown).not.toHaveBeenCalled();
      });

      it.each([
        ['a saga the API refuses the viewer', () => httpError(403, problem('forbidden', 'No access.')), 'forbidden'],
        ['a saga that could not be read', () => httpError(500, null), 'error'],
      ] as const)('announces nothing for %s', (_name, failure, flag) => {
        const paramMap$ = new Subject<ParamMap>();
        const fixture = setup(makeDetail({ status: 'Failed' }), [], makeMap(), undefined, [], paramMap$);
        apiMock.get.mockReturnValue(throwError(() => failure()));
        apiMock.getTimeline.mockReturnValue(throwError(() => failure()));
        apiMock.getMap.mockReturnValue(throwError(() => failure()));

        paramMap$.next(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' }));
        fixture.detectChanges();

        expect(flag === 'forbidden' ? fixture.componentInstance.forbidden() : fixture.componentInstance.error() !== null).toBe(true);
        expect(guideMock.areaShown).not.toHaveBeenCalled();
      });

      it.each([
        ['a read that fails', () => httpError(500, null)],
        ['a read the API refuses the viewer', () => httpError(403, problem('forbidden', 'No access.'))],
      ] as const)('announces nothing more when a later read of a saga that was shown ends in %s: the old content is behind the message', (_name, failure) => {
        const fixture = setup(makeDetail({ status: 'Failed' }));
        guideMock.areaShown.mockClear();
        apiMock.get.mockReturnValue(throwError(() => failure()));

        fixture.componentInstance.load();
        fixture.detectChanges();

        expect(fixture.componentInstance.detail()).not.toBeNull(); // still held, not shown
        expect(guideMock.areaShown).not.toHaveBeenCalled();
      });

      it('announces the parts again when the session returns after the page was hidden by its ending', () => {
        const fixture = setup(makeDetail({ status: 'Failed' }));
        auth.status.set('anonymous'); // the session is ending: the page shows nothing
        fixture.detectChanges();
        guideMock.areaShown.mockClear();

        auth.status.set('authenticated');
        fixture.detectChanges();

        expect(shown()).toEqual(['summary', 'retry', 'map']);
      });

      it('announces the map when it has arrived, not while it loads', () => {
        const paramMap$ = new Subject<ParamMap>();
        const fixture = setup(makeDetail({ status: 'Running' }), [], makeMap(), undefined, [], paramMap$);
        const pending = new Subject<SagaMapModel>();
        apiMock.getMap.mockReturnValue(pending);

        paramMap$.next(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' }));
        fixture.detectChanges();
        expect(shown()).toEqual(['summary']);

        pending.next(makeMap());
        pending.complete();
        fixture.detectChanges();
        expect(shown()).toEqual(['summary', 'map']);
      });

      it('does not announce the data for an inspector that is open on the other tab', () => {
        const fixture = setup(makeDetail({ status: 'Running' }), timelineEntries());
        guideMock.areaShown.mockClear();

        fixture.componentInstance.openKeys.set(new Set([60])); // opened on the Timeline tab earlier; the Map tab shows now
        fixture.detectChanges();

        expect(fixture.componentInstance.tab()).toBe('map');
        expect(shown()).not.toContain('data');
      });

      it('announces the timeline when the Timeline tab is shown with entries, once, and the map again when the Map tab returns', () => {
        const fixture = setup(makeDetail({ status: 'Running' }), timelineEntries());
        expect(shown()).toEqual(['summary', 'map']);

        fixture.componentInstance.setTab('timeline');
        fixture.detectChanges();
        expect(shown()).toEqual(['summary', 'map', 'timeline']);

        fixture.componentInstance.setTab('map');
        fixture.detectChanges();
        expect(shown()).toEqual(['summary', 'map', 'timeline', 'map']); // the guide knows it was explained
      });

      it('does not announce the Timeline tab while there is nothing on it yet', () => {
        const fixture = setup(makeDetail({ status: 'Running' }), []);

        fixture.componentInstance.setTab('timeline');
        fixture.detectChanges();

        expect(shown()).not.toContain('timeline');
      });

      it('does not announce the map again for a live refresh', () => {
        vi.useFakeTimers();
        const fixture = setup(makeDetail({ status: 'Running' }));
        guideMock.areaShown.mockClear();

        hubMock.sagaUpdated$.next({ ...makeDetail({ status: 'Running' }).summary, version: 3 });
        vi.advanceTimersByTime(REFRESH_AUDIT_MS);
        fixture.detectChanges();

        expect(guideMock.areaShown).not.toHaveBeenCalled();
      });

      it('announces the data when a Saga data view is opened, and again when it is reopened', () => {
        const fixture = setup(makeDetail({ status: 'Running' }));
        guideMock.areaShown.mockClear();

        fixture.componentInstance.setDataView('start');
        fixture.detectChanges();
        expect(shown()).toEqual(['data']);

        fixture.componentInstance.setDataView('end'); // another view of the same bar
        fixture.detectChanges();
        expect(shown()).toEqual(['data']);

        fixture.componentInstance.setDataView(null);
        fixture.detectChanges();
        fixture.componentInstance.setDataView('compare');
        fixture.detectChanges();
        expect(shown()).toEqual(['data', 'data']);
      });

      it('announces the data when a step inspector is opened on the Timeline tab', () => {
        const fixture = setup(makeDetail({ status: 'Running' }), timelineEntries());
        fixture.componentInstance.setTab('timeline');
        fixture.detectChanges();
        guideMock.areaShown.mockClear();

        (fixture.nativeElement.querySelector('.tl-data') as HTMLButtonElement).click();
        fixture.detectChanges();

        expect(shown()).toEqual(['data']);
      });

      it('does not announce the data to someone without sagas.data, who has no data to open', () => {
        authOptions = { access: { permissions: ['sagas.view'], scoped: [] } };
        const fixture = setup(makeDetail({ status: 'Running' }), timelineEntries());

        fixture.componentInstance.setDataView('start');
        fixture.componentInstance.openKeys.set(new Set([1]));
        fixture.componentInstance.setTab('timeline');
        fixture.detectChanges();

        expect(shown()).not.toContain('data');
      });

      it('announces the parts again for the next saga when the route reuses the instance', () => {
        const paramMap$ = new Subject<ParamMap>();
        const fixture = setup(makeDetail({ status: 'Failed' }), [], makeMap(), [makeDetail().summary], [], paramMap$);
        paramMap$.next(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' }));
        fixture.detectChanges();
        const first = shown().length;
        expect(shown()).toEqual(['summary', 'retry', 'map']);

        const next = new Subject<SagaDetailModel>();
        apiMock.get.mockReturnValue(next);
        paramMap$.next(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-2' }));
        fixture.detectChanges(); // the next saga is loading: nothing of the last one is on screen
        next.next(makeDetail({ correlationId: 'saga-2', status: 'Failed' }));
        next.complete();
        fixture.detectChanges();

        expect(shown().slice(first)).toEqual(['summary', 'retry', 'map']);
      });
    });
  });
});
