import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { USER_GUIDE_URL } from '../../services/guide-areas';
import { GuideService } from '../../services/guide.service';
import {
  MemoryStorage,
  createGuideStorage,
  provideGuideStorage,
  storedGuide,
} from '../../testing/guide';
import { GuideToggle } from './guide-toggle';

@Component({
  selector: 'app-page-stub',
  template: '',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class PageStub {}

describe('GuideToggle', () => {
  let storage: MemoryStorage;

  async function create(url = '/sagas', stored?: unknown) {
    storage = createGuideStorage(stored);
    TestBed.configureTestingModule({
      imports: [GuideToggle],
      providers: [
        provideRouter([
          { path: 'sagas', component: PageStub },
          { path: 'account', component: PageStub },
        ]),
        provideGuideStorage(storage),
      ],
    });
    const guide = TestBed.inject(GuideService);
    await TestBed.inject(Router).navigateByUrl(url);
    const fixture = TestBed.createComponent(GuideToggle);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      guide,
      root,
      switchButton: () => root.querySelector('button.guide-switch') as HTMLButtonElement,
      buttons: () => Array.from(root.querySelectorAll('button'), (b) => b.textContent?.trim()),
      hint: () => root.querySelector('.guide-hint') as HTMLElement | null,
    };
  }

  const on = { v: 1, enabled: true, seen: { list: 1 }, hintDismissed: true };

  it('is the anchor of the top bar step', async () => {
    const { root } = await create();

    expect(root.getAttribute('data-tour')).toBe('topbar-guide');
  });

  describe('the Guide switch', () => {
    it('is a button that says Guide, and is not pressed while Guide is off', async () => {
      const { switchButton } = await create();

      expect(switchButton().type).toBe('button');
      expect(switchButton().textContent?.trim()).toBe('Guide');
      expect(switchButton().getAttribute('aria-pressed')).toBe('false');
    });

    it('is pressed while Guide is on', async () => {
      const { switchButton } = await create('/sagas', on);

      expect(switchButton().getAttribute('aria-pressed')).toBe('true');
    });

    it('switches Guide on and off, and says so', async () => {
      const { fixture, guide, switchButton } = await create('/account');

      switchButton().click();
      fixture.detectChanges();
      expect(guide.enabled()).toBe(true);
      expect(switchButton().getAttribute('aria-pressed')).toBe('true');
      expect(storedGuide(storage)).toMatchObject({ enabled: true });

      switchButton().click();
      fixture.detectChanges();
      expect(guide.enabled()).toBe(false);
      expect(switchButton().getAttribute('aria-pressed')).toBe('false');
      expect(storedGuide(storage)).toMatchObject({ enabled: false });
    });

    it('starts the area of the page it is switched on from', async () => {
      const { guide, switchButton } = await create('/sagas');

      switchButton().click();

      expect(guide.request()?.area.id).toBe('list');
    });

    it('keeps the focus it was given: nothing moves it', async () => {
      const { fixture, switchButton } = await create();
      switchButton().focus();

      switchButton().click();
      fixture.detectChanges();

      expect(document.activeElement).toBe(switchButton());
    });
  });

  describe('Replay tour', () => {
    it('is not there while Guide is off', async () => {
      const { buttons } = await create();

      expect(buttons()).not.toContain('Replay tour');
    });

    it('is not there on a page that has nothing to replay', async () => {
      const { buttons } = await create('/account', on);

      expect(buttons()).not.toContain('Replay tour');
    });

    it('is there with Guide on, on a page with an area, once the tour was seen', async () => {
      const { buttons } = await create('/sagas', on);

      expect(buttons()).toContain('Replay tour');
    });

    it('is not there while the tour is wanted or running', async () => {
      const { fixture, guide, buttons } = await create('/sagas', { ...on, seen: {} });
      expect(guide.request()?.area.id).toBe('list');
      expect(buttons()).not.toContain('Replay tour');

      guide.started('list');
      fixture.detectChanges();
      expect(buttons()).not.toContain('Replay tour');

      guide.ended('list', true);
      fixture.detectChanges();
      expect(buttons()).toContain('Replay tour');
    });

    it('asks for the current area again', async () => {
      const { fixture, guide, root } = await create('/sagas', on);
      const replay = Array.from(root.querySelectorAll('button')).find(
        (b) => b.textContent?.trim() === 'Replay tour',
      ) as HTMLButtonElement;

      replay.click();
      fixture.detectChanges();

      expect(guide.request()?.area.id).toBe('list');
      expect(root.textContent).not.toContain('Replay tour');
    });
  });

  describe('the User guide link', () => {
    it('opens the user guide in a new tab, without handing it the page', async () => {
      const { root } = await create();
      const link = root.querySelector('a') as HTMLAnchorElement;

      expect(link.textContent?.trim()).toBe('User guide');
      expect(link.getAttribute('href')).toBe(USER_GUIDE_URL);
      expect(link.getAttribute('target')).toBe('_blank');
      expect(link.getAttribute('rel')).toBe('noopener noreferrer');
    });

    it('is there with Guide on and with it off', async () => {
      const { root, guide, fixture } = await create('/account');
      expect(root.querySelectorAll('a')).toHaveLength(1);

      guide.setEnabled(true);
      fixture.detectChanges();
      expect(root.querySelectorAll('a')).toHaveLength(1);
    });
  });

  describe('the hint', () => {
    it('is in a status region that is in the page before it has anything to say', async () => {
      const { root, hint } = await create('/account');

      expect(root.querySelector('[role="status"]')).not.toBeNull();
      expect(hint()).toBeNull();
    });

    it('offers a walkthrough on a page that has a tour, with two ways to answer', async () => {
      const { root, hint, buttons } = await create('/sagas');

      expect(hint()?.textContent).toContain(
        'New here? Turn on Guide for a walkthrough of each page.',
      );
      expect(root.querySelector('[role="status"]')?.contains(hint())).toBe(true);
      expect(buttons()).toEqual(expect.arrayContaining(['Start the tour', 'No thanks']));
    });

    it('does not move the focus or cover the page: it is not modal', async () => {
      const { hint } = await create('/sagas');

      expect(document.activeElement).toBe(document.body);
      expect(hint()?.getAttribute('role')).toBeNull();
      expect(hint()?.getAttribute('aria-modal')).toBeNull();
    });

    it('starts the tour from "Start the tour" and does not show again', async () => {
      const { fixture, guide, root, hint } = await create('/sagas');

      (
        Array.from(root.querySelectorAll('button')).find(
          (b) => b.textContent?.trim() === 'Start the tour',
        ) as HTMLElement
      ).click();
      fixture.detectChanges();

      expect(guide.enabled()).toBe(true);
      expect(guide.request()?.area.id).toBe('list');
      expect(hint()).toBeNull();
      expect(storedGuide(storage)).toMatchObject({ enabled: true, hintDismissed: true });
    });

    it('goes away for good on "No thanks" and leaves Guide off', async () => {
      const { fixture, guide, root, hint } = await create('/sagas');

      (
        Array.from(root.querySelectorAll('button')).find(
          (b) => b.textContent?.trim() === 'No thanks',
        ) as HTMLElement
      ).click();
      fixture.detectChanges();

      expect(guide.enabled()).toBe(false);
      expect(hint()).toBeNull();
      expect(storedGuide(storage)).toMatchObject({ enabled: false, hintDismissed: true });
    });

    it('does not show once it was answered, or on a page with no tour, or while Guide is on', async () => {
      expect(
        (await create('/sagas', { v: 1, enabled: false, seen: {}, hintDismissed: true })).hint(),
      ).toBeNull();
      TestBed.resetTestingModule();
      expect((await create('/account')).hint()).toBeNull();
      TestBed.resetTestingModule();
      expect((await create('/sagas', on)).hint()).toBeNull();
    });

    it('appears when the user reaches a page that has a tour', async () => {
      const { fixture, hint } = await create('/account');
      expect(hint()).toBeNull();

      await TestBed.inject(Router).navigateByUrl('/sagas');
      fixture.detectChanges();

      expect(hint()).not.toBeNull();
    });
  });
});
