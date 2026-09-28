import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, ParamMap, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, Observable, Subject, of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubConnectionState, SagaHubService } from '../../services/saga-hub.service';
import { SagaDetail as SagaDetailModel, SagaLogEntry, SagaMap as SagaMapModel, SagaStatus, SagaSummary } from '../../models/saga.model';
import { SagaDetail } from './saga-detail';

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
  ) {
    apiMock = {
      get: vi.fn().mockReturnValue(of(detail)),
      getTimeline: vi.fn().mockReturnValue(of(timeline)),
      getMap: vi.fn().mockReturnValue(of(map)),
      retry: vi.fn(),
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

    TestBed.configureTestingModule({
      imports: [SagaDetail],
      providers: [
        provideRouter([]),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: paramMap$,
          },
        },
      ],
    });

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
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
        { provide: ActivatedRoute, useValue: { paramMap: paramMap$ } },
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
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ sagaType: 'OrderSaga', id: 'saga-1' })) } },
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

  // Mirrors the guard hasEverConnected already uses: only a reconnect that follows an actual failure
  // should force an extra reload. A first-ever connect (or an ordinary reconnect blip that never
  // surfaced an error) must not double up on the load() ngOnInit already fired.
  it('does not trigger a redundant load on an ordinary connect/reconnect when there was no prior error', () => {
    const fixture = setup();

    expect(apiMock.get).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.error()).toBeNull();

    hubMock.connectionState$.next('reconnecting');
    hubMock.connectionState$.next('connected');

    expect(apiMock.get).toHaveBeenCalledTimes(1);
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
    apiMock.retry.mockReturnValue(throwError(() => ({ error: { error: 'Saga cannot be retried' } })));

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

  it('leaves every other entry type unchanged', () => {
    const fixture = setup(makeDetail());
    expect(fixture.componentInstance.entryTypeLabel('StepFailed')).toBe('StepFailed');
    expect(fixture.componentInstance.entryTypeLabel('MessageReceived')).toBe('MessageReceived');
  });

  it('prettyDataJson pretty-prints valid JSON', () => {
    const fixture = setup(makeDetail());
    fixture.componentInstance.detail.set({ ...fixture.componentInstance.detail()!, dataJson: '{"a":1}' });

    expect(fixture.componentInstance.prettyDataJson).toBe(JSON.stringify({ a: 1 }, null, 2));
  });

  it('prettyDataJson returns the raw string when the JSON is invalid', () => {
    const fixture = setup(makeDetail());
    fixture.componentInstance.detail.set({ ...fixture.componentInstance.detail()!, dataJson: 'not json' });

    expect(fixture.componentInstance.prettyDataJson).toBe('not json');
  });

  it('prettyDataJson returns an empty string when there is no data', () => {
    const fixture = setup(makeDetail());
    expect(fixture.componentInstance.prettyDataJson).toBe('');
  });

  it('setTab switches the active tab', () => {
    const fixture = setup();
    expect(fixture.componentInstance.tab()).toBe('map'); // Map is the default tab

    fixture.componentInstance.setTab('data');

    expect(fixture.componentInstance.tab()).toBe('data');
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
    const fixture = setup();
    expect(apiMock.getChildren).toHaveBeenCalledTimes(1);

    hubMock.sagaUpdated$.next(fixture.componentInstance.detail()!.summary);

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
    const fixture = setup();
    const entry = makeEntry({ sequenceNumber: 2, sagaType: 'ShippingChoreography' });

    hubMock.timelineEntryAdded$.next({ sagaType: 'ShippingChoreography', correlationId: 'saga-1', entry });

    expect(fixture.componentInstance.timeline()).toEqual([]);
  });

  it('appends a live timeline entry when the correlation id matches', () => {
    const fixture = setup();
    const entry = makeEntry({ sequenceNumber: 2, entryType: 'StepSucceeded' });

    hubMock.timelineEntryAdded$.next({ sagaType: 'OrderSaga', correlationId: 'saga-1', entry });

    expect(fixture.componentInstance.timeline()).toContainEqual(entry);
  });

  it('ignores a live timeline entry for a different correlation id', () => {
    const fixture = setup();
    const entry = makeEntry({ sequenceNumber: 2 });

    hubMock.timelineEntryAdded$.next({ sagaType: 'OrderSaga', correlationId: 'other-id', entry });

    expect(fixture.componentInstance.timeline()).toEqual([]);
  });

  it('loads the map alongside the timeline', () => {
    const map = makeMap();
    const fixture = setup(makeDetail(), [], map);

    expect(apiMock.getMap).toHaveBeenCalledWith('OrderSaga', 'saga-1');
    expect(fixture.componentInstance.map()).toEqual(map);
  });

  it('re-fetches the map (not incrementally, via a whole re-fetch) when a live saga update arrives', () => {
    const fixture = setup();
    expect(apiMock.getMap).toHaveBeenCalledTimes(1);

    hubMock.sagaUpdated$.next(fixture.componentInstance.detail()!.summary);

    expect(apiMock.getMap).toHaveBeenCalledTimes(2);
  });

  // SignalR only ever pushes a summary-level SagaUpdated, never an incremental timeline diff, across
  // processes — so after a manual retry the Timeline tab must be re-fetched whole, the same as the map.
  it('re-fetches the timeline (not incrementally, via a whole re-fetch) when a live saga update arrives', () => {
    const fixture = setup();
    expect(apiMock.getTimeline).toHaveBeenCalledTimes(1);

    hubMock.sagaUpdated$.next(fixture.componentInstance.detail()!.summary);

    expect(apiMock.getTimeline).toHaveBeenCalledTimes(2);
  });

  it('does not re-fetch the map for a live saga update on a different correlation id', () => {
    const fixture = setup();
    const other: SagaSummary = { ...fixture.componentInstance.detail()!.summary, correlationId: 'other-id' };

    hubMock.sagaUpdated$.next(other);

    expect(apiMock.getMap).toHaveBeenCalledTimes(1);
  });

  it('switching to the Map tab renders the saga-map component once the map has loaded', () => {
    const fixture = setup();
    fixture.componentInstance.setTab('map');
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('app-saga-map')).not.toBeNull();
  });
});
