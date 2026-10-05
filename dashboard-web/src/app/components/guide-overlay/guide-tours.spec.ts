import { GuideAreaId } from '../../models/guide.model';
import { GUIDE_AREAS } from '../../services/guide-areas';
import { ALL_PERMISSIONS } from '../../testing/auth-mock';
import { LAST_ADMINISTRATOR_ADVICE } from '../../util/admin-failure';
import { GUIDE_ANCHORS, GUIDE_TOURS } from './guide-tours';

const AREA_IDS = GUIDE_AREAS.map((a) => a.id);
const STEPS = AREA_IDS.flatMap((id) => GUIDE_TOURS[id].map((step) => ({ area: id, step })));
const VOCABULARY: readonly string[] = GUIDE_ANCHORS;

describe('the guide tours', () => {
  describe('the anchor vocabulary', () => {
    it('is the one design lists, once each', () => {
      expect([...GUIDE_ANCHORS].sort()).toEqual(
        [
          'topbar-guide',
          'list-filters',
          'list-table',
          'list-sort',
          'list-row',
          'list-pagination',
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
          'admin-nav',
          'admin-nav-users',
          'admin-nav-teams',
          'admin-nav-roles',
          'admin-list',
        ].sort(),
      );
      expect(new Set(GUIDE_ANCHORS).size).toBe(GUIDE_ANCHORS.length);
    });

    it('is written as lower-case words joined by hyphens, which is all a selector must cope with', () => {
      for (const anchor of GUIDE_ANCHORS) expect(anchor).toMatch(/^[a-z]+(-[a-z]+)*$/);
    });
  });

  describe('the tour table', () => {
    it('has a tour for every area, and no tour for anything else', () => {
      expect(Object.keys(GUIDE_TOURS).sort()).toEqual([...AREA_IDS].sort());
    });

    it('has steps for every area, so an area that Replay or the hint offers is one that can be shown', () => {
      for (const area of GUIDE_AREAS) {
        const steps = GUIDE_TOURS[area.id];
        expect(steps.length, area.id).toBeGreaterThan(0);
        // Anyone who holds the area's permission has a step to see: a step that asks for more is a bonus.
        expect(
          steps.some((s) => s.requires === undefined || s.requires === area.requires),
          area.id,
        ).toBe(true);
      }
    });

    it('ships the list tour, in the order the page is read', () => {
      expect(GUIDE_TOURS.list.map((s) => s.id)).toEqual([
        'list-welcome',
        'list-filters',
        'list-table',
        'list-sort',
        'list-row',
        'list-pagination',
        'list-guide',
      ]);
    });

    it('ships the five areas of the detail page and the administration area', () => {
      const ids = (area: GuideAreaId) => GUIDE_TOURS[area].map((s) => s.id);

      expect(ids('summary')).toEqual(['summary-glance', 'summary-tabs']);
      expect(ids('map')).toEqual(['map-canvas', 'map-controls']);
      expect(ids('timeline')).toEqual(['timeline-steps', 'timeline-entry', 'timeline-data']);
      expect(ids('data')).toEqual(['data-bar', 'data-views']);
      expect(ids('retry')).toEqual(['retry-what', 'retry-effects']);
      expect(ids('admin')).toEqual([
        'admin-nav',
        'admin-users',
        'admin-teams',
        'admin-roles',
        'admin-list',
        'admin-grants',
        'admin-preview',
        'admin-last',
      ]);
    });

    it('opens the list tour with a centred welcome and points the last step at the top bar', () => {
      expect(GUIDE_TOURS.list[0].anchor).toBeNull();
      expect(GUIDE_TOURS.list.at(-1)?.anchor).toBe('topbar-guide');
    });

    it('anchors every step of the list tour on the list page, except the first and last', () => {
      const middle = GUIDE_TOURS.list.slice(1, -1);

      expect(middle.map((s) => s.anchor)).toEqual([
        'list-filters',
        'list-table',
        'list-sort',
        'list-row',
        'list-pagination',
      ]);
    });
  });

  describe('every name a step or an area uses is in GUIDE_ANCHORS', () => {
    it.each(STEPS.map((s) => [s.step.id, s.step] as const))(
      '%s: anchor, fallback and reveal',
      (_id, step) => {
        for (const name of [step.anchor, step.fallbackAnchor, step.reveal]) {
          if (name !== null && name !== undefined) expect(VOCABULARY).toContain(name);
        }
      },
    );

    it.each(GUIDE_AREAS.map((a) => [a.id, a.readyAnchor] as const))(
      'the ready anchor of %s (%s)',
      (_id, anchor) => {
        expect(VOCABULARY).toContain(anchor);
      },
    );

    it('a fallback is never the step own anchor, and a step with a fallback or a reveal has an anchor', () => {
      for (const { step } of STEPS) {
        if (step.fallbackAnchor !== undefined) expect(step.fallbackAnchor).not.toBe(step.anchor);
        if (step.fallbackAnchor !== undefined || step.reveal !== undefined) {
          expect(step.anchor).not.toBeNull();
        }
      }
    });

    it('a reveal control is only ever a detail page tab', () => {
      for (const { step } of STEPS) {
        if (step.reveal !== undefined)
          expect(['detail-tab-map', 'detail-tab-timeline']).toContain(step.reveal);
      }
    });
  });

  describe('the steps', () => {
    it('have ids that are unique across all the tours', () => {
      const ids = STEPS.map((s) => s.step.id);

      expect(new Set(ids).size).toBe(ids.length);
    });

    it('have ids that start with their area, so a step names where it belongs', () => {
      for (const { area, step } of STEPS) expect(step.id.startsWith(`${area}-`)).toBe(true);
    });

    it('name only permissions that exist', () => {
      for (const { step } of STEPS) {
        if (step.requires !== undefined) expect(ALL_PERMISSIONS).toContain(step.requires);
      }
      for (const area of GUIDE_AREAS) expect(ALL_PERMISSIONS).toContain(area.requires);
    });

    it('have a heading and a body of a size a popover can hold', () => {
      for (const { step } of STEPS) {
        expect(step.title.trim()).toBe(step.title);
        expect(step.title.length).toBeGreaterThan(0);
        expect(step.title.length).toBeLessThanOrEqual(48);
        expect(step.body.trim()).toBe(step.body);
        expect(step.body.length).toBeGreaterThan(0);
        expect(step.body.length).toBeLessThanOrEqual(360);
        expect(step.body).not.toMatch(/\s{2,}/);
      }
    });

    it('prefer only a side the geometry knows', () => {
      for (const { step } of STEPS) {
        if (step.placement !== undefined)
          expect(['bottom', 'top', 'right', 'left']).toContain(step.placement);
      }
    });
  });

  describe('the detail page tours', () => {
    const steps = (area: GuideAreaId) => GUIDE_TOURS[area];
    const copy = (area: GuideAreaId) =>
      steps(area)
        .map((s) => `${s.title} ${s.body}`)
        .join(' ');

    it('start on the element the area is about: every area has a first step anchored on its ready anchor, or a centred one', () => {
      for (const area of GUIDE_AREAS.filter(
        (a) => GUIDE_TOURS[a.id].length > 0 && a.id !== 'list',
      )) {
        expect(GUIDE_TOURS[area.id][0].anchor, area.id).toBe(area.readyAnchor);
      }
    });

    it('ask for the permission of the data and the retry they explain', () => {
      expect(steps('data').map((s) => s.requires)).toEqual(['sagas.data', 'sagas.data']);
      expect(steps('retry').map((s) => s.requires)).toEqual(['sagas.retry', 'sagas.retry']);
      expect(steps('timeline').find((s) => s.id === 'timeline-data')?.requires).toBe('sagas.data');
    });

    it('ask for nothing beyond viewing the saga for the summary, the map and the rest of the timeline', () => {
      const unrestricted = [
        ...steps('summary'),
        ...steps('map'),
        ...steps('timeline').filter((s) => s.id !== 'timeline-data'),
      ];

      expect(unrestricted.map((s) => s.requires)).toEqual(
        Array(unrestricted.length).fill(undefined),
      );
    });

    it('never use a reveal control: each area starts when its part is on screen', () => {
      for (const area of ['summary', 'map', 'timeline', 'data', 'retry'] as const) {
        for (const step of steps(area)) expect(step.reveal, step.id).toBeUndefined();
      }
    });

    it('say what a retry is: the step that failed, for this saga only, and that others still receive the message', () => {
      const text = copy('retry');

      expect(text).toContain('re-runs the step that failed, for this saga only');
      expect(text).toContain('Other services that consume the same message still receive it');
      expect(text).toContain('A failure decided by the message alone');
      expect(text).toContain('fails again');
      // Not the blueprint's: a retry does not reset the saga or replay its first message.
      expect(text).not.toMatch(/resets the saga|first message|replays? the whole|every step/i);
    });

    it('say what a refused retry looks like, and that a participant that handles the message again repeats its side effects', () => {
      const body = (id: string) => GUIDE_TOURS.retry.find((s) => s.id === id)?.body ?? '';

      // The button is dimmed (aria-disabled) with the plan's reason beside it, and cannot open the prompt.
      expect(body('retry-what')).toContain('A refused retry is dimmed, with its reason beside it');
      // Design 7.5: other saga types ignore the replay; participants subscribed through the transport act on it.
      expect(body('retry-effects')).toContain(
        'so a participant that handles it again repeats its side effects',
      );
      expect(body('retry-effects')).not.toContain('their side effects can repeat');
    });

    it('say the data views as the page has them: Changes and Full state in any view, Message only under a step', () => {
      const views = GUIDE_TOURS.data.find((s) => s.id === 'data-views')?.body ?? '';

      expect(views).toContain('Once a view is open it offers Changes');
      expect(views).toContain('and Full state');
      expect(views).toContain("Under a step's Data button on the Timeline it also offers Message");
      expect(views).toContain('Copy JSON');
    });

    it('say which tab the summary step highlights, and that the other is next to it', () => {
      const tabs = GUIDE_TOURS.summary.find((s) => s.id === 'summary-tabs');

      expect(tabs?.anchor).toBe('detail-tab-map');
      expect(tabs?.body).toContain(
        'The Map tab, highlighted here, and the Timeline tab next to it',
      );
    });

    it('name the labels the retry row and its confirmation have', () => {
      const text = copy('retry');

      for (const label of [
        'Retry this saga',
        'Re-run step N (message type, state) for this saga only?',
        'Yes, retry',
        'Cancel',
        'Failed here',
        'Re-run starts here',
      ]) {
        expect(text, label).toContain(label);
      }
    });

    it('name the labels the Saga data group, its views and the timeline have', () => {
      const data = copy('data');
      for (const label of [
        'At start',
        'At end',
        'Current',
        'Compare',
        'Changes',
        'Full state',
        'Message',
        'Copy JSON',
      ]) {
        expect(data, label).toContain(label);
      }
      const body = (id: string) => GUIDE_TOURS.timeline.find((s) => s.id === id)?.body ?? '';
      expect(body('timeline-steps')).toContain('Failed here');
      expect(body('timeline-entry')).toContain('Recorded at');
      expect(body('timeline-data')).toContain('Data button');
      expect(copy('summary')).toContain('Map and Timeline');
      expect(copy('map')).toContain('Play');
    });
  });

  describe('the administration tour', () => {
    const steps = GUIDE_TOURS.admin;
    const body = (id: string) => steps.find((s) => s.id === id)?.body ?? '';
    const all = steps.map((s) => `${s.title} ${s.body}`).join(' ');

    it("points at the shell's tabs in the order they are, then at the table of the page", () => {
      expect(steps.map((s) => s.anchor)).toEqual([
        'admin-nav',
        'admin-nav-users',
        'admin-nav-teams',
        'admin-nav-roles',
        'admin-list',
        null,
        null,
        null,
      ]);
    });

    it('has no fallback and no reveal: the table step is left out of a page with no table, the rest never wait for one', () => {
      for (const step of steps) {
        expect(step.fallbackAnchor, step.id).toBeUndefined();
        expect(step.reveal, step.id).toBeUndefined();
      }
    });

    it('is about managing access, and asks for nothing less', () => {
      for (const step of steps) expect(step.requires, step.id).toBe('access.manage');
    });

    it('says who can open the area: Manage access, held for all saga types, and names its three parts', () => {
      const text = body('admin-nav');

      expect(text).toContain(
        'Only accounts that hold Manage access for all saga types can open it',
      );
      for (const part of ['Users', 'Teams', 'Roles']) expect(text, part).toContain(part);
    });

    it('names the four permissions as the catalogue labels them, and the three built-in roles', () => {
      for (const label of ['View sagas', 'View saga data', 'Retry sagas', 'Manage access']) {
        expect(body('admin-roles') + body('admin-nav'), label).toContain(label);
      }
      for (const role of ['Administrator', 'Operator', 'Viewer']) {
        expect(body('admin-roles'), role).toContain(role);
      }
    });

    it('names what the users list and the user page have: the chips, the actions, and what ends sessions', () => {
      const users = body('admin-users');

      for (const label of ['Disabled', 'Locked', 'Must change password']) {
        expect(users, label).toContain(label);
      }
      expect(users).toContain(
        'resets the password, unlocks the account, disables it or deletes it',
      );
      expect(users).toContain(
        'Disabling an account or resetting its password ends its open sessions',
      );
    });

    it('says a team is saved as a whole, and that its members hold its access on top of their own', () => {
      expect(body('admin-teams')).toContain('on top of what they hold directly');
      expect(body('admin-teams')).toContain('saving replaces both');
    });

    it('says built-in roles are read and duplicated, and that a role in use cannot be deleted', () => {
      const roles = body('admin-roles');

      expect(roles).toContain('Duplicate as custom role');
      expect(roles).toContain('cannot be changed or deleted');
      expect(roles).toContain('A custom role cannot be deleted while a grant uses it');
    });

    it('names the buttons and columns the lists have', () => {
      const list = body('admin-list');

      for (const label of ['New user', 'New team', 'New role', 'Access', 'In use']) {
        expect(list, label).toContain(label);
      }
      // The phrases the lists summarise a grant with (`summarizeGrants`).
      expect(list).toContain('Operator · all types');
      expect(list).toContain('Viewer · 2 types');
    });

    it('describes the grants editor as shipped: one grant per role, all or selected saga types, the exact name', () => {
      const grants = body('admin-grants');

      expect(grants).toContain('one grant per role');
      expect(grants).toContain('All saga types');
      expect(grants).toContain('Selected saga types');
      expect(grants).toContain('exact saga type name');
      expect(grants).toContain('case matters');
      // The editor's own sentence: "access.manage is ignored in a scoped grant: it counts only for all saga types."
      expect(grants).toContain('access.manage is ignored in a scoped grant');
      expect(grants).toContain('it counts only for all saga types');
      // Not the blueprint's wording, which is not what the editor says.
      expect(grants).not.toContain('Manage access counts only');
    });

    it('describes the effective access preview with its origins, for a user and for a team', () => {
      const preview = body('admin-preview');

      expect(preview).toContain('Effective access');
      expect(preview).toContain('saved or not');
      expect(preview).toContain('direct: Operator');
      expect(preview).toContain('team Payments: Viewer');
      expect(preview).toContain('what the team gives its members');
    });

    it('ends on the last administrator rule, with the advice the page itself gives', () => {
      const last = steps.at(-1);

      expect(last?.id).toBe('admin-last');
      expect(last?.anchor).toBeNull();
      expect(last?.body).toContain(
        'would leave no enabled user holding access.manage for all saga types',
      );
      // The sentence the 409 banner ends with, as a sentence in the middle of this one.
      const advice = LAST_ADMINISTRATOR_ADVICE.replace(/^G/, 'g').replace(/\.$/, '');
      expect(last?.body).toContain(advice);
    });

    it("does not use the blueprint's claims that the shipped pages do not make", () => {
      // "Create accounts" (it is New user), "Only accounts holding Manage access can open it" without the scope.
      expect(all).not.toContain('Create accounts');
      expect(all).not.toContain('refuses to delete, disable or demote');
    });
  });

  describe('the copy of the list tour', () => {
    const copy = (id: string) => GUIDE_TOURS.list.find((s) => s.id === id)?.body ?? '';

    it('names the controls and the columns the list page has', () => {
      expect(copy('list-filters')).toContain('status, kind or saga type');
      expect(copy('list-filters')).toContain('correlation id');
      expect(copy('list-table')).toMatch(
        /correlation id.*type.*kind.*current state.*status.*last update/,
      );
      expect(copy('list-sort')).toContain('Status or Updated');
      expect(copy('list-pagination')).toContain('rows per page');
    });

    it('says the rows open from the keyboard through the correlation id', () => {
      expect(copy('list-row')).toContain('Tab to a correlation id and press Enter');
    });

    it('does not promise the data to everyone: it needs the permission', () => {
      expect(copy('list-row')).toContain('if your account may see saga data');
    });
  });
});
