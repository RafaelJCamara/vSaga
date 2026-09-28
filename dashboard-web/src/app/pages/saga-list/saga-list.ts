import { Component, OnDestroy, OnInit, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subject, Subscription, debounceTime } from 'rxjs';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubConnectionState, SagaHubService } from '../../services/saga-hub.service';
import { SagaKind, SagaSortColumn, SagaStatus, SagaSummary, SagaTypeInfo } from '../../models/saga.model';
import { KindBadge } from '../../components/kind-badge/kind-badge';
import { StatusBadge } from '../../components/status-badge/status-badge';

const STATUSES: SagaStatus[] = ['Running', 'Completed', 'Failed', 'Compensating', 'Compensated', 'TimedOut', 'Cancelled'];
const KINDS: SagaKind[] = ['Orchestrated', 'Choreographed'];
const PAGE_SIZES = [25, 50, 75, 100];
const SEARCH_DEBOUNCE_MS = 300;

type SortDirection = 'asc' | 'desc';

@Component({
  selector: 'app-saga-list',
  imports: [CommonModule, FormsModule, RouterLink, KindBadge, StatusBadge],
  templateUrl: './saga-list.html',
  styleUrl: './saga-list.scss',
})
export class SagaList implements OnInit, OnDestroy {
  readonly statuses = STATUSES;
  readonly kinds = KINDS;

  readonly sagas = signal<SagaSummary[]>([]);
  readonly sagaTypes = signal<SagaTypeInfo[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly totalCount = signal(0);
  readonly connectionState = signal<SagaHubConnectionState>('disconnected');
  /** True once the hub has connected at least once — gates the "disconnected" banner so it doesn't
   * flash during the ordinary, brief window before the very first connect resolves on page load. */
  readonly hasEverConnected = signal(false);

  readonly pageSizes = PAGE_SIZES;
  pageSize = PAGE_SIZES[0];
  readonly page = signal(1);
  readonly hasNextPage = computed(() => this.page() * this.pageSize < this.totalCount());
  readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalCount() / this.pageSize)));

  pageJump: number | null = null;

  /** Bumped instead of prepended when a live update matches the filter but we're off page 1 —
   * prepending there would silently show the wrong rows for the page the user is looking at. */
  readonly newSagasAvailable = signal(0);

  readonly sortColumn = signal<SagaSortColumn | null>(null);
  readonly sortDirection = signal<SortDirection>('asc');

  status: SagaStatus | '' = '';
  kind: SagaKind | '' = '';
  sagaType = '';
  search = '';

  private subs: Subscription[] = [];
  private readonly searchChange$ = new Subject<void>();

  constructor(
    private readonly api: SagaApiService,
    private readonly hub: SagaHubService,
    private readonly route: ActivatedRoute,
    private readonly router: Router,
  ) {}

  ngOnInit(): void {
    this.readFiltersFromUrl();

    this.api.getSagaTypes().subscribe((types) => this.sagaTypes.set(types));
    this.refresh();

    void this.hub.subscribeToList();
    this.subs.push(
      this.hub.sagaUpdated$.subscribe((summary) => this.upsert(summary)),
      this.hub.connectionState$.subscribe((s) => {
        // Captured before the error signal is touched by anything below -- a reconnect after an
        // ordinary first-ever connect (error() still null, nothing has failed yet) must not trigger
        // a redundant extra refresh() on top of the one ngOnInit already fired.
        const hadError = this.error() !== null;
        this.connectionState.set(s);
        if (s === 'connected') {
          this.hasEverConnected.set(true);
          // A prior REST load failure (e.g. the API was down on page load) leaves the error banner
          // and stale/empty data on screen even after the hub reconnects and live push updates
          // resume -- reconnecting only proves the SignalR channel is back, not that the failed
          // GET /api/sagas has been retried. Re-run it now so both clear together.
          if (hadError) this.refresh();
        }
      }),
      this.searchChange$.pipe(debounceTime(SEARCH_DEBOUNCE_MS)).subscribe(() => this.onFilterChange()),
    );
  }

  ngOnDestroy(): void {
    this.subs.forEach((s) => s.unsubscribe());
  }

  /** Restores filters/page/sort from the URL on load — so a shared or bookmarked link (or a plain
   * refresh) lands back on the same view instead of the unfiltered default. Values that don't match a
   * known status/kind/column are ignored rather than trusted verbatim. */
  private readFiltersFromUrl(): void {
    const params = this.route.snapshot.queryParamMap;

    const status = params.get('status');
    if (status && (STATUSES as string[]).includes(status)) this.status = status as SagaStatus;

    const kind = params.get('kind');
    if (kind && (KINDS as string[]).includes(kind)) this.kind = kind as SagaKind;

    const sagaType = params.get('sagaType');
    if (sagaType) this.sagaType = sagaType;

    const search = params.get('search');
    if (search) this.search = search;

    const page = Number(params.get('page'));
    if (Number.isInteger(page) && page > 0) this.page.set(page);

    const pageSize = Number(params.get('pageSize'));
    if (PAGE_SIZES.includes(pageSize)) this.pageSize = pageSize;

    const sortBy = params.get('sortBy');
    if (sortBy === 'Status' || sortBy === 'UpdatedAt') {
      this.sortColumn.set(sortBy);
      this.sortDirection.set(params.get('sortDescending') === 'true' ? 'desc' : 'asc');
    }
  }

  /** The inverse of readFiltersFromUrl — called after every filter/sort/page change so the URL always
   * reflects what's on screen. Empty/default values are cleared (`null`) rather than written, keeping
   * the URL free of noise for the common all-sagas, page-1, unsorted view. */
  private syncUrlFromFilters(): void {
    const queryParams: Record<string, string | number | null> = {
      status: this.status || null,
      kind: this.kind || null,
      sagaType: this.sagaType || null,
      search: this.search || null,
      page: this.page() > 1 ? this.page() : null,
      pageSize: this.pageSize !== PAGE_SIZES[0] ? this.pageSize : null,
      sortBy: this.sortColumn(),
      sortDescending: this.sortColumn() && this.sortDirection() === 'desc' ? 'true' : null,
    };
    void this.router.navigate([], { relativeTo: this.route, queryParams, queryParamsHandling: '' });
  }

  /** Filter changes must land back on page 1 — the previous page number may no longer exist under
   * the new filter, and showing a stale page's rows under a changed filter would be misleading. */
  onFilterChange(): void {
    this.page.set(1);
    this.refresh();
    this.syncUrlFromFilters();
  }

  /** Debounced live search — see the (ngModelChange) binding in the template. Typing a term no longer
   * requires pressing Enter to see results. */
  onSearchInput(): void {
    this.searchChange$.next();
  }

  nextPage(): void {
    if (!this.hasNextPage()) return;
    this.page.set(this.page() + 1);
    this.refresh();
    this.syncUrlFromFilters();
  }

  prevPage(): void {
    if (this.page() <= 1) return;
    this.page.set(this.page() - 1);
    this.refresh();
    this.syncUrlFromFilters();
  }

  goToPage(): void {
    const target = this.pageJump;
    if (
      target === null ||
      !Number.isInteger(target) ||
      target < 1 ||
      target > this.totalPages() ||
      target === this.page()
    ) {
      return;
    }
    this.page.set(target);
    this.refresh();
    this.syncUrlFromFilters();
    this.pageJump = null;
  }

  /** Sorting reorders the whole server-side result set, not just the rows already on screen — the
   * previous page number's rows would land somewhere else entirely under the new order, so (like a
   * filter change) this lands back on page 1 and re-fetches rather than reshuffling in place. */
  toggleSort(column: SagaSortColumn): void {
    if (this.sortColumn() === column) {
      this.sortDirection.set(this.sortDirection() === 'asc' ? 'desc' : 'asc');
    } else {
      this.sortColumn.set(column);
      this.sortDirection.set('asc');
    }
    this.page.set(1);
    this.refresh();
    this.syncUrlFromFilters();
  }

  refresh(): void {
    this.loading.set(true);
    this.error.set(null);
    this.newSagasAvailable.set(0);

    this.api
      .list({
        status: this.status || undefined,
        kind: this.kind || undefined,
        sagaType: this.sagaType || undefined,
        search: this.search || undefined,
        page: this.page(),
        pageSize: this.pageSize,
        sortBy: this.sortColumn() ?? undefined,
        sortDescending: this.sortColumn() ? this.sortDirection() === 'desc' : undefined,
      })
      .subscribe({
        next: (result) => {
          this.sagas.set(result.items);
          this.totalCount.set(result.totalCount);
          this.loading.set(false);

          // A shared/bookmarked link's page number (restored in readFiltersFromUrl) can be stale by
          // the time it's opened, if the result set has since shrunk -- totalPages() only becomes
          // knowable once totalCount arrives here, so it can't be clamped any earlier. Left
          // uncorrected, the user lands on an empty "No sagas match these filters yet." view showing
          // e.g. "Page 40 of 3", with no way back to page 1 except 39 clicks of Previous.
          if (this.page() > this.totalPages()) {
            this.page.set(this.totalPages());
            this.syncUrlFromFilters();
            this.refresh();
          }
        },
        error: () => {
          this.error.set('Could not reach the vSaga Dashboard API. Is it running?');
          this.loading.set(false);
        },
      });
  }

  private upsert(summary: SagaSummary): void {
    const current = this.sagas();
    // Matched on both halves of the identity: a correlation id alone can be tracked by more than
    // one saga type, and matching on it alone would let one saga's update overwrite the other's row.
    const index = current.findIndex(
      (s) => s.correlationId === summary.correlationId && s.sagaType === summary.sagaType,
    );

    if (index >= 0) {
      const next = current.slice();
      next[index] = summary;
      next.sort((a, b) => this.compareSagas(a, b));
      this.sagas.set(next);
      return;
    }

    if (!this.matchesFilter(summary)) return;

    this.totalCount.set(this.totalCount() + 1);

    if (this.page() === 1) {
      // totalCount tracks the real server-side total, but the rendered page-1 array must stay
      // capped at pageSize — otherwise live inserts under sustained traffic grow the DOM without
      // bound. Sorting first means the trimmed-off tail is whatever a server refresh would also
      // push to page 2.
      this.sagas.set(
        [...current, summary].sort((a, b) => this.compareSagas(a, b)).slice(0, this.pageSize),
      );
    } else {
      this.newSagasAvailable.set(this.newSagasAvailable() + 1);
    }
  }

  /** Keeps a live-patched page1 in the same order the server would return it in — an in-place status
   * update or a newly-inserted saga can change where a row belongs under the active sort, so a plain
   * prepend/replace would silently drift out of order until the next refresh(). */
  private compareSagas(a: SagaSummary, b: SagaSummary): number {
    const column = this.sortColumn();
    if (!column) return new Date(b.updatedAtUtc).getTime() - new Date(a.updatedAtUtc).getTime();

    const direction = this.sortDirection() === 'asc' ? 1 : -1;
    const valueA = column === 'Status' ? STATUSES.indexOf(a.status) : new Date(a.updatedAtUtc).getTime();
    const valueB = column === 'Status' ? STATUSES.indexOf(b.status) : new Date(b.updatedAtUtc).getTime();
    return (valueA - valueB) * direction;
  }

  private matchesFilter(summary: SagaSummary): boolean {
    if (this.status && summary.status !== this.status) return false;
    if (this.kind && summary.kind !== this.kind) return false;
    if (this.sagaType && summary.sagaType !== this.sagaType) return false;

    const term = this.search.trim().toLowerCase();
    if (term) {
      const matchesType = summary.sagaType.toLowerCase().includes(term);
      const matchesCorrelationId = summary.correlationId.toLowerCase().includes(term);
      if (!matchesType && !matchesCorrelationId) return false;
    }

    return true;
  }
}
