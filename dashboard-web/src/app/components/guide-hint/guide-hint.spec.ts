import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { GuideService } from '../../services/guide.service';
import {
  MemoryStorage,
  createGuideStorage,
  provideGuideStorage,
  storedGuide,
} from '../../testing/guide';
import { GuideToggle } from '../guide-toggle/guide-toggle';
import { GuideHint } from './guide-hint';

@Component({
  selector: 'app-page-stub',
  template: '',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class PageStub {}

/** The bar as the app lays it out: the toggle in the top bar, the hint in its own row after it. */
@Component({
  selector: 'app-hint-host',
  imports: [GuideToggle, GuideHint],
  template: '<app-guide-toggle /><app-guide-hint />',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class HintHost {}

describe('GuideHint', () => {
  let storage: MemoryStorage;

  async function create(url = '/sagas', stored?: unknown) {
    storage = createGuideStorage(stored);
    TestBed.configureTestingModule({
      imports: [HintHost],
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
    const fixture = TestBed.createComponent(HintHost);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      guide,
      root,
      switchButton: () => root.querySelector('button.guide-switch') as HTMLButtonElement,
      buttons: () => Array.from(root.querySelectorAll('app-guide-hint button'), (b) => b.textContent?.trim()),
      hint: () => root.querySelector('.guide-hint') as HTMLElement | null,
    };
  }

  const on = { v: 1, enabled: true, seen: { list: 1 }, hintDismissed: true };

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

  it('sits in a bar of its own, not inside the Guide switch, so it cannot cover the page', async () => {
    const { root, hint } = await create('/sagas');

    expect(root.querySelector('app-guide-toggle')?.contains(hint())).toBe(false);
    expect(root.querySelector('app-guide-hint')?.contains(hint())).toBe(true);
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

  it('keeps the keyboard focus on the Guide switch when the button that was pressed goes with the hint', async () => {
    for (const [label, enabled] of [
      ['No thanks', false],
      ['Start the tour', true],
    ] as const) {
      TestBed.resetTestingModule();
      const { fixture, guide, root, switchButton } = await create('/sagas');
      const answer = Array.from(root.querySelectorAll('button')).find(
        (b) => b.textContent?.trim() === label,
      ) as HTMLButtonElement;
      answer.focus();
      expect(document.activeElement).toBe(answer); // the focus starts somewhere else than the switch

      answer.click();
      fixture.detectChanges();

      expect(answer.isConnected).toBe(false);
      expect(guide.enabled()).toBe(enabled);
      expect(document.activeElement, label).toBe(switchButton());
    }
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
