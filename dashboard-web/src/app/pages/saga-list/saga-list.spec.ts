import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, Observable, Subject, of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubConnectionState, SagaHubService } from '../../services/saga-hub.service';
import { PagedResult, SagaSummary } from '../../models/saga.model';
import { AuthMock, AuthMockOptions, createAuthMock, provideAuthMock } from '../../testing/auth-mock';
import { httpError, problem } from '../../testing/http-error';
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
        provideAuthMock(),
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
        provideAuthMock(),
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
        provideAuthMock(),
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
        provideAuthMock(),
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

  // The first connect adds nothing to the load ngOnInit fired. A later reconnect re-reads the list: the
  // pushes sent while the hub was down are lost, and the server closes a user's connection whenever their
  // access changes, so the reconnect is how a narrowed or widened list is noticed. (This replaces the
  // test that pinned "no refresh on an ordinary reconnect", which is exactly the behaviour that missed it.)
  it('adds no request on the first connect, and reads the list again on a later reconnect', () => {
    const fixture = setup({ items: [], page: 1, pageSize: 25, totalCount: 0 });

    expect(apiMock.list).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.error()).toBeNull();

    hubMock.connectionState$.next('reconnecting');
    expect(apiMock.list).toHaveBeenCalledTimes(1);
    hubMock.connectionState$.next('connected');

    expect(apiMock.list).toHaveBeenCalledTimes(2);
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

  // The page's toolbar, controls, table and banners are the shared classes of src/styles.scss, which
  // the unit tests do not load, so the class names are all there is to check. The base `.banner`
  // carries the box: a modifier on its own would show a banner with no padding.
  it('builds its header, toolbar, form controls, table and banners from the shared global classes', () => {
    const fixture = setup({ items: [makeSummary()], page: 1, pageSize: 25, totalCount: 75 });
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('.page-header > h1')).not.toBeNull();
    expect(el.querySelectorAll('.toolbar > select.input, .toolbar > input.input')).toHaveLength(4);
    expect(el.querySelector('table.data-table')).not.toBeNull();
    expect(el.querySelectorAll('.pagination select.input, .pagination input.input')).toHaveLength(2);

    hubMock.connectionState$.next('reconnecting');
    fixture.componentInstance.error.set('Could not reach the API.');
    fixture.componentInstance.newSagasAvailable.set(2);
    fixture.detectChanges();

    expect(Array.from(el.querySelectorAll('[class*="banner"]'), (b) => b.className)).toEqual([
      'banner banner--warning',
      'banner banner--error',
      'banner banner--info',
    ]);

    fixture.componentInstance.sagas.set([]);
    fixture.componentInstance.error.set(null);
    fixture.detectChanges();
    expect(el.querySelector('.empty')?.textContent).toBe('No sagas match these filters yet.');
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
        provideAuthMock(),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
      ],
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(SagaList);
    fixture.componentInstance.page.set(40);
    fixture.detectChanges();

    // totalCount=60 at pageSize=25 -> 3 pages; page 40 is out of range and should self-correct.
    expect(fixture.componentInstance.page()).toBe(3);
    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ page: 3 }));
    // The correction replaces the URL's page 40, so Back does not land on a page that does not exist.
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({ queryParams: expect.objectContaining({ page: 3 }), replaceUrl: true }));
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
        provideAuthMock(),
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
        provideAuthMock(),
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

  // What the session lets the viewer list: the API enforces it, and the page follows the session so it
  // neither asks for what it would refuse nor words a refusal as an outage.
  describe('access by permission', () => {
    const none = { permissions: [], scoped: [] };
    const scopedView = { permissions: [], scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.view'] }] };
    const page = (items: SagaSummary[], pageNumber: number, totalCount: number): PagedResult<SagaSummary> => ({
      items,
      page: pageNumber,
      pageSize: 25,
      totalCount,
    });
    const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();
    const noAccessText = 'Your account has no access to any saga type yet. Ask an administrator for sagas.view.';

    let auth: AuthMock;

    /** The page with the given session and the answers `list` gives, in order (the last one repeats). */
    function open(
      options: AuthMockOptions,
      answers: Array<Observable<PagedResult<SagaSummary>>> = [of(page([], 1, 0))],
      startPage = 1,
    ) {
      auth = createAuthMock(options);
      const list = vi.fn();
      answers.forEach((answer, i) => (i === answers.length - 1 ? list.mockReturnValue(answer) : list.mockReturnValueOnce(answer)));
      apiMock = { list, getSagaTypes: vi.fn().mockReturnValue(of([])) };
      hubMock = {
        sagaUpdated$: new Subject<SagaSummary>(),
        connectionState$: new BehaviorSubject<SagaHubConnectionState>('connected'),
        subscribeToList: vi.fn().mockResolvedValue(undefined),
      };
      TestBed.configureTestingModule({
        imports: [SagaList],
        providers: [
          provideRouter([]),
          provideAuthMock(auth),
          { provide: SagaApiService, useValue: apiMock },
          { provide: SagaHubService, useValue: hubMock },
        ],
      });
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
      const fixture = TestBed.createComponent(SagaList);
      fixture.componentInstance.page.set(startPage);
      fixture.detectChanges();
      return { fixture, navigate };
    }

    describe('a session without sagas.view', () => {
      it('makes no request and no hub subscription, and says why', () => {
        const { fixture } = open({ access: none });
        const el: HTMLElement = fixture.nativeElement;

        expect(apiMock.list).not.toHaveBeenCalled();
        expect(apiMock.getSagaTypes).not.toHaveBeenCalled();
        expect(hubMock.subscribeToList).not.toHaveBeenCalled();
        expect(text(el.querySelector('.empty'))).toBe(noAccessText);
        expect(el.querySelector('.empty')?.getAttribute('role')).toBe('alert');
        expect(el.querySelector('.toolbar')).toBeNull();
        expect(el.querySelector('.count')).toBeNull();
        expect(el.textContent).not.toContain('Loading…');
        expect(fixture.componentInstance.loading()).toBe(false);
      });

      it('lists for a viewer whose sagas.view is scoped to some saga types', () => {
        const { fixture } = open({ access: scopedView }, [of(page([makeSummary()], 1, 1))]);

        expect(apiMock.list).toHaveBeenCalledTimes(1);
        expect(hubMock.subscribeToList).toHaveBeenCalledTimes(1);
        expect(fixture.nativeElement.querySelector('.empty')).toBeNull();
        expect(fixture.nativeElement.querySelectorAll('.saga-row')).toHaveLength(1);
      });

      it('starts when the session gains sagas.view, and asks for nothing twice', () => {
        const { fixture } = open({ access: none });

        auth.access.set(scopedView);
        fixture.detectChanges();
        expect(apiMock.list).toHaveBeenCalledTimes(1);
        expect(apiMock.getSagaTypes).toHaveBeenCalledTimes(1);
        expect(hubMock.subscribeToList).toHaveBeenCalledTimes(1);
        expect(fixture.nativeElement.querySelector('.toolbar')).not.toBeNull();

        // Another read of the session that still allows listing adds nothing (the allowed state did not change).
        auth.access.set({ permissions: ['sagas.view'], scoped: [] });
        fixture.detectChanges();
        expect(apiMock.list).toHaveBeenCalledTimes(1);
        expect(hubMock.subscribeToList).toHaveBeenCalledTimes(1);
      });

      it('replaces the list with the no-access state when the session loses sagas.view, and reads it again when it is back', () => {
        const { fixture } = open({}, [of(page([makeSummary()], 1, 1))]);
        const el: HTMLElement = fixture.nativeElement;
        expect(el.querySelectorAll('.saga-row')).toHaveLength(1);

        auth.access.set(none);
        fixture.detectChanges();
        expect(text(el.querySelector('.empty'))).toBe(noAccessText);
        expect(el.querySelector('.saga-row')).toBeNull();
        expect(apiMock.list).toHaveBeenCalledTimes(1);

        auth.access.set(scopedView);
        fixture.detectChanges();
        expect(apiMock.list).toHaveBeenCalledTimes(2);
        expect(hubMock.subscribeToList).toHaveBeenCalledTimes(1);
        expect(el.querySelectorAll('.saga-row')).toHaveLength(1);
      });
    });

    describe('a request the API refuses', () => {
      it('words a 403 from the API as no access, not as an outage, and reads again when live updates reconnect', () => {
        const refused = httpError(403, problem('forbidden', 'This needs the sagas.view permission. See docs/dashboard.md#authentication.'));
        const { fixture } = open({}, [of(page([makeSummary()], 1, 1)), throwError(() => refused), of(page([makeSummary()], 1, 1))]);
        fixture.componentInstance.refresh();
        fixture.detectChanges();

        expect(fixture.componentInstance.error()).toBe('You do not have access to these sagas.');
        expect(text(fixture.nativeElement.querySelector('.banner--error'))).toBe('You do not have access to these sagas.');
        expect(fixture.nativeElement.querySelector('.banner--error')?.getAttribute('role')).toBe('alert');
        expect(fixture.componentInstance.sagas()).toEqual([]);
        expect(fixture.nativeElement.textContent).not.toContain('Could not reach');

        // The server closes a connection when access changes; the client's reconnect is the cue to ask again.
        hubMock.connectionState$.next('reconnecting');
        hubMock.connectionState$.next('connected');
        expect(apiMock.list).toHaveBeenCalledTimes(3);
        expect(fixture.componentInstance.error()).toBeNull();
        expect(fixture.componentInstance.sagas()).toHaveLength(1);
      });

      it('shows a 403 that is not the API saying "forbidden" in its own words, and one without a code as an outage', () => {
        const change = httpError(403, problem('password_change_required', 'Change your password before using the dashboard.'));
        const { fixture } = open({}, [throwError(() => change), throwError(() => httpError(403, '<html>Forbidden</html>'))]);
        expect(fixture.componentInstance.error()).toBe('Change your password before using the dashboard.');

        fixture.componentInstance.refresh();
        expect(fixture.componentInstance.error()).toBe('Could not reach the vSaga Dashboard API. Is it running?');
      });

      it('shows the API text of a 400, not "Could not reach", and reads again when live updates reconnect', () => {
        const text400 = 'The search reads too many sagas. Narrow it with a saga type, a status or a kind.';
        const { fixture } = open({}, [of(page([makeSummary()], 1, 30)), throwError(() => httpError(400, { error: text400 }))]);
        fixture.componentInstance.refresh();
        fixture.detectChanges();

        expect(fixture.componentInstance.error()).toBe(text400);
        expect(text(fixture.nativeElement.querySelector('.banner--error'))).toBe(text400);
        // The rows on screen are still the last good page's, read under the same filters.
        expect(fixture.componentInstance.sagas()).toHaveLength(1);
        expect(fixture.componentInstance.page()).toBe(1);

        hubMock.connectionState$.next('reconnecting');
        hubMock.connectionState$.next('connected');
        expect(apiMock.list).toHaveBeenCalledTimes(3);
      });

      it('goes back to the last good page after a 400 with no maxPage, and replaces the URL with it', () => {
        const refused = new Subject<PagedResult<SagaSummary>>();
        const { fixture, navigate } = open({}, [of(page([makeSummary()], 1, 75)), refused]);
        fixture.componentInstance.nextPage();
        expect(fixture.componentInstance.page()).toBe(2);

        refused.error(httpError(400, { error: 'Too broad.' }));

        expect(apiMock.list).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2 }));
        expect(fixture.componentInstance.error()).toBe('Too broad.');
        expect(fixture.componentInstance.page()).toBe(1);
        expect(apiMock.list).toHaveBeenCalledTimes(2);
        // The refusal's correction is the last word on the URL, and it replaces the entry of the refused page.
        expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: expect.objectContaining({ page: null }), replaceUrl: true }));
      });

      it('still says an unreachable API is one', () => {
        const { fixture } = open({}, [throwError(() => httpError(0, null)), throwError(() => httpError(500, { title: 'Boom' }))]);
        expect(fixture.componentInstance.error()).toBe('Could not reach the vSaga Dashboard API. Is it running?');

        fixture.componentInstance.refresh();
        expect(fixture.componentInstance.error()).toBe('Could not reach the vSaga Dashboard API. Is it running?');
      });

      // RxJS reports an error nobody subscribed to from a timer, outside the test: fake timers bring that
      // throw inside it, so a missing error handler fails this spec itself.
      it('survives a failing saga type request', () => {
        vi.useFakeTimers();
        try {
          auth = createAuthMock();
          apiMock = {
            list: vi.fn().mockReturnValue(of(page([makeSummary()], 1, 1))),
            getSagaTypes: vi.fn().mockReturnValue(throwError(() => httpError(403, null))),
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
              provideAuthMock(auth),
              { provide: SagaApiService, useValue: apiMock },
              { provide: SagaHubService, useValue: hubMock },
            ],
          });
          const fixture = TestBed.createComponent(SagaList);

          fixture.detectChanges();
          expect(() => vi.runAllTimers()).not.toThrow();

          expect(fixture.componentInstance.sagaTypes()).toEqual([]);
          expect(fixture.componentInstance.sagas()).toHaveLength(1);
        } finally {
          vi.useRealTimers();
        }
      });
    });

    describe('a list that lost its access', () => {
      it('asks the API for no list on a reconnect, reads the session, and reloads once the session holds the permission again', () => {
        const { fixture } = open({}, [of(page([makeSummary()], 1, 1))]);
        auth.access.set(none);
        fixture.detectChanges();
        expect(fixture.nativeElement.querySelector('.saga-row')).toBeNull();
        expect(apiMock.list).toHaveBeenCalledTimes(1);

        hubMock.connectionState$.next('reconnecting');
        hubMock.connectionState$.next('connected');
        expect(auth.refresh).toHaveBeenCalledTimes(1);
        expect(apiMock.list).toHaveBeenCalledTimes(1);

        // The next reconnect finds a grant: the session read brings it, and the effect reads the list once.
        auth.refresh.mockImplementation(() => {
          auth.access.set(scopedView);
          return Promise.resolve('authenticated' as const);
        });
        hubMock.connectionState$.next('reconnecting');
        hubMock.connectionState$.next('connected');
        fixture.detectChanges();

        expect(auth.refresh).toHaveBeenCalledTimes(2);
        expect(apiMock.list).toHaveBeenCalledTimes(2);
        expect(fixture.nativeElement.querySelectorAll('.saga-row')).toHaveLength(1);
      });

      it('does not read the session on a reconnect while it holds the permission', () => {
        const { fixture } = open({}, [of(page([makeSummary()], 1, 1))]);
        hubMock.connectionState$.next('reconnecting');
        hubMock.connectionState$.next('connected');

        expect(auth.refresh).not.toHaveBeenCalled();
        expect(apiMock.list).toHaveBeenCalledTimes(2);
        expect(fixture.componentInstance.error()).toBeNull();
      });
    });

    describe('a list that was left', () => {
      it.each([
        ['a 400 past what the shape can reach', (answer: Subject<PagedResult<SagaSummary>>) => answer.error(httpError(400, { error: 'Page 30 is past the last page (20).', maxPage: 20 }))],
        ['a page past the real end', (answer: Subject<PagedResult<SagaSummary>>) => answer.next(page([], 30, 60))],
      ])('does not correct the URL when %s answers after leaving', (_, answerLate) => {
        const answer = new Subject<PagedResult<SagaSummary>>();
        const { fixture, navigate } = open({}, [answer], 30);
        navigate.mockClear();

        fixture.destroy();
        answerLate(answer);

        expect(navigate).not.toHaveBeenCalled();
        expect(apiMock.list).toHaveBeenCalledTimes(1);
        expect(fixture.componentInstance.page()).toBe(30);
      });
    });

    describe('answers that arrive late', () => {
      /** The page with each request answered by hand: every `list` call returns the next subject. */
      function openByHand() {
        const answers = [0, 1, 2, 3].map(() => new Subject<PagedResult<SagaSummary>>());
        const { fixture, navigate } = open({}, [...answers]);
        return { fixture, navigate, answers };
      }

      it('ignores a 400 with a maxPage that answers an earlier request: page, cap and URL stay', () => {
        const { fixture, navigate, answers } = openByHand();
        const c = fixture.componentInstance;
        answers[0].next(page([makeSummary()], 1, 1000));
        c.pageJump = 30;
        c.goToPage(); // request 2, for a page past what the shape can reach, left in flight
        c.status = 'Failed';
        c.onFilterChange(); // request 3
        answers[2].next(page([makeSummary({ correlationId: 'failed-1', status: 'Failed' })], 1, 1000));
        navigate.mockClear();

        answers[1].error(httpError(400, { error: 'Page 30 is past the last page (20).', maxPage: 20 }));

        expect(apiMock.list).toHaveBeenCalledTimes(3);
        expect(c.page()).toBe(1);
        expect(c.totalPages()).toBe(40);
        expect(c.error()).toBeNull();
        expect(c.sagas().map((s) => s.correlationId)).toEqual(['failed-1']);
        expect(navigate).not.toHaveBeenCalled();
      });

      it("ignores a late success: the rows stay the latest request's, and it is not the page a refusal returns to", () => {
        const { fixture, answers } = openByHand();
        const c = fixture.componentInstance;
        answers[0].next(page([makeSummary()], 1, 1000));
        c.nextPage(); // request 2, left in flight
        c.onFilterChange(); // request 3
        answers[2].next(page([makeSummary({ correlationId: 'latest' })], 1, 1000));

        answers[1].next(page([makeSummary({ correlationId: 'late' })], 2, 1000));
        expect(c.sagas().map((s) => s.correlationId)).toEqual(['latest']);

        c.refresh(); // request 4 is refused
        answers[3].error(httpError(400, { error: 'Too broad.' }));
        expect(c.page()).toBe(1);
        expect(c.sagas().map((s) => s.correlationId)).toEqual(['latest']);
      });
    });

    describe('rows that would contradict the toolbar', () => {
      const change: Array<[string, (c: SagaList) => void]> = [
        ['a status filter', (c) => { c.status = 'Failed'; c.onFilterChange(); }],
        ['the sort', (c) => c.toggleSort('Status')],
        ['the page size', (c) => { c.pageSize = 50; c.onFilterChange(); }],
      ];

      it.each(change)('are cleared with the total and the page when %s changes and the API refuses the new request', (_, apply) => {
        const { fixture } = open({}, [of(page([makeSummary()], 1, 90)), throwError(() => httpError(400, { error: 'Too broad.' }))]);
        const c = fixture.componentInstance;
        expect(c.sagas()).toHaveLength(1);

        apply(c);
        fixture.detectChanges();

        expect(c.error()).toBe('Too broad.');
        expect(c.sagas()).toEqual([]);
        expect(c.totalCount()).toBe(0);
        expect(c.page()).toBe(1);
        expect(fixture.nativeElement.querySelector('.saga-row')).toBeNull();
      });

      it('stay when the refused request is the one they were read under', () => {
        const { fixture } = open({}, [of(page([makeSummary()], 1, 90)), throwError(() => httpError(400, { error: 'Too broad.' }))]);
        fixture.componentInstance.refresh();

        expect(fixture.componentInstance.sagas()).toHaveLength(1);
      });
    });

    describe('live updates under a refusal', () => {
      it.each([
        ['a maxPage of 0', () => throwError(() => httpError(400, { error: 'Choose a saga type.', maxPage: 0 }))],
        ['a 403 from the API', () => throwError(() => httpError(403, problem('forbidden', 'No.')))],
      ])('paint no row after %s, and paint rows again once a request succeeds', (_, refusal) => {
        const { fixture } = open({}, [of(page([makeSummary()], 1, 1)), refusal()]);
        fixture.componentInstance.refresh();
        fixture.detectChanges();
        expect(fixture.componentInstance.sagas()).toEqual([]);

        hubMock.sagaUpdated$.next(makeSummary({ correlationId: 'pushed' }));
        fixture.detectChanges();
        expect(fixture.componentInstance.sagas()).toEqual([]);
        expect(fixture.componentInstance.totalCount()).toBe(0);
        expect(fixture.nativeElement.querySelector('.saga-row')).toBeNull();

        apiMock.list.mockReturnValue(of(page([], 1, 0)));
        fixture.componentInstance.refresh();
        hubMock.sagaUpdated$.next(makeSummary({ correlationId: 'pushed' }));
        expect(fixture.componentInstance.sagas().map((s) => s.correlationId)).toEqual(['pushed']);
      });
    });

    describe('while the session is ending', () => {
      it('says nothing about access and shows no list, and shows the list again if the session returns', () => {
        const { fixture } = open({}, [of(page([makeSummary()], 1, 1))]);
        const el: HTMLElement = fixture.nativeElement;

        auth.status.set('anonymous');
        fixture.detectChanges();
        expect(el.querySelector('h1')?.textContent).toBe('Sagas');
        expect(el.querySelector('.empty')).toBeNull();
        expect(el.querySelector('.toolbar')).toBeNull();
        expect(el.querySelector('.saga-row')).toBeNull();
        expect(el.querySelector('.count')).toBeNull();
        expect(el.textContent).not.toContain('no access');

        auth.status.set('authenticated');
        fixture.detectChanges();
        expect(el.querySelectorAll('.saga-row')).toHaveLength(1);
      });
    });

    describe('a page past what a list across several saga types can reach', () => {
      const pastTheEnd = (maxPage: number | undefined, error = 'Page 30 is past the last page (20) a list across several saga types can reach. Choose a saga type with the sagaType filter to page further.') =>
        throwError(() => httpError(400, maxPage === undefined ? { error } : { error, maxPage }));

      it('goes to maxPage, keeps the message, and stops offering pages beyond it', () => {
        const rows = [makeSummary({ correlationId: 'deep' })];
        const refused = new Subject<PagedResult<SagaSummary>>();
        const { fixture, navigate } = open({}, [of(page([makeSummary()], 1, 1000)), refused, of(page(rows, 20, 1000))]);
        const c = fixture.componentInstance;
        expect(c.totalPages()).toBe(40);

        c.pageJump = 30;
        c.goToPage();
        expect(c.page()).toBe(30);
        refused.error(httpError(400, { error: 'Page 30 is past the last page (20) a list across several saga types can reach. Choose a saga type with the sagaType filter to page further.', maxPage: 20 }));
        fixture.detectChanges();

        expect(apiMock.list).toHaveBeenCalledTimes(3);
        expect(apiMock.list).toHaveBeenLastCalledWith(expect.objectContaining({ page: 20 }));
        expect(c.page()).toBe(20);
        expect(c.sagas()).toEqual(rows);
        expect(c.error()).toContain('past the last page (20)');
        expect(text(fixture.nativeElement.querySelector('.banner--error'))).toContain('Choose a saga type with the sagaType filter');
        expect(c.totalPages()).toBe(20);
        expect(c.hasNextPage()).toBe(false);
        expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: expect.objectContaining({ page: 20 }), replaceUrl: true }));

        // The cap belongs to the request that met it: changing a filter lifts it.
        c.onFilterChange();
        expect(c.totalPages()).toBe(40);
      });

      it('returns to the page on screen without asking again when that is already maxPage', () => {
        const { fixture } = open({}, [of(page([makeSummary()], 20, 1000)), pastTheEnd(20)], 20);
        const c = fixture.componentInstance;
        expect(apiMock.list).toHaveBeenCalledTimes(1);

        c.nextPage();

        expect(apiMock.list).toHaveBeenCalledTimes(2);
        expect(c.page()).toBe(20);
        expect(c.sagas()).toHaveLength(1);
        expect(c.error()).toContain('past the last page');
        expect(c.hasNextPage()).toBe(false);
      });

      it('does not ask for a page it was told is beyond the end when the pager is used', () => {
        const { fixture } = open({}, [of(page([makeSummary()], 20, 1000)), pastTheEnd(20)], 20);
        fixture.componentInstance.nextPage();
        apiMock.list.mockClear();

        fixture.componentInstance.nextPage();

        expect(apiMock.list).not.toHaveBeenCalled();
      });

      it('never navigates to page 0: a bookmarked deep page with a maxPage of 0 lands on page 1 and asks once', () => {
        const { fixture } = open({}, [pastTheEnd(0, 'Choose a saga type.')], 3);
        const c = fixture.componentInstance;

        expect(c.page()).toBe(1);
        expect(c.error()).toBe('Choose a saga type.');
        expect(apiMock.list).toHaveBeenCalledTimes(1);
        expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ page: 3 }));
      });

      // The bound the API names can be far beyond the pages that really exist: the page it sends the list to
      // is then empty, and the list moves on to the last real page. The refusal's message is not the
      // correction's to clear.
      it('keeps the refusal message when the page it sent the list to is itself past the real end', () => {
        const message = 'Page 999 is past the last page (400) a list across several saga types can reach. Choose a saga type with the sagaType filter.';
        const { fixture } = open({}, [pastTheEnd(400, message), of(page([], 400, 150)), of(page([makeSummary()], 6, 150))], 999);
        const c = fixture.componentInstance;
        fixture.detectChanges();

        expect(apiMock.list).toHaveBeenCalledTimes(3);
        expect(apiMock.list).toHaveBeenLastCalledWith(expect.objectContaining({ page: 6 }));
        expect(c.page()).toBe(6);
        expect(c.sagas()).toHaveLength(1);
        expect(c.error()).toBe(message);
        expect(text(fixture.nativeElement.querySelector('.banner--error'))).toBe(message);

        // The next thing the viewer asks for clears it.
        c.prevPage();
        expect(c.error()).toBeNull();
      });

      it('brings a bookmarked page beyond the end back to maxPage, with the message', () => {
        const { fixture } = open({}, [pastTheEnd(20), of(page([makeSummary()], 20, 1000))], 30);
        const c = fixture.componentInstance;

        expect(apiMock.list).toHaveBeenCalledTimes(2);
        expect(apiMock.list).toHaveBeenLastCalledWith(expect.objectContaining({ page: 20 }));
        expect(c.page()).toBe(20);
        expect(c.sagas()).toHaveLength(1);
        expect(c.error()).toContain('past the last page (20)');
      });

      it('says that no page can be served for a maxPage of 0: the API text, no rows, page 1, no retry', () => {
        const message = 'You can see 60 saga types, more than the 10 a list sorted by status, filtered by status or kind, or searched can combine. Choose a saga type with the sagaType filter.';
        const refused = new Subject<PagedResult<SagaSummary>>();
        const { fixture, navigate } = open({}, [of(page([makeSummary()], 1, 90)), refused]);
        const c = fixture.componentInstance;
        c.nextPage();
        refused.error(httpError(400, { error: message, maxPage: 0 }));
        fixture.detectChanges();

        expect(c.error()).toBe(message);
        expect(text(fixture.nativeElement.querySelector('.banner--error'))).toBe(message);
        expect(c.page()).toBe(1);
        expect(c.sagas()).toEqual([]);
        expect(c.totalCount()).toBe(0);
        expect(apiMock.list).toHaveBeenCalledTimes(2);
        expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: expect.objectContaining({ page: null }), replaceUrl: true }));
        // The filter that always works is still offered, and a pager with nothing to page through is not.
        expect(fixture.nativeElement.querySelector('.toolbar select[class~="input"]')).not.toBeNull();
        expect(fixture.nativeElement.querySelector('.pagination')).toBeNull();
      });

      it('asks for a saga type filter in its own words when the API sent no text with a maxPage of 0', () => {
        const { fixture } = open({}, [throwError(() => httpError(400, { maxPage: 0 }))]);

        expect(fixture.componentInstance.error()).toBe('Too many saga types are visible to list them together. Choose a saga type with the filter.');
        expect(fixture.componentInstance.page()).toBe(1);
      });

      it('does not loop when the API names a maxPage that is not below the page it refused', () => {
        const { fixture } = open({}, [of(page([makeSummary()], 1, 75)), pastTheEnd(5)]);
        fixture.componentInstance.nextPage();

        expect(apiMock.list).toHaveBeenCalledTimes(2);
        expect(fixture.componentInstance.page()).toBe(1);
        expect(fixture.componentInstance.error()).toContain('past the last page');
      });
    });
  });
});
