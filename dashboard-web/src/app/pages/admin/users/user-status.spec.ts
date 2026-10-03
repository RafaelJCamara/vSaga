import { adminUser } from '../../../testing/admin';
import { userStatus } from './user-status';

describe('userStatus', () => {
  const NOW = Date.parse('2026-10-03T12:00:00Z');

  it('has nothing the matter with an enabled, unlocked user who has a password', () => {
    expect(userStatus(adminUser(), NOW)).toEqual({
      disabled: false,
      locked: false,
      mustChangePassword: false,
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

  it('reads the seven fractional digits .NET writes', () => {
    const user = adminUser({ lockedUntilUtc: '2026-10-03T12:15:00.0000000+00:00' });

    expect(userStatus(user, NOW).locked).toBe(true);
  });

  it('does not count a lockout end it cannot read', () => {
    expect(userStatus(adminUser({ lockedUntilUtc: 'soon' }), NOW).locked).toBe(false);
  });
});
