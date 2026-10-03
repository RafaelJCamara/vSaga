import { TestBed } from '@angular/core/testing';
import { PermissionKey } from '../models/auth.model';
import { AuthService } from '../services/auth.service';
import { ALL_PERMISSIONS, AuthMock, createAuthMock, provideAuthMock } from './auth-mock';

describe('auth mock', () => {
  it('is signed in and grants every permission for every saga type by default', () => {
    const auth = createAuthMock();

    expect(auth.status()).toBe('authenticated');
    expect(auth.isAuthenticated()).toBe(true);
    expect(auth.user()?.username).toBe('admin');
    for (const permission of ALL_PERMISSIONS) {
      expect(auth.can(permission)).toBe(true);
      expect(auth.can(permission, 'OrderSaga')).toBe(true);
      expect(auth.canAny(permission)).toBe(true);
    }
    expect(auth.canManageAccess()).toBe(true);
    expect(auth.passwordMinLength()).toBe(12);
  });

  it('answers can() by the rule of the real service, so a narrowed access behaves like production', () => {
    const auth = createAuthMock({
      access: {
        permissions: ['sagas.view'],
        scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.retry'] }],
      },
    });

    expect(auth.can('sagas.view', 'Anything')).toBe(true);
    expect(auth.can('sagas.retry', 'OrderSaga')).toBe(true);
    expect(auth.can('sagas.retry', 'ordersaga')).toBe(false);
    expect(auth.can('sagas.retry')).toBe(false);
    expect(auth.canAny('sagas.retry')).toBe(true);
    expect(auth.canManageAccess()).toBe(false);
  });

  it('holds nothing unless authenticated, like the real service, whatever access it was given', () => {
    for (const status of ['anonymous', 'unknown', 'unreachable'] as const) {
      const auth = createAuthMock({ status });

      expect(auth.access()).not.toBeNull();
      expect(auth.can('sagas.view')).toBe(false);
      expect(auth.can('sagas.view', 'OrderSaga')).toBe(false);
      expect(auth.canAny('sagas.view')).toBe(false);
      expect(auth.canManageAccess()).toBe(false);
    }

    const auth = createAuthMock({ status: 'anonymous' });
    auth.status.set('authenticated');
    expect(auth.can('sagas.retry')).toBe(true);
    expect(auth.canManageAccess()).toBe(true);
  });

  it('lists every permission the type has, so the default mock is an administrator in full', () => {
    expect(new Set(ALL_PERMISSIONS)).toEqual(
      new Set<PermissionKey>(['sagas.view', 'sagas.data', 'sagas.retry', 'access.manage']),
    );
    expect(ALL_PERMISSIONS).toHaveLength(4);
  });

  it('follows the writable signals: a spec can change the session after a component exists', () => {
    const auth = createAuthMock();

    auth.access.set({ permissions: ['sagas.view'], scoped: [] });
    expect(auth.can('sagas.retry')).toBe(false);
    expect(auth.canManageAccess()).toBe(false);

    auth.status.set('anonymous');
    auth.access.set(null);
    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.can('sagas.view')).toBe(false);
  });

  it('takes the other state as options', () => {
    const auth = createAuthMock({
      status: 'unreachable',
      user: null,
      access: null,
      setupRequired: true,
      setupAvailable: false,
      setupProblem: { code: 'setup_unavailable', detail: 'Seed keys are set.' },
      passwordMinLength: null,
      signInUnavailable: true,
    });

    expect(auth.status()).toBe('unreachable');
    expect(auth.user()).toBeNull();
    expect(auth.setupRequired()).toBe(true);
    expect(auth.setupProblem()?.code).toBe('setup_unavailable');
    expect(auth.passwordMinLength()).toBeNull();
    expect(auth.signInUnavailable()).toBe(true);
  });

  it('has methods that resolve, record their calls and can be reprogrammed', async () => {
    const auth = createAuthMock({ status: 'anonymous' });

    await expect(auth.login('alice', 'pw')).resolves.toBeUndefined();
    expect(auth.login).toHaveBeenCalledWith('alice', 'pw');
    await expect(auth.refresh()).resolves.toBe('anonymous');
    expect(auth.currentIdentityEpoch()).toBe(0);
    auth.handleUnauthorized(auth.currentIdentityEpoch());
    expect(auth.handleUnauthorized).toHaveBeenCalledWith(0);

    auth.login.mockRejectedValueOnce(new Error('refused'));
    await expect(auth.login('alice', 'wrong')).rejects.toThrow('refused');
  });

  it('leaves the session as it is when a flow resolves: a spec that needs the outcome sets it', async () => {
    const auth = createAuthMock({ status: 'anonymous', setupRequired: true });

    await auth.login('alice', 'pw');
    await auth.setup({ username: 'root', displayName: 'Root', password: 'pw', code: 'code' });
    expect(auth.status()).toBe('anonymous');

    auth.setup.mockImplementationOnce(async () => auth.status.set('authenticated'));
    await auth.setup({ username: 'root', displayName: 'Root', password: 'pw', code: 'code' });
    expect(auth.isAuthenticated()).toBe(true);
  });

  it('is provided as AuthService, from options or from a mock built first', () => {
    TestBed.configureTestingModule({
      providers: [provideAuthMock({ access: { permissions: ['sagas.view'], scoped: [] } })],
    });
    const injected = TestBed.inject(AuthService);
    expect(injected.can('sagas.view')).toBe(true);
    expect(injected.can('sagas.retry')).toBe(false);
    TestBed.resetTestingModule();

    const mine: AuthMock = createAuthMock();
    TestBed.configureTestingModule({ providers: [provideAuthMock(mine)] });
    expect(TestBed.inject(AuthService)).toBe(mine);
  });
});
