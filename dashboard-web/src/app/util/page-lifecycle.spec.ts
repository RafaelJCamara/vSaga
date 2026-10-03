import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { vi } from 'vitest';
import { leaveTo, trackDestroyed } from './page-lifecycle';

describe('leaveTo', () => {
  let navigate: ReturnType<typeof vi.fn<(url: string) => Promise<boolean>>>;
  let router: Router;

  beforeEach(() => {
    navigate = vi.fn<(url: string) => Promise<boolean>>().mockResolvedValue(true);
    router = { navigateByUrl: navigate } as unknown as Router;
  });

  const urls = () => navigate.mock.calls.map(([url]) => url);

  it('goes to the destination, and nowhere else when it is reached', async () => {
    await leaveTo(router, '/account', () => true);

    expect(urls()).toEqual(['/account']);
  });

  it('goes to the saga list when the navigation was cancelled', async () => {
    navigate.mockResolvedValueOnce(false);

    await leaveTo(router, '/account', () => true);

    expect(urls()).toEqual(['/account', '/sagas']);
  });

  it('goes to the saga list when the navigation fails (a URL that matches no route)', async () => {
    navigate.mockRejectedValueOnce(new Error('NG04002'));

    await leaveTo(router, '/gone', () => true);

    expect(urls()).toEqual(['/gone', '/sagas']);
  });

  it('does not try the saga list twice when it is the destination that fails', async () => {
    navigate.mockResolvedValue(false);

    await leaveTo(router, '/sagas', () => true);

    expect(urls()).toEqual(['/sagas']);
  });

  it('does not override a navigation made meanwhile: the page that asked is gone', async () => {
    navigate.mockResolvedValueOnce(false);

    await leaveTo(router, '/account', () => false);

    expect(urls()).toEqual(['/account']);
  });

  it('never rejects, not even when the saga list cannot be reached either', async () => {
    navigate.mockRejectedValue(new Error('boom'));

    await expect(leaveTo(router, '/account', () => true)).resolves.toBeUndefined();
    expect(urls()).toEqual(['/account', '/sagas']);
  });
});

describe('trackDestroyed', () => {
  it('turns true when the injector it was created in is destroyed, and not before', () => {
    const destroyed = TestBed.runInInjectionContext(() => trackDestroyed());
    expect(destroyed()).toBe(false);

    TestBed.resetTestingModule();

    expect(destroyed()).toBe(true);
  });
});
