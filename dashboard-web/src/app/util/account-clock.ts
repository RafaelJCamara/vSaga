import { Signal, effect, signal } from '@angular/core';
import { toMillis } from './time-format';

/** The longest delay a timer takes (a larger one fires at once). */
const MAX_TIMER_MS = 2 ** 31 - 1;

/**
 * The current time as a signal that moves when a lockout in `users` ends or a temporary password in `users`
 * expires, so that what a page computes from either (a "Locked" chip, an Unlock button, a "Password expired" chip)
 * follows the clock, not only the data: a page can stay open longer than a lockout or a password lasts. Between two
 * such moments nothing runs. One timer is set, for the next one ahead; it is set again when the list changes or the
 * timer fires, and it is cleared when the page is destroyed. Call it in an injection context (a field initializer).
 */
export function accountClock(
  users: () => readonly { lockedUntilUtc: string | null; passwordExpiresAtUtc?: string | null }[],
): Signal<number> {
  const now = signal(Date.now());
  effect((onCleanup) => {
    now(); // the timer firing makes this run again, for the next moment
    const current = Date.now();
    let next = Infinity;
    for (const user of users()) {
      for (const iso of [user.lockedUntilUtc, user.passwordExpiresAtUtc]) {
        const at = toMillis(iso);
        if (at !== null && at > current && at < next) next = at;
      }
    }
    if (next === Infinity) return;
    const timer = setTimeout(() => now.set(Date.now()), Math.min(next - current + 1, MAX_TIMER_MS));
    onCleanup(() => clearTimeout(timer));
  });
  return now.asReadonly();
}
