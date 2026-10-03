import { ChangeDetectionStrategy, Component } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, Routes, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import {
  AdminData,
  BUILT_IN_ROLES,
  OPERATOR_ID,
  PERMISSIONS,
  adminData,
  answerLoad,
  answerReload,
  grant,
  role,
  team,
} from '../../../../testing/admin';
import roleRequest from '../../../../testing/contracts/admin/role.request.json';
import { problem } from '../../../../testing/http-error';
import { AdminStore } from '../../admin.store';
import { RoleEdit } from './role-edit';

@Component({
  selector: 'app-list-stub',
  template: 'the roles',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class ListStub {}

const ROUTES: Routes = [
  {
    path: 'admin/roles',
    children: [
      { path: '', component: ListStub },
      { path: 'new', component: RoleEdit },
      { path: ':id', component: RoleEdit },
    ],
  },
];

const SUPPORT = role();
const LAST_ADMIN =
  'This change would leave no enabled user who can manage access for all saga types.';

describe('RoleEdit', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;
  let store: AdminStore;

  async function open(url: string, data: AdminData = adminData()): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(ROUTES),
        provideHttpClient(),
        provideHttpClientTesting(),
        AdminStore,
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(AdminStore);
    const loading = store.load();
    answerLoad(http, data);
    await loading;
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    await settle();
  }

  afterEach(() => http.verify());

  /** Lets promises finish and the model write its values to the inputs, then renders. */
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve));
    harness.detectChanges();
    await harness.fixture.whenStable();
  }

  const el = () => harness.routeNativeElement as HTMLElement;
  const url = () => TestBed.inject(Router).url;
  const nameInput = () => el().querySelector<HTMLInputElement>('#role-name')!;
  const descriptionInput = () => el().querySelector<HTMLTextAreaElement>('#role-description')!;
  const boxes = () =>
    Array.from(el().querySelectorAll<HTMLInputElement>('fieldset input[type="checkbox"]'));
  const box = (key: string) =>
    el().querySelector<HTMLInputElement>(`fieldset input[value="${key}"]`)!;
  const checked = () =>
    boxes()
      .filter((b) => b.checked)
      .map((b) => b.value);
  const message = (id: string) => el().querySelector(`#role-${id}-error`)?.textContent?.trim();
  const banner = (selector = '.banner--error') => el().querySelector<HTMLElement>(selector);
  const button = (text: string) =>
    Array.from(el().querySelectorAll<HTMLElement>('button, a.btn')).find(
      (b) => b.textContent?.trim() === text,
    );
  const focused = () => document.activeElement;
  const askAbleDelete = () => button('Delete role') as HTMLButtonElement;
  /** Whether a button says it is unavailable (aria-disabled, as the confirm button does: it stays focusable). */
  const unavailable = (el: HTMLElement | undefined) => el?.getAttribute('aria-disabled') === 'true';

  function type(field: HTMLInputElement | HTMLTextAreaElement, value: string): void {
    field.value = value;
    field.dispatchEvent(new Event('input'));
    harness.detectChanges();
  }

  function tick(key: string, on = true): void {
    const target = box(key);
    target.checked = on;
    target.dispatchEvent(new Event('change'));
    harness.detectChanges();
  }

  async function save(): Promise<void> {
    el().querySelector<HTMLButtonElement>('button[type="submit"]')!.click();
    await settle();
  }

  /** The write the page sent, answered with `status`. Returns the request before it is answered. */
  function write(method: string, path: string): TestRequest {
    const req = http.expectOne(path);
    expect(req.request.method).toBe(method);
    return req;
  }

  /** Answers the three reads that follow a change, once the store has asked for them. */
  async function reloaded(data: AdminData = adminData()): Promise<void> {
    await settle();
    answerReload(http, data);
    await settle();
  }

  const refuse = (req: TestRequest, status: number, body: object | null) =>
    req.flush(body, { status, statusText: 'Refused' });

  describe('a new role', () => {
    it('starts empty, with one checkbox per catalogue entry and its description', async () => {
      await open('/admin/roles/new');

      expect(el().querySelector('h2')?.textContent).toBe('New role');
      expect(nameInput().value).toBe('');
      expect(descriptionInput().value).toBe('');
      expect(boxes().map((b) => b.value)).toEqual(PERMISSIONS.map((p) => p.key));
      expect(checked()).toEqual([]);
      const labels = Array.from(el().querySelectorAll('.permission')).map((l) => [
        l.querySelector('.permission-name')?.textContent?.replace(/\s+/g, ' ').trim(),
        l.querySelector('.field-hint')?.textContent?.trim(),
      ]);
      expect(labels).toEqual(PERMISSIONS.map((p) => [`${p.name} ${p.key}`, p.description]));
      expect(button('Create role')).toBeDefined();
      // Nothing to delete yet, and nothing built in about it.
      expect(button('Delete role')).toBeUndefined();
      expect(banner('.banner--info')).toBeNull();
    });

    it('starts from another role with ?from=: its permissions and description, under a name of its own', async () => {
      await open(`/admin/roles/new?from=${OPERATOR_ID}`);

      expect(el().querySelector('h2')?.textContent).toBe('New role');
      expect(nameInput().value).toBe('Operator copy');
      expect(descriptionInput().value).toBe(BUILT_IN_ROLES[1].description);
      expect(checked()).toEqual(['sagas.view', 'sagas.data', 'sagas.retry']);
      expect(button('Create role')).toBeDefined();
    });

    it("keeps a copy's name within the limit", async () => {
      await open(
        '/admin/roles/new?from=long',
        adminData({ roles: [role({ id: 'long', name: 'x'.repeat(64) })] }),
      );

      expect(nameInput().value).toHaveLength(64);
    });

    it('starts empty for a ?from= that names no role', async () => {
      await open('/admin/roles/new?from=nobody');

      expect(nameInput().value).toBe('');
      expect(checked()).toEqual([]);
    });

    describe('the checks made before anything is sent', () => {
      it('asks for a name and a permission, under their fields, and sends nothing', async () => {
        await open('/admin/roles/new');

        await save();

        expect(message('name')).toBe('Enter a name.');
        expect(message('permissions')).toBe('Choose at least one permission.');
        expect(message('description')).toBeUndefined();
        http.expectNone((r) => r.method !== 'GET');
      });

      it('marks the fields: aria-invalid, aria-describedby, and the message beside them', async () => {
        await open('/admin/roles/new');
        await save();

        expect(nameInput().getAttribute('aria-invalid')).toBe('true');
        expect(nameInput().getAttribute('aria-describedby')).toBe('role-name-error');
        expect(el().querySelector('#role-name-error')?.classList).toContain('field-error');
        expect(descriptionInput().getAttribute('aria-invalid')).toBeNull();
        expect(el().querySelector('fieldset')?.getAttribute('aria-describedby')).toBe(
          'role-permissions-error',
        );
      });

      it('moves focus to the first field with an error', async () => {
        await open('/admin/roles/new');
        await save();
        expect(focused()).toBe(nameInput());

        type(nameInput(), 'Support');
        await save();
        expect(focused()).toBe(box('sagas.view'));
      });

      it('shows nothing before the first save, and follows the draft after it', async () => {
        await open('/admin/roles/new');
        expect(el().querySelector('.field-error')).toBeNull();

        await save();
        type(nameInput(), 'Support');
        tick('sagas.view');
        await settle();

        expect(el().querySelector('.field-error')).toBeNull();
        expect(nameInput().getAttribute('aria-invalid')).toBeNull();
      });

      it('refuses a name or description over the API limits, and says so', async () => {
        await open('/admin/roles/new');
        // The maxlength attribute stops typing; a pasted or scripted value is still checked.
        type(nameInput(), 'x'.repeat(65));
        type(descriptionInput(), 'y'.repeat(257));
        tick('sagas.view');

        await save();

        expect(message('name')).toBe('Use at most 64 characters.');
        expect(message('description')).toBe('Use at most 256 characters.');
        http.expectNone((r) => r.method !== 'GET');
      });

      it('treats a blank name as no name', async () => {
        await open('/admin/roles/new');
        type(nameInput(), '   ');
        tick('sagas.view');

        await save();

        expect(message('name')).toBe('Enter a name.');
      });

      it('limits what can be typed with the maxlength attribute, set by binding', async () => {
        await open('/admin/roles/new');

        expect(nameInput().getAttribute('maxlength')).toBe('64');
        expect(descriptionInput().getAttribute('maxlength')).toBe('256');
      });
    });

    describe('Create role', () => {
      it('sends exactly role.request.json, whatever order the permissions were ticked in, then goes to the list', async () => {
        await open('/admin/roles/new');
        type(nameInput(), roleRequest.name);
        type(descriptionInput(), roleRequest.description);
        tick('sagas.retry');
        tick('sagas.view');

        await save();
        const post = write('POST', '/api/admin/roles');
        expect(post.request.body).toEqual(roleRequest);
        post.flush(role());
        await settle();
        answerReload(http, adminData());
        await settle();

        expect(url()).toBe('/admin/roles');
      });

      it('trims the name and the description, and sends a blank description as empty', async () => {
        await open('/admin/roles/new');
        type(nameInput(), '  Support  ');
        type(descriptionInput(), '   ');
        tick('sagas.view');

        await save();

        const post = write('POST', '/api/admin/roles');
        expect(post.request.body).toEqual({
          name: 'Support',
          description: '',
          permissions: ['sagas.view'],
        });
        post.flush(role());
        await settle();
        answerReload(http);
        await settle();
      });

      it('shows the work: the button is disabled and says so, and a second submit sends nothing', async () => {
        await open('/admin/roles/new');
        type(nameInput(), 'Support');
        tick('sagas.view');

        await save();
        const submit = el().querySelector<HTMLButtonElement>('button[type="submit"]')!;
        expect(submit.disabled).toBe(true);
        expect(submit.textContent?.trim()).toBe('Saving…');
        el()
          .querySelector('form')!
          .dispatchEvent(new Event('submit', { cancelable: true }));
        await settle();

        const post = write('POST', '/api/admin/roles');
        post.flush(role());
        await settle();
        answerReload(http);
        await settle();
      });

      it('goes back to the list for Cancel, with nothing sent', async () => {
        await open('/admin/roles/new');
        type(nameInput(), 'Support');

        const cancel = button('Cancel') as HTMLAnchorElement;
        expect(cancel.getAttribute('href')).toBe('/admin/roles');
        cancel.click();
        await settle();

        expect(url()).toBe('/admin/roles');
        http.expectNone((r) => r.method !== 'GET');
      });
    });

    describe('what the API refuses', () => {
      async function filled(): Promise<void> {
        await open('/admin/roles/new');
        type(nameInput(), 'Support');
        type(descriptionInput(), 'Looks at things.');
        tick('sagas.view');
        tick('sagas.retry');
      }

      it('puts the messages of a 400 under their fields, keeps the draft and focuses the first', async () => {
        await filled();
        await save();

        refuse(
          write('POST', '/api/admin/roles'),
          400,
          problem('validation', 'The request is not valid.', {
            name: ['Enter 1 to 64 characters, with no control characters.'],
            'permissions[1]': ["'sagas.zap' is not a permission."],
          }),
        );
        await settle();

        expect(message('name')).toBe('Enter 1 to 64 characters, with no control characters.');
        expect(message('permissions')).toBe("'sagas.zap' is not a permission.");
        expect(nameInput().getAttribute('aria-invalid')).toBe('true');
        expect(nameInput().getAttribute('aria-describedby')).toBe('role-name-error');
        expect(banner()).toBeNull();
        expect(nameInput().value).toBe('Support');
        expect(descriptionInput().value).toBe('Looks at things.');
        expect(checked()).toEqual(['sagas.view', 'sagas.retry']);
        expect(focused()).toBe(nameInput());
        expect(url()).toBe('/admin/roles/new');
      });

      it('lists the messages of a path it has no field for in the banner', async () => {
        await filled();
        await save();

        refuse(
          write('POST', '/api/admin/roles'),
          400,
          problem('validation', 'The request is not valid.', {
            'grants[0].roleId': ['No role has this id.'],
          }),
        );
        await settle();

        expect(banner('.banner--error[role="alert"]')?.textContent).toContain(
          'No role has this id.',
        );
      });

      it("ends a field's message when that field changes, and leaves the others", async () => {
        await filled();
        await save();
        refuse(
          write('POST', '/api/admin/roles'),
          400,
          problem('validation', 'x', { name: ['Taken.'], description: ['Too long.'] }),
        );
        await settle();
        expect(message('name')).toBe('Taken.');

        type(nameInput(), 'Support 2');

        expect(message('name')).toBeUndefined();
        expect(message('description')).toBe('Too long.');
      });

      it('shows a duplicate name as a banner with the server detail and keeps the draft', async () => {
        await filled();
        await save();

        refuse(
          write('POST', '/api/admin/roles'),
          409,
          problem(
            'name_taken',
            "A role named 'Support' already exists; names are compared ignoring case.",
          ),
        );
        await reloaded();

        expect(banner('.banner--error[role="alert"]')?.textContent).toContain(
          "A role named 'Support' already exists",
        );
        expect(nameInput().value).toBe('Support');
        expect(checked()).toEqual(['sagas.view', 'sagas.retry']);
        expect(url()).toBe('/admin/roles/new');
        // The form is usable again.
        expect(el().querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled).toBe(
          false,
        );
      });

      it.each([
        [403, 'You no longer have permission to manage access.'],
        [500, 'HTTP 500'],
      ])('says a %i in its own words and keeps the draft', async (status, text) => {
        await filled();
        await save();

        refuse(write('POST', '/api/admin/roles'), status, problem('x', 'server words'));
        await settle();

        expect(banner('.banner--error[role="alert"]')?.textContent).toContain(text);
        expect(nameInput().value).toBe('Support');
      });

      it('says "This no longer exists" for a 404 and offers the way back to the list', async () => {
        await filled();
        await save();

        refuse(write('POST', '/api/admin/roles'), 404, { title: 'Not found' });
        await settle();
        // The store reads the lists again after a 404, so the lists stop showing what is gone.
        answerReload(http);
        await settle();

        const alert = banner('.banner--warning[role="alert"]')!;
        expect(alert.textContent).toContain('This no longer exists');
        expect(alert.querySelector('a')?.getAttribute('href')).toBe('/admin/roles');
        expect(el().querySelector('form')).toBeNull();
      });
    });
  });

  describe('a custom role', () => {
    it('is filled in from the store, with the saved name as its heading', async () => {
      await open(`/admin/roles/${SUPPORT.id}`);

      expect(el().querySelector('h2')?.textContent).toBe('Support');
      expect(nameInput().value).toBe('Support');
      expect(descriptionInput().value).toBe(SUPPORT.description);
      expect(checked()).toEqual(['sagas.view', 'sagas.retry']);
      expect(nameInput().readOnly).toBe(false);
      expect(boxes().every((b) => !b.disabled)).toBe(true);
      expect(button('Save')).toBeDefined();
      // Not called Cancel: the question of Delete role has a Cancel of its own.
      expect(button('Cancel')).toBeUndefined();
      expect((button('Back to the roles') as HTMLAnchorElement).getAttribute('href')).toBe(
        '/admin/roles',
      );
      expect(button('Delete role')).toBeDefined();
      expect(banner('.banner--info')).toBeNull();
    });

    it('shows a role with no description as an empty box', async () => {
      await open('/admin/roles/r', adminData({ roles: [role({ id: 'r', description: null })] }));

      expect(descriptionInput().value).toBe('');
    });

    describe('Save', () => {
      it('replaces the role with a PUT of the draft, then shows the API answer and says Saved', async () => {
        await open(`/admin/roles/${SUPPORT.id}`);
        type(nameInput(), '  Support desk ');
        tick('sagas.data');

        await save();
        const put = write('PUT', `/api/admin/roles/${SUPPORT.id}`);
        expect(put.request.body).toEqual({
          name: 'Support desk',
          description: SUPPORT.description,
          permissions: ['sagas.view', 'sagas.data', 'sagas.retry'],
        });
        put.flush(
          role({ name: 'Support desk', permissions: ['sagas.view', 'sagas.data', 'sagas.retry'] }),
        );
        await settle();
        answerReload(
          http,
          adminData({
            roles: [
              ...BUILT_IN_ROLES,
              role({
                name: 'Support desk',
                permissions: ['sagas.view', 'sagas.data', 'sagas.retry'],
              }),
            ],
          }),
        );
        await settle();

        // It stays where it is, shows what the API stored (trimmed), and says it is saved.
        expect(url()).toBe(`/admin/roles/${SUPPORT.id}`);
        expect(nameInput().value).toBe('Support desk');
        expect(el().querySelector('h2')?.textContent).toBe('Support desk');
        expect(banner('.banner--success')?.textContent).toContain('Saved.');
        expect(banner('.banner--success')?.closest('[role="status"]')).not.toBeNull();
      });

      it.each([
        ['a permission is ticked', () => tick('sagas.data')],
        ['the name is typed in', () => type(nameInput(), 'Support desk')],
        ['the description is typed in', () => type(descriptionInput(), 'Something else.')],
      ])('takes Saved away as soon as the draft changes again: %s', async (_when, change) => {
        await open(`/admin/roles/${SUPPORT.id}`);
        await save();
        write('PUT', `/api/admin/roles/${SUPPORT.id}`).flush(role());
        await settle();
        answerReload(http);
        await settle();
        expect(banner('.banner--success')).not.toBeNull();

        change();
        await settle();

        expect(banner('.banner--success')).toBeNull();
      });

      it('keeps what is being typed when the store reads the lists again behind the page', async () => {
        await open(`/admin/roles/${SUPPORT.id}`);
        type(nameInput(), 'Half typed');

        const other = store.saveRole(null, {
          name: 'Other',
          description: '',
          permissions: ['sagas.view'],
        });
        write('POST', '/api/admin/roles').flush(role({ id: 'other', name: 'Other' }));
        await settle();
        answerReload(
          http,
          adminData({ roles: [...adminData().roles, role({ id: 'other', name: 'Other' })] }),
        );
        await other;
        await settle();

        expect(nameInput().value).toBe('Half typed');
      });

      it('keeps the draft and says why for a last-administrator refusal', async () => {
        await open(`/admin/roles/${SUPPORT.id}`);
        type(nameInput(), 'Support desk');
        tick('access.manage');
        await save();

        refuse(
          write('PUT', `/api/admin/roles/${SUPPORT.id}`),
          409,
          problem('last_administrator', LAST_ADMIN),
        );
        await reloaded();

        const alert = banner('.banner--error[role="alert"]')!;
        expect(alert.textContent).toContain(LAST_ADMIN);
        expect(alert.textContent).toContain(
          'Give another enabled user an all-saga-types grant whose role includes access.manage, then try again.',
        );
        expect(nameInput().value).toBe('Support desk');
        expect(checked()).toEqual(['sagas.view', 'sagas.retry', 'access.manage']);
        expect(banner('.banner--success')).toBeNull();
        // Saving again is possible.
        expect(el().querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled).toBe(
          false,
        );
      });

      it('says "This no longer exists" when the API answers 404', async () => {
        await open(`/admin/roles/${SUPPORT.id}`);
        await save();

        refuse(write('PUT', `/api/admin/roles/${SUPPORT.id}`), 404, { title: 'Not found' });
        await settle();
        answerReload(http, adminData({ roles: BUILT_IN_ROLES }));
        await settle();

        expect(banner('.banner--warning[role="alert"]')?.textContent).toContain(
          'This no longer exists',
        );
        expect(el().querySelector('form')).toBeNull();
      });
    });

    describe('Delete role', () => {
      it('asks first, and deletes only on yes, then goes to the list', async () => {
        await open(`/admin/roles/${SUPPORT.id}`);

        button('Delete role')!.click();
        await settle();
        expect(el().querySelector('.confirm-prompt')?.textContent).toBe(
          'Delete the role Support? This cannot be undone.',
        );
        http.expectNone((r) => r.method === 'DELETE');

        button('Yes, delete')!.click();
        await settle();
        write('DELETE', `/api/admin/roles/${SUPPORT.id}`).flush(null, {
          status: 204,
          statusText: 'No Content',
        });
        await settle();
        answerReload(http, adminData({ roles: BUILT_IN_ROLES }));
        await settle();

        expect(url()).toBe('/admin/roles');
      });

      it('sends nothing on Cancel', async () => {
        await open(`/admin/roles/${SUPPORT.id}`);

        button('Delete role')!.click();
        await settle();
        button('Cancel')!.click();
        await settle();
        expect(el().querySelector('.confirm-prompt')).toBeNull();

        http.expectNone((r) => r.method === 'DELETE');
        expect(url()).toBe(`/admin/roles/${SUPPORT.id}`);
      });

      it('is disabled while a user or a team holds the role, and says by how many grants', async () => {
        await open(
          `/admin/roles/${SUPPORT.id}`,
          adminData({ teams: [team({ grants: [grant(SUPPORT.id, ['OrderSaga'])] })] }),
        );

        const remove = askAbleDelete();
        expect(unavailable(remove)).toBe(true);
        expect(
          el().querySelector('.danger .field-hint')?.textContent?.replace(/\s+/g, ' '),
        ).toContain('Used by 1 grant:');
        remove.click();
        await settle();
        expect(el().querySelector('.confirm-prompt')).toBeNull();
      });

      it('counts the grants of users and teams together', async () => {
        await open(
          `/admin/roles/${SUPPORT.id}`,
          adminData({
            users: adminData().users.map((u) => ({ ...u, grants: [grant(SUPPORT.id)] })),
            teams: [team({ grants: [grant(SUPPORT.id, ['A'])] })],
          }),
        );

        expect(el().querySelector('.danger .field-hint')?.textContent).toContain(
          'Used by 3 grants',
        );
      });

      it('is enabled again, with no hint, once nothing holds the role', async () => {
        await open(
          `/admin/roles/${SUPPORT.id}`,
          adminData({ teams: [team({ grants: [grant(SUPPORT.id)] })] }),
        );
        expect(unavailable(button('Delete role'))).toBe(true);

        const saving = store.saveTeam('t', {
          name: 'T',
          description: '',
          memberIds: [],
          grants: [],
        });
        write('PUT', '/api/admin/teams/t').flush(team({ grants: [] }));
        await settle();
        answerReload(http, adminData({ teams: [team({ grants: [] })] }));
        await saving;
        await settle();

        expect(unavailable(button('Delete role'))).toBe(false);
        expect(el().querySelector('.danger .field-hint')).toBeNull();
      });

      it("shows the API's refusal when the role turns out to be in use after all, and stays", async () => {
        await open(`/admin/roles/${SUPPORT.id}`);

        button('Delete role')!.click();
        await settle();
        button('Yes, delete')!.click();
        await settle();
        refuse(
          write('DELETE', `/api/admin/roles/${SUPPORT.id}`),
          409,
          problem('role_in_use', "The role 'Support' is still granted to a user or a team."),
        );
        // What the API refused for is a fact the page should show: the store reads the lists again.
        await reloaded(
          adminData({ teams: [team({ grants: [grant(SUPPORT.id, ['OrderSaga'])] })] }),
        );

        expect(banner('.banner--error[role="alert"]')?.textContent).toContain(
          "The role 'Support' is still granted",
        );
        expect(url()).toBe(`/admin/roles/${SUPPORT.id}`);
        expect(nameInput().value).toBe('Support');
        // The count and the Delete button follow: it is now unavailable, and says why.
        expect(unavailable(button('Delete role'))).toBe(true);
        expect(el().querySelector('.danger .field-hint')?.textContent).toContain('Used by 1 grant');
      });

      it('says a lost permission for a 403', async () => {
        await open(`/admin/roles/${SUPPORT.id}`);

        button('Delete role')!.click();
        await settle();
        button('Yes, delete')!.click();
        await settle();
        refuse(write('DELETE', `/api/admin/roles/${SUPPORT.id}`), 403, problem('forbidden', 'x'));
        await settle();

        expect(banner('.banner--error[role="alert"]')?.textContent).toContain(
          'You no longer have permission to manage access.',
        );
      });
    });
  });

  describe('a built-in role', () => {
    const OPERATOR = BUILT_IN_ROLES[1];

    beforeEach(async () => {
      await open(`/admin/roles/${OPERATOR_ID}`);
    });

    it('is shown read-only, with its permissions and a notice', () => {
      expect(el().querySelector('h2')?.textContent).toBe('Operator');
      expect(nameInput().value).toBe('Operator');
      expect(nameInput().readOnly).toBe(true);
      expect(descriptionInput().readOnly).toBe(true);
      expect(descriptionInput().value).toBe(OPERATOR.description);
      expect(checked()).toEqual(['sagas.view', 'sagas.data', 'sagas.retry']);
      expect(boxes().every((b) => b.disabled)).toBe(true);
      expect(banner('.banner--info')?.textContent).toContain('cannot be changed or deleted');
      expect(el().querySelector('.chip--muted')?.textContent).toBe('Built-in');
    });

    it('has no Save and no Delete', () => {
      expect(el().querySelector('button[type="submit"]')).toBeNull();
      expect(button('Save')).toBeUndefined();
      expect(button('Delete role')).toBeUndefined();
      expect(el().querySelector('app-confirm-button')).toBeNull();
    });

    it('sends nothing when the form is submitted', async () => {
      el()
        .querySelector('form')!
        .dispatchEvent(new Event('submit', { cancelable: true }));
      await settle();

      http.expectNone((r) => r.method !== 'GET');
      expect(banner()).toBeNull();
    });

    it('offers to duplicate it as a custom role, and to go back', async () => {
      const duplicate = button('Duplicate as custom role') as HTMLAnchorElement;
      expect(duplicate.getAttribute('href')).toBe(`/admin/roles/new?from=${OPERATOR_ID}`);
      expect((button('Back to the roles') as HTMLAnchorElement).getAttribute('href')).toBe(
        '/admin/roles',
      );

      duplicate.click();
      await settle();
      await settle();

      expect(url()).toBe(`/admin/roles/new?from=${OPERATOR_ID}`);
      expect(nameInput().value).toBe('Operator copy');
      expect(nameInput().readOnly).toBe(false);
      expect(checked()).toEqual(['sagas.view', 'sagas.data', 'sagas.retry']);
    });
  });

  describe('a role that is not there', () => {
    it('says "This no longer exists" and links back to the list, with no form', async () => {
      await open('/admin/roles/nobody');

      const alert = banner('.banner--warning[role="alert"]')!;
      expect(alert.textContent).toContain('This no longer exists');
      expect(alert.querySelector('a')?.getAttribute('href')).toBe('/admin/roles');
      expect(el().querySelector('form')).toBeNull();
    });

    it('says so when the role is deleted by someone else while the page is open', async () => {
      await open(`/admin/roles/${SUPPORT.id}`);
      expect(el().querySelector('form')).not.toBeNull();

      const reading = store.saveTeam('t', {
        name: 'T',
        description: '',
        memberIds: [],
        grants: [],
      });
      write('PUT', '/api/admin/teams/t').flush(team());
      await settle();
      answerReload(http, adminData({ roles: BUILT_IN_ROLES }));
      await reading;
      await settle();

      expect(banner('.banner--warning[role="alert"]')?.textContent).toContain(
        'This no longer exists',
      );
    });
  });

  describe('moving between roles', () => {
    it('fills the form from the role the URL names now, when the router reuses the page', async () => {
      await open(
        `/admin/roles/${SUPPORT.id}`,
        adminData({
          roles: [
            ...BUILT_IN_ROLES,
            SUPPORT,
            role({ id: 'two', name: 'Second', permissions: ['sagas.data'] }),
          ],
        }),
      );
      type(nameInput(), 'typed over');
      const page = harness.routeDebugElement!.componentInstance;

      await harness.navigateByUrl('/admin/roles/two');
      await settle();

      expect(harness.routeDebugElement!.componentInstance).toBe(page);
      expect(nameInput().value).toBe('Second');
      expect(checked()).toEqual(['sagas.data']);
      expect(el().querySelector('h2')?.textContent).toBe('Second');
    });

    it('starts from the other role when ?from= changes on the same page, and from nothing when it goes', async () => {
      await open(
        '/admin/roles/new?from=a',
        adminData({
          roles: [
            ...BUILT_IN_ROLES,
            role({ id: 'a', name: 'Alpha', permissions: ['sagas.view'] }),
            role({ id: 'b', name: 'Beta', permissions: ['sagas.data', 'sagas.retry'] }),
          ],
        }),
      );
      expect(nameInput().value).toBe('Alpha copy');
      const page = harness.routeDebugElement!.componentInstance;

      await harness.navigateByUrl('/admin/roles/new?from=b');
      await settle();
      expect(harness.routeDebugElement!.componentInstance).toBe(page);
      expect(nameInput().value).toBe('Beta copy');
      expect(checked()).toEqual(['sagas.data', 'sagas.retry']);

      await harness.navigateByUrl('/admin/roles/new');
      await settle();
      expect(nameInput().value).toBe('');
      expect(checked()).toEqual([]);
    });

    it('does not apply the answer to a save to the role the page shows by then', async () => {
      const data = adminData({
        roles: [
          ...BUILT_IN_ROLES,
          SUPPORT,
          role({ id: 'two', name: 'Second', permissions: ['sagas.data'] }),
        ],
      });
      await open(`/admin/roles/${SUPPORT.id}`, data);
      await save();
      const put = write('PUT', `/api/admin/roles/${SUPPORT.id}`);

      await harness.navigateByUrl('/admin/roles/two');
      await settle();
      put.flush(SUPPORT);
      await reloaded(data);

      expect(nameInput().value).toBe('Second');
      expect(banner('.banner--success')).toBeNull();
    });
  });

  describe('what is typed while a request runs', () => {
    it.each([
      ['while the save is on its way', 'put'],
      ['while the lists are read again after it', 'reload'],
    ] as const)('is not overwritten by the answer to a save, %s', async (_when, moment) => {
      await open(`/admin/roles/${SUPPORT.id}`);
      type(nameInput(), 'Support desk');
      await save();
      const put = write('PUT', `/api/admin/roles/${SUPPORT.id}`);
      const typeMore = () => {
        type(nameInput(), 'Support desk 2');
        tick('sagas.data');
      };
      if (moment === 'put') typeMore();
      put.flush(role({ name: 'Support desk' }));
      await settle();
      if (moment === 'reload') typeMore();
      answerReload(http, adminData({ roles: [...BUILT_IN_ROLES, role({ name: 'Support desk' })] }));
      await settle();

      // The user's draft stands, and is not announced as saved: it is not what the API stored.
      expect(nameInput().value).toBe('Support desk 2');
      expect(checked()).toEqual(['sagas.view', 'sagas.data', 'sagas.retry']);
      expect(banner('.banner--success')).toBeNull();
      expect(el().querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled).toBe(false);
    });

    it('is replaced by what the API stored when nothing was typed meanwhile', async () => {
      await open(`/admin/roles/${SUPPORT.id}`);
      type(nameInput(), '  Support desk ');
      await save();
      write('PUT', `/api/admin/roles/${SUPPORT.id}`).flush(role({ name: 'Support desk' }));
      await reloaded(adminData({ roles: [...BUILT_IN_ROLES, role({ name: 'Support desk' })] }));

      expect(nameInput().value).toBe('Support desk');
      expect(banner('.banner--success')).not.toBeNull();
    });
  });

  describe('Save and Delete exclude each other', () => {
    const submit = () => el().querySelector<HTMLButtonElement>('button[type="submit"]')!;

    it('does not ask about deleting while a save runs, and the question that is open lapses', async () => {
      await open(`/admin/roles/${SUPPORT.id}`);
      askAbleDelete().click();
      await settle();
      expect(el().querySelector('.confirm-prompt')).not.toBeNull();

      await save();

      expect(el().querySelector('.confirm-prompt')).toBeNull();
      expect(unavailable(button('Delete role'))).toBe(true);
      button('Delete role')!.click();
      await settle();
      expect(el().querySelector('.confirm-prompt')).toBeNull();

      write('PUT', `/api/admin/roles/${SUPPORT.id}`).flush(SUPPORT);
      await reloaded();
      expect(unavailable(button('Delete role'))).toBe(false);
    });

    it('does not save while a delete runs', async () => {
      await open(`/admin/roles/${SUPPORT.id}`);
      button('Delete role')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();

      expect(submit().disabled).toBe(true);
      el()
        .querySelector('form')!
        .dispatchEvent(new Event('submit', { cancelable: true }));
      await settle();
      http.expectNone((r) => r.method === 'PUT');

      write('DELETE', `/api/admin/roles/${SUPPORT.id}`).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      await reloaded(adminData({ roles: BUILT_IN_ROLES }));
    });

    it('does not delete while a save runs, even if the delete had been confirmed before it', async () => {
      await open(`/admin/roles/${SUPPORT.id}`);
      await save();

      // A delete asked for by other means than the buttons is refused all the same.
      const page = harness.routeDebugElement!.componentInstance as unknown as {
        remove(): Promise<void>;
      };
      await page.remove();

      http.expectNone((r) => r.method === 'DELETE');
      write('PUT', `/api/admin/roles/${SUPPORT.id}`).flush(SUPPORT);
      await reloaded();
    });
  });

  describe('leaving after a create or a delete', () => {
    /** A navigation that has not finished: what the page shows meanwhile is what the user sees. */
    function holdNavigation() {
      return vi
        .spyOn(TestBed.inject(Router), 'navigateByUrl')
        .mockImplementation(() => new Promise<boolean>(() => undefined));
    }

    it('keeps the form busy until the page has gone, so it cannot be used twice', async () => {
      await open('/admin/roles/new');
      type(nameInput(), 'Support');
      tick('sagas.view');
      const navigate = holdNavigation();

      await save();
      write('POST', '/api/admin/roles').flush(role());
      await reloaded();

      expect(navigate).toHaveBeenCalledWith('/admin/roles');
      const submit = el().querySelector<HTMLButtonElement>('button[type="submit"]')!;
      expect(submit.disabled).toBe(true);
      expect(submit.textContent?.trim()).toBe('Saving…');
    });

    it('does not turn into "This no longer exists" between the reload and the navigation of a delete', async () => {
      await open(`/admin/roles/${SUPPORT.id}`);
      const navigate = holdNavigation();

      button('Delete role')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();
      write('DELETE', `/api/admin/roles/${SUPPORT.id}`).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      // The lists no longer hold the role: the page is still the page of a role that is being deleted.
      await reloaded(adminData({ roles: BUILT_IN_ROLES }));

      expect(navigate).toHaveBeenCalledWith('/admin/roles');
      expect(banner('.banner--warning')).toBeNull();
      expect(el().querySelector('form')).not.toBeNull();
      expect(el().querySelector('h2')?.textContent).toBe('Support');
    });
  });

  describe('a duplicate name', () => {
    async function taken(): Promise<void> {
      await open('/admin/roles/new');
      type(nameInput(), 'Operator');
      tick('sagas.view');
      await save();
      refuse(
        write('POST', '/api/admin/roles'),
        409,
        problem('name_taken', "A role named 'Operator' already exists."),
      );
      await reloaded();
    }

    it('marks the name field as the one to change, tied to the banner that says why, and focuses it', async () => {
      await taken();

      const banner = el().querySelector('#role-failure');
      expect(banner?.textContent).toContain("A role named 'Operator' already exists.");
      expect(banner?.getAttribute('role')).toBe('alert');
      expect(nameInput().getAttribute('aria-invalid')).toBe('true');
      expect(nameInput().getAttribute('aria-describedby')).toBe('role-failure');
      expect(focused()).toBe(nameInput());
    });

    it('ends the mark when the name is changed, and does not mark the name for any other conflict', async () => {
      await taken();

      type(nameInput(), 'Operator 2');

      expect(nameInput().getAttribute('aria-invalid')).toBeNull();
      expect(nameInput().getAttribute('aria-describedby')).toBeNull();
    });

    it('leaves the name alone when the conflict is about something else', async () => {
      await open(`/admin/roles/${SUPPORT.id}`);
      await save();
      refuse(
        write('PUT', `/api/admin/roles/${SUPPORT.id}`),
        409,
        problem('last_administrator', LAST_ADMIN),
      );
      await reloaded();

      expect(nameInput().getAttribute('aria-invalid')).toBeNull();
      expect(nameInput().getAttribute('aria-describedby')).toBeNull();
    });
  });

  describe('when the page is replaced by "This no longer exists"', () => {
    it('moves focus to the way back, after a 404 on save', async () => {
      await open(`/admin/roles/${SUPPORT.id}`);
      nameInput().focus();
      await save();

      refuse(write('PUT', `/api/admin/roles/${SUPPORT.id}`), 404, { title: 'Not found' });
      await reloaded(adminData({ roles: BUILT_IN_ROLES }));

      expect(focused()).toBe(el().querySelector('.banner--warning a'));
    });

    it('says so for a 404 on Delete, too, and focuses the way back', async () => {
      await open(`/admin/roles/${SUPPORT.id}`);

      button('Delete role')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();
      refuse(write('DELETE', `/api/admin/roles/${SUPPORT.id}`), 404, { title: 'Not found' });
      await reloaded(adminData({ roles: BUILT_IN_ROLES }));

      expect(banner('.banner--warning[role="alert"]')?.textContent).toContain(
        'This no longer exists',
      );
      expect(el().querySelector('form')).toBeNull();
      expect(focused()).toBe(el().querySelector('.banner--warning a'));
      expect(url()).toBe(`/admin/roles/${SUPPORT.id}`);
    });

    it('moves focus there when the role is deleted by someone else while the page is open', async () => {
      await open(`/admin/roles/${SUPPORT.id}`);
      nameInput().focus();

      const reading = store.saveTeam('t', {
        name: 'T',
        description: '',
        memberIds: [],
        grants: [],
      });
      write('PUT', '/api/admin/teams/t').flush(team());
      await reloaded(adminData({ roles: BUILT_IN_ROLES }));
      await reading;

      expect(focused()).toBe(el().querySelector('.banner--warning a'));
    });
  });

  describe('a permission the catalogue does not list', () => {
    const WITH_EXPORT = (isBuiltIn = false) =>
      adminData({
        roles: [
          ...BUILT_IN_ROLES,
          role({ isBuiltIn, permissions: ['sagas.view', 'sagas.export'] }),
        ],
      });
    const unknownBox = () => box('sagas.export');

    it('is shown after the catalogue, checked and labelled with its key, so nothing is dropped unseen', async () => {
      await open(`/admin/roles/${SUPPORT.id}`, WITH_EXPORT());

      expect(boxes().map((b) => b.value)).toEqual([
        ...PERMISSIONS.map((p) => p.key),
        'sagas.export',
      ]);
      expect(checked()).toEqual(['sagas.view', 'sagas.export']);
      const entry = unknownBox().closest('.permission')!;
      expect(entry.querySelector('code')?.textContent).toBe('sagas.export');
      expect(entry.textContent).toContain("Not in this API's permission catalogue");
    });

    it('is sent with the rest, in the draft order after the catalogue ones, and the API says if it refuses', async () => {
      await open(`/admin/roles/${SUPPORT.id}`, WITH_EXPORT());
      tick('sagas.retry');

      await save();
      const put = write('PUT', `/api/admin/roles/${SUPPORT.id}`);
      expect(put.request.body.permissions).toEqual(['sagas.view', 'sagas.retry', 'sagas.export']);
      refuse(
        put,
        400,
        problem('validation', 'The request is not valid.', {
          'permissions[2]': ["'sagas.export' is not a permission."],
        }),
      );
      await settle();

      expect(message('permissions')).toBe("'sagas.export' is not a permission.");
    });

    it('can be unticked, and is then not sent', async () => {
      await open(`/admin/roles/${SUPPORT.id}`, WITH_EXPORT());

      tick('sagas.export', false);
      await settle();
      expect(checked()).toEqual(['sagas.view']);

      await save();
      const put = write('PUT', `/api/admin/roles/${SUPPORT.id}`);
      expect(put.request.body.permissions).toEqual(['sagas.view']);
      put.flush(role({ permissions: ['sagas.view'] }));
      await reloaded();
    });

    it('is shown on a built-in role too, which cannot be changed', async () => {
      await open(`/admin/roles/${SUPPORT.id}`, WITH_EXPORT(true));

      expect(unknownBox().checked).toBe(true);
      expect(unknownBox().disabled).toBe(true);
    });
  });

  describe('the live region of Saved', () => {
    it('is there from the start, and the text is put into it', async () => {
      await open(`/admin/roles/${SUPPORT.id}`);
      const region = el().querySelector('form')!.previousElementSibling as HTMLElement;
      expect(region.getAttribute('role')).toBe('status');
      expect(region.textContent?.trim()).toBe('');

      await save();
      write('PUT', `/api/admin/roles/${SUPPORT.id}`).flush(SUPPORT);
      await reloaded();

      expect(el().querySelector('form')!.previousElementSibling).toBe(region);
      expect(region.textContent).toContain('Saved.');
    });
  });

  describe('the reason Delete is unavailable', () => {
    it('describes the button, which stays on the tab order', async () => {
      await open(
        `/admin/roles/${SUPPORT.id}`,
        adminData({ teams: [team({ grants: [grant(SUPPORT.id)] })] }),
      );

      const remove = askAbleDelete();
      const hint = el().querySelector('#role-delete-hint')!;
      expect(hint.textContent).toContain('Used by 1 grant');
      expect(remove.getAttribute('aria-describedby')).toBe('role-delete-hint');
      expect(remove.disabled).toBe(false);
      remove.focus();
      expect(focused()).toBe(remove);
    });

    it('describes nothing when Delete is available', async () => {
      await open(`/admin/roles/${SUPPORT.id}`);

      expect(askAbleDelete().hasAttribute('aria-describedby')).toBe(false);
    });
  });
});
