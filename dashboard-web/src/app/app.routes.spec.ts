import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, Routes, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { authGuard } from './guards/auth.guards';
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
      detail: vi.fn<() => void>(),
    };
    return routes.map((route) => {
      if (route.path?.startsWith('sagas')) {
        // The stub stands in for both saga pages, with the route's own guards. The detail page is lazy and
        // the list is not: its loader is kept and counted (it hands back the stub instead of the real page,
        // which needs the API and the hub).
        const { loadComponent: lazy, ...rest } = route;
        if (!lazy) return { ...rest, component: SagaStub };
        return {
          ...rest,
          loadComponent: () => {
            loads['detail']();
            return SagaStub;
          },
        };
      }
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

  // The detail page (timeline, map, state inspector) is the heaviest in the app: with it eager the initial
  // bundle was 506 kB against the 500 kB budget in angular.json, and lazy it is 431 kB. It stays in a
  // chunk of its own. The saga list is the page everyone lands on, so it ships with the app.
  it('loads the saga detail page lazily and the saga list with the app', () => {
    const detail = routes.find((r) => r.path === 'sagas/:sagaType/:id');
    const list = routes.find((r) => r.path === 'sagas');

    expect(detail?.component).toBeUndefined();
    expect(detail?.loadComponent).toBeInstanceOf(Function);
    expect(list?.component).toBeDefined();
  });

  it('puts every saga page behind a signed-in user', () => {
    for (const path of ['sagas', 'sagas/:sagaType/:id']) {
      expect(routes.find((r) => r.path === path)?.canActivate, path).toEqual([authGuard]);
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
  describe('where each session ends up, for each page', () => {
    // The saga pages are as guarded as the account page: a user who must change the password is sent from
    // them to /account, and /login and /setup hand a signed-in visitor on to /sagas first.
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
      ['signed in, must change the password', '/login', '/account', 'Account'],
      ['signed in, must change the password', '/login?returnUrl=%2Faccount', '/account', 'Account'],
      ['signed in, must change the password', '/login?returnUrl=%2Fsagas', '/account', 'Account'],
      ['signed in, must change the password', '/setup', '/account', 'Account'],
      ['signed in, must change the password', '/account', '/account', 'Account'],
      ['the API key, with no user yet', '/login', '/setup', 'Setup'],
      ['the API key, with no user yet', '/setup', '/setup', 'Setup'],
      ['the API key, with no user yet', '/account', '/setup', 'Setup'],
      // The saga pages, the list and a detail URL.
      ['anonymous', '/sagas', '/login?returnUrl=%2Fsagas', 'Login'],
      ['anonymous', '/sagas/OrderSaga/abc', '/login?returnUrl=%2Fsagas%2FOrderSaga%2Fabc', 'Login'],
      [
        'anonymous',
        '/sagas?status=Failed&page=2',
        '/login?returnUrl=%2Fsagas%3Fstatus%3DFailed%26page%3D2',
        'Login',
      ],
      // A saga type is free-form text and travels percent-encoded: it must come back as it left.
      [
        'anonymous',
        '/sagas/Order%2FSaga/abc',
        '/login?returnUrl=%2Fsagas%2FOrder%252FSaga%2Fabc',
        'Login',
      ],
      [
        'signed in',
        '/login?returnUrl=%2Fsagas%2FOrder%252FSaga%2Fabc',
        '/sagas/Order%2FSaga/abc',
        'sagas',
      ],
      ['signed in', '/login?returnUrl=%2Fsagas%2FOrderSaga%2Fabc', '/sagas/OrderSaga/abc', 'sagas'],
      ['no user yet, setup open', '/sagas', '/setup', 'Setup'],
      ['no user yet, setup open', '/sagas/OrderSaga/abc', '/setup', 'Setup'],
      ['no user yet, setup closed by a seed', '/sagas', '/setup', 'Setup'],
      ['API unreachable', '/sagas', '/login?returnUrl=%2Fsagas', 'Login'],
      [
        'API unreachable',
        '/sagas/OrderSaga/abc',
        '/login?returnUrl=%2Fsagas%2FOrderSaga%2Fabc',
        'Login',
      ],
      ['signed in', '/sagas', '/sagas', 'sagas'],
      ['signed in', '/sagas/OrderSaga/abc', '/sagas/OrderSaga/abc', 'sagas'],
      ['signed in, must change the password', '/sagas', '/account', 'Account'],
      ['signed in, must change the password', '/sagas/OrderSaga/abc', '/account', 'Account'],
      ['the API key, with no user yet', '/sagas', '/setup', 'Setup'],
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

    // The detail page is the biggest chunk, and the one whose address a visitor may know: the guard must
    // run before the chunk is requested, whoever is turned away.
    it.each([
      'anonymous',
      'no user yet, setup open',
      'API unreachable',
      'signed in, must change the password',
    ])('never downloads the saga detail page for a visitor who is %s', async (session) => {
      await visit('/sagas/OrderSaga/abc', SESSIONS[session]);

      expect(url()).not.toBe('/sagas/OrderSaga/abc');
      expect(loads['detail']).not.toHaveBeenCalled();
    });

    it('downloads the saga detail page once for a signed-in visitor', async () => {
      await visit('/sagas/OrderSaga/abc');

      expect(url()).toBe('/sagas/OrderSaga/abc');
      expect(loads['detail']).toHaveBeenCalledTimes(1);
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
