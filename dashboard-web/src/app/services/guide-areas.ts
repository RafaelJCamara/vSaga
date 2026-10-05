import type { GuideAnchor } from '../components/guide-overlay/guide-tours';
import { GuideArea, GuideAreaId } from '../models/guide.model';

/**
 * The dashboard's user guide on GitHub's `main` branch (the base the package READMEs link with). One
 * constant: the toggle's "User guide" link and every tour's own link are built from it, and each area's
 * `docsAnchor` is a heading of that document.
 */
export const USER_GUIDE_URL =
  'https://github.com/RafaelJCamara/vSaga/blob/main/docs/dashboard-guide.md';

/**
 * The areas that explain themselves, one row each. The list starts when its route is opened and the detail
 * page's summary when the detail page is; the map, timeline, data and retry areas start when their page
 * announces them (`GuideService.areaShown`), because they are parts of one page that show at different
 * moments. So does the administration area: its shell announces it once its pages show, not when the route
 * opens, because the tour points at a table that is there only after the shell has read everything. Where
 * each area's steps are is `guide-tours.ts`; every area has a tour (a spec pins it), so an area that is
 * offered (Replay, the hint) is one that can be shown.
 *
 * `version` is bumped with the area's tour copy: `seen[area] = version` is what is stored, so a changed
 * tour shows once more to everyone who saw the old one. The type import below is erased: the tour copy
 * and the anchor vocabulary stay in the lazy chunk of the overlay.
 */
export const GUIDE_AREAS: readonly GuideArea<GuideAnchor>[] = [
  {
    id: 'list',
    version: 1,
    trigger: { on: 'route', match: /^\/sagas\/?$/ },
    readyAnchor: 'list-table',
    requires: 'sagas.view',
    docsAnchor: '#the-saga-list',
  },
  {
    id: 'summary',
    version: 1,
    trigger: { on: 'route', match: /^\/sagas\/[^/]+\/[^/]+\/?$/ },
    readyAnchor: 'detail-summary',
    requires: 'sagas.view',
    docsAnchor: '#the-saga-detail-page',
  },
  {
    id: 'map',
    version: 1,
    trigger: { on: 'shown', what: 'the Map tab is shown' },
    readyAnchor: 'map-canvas',
    requires: 'sagas.view',
    docsAnchor: '#map',
  },
  {
    id: 'timeline',
    version: 1,
    trigger: { on: 'shown', what: 'the Timeline tab is shown' },
    readyAnchor: 'timeline',
    requires: 'sagas.view',
    docsAnchor: '#timeline',
  },
  {
    id: 'data',
    version: 1,
    trigger: { on: 'shown', what: 'a step inspector or the Saga data bar is opened' },
    readyAnchor: 'detail-data',
    requires: 'sagas.data',
    docsAnchor: '#saga-data',
  },
  {
    id: 'retry',
    version: 1,
    trigger: { on: 'shown', what: 'the retry row is shown on a Failed or TimedOut saga' },
    readyAnchor: 'detail-retry',
    requires: 'sagas.retry',
    docsAnchor: '#retrying-a-saga',
  },
  {
    id: 'admin',
    version: 1,
    trigger: { on: 'shown', what: 'the administration pages are shown' },
    readyAnchor: 'admin-nav',
    requires: 'access.manage',
    docsAnchor: '#administration',
  },
];

/** The area with this id (every id of `GuideAreaId` has a row: the service spec pins it). */
export function guideAreaOf(id: GuideAreaId): GuideArea<GuideAnchor> | undefined {
  return GUIDE_AREAS.find((a) => a.id === id);
}

/** The route area for a page's path (no query, no fragment), or null when the page has none. */
export function guideAreaForPath(path: string): GuideArea<GuideAnchor> | null {
  return GUIDE_AREAS.find((a) => a.trigger.on === 'route' && a.trigger.match.test(path)) ?? null;
}
