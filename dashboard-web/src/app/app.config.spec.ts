import { HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Mock, vi } from 'vitest';
import { appConfig } from './app.config';
import { PAGE_RELOAD } from './services/auth.service';
import { GUIDE_PERMISSION_CHECK } from './services/guide.service';
import { STALE_CHUNK_RELOAD_KEY } from './stale-chunk-reload';
import { AuthMock, createAuthMock, provideAuthMock } from './testing/auth-mock';

describe('app config', () => {
  let reload: Mock<() => void>;
  let auth: AuthMock;

  // The real providers (router, HTTP client, interceptor, initializer) with only the edges replaced: the
  // auth service is a mock, so no request leaves the spec, and the HTTP backend is the testing one.
  beforeEach(() => {
    sessionStorage.clear();
    reload = vi.fn();
    auth = createAuthMock();
    TestBed.configureTestingModule({
      providers: [
        ...appConfig.providers,
        { provide: PAGE_RELOAD, useValue: reload },
        provideAuthMock(auth),
        provideHttpClientTesting(),
      ],
    });
  });

  afterEach(() => {
    sessionStorage.clear();
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  });

  describe('the session', () => {
    it('is read before the app starts, once', async () => {
      const status = TestBed.inject(ApplicationInitStatus);
      await status.donePromise;

      expect(auth.bootstrap).toHaveBeenCalledTimes(1);
    });

    it('holds the app until the session was read', async () => {
      let finish!: () => void;
      auth.bootstrap.mockReturnValue(new Promise<void>((resolve) => (finish = resolve)));

      const status = TestBed.inject(ApplicationInitStatus);
      await Promise.resolve();
      expect(status.done).toBe(false);

      finish();
      await status.donePromise;
      expect(status.done).toBe(true);
    });
  });

  describe('guide mode', () => {
    const scopedViewer = {
      permissions: ['sagas.view' as const],
      scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.data', 'sagas.retry'] }],
    };

    it('asks the session: for a saga type, what the session holds for that type', () => {
      auth.access.set(scopedViewer);
      const check = TestBed.inject(GUIDE_PERMISSION_CHECK);

      expect(check('sagas.data', 'OrderSaga')).toBe(true);
      expect(check('sagas.retry', 'OrderSaga')).toBe(true);
      expect(check('sagas.data', 'ShipSaga')).toBe(false);
      expect(check('sagas.view', 'ShipSaga')).toBe(true); // held for every type
    });

    it('asks, without a saga type, whether the session holds the permission for any type', () => {
      auth.access.set({
        permissions: [],
        scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.view'] }],
      });
      const check = TestBed.inject(GUIDE_PERMISSION_CHECK);

      expect(check('sagas.view')).toBe(true); // the list is there for a user scoped to one type
      expect(check('sagas.retry')).toBe(false);
    });

    it('never counts access.manage as held by a session that only has it scoped', () => {
      auth.access.set({
        permissions: ['sagas.view'],
        scoped: [{ sagaType: 'OrderSaga', permissions: ['access.manage'] }],
      });
      const check = TestBed.inject(GUIDE_PERMISSION_CHECK);

      expect(check('access.manage')).toBe(false);
      auth.access.set({ permissions: ['sagas.view', 'access.manage'], scoped: [] });
      expect(check('access.manage')).toBe(true);
    });

    it('holds nothing once the session is gone', () => {
      const check = TestBed.inject(GUIDE_PERMISSION_CHECK);
      expect(check('sagas.view')).toBe(true);

      auth.status.set('anonymous');

      expect(check('sagas.view')).toBe(false);
      expect(check('sagas.view', 'OrderSaga')).toBe(false);
    });
  });

  describe('the HTTP client', () => {
    it('carries no API key and no Authorization header, and the built-in interceptor adds the antiforgery header to a write only', () => {
      document.cookie = 'XSRF-TOKEN=cookie-token; path=/';
      const http = TestBed.inject(HttpClient);
      const backend = TestBed.inject(HttpTestingController);

      http.get('/api/sagas').subscribe();
      http.post('/api/sagas/OrderSaga/1/retry', {}).subscribe();

      const get = backend.expectOne((r) => r.method === 'GET');
      const post = backend.expectOne((r) => r.method === 'POST');
      for (const request of [get, post]) {
        expect(request.request.headers.has('X-Api-Key')).toBe(false);
        expect(request.request.headers.has('Authorization')).toBe(false);
      }
      expect(post.request.headers.get('X-XSRF-TOKEN')).toBe('cookie-token');
      expect(get.request.headers.has('X-XSRF-TOKEN')).toBe(false);
      get.flush(null);
      post.flush(null);
    });

    it('ends the session when the API answers 401 to a request that was sent under the current identity', () => {
      auth.currentIdentityEpoch.mockReturnValue(7);
      const http = TestBed.inject(HttpClient);
      const backend = TestBed.inject(HttpTestingController);

      http.get('/api/sagas').subscribe({ error: () => undefined });
      backend.expectOne('/api/sagas').flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(auth.handleUnauthorized).toHaveBeenCalledWith(7);
    });

    it('notes a 403 so access is read again', () => {
      const http = TestBed.inject(HttpClient);
      const backend = TestBed.inject(HttpTestingController);

      http.get('/api/sagas').subscribe({ error: () => undefined });
      backend.expectOne('/api/sagas').flush(null, { status: 403, statusText: 'Forbidden' });

      expect(auth.noteForbidden).toHaveBeenCalledTimes(1);
    });
  });

  it('reloads the page when a lazy page cannot be imported, once', async () => {
    const router = TestBed.inject(Router);
    router.resetConfig([
      {
        path: 'broken',
        loadComponent: () =>
          Promise.reject(new TypeError('Failed to fetch dynamically imported module: /chunk-A.js')),
      },
    ]);

    await router.navigateByUrl('/broken').catch(() => false);
    await router.navigateByUrl('/broken').catch(() => false);

    expect(reload).toHaveBeenCalledTimes(1);
    expect(sessionStorage.getItem(STALE_CHUNK_RELOAD_KEY)).not.toBeNull();
  });

  it('does not reload the page for a route that does not exist', async () => {
    const router = TestBed.inject(Router);
    router.resetConfig([]);

    await router.navigateByUrl('/nowhere').catch(() => false);

    expect(reload).not.toHaveBeenCalled();
  });
});
