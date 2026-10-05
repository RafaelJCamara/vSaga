import { ChangeDetectionStrategy, Component, WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { Mock, vi } from 'vitest';
import { PermissionKey } from '../../models/auth.model';
import { GuidePermissionCheck, GuideStep } from '../../models/guide.model';
import { USER_GUIDE_URL } from '../../services/guide-areas';
import { GUIDE_PERMISSION_CHECK, GuideService } from '../../services/guide.service';
import {
  MemoryStorage,
  createGuideStorage,
  provideGuideStorage,
  storedGuide,
} from '../../testing/guide';
import { GuideOverlay } from './guide-overlay';
import { GUIDE_TOURS, GuideAnchor } from './guide-tours';

/** The app shell in miniature: a page (the overlay's sibling, which it makes inert) with the toggle in it,
 *  and the overlay as the last child. */
@Component({
  selector: 'app-test-host',
  imports: [GuideOverlay],
  template: `
    <div id="page">
      <div data-tour="topbar-guide">
        <button id="guide-switch" class="guide-switch" type="button">Guide</button>
      </div>
      <button id="other" type="button">Somewhere else</button>
    </div>
    <app-guide-overlay />
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
})
class Host {}

@Component({
  selector: 'app-page-stub',
  template: '',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class PageStub {}

interface Box {
  top: number;
  left: number;
  width: number;
  height: number;
}

interface FakeAnchor {
  el: HTMLElement;
  /** Mutable: the next `getBoundingClientRect` reads it. */
  box: Box;
}

const ALL: readonly PermissionKey[] = ['sagas.view', 'sagas.data', 'sagas.retry', 'access.manage'];

describe('GuideOverlay', () => {
  let storage: MemoryStorage;
  let held: WritableSignal<readonly PermissionKey[]>;
  let check: Mock<GuidePermissionCheck>;
  let guide: GuideService;
  let router: Router;
  let fixture: ComponentFixture<Host>;
  let root: HTMLElement;
  let page: HTMLElement;
  /** The animation frames asked for and not yet run (the page's own stub of `requestAnimationFrame`). */
  let frames: Map<number, FrameRequestCallback>;
  let cancelFrame: Mock<(id: number) => void>;
  let nextFrame: number;

  /** Steps replaced in the tour table for a spec; restored after it. */
  const replaced: Record<string, readonly GuideStep<GuideAnchor>[]> = {};

  async function setup(url = '/sagas', stored?: unknown) {
    storage = createGuideStorage(stored);
    held = signal<readonly PermissionKey[]>(ALL);
    check = vi.fn<GuidePermissionCheck>((permission) => held().includes(permission));
    frames = new Map();
    nextFrame = 0;
    cancelFrame = vi.fn((id: number) => void frames.delete(id));
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
      frames.set(++nextFrame, cb);
      return nextFrame;
    });
    vi.stubGlobal('cancelAnimationFrame', cancelFrame);

    TestBed.configureTestingModule({
      imports: [Host],
      providers: [
        provideRouter([
          { path: 'sagas', component: PageStub },
          { path: 'sagas/:sagaType/:id', component: PageStub },
          { path: 'account', component: PageStub },
        ]),
        provideGuideStorage(storage),
        { provide: GUIDE_PERMISSION_CHECK, useValue: check },
      ],
    });
    guide = TestBed.inject(GuideService);
    router = TestBed.inject(Router);
    await router.navigateByUrl(url);
    fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
    page = root.querySelector('#page') as HTMLElement;
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  }

  afterEach(() => {
    for (const id of Object.keys(replaced)) {
      (GUIDE_TOURS as Record<string, readonly GuideStep[]>)[id] = replaced[id];
      delete replaced[id];
    }
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  /** Puts a tour of `steps` in the table for `area` until the end of the spec. */
  function tourFor(area: 'summary' | 'map', steps: readonly GuideStep<GuideAnchor>[]): void {
    replaced[area] ??= GUIDE_TOURS[area];
    GUIDE_TOURS[area] = steps;
  }

  function addAnchor(
    id: GuideAnchor,
    box: Partial<Box> = {},
    parent: HTMLElement = page,
  ): FakeAnchor {
    const el = document.createElement('div');
    el.setAttribute('data-tour', id);
    const fake: FakeAnchor = { el, box: { top: 100, left: 50, width: 300, height: 40, ...box } };
    el.getBoundingClientRect = () =>
      ({
        ...fake.box,
        x: fake.box.left,
        y: fake.box.top,
        right: fake.box.left + fake.box.width,
        bottom: fake.box.top + fake.box.height,
        toJSON: () => ({}),
      }) as DOMRect;
    el.scrollIntoView = vi.fn();
    parent.appendChild(el);
    return fake;
  }

  /** The anchors of the list page, each somewhere different. */
  function listPage(): Record<string, FakeAnchor> {
    return {
      filters: addAnchor('list-filters', { top: 80, height: 44 }),
      table: addAnchor('list-table', { top: 140, height: 300 }),
      sort: addAnchor('list-sort', { top: 140, left: 400, width: 80, height: 30 }),
      row: addAnchor('list-row', { top: 180, height: 36 }),
      pagination: addAnchor('list-pagination', { top: 460, height: 40 }),
    };
  }

  /** Lets effects run and the view render. */
  function flush(): void {
    fixture.detectChanges();
    fixture.detectChanges();
  }

  const popover = () => root.querySelector('.guide-popover') as HTMLElement | null;
  const spot = () => root.querySelector('.guide-spot') as HTMLElement | null;
  const shield = () => root.querySelector('.guide-shield') as HTMLElement | null;
  const title = () => root.querySelector('.guide-title')?.textContent?.trim();
  const progress = () =>
    root.querySelector('.guide-progress')?.textContent?.trim().replace(/\s+/g, ' ');
  const button = (label: string) =>
    Array.from(root.querySelectorAll<HTMLElement>('.guide-popover button')).find(
      (b) => b.textContent?.trim() === label,
    );
  const press = (label: string) => {
    (button(label) as HTMLElement).click();
    flush();
  };
  const key = (name: string, init: KeyboardEventInit = {}) => {
    const event = new KeyboardEvent('keydown', {
      key: name,
      bubbles: true,
      cancelable: true,
      ...init,
    });
    document.dispatchEvent(event);
    flush();
    return event;
  };
  const runFrame = () => {
    const due = [...frames.entries()];
    frames.clear();
    for (const [, cb] of due) cb(0);
  };
  const overlay = () =>
    fixture.debugElement.query(By.directive(GuideOverlay)).componentInstance as unknown as Record<
      string,
      unknown
    >;

  /** Switches Guide on from the list page with every anchor there, and renders the first step. */
  async function startList(stored?: unknown) {
    await setup('/sagas', stored);
    const anchors = listPage();
    guide.setEnabled(true);
    flush();
    return anchors;
  }

  describe('while Guide is idle', () => {
    it('renders nothing, makes nothing inert and listens to no key', async () => {
      await setup('/sagas');
      listPage();
      const add = vi.spyOn(document, 'addEventListener');
      flush();

      expect(popover()).toBeNull();
      expect(spot()).toBeNull();
      expect(shield()).toBeNull();
      expect(page.hasAttribute('inert')).toBe(false);
      expect(add.mock.calls.filter((c) => c[0] === 'keydown')).toHaveLength(0);
    });

    it('stays idle with Guide on when the area was seen', async () => {
      await setup('/sagas', { v: 1, enabled: true, seen: { list: 1 }, hintDismissed: true });
      listPage();
      flush();

      expect(popover()).toBeNull();
      expect(guide.running()).toBeNull();
    });
  });

  describe('waiting for the area to be on screen', () => {
    it('begins at once when the ready anchor is already there', async () => {
      await startList();

      expect(popover()).not.toBeNull();
      expect(guide.running()).toBe('list');
      expect(guide.request()).toBeNull();
    });

    it('looks again every 250 ms, and begins at the first look that finds it', async () => {
      await setup('/sagas');
      guide.setEnabled(true);
      flush();
      expect(guide.request()?.area.id).toBe('list');
      expect(popover()).toBeNull();

      vi.advanceTimersByTime(600); // looks at 250 and 500: nothing yet
      listPage();
      flush();
      expect(popover()).toBeNull();
      expect(guide.running()).toBeNull();

      vi.advanceTimersByTime(149); // 749: the next look is at 750
      flush();
      expect(popover()).toBeNull();
      vi.advanceTimersByTime(1);
      flush();
      expect(popover()).not.toBeNull();
      expect(guide.running()).toBe('list');
      expect(progress()).toBe('Step 1 of 7');
    });

    it('gives up after twenty looks (about five seconds) and begins with what can be anchored', async () => {
      await setup('/sagas');
      addAnchor('list-filters');
      guide.setEnabled(true);
      flush();

      vi.advanceTimersByTime(4749); // the twentieth look is at 4750
      flush();
      expect(popover()).toBeNull();
      vi.advanceTimersByTime(1);
      flush();

      expect(popover()).not.toBeNull();
      // The welcome (centred), the filters and the top bar: the table's steps have nothing to show.
      expect(progress()).toBe('Step 1 of 3');
    });

    it('abandons the request when nothing on the page can be shown, and remembers nothing', async () => {
      await setup('/sagas');
      page.querySelector('[data-tour="topbar-guide"]')?.removeAttribute('data-tour');
      guide.setEnabled(true);
      flush();

      vi.advanceTimersByTime(5000);
      flush();

      expect(popover()).toBeNull();
      expect(guide.request()).toBeNull();
      expect(guide.running()).toBeNull();
      expect(guide.isSeen('list')).toBe(false);
      expect(page.hasAttribute('inert')).toBe(false);
    });

    it('abandons an area that has no tour at once, without waiting for its page', async () => {
      await setup('/account', { v: 1, enabled: true, seen: {}, hintDismissed: true });
      await router.navigateByUrl('/sagas/OrderSaga/abc'); // no anchor on the page: a tour would wait for it
      flush();

      expect(GUIDE_TOURS.summary).toEqual([]);
      expect(guide.request()).toBeNull();
      expect(popover()).toBeNull();
      expect(guide.isSeen('summary')).toBe(false);
    });

    it('stops looking when Guide is switched off meanwhile', async () => {
      await setup('/sagas');
      guide.setEnabled(true);
      flush();
      vi.advanceTimersByTime(500);

      guide.setEnabled(false);
      flush();
      listPage();
      vi.advanceTimersByTime(5000);
      flush();

      expect(popover()).toBeNull();
      expect(guide.running()).toBeNull();
    });

    it('stops looking when the page is left', async () => {
      await setup('/sagas');
      guide.setEnabled(true);
      flush();
      vi.advanceTimersByTime(500);

      await router.navigateByUrl('/account');
      flush();
      listPage();
      vi.advanceTimersByTime(5000);
      flush();

      expect(popover()).toBeNull();
      expect(guide.running()).toBeNull();
    });

    it('abandons the request if the session no longer holds the area permission when the page is ready', async () => {
      await setup('/sagas');
      guide.setEnabled(true);
      flush();
      vi.advanceTimersByTime(250);
      listPage();
      held.set([]);
      const started = vi.spyOn(guide, 'started');
      vi.advanceTimersByTime(250);
      flush();

      expect(popover()).toBeNull();
      expect(started).not.toHaveBeenCalled(); // it never began, so the page was never made inert
      expect(guide.running()).toBeNull();
      expect(guide.request()).toBeNull();
    });
  });

  describe('beginning', () => {
    it('shows the dialog: a heading, a body, how far along it is, and the way out', async () => {
      await startList();
      const dialog = popover() as HTMLElement;

      expect(dialog.tagName).toBe('SECTION');
      expect(dialog.getAttribute('role')).toBe('dialog');
      expect(dialog.getAttribute('aria-modal')).toBe('true');
      const heading = dialog.querySelector('#guide-title') as HTMLElement;
      const body = dialog.querySelector('#guide-body') as HTMLElement;
      expect(dialog.getAttribute('aria-labelledby')).toBe('guide-title');
      expect(dialog.getAttribute('aria-describedby')).toBe('guide-body');
      expect(heading.textContent?.trim()).toBe('Welcome to the saga dashboard');
      expect(body.textContent).toContain('Press Esc to leave it');
      expect(heading.closest('[aria-live="polite"]')).not.toBeNull();
      expect(body.closest('[aria-live="polite"]')).toBe(heading.closest('[aria-live="polite"]'));
      expect(progress()).toBe('Step 1 of 7');
      expect(button('Skip tour')).toBeTruthy();
      expect(button('Next')).toBeTruthy();
      expect(button('Back')).toBeUndefined();
    });

    it('links the user guide section of the area, in a new tab', async () => {
      await startList();
      const link = popover()?.querySelector('a') as HTMLAnchorElement;

      expect(link.textContent?.trim()).toBe('User guide');
      expect(link.getAttribute('href')).toBe(`${USER_GUIDE_URL}#the-saga-list`);
      expect(link.getAttribute('target')).toBe('_blank');
      expect(link.getAttribute('rel')).toBe('noopener noreferrer');
    });

    it('tells the service it started, which clears the request', async () => {
      await startList();

      expect(guide.running()).toBe('list');
      expect(guide.request()).toBeNull();
      expect(guide.canReplay()).toBe(false);
    });

    it('focuses the primary button', async () => {
      await startList();

      expect(document.activeElement).toBe(button('Next'));
    });

    it('drops the steps whose permission fails and counts the rest', async () => {
      await setup('/account');
      addAnchor('detail-summary');
      addAnchor('detail-data');
      addAnchor('detail-retry');
      tourFor('summary', [
        { id: 'summary-a', title: 'A', body: 'a', anchor: 'detail-summary' },
        { id: 'summary-b', title: 'B', body: 'b', anchor: 'detail-data', requires: 'sagas.data' },
        { id: 'summary-c', title: 'C', body: 'c', anchor: 'detail-retry', requires: 'sagas.retry' },
      ]);
      held.set(['sagas.view', 'sagas.data']);
      guide.setEnabled(true);
      await router.navigateByUrl('/sagas/OrderSaga/abc');
      flush();

      expect(progress()).toBe('Step 1 of 2');
      expect(check).toHaveBeenCalledWith('sagas.retry', 'OrderSaga');
      press('Next');
      expect(title()).toBe('B');
      expect(button('Done')).toBeTruthy(); // no third step
    });

    it('drops the steps with nothing on the page to show, and counts the rest', async () => {
      await setup('/account');
      addAnchor('detail-summary');
      addAnchor('detail-retry'); // a fallback that is there
      addAnchor('detail-tab-timeline'); // a reveal control that is there
      tourFor('summary', [
        { id: 'summary-centred', title: 'Centred', body: 'c', anchor: null },
        { id: 'summary-anchored', title: 'Anchored', body: 'a', anchor: 'detail-summary' },
        { id: 'summary-missing', title: 'Missing', body: 'm', anchor: 'detail-data' },
        {
          id: 'summary-fallback',
          title: 'Fallback',
          body: 'f',
          anchor: 'detail-data',
          fallbackAnchor: 'detail-retry',
        },
        {
          id: 'summary-reveal',
          title: 'Reveal',
          body: 'r',
          anchor: 'timeline',
          reveal: 'detail-tab-timeline',
        },
        {
          id: 'summary-nothing',
          title: 'Nothing',
          body: 'n',
          anchor: 'timeline-entry',
          fallbackAnchor: 'timeline',
          reveal: 'detail-tab-map',
        },
      ]);
      guide.setEnabled(true);
      await router.navigateByUrl('/sagas/OrderSaga/abc');
      flush();

      expect(progress()).toBe('Step 1 of 4');
      const seen: (string | undefined)[] = [title()];
      for (let i = 0; i < 3; i++) {
        press('Next');
        seen.push(title());
      }
      expect(seen).toEqual(['Centred', 'Anchored', 'Fallback', 'Reveal']);
    });

    it('keeps a step whose permission holds for the saga type of the page, not for every type', async () => {
      await setup('/account');
      addAnchor('detail-summary');
      tourFor('summary', [
        { id: 'summary-a', title: 'A', body: 'a', anchor: 'detail-summary' },
        {
          id: 'summary-b',
          title: 'B',
          body: 'b',
          anchor: 'detail-summary',
          requires: 'sagas.data',
        },
      ]);
      check.mockImplementation(
        (permission, sagaType) => permission === 'sagas.view' || sagaType === 'OrderSaga',
      );
      guide.setEnabled(true);

      await router.navigateByUrl('/sagas/OrderSaga/abc');
      flush();
      expect(progress()).toBe('Step 1 of 2');
    });
  });

  describe('moving through the steps', () => {
    it('goes forward with Next and back with Back, saying where it is', async () => {
      await startList();

      press('Next');
      expect(title()).toBe('Narrow the list');
      expect(progress()).toBe('Step 2 of 7');
      expect(button('Back')).toBeTruthy();

      press('Next');
      expect(title()).toBe('One row per saga instance');

      press('Back');
      expect(title()).toBe('Narrow the list');
      press('Back');
      expect(title()).toBe('Welcome to the saga dashboard');
      expect(button('Back')).toBeUndefined();
    });

    it('says Done on the last step', async () => {
      await startList();
      for (let i = 0; i < 6; i++) press('Next');

      expect(title()).toBe('Guide mode');
      expect(progress()).toBe('Step 7 of 7');
      expect(button('Next')).toBeUndefined();
      expect(button('Done')).toBeTruthy();
    });

    it('keeps the dialog in place from one step to the next, so its live region announces the change', async () => {
      await startList();
      const dialog = popover();
      const region = root.querySelector('[aria-live="polite"]');

      press('Next');

      expect(popover()).toBe(dialog);
      expect(root.querySelector('[aria-live="polite"]')).toBe(region);
      expect(region?.textContent).toContain('Step 2 of 7'); // the position is announced with the step
    });

    it('focuses the primary button on every step, even when focus had moved off it', async () => {
      await startList();
      (button('Skip tour') as HTMLElement).focus();
      expect(document.activeElement).toBe(button('Skip tour'));

      key('ArrowRight');
      fixture.detectChanges();

      expect(title()).toBe('Narrow the list');
      expect(document.activeElement).toBe(button('Next'));
    });

    it('moves with the arrow keys, ignoring them with a modifier, and stops at both ends', async () => {
      await startList();

      key('ArrowLeft');
      expect(title()).toBe('Welcome to the saga dashboard');

      const forward = key('ArrowRight');
      expect(title()).toBe('Narrow the list');
      expect(forward.defaultPrevented).toBe(true);

      const withAlt = key('ArrowRight', { altKey: true });
      const withShift = key('ArrowLeft', { shiftKey: true });
      expect(title()).toBe('Narrow the list');
      expect(withAlt.defaultPrevented).toBe(false);
      expect(withShift.defaultPrevented).toBe(false);

      key('ArrowLeft');
      expect(title()).toBe('Welcome to the saga dashboard');

      for (let i = 0; i < 6; i++) key('ArrowRight');
      expect(title()).toBe('Guide mode');
      key('ArrowRight'); // the last step is ended with Done, not with an arrow
      expect(title()).toBe('Guide mode');
      expect(popover()).not.toBeNull();
    });

    it('ignores other keys', async () => {
      await startList();

      const event = key('a');

      expect(title()).toBe('Welcome to the saga dashboard');
      expect(event.defaultPrevented).toBe(false);
    });

    it('keeps Tab inside the dialog: past the last control it goes to the first, and back', async () => {
      await startList();
      const first = popover()?.querySelector('a') as HTMLElement;
      const last = button('Next') as HTMLElement;
      expect(document.activeElement).toBe(last);

      const forward = key('Tab');
      expect(forward.defaultPrevented).toBe(true);
      expect(document.activeElement).toBe(first);

      const backward = key('Tab', { shiftKey: true });
      expect(backward.defaultPrevented).toBe(true);
      expect(document.activeElement).toBe(last);
    });

    it('leaves Tab to the browser between the controls', async () => {
      await startList();
      press('Next'); // now Back is there too
      (button('Back') as HTMLElement).focus();

      expect(key('Tab').defaultPrevented).toBe(false);
      expect(key('Tab', { shiftKey: true }).defaultPrevented).toBe(false);
    });

    it('pulls Tab back into the dialog when focus is outside it', async () => {
      await startList();
      (document.activeElement as HTMLElement).blur();
      expect(document.activeElement).toBe(document.body);

      key('Tab');
      expect(document.activeElement).toBe(popover()?.querySelector('a'));

      (document.activeElement as HTMLElement).blur();
      key('Tab', { shiftKey: true });
      expect(document.activeElement).toBe(button('Next'));
    });

    it('takes a click on the dimmed page away: mousedown on the layer is cancelled', async () => {
      await startList();
      const event = new MouseEvent('mousedown', { bubbles: true, cancelable: true });

      shield()?.dispatchEvent(event);

      expect(event.defaultPrevented).toBe(true);
    });
  });

  describe('ending', () => {
    it('Done ends the tour and remembers it', async () => {
      await startList();
      for (let i = 0; i < 6; i++) press('Next');

      press('Done');

      expect(popover()).toBeNull();
      expect(spot()).toBeNull();
      expect(shield()).toBeNull();
      expect(guide.running()).toBeNull();
      expect(guide.isSeen('list')).toBe(true);
      expect(storedGuide(storage)).toMatchObject({ seen: { list: 1 } });
    });

    it('Escape ends the tour and remembers it', async () => {
      await startList();

      const event = key('Escape');

      expect(event.defaultPrevented).toBe(true);
      expect(popover()).toBeNull();
      expect(guide.running()).toBeNull();
      expect(guide.isSeen('list')).toBe(true);
    });

    it('Skip tour ends the tour and remembers it', async () => {
      await startList();

      press('Skip tour');

      expect(popover()).toBeNull();
      expect(guide.isSeen('list')).toBe(true);
    });

    it('is not started again by the page being opened again once it was remembered', async () => {
      await startList();
      key('Escape');

      await router.navigateByUrl('/account');
      await router.navigateByUrl('/sagas');
      flush();

      expect(popover()).toBeNull();
      expect(guide.request()).toBeNull();
    });

    it('can be replayed from the first step, and ends and remembers again', async () => {
      await startList();
      press('Next');
      key('Escape');

      guide.replay();
      flush();

      expect(popover()).not.toBeNull();
      expect(title()).toBe('Welcome to the saga dashboard');
      expect(progress()).toBe('Step 1 of 7');
    });

    it('shows a replayed tour whose first step lands exactly where the last step of the one before did', async () => {
      await startList();
      runFrame(); // the welcome, centred
      expect(popover()?.style.visibility).toBe('visible');
      key('Escape');

      guide.replay();
      flush();
      runFrame();

      expect(title()).toBe('Welcome to the saga dashboard');
      expect(popover()?.style.visibility).toBe('visible'); // a fresh dialog: the old positions were not reused
      expect(popover()?.style.top).toBe('384px');
    });

    it('starts the next area that was announced while it ran, once it is done', async () => {
      await setup('/account');
      addAnchor('detail-summary');
      addAnchor('map-canvas');
      tourFor('summary', [
        { id: 'summary-a', title: 'Summary', body: 's', anchor: 'detail-summary' },
      ]);
      tourFor('map', [{ id: 'map-a', title: 'Map', body: 'm', anchor: 'map-canvas' }]);
      guide.setEnabled(true);
      await router.navigateByUrl('/sagas/OrderSaga/abc');
      flush();
      expect(title()).toBe('Summary');
      guide.areaShown('map');

      press('Done');

      expect(title()).toBe('Map');
      expect(guide.running()).toBe('map');
    });
  });

  describe('the page behind it', () => {
    it('makes the siblings of the overlay inert while the tour runs, and the overlay itself not', async () => {
      await startList();

      expect(page.hasAttribute('inert')).toBe(true);
      expect(root.querySelector('app-guide-overlay')?.hasAttribute('inert')).toBe(false);
      expect(popover()?.closest('[inert]')).toBeNull();
    });

    it('leaves a sibling alone that was inert already', async () => {
      await setup('/sagas');
      listPage();
      page.setAttribute('inert', '');
      guide.setEnabled(true);
      flush();
      key('Escape');

      expect(page.hasAttribute('inert')).toBe(true);
    });

    it('listens to the keys only while the tour runs', async () => {
      await setup('/sagas');
      listPage();
      const add = vi.spyOn(document, 'addEventListener');
      const remove = vi.spyOn(document, 'removeEventListener');
      guide.setEnabled(true);
      flush();
      expect(add.mock.calls.filter((c) => c[0] === 'keydown')).toHaveLength(1);
      expect(remove.mock.calls.filter((c) => c[0] === 'keydown')).toHaveLength(0);

      key('Escape');
      expect(remove.mock.calls.filter((c) => c[0] === 'keydown')).toHaveLength(1);
      const handler = add.mock.calls.find((c) => c[0] === 'keydown')?.[1];
      expect(remove.mock.calls.find((c) => c[0] === 'keydown')?.[1]).toBe(handler);
    });

    const exits: [name: string, remembered: boolean, end: () => Promise<void>][] = [
      [
        'Done',
        true,
        async () => {
          for (let i = 0; i < 6; i++) press('Next');
          press('Done');
        },
      ],
      ['Escape', true, async () => void key('Escape')],
      ['Skip tour', true, async () => press('Skip tour')],
      [
        'Guide switched off',
        false,
        async () => {
          guide.setEnabled(false);
          flush();
        },
      ],
      [
        'leaving the page',
        false,
        async () => {
          await router.navigateByUrl('/account');
          flush();
        },
      ],
      [
        'the session losing the permission',
        false,
        async () => {
          held.set([]);
          flush();
        },
      ],
      ['the overlay being destroyed', false, async () => fixture.destroy()],
    ];

    describe.each(exits)('when it ends with %s', (_name, remembered, end) => {
      it('gives the page back', async () => {
        await startList();
        expect(page.hasAttribute('inert')).toBe(true);

        await end();

        expect(page.hasAttribute('inert')).toBe(false);
        expect(guide.running()).toBeNull();
      });

      it(remembered ? 'remembers the tour' : 'does not remember the tour', async () => {
        await startList();

        await end();

        expect(guide.isSeen('list')).toBe(remembered);
        expect(storedGuide(storage)).toMatchObject({ seen: remembered ? { list: 1 } : {} });
      });

      it('takes the key listener, the timers and the frame away', async () => {
        await setup('/sagas');
        listPage();
        const add = vi.spyOn(document, 'addEventListener');
        const remove = vi.spyOn(document, 'removeEventListener');
        guide.setEnabled(true);
        flush();
        const handler = add.mock.calls.find((c) => c[0] === 'keydown')?.[1];
        expect(handler).toBeDefined();
        expect(remove).not.toHaveBeenCalledWith('keydown', handler);
        const instance = overlay();
        const frame = instance['frame'] as number;
        expect(frames.has(frame)).toBe(true);

        await end();

        expect(remove).toHaveBeenCalledWith('keydown', handler); // the very handler that was added
        expect(frames.has(frame)).toBe(false);
        expect(cancelFrame).toHaveBeenCalledWith(frame);
        expect(instance['frame']).toBeNull();
        expect(instance['pollTimer']).toBeNull();
        expect(instance['revealTimer']).toBeNull();
      });
    });
  });

  describe('focus', () => {
    it('returns to the element that had it when the tour began', async () => {
      await setup('/sagas');
      listPage();
      const other = page.querySelector('#other') as HTMLElement;
      other.focus();
      guide.setEnabled(true);
      flush();
      expect(document.activeElement).toBe(button('Next'));

      key('Escape');

      expect(document.activeElement).toBe(other);
    });

    it('goes to the Guide switch when nothing in the page had it', async () => {
      await setup('/sagas');
      listPage();
      expect(document.activeElement).toBe(document.body);
      guide.setEnabled(true);
      flush();

      key('Escape');

      expect(document.activeElement).toBe(page.querySelector('#guide-switch'));
    });

    it('goes to the Guide switch when the element that had it is gone', async () => {
      await setup('/sagas');
      listPage();
      const other = page.querySelector('#other') as HTMLElement;
      other.focus();
      guide.setEnabled(true);
      flush();
      other.remove();

      key('Escape');

      expect(document.activeElement).toBe(page.querySelector('#guide-switch'));
    });

    it('gives the page back before it moves the focus: the element it returns to is never inert when it is focused', async () => {
      await setup('/sagas');
      listPage();
      const other = page.querySelector('#other') as HTMLElement;
      other.focus();
      guide.setEnabled(true);
      flush();
      let sawInert: boolean | null = null;
      vi.spyOn(other, 'focus').mockImplementation(() => {
        sawInert = page.hasAttribute('inert');
      });

      key('Escape');

      expect(sawInert).toBe(false); // an inert element cannot take the focus, so the page comes back first
    });

    it('does the same when it falls back on the Guide switch', async () => {
      await setup('/sagas');
      listPage();
      guide.setEnabled(true);
      flush();
      const toggle = page.querySelector('#guide-switch') as HTMLElement;
      let sawInert: boolean | null = null;
      vi.spyOn(toggle, 'focus').mockImplementation(() => {
        sawInert = page.hasAttribute('inert');
      });

      key('Escape');

      expect(sawInert).toBe(false);
    });

    describe('when the tour ends because the page changed or the session lost the permission', () => {
      const ends: [string, () => Promise<void>][] = [
        [
          'the page changed',
          async () => {
            await router.navigateByUrl('/account');
            flush();
          },
        ],
        [
          'the permission was lost',
          async () => {
            held.set([]);
            flush();
          },
        ],
      ];

      describe.each(ends)('after %s', (_name, end) => {
        it('leaves the focus where the next page put it', async () => {
          await setup('/sagas');
          listPage();
          guide.setEnabled(true);
          flush();
          // The page that follows has placed the focus on a control of its own.
          const other = page.querySelector('#other') as HTMLElement;
          other.focus();
          expect(popover()?.contains(document.activeElement)).toBe(false);

          await end();

          expect(document.activeElement).toBe(other);
        });

        it('takes the focus from the dialog, which is about to go, to the Guide switch', async () => {
          await setup('/sagas');
          listPage();
          guide.setEnabled(true);
          flush();
          expect(popover()?.contains(document.activeElement)).toBe(true);

          await end();

          expect(document.activeElement).toBe(page.querySelector('#guide-switch'));
        });

        it('takes the focus from the body to the Guide switch', async () => {
          await setup('/sagas');
          listPage();
          guide.setEnabled(true);
          flush();
          (document.activeElement as HTMLElement).blur();
          expect(document.activeElement).toBe(document.body);

          await end();

          expect(document.activeElement).toBe(page.querySelector('#guide-switch'));
        });
      });
    });

    it('is back on the page before the dialog is removed, so it is never lost to the body', async () => {
      await setup('/sagas');
      listPage();
      (page.querySelector('#other') as HTMLElement).focus();
      guide.setEnabled(true);
      flush();

      const keyEvent = new KeyboardEvent('keydown', {
        key: 'Escape',
        bubbles: true,
        cancelable: true,
      });
      document.dispatchEvent(keyEvent);

      // No change detection has run yet: the dialog is still in the page, and the focus is already out of it.
      expect(popover()).not.toBeNull();
      expect(document.activeElement?.id).toBe('other');
    });
  });

  describe('following the element', () => {
    it('puts the spotlight on the element, grown by its padding, and the popover below it', async () => {
      await startList();
      press('Next'); // the filters: top 80, left 50, 300 x 44
      runFrame();

      expect(spot()?.style.display).toBe('block');
      expect(spot()?.style.top).toBe('74px');
      expect(spot()?.style.left).toBe('44px');
      expect(spot()?.style.width).toBe('312px');
      expect(spot()?.style.height).toBe('56px');
      expect(shield()?.classList).not.toContain('guide-shield--dim');
      expect(popover()?.style.top).toBe('142px'); // 74 + 56 + the gap of 12
      expect(popover()?.style.visibility).toBe('visible');
    });

    it('has no spotlight on a step that is centred by design: the layer dims the page and the popover is centred', async () => {
      await startList();
      runFrame();

      expect(spot()?.style.display).toBe('none');
      expect(shield()?.classList).toContain('guide-shield--dim');
      expect(popover()?.style.top).toBe('384px'); // the middle of the 768 px viewport
      expect(popover()?.style.left).toBe('512px');
    });

    it('starts on a step that is already on screen before any frame has run', async () => {
      await startList();
      press('Next');

      expect(spot()?.style.display).toBe('block');
      expect(popover()?.style.visibility).toBe('visible');
    });

    it('asks for the next frame, one at a time, for as long as the tour runs', async () => {
      await startList();
      expect(frames.size).toBe(1);

      runFrame();
      expect(frames.size).toBe(1);
    });

    it('follows the element when it moves or resizes', async () => {
      const anchors = await startList();
      press('Next');
      runFrame();
      expect(spot()?.style.top).toBe('74px');

      anchors['filters'].box.top = 300;
      anchors['filters'].box.width = 500;
      runFrame();

      expect(spot()?.style.top).toBe('294px');
      expect(spot()?.style.width).toBe('512px');
      expect(popover()?.style.top).toBe(`${294 + 56 + 12}px`);
    });

    it('writes nothing when a frame finds the element where it was', async () => {
      await startList();
      press('Next');
      runFrame();
      spot()!.style.top = '1px'; // something only a write would change
      popover()!.style.top = '2px';

      runFrame();

      expect(spot()?.style.top).toBe('1px');
      expect(popover()?.style.top).toBe('2px');
    });

    it('rounds before it compares, so a sub-pixel change writes nothing', async () => {
      const anchors = await startList();
      press('Next');
      runFrame();
      spot()!.style.top = '1px';

      anchors['filters'].box.top = 80.2;
      runFrame();

      expect(spot()?.style.top).toBe('1px');
    });

    it('centres the popover, with no spotlight, when the element disappears mid-tour, and goes back when it returns', async () => {
      const anchors = await startList();
      press('Next');
      runFrame();
      expect(spot()?.style.display).toBe('block');

      anchors['filters'].el.remove();
      runFrame();

      expect(guide.running()).toBe('list'); // the step is not skipped
      expect(title()).toBe('Narrow the list');
      expect(progress()).toBe('Step 2 of 7');
      expect(spot()?.style.display).toBe('none');
      expect(shield()?.classList).toContain('guide-shield--dim');
      expect(popover()?.style.top).toBe('384px');
      expect(popover()?.style.left).toBe('512px');

      addAnchor('list-filters', { top: 200, height: 50 });
      runFrame();

      expect(spot()?.style.display).toBe('block');
      expect(spot()?.style.top).toBe('194px');
      expect(shield()?.classList).not.toContain('guide-shield--dim');
    });

    it('looks for the element again by its anchor when the page re-renders it', async () => {
      const anchors = await startList();
      press('Next');
      runFrame();

      anchors['filters'].el.remove();
      addAnchor('list-filters', { top: 400, height: 20 }); // a new element, in the same frame
      runFrame();

      expect(spot()?.style.top).toBe('394px');
    });

    it('highlights the fallback instead when the element goes and the fallback is there', async () => {
      const anchors = await startList();
      for (let i = 0; i < 3; i++) press('Next');
      expect(title()).toBe('Sort by status or last update');
      runFrame();
      expect(spot()?.style.top).toBe(`${anchors['sort'].box.top - 6}px`);

      anchors['sort'].el.remove();
      runFrame();

      expect(spot()?.style.display).toBe('block'); // not centred: the table is still there
      expect(spot()?.style.top).toBe(`${anchors['table'].box.top - 6}px`);
    });

    it('scrolls the fallback into view when it is what is shown', async () => {
      await setup('/sagas');
      addAnchor('list-filters');
      const table = addAnchor('list-table', { top: 2000, height: 300 });
      guide.setEnabled(true);
      flush();
      for (let i = 0; i < 2; i++) press('Next');
      expect(title()).toBe('One row per saga instance'); // the table is this step's own element
      (table.el.scrollIntoView as Mock).mockClear();

      press('Next');

      expect(title()).toBe('Sort by status or last update'); // its own element is missing: the table is shown
      expect(table.el.scrollIntoView).toHaveBeenCalledWith(
        expect.objectContaining({ block: 'center' }),
      );
    });

    it('moves the popover when only its size changed, with the element where it was', async () => {
      await startList();
      for (let i = 0; i < 5; i++) press('Next');
      runFrame();
      const spotTop = Number.parseInt(spot()?.style.top ?? '');
      expect(popover()?.style.top).toBe(`${spotTop - 12}px`); // above the pager, whose popover is empty here

      Object.defineProperty(popover(), 'offsetHeight', { value: 200, configurable: true });
      runFrame();

      expect(popover()?.style.top).toBe(`${spotTop - 12 - 200}px`);
    });

    it('does nothing, and asks for no more frames, when a frame that was already queued runs after the tour ended', async () => {
      await startList();
      const late = [...frames.values()][0];
      key('Escape');
      frames.clear();

      late(0);

      expect(frames.size).toBe(0);
    });

    it('highlights the fallback when the anchor of a step is not on the page', async () => {
      await setup('/sagas');
      addAnchor('list-filters');
      addAnchor('list-table', { top: 300, height: 200 });
      guide.setEnabled(true);
      flush();
      for (let i = 0; i < 3; i++) press('Next');
      expect(title()).toBe('Sort by status or last update'); // list-sort is missing: the table is shown
      runFrame();

      expect(spot()?.style.display).toBe('block');
      expect(spot()?.style.top).toBe('294px');
      expect(spot()?.style.height).toBe('212px');
    });

    it('scrolls an element that is off screen into view, centred and smoothly', async () => {
      const anchors = await startList();
      anchors['pagination'].box.top = 2000;
      for (let i = 0; i < 5; i++) press('Next');

      expect(title()).toBe('Page through results');
      expect(anchors['pagination'].el.scrollIntoView).toHaveBeenCalledWith({
        block: 'center',
        inline: 'nearest',
        behavior: 'smooth',
      });
    });

    it('puts the popover of a step that prefers the top above its element', async () => {
      await startList();
      for (let i = 0; i < 5; i++) press('Next');
      runFrame();

      expect(title()).toBe('Page through results'); // the pager: it asks for the top
      expect(Number.parseInt(popover()?.style.top ?? '')).toBeLessThan(
        Number.parseInt(spot()?.style.top ?? ''),
      );
    });

    it('does not scroll an element that is on screen', async () => {
      const anchors = await startList();
      for (let i = 0; i < 5; i++) press('Next');

      expect(anchors['pagination'].el.scrollIntoView).not.toHaveBeenCalled();
    });

    it('scrolls to the top of an element taller than the screen that starts off screen', async () => {
      const anchors = await startList();
      anchors['table'].box.top = 900;
      anchors['table'].box.height = 2000;
      for (let i = 0; i < 2; i++) press('Next');

      expect(anchors['table'].el.scrollIntoView).toHaveBeenCalledWith({
        block: 'start',
        inline: 'nearest',
        behavior: 'smooth',
      });
    });

    it('counts an element taller than the screen as in view when its top is', async () => {
      const anchors = await startList();
      anchors['table'].box.top = 100;
      anchors['table'].box.height = 2000;
      for (let i = 0; i < 2; i++) press('Next');

      expect(anchors['table'].el.scrollIntoView).not.toHaveBeenCalled();
    });

    it('scrolls without animation when the user prefers reduced motion', async () => {
      const anchors = await startList();
      vi.stubGlobal('matchMedia', (query: string) => ({ matches: query.includes('reduce') }));
      anchors['pagination'].box.top = 2000;
      for (let i = 0; i < 5; i++) press('Next');

      expect(anchors['pagination'].el.scrollIntoView).toHaveBeenCalledWith(
        expect.objectContaining({ behavior: 'auto' }),
      );
    });
  });

  describe('a step behind a control', () => {
    beforeEach(() => {
      vi.stubGlobal('matchMedia', undefined);
    });

    async function revealSetup() {
      await setup('/account');
      addAnchor('detail-summary');
      tourFor('summary', [
        { id: 'summary-a', title: 'First', body: 'a', anchor: 'detail-summary' },
        {
          id: 'summary-b',
          title: 'Timeline',
          body: 'b',
          anchor: 'timeline',
          reveal: 'detail-tab-timeline',
          fallbackAnchor: 'detail-summary',
        },
        { id: 'summary-c', title: 'Last', body: 'c', anchor: 'detail-summary' },
      ]);
      const control = addAnchor('detail-tab-timeline');
      const clicks: boolean[] = [];
      control.el.addEventListener('click', () => {
        clicks.push(page.hasAttribute('inert'));
        // The page shows the tab a moment later.
        setTimeout(() => addAnchor('timeline', { top: 300, height: 100 }), 120);
      });
      guide.setEnabled(true);
      await router.navigateByUrl('/sagas/OrderSaga/abc');
      flush();
      return { control, clicks };
    }

    it('clicks the control, once, with the page live for the click, and highlights what appears', async () => {
      const { clicks } = await revealSetup();
      press('Next');

      expect(clicks).toEqual([false]); // the page was not inert while it was clicked
      expect(page.hasAttribute('inert')).toBe(true); // and is again
      vi.advanceTimersByTime(150);
      flush();
      runFrame();

      expect(title()).toBe('Timeline');
      expect(spot()?.style.top).toBe('294px');
      expect(clicks).toHaveLength(1);
      expect(document.activeElement).toBe(button('Next'));
    });

    it('does not click when the anchor is on the page already', async () => {
      const { clicks } = await revealSetup();
      addAnchor('timeline', { top: 300 });

      press('Next');

      expect(clicks).toEqual([]);
    });

    it('waits a second for the element, then falls back to the step fallback', async () => {
      await setup('/account');
      addAnchor('detail-summary', { top: 50 });
      tourFor('summary', [
        { id: 'summary-a', title: 'First', body: 'a', anchor: 'detail-summary' },
        {
          id: 'summary-b',
          title: 'Timeline',
          body: 'b',
          anchor: 'timeline',
          reveal: 'detail-tab-timeline',
          fallbackAnchor: 'detail-summary',
        },
      ]);
      addAnchor('detail-tab-timeline'); // a control that shows nothing
      guide.setEnabled(true);
      await router.navigateByUrl('/sagas/OrderSaga/abc');
      flush();

      press('Next');
      vi.advanceTimersByTime(950);
      flush();
      runFrame();
      expect(spot()?.style.display).not.toBe('block'); // still waiting: nothing highlighted yet

      vi.advanceTimersByTime(50);
      flush();
      runFrame();

      expect(spot()?.style.display).toBe('block');
      expect(spot()?.style.top).toBe('44px');
      expect(title()).toBe('Timeline');
    });

    it('drops a reveal that was waiting when the user moves on', async () => {
      await revealSetup();
      press('Next'); // waiting for the timeline
      press('Back');
      vi.advanceTimersByTime(300);
      flush();
      runFrame();

      expect(title()).toBe('First'); // the late arrival of the timeline changed nothing
      expect(overlay()['revealTimer']).toBeNull();
    });
  });

  describe('leaving mid-tour', () => {
    it('ends without remembering when Guide is switched off', async () => {
      await startList();
      press('Next');

      guide.setEnabled(false);
      flush();

      expect(popover()).toBeNull();
      expect(guide.isSeen('list')).toBe(false);
      expect(storedGuide(storage)).toMatchObject({ enabled: false, seen: {} });
    });

    it('ends when the page changes, and the next page starts its own tour', async () => {
      await startList();
      addAnchor('detail-summary');
      tourFor('summary', [
        { id: 'summary-a', title: 'Summary', body: 's', anchor: 'detail-summary' },
      ]);

      await router.navigateByUrl('/sagas/OrderSaga/abc');
      flush();

      expect(page.hasAttribute('inert')).toBe(true);
      expect(title()).toBe('Summary');
      expect(guide.running()).toBe('summary');
      expect(guide.isSeen('list')).toBe(false);
    });

    it('is not ended by the list changing its query: that is the same page', async () => {
      await startList();

      await router.navigateByUrl('/sagas?status=Failed');
      flush();

      expect(popover()).not.toBeNull();
      expect(guide.running()).toBe('list');
    });

    it('runs one frame loop at a time when the next page starts its tour', async () => {
      await startList();
      addAnchor('detail-summary');
      tourFor('summary', [
        { id: 'summary-a', title: 'Summary', body: 's', anchor: 'detail-summary' },
      ]);
      const first = overlay()['frame'] as number;

      await router.navigateByUrl('/sagas/OrderSaga/abc');
      flush();

      expect(title()).toBe('Summary');
      expect(cancelFrame).toHaveBeenCalledWith(first);
      expect(frames.size).toBe(1);
      expect(frames.has(overlay()['frame'] as number)).toBe(true);
    });

    it('ends when the session ends', async () => {
      await startList();

      held.set([]);
      flush();

      expect(popover()).toBeNull();
      expect(page.hasAttribute('inert')).toBe(false);
    });

    it('cancels the polling when it is destroyed while waiting, and asks nothing more', async () => {
      await setup('/sagas');
      guide.setEnabled(true);
      flush();
      expect(guide.request()).not.toBeNull();

      fixture.destroy();
      listPage();
      vi.advanceTimersByTime(6000);

      expect(guide.running()).toBeNull();
      expect(guide.request()).toBeNull();
    });

    it('cancels the frame and the reveal wait when it is destroyed mid-tour', async () => {
      await setup('/account');
      addAnchor('detail-summary');
      tourFor('summary', [
        { id: 'summary-a', title: 'First', body: 'a', anchor: 'detail-summary' },
        {
          id: 'summary-b',
          title: 'Timeline',
          body: 'b',
          anchor: 'timeline',
          reveal: 'detail-tab-timeline',
        },
      ]);
      addAnchor('detail-tab-timeline');
      guide.setEnabled(true);
      await router.navigateByUrl('/sagas/OrderSaga/abc');
      flush();
      press('Next'); // waiting for a timeline that never comes
      const instance = overlay();
      const frame = instance['frame'] as number;
      const timer = instance['revealTimer'];
      expect(timer).not.toBeNull();
      const clear = vi.spyOn(globalThis, 'clearTimeout');

      fixture.destroy();

      expect(cancelFrame).toHaveBeenCalledWith(frame);
      expect(frames.has(frame)).toBe(false);
      expect(clear).toHaveBeenCalledWith(timer);
      expect(instance['revealTimer']).toBeNull();
      vi.advanceTimersByTime(3000); // nothing is left to run against the destroyed view
    });
  });
});
