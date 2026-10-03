import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import {
  ADMINISTRATOR_ID,
  AdminData,
  OPERATOR_ID,
  VIEWER_ID,
  adminData,
  adminUser,
  answerLoad,
  answerReload,
  grant,
  role,
  team,
} from '../../testing/admin';
import { adminFailure } from '../../util/admin-failure';
import { SaveRole } from './admin.model';
import { AdminStore, READ_TIMEOUT_MS } from './admin.store';

/** What `fn` rejected with, or undefined when it did not reject. */
async function rejection(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => undefined,
    (err: unknown) => err,
  );
}

/** Refuses the open request for `url` and leaves the rest unanswered: `forkJoin` ends at the first error and cancels them. */
function refuse(http: HttpTestingController, url: string, status: number): void {
  const open = http.match(() => true);
  open
    .find((req) => req.request.url === url)!
    .flush(
      { code: status === 403 ? 'forbidden' : undefined, title: 'Refused' },
      { status, statusText: 'Refused' },
    );
}

/** Fails every open request at the network: the first one ends the read, the others are cancelled. */
function failEverything(http: HttpTestingController): void {
  for (const req of http.match(() => true))
    if (!req.cancelled) req.error(new ProgressEvent('error'));
}

/** Answers every open read of the store (the full set or the lists alone) with `data`. */
function answer(requests: TestRequest[], data: AdminData): void {
  const bodies: Record<string, object> = {
    '/api/admin/permissions': data.permissions,
    '/api/admin/users': data.users,
    '/api/admin/teams': data.teams,
    '/api/admin/roles': data.roles,
    '/api/saga-types': data.sagaTypes,
  };
  for (const req of requests) if (!req.cancelled) req.flush(bodies[req.request.url]);
}

/** Lets the promise chains of the store run: a read starts only after the answer to the change has been seen. */
const turn = () => new Promise<void>((resolve) => setTimeout(resolve));

describe('AdminStore', () => {
  let store: AdminStore;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), AdminStore],
    });
    store = TestBed.inject(AdminStore);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Loads the store with `data` and lets the promises settle. */
  async function loaded(data: AdminData = adminData()): Promise<void> {
    const done = store.load();
    answerLoad(http, data);
    await done;
  }

  describe('load', () => {
    it('starts empty and not loaded', () => {
      expect(store.loaded()).toBe(false);
      expect(store.loadError()).toBeNull();
      expect(store.users()).toEqual([]);
      expect(store.teams()).toEqual([]);
      expect(store.roles()).toEqual([]);
      expect(store.permissions()).toEqual([]);
      expect(store.sagaTypes()).toEqual([]);
    });

    it('reads the catalogue, users, teams, roles and saga types in one go', async () => {
      const data = adminData();
      const done = store.load();
      expect(store.loaded()).toBe(false);

      answerLoad(http, data);
      await done;

      expect(store.loaded()).toBe(true);
      expect(store.loadError()).toBeNull();
      expect(store.permissions()).toEqual(data.permissions);
      expect(store.users()).toEqual(data.users);
      expect(store.teams()).toEqual(data.teams);
      expect(store.roles()).toEqual(data.roles);
    });

    it('lists each saga type once, in ordinal order', async () => {
      await loaded(
        adminData({
          sagaTypes: [
            { sagaType: 'b', kind: 'Orchestrated' },
            { sagaType: 'OrderSaga', kind: 'Orchestrated' },
            { sagaType: 'OrderSaga', kind: 'Choreographed' },
            { sagaType: 'a', kind: 'Orchestrated' },
          ],
        }),
      );

      expect(store.sagaTypes()).toEqual(['OrderSaga', 'a', 'b']);
    });

    it('is not failed by a 403 on the saga types: a manager who cannot view sagas has none', async () => {
      const done = store.load();
      answerLoad(http, adminData(), 403);
      await done;

      expect(store.loaded()).toBe(true);
      expect(store.loadError()).toBeNull();
      expect(store.sagaTypes()).toEqual([]);
      expect(store.users().length).toBeGreaterThan(0);
    });

    it('fails on any other refusal, with the sentence to show, and stays not loaded', async () => {
      const done = store.load();
      // The first refusal ends the read: the requests still open are cancelled, and are not answered.
      refuse(http, '/api/admin/users', 500);
      await done;

      expect(store.loaded()).toBe(false);
      expect(store.loadError()).toContain('HTTP 500');
    });

    it('says a lost permission in its own words', async () => {
      const done = store.load();
      refuse(http, '/api/admin/users', 403);
      await done;

      expect(store.loaded()).toBe(false);
      expect(store.loadError()).toBe('You no longer have permission to manage access.');
    });

    it.each([
      ['a server error', 500],
      ['the service being unavailable', 503],
      ['no answer at all', 0],
    ])('is not failed by any error on the saga types: %s', async (_what, status) => {
      const done = store.load();
      const data = adminData();
      http.expectOne('/api/admin/permissions').flush(data.permissions);
      http.expectOne('/api/admin/users').flush(data.users);
      http.expectOne('/api/admin/teams').flush(data.teams);
      http.expectOne('/api/admin/roles').flush(data.roles);
      const types = http.expectOne('/api/saga-types');
      if (status === 0) types.error(new ProgressEvent('error'));
      else types.flush(null, { status, statusText: 'Refused' });
      await done;

      // The saga reader being down must not lock the manager out of users, teams and roles.
      expect(store.loaded()).toBe(true);
      expect(store.loadError()).toBeNull();
      expect(store.sagaTypes()).toEqual([]);
      expect(store.roles()).toEqual(data.roles);
    });

    it('does not say "This no longer exists" for a 404 on a read: the endpoint is what is missing', async () => {
      const done = store.load();
      refuse(http, '/api/admin/users', 404);
      await done;

      expect(store.loadError()).toBe('The administration data could not be loaded.');
      expect(store.loadErrorKind()).toBe('failed');
    });

    it('says which kind of error it is, so the shell can tell one that asking again cannot mend', async () => {
      expect(store.loadErrorKind()).toBeNull();

      const done = store.load();
      refuse(http, '/api/admin/users', 403);
      await done;

      expect(store.loadErrorKind()).toBe('forbidden');
      store.clear();
      expect(store.loadErrorKind()).toBeNull();
    });

    it('says the session ended for a 401', async () => {
      const done = store.load();
      refuse(http, '/api/admin/users', 401);
      await done;

      expect(store.loadError()).toBe('Your session has ended. Sign in again.');
    });

    describe('when the API does not answer', () => {
      beforeEach(() => vi.useFakeTimers());
      afterEach(() => vi.useRealTimers());

      it('fails with a sentence after the timeout, not before, instead of loading for good', async () => {
        const done = store.load();

        await vi.advanceTimersByTimeAsync(READ_TIMEOUT_MS - 1);
        expect(store.loadError()).toBeNull();
        expect(store.loaded()).toBe(false);

        await vi.advanceTimersByTimeAsync(1);
        await done;

        expect(store.loaded()).toBe(false);
        expect(store.loadError()).toBe('The dashboard API did not answer in time. Try again.');
        expect(store.loadErrorKind()).toBe('failed');
        // The reads that never answered are given up, not left open.
        expect(http.match(() => true).every((req) => req.cancelled)).toBe(true);
      });

      it('can be asked again after the timeout', async () => {
        const first = store.load();
        await vi.advanceTimersByTimeAsync(READ_TIMEOUT_MS);
        await first;
        http.match(() => true);

        const again = store.load();
        answerLoad(http);
        await again;

        expect(store.loaded()).toBe(true);
        expect(store.loadError()).toBeNull();
      });

      it('applies to a refresh too: the lists stay, with the sentence', async () => {
        const done = store.load();
        answerLoad(http);
        await done;
        const before = store.users();

        const refreshing = store.refresh();
        await vi.advanceTimersByTimeAsync(READ_TIMEOUT_MS);
        await refreshing;
        http.match(() => true);

        expect(store.loaded()).toBe(true);
        expect(store.users()).toEqual(before);
        expect(store.loadError()).toBe('The dashboard API did not answer in time. Try again.');
      });
    });

    it('never rejects', async () => {
      const done = store.load();
      failEverything(http);

      await expect(done).resolves.toBeUndefined();
      expect(store.loadError()).toContain('Cannot reach');
    });

    it('can be asked again after a failure, and clears the error when it starts', async () => {
      const failed = store.load();
      failEverything(http);
      await failed;
      expect(store.loadError()).not.toBeNull();

      const again = store.load();
      expect(store.loadError()).toBeNull();
      answerLoad(http);
      await again;

      expect(store.loaded()).toBe(true);
    });

    it('hides the pages while it reads again, and drops an answer to a read that was replaced', async () => {
      await loaded();
      expect(store.loaded()).toBe(true);

      const first = store.load();
      expect(store.loaded()).toBe(false);
      const stale = http.match((r) => r.url.startsWith('/api/'));
      const second = store.load();
      const fresh = http.match((r) => r.url.startsWith('/api/'));
      // The newer read answers first, the older one afterwards: the older answer must not win.
      for (const req of fresh)
        req.flush(req.request.url === '/api/admin/users' ? [adminUser({ username: 'new' })] : []);
      await second;
      for (const req of stale)
        req.flush(req.request.url === '/api/admin/users' ? [adminUser({ username: 'old' })] : []);
      await first;

      expect(store.users().map((u) => u.username)).toEqual(['new']);
      expect(store.loaded()).toBe(true);
    });
  });

  describe('refresh', () => {
    it('reads the users, teams and roles again and nothing else, and leaves loaded true', async () => {
      await loaded();

      const refreshing = store.refresh();
      expect(store.loaded()).toBe(true);
      answerReload(http, adminData({ users: [], teams: [] }));
      await refreshing;

      expect(store.loaded()).toBe(true);
      expect(store.users()).toEqual([]);
      http.expectNone('/api/admin/permissions');
      http.expectNone('/api/saga-types');
    });

    it('clears a reload error when it succeeds', async () => {
      await loaded();
      const failing = store.refresh();
      failEverything(http);
      await failing;
      expect(store.loadError()).not.toBeNull();

      const again = store.refresh();
      answerReload(http);
      await again;

      expect(store.loadError()).toBeNull();
      expect(store.loaded()).toBe(true);
    });

    it('keeps the lists and sets the error when it fails, and says a lost permission as such', async () => {
      await loaded();
      const before = store.roles();

      const refreshing = store.refresh();
      refuse(http, '/api/admin/users', 403);
      await refreshing;

      expect(store.roles()).toEqual(before);
      expect(store.loaded()).toBe(true);
      expect(store.loadError()).toBe('You no longer have permission to manage access.');
      expect(store.loadErrorKind()).toBe('forbidden');
    });

    it('is a full read while nothing has been read yet, so loaded can become true', async () => {
      const first = store.load();
      failEverything(http);
      await first;
      expect(store.loaded()).toBe(false);

      const refreshing = store.refresh();
      answerLoad(http);
      await refreshing;

      expect(store.loaded()).toBe(true);
    });

    it('does nothing in a store that was cleared', async () => {
      await loaded();
      store.clear();

      await store.refresh();

      http.expectNone(() => true);
      expect(store.loaded()).toBe(false);
    });
  });

  describe('clear', () => {
    it('forgets everything, so a shell that is opened again starts empty', async () => {
      await loaded();

      store.clear();

      expect(store.loaded()).toBe(false);
      expect(store.users()).toEqual([]);
      expect(store.roles()).toEqual([]);
      expect(store.permissions()).toEqual([]);
      expect(store.sagaTypes()).toEqual([]);
    });

    it('drops a read that is still running', async () => {
      const done = store.load();
      store.clear();
      answerLoad(http);
      await done;

      expect(store.loaded()).toBe(false);
      expect(store.users()).toEqual([]);
    });

    it('stops a change that is still running from reading the lists back', async () => {
      await loaded();
      const saving = store.deleteRole('x');
      store.clear();
      http.expectOne('/api/admin/roles/x').flush(null, { status: 204, statusText: 'No Content' });
      await saving;
      await turn();

      http.expectNone('/api/admin/users');
      expect(store.users()).toEqual([]);
    });
  });

  describe('roleUsage', () => {
    it('counts the grants of users and of teams that name each role', async () => {
      await loaded(
        adminData({
          users: [
            adminUser({ id: 'u1', grants: [grant(ADMINISTRATOR_ID), grant(OPERATOR_ID, ['A'])] }),
            adminUser({ id: 'u2', grants: [grant(OPERATOR_ID)] }),
          ],
          teams: [team({ grants: [grant(OPERATOR_ID, ['B']), grant(VIEWER_ID)] })],
        }),
      );

      const usage = store.roleUsage();
      expect(usage.get(ADMINISTRATOR_ID)).toBe(1);
      expect(usage.get(OPERATOR_ID)).toBe(3);
      expect(usage.get(VIEWER_ID)).toBe(1);
      expect(usage.get('unheld')).toBeUndefined();
    });

    it('follows the lists', async () => {
      await loaded(adminData({ users: [], teams: [] }));
      expect(store.roleUsage().size).toBe(0);

      const saving = store.saveTeam(null, {
        name: 'T',
        description: '',
        memberIds: [],
        grants: [grant(VIEWER_ID)],
      });
      http.expectOne('/api/admin/teams').flush(team());
      await turn();
      answerReload(http, adminData({ users: [], teams: [team({ grants: [grant(VIEWER_ID)] })] }));
      await saving;

      expect(store.roleUsage().get(VIEWER_ID)).toBe(1);
    });
  });

  describe('a change', () => {
    const NEW_ROLE: SaveRole = { name: 'Support', description: '', permissions: ['sagas.view'] };

    it('awaits the API, then reads users, teams and roles again, and resolves after that with the API answer', async () => {
      await loaded();
      const created = role({ id: 'new-role' });

      let settled = false;
      const saving = store.saveRole(null, NEW_ROLE).then((answer) => {
        settled = true;
        return answer;
      });
      const post = http.expectOne('/api/admin/roles');
      expect(post.request.method).toBe('POST');
      // No read before the API has answered.
      http.expectNone('/api/admin/users');
      post.flush(created);
      await turn();
      expect(settled).toBe(false);

      answerReload(http, adminData({ roles: [...adminData().roles, created] }));
      expect(await saving).toEqual(created);

      expect(store.roles().map((r) => r.id)).toContain('new-role');
      // The catalogue and the saga types are not read again.
      http.expectNone('/api/admin/permissions');
      http.expectNone('/api/saga-types');
    });

    it.each([
      [
        'saveUser (create) is POST /api/admin/users',
        (s: AdminStore) =>
          s.saveUser(null, {
            username: 'u',
            displayName: 'U',
            password: 'p',
            mustChangePassword: true,
            grants: [],
          }),
        'POST',
        '/api/admin/users',
      ],
      [
        'saveUser (update) is PUT /api/admin/users/{id}',
        (s: AdminStore) => s.saveUser('7', { displayName: 'U', isEnabled: true, grants: [] }),
        'PUT',
        '/api/admin/users/7',
      ],
      [
        'deleteUser is DELETE /api/admin/users/{id}',
        (s: AdminStore) => s.deleteUser('7'),
        'DELETE',
        '/api/admin/users/7',
      ],
      [
        'resetPassword is POST /api/admin/users/{id}/password',
        (s: AdminStore) => s.resetPassword('7', { newPassword: 'p', mustChangePassword: true }),
        'POST',
        '/api/admin/users/7/password',
      ],
      [
        'unlockUser is POST /api/admin/users/{id}/unlock',
        (s: AdminStore) => s.unlockUser('7'),
        'POST',
        '/api/admin/users/7/unlock',
      ],
      [
        'saveTeam (create) is POST /api/admin/teams',
        (s: AdminStore) =>
          s.saveTeam(null, { name: 'T', description: '', memberIds: [], grants: [] }),
        'POST',
        '/api/admin/teams',
      ],
      [
        'saveTeam (update) is PUT /api/admin/teams/{id}',
        (s: AdminStore) =>
          s.saveTeam('7', { name: 'T', description: '', memberIds: [], grants: [] }),
        'PUT',
        '/api/admin/teams/7',
      ],
      [
        'deleteTeam is DELETE /api/admin/teams/{id}',
        (s: AdminStore) => s.deleteTeam('7'),
        'DELETE',
        '/api/admin/teams/7',
      ],
      [
        'saveRole (create) is POST /api/admin/roles',
        (s: AdminStore) => s.saveRole(null, NEW_ROLE),
        'POST',
        '/api/admin/roles',
      ],
      [
        'saveRole (update) is PUT /api/admin/roles/{id}',
        (s: AdminStore) => s.saveRole('7', NEW_ROLE),
        'PUT',
        '/api/admin/roles/7',
      ],
      [
        'deleteRole is DELETE /api/admin/roles/{id}',
        (s: AdminStore) => s.deleteRole('7'),
        'DELETE',
        '/api/admin/roles/7',
      ],
    ] as const)('%s, then reads the three lists again', async (_name, change, method, url) => {
      await loaded();

      const done = change(store);
      const req = http.expectOne(url);
      expect(req.request.method).toBe(method);
      req.flush(
        method === 'DELETE' ? null : {},
        method === 'DELETE'
          ? { status: 204, statusText: 'No Content' }
          : { status: 200, statusText: 'OK' },
      );
      await turn();
      answerReload(http);
      await done;
    });

    it.each([
      ['a validation error', 400],
      ['a server error', 500],
      ['a lost permission', 403],
    ])(
      'rejects with the API error and reads nothing for %s: nothing changed',
      async (_what, status) => {
        await loaded();

        const saving = rejection(store.saveRole('7', NEW_ROLE));
        http
          .expectOne('/api/admin/roles/7')
          .flush({ code: 'x', detail: 'Refused.' }, { status, statusText: 'Refused' });

        expect(await saving).toBeDefined();
        await turn();
        http.expectNone('/api/admin/users');
        http.expectNone('/api/admin/roles');
      },
    );

    it.each([
      ['a 404, so a list stops showing what is gone', 404, 'gone'],
      [
        'a 409, so the lists show the fact that refused it (a role in use, a taken name)',
        409,
        'conflict',
      ],
    ] as const)(
      'rejects at once and reads the lists again after %s',
      async (_what, status, kind) => {
        await loaded();

        const saving = rejection(store.deleteRole('7'));
        http
          .expectOne('/api/admin/roles/7')
          .flush(
            { code: 'role_in_use', detail: 'Still granted.' },
            { status, statusText: 'Refused' },
          );
        // The refusal is not held up by the read: the page shows it at once.
        const err = await saving;
        expect(adminFailure(err, 'x').kind).toBe(kind);

        const changed = adminData({ users: [adminData().users[0]] });
        answerReload(http, changed);
        await turn();
        expect(store.users().map((u) => u.id)).toEqual(['u-admin']);
      },
    );

    it('does not leave a full read that is running unanswered for ever when a change finishes meanwhile', async () => {
      await loaded();
      // A save is slow; the manager leaves the area and enters it again, which reads everything.
      const saving = store.saveRole('7', NEW_ROLE);
      store.clear();
      const reading = store.load();
      expect(store.loaded()).toBe(false);

      // The save answers while that read is running: it asks for the lists, which must not supersede the full read.
      http.expectOne('/api/admin/roles/7').flush(role());
      await turn();
      const open = http.match(() => true);
      answer(open, adminData());
      await Promise.all([saving, reading]);

      expect(store.loaded()).toBe(true);
      expect(store.loadError()).toBeNull();
      expect(store.roles()).toEqual(adminData().roles);
    });

    it('is not failed by a reload that fails: the change was made', async () => {
      await loaded();
      const before = store.users();

      const saving = store.deleteTeam('t');
      http.expectOne('/api/admin/teams/t').flush(null, { status: 204, statusText: 'No Content' });
      await turn();
      http.expectOne('/api/admin/teams').flush([]);
      http.expectOne('/api/admin/roles').flush([]);
      http.expectOne('/api/admin/users').error(new ProgressEvent('error'));

      await expect(saving).resolves.toBeNull();
      expect(store.loaded()).toBe(true);
      expect(store.loadError()).toContain('Cannot reach');
      // What the lists held stays; nothing was half-replaced.
      expect(store.users()).toEqual(before);
    });

    it('clears a reload error once a later reload succeeds', async () => {
      await loaded();
      const failing = store.deleteTeam('t');
      http.expectOne('/api/admin/teams/t').flush(null, { status: 204, statusText: 'No Content' });
      await turn();
      failEverything(http);
      await failing;
      expect(store.loadError()).not.toBeNull();

      const next = store.deleteTeam('t2');
      http.expectOne('/api/admin/teams/t2').flush(null, { status: 204, statusText: 'No Content' });
      await turn();
      answerReload(http);
      await next;

      expect(store.loadError()).toBeNull();
    });
  });
});
