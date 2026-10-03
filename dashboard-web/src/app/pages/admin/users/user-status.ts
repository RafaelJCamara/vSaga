import { toMillis } from '../../../util/time-format';
import { AdminUser } from '../admin.model';

/** What is the matter with an account, as the lists and the user page show it (a chip each). */
export interface UserStatus {
  /** `isEnabled` is false: the user cannot sign in and holds no access. */
  disabled: boolean;
  /** A lockout is in force: too many wrong passwords. Unlock ends it. */
  locked: boolean;
  /** The user must choose a new password at next sign-in, and holds no access until then. */
  mustChangePassword: boolean;
}

/**
 * The status of `user` at `now`. A lockout counts while its end is ahead: the API sends `lockedUntilUtc` only
 * for one in force when it answers, but a page can be open longer than a lockout lasts. Pure.
 */
export function userStatus(user: AdminUser, now: number = Date.now()): UserStatus {
  const until = toMillis(user.lockedUntilUtc);
  return {
    disabled: !user.isEnabled,
    locked: until !== null && until > now,
    mustChangePassword: user.mustChangePassword,
  };
}
