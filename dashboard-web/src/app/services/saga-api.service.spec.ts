import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE_URL } from '../api-config';
import { PagedResult, SagaDetail, SagaLogEntry, SagaMap, SagaSummary, SagaTypeInfo } from '../models/saga.model';
import { SagaApiService } from './saga-api.service';

describe('SagaApiService', () => {
  let service: SagaApiService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(SagaApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('list() requests /api/sagas with default paging when no filter is given', () => {
    const empty: PagedResult<SagaSummary> = { items: [], page: 1, pageSize: 25, totalCount: 0 };

    service.list({}).subscribe((result) => expect(result).toEqual(empty));

    const req = httpMock.expectOne((r) => r.url === `${API_BASE_URL}/api/sagas`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('25');
    expect(req.request.params.has('status')).toBe(false);
    expect(req.request.params.has('sagaType')).toBe(false);
    expect(req.request.params.has('kind')).toBe(false);
    expect(req.request.params.has('search')).toBe(false);
    req.flush(empty);
  });

  it('list() forwards status/kind/sagaType/search/paging filters as query params', () => {
    service
      .list({ status: 'Failed', kind: 'Orchestrated', sagaType: 'OrderSaga', search: 'abc', page: 2, pageSize: 10 })
      .subscribe();

    const req = httpMock.expectOne((r) => r.url === `${API_BASE_URL}/api/sagas`);
    expect(req.request.params.get('status')).toBe('Failed');
    expect(req.request.params.get('kind')).toBe('Orchestrated');
    expect(req.request.params.get('sagaType')).toBe('OrderSaga');
    expect(req.request.params.get('search')).toBe('abc');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('10');
    req.flush({ items: [], page: 2, pageSize: 10, totalCount: 0 });
  });

  it('get() requests the saga detail endpoint by id', () => {
    const detail: SagaDetail = {
      summary: {
        correlationId: 'abc-123',
        sagaType: 'OrderSaga',
        kind: 'Orchestrated',
        currentState: 'Completed',
        status: 'Completed',
        createdAtUtc: '2026-01-01T00:00:00Z',
        updatedAtUtc: '2026-01-01T00:00:01Z',
        version: 3,
        parentSagaType: null,
        parentCorrelationId: null,
      },
      dataJson: null,
    };

    service.get('OrderSaga', 'abc-123').subscribe((result) => expect(result).toEqual(detail));

    const req = httpMock.expectOne(`${API_BASE_URL}/api/sagas/OrderSaga/abc-123`);
    expect(req.request.method).toBe('GET');
    req.flush(detail);
  });

  it('getTimeline() requests the timeline endpoint', () => {
    const entries: SagaLogEntry[] = [];
    service.getTimeline('OrderSaga', 'abc-123').subscribe((result) => expect(result).toBe(entries));

    const req = httpMock.expectOne(`${API_BASE_URL}/api/sagas/OrderSaga/abc-123/timeline`);
    expect(req.request.method).toBe('GET');
    req.flush(entries);
  });

  it('getMap() requests the map endpoint', () => {
    const map: SagaMap = {
      summary: {
        correlationId: 'abc-123',
        sagaType: 'OrderSaga',
        kind: 'Orchestrated',
        currentState: 'Completed',
        status: 'Completed',
        createdAtUtc: '2026-01-01T00:00:00Z',
        updatedAtUtc: '2026-01-01T00:00:01Z',
        version: 1,
        parentSagaType: null,
        parentCorrelationId: null,
      },
      nodes: [],
      edges: [],
      events: [],
      failureEventIndex: null,
    };

    service.getMap('OrderSaga', 'abc-123').subscribe((result) => expect(result).toBe(map));

    const req = httpMock.expectOne(`${API_BASE_URL}/api/sagas/OrderSaga/abc-123/map`);
    expect(req.request.method).toBe('GET');
    req.flush(map);
  });

  it('retry() posts to the retry endpoint', () => {
    service.retry('OrderSaga', 'abc-123').subscribe();

    const req = httpMock.expectOne(`${API_BASE_URL}/api/sagas/OrderSaga/abc-123/retry`);
    expect(req.request.method).toBe('POST');
    req.flush(null);
  });

  it('get() percent-encodes the saga type segment', () => {
    service.get('Order/Saga', 'abc-123').subscribe();

    const req = httpMock.expectOne(`${API_BASE_URL}/api/sagas/Order%2FSaga/abc-123`);
    expect(req.request.method).toBe('GET');
    req.flush(null);
  });

  it('getChildren() requests the children endpoint', () => {
    service.getChildren('OrderSaga', 'abc-123').subscribe();

    const req = httpMock.expectOne(`${API_BASE_URL}/api/sagas/OrderSaga/abc-123/children`);
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('findByCorrelationId() requests the correlations endpoint', () => {
    service.findByCorrelationId('abc-123').subscribe();

    const req = httpMock.expectOne(`${API_BASE_URL}/api/correlations/abc-123`);
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('getSagaTypes() requests the saga-types endpoint', () => {
    const types: SagaTypeInfo[] = [{ sagaType: 'OrderSaga', kind: 'Orchestrated' }];
    service.getSagaTypes().subscribe((result) => expect(result).toBe(types));

    const req = httpMock.expectOne(`${API_BASE_URL}/api/saga-types`);
    expect(req.request.method).toBe('GET');
    req.flush(types);
  });
});
