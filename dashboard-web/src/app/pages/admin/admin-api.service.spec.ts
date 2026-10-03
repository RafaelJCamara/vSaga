import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Observable } from 'rxjs';
import conflictProblem from '../../testing/contracts/admin/conflict-problem.response.json';
import createUserRequest from '../../testing/contracts/admin/create-user.request.json';
import permissionsResponse from '../../testing/contracts/admin/permissions.response.json';
import resetPasswordRequest from '../../testing/contracts/admin/reset-password.request.json';
import roleRequest from '../../testing/contracts/admin/role.request.json';
import roleResponse from '../../testing/contracts/admin/role.response.json';
import teamRequest from '../../testing/contracts/admin/team.request.json';
import teamResponse from '../../testing/contracts/admin/team.response.json';
import updateUserRequest from '../../testing/contracts/admin/update-user.request.json';
import userResponse from '../../testing/contracts/admin/user.response.json';
import validationProblem from '../../testing/contracts/admin/validation-problem.response.json';
import { adminFailure } from '../../util/admin-failure';
import { problemOf } from '../../util/http-error';
import { AdminApiService } from './admin-api.service';
import {
  AdminUser,
  CreateUser,
  Grant,
  PermissionInfo,
  ResetPassword,
  Role,
  SaveRole,
  SaveTeam,
  Team,
  UpdateUser,
} from './admin.model';

/**
 * The service against the golden fixtures in `src/app/testing/contracts/admin/`, the same files the .NET
 * endpoint tests assert (`AdminApi.cs` in `VSaga.Dashboard.Api.Tests`). A request fixture is the body the API
 * accepts: what the service puts on the wire must equal it, deeply. The service passes a body through as it is, so
 * this pins the models and the routes; that the pages build such bodies is asserted by their own specs
 * (`role-edit.spec.ts` builds `role.request.json` from a form). A response fixture is what the API answers: it must
 * parse into the models, which the compiler checks (the assignments below) and the key lists confirm in
 * both directions (a model with a member the API never sends, or without one it does, fails).
 *
 * Each model's members are listed once, as a record keyed by `keyof Model`, so that adding a member to a
 * model without adding it here fails the build, and a member in a fixture that is not in the model fails the
 * spec.
 */
const GRANT_KEYS: Record<keyof Grant, true> = { roleId: true, allSagaTypes: true, sagaTypes: true };
const USER_KEYS: Record<keyof AdminUser, true> = {
  id: true,
  username: true,
  displayName: true,
  isEnabled: true,
  mustChangePassword: true,
  lockedUntilUtc: true,
  lastSignInAtUtc: true,
  createdAtUtc: true,
  grants: true,
  teamIds: true,
};
const TEAM_KEYS: Record<keyof Team, true> = {
  id: true,
  name: true,
  description: true,
  memberIds: true,
  grants: true,
};
const ROLE_KEYS: Record<keyof Role, true> = {
  id: true,
  name: true,
  description: true,
  isBuiltIn: true,
  permissions: true,
};
const PERMISSION_KEYS: Record<keyof PermissionInfo, true> = {
  key: true,
  name: true,
  description: true,
  scopable: true,
  implies: true,
};
const CREATE_USER_KEYS: Record<keyof CreateUser, true> = {
  username: true,
  displayName: true,
  password: true,
  mustChangePassword: true,
  grants: true,
};
const UPDATE_USER_KEYS: Record<keyof UpdateUser, true> = {
  displayName: true,
  isEnabled: true,
  grants: true,
};
const RESET_PASSWORD_KEYS: Record<keyof ResetPassword, true> = {
  newPassword: true,
  mustChangePassword: true,
};
const SAVE_TEAM_KEYS: Record<keyof SaveTeam, true> = {
  name: true,
  description: true,
  memberIds: true,
  grants: true,
};
const SAVE_ROLE_KEYS: Record<keyof SaveRole, true> = {
  name: true,
  description: true,
  permissions: true,
};

const keysOf = (value: object) => Object.keys(value).sort();
const modelKeys = (model: Record<string, true>) => Object.keys(model).sort();

const ID = userResponse.id;
const BASE = '/api/admin';

describe('AdminApiService', () => {
  let service: AdminApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AdminApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Runs a call, answers its one request with `body`, and returns what was sent and what came back. */
  function exchange<T>(
    call: Observable<T>,
    method: string,
    url: string,
    body: object | null = null,
  ): { sent: unknown; received: T | undefined } {
    let received: T | undefined;
    call.subscribe((value) => (received = value));
    const req = http.expectOne(url);
    expect(req.request.method).toBe(method);
    const sent = req.request.body;
    req.flush(body);
    return { sent, received };
  }

  /** The error a call fails with when its one request is refused. */
  function refusal(call: Observable<unknown>, url: string, status: number, body: object) {
    let error: HttpErrorResponse | undefined;
    call.subscribe({ error: (e: HttpErrorResponse) => (error = e) });
    http.expectOne(url).flush(body, { status, statusText: 'Refused' });
    return error;
  }

  describe('the request fixtures', () => {
    it('create user puts create-user.request.json on the wire unchanged', () => {
      const typed: CreateUser = createUserRequest;
      const { sent } = exchange(service.createUser(typed), 'POST', `${BASE}/users`, userResponse);

      expect(sent).toEqual(createUserRequest);
      expect(keysOf(createUserRequest)).toEqual(modelKeys(CREATE_USER_KEYS));
    });

    it('update user puts update-user.request.json on the wire unchanged, with neither teamIds nor enabled', () => {
      const typed: UpdateUser = updateUserRequest;
      const { sent } = exchange(
        service.updateUser(ID, typed),
        'PUT',
        `${BASE}/users/${ID}`,
        userResponse,
      );

      expect(sent).toEqual(updateUserRequest);
      expect(keysOf(updateUserRequest)).toEqual(modelKeys(UPDATE_USER_KEYS));
      // Membership is written through the team, and the member is isEnabled: the API refuses both of these.
      expect(Object.keys(sent as object)).not.toContain('teamIds');
      expect(Object.keys(sent as object)).not.toContain('enabled');
    });

    it('reset password puts reset-password.request.json on the wire unchanged', () => {
      const typed: ResetPassword = resetPasswordRequest;
      const { sent } = exchange(
        service.resetPassword(ID, typed),
        'POST',
        `${BASE}/users/${ID}/password`,
        userResponse,
      );

      expect(sent).toEqual(resetPasswordRequest);
      expect(keysOf(resetPasswordRequest)).toEqual(modelKeys(RESET_PASSWORD_KEYS));
    });

    it('create and update team put team.request.json on the wire unchanged', () => {
      const typed: SaveTeam = teamRequest;
      const created = exchange(service.createTeam(typed), 'POST', `${BASE}/teams`, teamResponse);
      const updated = exchange(
        service.updateTeam(teamResponse.id, typed),
        'PUT',
        `${BASE}/teams/${teamResponse.id}`,
        teamResponse,
      );

      expect(created.sent).toEqual(teamRequest);
      expect(updated.sent).toEqual(teamRequest);
      expect(keysOf(teamRequest)).toEqual(modelKeys(SAVE_TEAM_KEYS));
    });

    it('create and update role put role.request.json on the wire unchanged', () => {
      const typed: SaveRole = roleRequest;
      const created = exchange(service.createRole(typed), 'POST', `${BASE}/roles`, roleResponse);
      const updated = exchange(
        service.updateRole(roleResponse.id, typed),
        'PUT',
        `${BASE}/roles/${roleResponse.id}`,
        roleResponse,
      );

      expect(created.sent).toEqual(roleRequest);
      expect(updated.sent).toEqual(roleRequest);
      expect(keysOf(roleRequest)).toEqual(modelKeys(SAVE_ROLE_KEYS));
    });

    it('every grant in a request names exactly the members of a grant', () => {
      const grants = [
        ...createUserRequest.grants,
        ...updateUserRequest.grants,
        ...teamRequest.grants,
      ];
      for (const grant of grants) {
        expect(keysOf(grant)).toEqual(modelKeys(GRANT_KEYS));
      }
    });
  });

  describe('the response fixtures', () => {
    it('user.response.json parses into AdminUser, whole', () => {
      const user: AdminUser = userResponse;
      const { received } = exchange(
        service.getUser(ID),
        'GET',
        `${BASE}/users/${ID}`,
        userResponse,
      );

      expect(received).toEqual(userResponse);
      expect(keysOf(user)).toEqual(modelKeys(USER_KEYS));
      // The wire names, and never the pre-contract ones the first blueprint used.
      expect(user.isEnabled).toBe(true);
      expect(user.lastSignInAtUtc).toBeNull();
      expect(user.teamIds).toEqual([]);
      for (const grant of user.grants) expect(keysOf(grant)).toEqual(modelKeys(GRANT_KEYS));
    });

    it('every list answer is an array of the same records', () => {
      const users = exchange(service.listUsers(), 'GET', `${BASE}/users`, [userResponse]);
      const teams = exchange(service.listTeams(), 'GET', `${BASE}/teams`, [teamResponse]);
      const roles = exchange(service.listRoles(), 'GET', `${BASE}/roles`, [roleResponse]);

      expect(users.received).toEqual([userResponse]);
      expect(teams.received).toEqual([teamResponse]);
      expect(roles.received).toEqual([roleResponse]);
    });

    it('team.response.json parses into Team, whole', () => {
      const team: Team = teamResponse;
      const { received } = exchange(
        service.getTeam(team.id),
        'GET',
        `${BASE}/teams/${team.id}`,
        teamResponse,
      );

      expect(received).toEqual(teamResponse);
      expect(keysOf(team)).toEqual(modelKeys(TEAM_KEYS));
      for (const grant of team.grants) expect(keysOf(grant)).toEqual(modelKeys(GRANT_KEYS));
    });

    it('role.response.json parses into Role, whole', () => {
      const role: Role = roleResponse;
      const { received } = exchange(
        service.getRole(role.id),
        'GET',
        `${BASE}/roles/${role.id}`,
        roleResponse,
      );

      expect(received).toEqual(roleResponse);
      expect(keysOf(role)).toEqual(modelKeys(ROLE_KEYS));
      expect(role.isBuiltIn).toBe(false);
    });

    it('permissions.response.json parses into the catalogue', () => {
      const catalogue: PermissionInfo[] = permissionsResponse;
      const { received } = exchange(
        service.listPermissions(),
        'GET',
        `${BASE}/permissions`,
        permissionsResponse,
      );

      expect(received).toEqual(permissionsResponse);
      for (const entry of catalogue) expect(keysOf(entry)).toEqual(modelKeys(PERMISSION_KEYS));
      expect(catalogue.map((p) => p.key)).toEqual([
        'sagas.view',
        'sagas.data',
        'sagas.retry',
        'access.manage',
      ]);
    });
  });

  describe('the problem fixtures', () => {
    it('validation-problem.response.json is read as field errors keyed by request path', () => {
      const error = refusal(
        service.createUser(createUserRequest),
        `${BASE}/users`,
        400,
        validationProblem,
      );

      const problem = problemOf(error, 'x');
      expect(problem.status).toBe(400);
      expect(problem.code).toBe('validation');
      expect(problem.message).toBe('The request is not valid.');
      expect(problem.fieldErrors).toEqual(validationProblem.errors);
      expect(Object.keys(problem.fieldErrors)).toEqual(['grants[0].sagaTypes', 'grants[1].roleId']);

      const failure = adminFailure(error, 'x');
      expect(failure.kind).toBe('validation');
      expect(failure.fieldErrors).toEqual(validationProblem.errors);
    });

    it('conflict-problem.response.json is read as the last administrator', () => {
      const error = refusal(service.deleteRole(ID), `${BASE}/roles/${ID}`, 409, conflictProblem);

      expect(problemOf(error, 'x').code).toBe('last_administrator');
      const failure = adminFailure(error, 'x');
      expect(failure.kind).toBe('last_administrator');
      expect(failure.message).toContain(conflictProblem.detail);
      expect(failure.message).toContain('Give another enabled user an all-saga-types grant');
    });
  });

  describe('the routes', () => {
    const table: Array<[string, () => Observable<unknown>, string, string]> = [
      ['listPermissions', () => service.listPermissions(), 'GET', `${BASE}/permissions`],
      ['listUsers', () => service.listUsers(), 'GET', `${BASE}/users`],
      ['getUser', () => service.getUser(ID), 'GET', `${BASE}/users/${ID}`],
      ['deleteUser', () => service.deleteUser(ID), 'DELETE', `${BASE}/users/${ID}`],
      ['unlockUser', () => service.unlockUser(ID), 'POST', `${BASE}/users/${ID}/unlock`],
      ['listTeams', () => service.listTeams(), 'GET', `${BASE}/teams`],
      ['getTeam', () => service.getTeam(ID), 'GET', `${BASE}/teams/${ID}`],
      ['deleteTeam', () => service.deleteTeam(ID), 'DELETE', `${BASE}/teams/${ID}`],
      ['listRoles', () => service.listRoles(), 'GET', `${BASE}/roles`],
      ['getRole', () => service.getRole(ID), 'GET', `${BASE}/roles/${ID}`],
      ['deleteRole', () => service.deleteRole(ID), 'DELETE', `${BASE}/roles/${ID}`],
    ];
    it.each(table)('%s: %s %s', (_name, call, method, url) => {
      exchange(call(), method, url);
    });

    it('answers a delete (204) with an Observable that completes', () => {
      let completed = false;
      service.deleteRole(ID).subscribe({ complete: () => (completed = true) });
      http.expectOne(`${BASE}/roles/${ID}`).flush(null, { status: 204, statusText: 'No Content' });

      expect(completed).toBe(true);
    });

    it('writes an id into the path as one segment', () => {
      exchange(service.getRole('a/b c'), 'GET', `${BASE}/roles/a%2Fb%20c`);
    });
  });
});
