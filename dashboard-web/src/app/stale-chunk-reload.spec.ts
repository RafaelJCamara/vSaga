import { TestBed } from '@angular/core/testing';
import { NavigationError } from '@angular/router';
import { Mock, MockInstance, vi } from 'vitest';
import { PAGE_RELOAD } from './services/auth.service';
import {
  STALE_CHUNK_RELOAD_INTERVAL_MS,
  STALE_CHUNK_RELOAD_KEY,
  isStaleChunkError,
  reloadOnceOnStaleChunk,
} from './stale-chunk-reload';

const CHROMIUM = 'Failed to fetch dynamically imported module: http://localhost/chunk-ABC.js';
const FIREFOX = 'error loading dynamically imported module: http://localhost/chunk-ABC.js';
const SAFARI = 'Importing a module script failed.';

describe('stale chunk reload', () => {
  let reload: Mock<() => void>;
  let spies: MockInstance[] = [];

  beforeEach(() => {
    sessionStorage.clear();
    reload = vi.fn();
    TestBed.configureTestingModule({ providers: [{ provide: PAGE_RELOAD, useValue: reload }] });
  });

  afterEach(() => {
    vi.useRealTimers();
    for (const spy of spies) spy.mockRestore();
    spies = [];
    sessionStorage.clear();
  });

  const failure = (error: unknown) => new NavigationError(1, '/login', error);
  const handle = (error: unknown) =>
    TestBed.runInInjectionContext(() => reloadOnceOnStaleChunk(failure(error)));

  describe('isStaleChunkError', () => {
    it.each([CHROMIUM, FIREFOX, SAFARI])('recognises the browser message %s', (message) => {
      expect(isStaleChunkError(new TypeError(message))).toBe(true);
      expect(isStaleChunkError(message)).toBe(true);
    });

    it.each([
      new Error("NG04002: Cannot match any routes. URL Segment: 'nope'"),
      new Error('Cannot read properties of undefined'),
      'something else',
      null,
      undefined,
      { message: CHROMIUM },
    ])('does not recognise %s', (error) => {
      expect(isStaleChunkError(error)).toBe(false);
    });
  });

  it.each([CHROMIUM, FIREFOX, SAFARI])('reloads the page once for %s', (message) => {
    handle(new TypeError(message));

    expect(reload).toHaveBeenCalledTimes(1);
    expect(Number(sessionStorage.getItem(STALE_CHUNK_RELOAD_KEY))).toBeGreaterThan(0);
  });

  it('leaves every other navigation error alone', () => {
    handle(new Error("NG04002: Cannot match any routes. URL Segment: 'nope'"));

    expect(reload).not.toHaveBeenCalled();
    expect(sessionStorage.getItem(STALE_CHUNK_RELOAD_KEY)).toBeNull();
  });

  it('reloads at most once a minute: a chunk that stays missing must not loop', () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-10-03T12:00:00Z'));
    handle(new TypeError(CHROMIUM));
    expect(reload).toHaveBeenCalledTimes(1);

    vi.setSystemTime(Date.now() + STALE_CHUNK_RELOAD_INTERVAL_MS - 1);
    handle(new TypeError(CHROMIUM));
    expect(reload).toHaveBeenCalledTimes(1);

    vi.setSystemTime(Date.now() + 1);
    handle(new TypeError(CHROMIUM));
    expect(reload).toHaveBeenCalledTimes(2);
  });

  it('treats a record from the future (a clock that stepped back) as old', () => {
    sessionStorage.setItem(STALE_CHUNK_RELOAD_KEY, String(Date.now() + 3_600_000));

    handle(new TypeError(CHROMIUM));

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('reloads nothing when the time of the last reload cannot be read', () => {
    spies.push(
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
        throw new DOMException('blocked', 'SecurityError');
      }),
    );

    expect(() => handle(new TypeError(CHROMIUM))).not.toThrow();
    expect(reload).not.toHaveBeenCalled();
  });

  it('reloads nothing when the time cannot be written: without the record it could loop', () => {
    spies.push(
      vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
        throw new DOMException('full', 'QuotaExceededError');
      }),
    );

    expect(() => handle(new TypeError(CHROMIUM))).not.toThrow();
    expect(reload).not.toHaveBeenCalled();
  });

  it('reloads nothing when storage accepts a write and keeps nothing', () => {
    spies.push(vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => undefined));

    handle(new TypeError(CHROMIUM));

    expect(reload).not.toHaveBeenCalled();
  });
});
