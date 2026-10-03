import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthMock, createAuthMock, provideAuthMock } from '../../testing/auth-mock';
import { UserMenu } from './user-menu';

/** Where the account link goes; the page itself is not what this spec is about. */
@Component({
  selector: 'app-account-stub',
  template: '',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class AccountStub {}

describe('UserMenu', () => {
  let fixture: ComponentFixture<UserMenu>;
  let auth: AuthMock;

  function create(options: Parameters<typeof createAuthMock>[0] = {}): void {
    auth = createAuthMock({
      user: { id: 'u1', username: 'alice', displayName: 'Alice Doe', mustChangePassword: false },
      ...options,
    });
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'account', component: AccountStub }]),
        provideAuthMock(auth),
      ],
    });
    fixture = TestBed.createComponent(UserMenu);
    fixture.detectChanges();
  }

  const root = () => fixture.nativeElement as HTMLElement;
  const trigger = () => root().querySelector<HTMLButtonElement>('button.user-menu-trigger')!;
  const panel = () => root().querySelector<HTMLElement>('.user-menu-panel');
  const items = () => Array.from(root().querySelectorAll<HTMLElement>('[role="menuitem"]'));
  const account = () => items()[0] as HTMLAnchorElement;
  const signOutButton = () => items()[1] as HTMLButtonElement;
  const focused = () => document.activeElement;

  /** Opens the menu the way a click does, and lets the render hook that moves focus run. */
  async function open(): Promise<void> {
    trigger().click();
    fixture.detectChanges();
    await fixture.whenStable();
  }

  const key = (target: Element, name: string) =>
    target.dispatchEvent(
      new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }),
    );

  afterEach(() => {
    // A menu left open would answer the next spec's document clicks.
    fixture.destroy();
  });

  describe('the button', () => {
    it('shows the display name and announces a menu that is closed', () => {
      create();

      expect(trigger().textContent).toContain('Alice Doe');
      expect(trigger().getAttribute('aria-haspopup')).toBe('menu');
      expect(trigger().getAttribute('aria-expanded')).toBe('false');
      expect(panel()).toBeNull();
    });

    it('falls back to the username, then to "Account", for a name that is blank or missing', () => {
      create({ user: { id: 'u1', username: 'alice', displayName: '', mustChangePassword: false } });
      expect(trigger().textContent).toContain('alice');
      fixture.destroy();
      TestBed.resetTestingModule();

      create({ user: null });
      expect(trigger().textContent).toContain('Account');
    });

    it('follows the user when the session changes', () => {
      create();

      auth.user.set({
        id: 'u1',
        username: 'alice',
        displayName: 'Alice Renamed',
        mustChangePassword: false,
      });
      fixture.detectChanges();

      expect(trigger().textContent).toContain('Alice Renamed');
    });
  });

  describe('opening', () => {
    it('shows the name, the username, the account link and sign out, and announces it', async () => {
      create();

      await open();

      expect(trigger().getAttribute('aria-expanded')).toBe('true');
      expect(panel()?.classList).toContain('menu');
      expect(panel()?.textContent).toContain('Alice Doe');
      expect(panel()?.textContent).toContain('alice');
      expect(root().querySelector('[role="menu"]')?.getAttribute('aria-labelledby')).toBe(
        trigger().id,
      );
      expect(trigger().getAttribute('aria-controls')).toBe(panel()?.id);
      expect(items().map((item) => item.textContent?.trim())).toEqual(['Account', 'Sign out']);
      expect(account().getAttribute('href')).toBe('/account');
    });

    it('moves focus to the first item', async () => {
      create();

      await open();

      expect(focused()).toBe(account());
    });

    it('closes when the button is pressed again', async () => {
      create();
      await open();

      trigger().click();
      fixture.detectChanges();

      expect(panel()).toBeNull();
      expect(trigger().getAttribute('aria-expanded')).toBe('false');
      expect(focused()).toBe(trigger());
    });

    it('opens with the arrow down on the button', async () => {
      create();
      trigger().focus();

      key(trigger(), 'ArrowDown');
      fixture.detectChanges();
      await fixture.whenStable();

      expect(panel()).not.toBeNull();
      expect(focused()).toBe(account());
    });

    it('goes into an open menu with the arrow down on the button', async () => {
      create();
      await open();
      trigger().focus();

      key(trigger(), 'ArrowDown');

      expect(panel()).not.toBeNull();
      expect(focused()).toBe(account());
    });
  });

  describe('closing', () => {
    it('closes on a click outside that takes no focus: the menu has no focus handler to close it', async () => {
      create();
      await open();
      const page = document.createElement('div');
      document.body.appendChild(page);

      page.click();
      fixture.detectChanges();

      expect(panel()).toBeNull();
      expect(focused()).toBe(document.body);
      page.remove();
    });

    it('closes on a click on another control, which keeps the focus it took', async () => {
      create();
      await open();
      const elsewhere = document.createElement('button');
      document.body.appendChild(elsewhere);
      elsewhere.focus();

      elsewhere.click();
      fixture.detectChanges();

      expect(panel()).toBeNull();
      expect(focused()).toBe(elsewhere);
      elsewhere.remove();
    });

    it('stays open for a click inside the panel that selects nothing', async () => {
      create();
      await open();

      root().querySelector<HTMLElement>('.user-menu-who')!.click();
      fixture.detectChanges();

      expect(panel()).not.toBeNull();
    });

    it('closes on Escape and returns focus to the button', async () => {
      create();
      await open();
      expect(focused()).toBe(account());

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      fixture.detectChanges();

      expect(panel()).toBeNull();
      expect(focused()).toBe(trigger());
    });

    it('ignores Escape while it is closed: it belongs to whatever else is open', () => {
      create();
      const elsewhere = document.createElement('input');
      document.body.appendChild(elsewhere);
      elsewhere.focus();

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

      expect(focused()).toBe(elsewhere);
      elsewhere.remove();
    });

    it('closes when an item is selected, and returns focus to the button', async () => {
      create();
      await open();

      account().click();
      fixture.detectChanges();

      expect(panel()).toBeNull();
      expect(focused()).toBe(trigger());
    });

    it('closes when focus leaves it, by Tab', async () => {
      create();
      await open();
      const next = document.createElement('button');
      document.body.appendChild(next);

      signOutButton().dispatchEvent(
        new FocusEvent('focusout', { bubbles: true, relatedTarget: next }),
      );
      fixture.detectChanges();

      expect(panel()).toBeNull();
      next.remove();
    });

    it('stays open while focus moves inside it', async () => {
      create();
      await open();

      account().dispatchEvent(
        new FocusEvent('focusout', { bubbles: true, relatedTarget: signOutButton() }),
      );
      fixture.detectChanges();

      expect(panel()).not.toBeNull();
    });

    it('stays open when focus goes nowhere (the window lost it)', async () => {
      create();
      await open();

      account().dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: null }));
      fixture.detectChanges();

      expect(panel()).not.toBeNull();
    });
  });

  describe('the keyboard', () => {
    it('moves between the items with the arrow keys, wrapping at both ends', async () => {
      create();
      await open();

      key(focused()!, 'ArrowDown');
      expect(focused()).toBe(signOutButton());

      key(focused()!, 'ArrowDown');
      expect(focused()).toBe(account());

      key(focused()!, 'ArrowUp');
      expect(focused()).toBe(signOutButton());

      key(focused()!, 'ArrowUp');
      expect(focused()).toBe(account());
    });
  });

  describe('signing out', () => {
    it('signs out through the auth service and closes the menu', async () => {
      create();
      await open();

      signOutButton().click();
      fixture.detectChanges();
      await fixture.whenStable();

      expect(auth.logout).toHaveBeenCalledTimes(1);
      expect(panel()).toBeNull();
    });

    it('says so while the sign-out is under way', async () => {
      let finish!: () => void;
      create();
      auth.logout.mockReturnValue(new Promise<void>((resolve) => (finish = resolve)));
      await open();

      signOutButton().click();
      fixture.detectChanges();

      expect(trigger().textContent).toContain('Signing out…');
      expect(trigger().disabled).toBe(true);

      finish();
      await fixture.whenStable();
      fixture.detectChanges();
      expect(trigger().textContent).toContain('Alice Doe');
      expect(trigger().disabled).toBe(false);
    });

    it('starts the sign-out once for two activations before the page has repainted', async () => {
      create();
      auth.logout.mockReturnValue(new Promise<void>(() => undefined));
      await open();

      signOutButton().click();
      signOutButton().click();

      expect(auth.logout).toHaveBeenCalledTimes(1);
    });
  });

  describe('the account link', () => {
    it('does not sign anyone out', async () => {
      create();
      await open();

      account().click();

      expect(auth.logout).not.toHaveBeenCalled();
    });
  });
});
