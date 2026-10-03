/**
 * A stand-in for `AuthService` in component specs. Test-only: tsconfig.app.json excludes this folder from
 * the app build (and .dockerignore from the image), so app code must never import it.
 *
 * The default is the session most specs want: signed in, holding every permission for every saga type.
 * `can`, `canAny` and `canManageAccess` answer through the same pure functions as the real service
 * (`util/session-access.ts`), so a spec that narrows `access` sees the rule production applies:
 *
 *     createAuthMock({ access: { permissions: ['sagas.view'], scoped: [] } })      // no retry anywhere
 *     createAuthMock({ access: { permissions: ['sagas.view'],
 *                                scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.retry'] }] } })
 *
 * Every state is a writable signal, so a spec can change the session after the component exists, and every
 * method is a `vi.fn()`: assert on it, or reprogram it (`mock.login.mockRejectedValue(err)`).
 */

import { Provider, computed, signal } from '@angular/core';
import { vi } from 'vitest';
import {
  PermissionKey,
  SessionAccess,
  SessionStatus,
  SessionUser,
  SetupProblem,
} from '../models/auth.model';
import { AuthService } from '../services/auth.service';
import { holds, holdsAccessManage, holdsAny } from '../util/session-access';

/** The permissions a role can combine: what the default mock holds, for every saga type. */
export const ALL_PERMISSIONS: readonly PermissionKey[] = [
  'sagas.view',
  'sagas.data',
  'sagas.retry',
  'access.manage',
];

export interface AuthMockOptions {
  /** Default `authenticated`. */
  status?: SessionStatus;
  /** Default: an administrator. */
  user?: SessionUser | null;
  /** Default: every permission, for every saga type. */
  access?: SessionAccess | null;
  setupRequired?: boolean;
  setupAvailable?: boolean;
  setupProblem?: SetupProblem | null;
  /** Default 12, the API's default. */
  passwordMinLength?: number | null;
  signInUnavailable?: boolean;
}

export function createAuthMock(options: AuthMockOptions = {}) {
  const status = signal<SessionStatus>(options.status ?? 'authenticated');
  const user = signal<SessionUser | null>(
    options.user !== undefined
      ? options.user
      : {
          id: '00000000-0000-0000-0000-000000000001',
          username: 'admin',
          displayName: 'Administrator',
          mustChangePassword: false,
        },
  );
  const access = signal<SessionAccess | null>(
    options.access !== undefined
      ? options.access
      : { permissions: [...ALL_PERMISSIONS], scoped: [] },
  );

  // `satisfies` ties the mock to the service: a public member added to AuthService and missing here, or
  // typed differently, fails the spec build instead of surfacing as `undefined is not a function` at run time.
  return {
    status,
    isAuthenticated: computed(() => status() === 'authenticated'),
    user,
    access,
    setupRequired: signal(options.setupRequired ?? false),
    setupAvailable: signal(options.setupAvailable ?? false),
    setupProblem: signal<SetupProblem | null>(options.setupProblem ?? null),
    passwordMinLength: signal<number | null>(
      options.passwordMinLength !== undefined ? options.passwordMinLength : 12,
    ),
    canManageAccess: computed(() => holdsAccessManage(access())),
    signInUnavailable: signal(options.signInUnavailable ?? false),

    bootstrap: vi.fn<AuthService['bootstrap']>(() => Promise.resolve()),
    refresh: vi.fn<AuthService['refresh']>(() => Promise.resolve(status())),
    login: vi.fn<AuthService['login']>(() => Promise.resolve()),
    logout: vi.fn<AuthService['logout']>(() => Promise.resolve()),
    setup: vi.fn<AuthService['setup']>(() => Promise.resolve()),
    changePassword: vi.fn<AuthService['changePassword']>(() => Promise.resolve()),
    handleUnauthorized: vi.fn<AuthService['handleUnauthorized']>(),
    noteForbidden: vi.fn<AuthService['noteForbidden']>(),
    can: vi.fn<AuthService['can']>((permission, sagaType) => holds(access(), permission, sagaType)),
    canAny: vi.fn<AuthService['canAny']>((permission) => holdsAny(access(), permission)),
  } satisfies { [K in keyof AuthService]: AuthService[K] };
}

export type AuthMock = ReturnType<typeof createAuthMock>;

/** Provides the mock as `AuthService`. Reach it in a spec with `TestBed.inject(AuthService) as unknown as AuthMock`,
 *  or build it first and pass it in to keep a typed handle. */
export function provideAuthMock(options: AuthMockOptions | AuthMock = {}): Provider {
  const mock = 'login' in options ? options : createAuthMock(options);
  return { provide: AuthService, useValue: mock };
}
