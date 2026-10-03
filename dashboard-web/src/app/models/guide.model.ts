import { PermissionKey } from './auth.model';

/**
 * The areas of the dashboard that explain themselves in guide mode, each a short tour of its own: the saga
 * list, the detail page's summary, its map, its timeline, the saga data, the retry row, and the
 * administration pages.
 */
export type GuideAreaId = 'list' | 'summary' | 'map' | 'timeline' | 'data' | 'retry' | 'admin';

/** The side of the highlighted element a step's popover prefers (it takes another when that one does not fit). */
export type GuidePlacement = 'bottom' | 'top' | 'right' | 'left';

/**
 * What starts an area while Guide is on. A `route` area starts when the user opens the page (the path,
 * without its query, matches); a `shown` area starts when the page announces it (`GuideService.areaShown`):
 * `what` says in words which moment that is, for whoever wires the page.
 */
export type GuideTrigger = { on: 'route'; match: RegExp } | { on: 'shown'; what: string };

/**
 * One area. `version` is bumped when the area's tour changes, which shows the changed tour once more to
 * everyone who had seen the old one. `readyAnchor` is the `data-tour` anchor whose presence means the area
 * is on screen; `requires` is the permission the area is about (a user without it never gets the area);
 * `docsAnchor` is the heading of `docs/dashboard-guide.md` the tour's "User guide" link opens.
 *
 * `A` is the anchor vocabulary: the area table is checked against `GUIDE_ANCHORS` by the compiler.
 */
export interface GuideArea<A extends string = string> {
  id: GuideAreaId;
  version: number;
  trigger: GuideTrigger;
  readyAnchor: A;
  requires: PermissionKey;
  docsAnchor: string;
}

/**
 * One step of a tour. `anchor` is the `data-tour` value of the element it highlights (null: a centred
 * popover by design). `fallbackAnchor` is highlighted when the anchor is not on the page; `reveal` names a
 * control that is clicked to bring the anchor on to the page (a tab). A step whose permission fails, or whose
 * anchor, reveal control and fallback are all missing when the tour begins, is left out, so "Step n of m"
 * counts what is shown. Only an anchor that disappears while the tour runs gives a centred popover.
 */
export interface GuideStep<A extends string = string> {
  id: string;
  title: string;
  body: string;
  anchor: A | null;
  fallbackAnchor?: A;
  reveal?: A;
  /** The permission the step explains; for the page's saga type where the area is about one. */
  requires?: PermissionKey;
  placement?: GuidePlacement;
}

/**
 * Whether the session holds `permission` for `sagaType`; without a saga type, whether it holds it for at
 * least one (a page that is not about one saga type, the saga list). Provided from `AuthService` in
 * `app.config.ts`; unprovided, it allows everything.
 */
export type GuidePermissionCheck = (permission: PermissionKey, sagaType?: string) => boolean;

/** A tour the service asks the overlay to run. A new `nonce` makes a repeated request (Replay) a new one. */
export interface GuideRequest {
  area: GuideArea;
  nonce: number;
}
