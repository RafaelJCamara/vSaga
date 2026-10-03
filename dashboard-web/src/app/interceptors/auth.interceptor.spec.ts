import {
  HttpClient,
  HttpErrorResponse,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable } from 'rxjs';
import { AuthService } from '../services/auth.service';
import { AuthMock, createAuthMock, provideAuthMock } from '../testing/auth-mock';
import { problem } from '../testing/http-error';
import { authInterceptor } from './auth.interceptor';

const XSRF = 'X-XSRF-TOKEN';

function setXsrfCookie(value: string | null): void {
  document.cookie =
    value === null
      ? 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/'
      : `XSRF-TOKEN=${value}; path=/`;
}

/** What a call settled with, observed without awaiting: the backend is flushed by hand. */
interface Outcome {
  value?: unknown;
  error?: HttpErrorResponse;
  done: boolean;
}

function run(call: Observable<unknown>): Outcome {
  const outcome: Outcome = { done: false };
  call.subscribe({
    next: (value) => (outcome.value = value),
    error: (error: HttpErrorResponse) => {
      outcome.error = error;
      outcome.done = true;
    },
    complete: () => (outcome.done = true),
  });
  return outcome;
}

const refuse = (request: TestRequest, status: number, body: object | string | null = null) =>
  request.flush(body, { status, statusText: 'Refused' });
const antiforgery = () => problem('antiforgery', 'The antiforgery token is missing or not valid.');
/** Lets the promise chain behind `from(auth.refresh())` run. */
const settle = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

describe('authInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;
  let auth: AuthMock;

  beforeEach(() => {
    setXsrfCookie(null);
    auth = createAuthMock();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        provideAuthMock(auth),
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    try {
      backend.verify();
    } finally {
      // Also when a request was left open: the next spec must not meet this one's cookie or module.
      setXsrfCookie(null);
      TestBed.resetTestingModule();
    }
  });

  describe('credentials', () => {
    it('sends neither an API key nor an Authorization header, on a read or a write', () => {
      setXsrfCookie('token');

      run(http.get('/api/sagas'));
      run(http.post('/api/sagas/OrderSaga/1/retry', {}));

      const requests = backend.match(() => true);
      expect(requests).toHaveLength(2);
      for (const request of requests) {
        expect(request.request.headers.has('X-Api-Key'), request.request.url).toBe(false);
        expect(request.request.headers.has('Authorization'), request.request.url).toBe(false);
        request.flush(null);
      }
    });

    it('leaves the antiforgery header to the built-in interceptor: a POST carries the cookie, a GET does not', () => {
      setXsrfCookie('cookie-token');

      run(http.post('/api/sagas/OrderSaga/1/retry', {}));
      run(http.get('/api/sagas'));

      const post = backend.expectOne((r) => r.method === 'POST');
      const get = backend.expectOne((r) => r.method === 'GET');
      expect(post.request.headers.get(XSRF)).toBe('cookie-token');
      expect(get.request.headers.has(XSRF)).toBe(false);
      post.flush(null);
      get.flush(null);
    });

    it('passes the response through unchanged', () => {
      const outcome = run(http.get('/api/sagas'));

      backend.expectOne('/api/sagas').flush({ items: [] });

      expect(outcome.value).toEqual({ items: [] });
      expect(outcome.done).toBe(true);
      expect(auth.handleUnauthorized).not.toHaveBeenCalled();
      expect(auth.noteForbidden).not.toHaveBeenCalled();
    });
  });

  describe('a 401', () => {
    it('ends the session, and the call still fails', () => {
      const outcome = run(http.get('/api/sagas'));

      refuse(backend.expectOne('/api/sagas'), 401, { code: 'unauthenticated' });

      expect(auth.handleUnauthorized).toHaveBeenCalledTimes(1);
      expect(outcome.error?.status).toBe(401);
    });

    it.each(['login', 'logout', 'setup', 'session'])(
      'is an answer to the call, not an ended session, on /api/auth/%s',
      (endpoint) => {
        const outcome = run(http.post(`/api/auth/${endpoint}`, {}));

        refuse(backend.expectOne(`/api/auth/${endpoint}`), 401);

        expect(auth.handleUnauthorized).not.toHaveBeenCalled();
        expect(outcome.error?.status).toBe(401);
      },
    );

    it('ignores the query string when it tells a sign-in endpoint', () => {
      run(http.get('/api/auth/session?probe=1'));

      refuse(backend.expectOne('/api/auth/session?probe=1'), 401);

      expect(auth.handleUnauthorized).not.toHaveBeenCalled();
    });

    it('ends the session on the other auth endpoints: a password change is made by a signed-in user', () => {
      run(http.post('/api/auth/password', {}));

      refuse(backend.expectOne('/api/auth/password'), 401);

      expect(auth.handleUnauthorized).toHaveBeenCalledTimes(1);
    });

    // A sign-in, sign-out or password change moves the identity epoch. A 401 to a request sent before that
    // describes the session that was replaced; handleUnauthorized drops it when it is given the epoch the
    // request was sent under.
    it('hands handleUnauthorized the identity epoch from when the request was sent, not from when it failed', () => {
      auth.currentIdentityEpoch.mockReturnValue(3);
      run(http.get('/api/sagas'));
      const request = backend.expectOne('/api/sagas');

      auth.currentIdentityEpoch.mockReturnValue(4); // a sign-in completed while the request was in flight
      refuse(request, 401);

      expect(auth.handleUnauthorized).toHaveBeenCalledWith(3);
    });

    it('reads the epoch of each request on its own', () => {
      auth.currentIdentityEpoch.mockReturnValue(1);
      run(http.get('/api/a'));
      auth.currentIdentityEpoch.mockReturnValue(2);
      run(http.get('/api/b'));

      refuse(backend.expectOne('/api/b'), 401);
      refuse(backend.expectOne('/api/a'), 401);

      expect(auth.handleUnauthorized.mock.calls).toEqual([[2], [1]]);
    });
  });

  describe('a 403', () => {
    it('notes it, with no epoch, and the call still fails', () => {
      const outcome = run(http.get('/api/sagas'));

      refuse(backend.expectOne('/api/sagas'), 403, { code: 'forbidden' });

      expect(auth.noteForbidden).toHaveBeenCalledTimes(1);
      expect(auth.noteForbidden).toHaveBeenCalledWith();
      expect(auth.handleUnauthorized).not.toHaveBeenCalled();
      expect(outcome.error?.status).toBe(403);
    });
  });

  describe('other failures', () => {
    it.each([
      ['a 404', 404],
      ['a 409', 409],
      ['a 429', 429],
      ['a 500', 500],
      ['a 503', 503],
    ])('passes %s on without touching the session', (_name, status) => {
      const outcome = run(http.get('/api/sagas'));

      refuse(backend.expectOne('/api/sagas'), status, { error: 'nope' });

      expect(outcome.error?.status).toBe(status);
      expect(auth.handleUnauthorized).not.toHaveBeenCalled();
      expect(auth.noteForbidden).not.toHaveBeenCalled();
      expect(auth.refresh).not.toHaveBeenCalled();
    });

    it('passes a network failure on', () => {
      const outcome = run(http.get('/api/sagas'));

      backend.expectOne('/api/sagas').error(new ProgressEvent('error'));

      expect(outcome.error?.status).toBe(0);
      expect(auth.handleUnauthorized).not.toHaveBeenCalled();
      expect(auth.noteForbidden).not.toHaveBeenCalled();
    });
  });

  describe('which URLs it acts for', () => {
    it.each(['/assets/config.json', '/hubs/saga/negotiate', '/api', '/apiary/x', '/other/api/x'])(
      'ignores %s',
      (url) => {
        run(http.get(url));
        refuse(backend.expectOne(url), 401);

        run(http.get(url));
        refuse(backend.expectOne(url), 403);

        expect(auth.handleUnauthorized).not.toHaveBeenCalled();
        expect(auth.noteForbidden).not.toHaveBeenCalled();
      },
    );

    it('does not retry an antiforgery refusal of a URL outside /api/', () => {
      setXsrfCookie('old');
      run(http.post('/other/thing', {}));

      refuse(backend.expectOne('/other/thing'), 400, antiforgery());

      expect(auth.refresh).not.toHaveBeenCalled();
    });

    it('resolves a relative URL against the page, as the browser does', () => {
      run(http.get('api/sagas'));

      refuse(backend.expectOne('api/sagas'), 401);

      expect(auth.handleUnauthorized).toHaveBeenCalledTimes(1);
    });

    // Only this API's answers say anything about this session (and the built-in XSRF interceptor is
    // same-origin only too): a 401 from another host must not sign the user out, nor its 400 re-read
    // the session.
    it.each([
      'https://elsewhere.example/api/sagas',
      'http://localhost:9/api/sagas',
      '//elsewhere.example/api/sagas',
    ])('ignores %s: another origin', async (url) => {
      setXsrfCookie('stale');
      run(http.get(url));
      refuse(backend.expectOne(url), 401);
      run(http.get(url));
      refuse(backend.expectOne(url), 403);
      run(http.post(url, {}));
      refuse(backend.expectOne(url), 400, antiforgery());
      await settle();

      expect(auth.handleUnauthorized).not.toHaveBeenCalled();
      expect(auth.noteForbidden).not.toHaveBeenCalled();
      expect(auth.refresh).not.toHaveBeenCalled();
    });

    it('acts for an absolute URL on the API path', () => {
      run(http.get(`${document.location.origin}/api/sagas`));

      refuse(backend.expectOne(`${document.location.origin}/api/sagas`), 403);

      expect(auth.noteForbidden).toHaveBeenCalledTimes(1);
    });
  });

  describe('an antiforgery refusal (400, code antiforgery)', () => {
    /** The token the next session read leaves in the cookie. */
    function refreshRenews(token: string | null): void {
      auth.refresh.mockImplementation(async () => {
        setXsrfCookie(token);
        return auth.status();
      });
    }

    it('reads the session again and sends the request once more with the new token', async () => {
      setXsrfCookie('stale');
      refreshRenews('fresh');
      const outcome = run(http.post('/api/sagas/OrderSaga/1/retry', { note: 'again' }));
      const first = backend.expectOne('/api/sagas/OrderSaga/1/retry');
      expect(first.request.headers.get(XSRF)).toBe('stale');

      refuse(first, 400, antiforgery());
      await settle();

      expect(auth.refresh).toHaveBeenCalledTimes(1);
      const second = backend.expectOne('/api/sagas/OrderSaga/1/retry');
      expect(second.request.headers.get(XSRF)).toBe('fresh');
      expect(second.request.method).toBe('POST');
      expect(second.request.body).toEqual({ note: 'again' });
      second.flush({ queued: true });
      expect(outcome.value).toEqual({ queued: true });
      expect(outcome.error).toBeUndefined();
    });

    it('sends the request with a token it had none of before', async () => {
      refreshRenews('fresh');
      const outcome = run(http.post('/api/auth/login', { username: 'a', password: 'b' }));
      const first = backend.expectOne('/api/auth/login');
      expect(first.request.headers.has(XSRF)).toBe(false);

      refuse(first, 400, antiforgery());
      await settle();

      const second = backend.expectOne('/api/auth/login');
      expect(second.request.headers.get(XSRF)).toBe('fresh');
      second.flush(null);
      expect(outcome.done).toBe(true);
    });

    it("keeps the request's other headers", async () => {
      setXsrfCookie('stale');
      refreshRenews('fresh');
      run(http.post('/api/sagas/OrderSaga/1/retry', {}, { headers: { 'X-Trace': 'abc' } }));

      refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 400, antiforgery());
      await settle();

      const second = backend.expectOne('/api/sagas/OrderSaga/1/retry');
      expect(second.request.headers.get('X-Trace')).toBe('abc');
      expect(second.request.headers.has('X-Api-Key')).toBe(false);
      expect(second.request.headers.has('Authorization')).toBe(false);
      second.flush(null);
    });

    it('waits for the session read before it sends the request again', async () => {
      setXsrfCookie('stale');
      let finishRead!: () => void;
      auth.refresh.mockImplementation(
        () =>
          new Promise((resolve) => {
            finishRead = () => {
              setXsrfCookie('fresh');
              resolve(auth.status());
            };
          }),
      );
      run(http.post('/api/sagas/OrderSaga/1/retry', {}));

      refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 400, antiforgery());
      await settle();
      backend.expectNone('/api/sagas/OrderSaga/1/retry');

      finishRead();
      await settle();
      backend.expectOne('/api/sagas/OrderSaga/1/retry').flush(null);
    });

    it('retries once: a second refusal reaches the caller, with no third request', async () => {
      setXsrfCookie('stale');
      refreshRenews('fresh');
      const outcome = run(http.post('/api/sagas/OrderSaga/1/retry', {}));
      refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 400, antiforgery());
      await settle();

      refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 400, antiforgery());
      await settle();

      expect(auth.refresh).toHaveBeenCalledTimes(1);
      expect(outcome.error?.status).toBe(400);
      expect(outcome.error?.error).toEqual(antiforgery());
      backend.expectNone('/api/sagas/OrderSaga/1/retry');
    });

    it("gives the retried request's own failure to the caller", async () => {
      setXsrfCookie('stale');
      refreshRenews('fresh');
      const outcome = run(http.post('/api/sagas/OrderSaga/1/retry', {}));
      refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 400, antiforgery());
      await settle();

      refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 409, { error: 'Not retryable.' });

      expect(outcome.error?.status).toBe(409);
      expect(auth.refresh).toHaveBeenCalledTimes(1);
    });

    it('retries each call once, also when the same call is subscribed to again', async () => {
      setXsrfCookie('stale');
      let reads = 0;
      auth.refresh.mockImplementation(async () => {
        setXsrfCookie(`fresh-${++reads}`); // a new token every time, as the API issues them
        return auth.status();
      });
      const call = http.post('/api/sagas/OrderSaga/1/retry', {});

      for (let attempt = 1; attempt <= 2; attempt++) {
        run(call);
        refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 400, antiforgery());
        await settle();
        backend.expectOne('/api/sagas/OrderSaga/1/retry').flush(null);
        expect(auth.refresh).toHaveBeenCalledTimes(attempt);
      }
    });

    // A read that failed or timed out re-issues nothing: the retry would carry the token that was refused.
    it('does not retry when the session read left the token it was refused with', async () => {
      setXsrfCookie('stale');
      refreshRenews('stale');
      const outcome = run(http.post('/api/sagas/OrderSaga/1/retry', {}));

      refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 400, antiforgery());
      await settle();

      expect(auth.refresh).toHaveBeenCalledTimes(1);
      expect(outcome.error?.status).toBe(400);
      expect(outcome.error?.error).toEqual(antiforgery());
      backend.expectNone('/api/sagas/OrderSaga/1/retry');
    });

    it.each(['PUT', 'PATCH', 'DELETE'] as const)(
      'retries a %s as it does a POST',
      async (method) => {
        setXsrfCookie('stale');
        refreshRenews('fresh');
        const outcome = run(http.request(method, '/api/admin/users/1', { body: { a: 1 } }));
        const first = backend.expectOne('/api/admin/users/1');
        expect(first.request.headers.get(XSRF)).toBe('stale');

        refuse(first, 400, antiforgery());
        await settle();

        const second = backend.expectOne('/api/admin/users/1');
        expect(second.request.method).toBe(method);
        expect(second.request.headers.get(XSRF)).toBe('fresh');
        second.flush(null);
        expect(outcome.error).toBeUndefined();
      },
    );

    it('does not retry when the session read left no token: it would be refused the same way', async () => {
      setXsrfCookie('stale');
      refreshRenews(null);
      const outcome = run(http.post('/api/sagas/OrderSaga/1/retry', {}));

      refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 400, antiforgery());
      await settle();

      expect(auth.refresh).toHaveBeenCalledTimes(1);
      expect(outcome.error?.status).toBe(400);
      expect(outcome.error?.error).toEqual(antiforgery());
      backend.expectNone('/api/sagas/OrderSaga/1/retry');
    });

    it.each(['GET', 'HEAD', 'OPTIONS', 'TRACE'] as const)(
      'does not retry a %s: a safe request carries no token for the API to refuse',
      async (method) => {
        setXsrfCookie('stale');
        const outcome = run(http.request(method, '/api/auth/session'));

        refuse(backend.expectOne('/api/auth/session'), 400, antiforgery());
        await settle();

        expect(auth.refresh).not.toHaveBeenCalled();
        expect(outcome.error?.status).toBe(400);
      },
    );

    it.each([
      ['no code', { error: 'Bad input.' }],
      ['another code', problem('validation', 'Invalid.')],
      ['a body that is not an object', 'Bad Request'],
      ['no body', null],
    ])('does not retry a 400 with %s', async (_name, body) => {
      setXsrfCookie('stale');
      const outcome = run(http.post('/api/sagas/OrderSaga/1/retry', {}));

      refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 400, body);
      await settle();

      expect(auth.refresh).not.toHaveBeenCalled();
      expect(outcome.error?.status).toBe(400);
    });

    it('does not take a 403 or a 401 with that code for a refusal of the token', async () => {
      run(http.post('/api/a', {}));
      refuse(backend.expectOne('/api/a'), 403, antiforgery());
      run(http.post('/api/b', {}));
      refuse(backend.expectOne('/api/b'), 401, antiforgery());
      await settle();

      expect(auth.refresh).not.toHaveBeenCalled();
    });

    // The retry is a request of its own, sent after the session was read: an identity change that
    // happened meanwhile (the read itself may be what signed the user out) belongs to the first request's
    // epoch, not to the retry's.
    it('reads the identity epoch again for the retried request', async () => {
      setXsrfCookie('stale');
      auth.currentIdentityEpoch.mockReturnValue(5);
      auth.refresh.mockImplementation(async () => {
        setXsrfCookie('fresh');
        auth.currentIdentityEpoch.mockReturnValue(6);
        return auth.status();
      });
      run(http.post('/api/sagas/OrderSaga/1/retry', {}));
      refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 400, antiforgery());
      await settle();

      refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 401);

      expect(auth.handleUnauthorized).toHaveBeenCalledTimes(1);
      expect(auth.handleUnauthorized).toHaveBeenCalledWith(6);
    });

    it('treats a 403 of the retried request like any other 403', async () => {
      setXsrfCookie('stale');
      refreshRenews('fresh');
      run(http.post('/api/sagas/OrderSaga/1/retry', {}));
      refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 400, antiforgery());
      await settle();

      refuse(backend.expectOne('/api/sagas/OrderSaga/1/retry'), 403, { code: 'forbidden' });

      expect(auth.noteForbidden).toHaveBeenCalledTimes(1);
    });

    it('keeps a 401 of the sign-in endpoints out of the session even on the retry', async () => {
      setXsrfCookie('stale');
      refreshRenews('fresh');
      run(http.post('/api/auth/login', {}));
      refuse(backend.expectOne('/api/auth/login'), 400, antiforgery());
      await settle();

      refuse(backend.expectOne('/api/auth/login'), 401, { code: 'invalid_credentials' });

      expect(auth.handleUnauthorized).not.toHaveBeenCalled();
    });
  });
});

// The mock stands in for the service's single flight; the real service is what makes it true.
describe('authInterceptor with the real auth service', () => {
  const session = {
    authenticated: true,
    setupRequired: false,
    setupAvailable: false,
    setupProblem: null,
    passwordMinLength: 12,
    user: { id: 'u1', username: 'alice', displayName: 'Alice', mustChangePassword: false },
    access: { permissions: ['sagas.view'], scoped: [] },
  };

  beforeEach(() => {
    setXsrfCookie(null);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
  });

  afterEach(() => {
    try {
      TestBed.inject(HttpTestingController).verify();
    } finally {
      setXsrfCookie(null);
      TestBed.resetTestingModule();
    }
  });

  it('shares one session read between requests refused together, and sends each again with the new token', async () => {
    setXsrfCookie('stale');
    const http = TestBed.inject(HttpClient);
    const backend = TestBed.inject(HttpTestingController);
    TestBed.inject(AuthService);
    const first = run(http.post('/api/a', {}));
    const second = run(http.post('/api/b', {}));

    refuse(backend.expectOne('/api/a'), 400, antiforgery());
    refuse(backend.expectOne('/api/b'), 400, antiforgery());
    await settle();

    const read = backend.expectOne('/api/auth/session');
    setXsrfCookie('fresh');
    read.flush(session);
    await settle();

    const again = [backend.expectOne('/api/a'), backend.expectOne('/api/b')];
    expect(again.map((r) => r.request.headers.get(XSRF))).toEqual(['fresh', 'fresh']);
    again.forEach((r) => r.flush(null));
    expect([first.done, second.done]).toEqual([true, true]);
    expect([first.error, second.error]).toEqual([undefined, undefined]);
  });
});
