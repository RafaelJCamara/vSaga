import { GUIDE_AREAS } from '../../services/guide-areas';
import { ALL_PERMISSIONS } from '../../testing/auth-mock';
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
    it('has a tour (possibly still empty) for every area, and no tour for anything else', () => {
      expect(Object.keys(GUIDE_TOURS).sort()).toEqual([...AREA_IDS].sort());
    });

    it('ships the list tour, in the order the page is read, and no other yet', () => {
      expect(GUIDE_TOURS.list.map((s) => s.id)).toEqual([
        'list-welcome',
        'list-filters',
        'list-table',
        'list-sort',
        'list-row',
        'list-pagination',
        'list-guide',
      ]);
      const others = AREA_IDS.filter((id) => id !== 'list');
      expect(others.flatMap((id) => GUIDE_TOURS[id])).toEqual([]);
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
