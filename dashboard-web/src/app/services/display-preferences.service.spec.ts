import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { DisplayPreferences } from '../models/auth.model';
import { AuthMock, createAuthMock, provideAuthMock } from '../testing/auth-mock';
import { MemoryStorage } from '../testing/guide';
import { DENSITY_STORAGE_KEY } from './density.service';
import { DisplayPreferencesService } from './display-preferences.service';
import { THEME_STORAGE, THEME_STORAGE_KEY } from './theme.service';

describe('DisplayPreferencesService', () => {
  const root = document.documentElement;
  const ALICE = { id: 'u1', username: 'alice', displayName: 'Alice', mustChangePassword: false };
  const BOB = { id: 'u2', username: 'bob', displayName: 'Bob', mustChangePassword: false };
  let storage: MemoryStorage;
  let auth: AuthMock;
  let service: DisplayPreferencesService;

  /** Creates the service the way the shell does, with the browser's choices already in storage. */
  function create(
    options: Parameters<typeof createAuthMock>[0] = {},
    browser: { theme?: string; density?: string } = {},
    prepare: (mock: AuthMock) => void = () => undefined,
  ): void {
    storage = new MemoryStorage();
    if (browser.theme) storage.setItem(THEME_STORAGE_KEY, browser.theme);
    if (browser.density) storage.setItem(DENSITY_STORAGE_KEY, browser.density);
    auth = createAuthMock({ user: ALICE, ...options });
    prepare(auth);
    TestBed.configureTestingModule({
      providers: [provideAuthMock(auth), { provide: THEME_STORAGE, useValue: storage }],
    });
    service = TestBed.inject(DisplayPreferencesService);
    TestBed.tick();
  }

  /** Lets an awaited write finish and the effect it triggers run. */
  async function settle(): Promise<void> {
    await vi.waitFor(() => TestBed.tick());
    await Promise.resolve();
    TestBed.tick();
  }

  afterEach(() => {
    root.removeAttribute('data-theme');
    root.removeAttribute('data-density');
  });

  describe('an account with stored choices', () => {
    it('applies them, and caches them for the next first paint', () => {
      create({ preferences: { theme: 'dark', density: 'compact' } });

      expect(root.getAttribute('data-theme')).toBe('dark');
      expect(root.getAttribute('data-density')).toBe('compact');
      expect(storage.getItem(THEME_STORAGE_KEY)).toBe('dark');
      expect(storage.getItem(DENSITY_STORAGE_KEY)).toBe('compact');
      expect(auth.savePreferences).not.toHaveBeenCalled();
    });

    it('wins over what the browser had', () => {
      create(
        { preferences: { theme: 'light', density: 'comfortable' } },
        { theme: 'high-contrast', density: 'compact' },
      );

      expect(root.getAttribute('data-theme')).toBe('light');
      expect(root.hasAttribute('data-density')).toBe(false);
      expect(storage.getItem(THEME_STORAGE_KEY)).toBe('light');
      expect(storage.getItem(DENSITY_STORAGE_KEY)).toBeNull();
      expect(auth.savePreferences).not.toHaveBeenCalled();
    });

    it('applies System as a choice: it clears a browser theme', () => {
      create({ preferences: { theme: 'system', density: null } }, { theme: 'dark' });

      expect(root.hasAttribute('data-theme')).toBe(false);
      expect(storage.getItem(THEME_STORAGE_KEY)).toBeNull();
    });

    it('leaves a member the account has no choice for as the browser has it', () => {
      create({ preferences: { theme: 'dark', density: null } }, { density: 'compact' });

      expect(root.getAttribute('data-theme')).toBe('dark');
      expect(root.getAttribute('data-density')).toBe('compact');
      expect(auth.savePreferences).not.toHaveBeenCalled();
    });

    it('ignores a value this version does not offer', () => {
      create({ preferences: { theme: 'sepia', density: 'roomy' } }, { theme: 'light' });

      expect(root.getAttribute('data-theme')).toBe('light');
      expect(root.hasAttribute('data-density')).toBe(false);
    });

    it('follows a change another browser made, at the next session read', () => {
      create({ preferences: { theme: 'dark', density: 'comfortable' } });

      auth.preferences.set({ theme: 'light', density: 'compact' });
      TestBed.tick();

      expect(root.getAttribute('data-theme')).toBe('light');
      expect(root.getAttribute('data-density')).toBe('compact');
    });
  });

  describe('an account with no stored choice', () => {
    it('is given the browser’s choice once, so nobody loses the one they made', async () => {
      create({ preferences: null }, { theme: 'high-contrast', density: 'compact' });
      await settle();

      expect(auth.savePreferences).toHaveBeenCalledExactlyOnceWith({
        theme: 'high-contrast',
        density: 'compact',
      });
      expect(root.getAttribute('data-theme')).toBe('high-contrast');
    });

    it('uploads nothing for System and Comfortable: there is nothing to lose', async () => {
      create({ preferences: null });
      await settle();

      expect(auth.savePreferences).not.toHaveBeenCalled();
    });

    it('does not upload it again once the account holds it', async () => {
      create({ preferences: null }, { theme: 'dark' });
      await settle();
      auth.preferences.set({ theme: 'dark', density: 'comfortable' });
      await settle();

      expect(auth.savePreferences).toHaveBeenCalledTimes(1);
    });
  });

  describe('without an account', () => {
    it('keeps an anonymous visitor’s choices on the browser, and sends nothing', async () => {
      create({ status: 'anonymous', user: null }, { theme: 'dark' });

      service.setTheme('light');
      service.setDensity('compact');
      await settle();

      expect(root.getAttribute('data-theme')).toBe('light');
      expect(storage.getItem(THEME_STORAGE_KEY)).toBe('light');
      expect(storage.getItem(DENSITY_STORAGE_KEY)).toBe('compact');
      expect(auth.savePreferences).not.toHaveBeenCalled();
    });

    it('does the same for the API key, which has a session but no user', async () => {
      create({ user: null }, { theme: 'dark' });

      service.setTheme('light');
      await settle();

      expect(root.getAttribute('data-theme')).toBe('light');
      expect(auth.savePreferences).not.toHaveBeenCalled();
    });
  });

  describe('a change made in the menu', () => {
    it('applies at once and writes both choices through', async () => {
      create({ preferences: { theme: 'dark', density: 'comfortable' } });

      service.setDensity('compact');

      expect(root.getAttribute('data-density')).toBe('compact');
      await settle();
      expect(auth.savePreferences).toHaveBeenCalledExactlyOnceWith({
        theme: 'dark',
        density: 'compact',
      });
    });

    it('is not undone by a session read that still carries the older choice', () => {
      let finish!: () => void;
      create({ preferences: { theme: 'dark', density: 'comfortable' } }, {}, (mock) =>
        mock.savePreferences.mockImplementation(
          () => new Promise<void>((resolve) => (finish = resolve)),
        ),
      );

      service.setTheme('light');
      auth.preferences.set({ theme: 'dark', density: 'comfortable' }); // answered before the write landed
      TestBed.tick();

      expect(root.getAttribute('data-theme')).toBe('light');
      finish();
    });

    it('sends the last choice when several are made while a write is in flight', async () => {
      const finishers: (() => void)[] = [];
      create({ preferences: { theme: 'dark', density: 'comfortable' } }, {}, (mock) =>
        mock.savePreferences.mockImplementation(
          () => new Promise<void>((resolve) => finishers.push(resolve)),
        ),
      );

      service.setTheme('light');
      service.setTheme('high-contrast');
      service.setDensity('compact');
      expect(auth.savePreferences).toHaveBeenCalledTimes(1);
      finishers[0]();
      await vi.waitFor(() => expect(auth.savePreferences).toHaveBeenCalledTimes(2));
      finishers[1]();
      await settle();

      expect(auth.savePreferences).toHaveBeenLastCalledWith({
        theme: 'high-contrast',
        density: 'compact',
      });
      expect(auth.savePreferences).toHaveBeenCalledTimes(2);
    });

    it('stays on this browser when the write fails, and is sent again at the next session read', async () => {
      create({ preferences: { theme: 'dark', density: 'comfortable' } }, {}, (mock) =>
        mock.savePreferences.mockRejectedValueOnce(new Error('offline')),
      );

      service.setTheme('light');
      await settle();
      expect(auth.savePreferences).toHaveBeenCalledTimes(1);
      expect(root.getAttribute('data-theme')).toBe('light');

      // The session is read again and still says dark: the account's older value must not replace the choice.
      auth.preferences.set({ theme: 'dark', density: 'comfortable' });
      await settle();

      expect(root.getAttribute('data-theme')).toBe('light');
      expect(auth.savePreferences).toHaveBeenCalledTimes(2);
      expect(auth.savePreferences).toHaveBeenLastCalledWith({ theme: 'light', density: 'comfortable' });
    });
  });

  describe('another account on the same browser', () => {
    it('gets its own choices, and an unsaved choice of the previous one is not carried over', async () => {
      create({ preferences: { theme: 'dark', density: 'comfortable' } }, {}, (mock) =>
        mock.savePreferences.mockRejectedValueOnce(new Error('offline')),
      );
      service.setTheme('light');
      await settle();

      auth.user.set(BOB);
      auth.preferences.set({ theme: 'high-contrast', density: 'compact' } as DisplayPreferences);
      await settle();

      expect(root.getAttribute('data-theme')).toBe('high-contrast');
      expect(root.getAttribute('data-density')).toBe('compact');
      expect(auth.savePreferences).toHaveBeenCalledTimes(1);
    });
  });
});
