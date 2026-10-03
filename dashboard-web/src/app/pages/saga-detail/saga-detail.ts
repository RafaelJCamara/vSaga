import {
  ChangeDetectionStrategy,
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
import { AuthService } from '../../services/auth.service';
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
import { FORBIDDEN_CODE, problemOf } from '../../util/http-error';
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
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './saga-detail.scss',
})
export class SagaDetail implements OnInit, OnDestroy {
  correlationId = '';
  /** The routed saga type, kept in a signal behind a plain property so the permission checks below
   *  follow a route reuse (a sibling or sub-saga link keeps this instance and changes the type). */
  private readonly sagaTypeState = signal('');
  get sagaType(): string {
    return this.sagaTypeState();
  }
  set sagaType(value: string) {
    this.sagaTypeState.set(value);
  }

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
  /** The API answered 403 for this saga: the viewer holds no `sagas.view` for its type. Not an error (no
   *  banner), and a reconnect or a push asks the API for nothing, only the session (see `refresh`); it ends
   *  when the session gains the permission. */
  readonly forbidden = signal(false);
  /** What the session lets the viewer do with this saga's type. Computed from the auth service, so a
   *  refreshed session (after a 403, or when the tab is shown again) changes the page without a reload. */
  readonly canView = computed(() => this.auth.can('sagas.view', this.sagaType));
  /** False while the session is ending (sign-out clears the access at once, and the page stays until the
   *  navigation): the page then shows nothing, not no-access states that would be a lie. */
  readonly signedIn = computed(() => this.auth.isAuthenticated());
  readonly canRetry = computed(() => this.auth.can('sagas.retry', this.sagaType));
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
  /** Whether the viewer may see saga data (`sagas.data` for this type): the data bar and the timeline
   *  take it as their `canViewData` input. The API redacts payloads, state and error text either way. */
  readonly canViewData = computed(() => this.auth.can('sagas.data', this.sagaType));
  /** Whether the viewer may follow the "Started by" link: the parent is a saga of another type. */
  readonly canViewParent = computed(() => {
    const parentType = this.detail()?.summary.parentSagaType;
    return !!parentType && this.auth.can('sagas.view', parentType);
  });
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

  /** Whether the saga is in a status a retry accepts: only a Failed or TimedOut one. */
  readonly retryable = computed(() => isRetryable(this.detail()?.summary.status));
  /** Whether the summary card shows the retry row: a retryable saga, to a viewer holding `sagas.retry`
   *  for its type (the others read why they cannot instead). */
  readonly retryShown = computed(() => this.retryable() && this.canRetry());
  /** Where focus goes when the retry row swaps or drops the element that had it (see moveFocus). */
  private readonly retryRow = viewChild<ElementRef<HTMLElement>>('retryRow');
  private readonly retryButton = viewChild<ElementRef<HTMLElement>>('retryButton');
  private readonly retryCancelButton = viewChild<ElementRef<HTMLElement>>('retryCancelButton');
  private readonly sagaHeading = viewChild<ElementRef<HTMLElement>>('sagaHeading');

  private subs: Subscription[] = [];
  /** Whether the route has emitted yet — a later emission is the same instance reused for another saga,
   *  whose predecessor's content is dropped. */
  private hasRouted = false;
  /** Whether this saga's hub group is joined (see `subscribe`). */
  private subscribed = false;
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
  /** The detail requests sent so far, numbered in the order they were sent, and the highest-numbered one
   *  answered with a detail: a 403 to a request older than that says nothing about the session now (an
   *  older success that arrives late does not make a newer, genuine 403 stale: it only raises this to its
   *  own number). */
  private detailRequest = 0;
  private detailApplied = 0;
  /** Set first in `ngOnDestroy`: an answer that arrives after it must not join the hub group again. */
  private destroyed = false;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: SagaApiService,
    private readonly hub: SagaHubService,
    private readonly auth: AuthService,
    private readonly injector: Injector,
  ) {
    // A retried saga runs again, and its status hides the retry row with the focused button in it.
    // The effect runs before the view drops the row, so it can still tell whether focus was there.
    effect(() => {
      if (this.retryShown()) return;
      const row = untracked(this.retryRow)?.nativeElement;
      if (row?.contains(document.activeElement)) this.moveFocus(false, this.sagaHeading);
    });

    // The session gained `sagas.view` for this type while the page was open (the interceptor refreshes it
    // after a 403, and it is read again when the tab is shown): a page that was forbidden asks again, and
    // a Failed saga loaded under a session that could not yet see it gets its retry plan. A gain of
    // `sagas.data` reads the detail, the timeline and the map again, since what was loaded was redacted.
    // Only a gain counts for the same saga type: the first run, and every run after the route moved to
    // another type, only record where the session stands (a scoped session is judged for the type that
    // was routed, and a different saga is loaded anew anyway). Losing a permission needs no code here:
    // the page follows through its computed signals, and every refresh asks the API again, whose 403
    // forbids the page.
    let seenType: string | undefined;
    let couldView = false;
    let couldViewData = false;
    effect(() => {
      const type = this.sagaType;
      const mayView = this.canView();
      const mayViewData = this.canViewData();
      const sameType = type === seenType;
      const gainedView = sameType && mayView && !couldView;
      const gainedData = sameType && mayViewData && !couldViewData;
      seenType = type;
      couldView = mayView;
      couldViewData = mayViewData;
      if (!gainedView && !gainedData) return;
      untracked(() => {
        if (this.forbidden()) {
          if (gainedView) this.load();
        } else if (!this.loading()) {
          if (gainedView) this.syncRetryPlan();
          if (gainedData) this.reloadRedacted();
        }
      });
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
        if (this.hasRouted) {
          this.unsubscribe();
          // A focus belongs to one saga's timeline. The router emits query params before params, so
          // the URL of the new saga has already been read: keep only the entry it names.
          this.focusedSequence.set(this.urlEntry);
          // Step keys are sequence numbers of another saga's timeline.
          this.openKeys.set(new Set());
          this.resetSagaContent();
        }

        this.hasRouted = true;
        this.sagaType = params.get('sagaType') ?? '';
        this.correlationId = params.get('id') ?? '';
        this.load();

        // A viewer the session says cannot see this type would only have the hub negotiate, open its
        // socket and refuse the subscription: it waits for the API to let the page in (see `subscribe`).
        // The load above is answered asynchronously, so the page cannot be forbidden yet.
        if (this.canView()) this.subscribe();
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
          // The server closes a user's connection when their access changes, so a later connect is also
          // the cue to read the session: a grant that widened what the viewer may do (retry, data) is then
          // picked up without a reload, and the effects that follow a gained permission fire. A forbidden
          // page's refresh() reads it already. The read is single-flight and bounded, and adopting a
          // session never reconnects the hub, so a reconnect cannot start a loop.
          if (wasConnectedBefore && !this.forbidden()) void this.auth.refresh();
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
    this.destroyed = true;
    this.unsubscribe();
    this.subs.forEach((s) => s.unsubscribe());
    this.cancelSnapshotFollowUp();
  }

  /**
   * Joins this saga's hub group, once. A malformed id (not a real saga, or a stray URL segment) still gets
   * the REST 404 from the load: SagaHub.SubscribeToSaga parses its own correlationId argument leniently
   * server-side (see SagaHub.cs), so a non-Guid id just joins no group instead of failing the RPC.
   * Done when the session says the viewer may see the type, and when the detail is answered (the API let
   * the page in whatever the session said); never for a forbidden page, which would only have the hub
   * refuse it again on every reconnect.
   */
  private subscribe(): void {
    if (this.destroyed || this.subscribed) return;
    this.subscribed = true;
    void this.hub.subscribeToSaga(this.sagaType, this.correlationId);
  }

  private unsubscribe(): void {
    if (!this.subscribed) return;
    this.subscribed = false;
    void this.hub.unsubscribeFromSaga(this.sagaType, this.correlationId);
  }

  /**
   * The API refused the page: nothing to show, and no live updates to ask the hub for. Two limits stay,
   * both in the hub service, which this page does not change: `subscribeToSaga` writes its record only
   * after it has awaited the connection, so an unsubscribe that comes before the very first start
   * finishes is overtaken by it and leaves a record that every reconnect re-sends (a fix belongs in the
   * service, which could drop a record whose unsubscribe came first; chaining here would make every
   * unsubscribe asynchronous). And a page opened by URL straight into a 403 never starts the hub
   * (`subscribe` is not called), so nothing closes a connection to tell it about a grant: it recovers when
   * the session is read again (the tab shown, a 403), not at once.
   */
  private forbid(): void {
    this.forbidden.set(true);
    this.unsubscribe();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.forbidden.set(false);

    // Captured now, at the moment this call is fired — compared against the live fields when the
    // response arrives, below. Angular reuses this component instance across same-route-config
    // navigations (see ngOnInit), so an older, slower request can resolve after a newer one already
    // repainted the page for a different saga; without this guard its `next`/`error` callback would
    // silently overwrite the correctly-displayed newer saga with stale data.
    const sagaType = this.sagaType;
    const correlationId = this.correlationId;

    // A session without `sagas.view` for this type asks for the detail alone: the API decides, and
    // answers 403 (or, for a session that is out of date, 200, and then the rest is loaded).
    const sessionMayView = this.canView();

    const request = ++this.detailRequest;
    this.api.get(sagaType, correlationId).subscribe({
      next: (detail) => {
        if (sagaType !== this.sagaType || correlationId !== this.correlationId) return;
        this.detailApplied = Math.max(this.detailApplied, request);
        this.detail.set(detail);
        this.loading.set(false);
        this.forbidden.set(false);
        this.subscribe();
        this.syncRetryPlan();
        if (!sessionMayView) this.loadParts();
      },
      error: (err) => {
        if (sagaType !== this.sagaType || correlationId !== this.correlationId) return;
        // An answer to a request older than one that has been answered with a detail says nothing about
        // the session now.
        if (request < this.detailApplied) return;
        this.loading.set(false);
        const problem = problemOf(err, 'Could not load this saga. It may not exist.');
        if (problem.status === 403 && problem.code === FORBIDDEN_CODE) this.forbid();
        // Another 403 says its own reason (a password to change); anything else is a saga that cannot be had.
        else if (problem.status === 403 && problem.code !== null) this.error.set(problem.message);
        else this.error.set('Could not load this saga. It may not exist.');
      },
    });

    if (sessionMayView) this.loadParts();
  }

  /** The timeline, the map and both relation strips: what a saga's page holds beside its detail. */
  private loadParts(): void {
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
    // A forbidden page has nothing to refresh, but the connection that came back may be the server's
    // answer to an access change: read the session, and the page asks again if it now holds the permission.
    if (this.forbidden()) {
      void this.auth.refresh();
      return;
    }
    const parts = () => {
      this.loadTimeline(() => this.scheduleSnapshotFollowUp());
      this.loadMap();
      this.loadRelated();
      this.loadChildren();
    };
    // Four more requests that would all answer 403 are not worth sending when the session has just lost
    // the permission: the detail answers first, and the rest follows only when it is let through.
    if (this.canView()) {
      this.refreshDetail();
      parts();
    } else {
      this.refreshDetail(parts);
    }
  }

  /** What a gain of `sagas.data` makes stale: the stored state, the timeline's payloads and the map's error text. */
  private reloadRedacted(): void {
    this.refreshDetail();
    this.loadTimeline();
    this.loadMap();
  }

  /**
   * Re-reads the detail behind a live refresh. Unlike load() it never shows "Loading…", and a
   * failure keeps what is shown. Two refreshes can cross, and a push may already have patched in a
   * newer summary, so the detail with the higher version wins; on a tie the response does, since it
   * also carries the stored data. A 403 from the API is the one failure that is shown: the viewer lost
   * `sagas.view` (the hub closes their connection when access changes, so the reconnect's refresh finds
   * out), unless a request sent after this one has been answered with a detail. `onLoaded` runs after a success.
   */
  private refreshDetail(onLoaded?: () => void): void {
    const sagaType = this.sagaType;
    const correlationId = this.correlationId;
    const request = ++this.detailRequest;
    this.api.get(sagaType, correlationId).subscribe({
      next: (fresh) => {
        if (sagaType !== this.sagaType || correlationId !== this.correlationId) return;
        this.detailApplied = Math.max(this.detailApplied, request);
        this.detail.update((current) =>
          current && fresh.summary.version >= current.summary.version ? fresh : current,
        );
        this.syncRetryPlan();
        onLoaded?.();
      },
      error: (err) => {
        if (sagaType !== this.sagaType || correlationId !== this.correlationId) return;
        if (request < this.detailApplied) return;
        const problem = problemOf(err, '');
        if (problem.status === 403 && problem.code === FORBIDDEN_CODE) this.forbid();
      },
    });
  }

  /**
   * Keeps the retry plan in step with the summary shown: asked for once per status and version of a
   * Failed or TimedOut saga (a retry that fails again moves both), dropped for any other status or a
   * session without `sagas.view` for the type (the plan needs that and no more: it marks the failed
   * step for every viewer, while only `sagas.retry` shows the button). A plan already shown stays until
   * the new one arrives.
   */
  private syncRetryPlan(): void {
    const summary = this.detail()?.summary;
    if (!summary || !isRetryable(summary.status) || !this.canView()) {
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
      // The API's own words: a 409 and a 422 say why the saga cannot be retried, a 403 which permission is missing.
      error: (err) => settle(problemOf(err, 'Retry failed.').message),
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
