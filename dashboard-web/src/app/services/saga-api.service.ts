import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../api-config';
import {
  PagedResult,
  SagaDetail,
  SagaListFilter,
  SagaLogEntry,
  SagaMap,
  SagaSummary,
  SagaTypeInfo,
} from '../models/saga.model';

@Injectable({ providedIn: 'root' })
export class SagaApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${API_BASE_URL}/api/sagas`;

  list(filter: SagaListFilter): Observable<PagedResult<SagaSummary>> {
    let params = new HttpParams();
    if (filter.status) params = params.set('status', filter.status);
    if (filter.sagaType) params = params.set('sagaType', filter.sagaType);
    if (filter.kind) params = params.set('kind', filter.kind);
    if (filter.search) params = params.set('search', filter.search);
    params = params.set('page', filter.page ?? 1);
    params = params.set('pageSize', filter.pageSize ?? 25);
    if (filter.sortBy) params = params.set('sortBy', filter.sortBy).set('sortDescending', filter.sortDescending ?? false);

    return this.http.get<PagedResult<SagaSummary>>(this.baseUrl, { params });
  }

  // A saga instance is identified by (sagaType, correlationId), so every per-instance URL carries
  // both. encodeURIComponent on the type because it is a free-form saga name, not a guid.
  private instanceUrl(sagaType: string, correlationId: string): string {
    return `${this.baseUrl}/${encodeURIComponent(sagaType)}/${correlationId}`;
  }

  get(sagaType: string, correlationId: string): Observable<SagaDetail> {
    return this.http.get<SagaDetail>(this.instanceUrl(sagaType, correlationId));
  }

  getTimeline(sagaType: string, correlationId: string): Observable<SagaLogEntry[]> {
    return this.http.get<SagaLogEntry[]>(`${this.instanceUrl(sagaType, correlationId)}/timeline`);
  }

  getMap(sagaType: string, correlationId: string): Observable<SagaMap> {
    return this.http.get<SagaMap>(`${this.instanceUrl(sagaType, correlationId)}/map`);
  }

  /** The sagas this one started as sub-sagas. Each has its own correlation id, so this is not the same question as findByCorrelationId. */
  getChildren(sagaType: string, correlationId: string): Observable<SagaSummary[]> {
    return this.http.get<SagaSummary[]>(`${this.instanceUrl(sagaType, correlationId)}/children`);
  }

  retry(sagaType: string, correlationId: string): Observable<void> {
    return this.http.post<void>(`${this.instanceUrl(sagaType, correlationId)}/retry`, {});
  }

  /** Every saga instance tracking this correlation id, across all saga types. */
  findByCorrelationId(correlationId: string): Observable<SagaSummary[]> {
    return this.http.get<SagaSummary[]>(`${API_BASE_URL}/api/correlations/${correlationId}`);
  }

  getSagaTypes(): Observable<SagaTypeInfo[]> {
    return this.http.get<SagaTypeInfo[]>(`${API_BASE_URL}/api/saga-types`);
  }
}
