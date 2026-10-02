import { Component, OnDestroy, OnInit, computed, signal } from '@angular/core';
import { ActivatedRoute, ParamMap, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubConnectionState, SagaHubService } from '../../services/saga-hub.service';
import { SagaDetail as SagaDetailModel, SagaLogEntry, SagaMap as SagaMapModel, SagaSummary } from '../../models/saga.model';
import { KindBadge } from '../../components/kind-badge/kind-badge';
import { StatusBadge } from '../../components/status-badge/status-badge';
import { SagaMap } from '../../components/saga-map/saga-map';
import { LocalTime } from '../../components/local-time/local-time';
import { SagaTimeline } from '../../components/saga-timeline/saga-timeline';
import { formatStateJson } from '../../util/state-json';
import { foldTimeline } from '../../util/saga-transitions';
import { timezoneLabel, toMillis } from '../../util/time-format';

type Tab = 'timeline' | 'data' | 'map';

/** The tabs a URL may name; the map is the default and is written as no `tab` at all. */
const URL_TABS: readonly Tab[] = ['timeline', 'data'];

/** `UTC+02:00` as of `iso` (a zone's offset changes with daylight saving); now when unparseable. */
function zoneAt(iso: string | null | undefined): string {
  const at = toMillis(iso);
  return timezoneLabel(at === null ? new Date() : new Date(at));
}

/** A timeline entry's sequence number as the URL may carry it: a positive safe integer, else null. */
function parseEntry(raw: string | null): number | null {
  if (raw === null || !/^\d+$/.test(raw)) return null;
  const value = Number(raw);
  return Number.isSafeInteger(value) && value > 0 ? value : null;
}

function isEntry(value: number): boolean {
  return Number.isSafeInteger(value) && value > 0;
}

@Component({
  selector: 'app-saga-detail',
  imports: [RouterLink, KindBadge, StatusBadge, SagaMap, LocalTime, SagaTimeline],
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
  /** The timeline entry the page is focused on (`?entry=`): the map shows the saga as of it and the
   *  timeline highlights its row. */
  readonly focusedSequence = signal<number | null>(null);
  /** The timeline steps whose data inspector is open. Held here rather than in the timeline, whose
   *  view goes while the map tab shows, so an inspector stays open across a jump to the map and
   *  back and across refreshes (step keys are sequence numbers); a saga change closes them all. */
  readonly openKeys = signal<ReadonlySet<number>>(new Set());
  /** Whether the viewer may see saga data. Always true until the permission wiring lands. */
  readonly canViewData = true;
  readonly retrying = signal(false);
  readonly retryMessage = signal<string | null>(null);
  /** Retry re-drives a real saga against real participants, so the button asks before it fires. */
  readonly confirmingRetry = signal(false);
  readonly connectionState = signal<SagaHubConnectionState>('disconnected');
  readonly hasEverConnected = signal(false);

  /** The timeline as steps, each with the state it committed (see saga-transitions). */
  readonly history = computed(() => foldTimeline(this.timeline()));
  /** A saga that can still change: its final step without an outcome is in progress. */
  readonly live = computed(() => {
    const status = this.detail()?.summary.status;
    return status === 'Running' || status === 'Compensating';
  });
  /** The viewer's zone at each summary time, for the "Created (UTC+02:00)" labels. */
  readonly createdZone = computed(() => zoneAt(this.detail()?.summary.createdAtUtc));
  readonly updatedZone = computed(() => zoneAt(this.detail()?.summary.updatedAtUtc));

  private subs: Subscription[] = [];
  /** Whether we've ever joined a hub group yet — guards the unsubscribe-previous-saga step below,
   *  and ngOnDestroy, from firing before there's anything to unsubscribe from. */
  private hasSubscribedToHub = false;
  /** The entry the URL last named, validated; what a saga change keeps (see the paramMap handler). */
  private urlEntry: number | null = null;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: SagaApiService,
    private readonly hub: SagaHubService,
  ) {}

  ngOnInit(): void {
    this.subs.push(
      // Before paramMap, so the first load already knows the tab and entry. Back and Forward land
      // here too; the echo of the page's own navigate() sets the signals to the values they hold.
      this.route.queryParamMap.subscribe((query) => this.readUrlState(query)),
      // The observable, not `.snapshot` — Angular reuses this component instance when navigating
      // between two routes matched by the same route config (e.g. a sibling-saga or sub-saga link),
      // so ngOnInit itself does not re-fire. Reading the snapshot once would freeze sagaType/
      // correlationId on whichever saga was loaded first.
      this.route.paramMap.subscribe((params) => {
        if (this.hasSubscribedToHub) {
          void this.hub.unsubscribeFromSaga(this.sagaType, this.correlationId);
          // A focus belongs to one saga's timeline. The router emits query params before params, so
          // the URL of the new saga has already been read: keep only the entry it names.
          this.focusedSequence.set(this.urlEntry);
          // Step keys are sequence numbers of another saga's timeline.
          this.openKeys.set(new Set());
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

  /**
   * The tab and entry from the URL, validated the way the list page reads its filters: `tab` only
   * names a tab other than the default map, `entry` only a positive safe integer; anything else is
   * the default.
   */
  private readUrlState(query: ParamMap): void {
    const tab = query.get('tab');
    this.tab.set(tab !== null && (URL_TABS as readonly string[]).includes(tab) ? (tab as Tab) : 'map');
    this.urlEntry = parseEntry(query.get('entry'));
    this.focusedSequence.set(this.urlEntry);
  }

  /**
   * Writes the tab and entry to the URL after the signals already changed, defaults as null so the
   * plain detail URL stays plain. A tab or entry change is a history step, so Back returns to where
   * the viewer came from; dropping the focus replaces the current step instead.
   */
  private syncUrl(replaceUrl = false): void {
    const tab = this.tab();
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { tab: tab === 'map' ? null : tab, entry: this.focusedSequence() },
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }

  setTab(tab: Tab): void {
    if (this.tab() === tab) return;
    this.tab.set(tab);
    this.syncUrl();
  }

  /** The map as of a timeline entry. A map fetched before the entry was recorded is fetched again. */
  showOnMap(sequence: number): void {
    // A row pushed live carries no sequence number yet: show the map, unfocused.
    const entry = isEntry(sequence) ? sequence : null;
    this.tab.set('map');
    this.focusedSequence.set(entry);
    this.syncUrl();
    const map = this.map();
    if (entry !== null && map && !map.events.some((e) => e.sequenceNumber === entry)) this.loadMap();
  }

  /** The timeline, scrolled to and focused on an entry (the map's "Back to this entry"). */
  showInTimeline(sequence: number): void {
    this.tab.set('timeline');
    this.focusedSequence.set(isEntry(sequence) ? sequence : null);
    this.syncUrl();
  }

  /** The viewer took over the map's replay: the focus goes, without a history step of its own. */
  clearFocus(): void {
    if (this.focusedSequence() === null) return;
    this.focusedSequence.set(null);
    this.syncUrl(true);
  }

  /** The saga's raw persisted state, pretty-printed with Kind and Status as names (see state-json). */
  get prettyDataJson(): string {
    return formatStateJson(this.detail()?.dataJson);
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
