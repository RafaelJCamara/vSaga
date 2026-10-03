import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, effect, signal, untracked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subject, Subscription, debounceTime } from 'rxjs';
import { AuthService } from '../../services/auth.service';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubConnectionState, SagaHubService } from '../../services/saga-hub.service';
import { SagaKind, SagaSortColumn, SagaStatus, SagaSummary, SagaTypeInfo } from '../../models/saga.model';
import { KindBadge } from '../../components/kind-badge/kind-badge';
import { StatusBadge } from '../../components/status-badge/status-badge';
import { FORBIDDEN_CODE, problemOf } from '../../util/http-error';

const STATUSES: SagaStatus[] = ['Running', 'Completed', 'Failed', 'Compensating', 'Compensated', 'TimedOut', 'Cancelled'];
const KINDS: SagaKind[] = ['Orchestrated', 'Choreographed'];
const PAGE_SIZES = [25, 50, 75, 100];
const SEARCH_DEBOUNCE_MS = 300;

type SortDirection = 'asc' | 'desc';

const UNREACHABLE = 'Could not reach the vSaga Dashboard API. Is it running?';
const FORBIDDEN = 'You do not have access to these sagas.';
/** What a 400 says when its body carries no text of its own: for a `maxPage` of 0, and for any other. */
const TOO_MANY_TYPES = 'Too many saga types are visible to list them together. Choose a saga type with the filter.';
const REFUSED = 'The API could not list sagas for these filters.';
/**
 * The `maxPage` of the scoped list's 400 body, `{ error, maxPage }`: the last page the request's shape can
 * reach (0 when none can). Null when the body carries none (the Redis scan limit's 400 has no such member).
 */
function maxPageOf(err: unknown): number | null {
  const value = (err as { error?: { maxPage?: unknown } } | null)?.error?.maxPage;
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : null;
}

@Component({
  selector: 'app-saga-list',
  imports: [CommonModule, FormsModule, RouterLink, KindBadge, StatusBadge],
  templateUrl: './saga-list.html',
  changeDetection: ChangeDetectionStrategy.Eager,
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
  /** The last page the API said this request can reach (a 400's `maxPage`), until a filter, a sort or the
   *  page size changes the request: the pager does not offer a page past it. */
  private readonly pageCap = signal<number | null>(null);
  readonly totalPages = computed(() =>
    Math.max(1, Math.min(Math.ceil(this.totalCount() / this.pageSize), this.pageCap() ?? Infinity)),
  );
  readonly hasNextPage = computed(() => this.page() < this.totalPages());
  /** The page the rows on screen belong to: where a rejected request returns to. Null before the first answer. */
  private lastGoodPage: number | null = null;
  /** The filters, sort and page size `lastGoodPage` was read under: a page number means nothing under others. */
  private lastGoodKey: string | null = null;
  /** The latest list request; the answer to an earlier one is dropped, whatever it says. */
  private listRequest = 0;
  /** Set while the API refuses the list as asked (a 403, or no page to serve): a live push must not paint
   *  rows under the refusal. Cleared when the next request starts. */
  private listRefused = false;
  /** Whether the banner's failure is an API that could not be reached: it is then retried when the hub
   *  connects for the first time (a 403 or a 400 is not: it would answer the same again). Every later
   *  reconnect re-reads the list whatever is on screen. */
  private errorIsTransient = false;

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

  /** Whether the session may list sagas at all (`sagas.view` for some type, or for every one). A computed
   *  signal: a session refreshed while the page is open shows or removes the list without a reload. */
  readonly hasAccess = computed(() => this.auth.canAny('sagas.view'));
  /** False while the session is ending (sign-out clears the access at once, and the page stays until the
   *  navigation): the page then shows nothing, not a no-access state that would be a lie. */
  readonly signedIn = computed(() => this.auth.isAuthenticated());
  private started = false;
  /** Set while a list that had started is shown without access; regaining it reads the list again. */
  private accessLost = false;

  constructor(
    private readonly api: SagaApiService,
    private readonly hub: SagaHubService,
    private readonly auth: AuthService,
    private readonly route: ActivatedRoute,
    private readonly router: Router,
  ) {
    // The session can change under an open list (the interceptor refreshes it after a 403, and it is read
    // again when the tab is shown): a list opened without access starts when it gains it, and one that
    // lost it reads again when it comes back, so it never shows rows from before.
    effect(() => {
      const allowed = this.hasAccess();
      untracked(() => {
        if (!allowed) this.accessLost = this.started;
        else if (this.accessLost) {
          this.accessLost = false;
          this.refresh();
        } else this.start();
      });
    });
  }

  ngOnInit(): void {
    this.readFiltersFromUrl();
    this.subs.push(this.searchChange$.pipe(debounceTime(SEARCH_DEBOUNCE_MS)).subscribe(() => this.onFilterChange()));
    this.start();
  }

  /**
   * The list's requests and its hub subscription, once and only for a session that may see sagas: without
   * access there is nothing to ask for (every call would answer 403), so none is made and the page says so.
   */
  private start(): void {
    if (this.started) return;
    if (!this.hasAccess()) {
      this.loading.set(false);
      return;
    }
    this.started = true;

    // The types only fill the filter: without them the select offers "All saga types" alone.
    this.api.getSagaTypes().subscribe({ next: (types) => this.sagaTypes.set(types), error: () => undefined });
    this.refresh();

    void this.hub.subscribeToList();
    this.subs.push(
      this.hub.sagaUpdated$.subscribe((summary) => this.upsert(summary)),
      this.hub.connectionState$.subscribe((s) => {
        // Captured before the error signal is touched by anything below.
        const hadError = this.error() !== null && this.errorIsTransient;
        this.connectionState.set(s);
        if (s === 'connected') {
          const wasConnected = this.hasEverConnected();
          this.hasEverConnected.set(true);
          // A list that lost its access asks the list nothing (it would only answer 403): the reconnect
          // may be the server's word of a grant, so the session is read, and the effect reloads the list
          // if it now holds the permission.
          if (!this.hasAccess()) {
            void this.auth.refresh();
            return;
          }
          // The first connect adds nothing to the load ngOnInit fired. A later one re-reads the list: the
          // pushes sent while the hub was down are lost, and the server closes a user's connection when
          // their access changes, so this is how a narrowed or widened list is noticed (a 403 then goes
          // through the interceptor, which refreshes the session). A REST failure that is still on
          // screen (e.g. the API was down on page load) is retried the same way, so both clear together.
          if (hadError || wasConnected) this.refresh();
          // A later connect also reads the session, so a scope that was widened or narrowed since is
          // noticed by the session too, not only by what the API answers (the read is single-flight and
          // bounded, and adopting a session never reconnects the hub, so this cannot loop). Only a change
          // of whether the list may be shown at all moves the page, through the effect above.
          if (wasConnected) void this.auth.refresh();
        }
      }),
    );
  }

  ngOnDestroy(): void {
    // An answer to a request still in flight must not correct the page or the URL of a list that was left.
    this.listRequest++;
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
   * the URL free of noise for the common all-sagas, page-1, unsorted view. A correction of the page
   * replaces the history entry: Back must not land on the page that was refused. */
  private syncUrlFromFilters(replaceUrl = false): void {
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
    void this.router.navigate([], { relativeTo: this.route, queryParams, queryParamsHandling: '', replaceUrl });
  }

  /** Filter changes must land back on page 1 — the previous page number may no longer exist under
   * the new filter, and showing a stale page's rows under a changed filter would be misleading. */
  onFilterChange(): void {
    this.page.set(1);
    this.pageCap.set(null);
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
    this.pageCap.set(null);
    this.refresh();
    this.syncUrlFromFilters();
  }

  /** The filters, sort and page size of the next request: what a page number is relative to. */
  private requestKey(): string {
    return JSON.stringify([this.status, this.kind, this.sagaType, this.search, this.sortColumn(), this.sortDirection(), this.pageSize]);
  }

  /** `notice` is the message a rejected request left behind, kept on show while the page it sent the list to loads. */
  refresh(notice: string | null = null): void {
    this.loading.set(true);
    this.error.set(notice);
    this.errorIsTransient = false;
    this.listRefused = false;
    this.newSagasAvailable.set(0);

    const request = ++this.listRequest;
    const requestedPage = this.page();
    const key = this.requestKey();
    this.api
      .list({
        status: this.status || undefined,
        kind: this.kind || undefined,
        sagaType: this.sagaType || undefined,
        search: this.search || undefined,
        page: requestedPage,
        pageSize: this.pageSize,
        sortBy: this.sortColumn() ?? undefined,
        sortDescending: this.sortColumn() ? this.sortDirection() === 'desc' : undefined,
      })
      .subscribe({
        next: (result) => {
          // Requests overlap (a filter changed while one was in flight) and can answer out of order: only
          // the latest one may touch the page, the pager or the URL.
          if (request !== this.listRequest) return;
          this.sagas.set(result.items);
          this.totalCount.set(result.totalCount);
          this.loading.set(false);
          this.lastGoodPage = requestedPage;
          this.lastGoodKey = key;

          // A shared/bookmarked link's page number (restored in readFiltersFromUrl) can be stale by
          // the time it's opened, if the result set has since shrunk -- totalPages() only becomes
          // knowable once totalCount arrives here, so it can't be clamped any earlier. Left
          // uncorrected, the user lands on an empty "No sagas match these filters yet." view showing
          // e.g. "Page 40 of 3", with no way back to page 1 except 39 clicks of Previous.
          // The message a refusal left behind (the page it sent the list to may itself be past the end)
          // belongs to the refusal, not to this correction: it stays until the viewer asks for something.
          if (this.page() > this.totalPages()) {
            this.page.set(this.totalPages());
            this.syncUrlFromFilters(true);
            this.refresh(this.error());
          }
        },
        error: (err) => {
          if (request === this.listRequest) this.onListRejected(err, requestedPage, key);
        },
      });
  }

  /**
   * What a failed list request says. A 403 from the API (`forbidden`) means the session holds no
   * `sagas.view` (the interceptor refreshes it, and the page then shows its no-access state); any other
   * 403 says its own reason. A 400 is the API refusing this request, in its own words; anything else is an
   * API that could not be reached, which clears when live updates come back.
   */
  private onListRejected(err: unknown, requestedPage: number, key: string): void {
    const maxPage = maxPageOf(err);
    const problem = problemOf(err, maxPage === 0 ? TOO_MANY_TYPES : REFUSED);
    this.loading.set(false);

    if (problem.status === 403 && problem.code === FORBIDDEN_CODE) {
      this.clearRows();
      this.error.set(FORBIDDEN);
    } else if (problem.status === 403 && problem.code !== null) {
      this.error.set(problem.message);
    } else if (problem.status === 400) {
      this.onListRefused(problem.message, maxPage, requestedPage, key);
    } else {
      this.error.set(UNREACHABLE);
      this.errorIsTransient = true;
    }
  }

  /** Nothing on screen: the API refuses the list as asked, so no row (nor a pushed one) may stand under it. */
  private clearRows(): void {
    this.sagas.set([]);
    this.totalCount.set(0);
    this.listRefused = true;
  }

  /**
   * A 400: the request cannot be served as asked. The scoped list's body names the last page its shape can
   * reach (`maxPage`): when that is below the page asked for, the list goes there and the pager stops
   * offering pages beyond it. Otherwise the rows on screen are the last good page's, and the page goes back
   * to it (never to 0), provided they were read under the filters, sort and page size now in the toolbar;
   * if not (the 400 is the answer to a change of those) they would contradict the toolbar, so they go, and
   * the page is 1. A `maxPage` of 0 says no page of this request can be served at all (too many visible
   * saga types), which is the same: no rows, page 1, and the message asks for a saga type filter.
   */
  private onListRefused(message: string, maxPage: number | null, requestedPage: number, key: string): void {
    this.error.set(message);

    if (maxPage !== null && maxPage >= 1 && maxPage < requestedPage) {
      this.pageCap.set(maxPage);
      this.page.set(maxPage);
      if (key !== this.lastGoodKey || this.lastGoodPage !== maxPage) this.refresh(message);
    } else if (maxPage === 0 || key !== this.lastGoodKey) {
      this.clearRows();
      this.page.set(1);
      this.pageCap.set(null);
      this.lastGoodPage = null;
      this.lastGoodKey = null;
    } else {
      this.page.set(this.lastGoodPage ?? 1);
    }
    this.syncUrlFromFilters(true);
  }

  private upsert(summary: SagaSummary): void {
    if (this.listRefused) return;
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
