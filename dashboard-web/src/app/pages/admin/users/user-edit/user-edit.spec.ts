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
  ADMINISTRATOR_ID,
  AdminData,
  OPERATOR_ID,
  PERMISSIONS,
  VIEWER_ID,
  adminData,
  adminUser,
  answerLoad,
  answerReload,
  grant,
  role,
  team,
} from '../../../../testing/admin';
import { flat, quoted } from '../../../../testing/admin-tour';
import { AuthMockOptions, createAuthMock, provideAuthMock } from '../../../../testing/auth-mock';
import createUserRequest from '../../../../testing/contracts/admin/create-user.request.json';
import resetPasswordRequest from '../../../../testing/contracts/admin/reset-password.request.json';
import updateUserRequest from '../../../../testing/contracts/admin/update-user.request.json';
import { problem } from '../../../../testing/http-error';
import { AdminStore } from '../../admin.store';
import { UserEdit } from './user-edit';

@Component({
  selector: 'app-list-stub',
  template: 'the users',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class ListStub {}

const ROUTES: Routes = [
  {
    path: 'admin/users',
    children: [
      { path: '', component: ListStub },
      { path: 'new', component: UserEdit },
      { path: ':id', component: UserEdit },
    ],
  },
];

const FUTURE = '2099-01-01T00:00:00+00:00';
const LAST_ADMIN =
  'This change would leave no enabled user who can manage access for all saga types.';
const SIGNED_IN = {
  id: 'u-admin',
  username: 'admin',
  displayName: 'Administrator',
  mustChangePassword: false,
};

/** Alice of `adminData()`: Operator for OrderSaga, in the Payments team, which holds Viewer for every saga type. */
const ALICE = adminData().users[1];
const ADMIN = adminData().users[0];

describe('UserEdit', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;
  let store: AdminStore;

  async function open(
    url: string,
    data: AdminData = adminData(),
    auth: AuthMockOptions = {},
  ): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(ROUTES),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAuthMock(createAuthMock({ user: SIGNED_IN, ...auth })),
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

  afterEach(() => {
    vi.useRealTimers();
    http.verify();
  });

  /** Lets promises finish and the model write its values to the inputs, then renders. */
  async function settle(): Promise<void> {
    // Under fake timers a real timeout would never come: let the promises run and the faked ones due now.
    if (vi.isFakeTimers()) await vi.advanceTimersByTimeAsync(0);
    else await new Promise((resolve) => setTimeout(resolve));
    harness.detectChanges();
    await harness.fixture.whenStable();
  }

  const el = () => harness.routeNativeElement as HTMLElement;
  const url = () => TestBed.inject(Router).url;
  const focused = () => document.activeElement;
  const field = (id: string) => el().querySelector<HTMLInputElement>(`#${id}`)!;
  const username = () => field('user-username');
  const displayName = () => field('user-display-name');
  const password = () => field('user-password');
  const confirmation = () => field('user-confirmation');
  const enabled = () => el().querySelector<HTMLInputElement>('input[name="isEnabled"]')!;
  const requireChange = () =>
    el().querySelector<HTMLInputElement>('form.card input[name="mustChangePassword"]')!;
  const message = (id: string) => el().querySelector(`#${id}`)?.textContent?.trim();
  const banner = (selector = '.banner--error') => el().querySelector<HTMLElement>(selector);
  const button = (label: string) =>
    Array.from(el().querySelectorAll<HTMLElement>('button, a.btn')).find(
      (b) => b.textContent?.trim() === label,
    );
  const submit = () => el().querySelector<HTMLButtonElement>('form.card button[type="submit"]')!;
  /** Whether a button says it is unavailable (aria-disabled, as the confirm button does: it stays focusable). */
  const unavailable = (target: HTMLElement | undefined) =>
    target?.getAttribute('aria-disabled') === 'true';

  function type(target: HTMLInputElement, value: string): void {
    target.value = value;
    target.dispatchEvent(new Event('input'));
    harness.detectChanges();
  }

  function check(target: HTMLInputElement, on: boolean): void {
    target.checked = on;
    target.dispatchEvent(new Event('change'));
    harness.detectChanges();
  }

  async function save(): Promise<void> {
    submit().click();
    await settle();
  }

  /** The write the page sent. Returns the request before it is answered. */
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
  const noContent = (req: TestRequest) =>
    req.flush(null, { status: 204, statusText: 'No Content' });

  // The grants editor, through the page.
  const editor = () => el().querySelector('app-grants-editor') as HTMLElement;
  const grantGroups = () => Array.from(editor().querySelectorAll<HTMLElement>('.grant'));
  const grantSelect = (i: number) => grantGroups()[i].querySelector<HTMLSelectElement>('select')!;
  const grantRoles = () => grantGroups().map((_, i) => grantSelect(i).value);
  const grantBox = (i: number, name: string) =>
    Array.from(grantGroups()[i].querySelectorAll<HTMLInputElement>('.types input')).find(
      (b) => b.closest('label')?.textContent?.trim() === name,
    )!;
  const scopeRadio = (i: number, label: string) =>
    Array.from(grantGroups()[i].querySelectorAll<HTMLLabelElement>('.scope label'))
      .find((l) => l.textContent?.trim() === label)!
      .querySelector('input')!;

  async function addGrant(roleId: string, scope: 'all' | string[]): Promise<void> {
    const before = grantGroups().length;
    el().querySelector<HTMLButtonElement>('app-grants-editor .add button')!.click();
    await settle();
    const select = grantSelect(before);
    select.value = roleId;
    select.dispatchEvent(new Event('change'));
    await settle();
    if (scope === 'all') {
      scopeRadio(before, 'All saga types').click();
      await settle();
    } else {
      for (const name of scope) {
        check(grantBox(before, name), true);
      }
      await settle();
    }
  }

  async function removeGrant(i: number): Promise<void> {
    Array.from(grantGroups()[i].querySelectorAll('button'))
      .find((b) => b.textContent?.trim() === 'Remove')!
      .click();
    await settle();
  }

  /** The access preview as lines: the saga types, and each permission with what grants it. */
  function preview() {
    return Array.from(el().querySelectorAll('app-access-summary tbody tr')).map((row) => ({
      scope: row.querySelector('td')?.textContent?.trim(),
      permissions: Array.from(row.querySelectorAll('li')).map((li) => [
        li.querySelector('.chip')?.textContent?.trim(),
        li.querySelector('.origins')?.textContent?.trim(),
      ]),
    }));
  }

  describe('a new user', () => {
    it('starts empty: a username, a display name, a password with its confirmation, and a change at next sign-in, on', async () => {
      await open('/admin/users/new');

      expect(el().querySelector('h2')?.textContent).toBe('New user');
      expect(username().value).toBe('');
      expect(displayName().value).toBe('');
      expect(password().value).toBe('');
      expect(password().type).toBe('password');
      expect(confirmation().value).toBe('');
      expect(confirmation().type).toBe('password');
      expect(requireChange().checked).toBe(true);
      expect(button('Create user')).toBeDefined();
      expect((button('Cancel') as HTMLAnchorElement).getAttribute('href')).toBe('/admin/users');
      // Nothing that belongs to a user that exists: no Enabled (a new user is enabled), no actions.
      expect(enabled()).toBeNull();
      expect(button('Reset password')).toBeUndefined();
      expect(button('Delete user')).toBeUndefined();
      expect(el().querySelector('.chip')).toBeNull();
      expect(grantGroups()).toHaveLength(0);
    });

    it('sets the attributes of the fields: limits, autocomplete, and the policy minimum from the session', async () => {
      await open('/admin/users/new');

      expect(username().getAttribute('maxlength')).toBe('64');
      expect(username().getAttribute('autocomplete')).toBe('off');
      expect(displayName().getAttribute('maxlength')).toBe('128');
      expect(password().getAttribute('maxlength')).toBe('128');
      expect(password().getAttribute('minlength')).toBe('12');
      expect(password().getAttribute('autocomplete')).toBe('new-password');
      expect(message('user-password-hint')).toContain('At least 12 characters.');
    });

    it('says teams are added from the team, since a user payload has no teams', async () => {
      await open('/admin/users/new');

      expect(el().textContent).toContain('add the user to a team once the user exists');
      expect(el().querySelector('input[name="teamIds"], .teams')).toBeNull();
    });

    describe('Create user', () => {
      async function fixtureDraft(): Promise<void> {
        type(username(), createUserRequest.username);
        type(displayName(), createUserRequest.displayName);
        type(password(), createUserRequest.password);
        type(confirmation(), createUserRequest.password);
        await addGrant(VIEWER_ID, 'all');
        await addGrant(OPERATOR_ID, ['OrderSaga', 'PaymentSaga']);
      }

      it('sends exactly create-user.request.json for the fixture draft, then goes to the list', async () => {
        await open('/admin/users/new');
        await fixtureDraft();

        await save();
        const post = write('POST', '/api/admin/users');
        expect(post.request.body).toEqual(createUserRequest);
        post.flush(adminUser({ id: 'u-new', username: 'alice' }));
        await reloaded();

        expect(url()).toBe('/admin/users');
      });

      it('sends no enabled, no teamIds and no confirmation: the members of the API request only', async () => {
        await open('/admin/users/new');
        await fixtureDraft();

        await save();

        const post = write('POST', '/api/admin/users');
        expect(Object.keys(post.request.body as object).sort()).toEqual(
          ['displayName', 'grants', 'mustChangePassword', 'password', 'username'].sort(),
        );
        post.flush(adminUser());
        await reloaded();
      });

      it('trims the username and the display name, and sends the password as typed', async () => {
        await open('/admin/users/new');
        type(username(), '  alice ');
        type(displayName(), '  Alice Example ');
        type(password(), ' spaces kept around ');
        type(confirmation(), ' spaces kept around ');

        await save();

        const post = write('POST', '/api/admin/users');
        expect(post.request.body).toMatchObject({
          username: 'alice',
          displayName: 'Alice Example',
          password: ' spaces kept around ',
        });
        post.flush(adminUser());
        await reloaded();
      });

      it('sends mustChangePassword false when the change at next sign-in is turned off', async () => {
        await open('/admin/users/new');
        type(username(), 'alice');
        type(displayName(), 'Alice');
        type(password(), 'a long enough password');
        type(confirmation(), 'a long enough password');
        check(requireChange(), false);

        await save();

        const post = write('POST', '/api/admin/users');
        expect(post.request.body).toMatchObject({ mustChangePassword: false });
        post.flush(adminUser());
        await reloaded();
      });

      it('sends a grant for every saga type with no saga type, and a user with no grants with an empty list', async () => {
        await open('/admin/users/new');
        type(username(), 'alice');
        type(displayName(), 'Alice');
        type(password(), 'a long enough password');
        type(confirmation(), 'a long enough password');

        await save();

        const post = write('POST', '/api/admin/users');
        expect((post.request.body as { grants: unknown }).grants).toEqual([]);
        post.flush(adminUser());
        await reloaded();
      });

      it('shows the work: the button is disabled and says so, and a second submit sends nothing', async () => {
        await open('/admin/users/new');
        await fixtureDraft();

        await save();
        expect(unavailable(submit())).toBe(true);
        expect(submit().textContent?.trim()).toBe('Saving…');
        el()
          .querySelector('form.card')!
          .dispatchEvent(new Event('submit', { cancelable: true }));
        await settle();

        write('POST', '/api/admin/users').flush(adminUser());
        await reloaded();
      });

      it('goes back to the list for Cancel, with nothing sent', async () => {
        await open('/admin/users/new');
        type(username(), 'alice');

        (button('Cancel') as HTMLAnchorElement).click();
        await settle();

        expect(url()).toBe('/admin/users');
        http.expectNone((r) => r.method !== 'GET');
      });
    });

    describe('the checks made before anything is sent', () => {
      it('asks for a username, a display name and a password, under their fields, and sends nothing', async () => {
        await open('/admin/users/new');

        await save();

        expect(message('user-username-error')).toBe('Enter a username.');
        expect(message('user-display-name-error')).toBe('Enter a display name.');
        expect(message('user-password-error')).toBe('Enter a password.');
        http.expectNone((r) => r.method !== 'GET');
      });

      it('marks the fields: aria-invalid, aria-describedby, and the message beside them', async () => {
        await open('/admin/users/new');
        await save();

        expect(username().getAttribute('aria-invalid')).toBe('true');
        expect(username().getAttribute('aria-describedby')).toContain('user-username-error');
        expect(el().querySelector('#user-username-error')?.classList).toContain('field-error');
        expect(displayName().getAttribute('aria-invalid')).toBe('true');
        expect(password().getAttribute('aria-describedby')).toContain('user-password-error');
        // Not asked for before it is attempted, and not wrong when the passwords match (both empty).
        expect(confirmation().getAttribute('aria-invalid')).toBeNull();
      });

      it('moves focus to the first field with an error, and then the next', async () => {
        await open('/admin/users/new');
        await save();
        expect(focused()).toBe(username());

        type(username(), 'alice');
        await save();
        expect(focused()).toBe(displayName());

        type(displayName(), 'Alice');
        await save();
        expect(focused()).toBe(password());
      });

      it('shows nothing before the first save, and follows the draft after it', async () => {
        await open('/admin/users/new');
        expect(el().querySelector('.field-error')).toBeNull();

        await save();
        type(username(), 'alice');
        type(displayName(), 'Alice');
        type(password(), 'a long enough password');
        type(confirmation(), 'a long enough password');
        await settle();

        expect(el().querySelector('.field-error')).toBeNull();
      });

      it('refuses a password shorter than the policy, and one that does not match its confirmation', async () => {
        await open('/admin/users/new');
        type(username(), 'alice');
        type(displayName(), 'Alice');
        type(password(), 'short');
        type(confirmation(), 'shorter');

        await save();

        expect(message('user-password-error')).toBe('Use at least 12 characters.');
        expect(message('user-confirmation-error')).toBe('The passwords do not match.');
        expect(focused()).toBe(password());
        http.expectNone((r) => r.method !== 'GET');
      });

      it('refuses a username and a display name over the API limits, which maxlength only stops from typing', async () => {
        await open('/admin/users/new');
        type(username(), 'u'.repeat(65));
        type(displayName(), 'n'.repeat(129));
        type(password(), 'a long enough password');
        type(confirmation(), 'a long enough password');

        await save();

        expect(message('user-username-error')).toBe('Use at most 64 characters.');
        expect(message('user-display-name-error')).toBe('Use at most 128 characters.');
      });

      it('treats a blank display name as none', async () => {
        await open('/admin/users/new');
        type(username(), 'alice');
        type(displayName(), '   ');

        await save();

        expect(message('user-display-name-error')).toBe('Enter a display name.');
      });

      it('leaves the format of a username to the API, whose words are the ones shown', async () => {
        await open('/admin/users/new');
        type(username(), 'a b');
        type(displayName(), 'Alice');
        type(password(), 'a long enough password');
        type(confirmation(), 'a long enough password');

        await save();

        const post = write('POST', '/api/admin/users');
        expect(post.request.body).toMatchObject({ username: 'a b' });
        refuse(
          post,
          400,
          problem('validation', 'x', {
            username: [
              'Use 3 to 64 letters, digits or . _ @ + -, starting with a letter or a digit.',
            ],
          }),
        );
        await settle();
        expect(message('user-username-error')).toContain('Use 3 to 64 letters');
      });
    });

    describe('a scoped grant that names no saga type', () => {
      it('blocks Save, says "Pick at least one saga type" with the grant, and moves focus to its saga types', async () => {
        await open('/admin/users/new');
        type(username(), 'alice');
        type(displayName(), 'Alice');
        type(password(), 'a long enough password');
        type(confirmation(), 'a long enough password');
        el().querySelector<HTMLButtonElement>('app-grants-editor .add button')!.click();
        await settle();
        expect(editor().textContent).toContain('Pick at least one saga type');

        await save();

        http.expectNone((r) => r.method !== 'GET');
        expect(editor().textContent).toContain('Pick at least one saga type');
        expect(focused()).toBe(grantBox(0, 'OrderSaga'));
        expect(url()).toBe('/admin/users/new');
      });

      it('is sent once a saga type is picked', async () => {
        await open('/admin/users/new');
        type(username(), 'alice');
        type(displayName(), 'Alice');
        type(password(), 'a long enough password');
        type(confirmation(), 'a long enough password');
        await addGrant(OPERATOR_ID, []);
        await save();
        http.expectNone((r) => r.method !== 'GET');

        check(grantBox(0, 'PaymentSaga'), true);
        await save();

        const post = write('POST', '/api/admin/users');
        expect((post.request.body as { grants: unknown }).grants).toEqual([
          grant(OPERATOR_ID, ['PaymentSaga']),
        ]);
        post.flush(adminUser());
        await reloaded();
      });
    });

    describe('what the API refuses', () => {
      async function filled(): Promise<void> {
        await open('/admin/users/new');
        type(username(), 'alice');
        type(displayName(), 'Alice');
        type(password(), 'a long enough password');
        type(confirmation(), 'a long enough password');
        await addGrant(OPERATOR_ID, ['OrderSaga']);
        await addGrant(VIEWER_ID, 'all');
        await save();
      }

      it('puts the messages of a 400 under their fields, the grants ones with their grant, keeps the draft and focuses the first', async () => {
        await filled();

        refuse(
          write('POST', '/api/admin/users'),
          400,
          problem('validation', 'The request is not valid.', {
            username: [
              'Use 3 to 64 letters, digits or . _ @ + -, starting with a letter or a digit.',
            ],
            password: ['Use 12 to 128 characters.'],
            'grants[1].roleId': ['No role has this id.'],
            'grants[0].sagaTypes': ["'OrderSaga' is listed twice."],
          }),
        );
        await settle();

        expect(message('user-username-error')).toContain('Use 3 to 64 letters');
        expect(message('user-password-error')).toBe('Use 12 to 128 characters.');
        const rows = grantGroups().map((g) => g.textContent?.replace(/\s+/g, ' '));
        expect(rows[0]).toContain("'OrderSaga' is listed twice.");
        expect(rows[0]).not.toContain('No role has this id.');
        expect(rows[1]).toContain('No role has this id.');
        expect(rows[1]).not.toContain('listed twice');
        expect(banner()).toBeNull();
        // The draft is untouched, passwords included.
        expect(username().value).toBe('alice');
        expect(password().value).toBe('a long enough password');
        expect(grantRoles()).toEqual([OPERATOR_ID, VIEWER_ID]);
        expect(focused()).toBe(username());
        expect(url()).toBe('/admin/users/new');
      });

      // The focus is put somewhere else before the answer: that it ends up on the grant is the page's doing, not
      // where it happened to be (the grants editor focuses the new grant's role when a grant is added).
      it("moves focus to the grant's saga types when a grant's saga types are the only thing wrong", async () => {
        await filled();
        displayName().focus();
        expect(focused()).toBe(displayName());

        refuse(
          write('POST', '/api/admin/users'),
          400,
          problem('validation', 'x', {
            'grants[0].sagaTypes': ['Name 1 to 100 saga types, or grant all saga types.'],
          }),
        );
        await settle();

        expect(focused()).toBe(grantBox(0, 'OrderSaga'));
      });

      it("moves focus to the grant's role when its role is the only thing wrong", async () => {
        await filled();
        displayName().focus();

        refuse(
          write('POST', '/api/admin/users'),
          400,
          problem('validation', 'x', { 'grants[1].roleId': ['No role has this id.'] }),
        );
        await settle();

        expect(focused()).toBe(grantSelect(1));
      });

      it('moves focus to the grant with a problem on the update page too', async () => {
        await open('/admin/users/u-alice');
        await save();
        displayName().focus();

        refuse(
          write('PUT', '/api/admin/users/u-alice'),
          400,
          problem('validation', 'x', {
            'grants[0].sagaTypes': ['Name 1 to 100 saga types, or grant all saga types.'],
          }),
        );
        await settle();

        expect(focused()).toBe(grantBox(0, 'OrderSaga'));
      });

      // m1: the draft changed while the request ran.
      it('says what the API said in the banner, not on the fields or the grants, when the draft was changed while the request ran, and leaves the focus', async () => {
        await filled();
        type(displayName(), 'Changed meanwhile');
        displayName().focus();

        refuse(
          write('POST', '/api/admin/users'),
          400,
          problem('validation', 'x', {
            username: [
              'Use 3 to 64 letters, digits or . _ @ + -, starting with a letter or a digit.',
            ],
            'grants[0].sagaTypes': ['Name 1 to 100 saga types, or grant all saga types.'],
          }),
        );
        await settle();

        expect(message('user-username-error')).toBeUndefined();
        expect(username().getAttribute('aria-invalid')).toBeNull();
        expect(editor().textContent).not.toContain('Name 1 to 100 saga types');
        const alert = banner('.banner--error[role="alert"]')!;
        expect(alert.textContent).toContain('Use 3 to 64 letters');
        expect(alert.textContent).toContain('Name 1 to 100 saga types, or grant all saga types.');
        expect(alert.textContent).toContain('About what was sent, which you have changed since.');
        expect(focused()).toBe(displayName());
        expect(displayName().value).toBe('Changed meanwhile');
        expect(unavailable(submit())).toBe(false);
      });

      it('does not put a message about a grant on another grant when a grant was removed while the request ran', async () => {
        await filled();
        await removeGrant(0);

        refuse(
          write('POST', '/api/admin/users'),
          400,
          problem('validation', 'x', { 'grants[1].roleId': ['No role has this id.'] }),
        );
        await settle();

        // One grant is left, at position 0; the message was about the one that was at 1.
        expect(editor().querySelector('.field-error')).toBeNull();
        expect(banner('.banner--error[role="alert"]')?.textContent).toContain(
          'No role has this id.',
        );
      });

      it('does not mark the username as taken when it was changed while the request ran', async () => {
        await filled();
        type(username(), 'alice2');

        refuse(
          write('POST', '/api/admin/users'),
          409,
          problem('username_taken', "A user named 'alice' already exists."),
        );
        await reloaded();

        expect(el().querySelector('#user-failure')?.textContent).toContain('already exists');
        expect(username().getAttribute('aria-invalid')).toBeNull();
        expect(username().getAttribute('aria-describedby')).not.toContain('user-failure');
      });

      it('shows a refusal that is not about fields as it was, whether or not the draft changed', async () => {
        await filled();
        type(displayName(), 'Changed meanwhile');

        refuse(write('POST', '/api/admin/users'), 500, problem('x', 'server words'));
        await settle();

        expect(banner('.banner--error[role="alert"]')?.textContent).toContain('HTTP 500');
      });

      it('sends the draft again when Save is used again with nothing changed since a 400: the old messages do not block it', async () => {
        await filled();
        refuse(
          write('POST', '/api/admin/users'),
          400,
          problem('validation', 'x', { username: ['Bad.'] }),
        );
        await settle();
        expect(message('user-username-error')).toBe('Bad.');

        await save();

        // The message was about the value that was sent; the API is asked about it again, and the message is gone meanwhile.
        const post = write('POST', '/api/admin/users');
        expect(message('user-username-error')).toBeUndefined();
        post.flush(adminUser());
        await reloaded();
      });

      it('ends the messages about the grants as soon as the grants change', async () => {
        await filled();
        refuse(
          write('POST', '/api/admin/users'),
          400,
          problem('validation', 'x', { 'grants[1].roleId': ['No role has this id.'] }),
        );
        await settle();
        expect(editor().textContent).toContain('No role has this id.');

        await removeGrant(0);

        expect(editor().textContent).not.toContain('No role has this id.');
      });

      it("ends a field's message when that field changes, and leaves the others", async () => {
        await filled();
        refuse(
          write('POST', '/api/admin/users'),
          400,
          problem('validation', 'x', { username: ['Bad.'], displayName: ['Worse.'] }),
        );
        await settle();

        type(username(), 'alice2');

        expect(message('user-username-error')).toBeUndefined();
        expect(message('user-display-name-error')).toBe('Worse.');
      });

      it('lists the messages of a path it has no field for in the banner', async () => {
        await filled();

        refuse(
          write('POST', '/api/admin/users'),
          400,
          problem('validation', 'x', { mustChangePassword: ['Not a flag.'] }),
        );
        await settle();

        expect(banner('.banner--error[role="alert"]')?.textContent).toContain('Not a flag.');
      });

      it('shows a taken username as a banner with the server detail, marks the username and focuses it', async () => {
        await filled();

        refuse(
          write('POST', '/api/admin/users'),
          409,
          problem(
            'username_taken',
            "A user named 'alice' already exists; names are compared ignoring case.",
          ),
        );
        await reloaded();

        const alert = el().querySelector('#user-failure')!;
        expect(alert.textContent).toContain("A user named 'alice' already exists");
        expect(alert.getAttribute('role')).toBe('alert');
        expect(username().getAttribute('aria-invalid')).toBe('true');
        expect(username().getAttribute('aria-describedby')).toContain('user-failure');
        expect(focused()).toBe(username());
        expect(username().value).toBe('alice');
        expect(url()).toBe('/admin/users/new');
        expect(unavailable(submit())).toBe(false);
      });

      it('ends the mark on the username when it is changed', async () => {
        await filled();
        refuse(
          write('POST', '/api/admin/users'),
          409,
          problem('username_taken', "A user named 'alice' already exists."),
        );
        await reloaded();

        type(username(), 'alice2');

        expect(username().getAttribute('aria-invalid')).toBeNull();
        expect(username().getAttribute('aria-describedby')).not.toContain('user-failure');
      });

      it.each([
        [403, 'You no longer have permission to manage access.'],
        [500, 'HTTP 500'],
      ])('says a %i in its own words and keeps the draft', async (status, text) => {
        await filled();

        refuse(write('POST', '/api/admin/users'), status, problem('x', 'server words'));
        await settle();

        expect(banner('.banner--error[role="alert"]')?.textContent).toContain(text);
        expect(username().value).toBe('alice');
        expect(username().getAttribute('aria-invalid')).toBeNull();
      });
    });

    describe('leaving after a create', () => {
      it('keeps the form busy until the page has gone, so it cannot be used twice, and forgets the passwords', async () => {
        await open('/admin/users/new');
        type(username(), 'alice');
        type(displayName(), 'Alice');
        type(password(), 'a long enough password');
        type(confirmation(), 'a long enough password');
        const navigate = vi
          .spyOn(TestBed.inject(Router), 'navigateByUrl')
          .mockImplementation(() => new Promise<boolean>(() => undefined));

        await save();
        write('POST', '/api/admin/users').flush(adminUser());
        await reloaded();

        expect(navigate).toHaveBeenCalledWith('/admin/users');
        expect(unavailable(submit())).toBe(true);
        expect(submit().textContent?.trim()).toBe('Saving…');
        expect(password().value).toBe('');
        expect(confirmation().value).toBe('');
      });
    });
  });

  describe('an existing user', () => {
    it('is filled in from the store: the username as the heading, not as a field, and the display name', async () => {
      await open('/admin/users/u-alice');

      expect(el().querySelector('h2')?.textContent).toBe('alice');
      expect(username()).toBeNull();
      expect(displayName().value).toBe('Alice Example');
      expect(enabled().checked).toBe(true);
      // The password is only for a new user, and reset below.
      expect(password()).toBeNull();
      expect(confirmation()).toBeNull();
      expect(requireChange()).toBeNull();
      expect(button('Save')).toBeDefined();
      // Not called Cancel: the question of Delete user has a Cancel of its own.
      expect(button('Cancel')).toBeUndefined();
      expect((button('Back to the users') as HTMLAnchorElement).getAttribute('href')).toBe(
        '/admin/users',
      );
      expect(button('Delete user')).toBeDefined();
      expect(button('Reset password')).toBeDefined();
    });

    it('shows the grants of the user, with their saga types', async () => {
      await open('/admin/users/u-alice');

      expect(grantGroups()).toHaveLength(1);
      expect(grantSelect(0).value).toBe(OPERATOR_ID);
      expect(scopeRadio(0, 'Selected saga types').checked).toBe(true);
      expect(grantBox(0, 'OrderSaga').checked).toBe(true);
      expect(grantBox(0, 'PaymentSaga').checked).toBe(false);
    });

    it('shows a chip for what is the matter with the account', async () => {
      await open(
        '/admin/users/u-alice',
        adminData({
          users: [
            ADMIN,
            adminUser({
              ...ALICE,
              isEnabled: false,
              mustChangePassword: true,
              lockedUntilUtc: FUTURE,
            }),
          ],
        }),
      );

      expect(
        Array.from(el().querySelectorAll('.page-header .chip')).map((c) => c.textContent),
      ).toEqual(['Disabled', 'Locked', 'Must change password']);
    });

    it('shows no chip for an account with nothing the matter', async () => {
      await open('/admin/users/u-alice');

      expect(el().querySelector('.page-header .chip')).toBeNull();
    });

    describe('Save', () => {
      async function fixtureDraft(): Promise<void> {
        type(displayName(), updateUserRequest.displayName);
        check(enabled(), false);
        await removeGrant(0);
        await addGrant(VIEWER_ID, 'all');
      }

      it('sends exactly update-user.request.json for the fixture draft', async () => {
        await open('/admin/users/u-alice');
        await fixtureDraft();

        await save();

        const put = write('PUT', '/api/admin/users/u-alice');
        expect(put.request.body).toEqual(updateUserRequest);
        put.flush(
          adminUser({ ...ALICE, displayName: updateUserRequest.displayName, isEnabled: false }),
        );
        await reloaded();
      });

      it("sends no teamIds, no username and no password, whatever the user has: team membership is the team's", async () => {
        await open(
          '/admin/users/u-alice',
          adminData({ users: [ADMIN, adminUser({ ...ALICE, teamIds: ['t-1'] })] }),
        );

        await save();

        const put = write('PUT', '/api/admin/users/u-alice');
        expect(Object.keys(put.request.body as object).sort()).toEqual([
          'displayName',
          'grants',
          'isEnabled',
        ]);
        put.flush(ALICE);
        await reloaded();
      });

      it('sends a grant for every saga type with no saga type, and the names trimmed', async () => {
        await open('/admin/users/u-alice');
        await addGrant(VIEWER_ID, 'all');

        await save();

        const put = write('PUT', '/api/admin/users/u-alice');
        expect((put.request.body as { grants: unknown }).grants).toEqual([
          grant(OPERATOR_ID, ['OrderSaga']),
          grant(VIEWER_ID),
        ]);
        put.flush(ALICE);
        await reloaded();
      });

      it('sends a grant for every saga type with no saga type, even when the record it started from names some', async () => {
        await open(
          '/admin/users/u-alice',
          adminData({
            users: [
              ADMIN,
              adminUser({
                ...ALICE,
                grants: [{ roleId: VIEWER_ID, allSagaTypes: true, sagaTypes: ['Leftover'] }],
              }),
            ],
          }),
        );

        await save();

        const put = write('PUT', '/api/admin/users/u-alice');
        expect((put.request.body as { grants: unknown }).grants).toEqual([grant(VIEWER_ID)]);
        put.flush(ALICE);
        await reloaded();
      });

      it('trims the display name', async () => {
        await open('/admin/users/u-alice');
        type(displayName(), '  Alice Changed ');

        await save();

        const put = write('PUT', '/api/admin/users/u-alice');
        expect(put.request.body).toMatchObject({ displayName: 'Alice Changed' });
        put.flush(ALICE);
        await reloaded();
      });

      it('shows what the API answered and says Saved, in a live region that was there before', async () => {
        await open('/admin/users/u-alice');
        const region = el().querySelector('form.card')!.previousElementSibling!;
        expect(region.getAttribute('role')).toBe('status');
        expect(region.textContent?.trim()).toBe('');
        type(displayName(), '  Alice Changed ');

        await save();
        write('PUT', '/api/admin/users/u-alice').flush({ ...ALICE, displayName: 'Alice Changed' });
        await reloaded(
          adminData({ users: [ADMIN, adminUser({ ...ALICE, displayName: 'Alice Changed' })] }),
        );

        expect(url()).toBe('/admin/users/u-alice');
        expect(displayName().value).toBe('Alice Changed');
        expect(banner('.banner--success')?.textContent).toContain('Saved.');
        expect(banner('.banner--success')?.closest('[role="status"]')).toBe(region);
      });

      it.each([
        ['the display name is typed in', () => type(displayName(), 'Other')],
        ['Enabled is changed', () => check(enabled(), false)],
        ['a saga type is ticked', () => check(grantBox(0, 'PaymentSaga'), true)],
        ['a grant is removed', () => undefined],
      ])('takes Saved away as soon as the draft changes again: %s', async (when, change) => {
        await open('/admin/users/u-alice');
        await save();
        write('PUT', '/api/admin/users/u-alice').flush(ALICE);
        await reloaded();
        expect(banner('.banner--success')).not.toBeNull();

        if (when === 'a grant is removed') await removeGrant(0);
        else change();
        await settle();

        expect(banner('.banner--success')).toBeNull();
      });

      it('keeps what is being typed when the store reads the lists again behind the page', async () => {
        await open('/admin/users/u-alice');
        type(displayName(), 'Half typed');

        const other = store.saveUser(null, {
          username: 'zed',
          displayName: 'Zed',
          password: 'a long enough password',
          mustChangePassword: true,
          grants: [],
        });
        write('POST', '/api/admin/users').flush(adminUser({ id: 'u-zed', username: 'zed' }));
        await settle();
        answerReload(
          http,
          adminData({ users: [ADMIN, ALICE, adminUser({ id: 'u-zed', username: 'zed' })] }),
        );
        await other;
        await settle();

        expect(displayName().value).toBe('Half typed');
      });

      it('refuses a blank display name before sending', async () => {
        await open('/admin/users/u-alice');
        type(displayName(), '  ');

        await save();

        expect(message('user-display-name-error')).toBe('Enter a display name.');
        expect(focused()).toBe(displayName());
        http.expectNone((r) => r.method !== 'GET');
      });

      it('blocks Save on a scoped grant with no saga type, and says so with the grant', async () => {
        await open('/admin/users/u-alice');
        check(grantBox(0, 'OrderSaga'), false);
        await settle();

        await save();

        http.expectNone((r) => r.method !== 'GET');
        expect(editor().textContent).toContain('Pick at least one saga type');
        expect(focused()).toBe(grantBox(0, 'OrderSaga'));
      });
    });

    describe('what the API refuses', () => {
      it('puts the messages of a 400 under their fields, the grants ones with their grant, and keeps the draft', async () => {
        await open('/admin/users/u-alice');
        type(displayName(), 'Changed');
        await save();

        refuse(
          write('PUT', '/api/admin/users/u-alice'),
          400,
          problem('validation', 'x', {
            displayName: ['Enter 1 to 128 characters, with no control characters.'],
            'grants[0].sagaTypes': ['Name 1 to 100 saga types, or grant all saga types.'],
          }),
        );
        await settle();

        expect(message('user-display-name-error')).toContain('Enter 1 to 128 characters');
        expect(grantGroups()[0].textContent).toContain('Name 1 to 100 saga types');
        expect(displayName().value).toBe('Changed');
        expect(focused()).toBe(displayName());
        expect(banner()).toBeNull();
      });

      it('keeps the draft and says why for a last-administrator refusal when the last administrator is disabled', async () => {
        await open('/admin/users/u-alice');
        type(displayName(), 'Changed');
        check(enabled(), false);
        await save();

        refuse(
          write('PUT', '/api/admin/users/u-alice'),
          409,
          problem('last_administrator', LAST_ADMIN),
        );
        await reloaded();

        const alert = banner('.banner--error[role="alert"]')!;
        expect(alert.textContent).toContain(LAST_ADMIN);
        expect(alert.textContent).toContain(
          'Give another enabled user an all-saga-types grant whose role includes access.manage, then try again.',
        );
        expect(displayName().value).toBe('Changed');
        expect(enabled().checked).toBe(false);
        expect(banner('.banner--success')).toBeNull();
        expect(unavailable(submit())).toBe(false);
      });

      it('keeps the draft for a last-administrator refusal when the last all-types administrator grant is removed', async () => {
        await open('/admin/users/u-admin');
        await removeGrant(0);
        await save();

        refuse(
          write('PUT', '/api/admin/users/u-admin'),
          409,
          problem('last_administrator', LAST_ADMIN),
        );
        await reloaded();

        expect(banner('.banner--error[role="alert"]')?.textContent).toContain(LAST_ADMIN);
        expect(grantGroups()).toHaveLength(0);
      });

      it('says "This no longer exists" for a 404, with the way back, and no form', async () => {
        await open('/admin/users/u-alice');
        await save();

        refuse(write('PUT', '/api/admin/users/u-alice'), 404, { title: 'Not found' });
        await settle();
        // The store reads the lists again after a 404, so the lists stop showing what is gone.
        answerReload(http, adminData({ users: [ADMIN] }));
        await settle();

        const alert = banner('.banner--warning[role="alert"]')!;
        expect(alert.textContent).toContain('This no longer exists');
        expect(alert.querySelector('a')?.getAttribute('href')).toBe('/admin/users');
        expect(el().querySelector('form')).toBeNull();
        expect(focused()).toBe(alert.querySelector('a'));
      });

      it.each([
        [403, 'You no longer have permission to manage access.'],
        [500, 'HTTP 500'],
      ])('says a %i in its own words and keeps the draft', async (status, text) => {
        await open('/admin/users/u-alice');
        type(displayName(), 'Changed');
        await save();

        refuse(write('PUT', '/api/admin/users/u-alice'), status, problem('x', 'server words'));
        await settle();

        expect(banner('.banner--error[role="alert"]')?.textContent).toContain(text);
        expect(displayName().value).toBe('Changed');
      });
    });

    describe('what is typed while a request runs', () => {
      it.each([
        ['while the save is on its way', 'put'],
        ['while the lists are read again after it', 'reload'],
      ] as const)('is not overwritten by the answer to a save, %s', async (_when, moment) => {
        await open('/admin/users/u-alice');
        type(displayName(), 'First');
        await save();
        const put = write('PUT', '/api/admin/users/u-alice');
        const typeMore = () => {
          type(displayName(), 'Second');
          check(grantBox(0, 'PaymentSaga'), true);
        };
        if (moment === 'put') typeMore();
        put.flush({ ...ALICE, displayName: 'First' });
        await settle();
        if (moment === 'reload') typeMore();
        answerReload(http, adminData({ users: [ADMIN, { ...ALICE, displayName: 'First' }] }));
        await settle();

        // The user's draft stands, and is not announced as saved: it is not what the API stored.
        expect(displayName().value).toBe('Second');
        expect(grantBox(0, 'PaymentSaga').checked).toBe(true);
        expect(banner('.banner--success')).toBeNull();
        expect(unavailable(submit())).toBe(false);
      });

      it('is replaced by what the API stored when nothing was typed meanwhile', async () => {
        await open('/admin/users/u-alice');
        type(displayName(), '  First ');
        await save();
        write('PUT', '/api/admin/users/u-alice').flush({ ...ALICE, displayName: 'First' });
        await reloaded(adminData({ users: [ADMIN, { ...ALICE, displayName: 'First' }] }));

        expect(displayName().value).toBe('First');
        expect(banner('.banner--success')).not.toBeNull();
      });

      it('is not overwritten when only the grants were changed meanwhile', async () => {
        await open('/admin/users/u-alice');
        await save();
        const put = write('PUT', '/api/admin/users/u-alice');

        await addGrant(VIEWER_ID, 'all');
        put.flush(ALICE);
        await reloaded();

        expect(grantRoles()).toEqual([OPERATOR_ID, VIEWER_ID]);
        expect(banner('.banner--success')).toBeNull();
      });
    });

    describe('the user the page shows', () => {
      it('is the one the URL names, when the router reuses the page for another', async () => {
        await open('/admin/users/u-alice');
        type(displayName(), 'typed over');
        const page = harness.routeDebugElement!.componentInstance;

        await harness.navigateByUrl('/admin/users/u-admin');
        await settle();

        expect(harness.routeDebugElement!.componentInstance).toBe(page);
        expect(el().querySelector('h2')?.textContent).toBe('admin');
        expect(displayName().value).toBe('Administrator');
        expect(grantSelect(0).value).toBe(ADMINISTRATOR_ID);
      });

      it('does not apply the answer to a save to the user the page shows by then', async () => {
        await open('/admin/users/u-alice');
        await save();
        const put = write('PUT', '/api/admin/users/u-alice');

        await harness.navigateByUrl('/admin/users/u-admin');
        await settle();
        put.flush(ALICE);
        await reloaded();

        expect(displayName().value).toBe('Administrator');
        expect(banner('.banner--success')).toBeNull();
      });

      it('closes the reset form and forgets what it held when another user is shown', async () => {
        await open('/admin/users/u-alice');
        button('Reset password')!.click();
        await settle();
        type(field('user-new-password'), 'half typed');

        await harness.navigateByUrl('/admin/users/u-admin');
        await settle();

        expect(el().querySelector('#user-reset')).toBeNull();
        button('Reset password')!.click();
        await settle();
        expect(field('user-new-password').value).toBe('');
      });
    });
  });

  describe('a user that is not there', () => {
    it('says "This no longer exists" and links back to the list, with no form, and focuses the link', async () => {
      await open('/admin/users/nobody');

      const alert = banner('.banner--warning[role="alert"]')!;
      expect(alert.textContent).toContain('This no longer exists');
      expect(alert.querySelector('a')?.getAttribute('href')).toBe('/admin/users');
      expect(el().querySelector('form')).toBeNull();
      expect(focused()).toBe(alert.querySelector('a'));
    });

    it('says so when the user is deleted by someone else while the page is open', async () => {
      await open('/admin/users/u-alice');
      expect(el().querySelector('form')).not.toBeNull();

      const reading = store.deleteUser('u-zed');
      noContent(write('DELETE', '/api/admin/users/u-zed'));
      await settle();
      answerReload(http, adminData({ users: [ADMIN] }));
      await reading;
      await settle();

      expect(banner('.banner--warning[role="alert"]')?.textContent).toContain(
        'This no longer exists',
      );
    });
  });

  describe('the teams of the user', () => {
    it('are shown, each a link to the team, and cannot be changed here', async () => {
      await open('/admin/users/u-alice');

      const links = Array.from(el().querySelectorAll<HTMLAnchorElement>('.teams a'));
      expect(links.map((a) => [a.textContent, a.getAttribute('href')])).toEqual([
        ['Payments', `/admin/teams/${adminData().teams[0].id}`],
      ]);
      expect(el().textContent).toContain("Membership is changed on the team's page, not here.");
      // Nothing to tick: no checkbox in the form belongs to a team.
      expect(
        Array.from(el().querySelectorAll<HTMLInputElement>('form.card input[type="checkbox"]')).map(
          (box) => box.name || box.closest('label')?.textContent?.trim(),
        ),
      ).toEqual(['isEnabled', 'OrderSaga', 'PaymentSaga']);
    });

    it('say so when the user is in no team', async () => {
      await open('/admin/users/u-admin');

      expect(el().querySelector('.teams')).toBeNull();
      expect(el().textContent).toContain('Not in any team.');
    });

    it('lists several, and only those that name the user as a member', async () => {
      await open(
        '/admin/users/u-alice',
        adminData({
          teams: [
            team({ id: 't-1', name: 'Alpha', memberIds: ['u-alice'] }),
            team({ id: 't-2', name: 'Beta', memberIds: ['u-admin'] }),
            team({ id: 't-3', name: 'Gamma', memberIds: ['u-admin', 'u-alice'] }),
          ],
        }),
      );

      expect(Array.from(el().querySelectorAll('.teams a')).map((a) => a.textContent)).toEqual([
        'Alpha',
        'Gamma',
      ]);
    });
  });

  describe('the effective access', () => {
    it('labels the permissions with the names the API gives them, not the built-in ones', async () => {
      const permissions = PERMISSIONS.map((p) =>
        p.key === 'sagas.view' ? { ...p, name: 'See sagas' } : p,
      );
      await open('/admin/users/u-alice', adminData({ permissions }));

      const labels = preview().flatMap((row) => row.permissions.map(([label]) => label));
      expect(labels).toContain('See sagas');
      expect(labels).not.toContain('View sagas');
    });

    it('is headed, described and sourced as the administration tour says: Effective access, saved or not, direct: and team X: origins', async () => {
      await open('/admin/users/u-alice');

      const [heading] = quoted('admin-preview', /Effective access/);
      const [unsaved] = quoted('admin-preview', /saved or not/);
      const origins = quoted('admin-preview', /direct: \w+|team \w+: \w+/g);

      expect(flat(el().querySelector('#user-access-title'))).toBe(heading);
      expect(flat(el().querySelector('section.preview'))).toContain(unsaved);
      expect(origins).toEqual(['direct: Operator', 'team Payments: Viewer']);
      const written = flat(el().querySelector('app-access-summary'));
      for (const origin of origins) expect(written, origin).toContain(origin);
    });

    it('is what the grants and the teams confer, with what grants each permission', async () => {
      await open('/admin/users/u-alice');

      expect(preview()).toEqual([
        {
          scope: 'All saga types',
          permissions: [
            ['View sagas', 'team Payments: Viewer'],
            ['View saga data', 'team Payments: Viewer'],
          ],
        },
        {
          scope: 'OrderSaga',
          permissions: [
            ['View sagas', 'team Payments: Viewer; direct: Operator'],
            ['View saga data', 'team Payments: Viewer; direct: Operator'],
            ['Retry sagas', 'direct: Operator'],
          ],
        },
      ]);
    });

    it('is about the user, not about the person looking', async () => {
      await open('/admin/users/u-alice');

      expect(el().querySelector('app-access-summary caption')?.textContent).toContain(
        "The user's permissions",
      );
      expect(el().querySelector('app-access-summary')?.textContent).not.toContain('you');
    });

    it('follows the draft, saved or not: a grant added, a saga type ticked, a grant removed', async () => {
      await open('/admin/users/u-admin');
      expect(preview()).toEqual([
        {
          scope: 'All saga types',
          permissions: [
            ['View sagas', 'direct: Administrator'],
            ['View saga data', 'direct: Administrator'],
            ['Retry sagas', 'direct: Administrator'],
            ['Manage access', 'direct: Administrator'],
          ],
        },
      ]);

      await addGrant(adminData().roles[3].id, ['PaymentSaga']);
      // Scoped to a saga type, a custom role that views and retries: its type has a row of its own.
      expect(preview().map((row) => row.scope)).toEqual(['All saga types']);

      await removeGrant(0);
      expect(preview()).toEqual([
        {
          scope: 'PaymentSaga',
          permissions: [
            ['View sagas', 'direct: Support'],
            ['Retry sagas', 'direct: Support'],
          ],
        },
      ]);
      http.expectNone((r) => r.method !== 'GET');
    });

    it('does not count access.manage in a scoped grant, and says the grant ignores it', async () => {
      await open('/admin/users/u-admin');
      await removeGrant(0);
      await addGrant(ADMINISTRATOR_ID, ['OrderSaga']);

      expect(preview()).toEqual([
        {
          scope: 'OrderSaga',
          permissions: [
            ['View sagas', 'direct: Administrator'],
            ['View saga data', 'direct: Administrator'],
            ['Retry sagas', 'direct: Administrator'],
          ],
        },
      ]);
      expect(editor().textContent).toContain('access.manage is ignored in a scoped grant');
    });

    it('is empty, in the words of a user, when nothing is granted', async () => {
      // A user in no team, with the one grant removed.
      await open('/admin/users/u-admin');
      await removeGrant(0);

      expect(el().querySelector('app-access-summary')?.textContent).toContain(
        'This user would hold no permissions.',
      );
      expect(el().querySelector('app-access-summary table')).toBeNull();
    });

    it('says a disabled user holds no access, whatever the grants, and shows it again when Enabled is ticked', async () => {
      await open('/admin/users/u-alice');

      check(enabled(), false);
      await settle();
      expect(el().querySelector('app-access-summary')).toBeNull();
      expect(el().querySelector('.preview')?.textContent).toContain(
        'A disabled user holds no access.',
      );

      check(enabled(), true);
      await settle();
      expect(el().querySelector('app-access-summary table')).not.toBeNull();
    });

    it("counts the teams' grants only for a team the user is a member of, and names the team", async () => {
      await open(
        '/admin/users/u-admin',
        adminData({
          users: [adminUser({ ...ADMIN, grants: [grant(VIEWER_ID, ['PaymentSaga'])] }), ALICE],
          teams: [
            team({ id: 't-1', name: 'Others', memberIds: ['u-alice'], grants: [grant(VIEWER_ID)] }),
            team({
              id: 't-2',
              name: 'Admins',
              memberIds: ['u-admin'],
              grants: [grant(OPERATOR_ID, ['OrderSaga'])],
            }),
          ],
        }),
      );

      expect(preview()).toEqual([
        {
          scope: 'OrderSaga',
          permissions: [
            ['View sagas', 'team Admins: Operator'],
            ['View saga data', 'team Admins: Operator'],
            ['Retry sagas', 'team Admins: Operator'],
          ],
        },
        {
          scope: 'PaymentSaga',
          permissions: [
            ['View sagas', 'direct: Viewer'],
            ['View saga data', 'direct: Viewer'],
          ],
        },
      ]);
    });

    it('counts no team for a new user', async () => {
      await open('/admin/users/new');
      await addGrant(VIEWER_ID, 'all');

      expect(preview()).toEqual([
        {
          scope: 'All saga types',
          permissions: [
            ['View sagas', 'direct: Viewer'],
            ['View saga data', 'direct: Viewer'],
          ],
        },
      ]);
    });

    it('says the user holds nothing until they have chosen a new password, while that is so', async () => {
      await open(
        '/admin/users/u-alice',
        adminData({ users: [ADMIN, adminUser({ ...ALICE, mustChangePassword: true })] }),
      );

      expect(el().querySelector('.preview')?.textContent).toContain(
        'Until the user has chosen a new password they hold no access',
      );
    });

    it('says it for a new user while the change at next sign-in is on, and stops when it is turned off', async () => {
      await open('/admin/users/new');
      expect(el().querySelector('.preview')?.textContent).toContain('Until the user has chosen');

      check(requireChange(), false);
      await settle();

      expect(el().querySelector('.preview')?.textContent).not.toContain(
        'Until the user has chosen',
      );
    });

    it('does not say it for a user who has a password', async () => {
      await open('/admin/users/u-alice');

      expect(el().querySelector('.preview')?.textContent).not.toContain(
        'Until the user has chosen',
      );
    });
  });

  describe("the signed-in user's own record", () => {
    it('cannot be disabled or deleted, and says why beside each', async () => {
      await open('/admin/users/u-admin');

      expect(enabled().disabled).toBe(true);
      expect(enabled().checked).toBe(true);
      expect(enabled().getAttribute('aria-describedby')).toBe('user-enabled-hint');
      expect(message('user-enabled-hint')).toBe('You cannot disable your own account.');
      const remove = button('Delete user')!;
      expect(unavailable(remove)).toBe(true);
      expect(remove.getAttribute('aria-describedby')).toBe('user-delete-hint');
      expect(message('user-delete-hint')).toBe('You cannot delete your own account.');
    });

    it('asks nothing when Delete user is clicked', async () => {
      await open('/admin/users/u-admin');

      button('Delete user')!.click();
      await settle();

      expect(el().querySelector('.confirm-prompt')).toBeNull();
      http.expectNone((r) => r.method !== 'GET');
    });

    it('can be saved with its enabled left as it is, and its grants edited', async () => {
      await open('/admin/users/u-admin');
      type(displayName(), 'The Administrator');
      await addGrant(VIEWER_ID, 'all');

      await save();

      const put = write('PUT', '/api/admin/users/u-admin');
      expect(put.request.body).toEqual({
        displayName: 'The Administrator',
        isEnabled: true,
        grants: [grant(ADMINISTRATOR_ID), grant(VIEWER_ID)],
      });
      put.flush(ADMIN);
      await reloaded();
    });

    it('lets the API refuse what would leave nobody to manage access, and says nothing else of it', async () => {
      await open('/admin/users/u-admin');
      await removeGrant(0);
      await save();

      refuse(
        write('PUT', '/api/admin/users/u-admin'),
        409,
        problem('last_administrator', LAST_ADMIN),
      );
      await reloaded();

      expect(banner('.banner--error[role="alert"]')?.textContent).toContain(LAST_ADMIN);
    });

    it('refuses a delete asked for by other means than the buttons', async () => {
      await open('/admin/users/u-admin');

      const page = harness.routeDebugElement!.componentInstance as unknown as {
        remove(): Promise<void>;
      };
      await page.remove();

      http.expectNone((r) => r.method === 'DELETE');
    });

    it('is not special on the record of another user: Enabled and Delete are available', async () => {
      await open('/admin/users/u-alice');

      expect(enabled().disabled).toBe(false);
      expect(unavailable(button('Delete user'))).toBe(false);
      expect(button('Delete user')!.getAttribute('aria-describedby')).toBeNull();
      expect(el().querySelector('#user-delete-hint')).toBeNull();
      expect(message('user-enabled-hint')).toBe(
        'A disabled user cannot sign in and holds no access.',
      );
    });
  });

  describe('Reset password', () => {
    const newPassword = () => field('user-new-password');
    const newConfirmation = () => field('user-new-confirmation');
    const resetForm = () => el().querySelector<HTMLFormElement>('#user-reset');
    const resetChange = () =>
      el().querySelector<HTMLInputElement>('#user-reset input[name="mustChangePassword"]')!;
    const setPassword = () =>
      el().querySelector<HTMLButtonElement>('#user-reset button[type="submit"]')!;
    const status = () =>
      button('Reset password')!.closest('section')!.querySelector('[role="status"]')!;

    async function openReset(): Promise<void> {
      button('Reset password')!.click();
      await settle();
    }

    async function filledReset(): Promise<void> {
      await openReset();
      type(newPassword(), resetPasswordRequest.newPassword);
      type(newConfirmation(), resetPasswordRequest.newPassword);
    }

    async function setIt(): Promise<void> {
      setPassword().click();
      await settle();
    }

    it('is closed until asked for, and opens inline with the focus on the new password', async () => {
      await open('/admin/users/u-alice');
      expect(resetForm()).toBeNull();
      expect(button('Reset password')!.getAttribute('aria-expanded')).toBe('false');

      await openReset();

      expect(resetForm()).not.toBeNull();
      expect(button('Reset password')!.getAttribute('aria-expanded')).toBe('true');
      expect(button('Reset password')!.getAttribute('aria-controls')).toBe('user-reset');
      expect(focused()).toBe(newPassword());
      expect(newPassword().type).toBe('password');
      expect(newPassword().getAttribute('autocomplete')).toBe('new-password');
      expect(newPassword().getAttribute('minlength')).toBe('12');
      expect(resetChange().checked).toBe(true);
    });

    it('is a form of its own, not inside the form that saves the user', async () => {
      await open('/admin/users/u-alice');
      await openReset();

      expect(resetForm()!.closest('form.card')).toBeNull();
      expect(el().querySelectorAll('form form')).toHaveLength(0);
    });

    it('closes on Cancel reset, forgets what was typed, and returns the focus to Reset password', async () => {
      await open('/admin/users/u-alice');
      await openReset();
      type(newPassword(), 'half typed');

      button('Cancel reset')!.click();
      await settle();

      expect(resetForm()).toBeNull();
      expect(focused()).toBe(button('Reset password'));
      await openReset();
      expect(newPassword().value).toBe('');
    });

    it('closes on a second click of Reset password too', async () => {
      await open('/admin/users/u-alice');
      await openReset();

      await openReset();

      expect(resetForm()).toBeNull();
      expect(focused()).toBe(button('Reset password'));
    });

    it('asks for a password that matches its confirmation and the policy, sends nothing, and puts focus where it is wrong', async () => {
      await open('/admin/users/u-alice');
      await openReset();

      await setIt();
      expect(message('user-new-password-error')).toBe('Enter a password.');
      expect(focused()).toBe(newPassword());

      type(newPassword(), 'short');
      await setIt();
      expect(message('user-new-password-error')).toBe('Use at least 12 characters.');

      type(newPassword(), 'a long enough password');
      type(newConfirmation(), 'a different long password');
      await setIt();
      expect(message('user-new-confirmation-error')).toBe('The passwords do not match.');
      expect(newConfirmation().getAttribute('aria-invalid')).toBe('true');
      expect(focused()).toBe(newConfirmation());
      http.expectNone((r) => r.method !== 'GET');
    });

    it('sends exactly reset-password.request.json for the fixture draft, then closes, says so and returns the focus', async () => {
      await open('/admin/users/u-alice');
      await filledReset();

      await setIt();
      const post = write('POST', '/api/admin/users/u-alice/password');
      expect(post.request.body).toEqual(resetPasswordRequest);
      post.flush({ ...ALICE, mustChangePassword: true });
      await reloaded(adminData({ users: [ADMIN, { ...ALICE, mustChangePassword: true }] }));

      expect(resetForm()).toBeNull();
      expect(status().textContent).toContain(
        'The password of alice was reset and their sessions have ended.',
      );
      expect(status().textContent).toContain('They must choose a new one at next sign-in.');
      expect(focused()).toBe(button('Reset password'));
      // What was typed is gone from the page, and the user now shows as having to change it.
      await openReset();
      expect(newPassword().value).toBe('');
      expect(el().querySelector('.page-header .chip')?.textContent).toBe('Must change password');
    });

    it('sends mustChangePassword false when the change is turned off, and says nothing of one', async () => {
      await open('/admin/users/u-alice');
      await filledReset();
      check(resetChange(), false);

      await setIt();

      const post = write('POST', '/api/admin/users/u-alice/password');
      expect(post.request.body).toEqual({
        newPassword: resetPasswordRequest.newPassword,
        mustChangePassword: false,
      });
      post.flush(ALICE);
      await reloaded();
      expect(status().textContent).toContain('was reset');
      expect(status().textContent).not.toContain('They must choose');
    });

    it('shows the work: the button is disabled and says so, and a second submit sends nothing', async () => {
      await open('/admin/users/u-alice');
      await filledReset();

      await setIt();
      expect(unavailable(setPassword())).toBe(true);
      expect(setPassword().textContent?.trim()).toBe('Setting…');
      resetForm()!.dispatchEvent(new Event('submit', { cancelable: true }));
      await settle();

      write('POST', '/api/admin/users/u-alice/password').flush(ALICE);
      await reloaded();
    });

    it('puts the API message about the new password under its field, keeps what was typed, and ends it on typing', async () => {
      await open('/admin/users/u-alice');
      await filledReset();
      await setIt();
      // Not on the new password, where opening the form put it: the page must bring it back.
      newConfirmation().focus();
      expect(focused()).toBe(newConfirmation());

      refuse(
        write('POST', '/api/admin/users/u-alice/password'),
        400,
        problem('validation', 'x', { newPassword: ['The password may not be the username.'] }),
      );
      await settle();

      expect(message('user-new-password-error')).toBe('The password may not be the username.');
      expect(newPassword().getAttribute('aria-invalid')).toBe('true');
      expect(newPassword().value).toBe(resetPasswordRequest.newPassword);
      expect(focused()).toBe(newPassword());
      expect(el().querySelector('#user-action-failure')).toBeNull();

      type(newPassword(), 'another one entirely');
      expect(message('user-new-password-error')).toBeUndefined();
    });

    it.each([
      [403, 'You no longer have permission to manage access.'],
      [500, 'HTTP 500'],
    ])(
      'says a %i in the account section, and keeps the form and what was typed',
      async (status, text) => {
        await open('/admin/users/u-alice');
        await filledReset();
        await setIt();

        refuse(write('POST', '/api/admin/users/u-alice/password'), status, problem('x', 'words'));
        await settle();

        expect(el().querySelector('#user-action-failure')?.textContent).toContain(text);
        expect(el().querySelector('#user-action-failure')?.getAttribute('role')).toBe('alert');
        expect(resetForm()).not.toBeNull();
        expect(newPassword().value).toBe(resetPasswordRequest.newPassword);
        expect(unavailable(setPassword())).toBe(false);
      },
    );

    it('says "This no longer exists" for a 404', async () => {
      await open('/admin/users/u-alice');
      await filledReset();
      await setIt();

      refuse(write('POST', '/api/admin/users/u-alice/password'), 404, { title: 'Not found' });
      await settle();
      answerReload(http, adminData({ users: [ADMIN] }));
      await settle();

      expect(banner('.banner--warning[role="alert"]')?.textContent).toContain(
        'This no longer exists',
      );
    });

    it('leaves the user draft alone: what was typed in the display name stays, and Save still sends it', async () => {
      await open('/admin/users/u-alice');
      type(displayName(), 'Typed before');
      await filledReset();
      await setIt();
      write('POST', '/api/admin/users/u-alice/password').flush(ALICE);
      await reloaded();

      expect(displayName().value).toBe('Typed before');
      // The reset says so in the account section; the form's own Saved is about the form, which was not saved.
      expect(el().querySelector('form.card')!.previousElementSibling!.textContent).not.toContain(
        'Saved.',
      );
    });

    it('tells the signed-in user that resetting their own password ends their own session', async () => {
      await open('/admin/users/u-admin');
      await openReset();

      expect(resetForm()!.textContent).toContain('This is your own account');
      expect(resetForm()!.querySelector('a')?.getAttribute('href')).toBe('/account');
    });

    it('does not say it on the record of another user', async () => {
      await open('/admin/users/u-alice');
      await openReset();

      expect(resetForm()!.textContent).not.toContain('This is your own account');
    });

    it('has a status region that was there before there was anything to say', async () => {
      await open('/admin/users/u-alice');

      expect(status()).not.toBeNull();
      expect(status().textContent?.trim()).toBe('');
    });
  });

  describe('Unlock', () => {
    const locked = () =>
      adminData({ users: [ADMIN, adminUser({ ...ALICE, lockedUntilUtc: FUTURE })] });

    it('is not offered for an account that is not locked', async () => {
      await open('/admin/users/u-alice');
      expect(button('Unlock')).toBeUndefined();
      expect(el().querySelector('.locked')).toBeNull();
    });

    it('is not offered for a lockout that has ended', async () => {
      await open(
        '/admin/users/u-alice',
        adminData({
          users: [ADMIN, adminUser({ ...ALICE, lockedUntilUtc: '2020-01-01T00:00:00+00:00' })],
        }),
      );

      expect(button('Unlock')).toBeUndefined();
      expect(el().querySelector('.page-header .chip')).toBeNull();
    });

    it('is offered for a locked account, with the time the lockout ends', async () => {
      await open('/admin/users/u-alice', locked());

      expect(button('Unlock')).toBeDefined();
      expect(el().querySelector('.locked')?.textContent).toContain('Locked until');
      expect(el().querySelector('.locked time')?.getAttribute('datetime')).toBe(FUTURE);
      expect(el().querySelector('.page-header .chip')?.textContent).toBe('Locked');
    });

    it('posts the unlock, then the account shows as unlocked, says so, and keeps the focus on the page', async () => {
      await open('/admin/users/u-alice', locked());

      button('Unlock')!.click();
      await settle();
      const post = write('POST', '/api/admin/users/u-alice/unlock');
      expect(post.request.body).toEqual({});
      post.flush(ALICE);
      await reloaded();

      expect(button('Unlock')).toBeUndefined();
      expect(el().querySelector('.page-header .chip')).toBeNull();
      expect(
        button('Reset password')!.closest('section')!.querySelector('[role="status"]')?.textContent,
      ).toContain('alice is unlocked.');
      // The button that had the focus is gone: it goes to a button that stays.
      expect(focused()).toBe(button('Reset password'));
    });

    it('is disabled while it runs, and refuses a second unlock however it is asked for', async () => {
      await open('/admin/users/u-alice', locked());

      button('Unlock')!.click();
      await settle();

      expect(unavailable(button('Unlock'))).toBe(true);
      // The attribute is one guard; the page's own is that nothing starts while something runs.
      const page = harness.routeDebugElement!.componentInstance as unknown as {
        unlock(): Promise<void>;
      };
      await page.unlock();
      await settle();
      // One request, not two: expectOne fails when there are more.
      write('POST', '/api/admin/users/u-alice/unlock').flush(ALICE);
      await reloaded();
    });

    it.each([
      [403, 'You no longer have permission to manage access.'],
      [500, 'HTTP 500'],
    ])('says a %i in the account section and stays locked', async (status, text) => {
      await open('/admin/users/u-alice', locked());
      button('Unlock')!.click();
      await settle();

      refuse(write('POST', '/api/admin/users/u-alice/unlock'), status, problem('x', 'words'));
      await settle();

      expect(el().querySelector('#user-action-failure')?.textContent).toContain(text);
      expect(button('Unlock')).toBeDefined();
      expect(unavailable(button('Unlock'))).toBe(false);
    });
  });

  describe('Delete user', () => {
    it('asks first, and deletes only on yes, then goes to the list', async () => {
      await open('/admin/users/u-alice');

      button('Delete user')!.click();
      await settle();
      expect(el().querySelector('.confirm-prompt')?.textContent).toBe(
        'Delete the user alice? Their grants go with them. This cannot be undone.',
      );
      http.expectNone((r) => r.method === 'DELETE');

      button('Yes, delete')!.click();
      await settle();
      noContent(write('DELETE', '/api/admin/users/u-alice'));
      await reloaded(adminData({ users: [ADMIN] }));

      expect(url()).toBe('/admin/users');
    });

    it('sends nothing on Cancel, which is the question\'s own: the page\'s way back is "Back to the users"', async () => {
      await open('/admin/users/u-alice');

      button('Delete user')!.click();
      await settle();
      expect(
        Array.from(el().querySelectorAll('button, a.btn')).filter(
          (b) => b.textContent?.trim() === 'Cancel',
        ),
      ).toHaveLength(1);
      expect(button('Back to the users')).toBeDefined();
      button('Cancel')!.click();
      await settle();

      expect(el().querySelector('.confirm-prompt')).toBeNull();
      http.expectNone((r) => r.method === 'DELETE');
      expect(url()).toBe('/admin/users/u-alice');
    });

    it('shows the API refusal of a last administrator in the account section and stays', async () => {
      await open('/admin/users/u-alice');
      button('Delete user')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();

      refuse(
        write('DELETE', '/api/admin/users/u-alice'),
        409,
        problem('last_administrator', LAST_ADMIN),
      );
      await reloaded();

      const alert = el().querySelector('#user-action-failure')!;
      expect(alert.textContent).toContain(LAST_ADMIN);
      expect(alert.textContent).toContain('Give another enabled user an all-saga-types grant');
      expect(url()).toBe('/admin/users/u-alice');
      expect(displayName().value).toBe('Alice Example');
    });

    it('says a lost permission for a 403', async () => {
      await open('/admin/users/u-alice');
      button('Delete user')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();

      refuse(write('DELETE', '/api/admin/users/u-alice'), 403, problem('forbidden', 'x'));
      await settle();

      expect(el().querySelector('#user-action-failure')?.textContent).toContain(
        'You no longer have permission to manage access.',
      );
    });

    it('says "This no longer exists" for a 404', async () => {
      await open('/admin/users/u-alice');
      button('Delete user')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();

      refuse(write('DELETE', '/api/admin/users/u-alice'), 404, { title: 'Not found' });
      await settle();
      answerReload(http, adminData({ users: [ADMIN] }));
      await settle();

      expect(banner('.banner--warning[role="alert"]')?.textContent).toContain(
        'This no longer exists',
      );
    });

    it('does not turn into "This no longer exists" between the reload and the navigation of a delete', async () => {
      await open('/admin/users/u-alice');
      const navigate = vi
        .spyOn(TestBed.inject(Router), 'navigateByUrl')
        .mockImplementation(() => new Promise<boolean>(() => undefined));

      button('Delete user')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();
      noContent(write('DELETE', '/api/admin/users/u-alice'));
      // The lists no longer hold the user: the page is still the page of a user that is being deleted.
      await reloaded(adminData({ users: [ADMIN] }));

      expect(navigate).toHaveBeenCalledWith('/admin/users');
      expect(banner('.banner--warning')).toBeNull();
      expect(el().querySelector('form')).not.toBeNull();
      expect(el().querySelector('h2')?.textContent).toBe('alice');
    });
  });

  describe('one request at a time', () => {
    it('does not ask about deleting while a save runs, and the question that is open lapses', async () => {
      await open('/admin/users/u-alice');
      button('Delete user')!.click();
      await settle();
      expect(el().querySelector('.confirm-prompt')).not.toBeNull();

      await save();

      expect(el().querySelector('.confirm-prompt')).toBeNull();
      expect(unavailable(button('Delete user'))).toBe(true);
      write('PUT', '/api/admin/users/u-alice').flush(ALICE);
      await reloaded();
      expect(unavailable(button('Delete user'))).toBe(false);
    });

    it('does not save while a delete runs', async () => {
      await open('/admin/users/u-alice');
      button('Delete user')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();

      expect(unavailable(submit())).toBe(true);
      el()
        .querySelector('form.card')!
        .dispatchEvent(new Event('submit', { cancelable: true }));
      await settle();
      http.expectNone((r) => r.method === 'PUT');

      noContent(write('DELETE', '/api/admin/users/u-alice'));
      await reloaded(adminData({ users: [ADMIN] }));
    });

    it('does not delete while a save runs, even if the delete had been confirmed before it', async () => {
      await open('/admin/users/u-alice');
      await save();

      const page = harness.routeDebugElement!.componentInstance as unknown as {
        remove(): Promise<void>;
      };
      await page.remove();

      http.expectNone((r) => r.method === 'DELETE');
      write('PUT', '/api/admin/users/u-alice').flush(ALICE);
      await reloaded();
    });

    it('does not save while a password reset runs, and does not reset while a save runs', async () => {
      await open('/admin/users/u-alice');
      button('Reset password')!.click();
      await settle();
      type(field('user-new-password'), 'a long enough password');
      type(field('user-new-confirmation'), 'a long enough password');
      el().querySelector<HTMLButtonElement>('#user-reset button[type="submit"]')!.click();
      await settle();

      expect(unavailable(submit())).toBe(true);
      await save();
      http.expectNone((r) => r.method === 'PUT');

      write('POST', '/api/admin/users/u-alice/password').flush(ALICE);
      await reloaded();
      expect(unavailable(submit())).toBe(false);
    });

    it('does not unlock while a save runs', async () => {
      await open(
        '/admin/users/u-alice',
        adminData({ users: [ADMIN, adminUser({ ...ALICE, lockedUntilUtc: FUTURE })] }),
      );
      await save();

      expect(unavailable(button('Unlock'))).toBe(true);
      const page = harness.routeDebugElement!.componentInstance as unknown as {
        unlock(): Promise<void>;
      };
      await page.unlock();
      http.expectNone((r) => r.url.endsWith('/unlock'));

      write('PUT', '/api/admin/users/u-alice').flush(ALICE);
      await reloaded();
    });
  });

  // jsdom's click() does not move the focus: every spec here puts it somewhere with focus() and looks at it after.
  describe('where the keyboard focus is', () => {
    const banners = () => el().querySelector<HTMLElement>('#user-failure');
    const actionBanner = () => el().querySelector<HTMLElement>('#user-action-failure');

    describe('on the buttons that start a request', () => {
      it('stays on Save while the request runs and after the answer, and the button is not natively disabled', async () => {
        await open('/admin/users/u-alice');
        submit().focus();
        expect(focused()).toBe(submit());

        await save();

        expect(unavailable(submit())).toBe(true);
        // A natively disabled button loses the focus it holds: this one must stay focusable.
        expect(submit().disabled).toBe(false);
        expect(focused()).toBe(submit());
        write('PUT', '/api/admin/users/u-alice').flush(ALICE);
        await reloaded();

        expect(focused()).toBe(submit());
        expect(banner('.banner--success')?.textContent).toContain('Saved.');
        expect(unavailable(submit())).toBe(false);
      });

      it('stays on the field Enter was pressed in, when Save is made by the keyboard from a field', async () => {
        await open('/admin/users/u-alice');
        displayName().focus();

        await save();
        write('PUT', '/api/admin/users/u-alice').flush(ALICE);
        await reloaded();

        expect(focused()).toBe(displayName());
      });

      it('refuses what Save is asked while it runs, whichever way it is asked', async () => {
        await open('/admin/users/u-alice');
        submit().focus();
        await save();

        // The button is still there to be clicked and pressed: the page says no.
        submit().click();
        el()
          .querySelector('form.card')!
          .dispatchEvent(new Event('submit', { cancelable: true }));
        await settle();

        write('PUT', '/api/admin/users/u-alice').flush(ALICE);
        await reloaded();
      });

      it('stays on Unlock while it runs, and a click on it then asks nothing', async () => {
        await open(
          '/admin/users/u-alice',
          adminData({ users: [ADMIN, adminUser({ ...ALICE, lockedUntilUtc: FUTURE })] }),
        );
        button('Unlock')!.focus();

        button('Unlock')!.click();
        await settle();

        expect(unavailable(button('Unlock'))).toBe(true);
        expect((button('Unlock') as HTMLButtonElement).disabled).toBe(false);
        expect(focused()).toBe(button('Unlock'));
        button('Unlock')!.click();
        await settle();
        write('POST', '/api/admin/users/u-alice/unlock').flush(ALICE);
        await reloaded();
      });

      it('stays on Set password while the reset runs', async () => {
        await open('/admin/users/u-alice');
        button('Reset password')!.click();
        await settle();
        type(field('user-new-password'), 'a long enough password');
        type(field('user-new-confirmation'), 'a long enough password');
        const set = el().querySelector<HTMLButtonElement>('#user-reset button[type="submit"]')!;
        set.focus();

        set.click();
        await settle();

        expect(unavailable(set)).toBe(true);
        expect(set.disabled).toBe(false);
        expect(focused()).toBe(set);
        write('POST', '/api/admin/users/u-alice/password').flush(ALICE);
        await reloaded();
      });

      it('keeps Enabled usable while a save runs: its checkbox is not what the request waits for, and Enter in it must not drop the focus', async () => {
        await open('/admin/users/u-alice');
        enabled().focus();

        await save();

        expect(enabled().disabled).toBe(false);
        expect(focused()).toBe(enabled());
        write('PUT', '/api/admin/users/u-alice').flush(ALICE);
        await reloaded();
      });
    });

    describe('after a refusal', () => {
      async function ownAdministratorRemoved(): Promise<void> {
        await open('/admin/users/u-admin');
        await removeGrant(0);
        submit().focus();
        await save();
      }

      it('goes to the banner for a last-administrator refusal of a save, which can sit far above the button', async () => {
        await ownAdministratorRemoved();

        refuse(
          write('PUT', '/api/admin/users/u-admin'),
          409,
          problem('last_administrator', LAST_ADMIN),
        );
        await reloaded();

        expect(banners()?.textContent).toContain(LAST_ADMIN);
        expect(banners()?.getAttribute('tabindex')).toBe('-1');
        expect(focused()).toBe(banners());
        // The draft is kept, and the form can be used again from where it is.
        expect(grantGroups()).toHaveLength(0);
      });

      it.each([
        [403, 'You no longer have permission to manage access.'],
        [500, 'HTTP 500'],
      ])('goes to the banner for a %i of a save', async (status, text) => {
        await open('/admin/users/u-alice');
        submit().focus();
        await save();

        refuse(write('PUT', '/api/admin/users/u-alice'), status, problem('x', 'words'));
        await settle();

        expect(banners()?.textContent).toContain(text);
        expect(focused()).toBe(banners());
      });

      it('goes to the banner when the API names only paths the form has no field for', async () => {
        await open('/admin/users/u-alice');
        submit().focus();
        await save();

        refuse(
          write('PUT', '/api/admin/users/u-alice'),
          400,
          problem('validation', 'x', { mustChangePassword: ['Not a flag.'] }),
        );
        await settle();

        expect(banners()?.textContent).toContain('Not a flag.');
        expect(focused()).toBe(banners());
      });

      it('keeps the focus on a field the API names, and on the username for a taken one, not the banner', async () => {
        await open('/admin/users/new');
        type(username(), 'alice');
        type(displayName(), 'Alice');
        type(password(), 'a long enough password');
        type(confirmation(), 'a long enough password');
        submit().focus();
        await save();

        refuse(
          write('POST', '/api/admin/users'),
          409,
          problem('username_taken', "A user named 'alice' already exists."),
        );
        await reloaded();

        expect(focused()).toBe(username());
      });

      it('goes to the banner of the account section for a refused delete, and for a refused unlock', async () => {
        await open('/admin/users/u-alice');
        button('Delete user')!.click();
        await settle();
        button('Yes, delete')!.click();
        await settle();

        refuse(
          write('DELETE', '/api/admin/users/u-alice'),
          409,
          problem('last_administrator', LAST_ADMIN),
        );
        await reloaded();

        expect(actionBanner()?.textContent).toContain(LAST_ADMIN);
        expect(actionBanner()?.getAttribute('tabindex')).toBe('-1');
        expect(focused()).toBe(actionBanner());
      });

      it('goes to the banner of the account section for a refused unlock', async () => {
        await open(
          '/admin/users/u-alice',
          adminData({ users: [ADMIN, adminUser({ ...ALICE, lockedUntilUtc: FUTURE })] }),
        );
        button('Unlock')!.focus();
        button('Unlock')!.click();
        await settle();

        refuse(write('POST', '/api/admin/users/u-alice/unlock'), 500, problem('x', 'words'));
        await settle();

        expect(actionBanner()?.textContent).toContain('HTTP 500');
        expect(focused()).toBe(actionBanner());
      });

      it('goes to the banner of the account section for a refused reset that names no field of the form', async () => {
        await open('/admin/users/u-alice');
        button('Reset password')!.click();
        await settle();
        type(field('user-new-password'), 'a long enough password');
        type(field('user-new-confirmation'), 'a long enough password');
        el().querySelector<HTMLButtonElement>('#user-reset button[type="submit"]')!.click();
        await settle();
        field('user-new-confirmation').focus();

        refuse(
          write('POST', '/api/admin/users/u-alice/password'),
          400,
          problem('validation', 'x', { mustChangePassword: ['Not a flag.'] }),
        );
        await settle();

        expect(actionBanner()?.textContent).toContain('Not a flag.');
        expect(focused()).toBe(actionBanner());
      });

      it('goes to the banner of the account section for a 500 of a reset, and the form stays open', async () => {
        await open('/admin/users/u-alice');
        button('Reset password')!.click();
        await settle();
        type(field('user-new-password'), 'a long enough password');
        type(field('user-new-confirmation'), 'a long enough password');
        el().querySelector<HTMLButtonElement>('#user-reset button[type="submit"]')!.click();
        await settle();

        refuse(write('POST', '/api/admin/users/u-alice/password'), 500, problem('x', 'words'));
        await settle();

        expect(focused()).toBe(actionBanner());
        expect(el().querySelector('#user-reset')).not.toBeNull();
      });
    });

    describe('in the Reset password panel', () => {
      const escape = (target: HTMLElement) =>
        target.dispatchEvent(
          new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
        );

      it('closes on Escape from any field of it, forgets what was typed, and returns the focus to Reset password', async () => {
        await open('/admin/users/u-alice');
        button('Reset password')!.click();
        await settle();
        type(field('user-new-password'), 'half typed');
        field('user-new-confirmation').focus();

        escape(field('user-new-confirmation'));
        await settle();

        expect(el().querySelector('#user-reset')).toBeNull();
        expect(focused()).toBe(button('Reset password'));
        expect(button('Reset password')!.getAttribute('aria-expanded')).toBe('false');
        button('Reset password')!.click();
        await settle();
        expect(field('user-new-password').value).toBe('');
      });

      it('closes on Escape from the new password the form opened on', async () => {
        await open('/admin/users/u-alice');
        button('Reset password')!.click();
        await settle();
        expect(focused()).toBe(field('user-new-password'));

        escape(field('user-new-password'));
        await settle();

        expect(el().querySelector('#user-reset')).toBeNull();
        expect(focused()).toBe(button('Reset password'));
      });

      it('does nothing on Escape while the password is being set', async () => {
        await open('/admin/users/u-alice');
        button('Reset password')!.click();
        await settle();
        type(field('user-new-password'), 'a long enough password');
        type(field('user-new-confirmation'), 'a long enough password');
        el().querySelector<HTMLButtonElement>('#user-reset button[type="submit"]')!.click();
        await settle();

        escape(field('user-new-password'));
        await settle();

        expect(el().querySelector('#user-reset')).not.toBeNull();
        write('POST', '/api/admin/users/u-alice/password').flush(ALICE);
        await reloaded();
      });

      it('leaves an Escape in the rest of the page alone', async () => {
        await open('/admin/users/u-alice');
        button('Reset password')!.click();
        await settle();

        escape(displayName());
        await settle();

        expect(el().querySelector('#user-reset')).not.toBeNull();
      });
    });
  });

  describe('the exact-name box of the grants', () => {
    it('is added when the user saves, not dropped: a name typed and not added with Add type', async () => {
      await open('/admin/users/u-alice');
      const box = grantGroups()[0].querySelector<HTMLInputElement>('input.input')!;
      type(box, '  ShippingSaga ');

      await save();

      const put = write('PUT', '/api/admin/users/u-alice');
      expect((put.request.body as { grants: unknown }).grants).toEqual([
        grant(OPERATOR_ID, ['OrderSaga', 'ShippingSaga']),
      ]);
      put.flush(ALICE);
      await reloaded();
    });

    it('stops Save when a typed name cannot be added, with its message and the focus on its box, and sends nothing', async () => {
      await open('/admin/users/u-alice');
      const box = grantGroups()[0].querySelector<HTMLInputElement>('input.input')!;
      type(box, 'x'.repeat(201));

      await save();

      http.expectNone((r) => r.method !== 'GET');
      expect(grantGroups()[0].querySelector('.custom .field-error')?.textContent).toBe(
        'Use at most 200 characters.',
      );
      expect(focused()).toBe(box);
    });

    it('does not stop Save when nothing is typed in it', async () => {
      await open('/admin/users/u-alice');

      await save();

      write('PUT', '/api/admin/users/u-alice').flush(ALICE);
      await reloaded();
    });
  });

  describe('the access of the signed-in user', () => {
    const warning = 'You are removing your own access to administration.';

    it('is warned about when the draft no longer gives the user access.manage for every saga type, in a region that was there before', async () => {
      await open('/admin/users/u-admin');
      const region = el().querySelector('app-grants-editor')!.nextElementSibling!;
      expect(region.getAttribute('role')).toBe('status');
      expect(region.textContent?.trim()).toBe('');

      await removeGrant(0);

      expect(region.textContent).toContain(warning);
      expect(region.textContent).toContain('you will no longer be able to manage access');
    });

    it('stops saying it when the grant comes back', async () => {
      await open('/admin/users/u-admin');
      await removeGrant(0);
      expect(el().textContent).toContain('You are removing your own access');

      await addGrant(ADMINISTRATOR_ID, 'all');

      expect(el().textContent).not.toContain('You are removing your own access');
    });

    it('is warned about too when the administrator grant is only for some saga types, since access.manage counts for every one', async () => {
      await open('/admin/users/u-admin');
      await removeGrant(0);
      await addGrant(ADMINISTRATOR_ID, ['OrderSaga']);

      expect(el().textContent).toContain(warning);
    });

    it('is not warned about when a team of the user keeps it', async () => {
      await open(
        '/admin/users/u-admin',
        adminData({
          teams: [
            team({
              id: 't-admins',
              name: 'Admins',
              memberIds: ['u-admin'],
              grants: [grant(ADMINISTRATOR_ID)],
            }),
          ],
        }),
      );

      await removeGrant(0);

      expect(el().textContent).not.toContain(warning);
    });

    it('is not warned about when the user did not hold it to begin with', async () => {
      await open(
        '/admin/users/u-admin',
        adminData({ users: [adminUser({ ...ADMIN, grants: [grant(VIEWER_ID, ['OrderSaga'])] })] }),
      );

      await removeGrant(0);

      expect(el().textContent).not.toContain(warning);
    });

    it('is not warned about when the draft of another user lacks it, or on the record of another user', async () => {
      await open('/admin/users/u-alice');
      await removeGrant(0);
      expect(el().textContent).not.toContain(warning);

      await harness.navigateByUrl('/admin/users/u-admin');
      await settle();
      expect(el().textContent).not.toContain(warning);
    });

    it('is not warned about on another user who holds it', async () => {
      await open(
        '/admin/users/u-alice',
        adminData({ users: [ADMIN, adminUser({ ...ALICE, grants: [grant(ADMINISTRATOR_ID)] })] }),
      );

      await removeGrant(0);

      expect(el().textContent).not.toContain(warning);
    });
  });

  describe('what the page says of a username and a password', () => {
    it('names the reserved username in the hint of the username', async () => {
      await open('/admin/users/new');

      expect(message('user-username-hint')).toContain('api-key');
      expect(message('user-username-hint')).toContain('reserved');
    });

    it('asks for a long password, with no minimum, when the session does not give one, and leaves the length to the API', async () => {
      await open('/admin/users/new', adminData(), { passwordMinLength: null });

      expect(message('user-password-hint')).toContain('Choose a long password.');
      expect(password().getAttribute('minlength')).toBeNull();
      type(username(), 'alice');
      type(displayName(), 'Alice');
      type(password(), 'abc');
      type(confirmation(), 'abc');

      await save();

      // Nothing is refused here for its length: the API says if it is too short.
      expect(message('user-password-error')).toBeUndefined();
      const post = write('POST', '/api/admin/users');
      expect(post.request.body).toMatchObject({ password: 'abc' });
      post.flush(adminUser());
      await reloaded();
    });

    it('does the same for the new password of a reset', async () => {
      await open('/admin/users/u-alice', adminData(), { passwordMinLength: null });
      button('Reset password')!.click();
      await settle();

      expect(message('user-new-password-hint')).toContain('Choose a long password.');
      expect(field('user-new-password').getAttribute('minlength')).toBeNull();
    });
  });

  describe('the clock', () => {
    const lockedFor = (minutes: number) => new Date(Date.now() + minutes * 60_000).toISOString();
    const chips = () =>
      Array.from(el().querySelectorAll('.page-header .chip')).map((c) => c.textContent);

    async function openLocked(minutes: number): Promise<void> {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
      vi.setSystemTime(new Date('2026-10-03T12:00:00Z'));
      await open(
        '/admin/users/u-alice',
        adminData({ users: [ADMIN, adminUser({ ...ALICE, lockedUntilUtc: lockedFor(minutes) })] }),
      );
    }

    it('drops Locked and Unlock when the lockout ends while the page is open, and not before', async () => {
      await openLocked(15);
      expect(chips()).toEqual(['Locked']);
      expect(button('Unlock')).toBeDefined();

      await vi.advanceTimersByTimeAsync(14 * 60_000);
      await settle();
      expect(chips()).toEqual(['Locked']);
      expect(button('Unlock')).toBeDefined();

      await vi.advanceTimersByTimeAsync(61_000);
      await settle();
      expect(chips()).toEqual([]);
      expect(button('Unlock')).toBeUndefined();
      expect(el().querySelector('.locked')).toBeNull();
    });

    it('leaves no timer behind when the page is left', async () => {
      await openLocked(15);
      expect(vi.getTimerCount()).toBeGreaterThan(0);
      const timersWithTheClock = vi.getTimerCount();

      await harness.navigateByUrl('/admin/users');
      await settle();

      expect(vi.getTimerCount()).toBeLessThan(timersWithTheClock);
    });
  });

  it('has its status regions in place before there is anything to say', async () => {
    await open('/admin/users/u-alice');

    // Saved, the grants editor's, the warning about one's own access, and the account section's.
    const regions = Array.from(el().querySelectorAll('[role="status"]'));
    expect(regions).toHaveLength(4);
    expect(
      regions.filter((r) => r.children.length === 0 && r.textContent?.trim() === ''),
    ).toHaveLength(4);
  });

  it('uses a role that is a custom one in the select and in the preview', async () => {
    const support = role();
    await open(
      '/admin/users/u-alice',
      adminData({
        users: [ADMIN, adminUser({ ...ALICE, grants: [grant(support.id, ['OrderSaga'])] })],
      }),
    );

    expect(Array.from(grantSelect(0).options).map((o) => o.textContent?.trim())).toContain(
      'Support',
    );
    expect(grantSelect(0).value).toBe(support.id);
    expect(
      preview().some((row) =>
        row.permissions.some(([, origin]) => origin?.includes('direct: Support')),
      ),
    ).toBe(true);
  });
});
