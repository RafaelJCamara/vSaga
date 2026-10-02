import {
  Component,
  ElementRef,
  Injector,
  OnDestroy,
  OnInit,
  afterNextRender,
  computed,
  effect,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { ActivatedRoute, ParamMap, Router, RouterLink } from '@angular/router';
import { Subject, Subscription, auditTime } from 'rxjs';
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
import { KindBadge } from '../../components/kind-badge/kind-badge';
import { StatusBadge } from '../../components/status-badge/status-badge';
import { SagaMap } from '../../components/saga-map/saga-map';
import { LocalTime } from '../../components/local-time/local-time';
import { SagaTimeline } from '../../components/saga-timeline/saga-timeline';
import { DATA_VIEWS, DataView, SagaDataOverview } from '../../components/saga-data-overview/saga-data-overview';
import { PENDING_SNAPSHOT_MS, SagaHistory, foldTimeline, stepContaining } from '../../util/saga-transitions';
import { timezoneLabel, toMillis } from '../../util/time-format';

/**
 * How long live pushes are gathered into one refresh. One change reaches this page twice (the list
 * group and the instance group both push it), and a busy saga pushes several changes per step; each
 * refresh re-reads the whole timeline, so a burst costs one round of requests, not one per push.
 */
export const REFRESH_AUDIT_MS = 250;

/**
 * The delay of the one extra timeline fetch after a push-triggered refresh that found the final
 * step committed but its snapshot not appended yet: the poller can push between the engine's persist
 * and its StatePersisted append, and for a saga that has just finished no later push would come.
 */
export const SNAPSHOT_FOLLOW_UP_MS = 1500;

type Tab = 'map' | 'timeline';

/** The tabs a URL may name; the map is the default and is written as no `tab` at all. */
const URL_TABS: readonly Tab[] = ['timeline'];

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

/** The Saga data view a URL names: one of the three, else none. */
function parseDataView(raw: string | null): DataView | null {
  return raw !== null && (DATA_VIEWS as readonly string[]).includes(raw) ? (raw as DataView) : null;
}

function isEntry(value: number): boolean {
  return Number.isSafeInteger(value) && value > 0;
}

/** The statuses a dashboard retry accepts, and so the ones the page loads a retry plan for. */
function isRetryable(status: SagaStatus | undefined): boolean {
  return status === 'Failed' || status === 'TimedOut';
}

/**
 * Whether the final step's snapshot may still be on its way: the step has an outcome but no
 * snapshot, its newest row is younger than PENDING_SNAPSHOT_MS (either side of the browser's clock,
 * which can trail the API host's), and the saga records snapshots at all. Without that last test an
 * older saga, or one with snapshots switched off, would fetch again after every push.
 */
function awaitsSnapshot(history: SagaHistory, nowMs: number): boolean {
  const last = history.transitions.at(-1);
  if (!last || history.snapshotCount === 0) return false;
  if (last.outcome === 'in-flight' || last.snapshotState !== 'missing') return false;
  const at = toMillis(last.lastOccurredAtUtc);
  return at !== null && Math.abs(nowMs - at) < PENDING_SNAPSHOT_MS;
}

@Component({
  selector: 'app-saga-detail',
  imports: [RouterLink, KindBadge, StatusBadge, SagaMap, LocalTime, SagaTimeline, SagaDataOverview],
  templateUrl: './saga-detail.html',
  styleUrl: './saga-detail.scss',
})
export class SagaDetail implements OnInit, OnDestroy {
  correlationId = '';
  sagaType = '';

  readonly detail = signal<SagaDetailModel | null>(null);
  readonly timeline = signal<SagaLogEntry[]>([]);
  /** Whether `timeline` holds this saga's fetched timeline; until then the Saga data bar must not
   *  read an empty one as "nothing recorded". */
  readonly timelineLoaded = signal(false);
  /** Whether the last timeline fetch failed: an error with nothing loaded, a warning over stale rows. */
  readonly timelineError = signal(false);
  readonly map = signal<SagaMapModel | null>(null);
  /** Whether the last map fetch failed: an error with no map loaded, a warning over a stale one. */
  readonly mapError = signal(false);
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
  /** The Saga data view open under the summary card (`?data=`); null when none is. */
  readonly dataView = signal<DataView | null>(null);
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
  /**
   * What a retry would re-run, or why it cannot: loaded for a Failed or TimedOut saga, null
   * otherwise and until it arrives. It marks the failed step and words the retry confirmation.
   */
  readonly retryPlan = signal<SagaRetryPlan | null>(null);
  /** The viewer took over the map's replay, so the failed step no longer pins it. */
  private readonly failureFocusReleased = signal(false);
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

  /**
   * The entry the map opens on: the URL's, else, for a failed saga, its failure entry. The default
   * is never written to the URL, and once the viewer takes over the replay it stays released.
   */
  readonly mapFocus = computed(() => {
    const explicit = this.focusedSequence();
    if (explicit !== null || this.failureFocusReleased()) return explicit;
    return this.retryPlan()?.failureSequenceNumber ?? null;
  });

  /** The retry plan's entries, as the timeline marks them. */
  readonly failureSequence = computed(() => this.retryPlan()?.failureSequenceNumber ?? null);
  readonly replaySequence = computed(() => this.retryPlan()?.step?.sequenceNumber ?? null);

  /** Why the plan refuses a retry; null when it allows one or has not arrived. */
  readonly retryRefusal = computed(() => {
    const plan = this.retryPlan();
    return plan && !plan.retryable ? (plan.reason ?? 'This saga cannot be retried.') : null;
  });

  /** "Re-run step 1 (InvoiceIssued, Requested) for this saga only?", from the plan and the fold. */
  readonly retryPrompt = computed(() => {
    const step = this.retryPlan()?.step;
    if (!step) return 'Re-run the step that failed for this saga only?';
    const ordinal = stepContaining(this.history(), step.sequenceNumber)?.ordinal;
    const which = ordinal === undefined ? 'the step that failed' : `step ${ordinal}`;
    return `Re-run ${which} (${step.messageType}, ${step.fromState}) for this saga only?`;
  });

  /** The redrive is targeted at this saga type, but the message itself still goes out to everyone. */
  readonly retryAudience = computed(() => {
    const type = this.retryPlan()?.step?.messageType;
    return `Other services that consume ${type ?? 'that message'} still receive it.`;
  });

  /** Whether the summary card shows the retry row: only a Failed or TimedOut saga can be retried. */
  private readonly retryShown = computed(() => isRetryable(this.detail()?.summary.status));
  /** Where focus goes when the retry row swaps or drops the element that had it (see moveFocus). */
  private readonly retryRow = viewChild<ElementRef<HTMLElement>>('retryRow');
  private readonly retryButton = viewChild<ElementRef<HTMLElement>>('retryButton');
  private readonly retryCancelButton = viewChild<ElementRef<HTMLElement>>('retryCancelButton');
  private readonly sagaHeading = viewChild<ElementRef<HTMLElement>>('sagaHeading');

  private subs: Subscription[] = [];
  /** Whether we've ever joined a hub group yet — guards the unsubscribe-previous-saga step below,
   *  and ngOnDestroy, from firing before there's anything to unsubscribe from. */
  private hasSubscribedToHub = false;
  /** The entry the URL last named, validated; what a saga change keeps (see the paramMap handler). */
  private urlEntry: number | null = null;
  /** Live pushes for this saga; audited into one refresh per REFRESH_AUDIT_MS window. */
  private readonly refreshRequests = new Subject<void>();
  /** The pending snapshot follow-up fetch, if one is scheduled. */
  private followUp: ReturnType<typeof setTimeout> | null = null;
  /** The latest timeline and map fetches; an answer to an earlier one is dropped. */
  private timelineRequest = 0;
  private mapRequest = 0;
  private retryPlanRequest = 0;
  /** The status and version the current retry plan was asked for; null when none was. */
  private retryPlanFor: string | null = null;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: SagaApiService,
    private readonly hub: SagaHubService,
    private readonly injector: Injector,
  ) {
    // A retried saga runs again, and its status hides the retry row with the focused button in it.
    // The effect runs before the view drops the row, so it can still tell whether focus was there.
    effect(() => {
      if (this.retryShown()) return;
      const row = untracked(this.retryRow)?.nativeElement;
      if (row?.contains(document.activeElement)) this.moveFocus(false, this.sagaHeading);
    });
  }

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
          this.resetSagaContent();
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
        // Captured before anything below touches them -- the first-ever connect (nothing failed,
        // nothing missed yet) must not add requests on top of the load() ngOnInit already fired.
        const hadError = this.error() !== null;
        const hadTimelineError = this.timelineError();
        const hadMapError = this.mapError();
        this.connectionState.set(s);
        if (s === 'connected') {
          const wasConnectedBefore = this.hasEverConnected();
          this.hasEverConnected.set(true);
          // A prior REST load failure (e.g. the API was down on page load) leaves the error state
          // and stale/empty timeline/map/related/children on screen even after the hub reconnects
          // and live push updates resume -- reconnecting only proves the SignalR channel is back,
          // not that the failed GET requests have been retried. Re-run them now so both clear together.
          // Any later reconnect also re-reads everything once: the pushes sent while the hub was
          // down are lost, and a saga that finished meanwhile would never send another.
          if (hadError) {
            this.load();
          } else if (wasConnectedBefore || hadTimelineError || hadMapError) {
            this.refreshRequests.next();
          }
        }
      }),
      this.hub.sagaUpdated$.subscribe((summary) => {
        // Both halves must match: the list group pushes updates for every saga, and another saga
        // type may be tracking this same correlation id.
        if (summary.correlationId === this.correlationId && summary.sagaType === this.sagaType) {
          // The badge and the summary card follow at once; the rest waits for the refresh. A push
          // older than what is shown (the two hub groups can deliver out of order) changes nothing.
          this.detail.update((current) =>
            current && summary.version >= current.summary.version ? { ...current, summary } : current,
          );
          this.syncRetryPlan();
          this.refreshRequests.next();
        }
      }),
      // A pushed entry is never appended: it carries no payload and no error text (the API strips
      // them for everyone) and, from an older engine, no sequence number. It asks for the same
      // refresh instead, which reads the stored entries whole.
      this.hub.timelineEntryAdded$.subscribe(({ sagaType, correlationId }) => {
        if (correlationId === this.correlationId && sagaType === this.sagaType) {
          this.refreshRequests.next();
        }
      }),
      this.refreshRequests.pipe(auditTime(REFRESH_AUDIT_MS)).subscribe(() => this.refresh()),
    );
  }

  ngOnDestroy(): void {
    if (this.hasSubscribedToHub) {
      void this.hub.unsubscribeFromSaga(this.sagaType, this.correlationId);
    }
    this.subs.forEach((s) => s.unsubscribe());
    this.cancelSnapshotFollowUp();
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
        this.syncRetryPlan();
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

  /**
   * One coalesced live refresh: the detail (for the stored data and a summary at least as new as
   * the pushed one), the timeline, the map and both relation strips. It runs after a push or a hub
   * reconnect, and only such a refresh may schedule the snapshot follow-up.
   */
  private refresh(): void {
    this.refreshDetail();
    this.loadTimeline(() => this.scheduleSnapshotFollowUp());
    this.loadMap();
    this.loadRelated();
    this.loadChildren();
  }

  /**
   * Re-reads the detail behind a live refresh. Unlike load() it never shows "Loading…", and a
   * failure keeps what is shown. Two refreshes can cross, and a push may already have patched in a
   * newer summary, so the detail with the higher version wins; on a tie the response does, since it
   * also carries the stored data.
   */
  private refreshDetail(): void {
    const sagaType = this.sagaType;
    const correlationId = this.correlationId;
    this.api.get(sagaType, correlationId).subscribe({
      next: (fresh) => {
        if (sagaType !== this.sagaType || correlationId !== this.correlationId) return;
        this.detail.update((current) =>
          current && fresh.summary.version >= current.summary.version ? fresh : current,
        );
        this.syncRetryPlan();
      },
      error: () => undefined,
    });
  }

  /**
   * Keeps the retry plan in step with the summary shown: asked for once per status and version of a
   * Failed or TimedOut saga (a retry that fails again moves both), dropped for any other status. A
   * plan already shown stays until the new one arrives.
   */
  private syncRetryPlan(): void {
    const summary = this.detail()?.summary;
    if (!summary || !isRetryable(summary.status)) {
      this.retryPlanRequest++;
      this.retryPlanFor = null;
      this.retryPlan.set(null);
      return;
    }
    const key = `${summary.status}:${summary.version}`;
    if (key === this.retryPlanFor) return;
    this.retryPlanFor = key;
    this.loadRetryPlan();
  }

  /** Fetches the retry plan; only the answer to the latest fetch for this saga is applied. */
  private loadRetryPlan(): void {
    const sagaType = this.sagaType;
    const correlationId = this.correlationId;
    const request = ++this.retryPlanRequest;
    const stale = () => request !== this.retryPlanRequest || sagaType !== this.sagaType || correlationId !== this.correlationId;
    this.api.getRetryPlan(sagaType, correlationId).subscribe({
      next: (plan) => {
        if (!stale()) this.retryPlan.set(plan);
      },
      // Without a plan the retry still works (the API decides and says why not); the next refresh
      // asks again.
      error: () => {
        if (stale()) return;
        this.retryPlanFor = null;
        this.retryPlan.set(null);
      },
    });
  }

  /** The timeline tab's Try again. */
  retryTimeline(): void {
    this.loadTimeline();
  }

  /**
   * Fetches the timeline. Fetches can overlap (a refresh, its follow-up, a later refresh on a slow
   * API) and answer out of order, so only the answer to the latest one is applied; an older
   * timeline would otherwise replace a newer one, and for a saga that has finished no later push
   * would correct it.
   */
  private loadTimeline(onLoaded?: () => void): void {
    const sagaType = this.sagaType;
    const correlationId = this.correlationId;
    const request = ++this.timelineRequest;
    const stale = () => request !== this.timelineRequest || sagaType !== this.sagaType || correlationId !== this.correlationId;
    this.api.getTimeline(sagaType, correlationId).subscribe({
      next: (entries) => {
        if (stale()) return;
        this.timeline.set(entries);
        this.timelineLoaded.set(true);
        this.timelineError.set(false);
        onLoaded?.();
      },
      error: () => {
        if (stale()) return;
        this.timelineError.set(true);
      },
    });
  }

  /** One timeline fetch SNAPSHOT_FOLLOW_UP_MS from now, when the final step's snapshot may still land. */
  private scheduleSnapshotFollowUp(): void {
    this.cancelSnapshotFollowUp();
    if (!awaitsSnapshot(this.history(), Date.now())) return;
    this.followUp = setTimeout(() => {
      this.followUp = null;
      this.loadTimeline();
    }, SNAPSHOT_FOLLOW_UP_MS);
  }

  private cancelSnapshotFollowUp(): void {
    if (this.followUp === null) return;
    clearTimeout(this.followUp);
    this.followUp = null;
  }

  /** What belongs to one saga's page, cleared when the route moves to another saga. */
  private resetSagaContent(): void {
    this.cancelSnapshotFollowUp();
    this.timeline.set([]);
    this.timelineLoaded.set(false);
    this.timelineError.set(false);
    this.map.set(null);
    this.mapError.set(false);
    this.retryPlanRequest++;
    this.retryPlanFor = null;
    this.retryPlan.set(null);
    this.failureFocusReleased.set(false);
    this.confirmingRetry.set(false);
    this.retryMessage.set(null);
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

  /** Fetches the map; like loadTimeline, only the answer to the latest fetch is applied. */
  loadMap(): void {
    const sagaType = this.sagaType;
    const correlationId = this.correlationId;
    const request = ++this.mapRequest;
    const stale = () => request !== this.mapRequest || sagaType !== this.sagaType || correlationId !== this.correlationId;
    this.api.getMap(sagaType, correlationId).subscribe({
      next: (map) => {
        if (stale()) return;
        this.map.set(map);
        this.mapError.set(false);
      },
      error: () => {
        if (stale()) return;
        this.mapError.set(true);
      },
    });
  }

  /**
   * The tab, entry and data view from the URL, validated the way the list page reads its filters:
   * `tab` only names a tab other than the default map, `entry` only a positive safe integer, `data`
   * only one of the three Saga data views; anything else is the default.
   */
  private readUrlState(query: ParamMap): void {
    const tab = query.get('tab');
    this.tab.set(tab !== null && (URL_TABS as readonly string[]).includes(tab) ? (tab as Tab) : 'map');
    this.urlEntry = parseEntry(query.get('entry'));
    this.focusedSequence.set(this.urlEntry);
    this.dataView.set(parseDataView(query.get('data')));
  }

  /**
   * Writes the tab, entry and data view to the URL after the signals already changed, defaults as
   * null so the plain detail URL stays plain. A tab or entry change is a history step, so Back
   * returns to where the viewer came from; dropping the focus or changing the data view replaces the
   * current step instead.
   */
  private syncUrl(replaceUrl = false): void {
    const tab = this.tab();
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { tab: tab === 'map' ? null : tab, entry: this.focusedSequence(), data: this.dataView() },
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
    // Only a positive sequence number can be a focus; anything else (an unstamped 0, say) shows the
    // map, unfocused.
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

  /**
   * The viewer took over the map's replay: the focus goes, without a history step of its own, and
   * the failure entry does not take its place. A default focus on the failure was never in the URL.
   * The map reports every take-over, focused or not, so one made before the retry plan arrives (or
   * while the saga was still running) is not jumped onto the failure when the plan lands.
   */
  clearFocus(): void {
    this.failureFocusReleased.set(true);
    if (this.focusedSequence() === null) return;
    this.focusedSequence.set(null);
    this.syncUrl(true);
  }

  /** Opens a Saga data view, or closes it (null). Not a history step of its own: it replaces. */
  setDataView(view: DataView | null): void {
    if (this.dataView() === view) return;
    this.dataView.set(view);
    this.syncUrl(true);
  }

  askRetryConfirmation(): void {
    if (this.retryRefusal() !== null) return;
    this.retryMessage.set(null);
    this.confirmingRetry.set(true);
    // Cancel, not "Yes, retry": a held or repeated Enter must not run the retry it just asked about.
    this.moveFocus(false, this.retryCancelButton);
  }

  cancelRetry(): void {
    this.confirmingRetry.set(false);
    this.moveFocus(false, this.retryButton);
  }

  retry(): void {
    this.confirmingRetry.set(false);
    this.retrying.set(true);
    this.retryMessage.set(null);

    // The Retry button is disabled while the request runs, so it can take focus back only after;
    // when the saga already runs again the row is gone, and the heading above it takes focus.
    const settle = (message: string) => {
      this.retrying.set(false);
      this.retryMessage.set(message);
      this.moveFocus(true, this.retryButton, this.sagaHeading);
    };
    this.api.retry(this.sagaType, this.correlationId).subscribe({
      next: () => settle('Retry accepted — redriving the failed step.'),
      error: (err) => settle(err?.error?.error ?? 'Retry failed.'),
    });
  }

  /**
   * Focuses the first of `targets` the next render shows. The retry row swaps its buttons in and
   * out, and a removed button drops focus to the body, which sends the next Tab past the prompt to
   * whatever follows the summary card. With `onlyIfLost`, a viewer who moved on while the request
   * ran keeps the focus they chose.
   */
  private moveFocus(onlyIfLost: boolean, ...targets: Array<() => ElementRef<HTMLElement> | undefined>): void {
    afterNextRender(
      () => {
        const active = document.activeElement;
        if (onlyIfLost && active !== null && active !== document.body) return;
        targets.map((target) => target()).find((ref) => ref !== undefined)?.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }
}
