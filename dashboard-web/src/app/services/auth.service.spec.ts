import { DOCUMENT } from '@angular/common';
import {
  HttpErrorResponse,
  HttpInterceptorFn,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Mock, vi } from 'vitest';
import { PermissionKey, SessionAccess, SessionInfo, SessionUser } from '../models/auth.model';
import { problemOf } from '../util/http-error';
import { AuthService, PAGE_RELOAD } from './auth.service';
import { SagaHubService } from './saga-hub.service';

const SESSION = '/api/auth/session';
const LOGIN = '/api/auth/login';
const LOGOUT = '/api/auth/logout';
const SETUP = '/api/auth/setup';
const PASSWORD = '/api/auth/password';

const ALICE: SessionUser = {
  id: 'user-alice',
  username: 'alice',
  displayName: 'Alice',
  mustChangePassword: false,
};
const BOB: SessionUser = {
  id: 'user-bob',
  username: 'bob',
  displayName: 'Bob',
  mustChangePassword: false,
};
const EVERYTHING: SessionAccess = {
  permissions: ['sagas.view', 'sagas.data', 'sagas.retry', 'access.manage'],
  scoped: [],
};

/** What the API answers an anonymous caller: setup closed, a user exists. */
function anonymous(overrides: Partial<SessionInfo> = {}): SessionInfo {
  return {
    authenticated: false,
    setupRequired: false,
    setupAvailable: false,
    setupProblem: null,
    user: null,
    access: null,
    passwordMinLength: 12,
    ...overrides,
  };
}

function signedIn(
  user: SessionUser | null = ALICE,
  access: SessionAccess = EVERYTHING,
): SessionInfo {
  return anonymous({ authenticated: true, user, access });
}

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;
  /** The same events, in the order they happened, across the stubs and the HTTP pipeline. */
  let order: string[];
  let hub: {
    setSessionProbe: Mock<SagaHubService['setSessionProbe']>;
    resume: Mock<SagaHubService['resume']>;
    stopAndReset: Mock<SagaHubService['stopAndReset']>;
  };
  let router: {
    url: string;
    navigate: Mock<(commands: unknown[], extras?: unknown) => Promise<boolean>>;
  };
  let pageReload: Mock<() => void>;

  /** Records each request as it leaves, with what the service believed at that moment. */
  const recordRequests: HttpInterceptorFn = (req, next) => {
    order.push(`${req.method} ${req.url} [${inject(AuthService).status()}]`);
    return next(req);
  };

  /** Lets every queued microtask and zero-delay timer run: the service awaits between its steps. */
  const flush = async (): Promise<void> => {
    await vi.advanceTimersByTimeAsync(0);
  };

  async function request(method: string, url: string): Promise<TestRequest> {
    await flush();
    return httpMock.expectOne({ method, url });
  }

  async function answerSession(session: SessionInfo): Promise<void> {
    (await request('GET', SESSION)).flush(session);
    await flush();
  }

  async function expectNoRequest(url: string): Promise<void> {
    await flush();
    httpMock.expectNone(url);
  }

  /** Brings the service to a known session, as the app initializer does, then forgets what it did. */
  async function load(session: SessionInfo): Promise<void> {
    const loading = service.bootstrap();
    await answerSession(session);
    await loading;
    order.length = 0;
    hub.resume.mockClear();
    hub.stopAndReset.mockClear();
    router.navigate.mockClear();
    pageReload.mockClear();
  }

  function setVisibility(state: 'visible' | 'hidden'): void {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
    document.dispatchEvent(new Event('visibilitychange'));
  }

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-03T12:00:00Z'));
    order = [];
    hub = {
      setSessionProbe: vi.fn<SagaHubService['setSessionProbe']>(),
      resume: vi.fn<SagaHubService['resume']>(),
      stopAndReset: vi.fn<SagaHubService['stopAndReset']>(async () => {
        order.push('hub.stopAndReset');
      }),
    };
    router = {
      url: '/sagas',
      navigate: vi.fn((commands: unknown[]) => {
        order.push(`navigate ${JSON.stringify(commands)}`);
        return Promise.resolve(true);
      }),
    };
    pageReload = vi.fn<() => void>();

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([recordRequests])),
        provideHttpClientTesting(),
        { provide: Router, useValue: router },
        { provide: SagaHubService, useValue: hub },
        { provide: PAGE_RELOAD, useValue: pageReload },
      ],
    });
    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // Restored even when the verification throws: a hook that throws stops the hooks after it (Angular's
    // own reset of the test module among them), so a test that leaves a request open would otherwise
    // fail every test after it too.
    try {
      httpMock.verify({ ignoreCancelled: true });
    } finally {
      Reflect.deleteProperty(document, 'visibilityState');
      vi.useRealTimers();
      TestBed.resetTestingModule();
    }
  });

  describe('bootstrap()', () => {
    it('loads the session: GET /api/auth/session, then status, user, access and password policy', async () => {
      const loading = service.bootstrap();
      const req = await request('GET', SESSION);
      expect(service.status()).toBe('unknown');
      expect(service.passwordMinLength()).toBeNull();

      req.flush(signedIn(ALICE, { permissions: ['sagas.view'], scoped: [] }));
      await expect(loading).resolves.toBeUndefined();

      expect(service.status()).toBe('authenticated');
      expect(service.isAuthenticated()).toBe(true);
      expect(service.user()).toEqual(ALICE);
      expect(service.access()).toEqual({ permissions: ['sagas.view'], scoped: [] });
      expect(service.passwordMinLength()).toBe(12);
      expect(service.setupRequired()).toBe(false);
      expect(service.signInUnavailable()).toBe(false);
    });

    it('resumes the hub when the session is authenticated', async () => {
      const loading = service.bootstrap();
      await answerSession(signedIn());
      await loading;

      expect(hub.resume).toHaveBeenCalledTimes(1);
    });

    it('does not resume the hub for an anonymous session', async () => {
      const loading = service.bootstrap();
      await answerSession(anonymous());
      await loading;

      expect(hub.resume).not.toHaveBeenCalled();
    });

    it('reports an anonymous session', async () => {
      const loading = service.bootstrap();
      await answerSession(anonymous());
      await loading;

      expect(service.status()).toBe('anonymous');
      expect(service.isAuthenticated()).toBe(false);
      expect(service.user()).toBeNull();
      expect(service.access()).toBeNull();
      expect(service.passwordMinLength()).toBe(12);
    });

    it('reports that setup is required and available', async () => {
      const loading = service.bootstrap();
      await answerSession(anonymous({ setupRequired: true, setupAvailable: true }));
      await loading;

      expect(service.status()).toBe('anonymous');
      expect(service.setupRequired()).toBe(true);
      expect(service.setupAvailable()).toBe(true);
      expect(service.setupProblem()).toBeNull();
    });

    it('reports why setup is required but not available', async () => {
      const problem = {
        code: 'setup_unavailable',
        detail: 'Dashboard:Admin:Password does not meet the password policy.',
      };
      const loading = service.bootstrap();
      await answerSession(
        anonymous({ setupRequired: true, setupAvailable: false, setupProblem: problem }),
      );
      await loading;

      expect(service.setupRequired()).toBe(true);
      expect(service.setupAvailable()).toBe(false);
      expect(service.setupProblem()).toEqual(problem);
    });

    it('never rejects on a network error: the status is unreachable', async () => {
      const loading = service.bootstrap();
      (await request('GET', SESSION)).error(new ProgressEvent('error'));

      await expect(loading).resolves.toBeUndefined();
      expect(service.status()).toBe('unreachable');
      expect(service.isAuthenticated()).toBe(false);
      expect(service.signInUnavailable()).toBe(false);
      expect(hub.resume).not.toHaveBeenCalled();
    });

    it('never rejects on a server error: the status is unreachable', async () => {
      const loading = service.bootstrap();
      (await request('GET', SESSION)).flush('Bad gateway', {
        status: 502,
        statusText: 'Bad Gateway',
      });

      await expect(loading).resolves.toBeUndefined();
      expect(service.status()).toBe('unreachable');
      expect(service.signInUnavailable()).toBe(false);
    });

    it('marks sign-in unavailable on a 503 (the identity store is not ready), still never rejecting', async () => {
      const loading = service.bootstrap();
      (await request('GET', SESSION)).flush(
        { title: 'Sign-in is unavailable', status: 503, code: 'identity_unavailable' },
        { status: 503, statusText: 'Service Unavailable' },
      );

      await expect(loading).resolves.toBeUndefined();
      expect(service.status()).toBe('unreachable');
      expect(service.signInUnavailable()).toBe(true);
    });

    it('treats a 200 that is not a session (a proxy fallback page) as unreachable, not as anonymous', async () => {
      const loading = service.bootstrap();
      (await request('GET', SESSION)).flush({ message: 'not a session' });
      await loading;

      expect(service.status()).toBe('unreachable');
      expect(service.user()).toBeNull();
    });

    it('gives up after 8 s: the request is cancelled and the status is unreachable', async () => {
      let settled = false;
      const loading = service.bootstrap().then(() => {
        settled = true;
      });
      const req = await request('GET', SESSION);

      await vi.advanceTimersByTimeAsync(7999);
      expect(settled).toBe(false);
      expect(service.status()).toBe('unknown');
      expect(req.cancelled).toBe(false);

      await vi.advanceTimersByTimeAsync(1);
      await loading;
      expect(settled).toBe(true);
      expect(service.status()).toBe('unreachable');
      expect(req.cancelled).toBe(true);
    });

    it('does not mark sign-in unavailable on a timeout', async () => {
      const loading = service.bootstrap();
      await request('GET', SESSION);
      await vi.advanceTimersByTimeAsync(8000);
      await loading;

      expect(service.signInUnavailable()).toBe(false);
    });
  });

  describe('refresh()', () => {
    it('is single-flight: callers that arrive while a request is in flight share it', async () => {
      const first = service.refresh();
      const second = service.refresh();
      await flush();
      const requests = httpMock.match(SESSION);
      expect(requests).toHaveLength(1);
      expect(second).toBe(first);

      requests[0].flush(signedIn());
      await expect(first).resolves.toBe('authenticated');
      await expect(second).resolves.toBe('authenticated');

      const third = service.refresh();
      await answerSession(anonymous());
      await expect(third).resolves.toBe('anonymous');
    });

    it('keeps the last known session when the API cannot be reached: an API restart never signs the UI out', async () => {
      await load(signedIn());

      const failing = service.refresh();
      (await request('GET', SESSION)).error(new ProgressEvent('error'));

      await expect(failing).resolves.toBe('authenticated');
      expect(service.user()).toEqual(ALICE);
      expect(service.access()).toEqual(EVERYTHING);
      expect(service.can('sagas.retry')).toBe(true);
      expect(router.navigate).not.toHaveBeenCalled();
      expect(hub.stopAndReset).not.toHaveBeenCalled();
    });

    it('keeps the last known session on a timeout', async () => {
      await load(signedIn());

      const failing = service.refresh();
      await request('GET', SESSION);
      await vi.advanceTimersByTimeAsync(8000);

      await expect(failing).resolves.toBe('authenticated');
      expect(service.user()).toEqual(ALICE);
    });

    it('keeps the last known session on a 503, and says sign-in is unavailable until the next good answer', async () => {
      await load(signedIn());

      const failing = service.refresh();
      (await request('GET', SESSION)).flush(
        { code: 'identity_unavailable' },
        { status: 503, statusText: 'Service Unavailable' },
      );
      await expect(failing).resolves.toBe('authenticated');
      expect(service.user()).toEqual(ALICE);
      expect(service.signInUnavailable()).toBe(true);

      const recovered = service.refresh();
      await answerSession(signedIn());
      await recovered;
      expect(service.signInUnavailable()).toBe(false);
    });

    it('marks unreachable only from unknown: an anonymous session stays anonymous when a refresh fails', async () => {
      await load(anonymous());

      const failing = service.refresh();
      (await request('GET', SESSION)).error(new ProgressEvent('error'));

      await expect(failing).resolves.toBe('anonymous');
      expect(service.status()).toBe('anonymous');
    });

    it('recovers from unreachable on the next good answer', async () => {
      const loading = service.bootstrap();
      (await request('GET', SESSION)).error(new ProgressEvent('error'));
      await loading;
      expect(service.status()).toBe('unreachable');

      const retry = service.refresh();
      await answerSession(signedIn());

      await expect(retry).resolves.toBe('authenticated');
      expect(service.user()).toEqual(ALICE);
      expect(hub.resume).toHaveBeenCalledTimes(1);
    });
  });

  describe('login()', () => {
    it('POSTs the credentials, then reads the session afresh: the new session comes from the GET, not the POST', async () => {
      await load(anonymous());

      const done = service.login('alice', 'correct horse battery');
      const post = await request('POST', LOGIN);
      expect(post.request.body).toEqual({ username: 'alice', password: 'correct horse battery' });
      await expectNoRequest(SESSION);

      post.flush(signedIn(BOB));
      await answerSession(signedIn(ALICE));
      await expect(done).resolves.toBeUndefined();

      expect(service.status()).toBe('authenticated');
      expect(service.user()).toEqual(ALICE);
      expect(hub.resume).toHaveBeenCalledTimes(1);
      expect(order).toEqual([`POST ${LOGIN} [anonymous]`, `GET ${SESSION} [anonymous]`]);
    });

    it('waits for a refresh already in flight and then reads again: that answer may predate the sign-in', async () => {
      await load(anonymous());
      const stale = service.refresh();
      const staleRequest = await request('GET', SESSION);

      const done = service.login('alice', 'pw');
      (await request('POST', LOGIN)).flush({});
      await expectNoRequest(SESSION);

      staleRequest.flush(anonymous());
      await stale;
      await answerSession(signedIn());
      await expect(done).resolves.toBeUndefined();
      expect(service.user()).toEqual(ALICE);
    });

    it('rejects with the HttpErrorResponse when refused, leaving the session as it was and reading nothing', async () => {
      await load(anonymous());

      const done = service.login('alice', 'wrong');
      const rejection = done.catch((e: unknown) => e);
      (await request('POST', LOGIN)).flush(
        { title: 'Sign-in failed', status: 401, code: 'invalid_credentials' },
        { status: 401, statusText: 'Unauthorized' },
      );

      const err = await rejection;
      expect(err).toBeInstanceOf(HttpErrorResponse);
      expect(problemOf(err, '')).toMatchObject({ status: 401, code: 'invalid_credentials' });
      expect(service.status()).toBe('anonymous');
      await expectNoRequest(SESSION);
    });

    it('rejects with the Retry-After a 429 carries', async () => {
      await load(anonymous());

      const rejection = service.login('alice', 'pw').catch((e: unknown) => e);
      (await request('POST', LOGIN)).flush(
        { code: 'rate_limited' },
        { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '12' } },
      );

      expect(problemOf(await rejection, '')).toMatchObject({
        status: 429,
        code: 'rate_limited',
        retryAfterSeconds: 12,
      });
    });

    it('rejects when the session is still anonymous after the POST succeeded', async () => {
      await load(anonymous());

      const rejection = service.login('alice', 'pw').catch((e: unknown) => e);
      (await request('POST', LOGIN)).flush({});
      await answerSession(anonymous());

      const err = await rejection;
      expect(err).toBeInstanceOf(HttpErrorResponse);
      expect(problemOf(err, '').status).toBe(401);
      expect(problemOf(err, '').message).toContain('cookies');
      expect(service.status()).toBe('anonymous');
    });

    it('rejects when the session cannot be read after the POST succeeded', async () => {
      await load(anonymous());

      const rejection = service.login('alice', 'pw').catch((e: unknown) => e);
      (await request('POST', LOGIN)).flush({});
      (await request('GET', SESSION)).error(new ProgressEvent('error'));

      expect(await rejection).toBeInstanceOf(HttpErrorResponse);
      expect(service.status()).toBe('anonymous');
    });
  });

  describe('setup()', () => {
    const body = {
      username: 'root',
      displayName: 'Root',
      password: 'a long enough password',
      code: 'ABCD-EFGH-JKLM-NPQR',
    };

    it('POSTs the first administrator with the one-time code, then reads the session afresh', async () => {
      await load(anonymous({ setupRequired: true, setupAvailable: true }));

      const done = service.setup(body);
      const post = await request('POST', SETUP);
      expect(post.request.body).toEqual(body);
      await expectNoRequest(SESSION);

      post.flush({});
      await answerSession(signedIn({ ...ALICE, id: 'user-root', username: 'root' }));
      await expect(done).resolves.toBeUndefined();

      expect(service.isAuthenticated()).toBe(true);
      expect(service.setupRequired()).toBe(false);
      expect(order).toEqual([`POST ${SETUP} [anonymous]`, `GET ${SESSION} [anonymous]`]);
    });

    it('resolves whatever the session is afterwards: the caller checks isAuthenticated()', async () => {
      await load(anonymous({ setupRequired: true, setupAvailable: true }));

      const done = service.setup(body);
      (await request('POST', SETUP)).flush({});
      await answerSession(anonymous());

      await expect(done).resolves.toBeUndefined();
      expect(service.isAuthenticated()).toBe(false);
    });

    it('rejects when refused, naming the field the server named, and reads nothing', async () => {
      await load(anonymous({ setupRequired: true, setupAvailable: true }));

      const rejection = service.setup(body).catch((e: unknown) => e);
      (await request('POST', SETUP)).flush(
        {
          code: 'invalid_credentials',
          detail: 'The setup code is not correct.',
          errors: { code: ['The setup code is not correct.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );

      expect(problemOf(await rejection, '')).toMatchObject({
        status: 400,
        code: 'invalid_credentials',
        fieldErrors: { code: ['The setup code is not correct.'] },
      });
      await expectNoRequest(SESSION);
    });

    it('rejects with setup_unavailable (409) when setup is not open', async () => {
      await load(anonymous({ setupRequired: true }));

      const rejection = service.setup(body).catch((e: unknown) => e);
      (await request('POST', SETUP)).flush(
        { code: 'setup_unavailable', detail: 'A user already exists.' },
        { status: 409, statusText: 'Conflict' },
      );

      expect(problemOf(await rejection, '')).toMatchObject({
        status: 409,
        code: 'setup_unavailable',
        message: 'A user already exists.',
      });
    });
  });

  describe('changePassword()', () => {
    it('POSTs the current and the new password, then reads the session afresh', async () => {
      await load(signedIn());

      const done = service.changePassword('old password 1', 'new password 2');
      const post = await request('POST', PASSWORD);
      expect(post.request.body).toEqual({
        currentPassword: 'old password 1',
        newPassword: 'new password 2',
      });
      await expectNoRequest(SESSION);

      post.flush({});
      await answerSession(signedIn(ALICE));
      await expect(done).resolves.toBeUndefined();

      expect(service.isAuthenticated()).toBe(true);
      expect(pageReload).not.toHaveBeenCalled();
      expect(order).toEqual([`POST ${PASSWORD} [authenticated]`, `GET ${SESSION} [authenticated]`]);
    });

    it('accepts a session that ends with the change: the caller sees it is not authenticated', async () => {
      await load(signedIn());

      const done = service.changePassword('old password 1', 'new password 2');
      (await request('POST', PASSWORD)).flush({});
      await answerSession(anonymous());

      await expect(done).resolves.toBeUndefined();
      expect(service.isAuthenticated()).toBe(false);
    });

    it('rejects on a wrong current password (400 with errors.currentPassword) and keeps the session', async () => {
      await load(signedIn());

      const rejection = service.changePassword('wrong', 'new password 2').catch((e: unknown) => e);
      (await request('POST', PASSWORD)).flush(
        {
          code: 'invalid_credentials',
          errors: { currentPassword: ['The current password is not correct.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );

      const problem = problemOf(await rejection, '');
      expect(problem.status).toBe(400);
      expect(problem.fieldErrors['currentPassword']).toEqual([
        'The current password is not correct.',
      ]);
      expect(service.isAuthenticated()).toBe(true);
      await expectNoRequest(SESSION);
    });
  });

  describe('logout()', () => {
    it('stops the hub, goes anonymous locally, POSTs, reads the session, then navigates to /login: in that order', async () => {
      await load(signedIn());

      const done = service.logout();
      const post = await request('POST', LOGOUT);
      expect(order).toEqual(['hub.stopAndReset', `POST ${LOGOUT} [anonymous]`]);
      expect(service.status()).toBe('anonymous');
      expect(service.user()).toBeNull();
      expect(service.can('sagas.view')).toBe(false);
      expect(post.request.body).toEqual({});

      post.flush(anonymous());
      await answerSession(anonymous());
      await expect(done).resolves.toBeUndefined();

      expect(order).toEqual([
        'hub.stopAndReset',
        `POST ${LOGOUT} [anonymous]`,
        `GET ${SESSION} [anonymous]`,
        'navigate ["/login"]',
      ]);
      expect(router.navigate).toHaveBeenCalledWith(['/login']);
      expect(service.status()).toBe('anonymous');
    });

    it("does not announce an expired session: signing out is not the session ending behind the SPA's back", async () => {
      await load(signedIn());

      const done = service.logout();
      (await request('POST', LOGOUT)).flush(anonymous());
      await answerSession(anonymous());
      await done;

      expect(router.navigate).toHaveBeenCalledTimes(1);
      expect(router.navigate).not.toHaveBeenCalledWith(['/login'], expect.anything());
      expect(hub.stopAndReset).toHaveBeenCalledTimes(1);
    });

    it('survives a POST that fails: still reads the session, still navigates, never rejects', async () => {
      await load(signedIn());

      const done = service.logout();
      (await request('POST', LOGOUT)).flush(
        { title: 'boom' },
        { status: 500, statusText: 'Server Error' },
      );
      await answerSession(anonymous());

      await expect(done).resolves.toBeUndefined();
      expect(router.navigate).toHaveBeenCalledWith(['/login']);
      expect(service.status()).toBe('anonymous');
    });

    it('survives a POST that never reached the server and a session that cannot be read afterwards', async () => {
      await load(signedIn());

      const done = service.logout();
      (await request('POST', LOGOUT)).error(new ProgressEvent('error'));
      (await request('GET', SESSION)).error(new ProgressEvent('error'));

      await expect(done).resolves.toBeUndefined();
      expect(router.navigate).toHaveBeenCalledWith(['/login']);
      expect(service.status()).toBe('anonymous');
      expect(service.isAuthenticated()).toBe(false);
    });

    it('never rejects when navigation fails', async () => {
      await load(signedIn());
      router.navigate.mockRejectedValueOnce(new Error('chunk failed'));

      const done = service.logout();
      (await request('POST', LOGOUT)).flush(anonymous());
      await answerSession(anonymous());

      await expect(done).resolves.toBeUndefined();
    });
  });

  describe('handleUnauthorized()', () => {
    it('signs out locally, stops the hub, goes to /login with the returnUrl and reason=expired, and reads the session', async () => {
      await load(signedIn());
      router.url = '/sagas/OrderSaga/abc-123?tab=map';

      service.handleUnauthorized();

      expect(hub.stopAndReset).toHaveBeenCalledTimes(1);
      expect(service.status()).toBe('anonymous');
      expect(service.user()).toBeNull();
      expect(service.can('sagas.view')).toBe(false);
      expect(router.navigate).toHaveBeenCalledTimes(1);
      expect(router.navigate).toHaveBeenCalledWith(['/login'], {
        queryParams: { returnUrl: '/sagas/OrderSaga/abc-123?tab=map', reason: 'expired' },
      });
      await answerSession(anonymous());
      expect(service.status()).toBe('anonymous');
    });

    it('is idempotent: the requests in flight when a session ends all fail, and only the first acts', async () => {
      await load(signedIn());

      service.handleUnauthorized();
      service.handleUnauthorized();
      service.handleUnauthorized();

      expect(hub.stopAndReset).toHaveBeenCalledTimes(1);
      expect(router.navigate).toHaveBeenCalledTimes(1);
      await flush();
      expect(httpMock.match(SESSION)).toHaveLength(1);
    });

    it.each(['/login', '/login?returnUrl=%2Fsagas&reason=expired', '/setup'])(
      'does not navigate when already on %s, but still signs out and reads the session',
      async (url) => {
        await load(signedIn());
        router.url = url;

        service.handleUnauthorized();

        expect(router.navigate).not.toHaveBeenCalled();
        expect(hub.stopAndReset).toHaveBeenCalledTimes(1);
        expect(service.status()).toBe('anonymous');
        await answerSession(anonymous());
      },
    );

    it('does nothing when there is no session to end', async () => {
      await load(anonymous());

      service.handleUnauthorized();

      expect(hub.stopAndReset).not.toHaveBeenCalled();
      expect(router.navigate).not.toHaveBeenCalled();
      await expectNoRequest(SESSION);
    });

    it('acts again for a session that began after the last one ended', async () => {
      await load(signedIn());
      service.handleUnauthorized();
      await answerSession(anonymous());

      const loggingIn = service.login('alice', 'pw');
      (await request('POST', LOGIN)).flush({});
      await answerSession(signedIn());
      await loggingIn;
      service.handleUnauthorized();

      expect(router.navigate).toHaveBeenCalledTimes(2);
      await answerSession(anonymous());
    });
  });

  describe('a refresh that finds the session gone', () => {
    it('does what handleUnauthorized does, without reading the session a second time', async () => {
      await load(signedIn());
      router.url = '/sagas';

      const refreshing = service.refresh();
      await answerSession(anonymous());
      await expect(refreshing).resolves.toBe('anonymous');

      expect(hub.stopAndReset).toHaveBeenCalledTimes(1);
      expect(router.navigate).toHaveBeenCalledTimes(1);
      expect(router.navigate).toHaveBeenCalledWith(['/login'], {
        queryParams: { returnUrl: '/sagas', reason: 'expired' },
      });
      expect(service.user()).toBeNull();
      await expectNoRequest(SESSION);
    });
  });

  describe('a different user after a refresh', () => {
    it('reloads the page', async () => {
      await load(signedIn(ALICE));

      const refreshing = service.refresh();
      await answerSession(signedIn(BOB));
      await refreshing;

      expect(pageReload).toHaveBeenCalledTimes(1);
    });

    it('does not reload for the same user', async () => {
      await load(signedIn(ALICE));

      const refreshing = service.refresh();
      await answerSession(signedIn(ALICE, { permissions: ['sagas.view'], scoped: [] }));
      await refreshing;

      expect(pageReload).not.toHaveBeenCalled();
      expect(service.access()?.permissions).toEqual(['sagas.view']);
    });

    it('does not reload for the first user after the session had ended, or after sign-in', async () => {
      await load(signedIn(ALICE));
      const expiring = service.refresh();
      await answerSession(anonymous());
      await expiring;

      const next = service.refresh();
      await answerSession(signedIn(BOB));
      await next;

      expect(pageReload).not.toHaveBeenCalled();
      expect(service.user()).toEqual(BOB);
    });

    it('does not reload on the first answer of the page', async () => {
      const loading = service.bootstrap();
      await answerSession(signedIn(BOB));
      await loading;

      expect(pageReload).not.toHaveBeenCalled();
    });
  });

  describe('noteForbidden()', () => {
    it('reads the session, at most once per 5 s', async () => {
      await load(signedIn());

      service.noteForbidden();
      await answerSession(signedIn());

      service.noteForbidden();
      service.noteForbidden();
      await expectNoRequest(SESSION);

      await vi.advanceTimersByTimeAsync(4999);
      service.noteForbidden();
      await expectNoRequest(SESSION);

      await vi.advanceTimersByTimeAsync(1);
      service.noteForbidden();
      await answerSession(signedIn(ALICE, { permissions: ['sagas.view'], scoped: [] }));
      expect(service.can('sagas.retry')).toBe(false);
    });

    it('catches up with access that changed: the permission-aware UI follows the new session', async () => {
      await load(signedIn());
      expect(service.can('sagas.retry')).toBe(true);

      service.noteForbidden();
      await answerSession(signedIn(ALICE, { permissions: ['sagas.view'], scoped: [] }));

      expect(service.can('sagas.retry')).toBe(false);
      expect(service.can('sagas.view')).toBe(true);
    });
  });

  describe('returning to the tab', () => {
    it('reads the session when the tab becomes visible, at most once per 60 s', async () => {
      await load(signedIn());

      setVisibility('visible');
      await answerSession(signedIn());

      setVisibility('visible');
      await expectNoRequest(SESSION);

      await vi.advanceTimersByTimeAsync(59_999);
      setVisibility('visible');
      await expectNoRequest(SESSION);

      await vi.advanceTimersByTimeAsync(1);
      setVisibility('visible');
      await answerSession(signedIn());
    });

    it('ignores the tab being hidden, and a hidden event does not use up the interval', async () => {
      await load(signedIn());

      setVisibility('hidden');
      await expectNoRequest(SESSION);

      setVisibility('visible');
      await answerSession(signedIn());
    });

    it('notices a session that was ended elsewhere', async () => {
      await load(signedIn());
      router.url = '/sagas';

      setVisibility('visible');
      await answerSession(anonymous());

      expect(service.status()).toBe('anonymous');
      expect(router.navigate).toHaveBeenCalledWith(['/login'], {
        queryParams: { returnUrl: '/sagas', reason: 'expired' },
      });
    });

    it('stops listening when the service is destroyed', () => {
      const remove = vi.spyOn(document, 'removeEventListener');

      TestBed.resetTestingModule();

      expect(remove).toHaveBeenCalledWith('visibilitychange', expect.any(Function));
    });
  });

  describe('the hub session probe', () => {
    let probe: () => Promise<boolean>;

    beforeEach(() => {
      expect(hub.setSessionProbe).toHaveBeenCalledTimes(1);
      probe = hub.setSessionProbe.mock.calls[0][0];
    });

    it('answers true while signed in', async () => {
      await load(signedIn());

      const alive = probe();
      await answerSession(signedIn());

      await expect(alive).resolves.toBe(true);
    });

    it('answers false when the session is gone, and ends the session as a refresh that finds it gone does', async () => {
      await load(signedIn());

      const alive = probe();
      await answerSession(anonymous());

      await expect(alive).resolves.toBe(false);
      expect(hub.stopAndReset).toHaveBeenCalledTimes(1);
      expect(router.navigate).toHaveBeenCalledWith(['/login'], {
        queryParams: { returnUrl: '/sagas', reason: 'expired' },
      });
    });

    it('answers false for an anonymous session', async () => {
      await load(anonymous());

      const alive = probe();
      await answerSession(anonymous());

      await expect(alive).resolves.toBe(false);
    });

    it('answers true when the API cannot be reached and no session is known', async () => {
      const alive = probe();
      (await request('GET', SESSION)).error(new ProgressEvent('error'));

      await expect(alive).resolves.toBe(true);
      expect(service.status()).toBe('unreachable');
    });

    it('answers true when the API cannot be reached and a session is known', async () => {
      await load(signedIn());

      const alive = probe();
      (await request('GET', SESSION)).error(new ProgressEvent('error'));

      await expect(alive).resolves.toBe(true);
      expect(service.status()).toBe('authenticated');
      expect(hub.stopAndReset).not.toHaveBeenCalled();
    });

    it('answers true on a 503 (the identity store is not ready)', async () => {
      await load(signedIn());

      const alive = probe();
      (await request('GET', SESSION)).flush(
        { code: 'identity_unavailable' },
        { status: 503, statusText: 'Service Unavailable' },
      );

      await expect(alive).resolves.toBe(true);
    });

    it('always settles: a session request that never answers is cut off after 8 s and counts as "cannot check", so true', async () => {
      await load(signedIn());
      let settled = false;
      const alive = probe().then((answer) => {
        settled = true;
        return answer;
      });
      const req = await request('GET', SESSION);

      await vi.advanceTimersByTimeAsync(7999);
      expect(settled).toBe(false);

      await vi.advanceTimersByTimeAsync(1);
      await expect(alive).resolves.toBe(true);
      expect(req.cancelled).toBe(true);
      expect(hub.stopAndReset).not.toHaveBeenCalled();
    });

    it('settles true for a session request that never answers even before any session is known', async () => {
      const alive = probe();
      await request('GET', SESSION);

      await vi.advanceTimersByTimeAsync(8000);

      await expect(alive).resolves.toBe(true);
      expect(service.status()).toBe('unreachable');
    });
  });

  describe('permissions', () => {
    const access: SessionAccess = {
      permissions: ['sagas.view'],
      scoped: [
        { sagaType: 'OrderSaga', permissions: ['sagas.data', 'sagas.retry'] },
        { sagaType: 'PaymentSaga', permissions: ['sagas.data'] },
      ],
    };

    it('holds nothing without a session', () => {
      expect(service.can('sagas.view')).toBe(false);
      expect(service.can('sagas.view', 'OrderSaga')).toBe(false);
      expect(service.canAny('sagas.view')).toBe(false);
      expect(service.canManageAccess()).toBe(false);
    });

    it('can: a permission held for every saga type holds for any type, and with no type named', async () => {
      await load(signedIn(ALICE, access));

      expect(service.can('sagas.view')).toBe(true);
      expect(service.can('sagas.view', 'OrderSaga')).toBe(true);
      expect(service.can('sagas.view', 'SomethingElse')).toBe(true);
    });

    it('can: a scoped permission holds for exactly that saga type and for no other', async () => {
      await load(signedIn(ALICE, access));

      expect(service.can('sagas.retry', 'OrderSaga')).toBe(true);
      expect(service.can('sagas.data', 'OrderSaga')).toBe(true);
      expect(service.can('sagas.data', 'PaymentSaga')).toBe(true);
      expect(service.can('sagas.retry', 'PaymentSaga')).toBe(false);
      expect(service.can('sagas.retry', 'ShippingSaga')).toBe(false);
    });

    it('can: a scoped permission does not hold for "every type" when no type is named', async () => {
      await load(signedIn(ALICE, access));

      expect(service.can('sagas.retry')).toBe(false);
      expect(service.can('sagas.data')).toBe(false);
    });

    it('can: saga types and permission keys compare ordinally, so case matters', async () => {
      await load(signedIn(ALICE, access));

      expect(service.can('sagas.retry', 'ordersaga')).toBe(false);
      expect(service.can('sagas.retry', 'ORDERSAGA')).toBe(false);
      expect(service.can('sagas.retry', 'OrderSaga ')).toBe(false);
      expect(service.can('Sagas.View' as PermissionKey)).toBe(false);
    });

    it('canAny: held for every type or for at least one', async () => {
      await load(signedIn(ALICE, access));

      expect(service.canAny('sagas.view')).toBe(true);
      expect(service.canAny('sagas.data')).toBe(true);
      expect(service.canAny('sagas.retry')).toBe(true);
      expect(service.canAny('access.manage')).toBe(false);
    });

    it('canManageAccess: access.manage held for every saga type counts', async () => {
      await load(signedIn(ALICE, EVERYTHING));

      expect(service.canManageAccess()).toBe(true);
    });

    it('canManageAccess: a scoped access.manage is ignored (the API never scopes it)', async () => {
      await load(
        signedIn(ALICE, {
          permissions: ['sagas.view'],
          scoped: [{ sagaType: 'OrderSaga', permissions: ['access.manage'] }],
        }),
      );

      expect(service.canManageAccess()).toBe(false);
    });

    it('describes the API key: authenticated, no user, access as configured', async () => {
      await load(
        signedIn(null, { permissions: ['sagas.view', 'sagas.data', 'sagas.retry'], scoped: [] }),
      );

      expect(service.isAuthenticated()).toBe(true);
      expect(service.user()).toBeNull();
      expect(service.can('sagas.retry', 'AnySaga')).toBe(true);
      expect(service.canManageAccess()).toBe(false);
    });
  });
});

describe('PAGE_RELOAD', () => {
  it('reloads the document of the page by default', () => {
    const reload = vi.fn();
    TestBed.configureTestingModule({
      providers: [{ provide: DOCUMENT, useValue: { location: { reload } } }],
    });

    TestBed.inject(PAGE_RELOAD)();

    expect(reload).toHaveBeenCalledTimes(1);
  });
});
