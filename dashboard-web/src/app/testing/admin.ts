/**
 * What the administration specs share: records as the API sends them, and the answers to the reads the
 * store makes. Test-only, like the rest of this folder (tsconfig.app.json excludes it from the app build).
 *
 * The built-in roles carry the ids and permissions the API gives them (`BuiltInRoles` in the Identity
 * project), and the catalogue is the golden `permissions.response.json`, so a spec built on these meets the
 * data the real API sends.
 */

import { HttpTestingController } from '@angular/common/http/testing';
import { AdminUser, Grant, PermissionInfo, Role, Team } from '../pages/admin/admin.model';
import permissionsFixture from './contracts/admin/permissions.response.json';

export const ADMINISTRATOR_ID = 'a0000000-0000-0000-0000-000000000001';
export const OPERATOR_ID = 'a0000000-0000-0000-0000-000000000002';
export const VIEWER_ID = 'a0000000-0000-0000-0000-000000000003';

export const PERMISSIONS: PermissionInfo[] = permissionsFixture;

export const BUILT_IN_ROLES: Role[] = [
  {
    id: ADMINISTRATOR_ID,
    name: 'Administrator',
    description: 'Everything: view sagas and their data, retry, and manage users, teams and roles.',
    isBuiltIn: true,
    permissions: ['sagas.view', 'sagas.data', 'sagas.retry', 'access.manage'],
  },
  {
    id: OPERATOR_ID,
    name: 'Operator',
    description: 'View sagas and their data, and retry failed sagas.',
    isBuiltIn: true,
    permissions: ['sagas.view', 'sagas.data', 'sagas.retry'],
  },
  {
    id: VIEWER_ID,
    name: 'Viewer',
    description: 'View sagas and their data.',
    isBuiltIn: true,
    permissions: ['sagas.view', 'sagas.data'],
  },
];

export function role(overrides: Partial<Role> = {}): Role {
  return {
    id: 'e4a7c1b9-2d3f-4e5a-8b6c-7d8e9f0a1b2c',
    name: 'Support',
    description: 'Views and retries sagas without seeing their data.',
    isBuiltIn: false,
    permissions: ['sagas.view', 'sagas.retry'],
    ...overrides,
  };
}

export function grant(roleId: string, sagaTypes: string[] | null = null): Grant {
  return { roleId, allSagaTypes: sagaTypes === null, sagaTypes: sagaTypes ?? [] };
}

export function adminUser(overrides: Partial<AdminUser> = {}): AdminUser {
  return {
    id: '3d9f6c1e-8a2b-4c7d-9e0f-1a2b3c4d5e6f',
    username: 'alice',
    displayName: 'Alice Example',
    isEnabled: true,
    mustChangePassword: false,
    lockedUntilUtc: null,
    lastSignInAtUtc: null,
    createdAtUtc: '2026-10-03T09:15:00+00:00',
    grants: [],
    teamIds: [],
    ...overrides,
  };
}

export function team(overrides: Partial<Team> = {}): Team {
  return {
    id: 'b8e1d2c3-4f5a-4b6c-8d7e-9f0a1b2c3d4e',
    name: 'Payments',
    description: 'The payments on-call rota.',
    memberIds: [],
    grants: [],
    ...overrides,
  };
}

/** Everything the store reads. */
export interface AdminData {
  permissions: PermissionInfo[];
  users: AdminUser[];
  teams: Team[];
  roles: Role[];
  /** What `/api/saga-types` answers. */
  sagaTypes: { sagaType: string; kind: string }[];
}

/** A small, typical set: the three built-in roles and one custom one, an administrator and a user. */
export function adminData(overrides: Partial<AdminData> = {}): AdminData {
  return {
    permissions: PERMISSIONS,
    users: [
      adminUser({
        id: 'u-admin',
        username: 'admin',
        displayName: 'Administrator',
        grants: [grant(ADMINISTRATOR_ID)],
      }),
      adminUser({ id: 'u-alice', grants: [grant(OPERATOR_ID, ['OrderSaga'])] }),
    ],
    teams: [team({ memberIds: ['u-alice'], grants: [grant(VIEWER_ID)] })],
    roles: [...BUILT_IN_ROLES, role()],
    sagaTypes: [
      { sagaType: 'PaymentSaga', kind: 'Orchestrated' },
      { sagaType: 'OrderSaga', kind: 'Orchestrated' },
    ],
    ...overrides,
  };
}

/** Answers the five reads of `AdminStore.load()`. `sagaTypes: 403` refuses the saga types, as the API does for a manager who cannot view sagas. */
export function answerLoad(
  http: HttpTestingController,
  data: AdminData = adminData(),
  sagaTypes: 'ok' | 403 = 'ok',
): void {
  http.expectOne('/api/admin/permissions').flush(data.permissions);
  http.expectOne('/api/admin/users').flush(data.users);
  http.expectOne('/api/admin/teams').flush(data.teams);
  http.expectOne('/api/admin/roles').flush(data.roles);
  const types = http.expectOne('/api/saga-types');
  if (sagaTypes === 403)
    types.flush({ code: 'forbidden' }, { status: 403, statusText: 'Forbidden' });
  else types.flush(data.sagaTypes);
}

/** Answers the three reads that follow every change. */
export function answerReload(http: HttpTestingController, data: AdminData = adminData()): void {
  http.expectOne('/api/admin/users').flush(data.users);
  http.expectOne('/api/admin/teams').flush(data.teams);
  http.expectOne('/api/admin/roles').flush(data.roles);
}
