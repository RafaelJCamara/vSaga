import { ChangeDetectionStrategy, Component, WritableSignal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Mock, vi } from 'vitest';
import { GuideAreaId, GuidePermissionCheck } from '../models/guide.model';
import { PermissionKey } from '../models/auth.model';
import {
  MemoryStorage,
  createGuideStorage,
  provideGuideStorage,
  storedGuide,
} from '../testing/guide';
import { GUIDE_AREAS, guideAreaForPath, guideAreaOf } from './guide-areas';
import {
  GUIDE_PERMISSION_CHECK,
  GUIDE_STORAGE,
  GUIDE_STORAGE_KEY,
  GuideService,
} from './guide.service';

@Component({
  selector: 'app-page-stub',
  template: '',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class PageStub {}

const ALL_AREAS: GuideAreaId[] = ['list', 'summary', 'map', 'timeline', 'data', 'retry', 'admin'];

describe('GuideService', () => {
  let storage: Storage | null;
  /** The permissions the session holds, for every saga type; a spec narrows it. */
  let held: WritableSignal<readonly PermissionKey[]>;
  let check: Mock<GuidePermissionCheck>;

  /** The service, created over `storage` (a stored state, garbage, nothing) with the router on `url`. */
  async function create(url = '/account'): Promise<{ guide: GuideService; router: Router }> {
    held = signal<readonly PermissionKey[]>([
      'sagas.view',
      'sagas.data',
      'sagas.retry',
      'access.manage',
    ]);
    check = vi.fn<GuidePermissionCheck>((permission) => held().includes(permission));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'sagas', component: PageStub },
          { path: 'sagas/:sagaType/:id', component: PageStub },
          { path: 'admin/users', component: PageStub },
          { path: 'admin', component: PageStub },
          { path: 'account', component: PageStub },
        ]),
        provideGuideStorage(storage),
        { provide: GUIDE_PERMISSION_CHECK, useValue: check },
      ],
    });
    const router = TestBed.inject(Router);
    const guide = TestBed.inject(GuideService);
    await router.navigateByUrl(url);
    return { guide, router };
  }

  const stored = (state: Partial<Record<string, unknown>> = {}) => ({
    v: 1,
    enabled: false,
    seen: {},
    hintDismissed: false,
    ...state,
  });

  beforeEach(() => {
    storage = createGuideStorage();
  });

  describe('the area table', () => {
    it('has one row for each area, with a ready anchor, a permission, a version and a docs anchor', () => {
      expect(GUIDE_AREAS.map((a) => a.id).sort()).toEqual([...ALL_AREAS].sort());
      for (const area of GUIDE_AREAS) {
        expect(area.version).toBeGreaterThanOrEqual(1);
        expect(area.docsAnchor).toMatch(/^#[a-z-]+$/);
        expect(area.readyAnchor).not.toBe('');
        expect(guideAreaOf(area.id)).toBe(area);
      }
    });

    it('starts the list, the detail summary and administration by route, and the rest when announced', () => {
      const triggers = Object.fromEntries(GUIDE_AREAS.map((a) => [a.id, a.trigger.on]));

      expect(triggers).toEqual({
        list: 'route',
        summary: 'route',
        map: 'shown',
        timeline: 'shown',
        data: 'shown',
        retry: 'shown',
        admin: 'route',
      });
    });

    it.each([
      ['/sagas', 'list'],
      ['/sagas/', 'list'],
      ['/sagas/OrderSaga/abc-123', 'summary'],
      ['/sagas/OrderSaga/abc-123/', 'summary'],
      ['/admin', 'admin'],
      ['/admin/users', 'admin'],
      ['/admin/users/new', 'admin'],
    ])('%s is the %s area', (path, id) => {
      expect(guideAreaForPath(path)?.id).toBe(id);
    });

    it.each([
      '/',
      '/login',
      '/account',
      '/sagas/OrderSaga',
      '/sagas/a/b/c',
      '/administration',
      '/sagas2',
    ])('%s has no area of its own', (path) => {
      expect(guideAreaForPath(path)).toBeNull();
    });

    it('waits for the element that says the area is on screen', () => {
      const ready = Object.fromEntries(GUIDE_AREAS.map((a) => [a.id, a.readyAnchor]));

      expect(ready).toEqual({
        list: 'list-table',
        summary: 'detail-summary',
        map: 'map-canvas',
        timeline: 'timeline',
        data: 'detail-data',
        retry: 'detail-retry',
        admin: 'admin-nav',
      });
    });

    it('asks for the permission each area is about', () => {
      const requires = Object.fromEntries(GUIDE_AREAS.map((a) => [a.id, a.requires]));

      expect(requires).toEqual({
        list: 'sagas.view',
        summary: 'sagas.view',
        map: 'sagas.view',
        timeline: 'sagas.view',
        data: 'sagas.data',
        retry: 'sagas.retry',
        admin: 'access.manage',
      });
    });
  });

  describe('the stored state', () => {
    it('is Guide off, nothing seen and the hint still to show when nothing is stored', async () => {
      const { guide } = await create('/sagas');

      expect(guide.enabled()).toBe(false);
      expect(guide.isSeen('list')).toBe(false);
      expect(guide.showHint()).toBe(true);
      expect(guide.request()).toBeNull();
      expect(guide.running()).toBeNull();
      expect(guide.canReplay()).toBe(false);
      expect(storedGuide(storage as Storage)).toBeNull(); // reading writes nothing
    });

    it('restores what was stored', async () => {
      storage = createGuideStorage(
        stored({ enabled: true, seen: { list: 1 }, hintDismissed: true }),
      );
      const { guide } = await create('/sagas');

      expect(guide.enabled()).toBe(true);
      expect(guide.isSeen('list')).toBe(true);
      expect(guide.isSeen('admin')).toBe(false);
      expect(guide.showHint()).toBe(false);
    });

    it('does not start an area that was seen, on a reload with Guide on', async () => {
      storage = createGuideStorage(
        stored({ enabled: true, seen: { list: 1 }, hintDismissed: true }),
      );
      const { guide } = await create('/sagas');

      expect(guide.request()).toBeNull();
      expect(guide.canReplay()).toBe(true);
    });

    it('starts an area that was not seen, on a reload with Guide on', async () => {
      storage = createGuideStorage(stored({ enabled: true, hintDismissed: true }));
      const { guide } = await create('/sagas');

      expect(guide.request()?.area.id).toBe('list');
    });

    it.each([
      ['corrupt JSON', '{"v":1,"enabled":tr'],
      ['not JSON at all', 'enabled'],
      [
        'an unknown version',
        JSON.stringify({ v: 2, enabled: true, seen: { list: 1 }, hintDismissed: true }),
      ],
      ['no version', JSON.stringify({ enabled: true, seen: { list: 1 }, hintDismissed: true })],
      ['a version that is a string', JSON.stringify({ v: '1', enabled: true })],
      ['null', 'null'],
      ['a number', '7'],
      ['an array', '[1]'],
    ])('reads %s as the defaults', async (_name, raw) => {
      storage = createGuideStorage(raw);
      const { guide } = await create('/sagas');

      expect(guide.enabled()).toBe(false);
      expect(guide.isSeen('list')).toBe(false);
      expect(guide.showHint()).toBe(true);
    });

    it('reads a malformed member of the right version as its own default, and keeps the rest', async () => {
      storage = createGuideStorage({
        v: 1,
        enabled: 'yes',
        hintDismissed: 'yes',
        seen: { list: 1, nowhere: 1, admin: 0, map: -2, timeline: 1.5, data: '1', retry: null },
      });
      const { guide } = await create('/account');

      expect(guide.enabled()).toBe(false);
      expect(guide.isSeen('list')).toBe(true);
      for (const id of ['admin', 'map', 'timeline', 'data', 'retry'] as const) {
        expect(guide.isSeen(id)).toBe(false);
      }
      // What it writes back says what it made of the stored state.
      guide.dismissHint();
      expect(storedGuide(storage as Storage)).toEqual({
        v: 1,
        enabled: false,
        seen: { list: 1 },
        hintDismissed: true,
      });
    });

    it('treats a seen entry that is not an object as nothing seen', async () => {
      storage = createGuideStorage({ v: 1, enabled: false, seen: 'list', hintDismissed: false });
      const { guide } = await create('/account');

      expect(guide.isSeen('list')).toBe(false);
    });

    it('reads the defaults from a storage that throws, and keeps working in memory', async () => {
      const broken = new MemoryStorage();
      vi.spyOn(broken, 'getItem').mockImplementation(() => {
        throw new Error('blocked');
      });
      vi.spyOn(broken, 'setItem').mockImplementation(() => {
        throw new Error('full');
      });
      storage = broken;
      const { guide } = await create('/sagas');

      expect(guide.enabled()).toBe(false);
      guide.setEnabled(true);
      expect(guide.enabled()).toBe(true);
      expect(guide.request()?.area.id).toBe('list');
      guide.started('list');
      guide.ended('list', true);
      expect(guide.isSeen('list')).toBe(true);
    });

    it('keeps its state in memory when there is no storage at all', async () => {
      storage = null;
      const { guide } = await create('/sagas');

      guide.setEnabled(true);
      guide.started('list');
      guide.ended('list', true);

      expect(guide.enabled()).toBe(true);
      expect(guide.isSeen('list')).toBe(true);
      expect(guide.showHint()).toBe(false);
    });

    it('writes the v1 shape under the one key, and nothing else', async () => {
      const { guide } = await create('/sagas');

      guide.setEnabled(true);
      expect(storedGuide(storage as Storage)).toEqual({
        v: 1,
        enabled: true,
        seen: {},
        hintDismissed: true,
      });
      guide.started('list');
      guide.ended('list', true);
      expect(storedGuide(storage as Storage)).toEqual({
        v: 1,
        enabled: true,
        seen: { list: 1 },
        hintDismissed: true,
      });
      expect(GUIDE_STORAGE_KEY).toBe('vsaga.guide');
      expect((storage as MemoryStorage).length).toBe(1);
    });

    it('is the browser localStorage unless a storage is provided', () => {
      TestBed.configureTestingModule({});

      expect(TestBed.inject(GUIDE_STORAGE)).toBe(globalThis.localStorage);
    });

    it('is no storage at all where the browser refuses the access (blocked site data)', () => {
      const original = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
      Object.defineProperty(globalThis, 'localStorage', {
        get() {
          throw new DOMException('denied', 'SecurityError');
        },
        configurable: true,
      });
      try {
        TestBed.configureTestingModule({});

        expect(TestBed.inject(GUIDE_STORAGE)).toBeNull();
      } finally {
        if (original) Object.defineProperty(globalThis, 'localStorage', original);
        else Reflect.deleteProperty(globalThis, 'localStorage');
      }
    });
  });

  describe('switching Guide on and off', () => {
    it('is off by default: opening the list asks for nothing', async () => {
      const { guide, router } = await create('/account');

      await router.navigateByUrl('/sagas');

      expect(guide.request()).toBeNull();
      expect(guide.canReplay()).toBe(false);
    });

    it('starts the current page area when it is switched on', async () => {
      const { guide } = await create('/sagas');

      guide.setEnabled(true);

      expect(guide.enabled()).toBe(true);
      expect(guide.request()?.area.id).toBe('list');
      expect(guide.running()).toBeNull();
    });

    it('starts nothing on a page with no area, and the next page that has one starts', async () => {
      const { guide, router } = await create('/account');

      guide.setEnabled(true);
      expect(guide.request()).toBeNull();

      await router.navigateByUrl('/sagas');
      expect(guide.request()?.area.id).toBe('list');
    });

    it('counts everything as unseen again, and answers the hint', async () => {
      storage = createGuideStorage(stored({ seen: { list: 1, admin: 1 } }));
      const { guide } = await create('/sagas');
      expect(guide.showHint()).toBe(true);

      guide.setEnabled(true);

      expect(guide.isSeen('list')).toBe(false);
      expect(guide.isSeen('admin')).toBe(false);
      expect(guide.showHint()).toBe(false);
      expect(guide.request()?.area.id).toBe('list'); // although it had been seen
    });

    it('does nothing when it is switched on while already on: what was seen stays seen', async () => {
      const { guide } = await create('/sagas');
      guide.setEnabled(true);
      guide.started('list');
      guide.ended('list', true);

      guide.setEnabled(true);

      expect(guide.request()).toBeNull();
      expect(guide.isSeen('list')).toBe(true);
    });

    it('drops what is wanted and queued when it is switched off, and stores that it is off', async () => {
      const { guide } = await create('/sagas');
      guide.setEnabled(true);
      guide.started('list');
      guide.areaShown('timeline'); // queued behind the running tour
      guide.setEnabled(false);

      expect(guide.enabled()).toBe(false);
      expect(guide.request()).toBeNull();
      expect(storedGuide(storage as Storage)).toMatchObject({ enabled: false });

      guide.ended('list', false);
      expect(guide.request()).toBeNull(); // the queue is gone
    });

    it('drops a request that was waiting for the page when it is switched off', async () => {
      const { guide } = await create('/sagas');
      guide.setEnabled(true);
      expect(guide.request()?.area.id).toBe('list');

      guide.setEnabled(false);

      expect(guide.request()).toBeNull();
    });

    it('does not carry the queue of one session of Guide into the next', async () => {
      const { guide } = await create('/sagas');
      guide.setEnabled(true);
      guide.started('list');
      guide.areaShown('map'); // both queued behind the running tour; the timeline is what is shown now
      guide.areaShown('timeline');

      guide.setEnabled(false);
      guide.ended('list', false); // the overlay ends the tour it was showing
      guide.setEnabled(true);
      expect(guide.request()?.area.id).toBe('timeline'); // switching on explains what is shown
      guide.started('timeline');
      guide.ended('timeline', true);

      expect(guide.request()).toBeNull(); // the map was forgotten with the first session
    });

    it('toggles', async () => {
      const { guide } = await create('/sagas');

      guide.toggle();
      expect(guide.enabled()).toBe(true);
      guide.toggle();
      expect(guide.enabled()).toBe(false);
    });
  });

  describe('opening a page with Guide on', () => {
    async function createOn(url = '/account', state: Partial<Record<string, unknown>> = {}) {
      storage = createGuideStorage(stored({ enabled: true, hintDismissed: true, ...state }));
      return create(url);
    }

    it('asks for the list when it is opened and not yet seen', async () => {
      const { guide, router } = await createOn();

      await router.navigateByUrl('/sagas');

      expect(guide.request()?.area.id).toBe('list');
    });

    it('does not ask for an area that was seen', async () => {
      const { guide, router } = await createOn('/account', { seen: { list: 1 } });

      await router.navigateByUrl('/sagas');

      expect(guide.request()).toBeNull();
    });

    it('asks again when the area version was bumped, once', async () => {
      const list = GUIDE_AREAS.find((a) => a.id === 'list') as { version: number };
      try {
        const { guide, router } = await createOn('/account', { seen: { list: 1 } });
        list.version = 2;

        await router.navigateByUrl('/sagas');
        expect(guide.request()?.area.id).toBe('list');
        expect(guide.isSeen('list')).toBe(false);

        guide.started('list');
        guide.ended('list', true);
        expect(storedGuide(storage as Storage)).toMatchObject({ seen: { list: 2 } });

        await router.navigateByUrl('/account');
        await router.navigateByUrl('/sagas');
        expect(guide.request()).toBeNull();
      } finally {
        list.version = 1;
      }
    });

    it('does not count a query-only change as another page', async () => {
      const { guide, router } = await createOn();
      await router.navigateByUrl('/sagas');
      const request = guide.request();
      expect(request).not.toBeNull();

      await router.navigateByUrl('/sagas?status=Failed&page=2');
      await router.navigateByUrl('/sagas?sortBy=Status#top');

      expect(guide.request()).toBe(request); // the very same request: nothing was reset
      expect(guide.page()).toBe('/sagas');
    });

    it('does not restart a running tour on a query-only change, and keeps a queued area', async () => {
      const { guide, router } = await createOn('/sagas', { seen: { list: 1 } });
      guide.replay();
      guide.started('list');
      guide.areaShown('timeline');

      await router.navigateByUrl('/sagas?page=2');
      expect(guide.running()).toBe('list');

      guide.ended('list', true);
      expect(guide.request()?.area.id).toBe('timeline');
    });

    it('asks again for a tour that was not remembered when the page is opened again', async () => {
      const { guide, router } = await createOn('/sagas');
      expect(guide.request()?.area.id).toBe('list');
      guide.started('list');
      guide.ended('list', false);
      expect(guide.isSeen('list')).toBe(false);

      await router.navigateByUrl('/account');
      await router.navigateByUrl('/sagas');

      expect(guide.request()?.area.id).toBe('list');
    });

    it('drops a request and the queue when the page changes', async () => {
      const { guide, router } = await createOn('/sagas');
      expect(guide.request()).not.toBeNull();

      await router.navigateByUrl('/account');

      expect(guide.request()).toBeNull();
    });

    it('lets the overlay end a running tour when the page changes: it is no longer running', async () => {
      const { guide, router } = await createOn('/sagas');
      guide.started('list');

      await router.navigateByUrl('/account');
      expect(guide.running()).toBeNull();

      guide.ended('list', true); // the overlay's late answer is ignored
      expect(guide.isSeen('list')).toBe(false);
    });

    it('asks for the summary on a detail page and the administration area on the admin pages', async () => {
      const { guide, router } = await createOn('/account');

      await router.navigateByUrl('/sagas/OrderSaga/abc');
      expect(guide.request()?.area.id).toBe('summary');

      await router.navigateByUrl('/admin/users');
      expect(guide.request()?.area.id).toBe('admin');
    });

    it('is another page for another saga, so the summary is asked for again if it was not remembered', async () => {
      const { guide, router } = await createOn('/sagas/OrderSaga/a');
      const first = guide.request();

      await router.navigateByUrl('/sagas/OrderSaga/b');

      expect(guide.request()?.area.id).toBe('summary');
      expect(guide.request()).not.toBe(first);
    });

    it('stays out of the way with the page: no request while a tour is running', async () => {
      const { guide } = await createOn('/sagas');
      guide.started('list');

      expect(guide.running()).toBe('list');
      expect(guide.request()).toBeNull();
    });
  });

  describe('the tour in progress', () => {
    it('marks the area seen only when it ends with remember', async () => {
      const { guide } = await create('/sagas');
      guide.setEnabled(true);
      guide.started('list');
      expect(guide.running()).toBe('list');
      expect(guide.request()).toBeNull();

      guide.ended('list', false);
      expect(guide.running()).toBeNull();
      expect(guide.isSeen('list')).toBe(false);
      expect(storedGuide(storage as Storage)).toMatchObject({ seen: {} });

      guide.replay();
      guide.started('list');
      guide.ended('list', true);
      expect(guide.isSeen('list')).toBe(true);
      expect(storedGuide(storage as Storage)).toMatchObject({ seen: { list: 1 } });
    });

    it('ignores the end of an area that is not the one running', async () => {
      const { guide } = await create('/sagas');
      guide.setEnabled(true);
      guide.started('list');

      guide.ended('admin', true);

      expect(guide.running()).toBe('list');
      expect(guide.isSeen('admin')).toBe(false);
    });

    it('gives up on a request without remembering anything', async () => {
      const { guide } = await create('/sagas');
      guide.setEnabled(true);
      expect(guide.request()).not.toBeNull();

      guide.abandoned();

      expect(guide.request()).toBeNull();
      expect(guide.isSeen('list')).toBe(false);
      expect(guide.canReplay()).toBe(true);
    });
  });

  describe('replay', () => {
    it('is offered only with Guide on, on a page with an area, while nothing runs or is wanted', async () => {
      const { guide, router } = await create('/sagas');
      expect(guide.canReplay()).toBe(false); // Guide off

      guide.setEnabled(true);
      expect(guide.canReplay()).toBe(false); // the list is asked for

      guide.started('list');
      expect(guide.canReplay()).toBe(false); // it is running

      guide.ended('list', true);
      expect(guide.canReplay()).toBe(true);

      await router.navigateByUrl('/account');
      expect(guide.canReplay()).toBe(false); // no area here
    });

    it('repeats the current area, although it was seen, with a new request each time', async () => {
      storage = createGuideStorage(
        stored({ enabled: true, hintDismissed: true, seen: { list: 1 } }),
      );
      const { guide } = await create('/sagas');
      expect(guide.request()).toBeNull();

      guide.replay();
      const first = guide.request();
      expect(first?.area.id).toBe('list');

      guide.started('list');
      guide.ended('list', true);
      guide.replay();
      const second = guide.request();

      expect(second?.area.id).toBe('list');
      expect(second?.nonce).not.toBe(first?.nonce);
    });

    it('does nothing when it is not offered', async () => {
      const { guide } = await create('/sagas');

      guide.replay(); // Guide off
      expect(guide.request()).toBeNull();

      guide.setEnabled(true);
      const request = guide.request();
      guide.replay(); // already asked for
      expect(guide.request()).toBe(request);
    });

    it('repeats the part of the page that was shown last', async () => {
      storage = createGuideStorage(
        stored({ enabled: true, hintDismissed: true, seen: { summary: 1, timeline: 1 } }),
      );
      const { guide } = await create('/sagas/OrderSaga/abc');
      expect(guide.area()?.id).toBe('summary');

      guide.areaShown('timeline');
      expect(guide.area()?.id).toBe('timeline');

      guide.replay();
      expect(guide.request()?.area.id).toBe('timeline');
    });
  });

  describe('the hint', () => {
    it('shows on a page with an area while Guide is off and it was not answered', async () => {
      const { guide, router } = await create('/account');
      expect(guide.showHint()).toBe(false); // nothing to offer here

      await router.navigateByUrl('/sagas');
      expect(guide.showHint()).toBe(true);
    });

    it('is answered by dismissing it, and the answer is stored', async () => {
      const { guide } = await create('/sagas');

      guide.dismissHint();

      expect(guide.showHint()).toBe(false);
      expect(guide.enabled()).toBe(false);
      expect(storedGuide(storage as Storage)).toEqual({
        v: 1,
        enabled: false,
        seen: {},
        hintDismissed: true,
      });
    });

    it('does not show while Guide is on', async () => {
      const { guide } = await create('/sagas');

      guide.setEnabled(true);
      expect(guide.showHint()).toBe(false);
      guide.setEnabled(false);
      expect(guide.showHint()).toBe(false); // switching it on answered it for good
    });

    it('does not show while Guide is on, whatever the stored state says about the hint', async () => {
      storage = createGuideStorage(
        stored({ enabled: true, hintDismissed: false, seen: { list: 1 } }),
      );
      const { guide } = await create('/sagas');

      expect(guide.enabled()).toBe(true);
      expect(guide.showHint()).toBe(false);
    });

    it('does not rewrite the storage when it was answered already', async () => {
      storage = createGuideStorage(stored({ hintDismissed: true }));
      const { guide } = await create('/sagas');
      const setItem = vi.spyOn(storage as MemoryStorage, 'setItem');

      guide.dismissHint();

      expect(setItem).not.toHaveBeenCalled();
    });
  });

  describe('permissions', () => {
    it('has no area, no request and no hint for a session without the area permission', async () => {
      const { guide, router } = await create('/account');
      held.set([]);
      guide.setEnabled(true);

      await router.navigateByUrl('/sagas');

      expect(guide.area()).toBeNull();
      expect(guide.request()).toBeNull();
      expect(guide.showHint()).toBe(false);
      expect(guide.canReplay()).toBe(false);
    });

    it('keeps the administration area from anyone without access.manage', async () => {
      const { guide, router } = await create('/account');
      held.set(['sagas.view']);
      guide.setEnabled(true);

      await router.navigateByUrl('/admin/users');
      expect(guide.request()).toBeNull();

      held.set(['sagas.view', 'access.manage']);
      await router.navigateByUrl('/sagas');
      await router.navigateByUrl('/admin/users');
      expect(guide.request()?.area.id).toBe('admin');
    });

    it('asks for the list without a saga type (any type will do) and for a detail page with its own', async () => {
      const { guide, router } = await create('/sagas');
      expect(guide.area()?.id).toBe('list');
      expect(check).toHaveBeenCalledWith('sagas.view', undefined);

      await router.navigateByUrl('/sagas/Order%20Saga%2F2/abc');
      expect(guide.sagaType()).toBe('Order Saga/2');
      expect(guide.area()?.id).toBe('summary');
      expect(check).toHaveBeenLastCalledWith('sagas.view', 'Order Saga/2');

      await router.navigateByUrl('/sagas');
      expect(guide.sagaType()).toBeNull();
    });

    it('follows the session: the area goes when the permission does', async () => {
      const { guide } = await create('/sagas');
      expect(guide.area()?.id).toBe('list');

      held.set([]);
      expect(guide.area()).toBeNull();

      held.set(['sagas.view']);
      expect(guide.area()?.id).toBe('list');
    });
  });

  describe('announcing a part of a page', () => {
    async function onDetailPage(state: Partial<Record<string, unknown>> = {}) {
      storage = createGuideStorage(stored({ enabled: true, hintDismissed: true, ...state }));
      const made = await create('/sagas/OrderSaga/abc');
      // The summary was asked for on arrival: it is shown and finished before the tabs matter.
      made.guide.started('summary');
      made.guide.ended('summary', true);
      return made;
    }

    it('asks for the area when Guide is on and it was not seen', async () => {
      const { guide } = await onDetailPage();

      guide.areaShown('timeline');

      expect(guide.request()?.area.id).toBe('timeline');
      expect(guide.area()?.id).toBe('timeline');
    });

    it('does not ask for an area that was seen, but it becomes the area to replay', async () => {
      const { guide } = await onDetailPage({ seen: { timeline: 1 } });

      guide.areaShown('timeline');

      expect(guide.request()).toBeNull();
      expect(guide.area()?.id).toBe('timeline');
    });

    it('does not ask while Guide is off', async () => {
      const { guide } = await create('/sagas/OrderSaga/abc');
      guide.setEnabled(false);

      guide.areaShown('map');

      expect(guide.request()).toBeNull();
    });

    it('does not ask again once the area was explained: showing the tab again explains nothing', async () => {
      const { guide } = await onDetailPage();
      guide.areaShown('map');
      guide.started('map');
      guide.ended('map', true);

      guide.areaShown('timeline');
      guide.started('timeline');
      guide.ended('timeline', true);
      guide.areaShown('map'); // back to a tab that was explained

      expect(guide.request()).toBeNull();
      expect(guide.area()?.id).toBe('map');
    });

    it('queues an area announced while another runs, and starts it after the one that finished', async () => {
      const { guide } = await onDetailPage();
      guide.areaShown('map');
      guide.started('map');

      guide.areaShown('retry');
      expect(guide.request()).toBeNull();

      guide.ended('map', true);
      expect(guide.request()?.area.id).toBe('retry');
    });

    it('queues an area announced while another is still being asked for', async () => {
      const { guide } = await onDetailPage();
      guide.areaShown('map');
      guide.areaShown('retry');
      guide.areaShown('retry'); // once only
      expect(guide.request()?.area.id).toBe('map');

      guide.started('map');
      guide.ended('map', true);
      expect(guide.request()?.area.id).toBe('retry');

      guide.started('retry');
      guide.ended('retry', true);
      expect(guide.request()).toBeNull();
    });

    it('does not queue the tour that is running behind itself: it would run a second time after Done', async () => {
      const { guide } = await onDetailPage();
      guide.areaShown('map');
      guide.started('map');

      guide.areaShown('map'); // the tab is shown again while its tour is on screen
      guide.ended('map', true);

      expect(guide.request()).toBeNull();
      expect(guide.isSeen('map')).toBe(true);
    });

    it('does not bring back, after the next tour, one that ended without being remembered', async () => {
      const { guide } = await onDetailPage();
      guide.areaShown('map');
      guide.started('map');
      guide.areaShown('map');
      guide.ended('map', false); // the session lost the permission, say: nothing is remembered

      guide.areaShown('timeline');
      guide.started('timeline');
      guide.ended('timeline', true);

      expect(guide.request()).toBeNull(); // the map was not left in the queue
    });

    it('queues an area once however often it is announced, which an abandoned request shows', async () => {
      const { guide } = await onDetailPage();
      guide.areaShown('map');
      guide.areaShown('retry');
      guide.areaShown('retry');
      guide.abandoned(); // the map never came on screen
      expect(guide.request()?.area.id).toBe('retry');

      guide.abandoned(); // nor did the retry row

      expect(guide.request()).toBeNull(); // it was not queued a second time
    });

    it('does not queue the area that is already being asked for behind itself', async () => {
      const { guide } = await onDetailPage();
      guide.areaShown('map');
      guide.areaShown('map');

      guide.abandoned();

      expect(guide.request()).toBeNull();
    });

    it('starts the next queued area when the request in front was abandoned', async () => {
      const { guide } = await onDetailPage();
      guide.areaShown('map');
      guide.areaShown('timeline');

      guide.abandoned();

      expect(guide.request()?.area.id).toBe('timeline');
    });

    it('skips a queued area that was seen, or that the session lost, by the time its turn comes', async () => {
      const { guide } = await onDetailPage();
      guide.areaShown('map');
      guide.started('map');
      guide.areaShown('timeline');
      guide.areaShown('data');
      guide.areaShown('retry');

      guide.ended('timeline', true); // not the running one: ignored
      held.set(['sagas.view', 'sagas.retry']); // the session loses sagas.data
      guide.ended('map', true);
      expect(guide.request()?.area.id).toBe('timeline');

      guide.started('timeline');
      guide.ended('timeline', true); // the queue holds data, now refused, then retry
      expect(guide.request()?.area.id).toBe('retry');
    });

    it('does not start a queued area after a tour that was not remembered', async () => {
      const { guide } = await onDetailPage();
      guide.areaShown('map');
      guide.started('map');
      guide.areaShown('timeline');

      guide.ended('map', false);

      expect(guide.request()).toBeNull();
    });

    it('drops the queue when the page changes', async () => {
      const { guide, router } = await onDetailPage();
      guide.areaShown('map');
      guide.started('map');
      guide.areaShown('timeline');

      await router.navigateByUrl('/account');
      await router.navigateByUrl('/sagas/OrderSaga/abc');
      guide.started('summary');
      guide.ended('summary', true);

      expect(guide.request()).toBeNull();
    });

    it('ignores an area the session may not see', async () => {
      const { guide } = await onDetailPage();
      held.set(['sagas.view']); // no sagas.data

      guide.areaShown('data');

      expect(guide.request()).toBeNull();
      expect(guide.area()?.id).not.toBe('data');
    });
  });
});
