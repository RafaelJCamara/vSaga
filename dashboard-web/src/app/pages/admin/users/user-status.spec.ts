import { adminUser } from '../../../testing/admin';
import { userStatus } from './user-status';

describe('userStatus', () => {
  const NOW = Date.parse('2026-10-03T12:00:00Z');

  it('has nothing the matter with an enabled, unlocked user who has a password', () => {
    expect(userStatus(adminUser(), NOW)).toEqual({
      disabled: false,
      locked: false,
      mustChangePassword: false,
      passwordExpired: false,
    });
  });

  it('reads isEnabled and mustChangePassword as they are, each on its own', () => {
    expect(userStatus(adminUser({ isEnabled: false }), NOW)).toMatchObject({
      disabled: true,
      locked: false,
      mustChangePassword: false,
    });
    expect(userStatus(adminUser({ mustChangePassword: true }), NOW)).toMatchObject({
      disabled: false,
      mustChangePassword: true,
    });
  });

  it('counts a lockout while its end is ahead, and not after it, or at the instant it ends', () => {
    const lockedAt = (iso: string | null) =>
      userStatus(adminUser({ lockedUntilUtc: iso }), NOW).locked;

    expect(lockedAt('2026-10-03T12:00:01Z')).toBe(true);
    expect(lockedAt('2026-10-03T12:00:00Z')).toBe(false);
    expect(lockedAt('2026-10-03T11:59:59Z')).toBe(false);
    expect(lockedAt(null)).toBe(false);
  });

  it('counts a temporary password as expired from the instant it expires, while the user still has to change it', () => {
    const expiredAt = (iso: string | null, mustChangePassword = true) =>
      userStatus(adminUser({ mustChangePassword, passwordExpiresAtUtc: iso }), NOW).passwordExpired;

    expect(expiredAt('2026-10-03T12:00:01Z')).toBe(false);
    expect(expiredAt('2026-10-03T12:00:00Z')).toBe(true);
    expect(expiredAt('2026-10-03T11:59:59Z')).toBe(true);
    expect(expiredAt(null)).toBe(false);
    expect(expiredAt('soon')).toBe(false);
    // A password the user keeps has nothing to expire, whatever the field says.
    expect(expiredAt('2026-10-03T11:59:59Z', false)).toBe(false);
  });

  it('reads the seven fractional digits .NET writes', () => {
    const user = adminUser({ lockedUntilUtc: '2026-10-03T12:15:00.0000000+00:00' });

    expect(userStatus(user, NOW).locked).toBe(true);
  });

  it('does not count a lockout end it cannot read', () => {
    expect(userStatus(adminUser({ lockedUntilUtc: 'soon' }), NOW).locked).toBe(false);
  });
});
