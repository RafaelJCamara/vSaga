import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import {
  ActivatedRouteSnapshot,
  GuardResult,
  Navigation,
  PartialMatchRouteSnapshot,
  Route,
  Router,
  RouterStateSnapshot,
  UrlSegment,
  UrlTree,
  convertToParamMap,
  provideRouter,
} from '@angular/router';
import { SessionStatus } from '../models/auth.model';
import { AuthMock, AuthMockOptions, createAuthMock, provideAuthMock } from '../testing/auth-mock';
import { adminGuard, anonymousGuard, authGuard, safeReturnUrl, setupGuard } from './auth.guards';

const ANONYMOUS: AuthMockOptions = { status: 'anonymous', user: null, access: null };
const MUST_CHANGE = {
  id: 'u1',
  username: 'alice',
  displayName: 'Alice',
  mustChangePassword: true,
};

describe('auth guards', () => {
  let auth: AuthMock;
  let router: Router;

  function configure(options: AuthMockOptions = {}): void {
    auth = createAuthMock(options);
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideAuthMock(auth)] });
    router = TestBed.inject(Router);
  }

  /** What a refresh finds: the session in `status` from then on. */
  function refreshFinds(status: SessionStatus, extra: () => void = () => undefined): void {
    auth.refresh.mockImplementation(async () => {
      auth.status.set(status);
      extra();
      return status;
    });
  }

  const state = (url: string) => ({ url }) as RouterStateSnapshot;
  const routeWith = (query: Record<string, string> = {}) =>
    ({ queryParamMap: convertToParamMap(query) }) as ActivatedRouteSnapshot;
  /** CanMatchFn's third argument is required: a snapshot of the route matched so far. */
  const currentSnapshot = {} as PartialMatchRouteSnapshot;
  const segments = (...paths: string[]) => paths.map((path) => new UrlSegment(path, {}));
  const url = (result: GuardResult) => router.serializeUrl(result as UrlTree);

  // Every guard here is an async function: what it returns is a promise, whatever MaybeAsync allows.
  const runAuth = (target: string) =>
    TestBed.runInInjectionContext(() =>
      authGuard(routeWith(), state(target)),
    ) as Promise<GuardResult>;
  const runAnonymous = (query: Record<string, string> = {}) =>
    TestBed.runInInjectionContext(() =>
      anonymousGuard(routeWith(query), state('/login')),
    ) as Promise<GuardResult>;
  const runSetup = () =>
    TestBed.runInInjectionContext(() =>
      setupGuard(routeWith(), state('/setup')),
    ) as Promise<GuardResult>;
  const runAdmin = (...paths: string[]) =>
    TestBed.runInInjectionContext(() =>
      adminGuard({ path: 'admin' } as Route, segments(...paths), currentSnapshot),
    ) as Promise<GuardResult>;

  describe('authGuard', () => {
    it('lets a signed-in user through without asking the server again', async () => {
      configure();

      expect(await runAuth('/sagas')).toBe(true);
      expect(auth.refresh).not.toHaveBeenCalled();
    });

    it('sends an anonymous visitor to the login page, carrying the page they asked for', async () => {
      configure(ANONYMOUS);

      const result = await runAuth('/sagas/OrderSaga/abc?tab=timeline');

      expect(url(result)).toBe('/login?returnUrl=%2Fsagas%2FOrderSaga%2Fabc%3Ftab%3Dtimeline');
    });

    it('sends everyone to setup while no user exists, before anything else', async () => {
      configure({ ...ANONYMOUS, setupRequired: true });

      expect(url(await runAuth('/sagas'))).toBe('/setup');
    });

    it('asks the server first when the session was never read, and judges the answer', async () => {
      configure({ ...ANONYMOUS, status: 'unknown' });
      refreshFinds('authenticated');

      expect(await runAuth('/sagas')).toBe(true);
      expect(auth.refresh).toHaveBeenCalledTimes(1);
    });

    it('sends a visitor to the login page when the first read could not reach the API', async () => {
      configure({ ...ANONYMOUS, status: 'unknown' });
      refreshFinds('unreachable');

      expect(url(await runAuth('/sagas'))).toBe('/login?returnUrl=%2Fsagas');
      expect(auth.refresh).toHaveBeenCalledTimes(1);
    });

    // The first read already waited out the session timeout: waiting for the same hung API a second time
    // would hold the login page (which says "cannot reach the API" and polls) back for as long again.
    it('goes straight to the login page, without asking again, while the API is unreachable', async () => {
      configure({ ...ANONYMOUS, status: 'unreachable' });
      refreshFinds('authenticated');

      expect(url(await runAuth('/sagas/OrderSaga/abc'))).toBe(
        '/login?returnUrl=%2Fsagas%2FOrderSaga%2Fabc',
      );
      expect(auth.refresh).not.toHaveBeenCalled();
    });

    it('sends a user who must change the password to the account page, from anywhere else', async () => {
      configure({ user: MUST_CHANGE });

      expect(url(await runAuth('/sagas'))).toBe('/account');
      expect(url(await runAuth('/sagas/OrderSaga/abc?tab=timeline'))).toBe('/account');
      expect(url(await runAuth('/accounts'))).toBe('/account');
    });

    it('lets a user who must change the password reach the account page', async () => {
      configure({ user: MUST_CHANGE });

      expect(await runAuth('/account')).toBe(true);
      expect(await runAuth('/account?from=login')).toBe(true);
    });

    it('lets the API key through: a session with no user has no password to change', async () => {
      configure({ user: null });

      expect(await runAuth('/sagas')).toBe(true);
    });
  });

  describe('anonymousGuard', () => {
    it('lets an anonymous visitor see the login page', async () => {
      configure(ANONYMOUS);

      expect(await runAnonymous()).toBe(true);
      expect(auth.refresh).not.toHaveBeenCalled();
    });

    it('asks the server when the session was never read, and judges the answer', async () => {
      configure({ ...ANONYMOUS, status: 'unknown' });
      refreshFinds('anonymous');

      expect(await runAnonymous()).toBe(true);
      expect(auth.refresh).toHaveBeenCalledTimes(1);
    });

    it('does not wait on the server again while it is unreachable: the page polls for itself', async () => {
      configure({ ...ANONYMOUS, status: 'unreachable' });

      expect(await runAnonymous()).toBe(true);
      expect(auth.refresh).not.toHaveBeenCalled();
    });

    it('sends everyone to setup while no user exists', async () => {
      configure({ ...ANONYMOUS, setupRequired: true });

      expect(url(await runAnonymous())).toBe('/setup');
    });

    it('sends a signed-in visitor on to the page they were headed for', async () => {
      configure();

      expect(url(await runAnonymous({ returnUrl: '/sagas/OrderSaga/abc?tab=timeline' }))).toBe(
        '/sagas/OrderSaga/abc?tab=timeline',
      );
    });

    it.each(['//evil.example/x', '/\\evil.example', 'https://evil.example', '/login'])(
      'sends a signed-in visitor to the saga list when the return URL is %s',
      async (returnUrl) => {
        configure();

        expect(url(await runAnonymous({ returnUrl }))).toBe('/sagas');
      },
    );

    it('sends a signed-in visitor to the saga list when there is no return URL', async () => {
      configure();

      expect(url(await runAnonymous())).toBe('/sagas');
    });
  });

  describe('setupGuard', () => {
    it('opens the setup page while no user exists', async () => {
      configure({ ...ANONYMOUS, setupRequired: true });

      expect(await runSetup()).toBe(true);
    });

    it('opens it even when setup cannot be completed: the page says why', async () => {
      configure({ ...ANONYMOUS, setupRequired: true, setupAvailable: false });

      expect(await runSetup()).toBe(true);
    });

    it('sends an anonymous visitor to the login page once a user exists', async () => {
      configure(ANONYMOUS);

      expect(url(await runSetup())).toBe('/login');
    });

    it('sends a signed-in user to the login page, which sends them on', async () => {
      configure();

      expect(url(await runSetup())).toBe('/login');
    });

    it('asks the server when the session was never read', async () => {
      configure({ ...ANONYMOUS, status: 'unknown' });
      refreshFinds('anonymous', () => auth.setupRequired.set(true));

      expect(await runSetup()).toBe(true);
      expect(auth.refresh).toHaveBeenCalledTimes(1);
    });

    it('sends a visitor to the login page, which says so, while the API is unreachable', async () => {
      configure({ ...ANONYMOUS, status: 'unreachable' });

      expect(url(await runSetup())).toBe('/login');
      expect(auth.refresh).not.toHaveBeenCalled();
    });
  });

  describe('adminGuard', () => {
    it('lets a user with access.manage match the administration area', async () => {
      configure();

      expect(await runAdmin('admin', 'users')).toBe(true);
      expect(auth.refresh).not.toHaveBeenCalled();
    });

    it('sends everyone else to the saga list', async () => {
      configure({
        access: { permissions: ['sagas.view', 'sagas.data', 'sagas.retry'], scoped: [] },
      });

      expect(url(await runAdmin('admin', 'users'))).toBe('/sagas');
    });

    it('does not count access.manage held for some saga types only', async () => {
      configure({
        access: {
          permissions: ['sagas.view'],
          scoped: [{ sagaType: 'OrderSaga', permissions: ['access.manage'] }],
        },
      });

      expect(url(await runAdmin('admin'))).toBe('/sagas');
    });

    it('sends an anonymous visitor to the login page, returning to the segments asked for', async () => {
      configure(ANONYMOUS);

      expect(url(await runAdmin('admin', 'users', '42'))).toBe(
        '/login?returnUrl=%2Fadmin%2Fusers%2F42',
      );
    });

    it('returns to the whole address that was asked for, query string included, when a navigation is under way', async () => {
      configure(ANONYMOUS);
      // What the router knows while it matches: the segments carry no query string, the navigation does.
      vi.spyOn(router, 'currentNavigation').mockReturnValue({
        extractedUrl: router.parseUrl('/admin/roles/new?from=abc#top'),
      } as Navigation);

      expect(url(await runAdmin('admin', 'roles', 'new'))).toBe(
        '/login?returnUrl=%2Fadmin%2Froles%2Fnew%3Ffrom%3Dabc%23top',
      );
    });

    it('sends everyone to setup while no user exists', async () => {
      configure({ ...ANONYMOUS, setupRequired: true });

      expect(url(await runAdmin('admin'))).toBe('/setup');
    });

    it('sends a user who must change the password to the account page', async () => {
      configure({ user: MUST_CHANGE });

      expect(url(await runAdmin('admin'))).toBe('/account');
    });

    it('asks the server first when the session was never read', async () => {
      configure({ status: 'unknown' });
      refreshFinds('authenticated');

      expect(await runAdmin('admin')).toBe(true);
      expect(auth.refresh).toHaveBeenCalledTimes(1);
    });

    it('goes to the login page, without asking again, while the API is unreachable', async () => {
      configure({ ...ANONYMOUS, status: 'unreachable' });
      refreshFinds('authenticated');

      expect(url(await runAdmin('admin', 'users'))).toBe('/login?returnUrl=%2Fadmin%2Fusers');
      expect(auth.refresh).not.toHaveBeenCalled();
    });
  });
});

describe('safeReturnUrl', () => {
  it.each([
    '/sagas',
    '/sagas/OrderSaga/abc',
    '/sagas/OrderSaga/abc?tab=timeline&entry=3',
    '/sagas?status=Failed#top',
    '/account',
    '/admin/users/42',
    '/loginfoo',
    '/sagas/login',
    '/',
  ])('keeps %s', (raw) => {
    expect(safeReturnUrl(raw)).toBe(raw);
  });

  it.each([
    ['//evil', 'protocol-relative'],
    ['//evil.example/sagas', 'protocol-relative with a path'],
    ['/\\evil', 'backslash after the slash, which browsers read as //'],
    ['/\\/evil', 'backslash then slash'],
    ['https://x', 'absolute'],
    ['http://localhost:4200/sagas', 'absolute, same host'],
    ['javascript:alert(1)', 'a script URL'],
    ['evil', 'relative'],
    ['sagas', 'relative to the app'],
    ['', 'empty'],
    ['%2F%2Fevil.example', 'still percent-encoded: a return URL that was encoded twice'],
    ['%2Fsagas', 'percent-encoded'],
    ['/\t/evil', 'a tab, which browsers drop from URLs'],
    ['/sagas\nSet-Cookie: x', 'a line break'],
  ])('replaces %s (%s) with the saga list', (raw) => {
    expect(safeReturnUrl(raw)).toBe('/sagas');
  });

  it.each([
    '/login',
    '/login?returnUrl=%2Fsagas',
    '/login#x',
    '/login/',
    '/login;a=b',
    '/setup',
    '/setup?x=1',
    '/setup/',
  ])('replaces %s, the pages a sign-in would loop back to, with the saga list', (raw) => {
    expect(safeReturnUrl(raw)).toBe('/sagas');
  });

  it('replaces a missing or non-string value with the saga list', () => {
    expect(safeReturnUrl(null)).toBe('/sagas');
    expect(safeReturnUrl(undefined)).toBe('/sagas');
    expect(safeReturnUrl(['/sagas'] as unknown as string)).toBe('/sagas');
  });
});
