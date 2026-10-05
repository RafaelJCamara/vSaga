// THE RULE: a change to the dashboard UI updates the user guide (docs/dashboard-guide.md) and the tours in
// this file in the same change. A label, a control, a banner or a behaviour that a step names but the page no
// longer has makes the tour wrong, and nothing but this rule notices (a missing anchor is caught by the
// anchor-contract spec of each page, a stale sentence by nobody). When a step's copy changes, bump the
// `version` of its area in `services/guide-areas.ts`: everyone who saw the old tour sees the new one once.

import { GuideAreaId, GuideStep } from '../../models/guide.model';

/**
 * The one vocabulary of `data-tour` anchors: a template writes one of these as the attribute's value, and a
 * step, a fallback, a reveal control or an area's ready anchor names one. Each page's spec has an
 * anchor-contract case, and `guide-tours.spec.ts` checks every name used here against this list, so a
 * renamed or removed anchor fails a test instead of silently turning a step into a centred popover.
 *
 * Only the guide commits add `data-tour` attributes, and only with these names.
 */
export const GUIDE_ANCHORS = [
  // The top bar.
  'topbar-guide',
  // The saga list.
  'list-filters',
  'list-table',
  'list-sort',
  'list-row',
  'list-pagination',
  // The saga detail page.
  'detail-summary',
  'detail-data',
  'detail-retry',
  'detail-tab-map',
  'detail-tab-timeline',
  'map-canvas',
  'map-controls',
  'timeline',
  'timeline-entry',
  'timeline-step-data',
  // The administration area.
  'admin-nav',
  'admin-nav-users',
  'admin-nav-teams',
  'admin-nav-roles',
  'admin-list',
] as const;

export type GuideAnchor = (typeof GUIDE_ANCHORS)[number];

/**
 * The steps of every area's tour. An area with no steps has no tour: a request for it ends at once and
 * nothing is remembered.
 *
 * Copy is written against the shipped labels. Keep a title short (it is a heading) and a body to a few
 * sentences: the popover is a glance, and the user guide is where the detail lives.
 */
export const GUIDE_TOURS: Record<GuideAreaId, readonly GuideStep<GuideAnchor>[]> = {
  list: [
    {
      id: 'list-welcome',
      title: 'Welcome to the saga dashboard',
      body: 'This page lists the sagas your account can see, across all saga types. The tour takes about a minute. Press Esc to leave it; Guide in the top bar brings it back.',
      anchor: null,
    },
    {
      id: 'list-filters',
      title: 'Narrow the list',
      body: 'Filter by status, kind or saga type, or search by saga type or correlation id. Filters, sort and page live in the address bar, so a view can be bookmarked or shared.',
      anchor: 'list-filters',
    },
    {
      id: 'list-table',
      title: 'One row per saga instance',
      body: "Each row shows a saga's correlation id (shortened), type, kind, current state, status and last update. Rows update live; a banner appears if live updates disconnect.",
      anchor: 'list-table',
    },
    {
      id: 'list-sort',
      title: 'Sort by status or last update',
      body: 'Select Status or Updated to sort by it, and again to reverse. The server sorts the whole result, not only this page. Status follows the lifecycle, from Running to Cancelled.',
      anchor: 'list-sort',
      fallbackAnchor: 'list-table',
    },
    {
      id: 'list-row',
      title: 'Open a saga',
      body: 'Select a row to open that saga: its summary, service map and timeline, and the data it held at each step if your account may see saga data. From the keyboard, Tab to a correlation id and press Enter.',
      anchor: 'list-row',
      fallbackAnchor: 'list-table',
    },
    {
      id: 'list-pagination',
      title: 'Page through results',
      body: 'Choose the rows per page, move between pages or jump to one. When new sagas arrive while you are past page 1, a banner offers a refresh instead of moving the rows you are reading.',
      anchor: 'list-pagination',
      fallbackAnchor: 'list-table',
      placement: 'top',
    },
    {
      id: 'list-guide',
      title: 'Guide mode',
      body: 'While Guide is on, a page explains itself the first time you open it. Replay tour runs the current one again; User guide opens the full documentation. Switch Guide off here when you are done.',
      anchor: 'topbar-guide',
    },
  ],
  // The detail page. Its parts are areas of their own, each explained the first time the page shows it.
  summary: [
    {
      id: 'summary-glance',
      title: 'The saga at a glance',
      body: 'Its type, correlation id, kind and status, then the current state, the version, and when it was created and last updated, in your local time. Links below lead to other sagas that share this correlation id, to the saga that started this one and to the sub-sagas it started.',
      anchor: 'detail-summary',
    },
    {
      id: 'summary-tabs',
      title: 'Map and Timeline',
      body: "Map and Timeline are two views of the same history: the map draws the messages between services and replays them, the timeline lists the engine's entries step by step. Each explains itself the first time you open it.",
      anchor: 'detail-tab-map',
      fallbackAnchor: 'detail-tab-timeline',
    },
  ],
  map: [
    {
      id: 'map-canvas',
      title: 'Service map',
      body: 'Boxes are the parties to this saga: whoever started it, the saga itself and each participant. Arrows are the messages between them. Solid arrows were answered, dashed ones got no response, dotted ones are compensations, and a red one is the hop that failed.',
      anchor: 'map-canvas',
    },
    {
      id: 'map-controls',
      title: 'Replay the saga',
      body: 'Restart, Play and Step forward move through the recorded history one entry at a time; drag the slider to jump. The speed buttons run it at 0.5×, 1×, 2× or 4×, and playback stops at the failure. The line under the controls names the entry shown and when it was recorded.',
      anchor: 'map-controls',
      placement: 'top',
    },
  ],
  timeline: [
    {
      id: 'timeline-steps',
      title: 'Timeline',
      body: "The engine's record of the saga, grouped into steps. A step shows what started it (a message, a timeout, a retry), how it ended and the entries it recorded. On a failed saga, the step where it failed is marked Failed here. A step's title opens the map as of the end of that step.",
      anchor: 'timeline',
    },
    {
      id: 'timeline-entry',
      title: 'Recorded at, and jump to the map',
      body: "Recorded at is when the engine wrote the entry, in your local time. Hover the time for UTC; the small offset counts from the saga's first entry. Select an entry to open the map as of it.",
      anchor: 'timeline-entry',
      fallbackAnchor: 'timeline',
    },
    {
      id: 'timeline-data',
      title: 'Data after each step',
      body: "A step's Data button opens the saga's data as it was saved when that step finished, and what changed since the earlier recorded state.",
      anchor: 'timeline-step-data',
      fallbackAnchor: 'timeline',
      requires: 'sagas.data',
    },
  ],
  data: [
    {
      id: 'data-bar',
      title: 'Saga data',
      body: 'At start shows the message that started the saga and the first saved state. At end shows the data as it is now (it reads Current until the saga finishes). Compare sets the two side by side, field by field.',
      anchor: 'detail-data',
      requires: 'sagas.data',
    },
    {
      id: 'data-views',
      title: 'Reading the data',
      body: "Changes lists the fields that differ from the earlier recorded state, Full state shows all of it, Message shows the message that ran the step, and Copy JSON copies the raw text. A step's Data button on the Timeline opens the same views.",
      anchor: 'detail-data',
      requires: 'sagas.data',
    },
  ],
  retry: [
    {
      id: 'retry-what',
      title: 'Retry a failed saga',
      body: 'For a Failed or TimedOut saga, Retry this saga re-runs the step that failed, for this saga only. It asks first: Re-run step N (message type, state) for this saga only? Yes, retry goes ahead; Cancel leaves the saga alone. The Timeline marks the failure Failed here, and Re-run starts here when a timeout means an earlier step runs again.',
      anchor: 'detail-retry',
      requires: 'sagas.retry',
    },
    {
      id: 'retry-effects',
      title: 'What a retry does not undo',
      body: 'Other services that consume the same message still receive it, so their side effects can repeat. A failure decided by the message alone, such as a business rule saying no, fails again. The timeline records the retry as a step, with who asked for it.',
      anchor: 'detail-retry',
      requires: 'sagas.retry',
    },
  ],
  // The administration area: no steps yet.
  admin: [],
};
