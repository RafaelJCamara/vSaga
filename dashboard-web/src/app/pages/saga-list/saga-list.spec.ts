import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, Subject, of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubConnectionState, SagaHubService } from '../../services/saga-hub.service';
import { PagedResult, SagaSummary } from '../../models/saga.model';
import { SagaList } from './saga-list';

function makeSummary(overrides: Partial<SagaSummary> = {}): SagaSummary {
  return {
    correlationId: 'id-1',
    sagaType: 'OrderSaga',
    kind: 'Orchestrated',
    currentState: 'Completed',
    status: 'Completed',
    createdAtUtc: '2026-01-01T00:00:00Z',
    updatedAtUtc: '2026-01-01T00:00:00Z',
    version: 1,
    parentSagaType: null,
    parentCorrelationId: null,
    ...overrides,
  };
}

describe('SagaList', () => {
  let apiMock: { list: ReturnType<typeof vi.fn>; getSagaTypes: ReturnType<typeof vi.fn> };
  let hubMock: {
    sagaUpdated$: Subject<SagaSummary>;
    connectionState$: BehaviorSubject<SagaHubConnectionState>;
    subscribeToList: ReturnType<typeof vi.fn>;
  };

  function setup(listResult: PagedResult<SagaSummary> = { items: [], page: 1, pageSize: 25, totalCount: 0 }) {
    apiMock = {
      list: vi.fn().mockReturnValue(of(listResult)),
      getSagaTypes: vi.fn().mockReturnValue(of([])),
    };
    hubMock = {
      sagaUpdated$: new Subject<SagaSummary>(),
      connectionState$: new BehaviorSubject<SagaHubConnectionState>('connected'),
      subscribeToList: vi.fn().mockResolvedValue(undefined),
    };

    TestBed.configureTestingModule({
      imports: [SagaList],
      providers: [
        provideRouter([]),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
      ],
    });

    const fixture = TestBed.createComponent(SagaList);
    fixture.detectChanges();
    return fixture;
  }

  it('loads saga types and the saga list on init, and subscribes to live list updates', () => {
    const summary = makeSummary();
    const fixture = setup({ items: [summary], page: 1, pageSize: 25, totalCount: 1 });

    expect(apiMock.getSagaTypes).toHaveBeenCalled();
    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 25 }));
    expect(fixture.componentInstance.sagas()).toEqual([summary]);
    expect(fixture.componentInstance.totalCount()).toBe(1);
    expect(hubMock.subscribeToList).toHaveBeenCalled();
  });

  it('shows an error banner when the list request fails', () => {
    apiMock = {
      list: vi.fn().mockReturnValue(throwError(() => new Error('network down'))),
      getSagaTypes: vi.fn().mockReturnValue(of([])),
    };
    hubMock = {
      sagaUpdated$: new Subject(),
      connectionState$: new BehaviorSubject<SagaHubConnectionState>('connected'),
      subscribeToList: vi.fn().mockResolvedValue(undefined),
    };

    TestBed.configureTestingModule({
      imports: [SagaList],
      providers: [
        provideRouter([]),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
      ],
    });
    const fixture = TestBed.createComponent(SagaList);
    fixture.detectChanges();

    expect(fixture.componentInstance.error()).toContain('Could not reach');
    expect(fixture.componentInstance.loading()).toBe(false);
    expect(fixture.nativeElement.querySelector('.banner--error')?.textContent).toContain('Could not reach');
  });

  // The error banner already explains why the table is empty; showing "No sagas match these
  // filters yet." underneath it as well reads as contradictory (one implies zero genuine matches,
  // the other implies the query couldn't even run).
  it('does not show the "no sagas match" empty state alongside the error banner', () => {
    apiMock = {
      list: vi.fn().mockReturnValue(throwError(() => new Error('network down'))),
      getSagaTypes: vi.fn().mockReturnValue(of([])),
    };
    hubMock = {
      sagaUpdated$: new Subject(),
      connectionState$: new BehaviorSubject<SagaHubConnectionState>('connected'),
      subscribeToList: vi.fn().mockResolvedValue(undefined),
    };

    TestBed.configureTestingModule({
      imports: [SagaList],
      providers: [
        provideRouter([]),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
      ],
    });
    const fixture = TestBed.createComponent(SagaList);
    fixture.detectChanges();

    expect(fixture.componentInstance.error()).toContain('Could not reach');
    expect(fixture.componentInstance.sagas()).toEqual([]);
    expect(fixture.nativeElement.querySelector('.banner--error')).toBeTruthy();
    expect(fixture.nativeElement.textContent).not.toContain('No sagas match these filters yet.');
  });

  // A failed initial REST load leaves the error banner up even after the SignalR hub itself
  // recovers -- reconnecting only proves the push channel is back, not that the failed GET has been
  // retried. Left unfixed, the list silently understates its rows (only what trickled in via live
  // push since reconnecting) while the stale "Could not reach..." banner keeps showing on top.
  it('re-runs the failed initial load and clears the error banner when the hub reconnects after a prior failure', () => {
    const summary = makeSummary();
    apiMock = {
      list: vi
        .fn()
        .mockReturnValueOnce(throwError(() => new Error('network down')))
        .mockReturnValueOnce(of({ items: [summary], page: 1, pageSize: 25, totalCount: 1 })),
      getSagaTypes: vi.fn().mockReturnValue(of([])),
    };
    hubMock = {
      sagaUpdated$: new Subject<SagaSummary>(),
      connectionState$: new BehaviorSubject<SagaHubConnectionState>('disconnected'),
      subscribeToList: vi.fn().mockResolvedValue(undefined),
    };
    TestBed.configureTestingModule({
      imports: [SagaList],
      providers: [
        provideRouter([]),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
      ],
    });
    const fixture = TestBed.createComponent(SagaList);
    fixture.detectChanges();

    expect(fixture.componentInstance.error()).toContain('Could not reach');
    expect(apiMock.list).toHaveBeenCalledTimes(1);

    hubMock.connectionState$.next('connected');

    expect(apiMock.list).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.error()).toBeNull();
    expect(fixture.componentInstance.sagas()).toEqual([summary]);
  });

  // Mirrors the guard hasEverConnected already uses: only a reconnect that follows an actual failure
  // should force an extra refetch. A first-ever connect (or an ordinary reconnect blip that never
  // surfaced an error) must not double up on the refresh() ngOnInit already fired.
  it('does not trigger a redundant refresh on an ordinary connect/reconnect when there was no prior error', () => {
    const fixture = setup({ items: [], page: 1, pageSize: 25, totalCount: 0 });

    expect(apiMock.list).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.error()).toBeNull();

    hubMock.connectionState$.next('reconnecting');
    hubMock.connectionState$.next('connected');

    expect(apiMock.list).toHaveBeenCalledTimes(1);
  });

  it('refresh() re-queries the API with the current filter values', () => {
    const fixture = setup();
    fixture.componentInstance.status = 'Failed';
    fixture.componentInstance.sagaType = 'OrderSaga';

    fixture.componentInstance.refresh();

    expect(apiMock.list).toHaveBeenLastCalledWith(expect.objectContaining({ status: 'Failed', sagaType: 'OrderSaga' }));
  });

  it('changing the status filter dropdown triggers a refresh with the new value', () => {
    const fixture = setup();
    apiMock.list.mockClear();

    const select: HTMLSelectElement = fixture.nativeElement.querySelectorAll('select')[0];
    select.value = 'Failed';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ status: 'Failed' }));
  });

  it('upserts an existing saga in place on a live update, leaving totalCount unchanged', () => {
    const original = makeSummary({ status: 'Running' });
    const fixture = setup({ items: [original], page: 1, pageSize: 25, totalCount: 1 });

    const updated = { ...original, status: 'Completed' as const };
    hubMock.sagaUpdated$.next(updated);

    expect(fixture.componentInstance.sagas()).toEqual([updated]);
    expect(fixture.componentInstance.totalCount()).toBe(1);
  });

  it('prepends a new saga on a live update when it matches the active filter', () => {
    const fixture = setup({ items: [], page: 1, pageSize: 25, totalCount: 0 });
    fixture.componentInstance.status = 'Failed';

    const incoming = makeSummary({ correlationId: 'new-1', status: 'Failed' });
    hubMock.sagaUpdated$.next(incoming);

    expect(fixture.componentInstance.sagas()).toEqual([incoming]);
    expect(fixture.componentInstance.totalCount()).toBe(1);
  });

  it('ignores a new saga on a live update when it does not match the active filter', () => {
    const fixture = setup({ items: [], page: 1, pageSize: 25, totalCount: 0 });
    fixture.componentInstance.status = 'Failed';

    const incoming = makeSummary({ correlationId: 'new-1', status: 'Running' });
    hubMock.sagaUpdated$.next(incoming);

    expect(fixture.componentInstance.sagas()).toEqual([]);
    expect(fixture.componentInstance.totalCount()).toBe(0);
  });

  it('ignores a new saga on a live update when it does not match the active search term', () => {
    const fixture = setup({ items: [], page: 1, pageSize: 25, totalCount: 0 });
    fixture.componentInstance.search = 'checkout';

    const incoming = makeSummary({ correlationId: 'new-1', sagaType: 'ShippingSaga' });
    hubMock.sagaUpdated$.next(incoming);

    expect(fixture.componentInstance.sagas()).toEqual([]);
    expect(fixture.componentInstance.totalCount()).toBe(0);
  });

  it('prepends a new saga on a live update when it matches the active search term by correlation id', () => {
    const fixture = setup({ items: [], page: 1, pageSize: 25, totalCount: 0 });
    fixture.componentInstance.search = 'NEW-1';

    const incoming = makeSummary({ correlationId: 'new-1-abc', sagaType: 'ShippingSaga' });
    hubMock.sagaUpdated$.next(incoming);

    expect(fixture.componentInstance.sagas()).toEqual([incoming]);
    expect(fixture.componentInstance.totalCount()).toBe(1);
  });

  it('prepends a new saga on a live update when it matches the active search term by saga type', () => {
    const fixture = setup({ items: [], page: 1, pageSize: 25, totalCount: 0 });
    fixture.componentInstance.search = 'shipping';

    const incoming = makeSummary({ correlationId: 'new-1', sagaType: 'ShippingSaga' });
    hubMock.sagaUpdated$.next(incoming);

    expect(fixture.componentInstance.sagas()).toEqual([incoming]);
    expect(fixture.componentInstance.totalCount()).toBe(1);
  });

  it('caps the rendered page-1 array at pageSize under a burst of live inserts, while totalCount keeps counting every one', () => {
    const fixture = setup({ items: [], page: 1, pageSize: 25, totalCount: 0 });

    for (let i = 0; i < 30; i++) {
      hubMock.sagaUpdated$.next(
        makeSummary({
          correlationId: `new-${i}`,
          updatedAtUtc: `2026-01-01T00:00:${String(i).padStart(2, '0')}Z`,
        }),
      );
    }

    expect(fixture.componentInstance.sagas().length).toBe(25);
    expect(fixture.componentInstance.totalCount()).toBe(30);
  });

  it('nextPage() requests the next page and disables itself once there is nothing further to load', () => {
    const fixture = setup({ items: [makeSummary()], page: 1, pageSize: 25, totalCount: 30 });
    apiMock.list.mockClear();

    fixture.componentInstance.nextPage();

    expect(fixture.componentInstance.page()).toBe(2);
    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ page: 2, pageSize: 25 }));
    expect(fixture.componentInstance.hasNextPage()).toBe(false);

    apiMock.list.mockClear();
    fixture.componentInstance.nextPage();

    expect(fixture.componentInstance.page()).toBe(2);
    expect(apiMock.list).not.toHaveBeenCalled();
  });

  it('prevPage() is a no-op on page 1 and moves back a page otherwise', () => {
    const fixture = setup({ items: [makeSummary()], page: 1, pageSize: 25, totalCount: 75 });

    fixture.componentInstance.prevPage();
    expect(fixture.componentInstance.page()).toBe(1);

    fixture.componentInstance.nextPage();
    apiMock.list.mockClear();
    fixture.componentInstance.prevPage();

    expect(fixture.componentInstance.page()).toBe(1);
    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ page: 1 }));
  });

  it('disables the Previous/Next pagination buttons appropriately', () => {
    const fixture = setup({ items: [makeSummary()], page: 1, pageSize: 25, totalCount: 30 });
    fixture.detectChanges();

    const [prevBtn, nextBtn] = Array.from(fixture.nativeElement.querySelectorAll('.pagination button')) as HTMLButtonElement[];
    expect(prevBtn.disabled).toBe(true);
    expect(nextBtn.disabled).toBe(false);

    fixture.componentInstance.nextPage();
    fixture.detectChanges();

    expect(prevBtn.disabled).toBe(false);
    expect(nextBtn.disabled).toBe(true);
  });

  it('computes totalPages from totalCount and pageSize, and renders it in the pagination area', () => {
    const fixture = setup({ items: [makeSummary()], page: 1, pageSize: 25, totalCount: 75 });
    fixture.detectChanges();

    expect(fixture.componentInstance.totalPages()).toBe(3);

    const pagination: HTMLElement = fixture.nativeElement.querySelector('.pagination');
    expect(pagination.textContent).toContain('3');
  });

  it('changing a filter resets back to page 1', () => {
    const fixture = setup({ items: [makeSummary()], page: 1, pageSize: 25, totalCount: 75 });
    fixture.componentInstance.nextPage();
    expect(fixture.componentInstance.page()).toBe(2);

    apiMock.list.mockClear();
    fixture.componentInstance.status = 'Failed';
    fixture.componentInstance.onFilterChange();

    expect(fixture.componentInstance.page()).toBe(1);
    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ page: 1, status: 'Failed' }));
  });

  it('does not prepend a live update while off page 1, but tracks it as available and bumps totalCount', () => {
    const fixture = setup({ items: [makeSummary()], page: 1, pageSize: 25, totalCount: 75 });
    fixture.componentInstance.nextPage();

    const sagasBefore = fixture.componentInstance.sagas();
    const incoming = makeSummary({ correlationId: 'new-1' });
    hubMock.sagaUpdated$.next(incoming);

    expect(fixture.componentInstance.sagas()).toBe(sagasBefore);
    expect(fixture.componentInstance.totalCount()).toBe(76);
    expect(fixture.componentInstance.newSagasAvailable()).toBe(1);
  });

  it('clears the "new sagas available" count on refresh', () => {
    const fixture = setup({ items: [makeSummary()], page: 1, pageSize: 25, totalCount: 75 });
    fixture.componentInstance.nextPage();
    hubMock.sagaUpdated$.next(makeSummary({ correlationId: 'new-1' }));
    expect(fixture.componentInstance.newSagasAvailable()).toBe(1);

    fixture.componentInstance.refresh();

    expect(fixture.componentInstance.newSagasAvailable()).toBe(0);
  });

  it('defaults to a page size of 25 and offers 25/50/75/100 as options', () => {
    const fixture = setup();

    expect(fixture.componentInstance.pageSize).toBe(25);
    expect(fixture.componentInstance.pageSizes).toEqual([25, 50, 75, 100]);
  });

  it('changing the page size resets to page 1 and requests the new size', () => {
    const fixture = setup({ items: [makeSummary()], page: 1, pageSize: 25, totalCount: 75 });
    fixture.componentInstance.nextPage();
    expect(fixture.componentInstance.page()).toBe(2);

    apiMock.list.mockClear();
    fixture.componentInstance.pageSize = 100;
    fixture.componentInstance.onFilterChange();

    expect(fixture.componentInstance.page()).toBe(1);
    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 100 }));
  });

  it('selecting a page size in the dropdown triggers the same reset-and-refetch', () => {
    const fixture = setup({ items: [makeSummary()], page: 1, pageSize: 25, totalCount: 75 });
    apiMock.list.mockClear();

    const pageSizeSelect: HTMLSelectElement = fixture.nativeElement.querySelector('.page-size select');
    pageSizeSelect.selectedIndex = 3; // pageSizes = [25, 50, 75, 100] -> 100
    pageSizeSelect.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 100 }));
  });

  // Sorting is applied server-side (see SagaEndpointsTests for the "spans the whole result set, not
  // just the current page" coverage) — these tests only cover the client's request-building and its
  // toggle/direction state, plus the small bit of client-side ordering that still matters: keeping a
  // live-patched page in step with whatever sort is active between refetches.

  it('toggleSort() requests the sort from the server and resets to page 1', () => {
    const fixture = setup({ items: [makeSummary()], page: 1, pageSize: 25, totalCount: 75 });
    fixture.componentInstance.nextPage();
    expect(fixture.componentInstance.page()).toBe(2);

    apiMock.list.mockClear();
    fixture.componentInstance.toggleSort('Status');

    expect(fixture.componentInstance.sortColumn()).toBe('Status');
    expect(fixture.componentInstance.sortDirection()).toBe('asc');
    expect(fixture.componentInstance.page()).toBe(1);
    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ page: 1, sortBy: 'Status', sortDescending: false }));
  });

  it('toggling the same column again reverses direction; a different column resets to ascending', () => {
    const fixture = setup();

    fixture.componentInstance.toggleSort('UpdatedAt');
    apiMock.list.mockClear();
    fixture.componentInstance.toggleSort('UpdatedAt');

    expect(fixture.componentInstance.sortDirection()).toBe('desc');
    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ sortBy: 'UpdatedAt', sortDescending: true }));

    apiMock.list.mockClear();
    fixture.componentInstance.toggleSort('Status');

    expect(fixture.componentInstance.sortColumn()).toBe('Status');
    expect(fixture.componentInstance.sortDirection()).toBe('asc');
    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ sortBy: 'Status', sortDescending: false }));
  });

  it('does not send sortBy/sortDescending when no column is active', () => {
    const fixture = setup();
    apiMock.list.mockClear();

    fixture.componentInstance.refresh();

    const call = apiMock.list.mock.calls.at(-1)![0];
    expect(call.sortBy).toBeUndefined();
    expect(call.sortDescending).toBeUndefined();
  });

  it('clicking the Status column header requests a sorted refetch and shows the direction indicator', () => {
    const fixture = setup({ items: [makeSummary()], page: 1, pageSize: 25, totalCount: 1 });
    fixture.detectChanges();
    apiMock.list.mockClear();

    const headers: HTMLElement[] = Array.from(fixture.nativeElement.querySelectorAll('th.sortable'));
    const statusHeader = headers.find((th) => th.textContent?.includes('Status'))!;
    statusHeader.click();
    fixture.detectChanges();

    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ sortBy: 'Status', sortDescending: false, page: 1 }));
    expect(statusHeader.querySelector('.sort-indicator')?.textContent).toContain('▲');
  });

  it('an in-place live update re-sorts the page to match the active sort', () => {
    const completed = makeSummary({ correlationId: 'b', status: 'Completed' });
    const running = makeSummary({ correlationId: 'a', status: 'Running' });
    // Seeded in an order that's "wrong" for an ascending Status sort, so a subsequent live patch
    // sorting it into ['a', 'b'] proves the client is doing real reordering, not an accidental pass.
    const fixture = setup({ items: [completed, running], page: 1, pageSize: 25, totalCount: 2 });
    fixture.componentInstance.toggleSort('Status');
    expect(fixture.componentInstance.sagas().map((s) => s.correlationId)).toEqual(['b', 'a']);

    hubMock.sagaUpdated$.next({ ...running, currentState: 'Updated' });

    expect(fixture.componentInstance.sagas().map((s) => s.correlationId)).toEqual(['a', 'b']);
  });

  it('inserts a new live saga into the correct sorted position instead of always prepending', () => {
    const running = makeSummary({ correlationId: 'a', status: 'Running' });
    const failed = makeSummary({ correlationId: 'c', status: 'Failed' });
    const fixture = setup({ items: [running, failed], page: 1, pageSize: 25, totalCount: 2 });
    fixture.componentInstance.toggleSort('Status');

    const completed = makeSummary({ correlationId: 'b', status: 'Completed' });
    hubMock.sagaUpdated$.next(completed);

    expect(fixture.componentInstance.sagas().map((s) => s.correlationId)).toEqual(['a', 'b', 'c']);
  });

  // A shared/bookmarked link's page number can be stale (the result set shrank since). Left
  // uncorrected, this strands the user on an empty page reading e.g. "Page 40 of 3" with no easy way
  // back to page 1.
  it('clamps to the last valid page when the current page no longer exists', () => {
    apiMock = {
      list: vi.fn().mockReturnValue(of({ items: [], page: 40, pageSize: 25, totalCount: 60 })),
      getSagaTypes: vi.fn().mockReturnValue(of([])),
    };
    hubMock = {
      sagaUpdated$: new Subject<SagaSummary>(),
      connectionState$: new BehaviorSubject<SagaHubConnectionState>('connected'),
      subscribeToList: vi.fn().mockResolvedValue(undefined),
    };
    TestBed.configureTestingModule({
      imports: [SagaList],
      providers: [
        provideRouter([]),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
      ],
    });
    const fixture = TestBed.createComponent(SagaList);
    fixture.componentInstance.page.set(40);
    fixture.detectChanges();

    // totalCount=60 at pageSize=25 -> 3 pages; page 40 is out of range and should self-correct.
    expect(fixture.componentInstance.page()).toBe(3);
    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ page: 3 }));
  });

  it('reads initial filters, page, and sort from the URL query params on load', () => {
    apiMock = {
      // totalCount=100 at pageSize=50 -> exactly 2 pages, so the URL's page=2 is valid and the
      // page-clamp fix (see the "clamps to the last valid page" test) does not kick in here.
      list: vi.fn().mockReturnValue(of({ items: [], page: 2, pageSize: 50, totalCount: 100 })),
      getSagaTypes: vi.fn().mockReturnValue(of([])),
    };
    hubMock = {
      sagaUpdated$: new Subject<SagaSummary>(),
      connectionState$: new BehaviorSubject<SagaHubConnectionState>('connected'),
      subscribeToList: vi.fn().mockResolvedValue(undefined),
    };
    TestBed.configureTestingModule({
      imports: [SagaList],
      providers: [
        provideRouter([]),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              queryParamMap: convertToParamMap({
                status: 'Failed',
                sagaType: 'OrderSaga',
                search: 'abc',
                page: '2',
                pageSize: '50',
                sortBy: 'Status',
                sortDescending: 'true',
              }),
            },
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(SagaList);
    fixture.detectChanges();

    const c = fixture.componentInstance;
    expect(c.status).toBe('Failed');
    expect(c.sagaType).toBe('OrderSaga');
    expect(c.search).toBe('abc');
    expect(c.page()).toBe(2);
    expect(c.pageSize).toBe(50);
    expect(c.sortColumn()).toBe('Status');
    expect(c.sortDirection()).toBe('desc');
    expect(apiMock.list).toHaveBeenCalledWith(
      expect.objectContaining({ status: 'Failed', sagaType: 'OrderSaga', search: 'abc', page: 2, pageSize: 50 }),
    );
  });

  it('ignores an unknown status/kind value from the URL rather than trusting it verbatim', () => {
    apiMock = {
      list: vi.fn().mockReturnValue(of({ items: [], page: 1, pageSize: 25, totalCount: 0 })),
      getSagaTypes: vi.fn().mockReturnValue(of([])),
    };
    hubMock = {
      sagaUpdated$: new Subject<SagaSummary>(),
      connectionState$: new BehaviorSubject<SagaHubConnectionState>('connected'),
      subscribeToList: vi.fn().mockResolvedValue(undefined),
    };
    TestBed.configureTestingModule({
      imports: [SagaList],
      providers: [
        provideRouter([]),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap({ status: 'NotARealStatus' }) } },
        },
      ],
    });
    const fixture = TestBed.createComponent(SagaList);
    fixture.detectChanges();

    expect(fixture.componentInstance.status).toBe('');
  });

  it('writes the active filters back to the URL as query params after a filter change', () => {
    const fixture = setup();
    const router = TestBed.inject(Router);
    const navigateSpy = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    fixture.componentInstance.status = 'Failed';
    fixture.componentInstance.onFilterChange();

    expect(navigateSpy).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParamsHandling: '',
        queryParams: expect.objectContaining({ status: 'Failed', page: null }),
      }),
    );
  });
});
