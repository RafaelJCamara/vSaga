import { Injectable, InjectionToken, Signal, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs';
import { GuideArea, GuideAreaId, GuidePermissionCheck, GuideRequest } from '../models/guide.model';
import { GUIDE_AREAS, guideAreaForPath, guideAreaOf } from './guide-areas';

/** The one browser storage key of guide mode, with the shape of `StoredGuide`. */
export const GUIDE_STORAGE_KEY = 'vsaga.guide';

/**
 * Where guide mode keeps its state: the browser's `localStorage`, or null where there is none (a browser
 * that blocks site data throws on the very access). With null the state stays in memory, for the life of
 * the page. Stored per origin, so per compose stack and not per user: a person who signs in as someone else
 * in the same browser keeps their Guide setting, and nothing about the session is kept here.
 */
export const GUIDE_STORAGE = new InjectionToken<Storage | null>('GUIDE_STORAGE', {
  providedIn: 'root',
  factory: () => {
    try {
      return globalThis.localStorage ?? null;
    } catch {
      return null;
    }
  },
});

/**
 * The session's answer to "may the user do this" for the tours: an area's `requires` and a step's `requires`.
 * `app.config.ts` provides it from `AuthService`; unprovided (a spec) it allows everything.
 */
export const GUIDE_PERMISSION_CHECK = new InjectionToken<GuidePermissionCheck>(
  'GUIDE_PERMISSION_CHECK',
  { providedIn: 'root', factory: () => () => true },
);

type SeenAreas = Partial<Record<GuideAreaId, number>>;

/** What is stored, as `{"v":1,"enabled":true,"seen":{"list":1},"hintDismissed":true}`. */
interface StoredGuide {
  v: 1;
  enabled: boolean;
  /** The version of each area's tour the user has been through. */
  seen: SeenAreas;
  hintDismissed: boolean;
}

const KNOWN_AREAS: readonly string[] = GUIDE_AREAS.map((a) => a.id);

/** Reads the stored state. Corrupt JSON, anything that is not the version this code writes, and a storage
 *  that throws all read as the defaults: Guide off, nothing seen, the hint still to show. A member of the
 *  right version that is malformed falls back on its own default. */
function readStored(storage: Storage | null): StoredGuide {
  const defaults: StoredGuide = { v: 1, enabled: false, seen: {}, hintDismissed: false };
  if (!storage) return defaults;
  let parsed: unknown;
  try {
    const raw = storage.getItem(GUIDE_STORAGE_KEY);
    if (raw === null) return defaults;
    parsed = JSON.parse(raw);
  } catch {
    return defaults;
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return defaults;
  const record = parsed as Record<string, unknown>;
  if (record['v'] !== 1) return defaults;

  const seen: SeenAreas = {};
  const stored = record['seen'];
  if (typeof stored === 'object' && stored !== null && !Array.isArray(stored)) {
    for (const [id, version] of Object.entries(stored)) {
      if (KNOWN_AREAS.includes(id) && Number.isInteger(version) && (version as number) > 0) {
        seen[id as GuideAreaId] = version as number;
      }
    }
  }
  return {
    v: 1,
    enabled: record['enabled'] === true,
    seen,
    hintDismissed: record['hintDismissed'] === true,
  };
}

/** The path of a URL: no query, no fragment. */
function pathOf(url: string): string {
  return url.split(/[?#]/, 1)[0];
}

/** The saga type of a detail page's path (`/sagas/:sagaType/:id`), decoded; null on any other page. */
function sagaTypeOf(path: string): string | null {
  const match = /^\/sagas\/([^/]+)\/[^/]+\/?$/.exec(path);
  if (!match) return null;
  try {
    return decodeURIComponent(match[1]);
  } catch {
    return match[1];
  }
}

/**
 * Guide mode's state and the rules for when an area explains itself. Eager, and small: the overlay that
 * draws a tour, and the tours' copy, are a lazy chunk that loads when Guide is on.
 *
 * Guide is off until the user switches it on. Switching it on starts the current page's area; while it is
 * on, an area explains itself the first time the user opens it (`seen[area]` is the version of the tour they
 * went through, so a tour whose version was bumped shows once more), and Replay repeats the current area.
 * An area is a page (the list, the administration pages: when its route is opened) or a part of the detail
 * page (the map, the timeline, the saga data, the retry row: when the page calls `areaShown`).
 *
 * The overlay answers to `request`: it waits for the area to be on screen, then calls `started`, and ends
 * with `ended` (with `remember` when the user finished or skipped the tour) or, if there was nothing to
 * show, `abandoned`. One tour runs at a time; an area announced meanwhile waits its turn, and a page
 * change or switching Guide off drops the queue.
 */
@Injectable({ providedIn: 'root' })
export class GuideService {
  private readonly router = inject(Router);
  private readonly storage = inject(GUIDE_STORAGE);
  private readonly check = inject(GUIDE_PERMISSION_CHECK);

  private readonly enabledState = signal(false);
  private readonly seenState = signal<SeenAreas>({});
  private readonly hintDismissedState = signal(false);
  private readonly pathState = signal('');
  /** The area Replay repeats: the page's own area, then whichever part of the page was shown last. */
  private readonly currentId = signal<GuideAreaId | null>(null);
  private readonly requestState = signal<GuideRequest | null>(null);
  private readonly runningState = signal<GuideAreaId | null>(null);
  /** Areas announced while a tour was running or asked for, in the order they came. */
  private pending: GuideAreaId[] = [];
  private nonce = 0;

  /** Whether Guide is on. The overlay loads when it first is. */
  readonly enabled: Signal<boolean> = this.enabledState.asReadonly();
  /** The path of the current page, without its query: the list's filters and sort change the query only,
   *  and that is not another page. */
  readonly page: Signal<string> = this.pathState.asReadonly();
  /** The saga type of the detail page being shown, for the checks of steps that are about its data. */
  readonly sagaType: Signal<string | null> = computed(() => sagaTypeOf(this.pathState()));
  /** The area of the page, as far as the session may see it: what Replay would repeat. Null on a page with
   *  no area, and for a session without the area's permission. */
  readonly area: Signal<GuideArea | null> = computed(() => {
    const id = this.currentId();
    const area = id === null ? undefined : guideAreaOf(id);
    return area && this.allowed(area) ? area : null;
  });
  /** The tour the overlay is asked to run, from the moment it is wanted until it starts or is abandoned. */
  readonly request: Signal<GuideRequest | null> = this.requestState.asReadonly();
  /** The area whose tour is on screen. */
  readonly running: Signal<GuideAreaId | null> = this.runningState.asReadonly();
  /** Whether "Replay tour" does something: Guide is on, the page has an area, and no tour is running or wanted. */
  readonly canReplay = computed(
    () =>
      this.enabledState() &&
      this.area() !== null &&
      this.runningState() === null &&
      this.requestState() === null,
  );
  /** Whether the one-time hint shows: Guide is off, the hint was not answered, and the page has an area to offer. */
  readonly showHint = computed(
    () => !this.enabledState() && !this.hintDismissedState() && this.area() !== null,
  );

  constructor() {
    const stored = readStored(this.storage);
    this.enabledState.set(stored.enabled);
    this.seenState.set(stored.seen);
    this.hintDismissedState.set(stored.hintDismissed);

    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe((event) => this.enterPage(pathOf(event.urlAfterRedirects)));
    // A service created after the first navigation finished (a spec; a lazily created page) starts from
    // the page the router is on.
    this.enterPage(pathOf(this.router.url));
  }

  /** Whether the user has been through this version of the area's tour. */
  isSeen(id: GuideAreaId): boolean {
    const area = guideAreaOf(id);
    return area !== undefined && this.seenState()[id] === area.version;
  }

  /**
   * Switches Guide on or off. On: everything counts as unseen again (a person who switches Guide on wants the
   * walkthrough, whatever an earlier session of it covered), the hint is answered, and the current page's
   * area starts. Off: whatever is wanted or queued is dropped; the overlay ends a tour it is showing,
   * without remembering it.
   */
  setEnabled(on: boolean): void {
    if (on === this.enabledState()) return;
    this.enabledState.set(on);
    this.pending = [];
    if (on) {
      this.hintDismissedState.set(true);
      this.seenState.set({});
      this.save();
      const area = this.area();
      if (area) this.enqueue(area);
    } else {
      this.requestState.set(null);
      this.save();
    }
  }

  toggle(): void {
    this.setEnabled(!this.enabledState());
  }

  /** Runs the current area's tour again, seen or not. Only when `canReplay`. */
  replay(): void {
    const area = this.area();
    if (!this.canReplay() || !area) return;
    this.requestState.set({ area, nonce: ++this.nonce });
  }

  /** The user answered the hint (either way) without switching Guide on. */
  dismissHint(): void {
    if (this.hintDismissedState()) return;
    this.hintDismissedState.set(true);
    this.save();
  }

  /**
   * A page tells the guide that a part of it is on screen: the map or timeline tab, a step inspector or the
   * saga data bar, the retry row. It becomes the area Replay repeats; and with Guide on, if the user has not
   * been through its tour it is asked for (after any tour that is running). Call it when the part shows, not
   * when it is created: a tab that was hidden announces itself each time it is shown, and an area that was
   * explained once is not explained again.
   *
   * Call it from an effect or `afterNextRender`, never from a constructor or `ngOnInit`: a page change
   * resets what the guide knows about the page when the navigation ends (`NavigationEnd`), and a component
   * is created before that, so an announcement made while it is being created is wiped.
   */
  areaShown(id: GuideAreaId): void {
    const area = guideAreaOf(id);
    if (!area || !this.allowed(area)) return;
    this.currentId.set(id);
    if (this.enabledState() && !this.isSeen(id)) this.enqueue(area);
  }

  /** The overlay found the area on screen and is showing its tour. */
  started(id: GuideAreaId): void {
    this.requestState.set(null);
    this.runningState.set(id);
    this.pending = this.pending.filter((p) => p !== id);
  }

  /**
   * The tour ended. `remember` (the user went through it, or skipped it on purpose) stores the area's version
   * as seen and lets the next queued area start; ending because Guide was switched off, the page changed or the
   * session lost the area is not remembered.
   */
  ended(id: GuideAreaId, remember: boolean): void {
    if (this.runningState() !== id) return;
    this.runningState.set(null);
    if (!remember) return;
    const area = guideAreaOf(id);
    if (area) {
      this.seenState.update((seen) => ({ ...seen, [id]: area.version }));
      this.save();
    }
    this.advance();
  }

  /** The overlay gave up on the request: the area never came on screen, or it has no steps for this user. */
  abandoned(): void {
    this.requestState.set(null);
    this.advance();
  }

  /** The permission of an area, for the saga type of the page where it is about one. */
  private allowed(area: GuideArea): boolean {
    return this.check(area.requires, this.sagaType() ?? undefined);
  }

  private enterPage(path: string): void {
    if (path === this.pathState()) return;
    this.pathState.set(path);
    // The tour that was on screen belongs to the page that was left: the overlay ends it.
    this.runningState.set(null);
    this.requestState.set(null);
    this.pending = [];
    const area = guideAreaForPath(path);
    this.currentId.set(area?.id ?? null);
    if (area && this.enabledState() && this.allowed(area) && !this.isSeen(area.id)) {
      this.enqueue(area);
    }
  }

  /** Asks for the area now, or queues it behind the tour that is running or already asked for. */
  private enqueue(area: GuideArea): void {
    // The tour on screen is not asked for again: it is not seen until it ends, so it would run a second time.
    if (this.runningState() === area.id) return;
    if (this.requestState() !== null || this.runningState() !== null) {
      if (this.requestState()?.area.id !== area.id && !this.pending.includes(area.id)) {
        this.pending.push(area.id);
      }
      return;
    }
    this.requestState.set({ area, nonce: ++this.nonce });
  }

  /** Starts the next queued area that is still unseen and that the session may still see. (Switching Guide off
   *  or changing page empties the queue; a seen area is queued only through a race the guard in `enqueue`
   *  closes, and is skipped here all the same.) */
  private advance(): void {
    while (this.pending.length > 0) {
      const area = guideAreaOf(this.pending.shift() as GuideAreaId);
      if (area && !this.isSeen(area.id) && this.allowed(area)) {
        this.requestState.set({ area, nonce: ++this.nonce });
        return;
      }
    }
  }

  private save(): void {
    if (!this.storage) return;
    const stored: StoredGuide = {
      v: 1,
      enabled: this.enabledState(),
      seen: this.seenState(),
      hintDismissed: this.hintDismissedState(),
    };
    try {
      this.storage.setItem(GUIDE_STORAGE_KEY, JSON.stringify(stored));
    } catch {
      // Storage refused (full, blocked): the state stays in memory for this page.
    }
  }
}
