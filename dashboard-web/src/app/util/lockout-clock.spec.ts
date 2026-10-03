import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { lockoutClock } from './lockout-clock';

describe('lockoutClock', () => {
  const START = Date.parse('2026-10-03T12:00:00Z');

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
    vi.setSystemTime(START);
  });

  afterEach(() => vi.useRealTimers());

  /** The clock over `users`, with its effect running (an effect runs when the app does). */
  function clock(users: () => { lockedUntilUtc: string | null }[]) {
    const now = TestBed.runInInjectionContext(() => lockoutClock(users));
    TestBed.tick();
    return now;
  }

  const at = (minutes: number) => new Date(START + minutes * 60_000).toISOString();

  it('starts at the current time', () => {
    const now = clock(() => []);

    expect(now()).toBe(START);
  });

  it('sets no timer while nobody is locked, or while every lockout has ended', () => {
    clock(() => [{ lockedUntilUtc: null }, { lockedUntilUtc: at(-5) }, { lockedUntilUtc: 'soon' }]);

    expect(vi.getTimerCount()).toBe(0);
  });

  it('moves the time when the lockout ends, and not before', async () => {
    const now = clock(() => [{ lockedUntilUtc: at(15) }]);
    expect(vi.getTimerCount()).toBe(1);

    await vi.advanceTimersByTimeAsync(15 * 60_000 - 1);
    TestBed.tick();
    expect(now()).toBe(START);

    await vi.advanceTimersByTimeAsync(2);
    TestBed.tick();
    expect(now()).toBeGreaterThan(START + 15 * 60_000);
    // Nothing is locked now: nothing is waited for.
    expect(vi.getTimerCount()).toBe(0);
  });

  it('waits for the next end after one has passed, one timer at a time', async () => {
    const now = clock(() => [{ lockedUntilUtc: at(10) }, { lockedUntilUtc: at(5) }]);

    await vi.advanceTimersByTimeAsync(5 * 60_000 + 10);
    TestBed.tick();
    const afterFirst = now();
    expect(afterFirst).toBeGreaterThan(START + 5 * 60_000);
    expect(vi.getTimerCount()).toBe(1);

    await vi.advanceTimersByTimeAsync(5 * 60_000);
    TestBed.tick();
    expect(now()).toBeGreaterThan(START + 10 * 60_000);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('follows the list: a lockout that appears is waited for, one that is gone is not', () => {
    const users = signal<{ lockedUntilUtc: string | null }[]>([]);
    clock(() => users());
    expect(vi.getTimerCount()).toBe(0);

    users.set([{ lockedUntilUtc: at(30) }]);
    TestBed.tick();
    expect(vi.getTimerCount()).toBe(1);

    users.set([]);
    TestBed.tick();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('measures a lockout against the clock when the list changes, not against the time it last moved', async () => {
    const users = signal<{ lockedUntilUtc: string | null }[]>([]);
    const now = clock(() => users());
    // The page stays open for an hour with nothing locked; then a lockout of 10 minutes (from the start) is read.
    await vi.advanceTimersByTimeAsync(60 * 60_000);

    users.set([{ lockedUntilUtc: at(10) }, { lockedUntilUtc: at(75) }]);
    TestBed.tick();

    // Only the one still ahead of the real time is waited for, and for the right while: the one that ended while
    // nobody was looking is not a reason to move the time, and the other is not waited for too early.
    expect(vi.getTimerCount()).toBe(1);
    await vi.advanceTimersByTimeAsync(14 * 60_000);
    TestBed.tick();
    expect(now()).toBe(START);

    await vi.advanceTimersByTimeAsync(60_000 + 10);
    TestBed.tick();
    expect(now()).toBeGreaterThan(START + 75 * 60_000);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('never asks a timer for more than it can take', () => {
    const spy = vi.spyOn(globalThis, 'setTimeout');
    clock(() => [{ lockedUntilUtc: new Date(START + 400 * 24 * 3600_000).toISOString() }]);

    const delay = spy.mock.calls.at(-1)?.[1] as number;
    expect(delay).toBeLessThanOrEqual(2 ** 31 - 1);
    expect(delay).toBeGreaterThan(0);
  });
});
