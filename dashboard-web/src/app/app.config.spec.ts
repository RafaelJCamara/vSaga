import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Mock, vi } from 'vitest';
import { appConfig } from './app.config';
import { PAGE_RELOAD } from './services/auth.service';
import { STALE_CHUNK_RELOAD_KEY } from './stale-chunk-reload';

describe('app config', () => {
  let reload: Mock<() => void>;

  beforeEach(() => {
    sessionStorage.clear();
    reload = vi.fn();
    TestBed.configureTestingModule({
      providers: [...appConfig.providers, { provide: PAGE_RELOAD, useValue: reload }],
    });
  });

  afterEach(() => sessionStorage.clear());

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
