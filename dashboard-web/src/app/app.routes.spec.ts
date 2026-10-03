import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, Routes, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { Account } from './pages/account/account';
import { Login } from './pages/login/login';
import { Setup } from './pages/setup/setup';
import { AuthMock, AuthMockOptions, createAuthMock, provideAuthMock } from './testing/auth-mock';
import { Mock, vi } from 'vitest';

/** Stands in for the saga pages, which need the API and the hub and are not what these specs are about. */
@Component({
  selector: 'app-saga-stub',
  template: 'sagas',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class SagaStub {}

const ANONYMOUS: AuthMockOptions = { status: 'anonymous', user: null, access: null };
const MUST_CHANGE: AuthMockOptions = {
  user: { id: 'u1', username: 'alice', displayName: 'Alice', mustChangePassword: true },
  access: { permissions: [], scoped: [] },
};

type Page = 'Login' | 'Setup' | 'Account' | 'sagas';
const PAGES = { Login, Setup, Account, sagas: SagaStub } as const;

/** Every session the pages can meet, by name. */
const SESSIONS: Record<string, AuthMockOptions> = {
  anonymous: ANONYMOUS,
  'no user yet, setup open': { ...ANONYMOUS, setupRequired: true, setupAvailable: true },
  'no user yet, setup closed by a seed': {
    ...ANONYMOUS,
    setupRequired: true,
    setupAvailable: false,
    setupProblem: { code: 'setup_unavailable', detail: 'A seed is set.' },
  },
  'API unreachable': { ...ANONYMOUS, status: 'unreachable' },
  'signed in': {},
  'signed in, must change the password': MUST_CHANGE,
  'the API key, with no user yet': { user: null, setupRequired: true, setupAvailable: true },
};

describe('routes', () => {
  let auth: AuthMock;
  let harness: RouterTestingHarness;
  /** How often each lazy page was asked for: a visitor who is turned away must never download it. */
  let loads: Record<string, Mock<() => void>>;

  /** The real route table, with the saga pages swapped for a stub and the lazy pages counted. */
  function table(): Routes {
    loads = {
      login: vi.fn<() => void>(),
      setup: vi.fn<() => void>(),
      account: vi.fn<() => void>(),
    };
    return routes.map((route) => {
      if (route.component) return { ...route, component: SagaStub };
      const load = route.loadComponent;
      const count = route.path === undefined ? undefined : loads[route.path];
      if (!load || !count) return route;
      return {
        ...route,
        loadComponent: () => {
          count();
          return load();
        },
      };
    });
  }

  async function visit(url: string, options: AuthMockOptions = {}): Promise<void> {
    auth = createAuthMock(options);
    TestBed.configureTestingModule({
      providers: [provideRouter(table()), provideAuthMock(auth)],
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
  }

  const url = () => TestBed.inject(Router).url;
  const shown = () => harness.routeNativeElement as HTMLElement;

  it('loads the login, setup and account pages lazily', () => {
    for (const path of ['login', 'setup', 'account']) {
      const route = routes.find((r) => r.path === path);

      expect(route?.component, path).toBeUndefined();
      expect(route?.loadComponent, path).toBeInstanceOf(Function);
    }
  });

  describe('/login', () => {
    it('shows the sign-in page to an anonymous visitor', async () => {
      await visit('/login', ANONYMOUS);

      expect(url()).toBe('/login');
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(Login);
    });

    it('sends everyone to setup while no user exists', async () => {
      await visit('/login', { ...ANONYMOUS, setupRequired: true, setupAvailable: true });

      expect(url()).toBe('/setup');
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(Setup);
    });

    it('sends a signed-in visitor on to the page the sign-in was for', async () => {
      await visit('/login?returnUrl=%2Fsagas%3Fstatus%3DFailed');

      expect(url()).toBe('/sagas?status=Failed');
    });

    it('sends a signed-in visitor with an unsafe return URL to the saga list', async () => {
      await visit('/login?returnUrl=%2F%2Fevil.example');

      expect(url()).toBe('/sagas');
    });
  });

  describe('/setup', () => {
    it('shows the setup page while no user exists', async () => {
      await visit('/setup', { ...ANONYMOUS, setupRequired: true, setupAvailable: true });

      expect(url()).toBe('/setup');
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(Setup);
    });

    it('sends everyone else to the login page', async () => {
      await visit('/setup', ANONYMOUS);

      expect(url()).toBe('/login');
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(Login);
    });
  });

  describe('/account', () => {
    it('shows the account page to a signed-in user', async () => {
      await visit('/account');

      expect(url()).toBe('/account');
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(Account);
    });

    it('sends an anonymous visitor to the login page, which returns to it', async () => {
      await visit('/account', ANONYMOUS);

      expect(url()).toBe('/login?returnUrl=%2Faccount');
      expect(shown().textContent).toContain('Sign in');
    });

    it('sends everyone to setup while no user exists', async () => {
      await visit('/account', { ...ANONYMOUS, setupRequired: true });

      expect(url()).toBe('/setup');
    });

    it('shows a user who must change the password the page where they do', async () => {
      await visit('/account', {
        user: { id: 'u1', username: 'alice', displayName: 'Alice', mustChangePassword: true },
      });

      expect(url()).toBe('/account');
      expect(shown().textContent).toContain('choose a new password');
    });
  });
  describe('where each session ends up, for each of the three pages', () => {
    // The saga routes are open in this commit, so a user who must change the password is not sent from
    // them to /account yet: the commit that guards them changes those rows.
    const matrix: Array<[string, string, string, Page]> = [
      ['anonymous', '/login', '/login', 'Login'],
      ['anonymous', '/setup', '/login', 'Login'],
      ['anonymous', '/account', '/login?returnUrl=%2Faccount', 'Login'],
      ['no user yet, setup open', '/login', '/setup', 'Setup'],
      ['no user yet, setup open', '/setup', '/setup', 'Setup'],
      ['no user yet, setup open', '/account', '/setup', 'Setup'],
      ['no user yet, setup closed by a seed', '/login', '/setup', 'Setup'],
      ['no user yet, setup closed by a seed', '/setup', '/setup', 'Setup'],
      ['no user yet, setup closed by a seed', '/account', '/setup', 'Setup'],
      ['API unreachable', '/login', '/login', 'Login'],
      ['API unreachable', '/setup', '/login', 'Login'],
      ['API unreachable', '/account', '/login?returnUrl=%2Faccount', 'Login'],
      ['signed in', '/login', '/sagas', 'sagas'],
      ['signed in', '/setup', '/sagas', 'sagas'],
      ['signed in', '/account', '/account', 'Account'],
      ['signed in, must change the password', '/login', '/sagas', 'sagas'],
      ['signed in, must change the password', '/login?returnUrl=%2Faccount', '/account', 'Account'],
      ['signed in, must change the password', '/login?returnUrl=%2Fsagas', '/sagas', 'sagas'],
      ['signed in, must change the password', '/setup', '/sagas', 'sagas'],
      ['signed in, must change the password', '/account', '/account', 'Account'],
      ['the API key, with no user yet', '/login', '/setup', 'Setup'],
      ['the API key, with no user yet', '/setup', '/setup', 'Setup'],
      ['the API key, with no user yet', '/account', '/setup', 'Setup'],
    ];

    it.each(matrix)('%s: %s settles on %s', async (session, from, settled, page) => {
      await visit(from, SESSIONS[session]);

      expect(url()).toBe(settled);
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(PAGES[page]);
    });
  });

  describe('a visitor who is turned away', () => {
    it('never downloads the setup page: the guard runs before the chunk is requested', async () => {
      await visit('/setup', ANONYMOUS);

      expect(url()).toBe('/login');
      expect(loads['setup']).not.toHaveBeenCalled();
      expect(loads['login']).toHaveBeenCalledTimes(1);
    });

    it('never downloads the login page when signed in, nor the account page when anonymous', async () => {
      await visit('/login');
      expect(loads['login']).not.toHaveBeenCalled();
      expect(loads['account']).not.toHaveBeenCalled();
      TestBed.resetTestingModule();

      await visit('/account', ANONYMOUS);
      expect(loads['account']).not.toHaveBeenCalled();
    });

    it('downloads the page that is shown, once', async () => {
      await visit('/account');

      expect(loads['account']).toHaveBeenCalledTimes(1);
      expect(loads['login']).not.toHaveBeenCalled();
    });
  });

  describe('a URL that matches nothing', () => {
    it('goes to the saga list', async () => {
      await visit('/nonsense/at/all');

      expect(url()).toBe('/sagas');
    });

    it('is where a stale return URL ends up, so a signed-in visitor leaves the login page', async () => {
      await visit('/login?returnUrl=%2Fa-page-that-is-gone');

      expect(url()).toBe('/sagas');
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(SagaStub);
    });
  });

  describe('a return URL that is not safe', () => {
    it.each([
      ['//evil.example', '%2F%2Fevil.example'],
      ['a backslash', '%2F%5Cevil.example'],
      ['a full URL', 'https%3A%2F%2Fevil.example'],
      ['one that was encoded twice', '%252F%252Fevil.example'],
      ['the login page itself', '%2Flogin'],
    ])('is ignored for a signed-in visitor: %s', async (_name, encoded) => {
      await visit(`/login?returnUrl=${encoded}`);

      expect(url()).toBe('/sagas');
    });
  });
});
