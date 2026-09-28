import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubConnectionState, SagaHubService } from '../../services/saga-hub.service';
import { SagaDetail as SagaDetailModel, SagaLogEntry, SagaMap as SagaMapModel, SagaSummary } from '../../models/saga.model';
import { KindBadge } from '../../components/kind-badge/kind-badge';
import { StatusBadge } from '../../components/status-badge/status-badge';
import { SagaMap } from '../../components/saga-map/saga-map';

type Tab = 'timeline' | 'data' | 'map';

@Component({
  selector: 'app-saga-detail',
  imports: [CommonModule, RouterLink, KindBadge, StatusBadge, SagaMap],
  templateUrl: './saga-detail.html',
  styleUrl: './saga-detail.scss',
})
export class SagaDetail implements OnInit, OnDestroy {
  correlationId = '';
  sagaType = '';

  readonly detail = signal<SagaDetailModel | null>(null);
  readonly timeline = signal<SagaLogEntry[]>([]);
  readonly map = signal<SagaMapModel | null>(null);
  /** Other saga types tracking this same correlation id — empty for the usual one-saga case. */
  readonly related = signal<SagaSummary[]>([]);
  /** Sagas this one started via StartChildAsync — empty unless it composes sub-sagas. */
  readonly children = signal<SagaSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly tab = signal<Tab>('map');
  readonly retrying = signal(false);
  readonly retryMessage = signal<string | null>(null);
  /** Retry re-drives a real saga against real participants, so the button asks before it fires. */
  readonly confirmingRetry = signal(false);
  readonly connectionState = signal<SagaHubConnectionState>('disconnected');
  readonly hasEverConnected = signal(false);

  private subs: Subscription[] = [];
  /** Whether we've ever joined a hub group yet — guards the unsubscribe-previous-saga step below,
   *  and ngOnDestroy, from firing before there's anything to unsubscribe from. */
  private hasSubscribedToHub = false;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: SagaApiService,
    private readonly hub: SagaHubService,
  ) {}

  ngOnInit(): void {
    this.subs.push(
      // The observable, not `.snapshot` — Angular reuses this component instance when navigating
      // between two routes matched by the same route config (e.g. a sibling-saga or sub-saga link),
      // so ngOnInit itself does not re-fire. Reading the snapshot once would freeze sagaType/
      // correlationId on whichever saga was loaded first.
      this.route.paramMap.subscribe((params) => {
        if (this.hasSubscribedToHub) {
          void this.hub.unsubscribeFromSaga(this.sagaType, this.correlationId);
        }

        this.sagaType = params.get('sagaType') ?? '';
        this.correlationId = params.get('id') ?? '';
        this.load();

        // A malformed id (not a real saga, or a stray URL segment) still gets the REST 404 above --
        // "Could not load this saga". SagaHub.SubscribeToSaga parses its own correlationId argument
        // leniently server-side (see SagaHub.cs), so it's safe to call unconditionally here too: a
        // non-Guid id just joins no group instead of failing the RPC.
        void this.hub.subscribeToSaga(this.sagaType, this.correlationId);
        this.hasSubscribedToHub = true;
      }),
      this.hub.connectionState$.subscribe((s) => {
        // Captured before the error signal is touched by anything below -- a reconnect after an
        // ordinary first-ever connect (error() still null, nothing has failed yet) must not trigger
        // a redundant extra load() on top of the one ngOnInit already fired.
        const hadError = this.error() !== null;
        this.connectionState.set(s);
        if (s === 'connected') {
          this.hasEverConnected.set(true);
          // A prior REST load failure (e.g. the API was down on page load) leaves the error state
          // and stale/empty timeline/map/related/children on screen even after the hub reconnects
          // and live push updates resume -- reconnecting only proves the SignalR channel is back,
          // not that the failed GET requests have been retried. Re-run them now so both clear together.
          if (hadError) this.load();
        }
      }),
      this.hub.sagaUpdated$.subscribe((summary) => {
        // Both halves must match: the list group pushes updates for every saga, and another saga
        // type may be tracking this same correlation id.
        if (summary.correlationId === this.correlationId && summary.sagaType === this.sagaType) {
          this.detail.update((current) => (current ? { ...current, summary } : current));
          // Neither the map nor the timeline is pushed incrementally here (SagaChangePollingService
          // only ever emits SagaUpdated, never TimelineEntryAdded, across processes) — re-fetch them
          // whole instead.
          this.loadMap();
          this.loadTimeline();
          this.loadRelated();
          this.loadChildren();
        }
      }),
      this.hub.timelineEntryAdded$.subscribe(({ sagaType, correlationId, entry }) => {
        if (correlationId === this.correlationId && sagaType === this.sagaType) {
          this.timeline.update((entries) => [...entries, entry]);
        }
      }),
    );
  }

  ngOnDestroy(): void {
    if (this.hasSubscribedToHub) {
      void this.hub.unsubscribeFromSaga(this.sagaType, this.correlationId);
    }
    this.subs.forEach((s) => s.unsubscribe());
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    // Captured now, at the moment this call is fired — compared against the live fields when the
    // response arrives, below. Angular reuses this component instance across same-route-config
    // navigations (see ngOnInit), so an older, slower request can resolve after a newer one already
    // repainted the page for a different saga; without this guard its `next`/`error` callback would
    // silently overwrite the correctly-displayed newer saga with stale data.
    const sagaType = this.sagaType;
    const correlationId = this.correlationId;

    this.api.get(sagaType, correlationId).subscribe({
      next: (detail) => {
        if (sagaType !== this.sagaType || correlationId !== this.correlationId) return;
        this.detail.set(detail);
        this.loading.set(false);
      },
      error: () => {
        if (sagaType !== this.sagaType || correlationId !== this.correlationId) return;
        this.error.set('Could not load this saga. It may not exist.');
        this.loading.set(false);
      },
    });

    this.loadTimeline();
    this.loadMap();
    this.loadRelated();
    this.loadChildren();
  }

  private loadTimeline(): void {
    const sagaType = this.sagaType;
    const correlationId = this.correlationId;
    this.api.getTimeline(sagaType, correlationId).subscribe((entries) => {
      if (sagaType !== this.sagaType || correlationId !== this.correlationId) return;
      this.timeline.set(entries);
    });
  }

  /**
   * The sagas this one started as sub-sagas. A separate call from loadRelated because it answers a
   * different question: a child has its own correlation id, so it can never turn up in
   * /api/correlations/{id}. The "started by" direction needs no call at all — the parent pointer is
   * already on this saga's own summary.
   *
   * Same snapshot-not-live compromise as the related strip: a child's status changes are pushed to
   * its own hub group, not this page's, so this refreshes when the parent itself updates. Failures
   * are swallowed rather than blanking a detail page that is otherwise fine.
   */
  loadChildren(): void {
    const sagaType = this.sagaType;
    const correlationId = this.correlationId;
    this.api.getChildren(sagaType, correlationId).subscribe({
      next: (found) => {
        if (sagaType !== this.sagaType || correlationId !== this.correlationId) return;
        this.children.set(found);
      },
      error: () => {
        if (sagaType !== this.sagaType || correlationId !== this.correlationId) return;
        this.children.set([]);
      },
    });
  }

  /**
   * A correlation id can be tracked by more than one saga type — in the OrderProcessing sample,
   * OrderSaga and PostShipmentChoreography share one per order. This resolves the id to every
   * instance under it and drops this page's own, leaving just the siblings to link to.
   *
   * Snapshot rather than live: this page joins only its own instance's hub group, so a sibling's
   * status changes aren't pushed here. Refreshed whenever this saga itself updates, the same
   * compromise the map tab already makes. A failure is swallowed — a missing cross-link must not
   * take down a detail page that is otherwise fine.
   */
  loadRelated(): void {
    const sagaType = this.sagaType;
    const correlationId = this.correlationId;
    this.api.findByCorrelationId(correlationId).subscribe({
      next: (all) => {
        if (sagaType !== this.sagaType || correlationId !== this.correlationId) return;
        this.related.set(all.filter((s) => s.sagaType !== sagaType));
      },
      error: () => {
        if (sagaType !== this.sagaType || correlationId !== this.correlationId) return;
        this.related.set([]);
      },
    });
  }

  loadMap(): void {
    const sagaType = this.sagaType;
    const correlationId = this.correlationId;
    this.api.getMap(sagaType, correlationId).subscribe((map) => {
      if (sagaType !== this.sagaType || correlationId !== this.correlationId) return;
      this.map.set(map);
    });
  }

  setTab(tab: Tab): void {
    this.tab.set(tab);
  }

  /**
   * The saga's raw persisted state, pretty-printed. `Kind` and `Status` are serialized by the .NET
   * engine as their underlying enum ints ("Kind": 0, "Status": 2) rather than names — everywhere
   * else in this UI shows the string form, so this remaps those two fields for display only. The
   * persisted JSON itself, and anything already using string enums, is left untouched.
   */
  get prettyDataJson(): string {
    const json = this.detail()?.dataJson;
    if (!json) return '';

    // Index = C#'s SagaKind enum order.
    const kinds = ['Orchestrated', 'Choreographed'];
    // Index = C#'s SagaStatus enum order (mirrors STATUSES in the sibling saga-list.ts).
    const statuses = ['Running', 'Completed', 'Failed', 'Compensating', 'Compensated', 'TimedOut', 'Cancelled'];

    try {
      const parsed: unknown = JSON.parse(json);
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
        const obj = parsed as Record<string, unknown>;
        if (typeof obj['Kind'] === 'number') {
          obj['Kind'] = kinds[obj['Kind']] ?? obj['Kind'];
        }
        if (typeof obj['Status'] === 'number') {
          obj['Status'] = statuses[obj['Status']] ?? obj['Status'];
        }
      }
      return JSON.stringify(parsed, null, 2);
    } catch {
      return json;
    }
  }

  /**
   * The entry type as the timeline should read it. The engine logs one terminal entry for every
   * Finalize, so a saga that failed still ends on SagaCompleted — accurate about the lifecycle, but
   * on screen directly under a red "Failed" badge it reads as a contradiction. "SagaFinalized" says
   * the same thing without arguing with the status. Presentation only: the persisted SagaEntryType
   * member is unchanged, and its `toState` already carries the outcome the entry ended on.
   */
  entryTypeLabel(entryType: string): string {
    return entryType === 'SagaCompleted' ? 'SagaFinalized' : entryType;
  }

  askRetryConfirmation(): void {
    this.retryMessage.set(null);
    this.confirmingRetry.set(true);
  }

  cancelRetry(): void {
    this.confirmingRetry.set(false);
  }

  retry(): void {
    this.confirmingRetry.set(false);
    this.retrying.set(true);
    this.retryMessage.set(null);

    this.api.retry(this.sagaType, this.correlationId).subscribe({
      next: () => {
        this.retrying.set(false);
        this.retryMessage.set('Retry accepted — redriving the failed step.');
      },
      error: (err) => {
        this.retrying.set(false);
        this.retryMessage.set(err?.error?.error ?? 'Retry failed.');
      },
    });
  }
}
