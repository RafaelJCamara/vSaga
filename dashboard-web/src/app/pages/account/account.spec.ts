import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MockInstance, vi } from 'vitest';
import { SessionUser } from '../../models/auth.model';
import {
  AuthMock,
  AuthMockOptions,
  createAuthMock,
  provideAuthMock,
} from '../../testing/auth-mock';
import { httpError, networkError, problem } from '../../testing/http-error';
import { CANNOT_REACH } from '../../util/failure-text';
import { Account } from './account';

const ALICE: SessionUser = {
  id: 'user-alice',
  username: 'alice',
  displayName: 'Alice Example',
  mustChangePassword: false,
};

const WRONG_PASSWORD = 'The current password is not correct.';

describe('Account', () => {
  let auth: AuthMock;
  let fixture: ComponentFixture<Account>;
  let navigate: MockInstance<Router['navigateByUrl']>;

  async function create(options: AuthMockOptions = { user: ALICE }) {
    auth = createAuthMock(options);
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideAuthMock(auth)] });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    fixture = TestBed.createComponent(Account);
    fixture.detectChanges();
    await settle();
  }

  /** Lets the promises of the component and of its mocks finish, then renders. */
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
    await fixture.whenStable();
  }

  const el = () => fixture.nativeElement as HTMLElement;
  const input = (id: string) => el().querySelector<HTMLInputElement>(`#account-${id}`)!;
  const message = (id: string) => el().querySelector(`#account-${id}-error`)?.textContent?.trim();
  const submitButton = () => el().querySelector<HTMLButtonElement>('button[type="submit"]');
  const failureBanner = () => el().querySelector<HTMLElement>('.banner--error');
  const focused = () => document.activeElement;
  /** Whether a button says it is unavailable (aria-disabled): it stays focusable, so the keyboard keeps its place. */
  const unavailable = (el: HTMLElement | null | undefined) =>
    el?.getAttribute('aria-disabled') === 'true';

  function type(id: string, value: string): void {
    const field = input(id);
    field.value = value;
    field.dispatchEvent(new Event('input'));
    // The app renders after an input event; a value the page resets later is compared with this one.
    fixture.detectChanges();
  }

  function fill(overrides: Partial<Record<string, string>> = {}): void {
    const values: Record<string, string> = {
      current: 'the old password',
      new: 'a brand new password',
      confirmation: 'a brand new password',
      ...overrides,
    };
    for (const [id, value] of Object.entries(values)) type(id, value);
  }

  async function submit(): Promise<void> {
    submitButton()!.click();
    await settle();
  }

  describe('who the user is', () => {
    it('shows the display name and the username', async () => {
      await create();

      const text = el().querySelector('.identity')?.textContent;
      expect(text).toContain('Alice Example');
      expect(el().querySelector('.identity code')?.textContent).toBe('alice');
      expect(el().querySelector('h1')?.textContent).toBe('Account');
    });

    it('has no password to change in a session with no user, and says so', async () => {
      await create({ user: null });

      expect(el().querySelector('form')).toBeNull();
      expect(el().querySelector('.identity')).toBeNull();
      expect(el().textContent).toContain('no user account');
    });

    it('says nothing of a missing user account while the session is ending: the page is about to leave', async () => {
      await create({ user: ALICE });

      // What the lockout after a wrong current password does: the session ends under the page.
      auth.user.set(null);
      auth.status.set('anonymous');
      await settle();

      expect(el().textContent).not.toContain('no user account');
      expect(el().querySelector('form')).toBeNull();
    });
  });

  describe('your access', () => {
    it('shows what the session may do, as the server computed it', async () => {
      await create({
        user: ALICE,
        access: {
          permissions: ['sagas.view'],
          scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.view', 'sagas.retry'] }],
        },
      });

      const rows = Array.from(el().querySelectorAll('app-access-summary tbody tr')).map((row) => [
        row.querySelector('td')?.textContent?.trim(),
        Array.from(row.querySelectorAll('.chip')).map((chip) => chip.textContent?.trim()),
      ]);
      expect(el().querySelector('#access-title')?.textContent).toBe('Your access');
      expect(rows).toEqual([
        ['All saga types', ['View sagas']],
        ['OrderSaga', ['View sagas', 'Retry sagas']],
      ]);
    });

    it('follows the session when access changes', async () => {
      await create({ user: ALICE, access: { permissions: ['sagas.view'], scoped: [] } });
      expect(el().querySelectorAll('app-access-summary .chip')).toHaveLength(1);

      auth.access.set({ permissions: ['sagas.view', 'sagas.retry'], scoped: [] });
      await settle();

      expect(el().querySelectorAll('app-access-summary .chip')).toHaveLength(2);
    });
  });

  describe('the forced password change', () => {
    const FORCED: AuthMockOptions = {
      user: { ...ALICE, mustChangePassword: true },
      access: { permissions: [], scoped: [] },
    };

    it('shows a banner that says the password must be changed first', async () => {
      await create(FORCED);

      const banner = el().querySelector('.banner--warning');
      expect(banner?.getAttribute('role')).toBe('status');
      expect(banner?.textContent).toContain(
        'choose a new password before you can use the dashboard',
      );
    });

    it('says the access is empty until then, instead of an empty table', async () => {
      await create(FORCED);

      expect(el().querySelector('app-access-summary')).toBeNull();
      expect(el().textContent).toContain(
        'Your access is empty until you have changed your password.',
      );
    });

    it('shows no banner for a user who need not', async () => {
      await create();

      expect(el().querySelector('.banner--warning')).toBeNull();
      expect(el().querySelector('app-access-summary')).not.toBeNull();
    });
  });

  describe('the password form', () => {
    it('labels every field and says what each is for, to browsers and password managers', async () => {
      await create();

      const labels = Array.from(el().querySelectorAll('label.label')).map((l) => [
        l.textContent?.trim(),
        l.getAttribute('for'),
      ]);
      expect(labels).toEqual([
        ['Current password', 'account-current'],
        ['New password', 'account-new'],
        ['Repeat the new password', 'account-confirmation'],
      ]);
      expect(input('current').autocomplete).toBe('current-password');
      expect(input('new').autocomplete).toBe('new-password');
      expect(input('confirmation').autocomplete).toBe('new-password');
      for (const id of ['current', 'new', 'confirmation']) expect(input(id).type).toBe('password');
      expect(el().querySelectorAll('[aria-required="true"]')).toHaveLength(3);
    });

    it('is sent as a POST, and the browser does not validate it on its own (the page does)', async () => {
      await create();

      const form = el().querySelector('form')!;
      expect(form.getAttribute('method')).toBe('post');
      expect(form.hasAttribute('novalidate')).toBe(true);
    });

    it('gives password managers the username the password belongs to, without showing or focusing it', async () => {
      await create();

      const hidden = el().querySelector<HTMLInputElement>('form input[autocomplete="username"]')!;
      expect(hidden.value).toBe('alice');
      expect(hidden.readOnly).toBe(true);
      expect(hidden.tabIndex).toBe(-1);
      expect(hidden.getAttribute('aria-hidden')).toBe('true');
      expect(hidden.classList).toContain('sr-only');
      // It is not one of the fields: nothing is sent from it and no label points at it.
      expect(el().querySelectorAll('label.label')).toHaveLength(3);
    });

    it('asks for no minimum length before the session says what the policy is', async () => {
      await create({ user: ALICE, passwordMinLength: null });

      expect(input('new').hasAttribute('minlength')).toBe(false);
      expect(el().querySelector('#account-new-hint')?.textContent).toContain('long password');
      fill({ new: 'x', confirmation: 'x' });

      await submit();

      expect(message('new')).toBeUndefined();
      expect(auth.changePassword).toHaveBeenCalledTimes(1);
    });

    it('takes the shortest password from the session, as a hint and as the minlength', async () => {
      await create({ user: ALICE, passwordMinLength: 16 });

      expect(input('new').getAttribute('minlength')).toBe('16');
      expect(el().querySelector('#account-new-hint')?.textContent).toContain(
        'At least 16 characters',
      );
      expect(input('new').getAttribute('aria-describedby')).toBe('account-new-hint');
    });

    it('shows no error until the form has been submitted', async () => {
      await create();
      type('new', 'x');
      type('confirmation', 'y');
      await settle();

      expect(el().querySelector('.field-error')).toBeNull();
      expect(el().querySelector('[aria-invalid]')).toBeNull();
    });

    it('names each missing field, without asking the API', async () => {
      await create();

      await submit();

      expect(auth.changePassword).not.toHaveBeenCalled();
      expect(message('current')).toBe('Enter your current password.');
      expect(message('new')).toBe('Enter a new password.');
      expect(message('confirmation')).toBeUndefined();
      expect(input('current').getAttribute('aria-invalid')).toBe('true');
      expect(input('current').getAttribute('aria-describedby')).toBe('account-current-error');
      expect(input('new').getAttribute('aria-describedby')).toBe(
        'account-new-hint account-new-error',
      );
      expect(focused()).toBe(input('current'));
    });

    it('rejects a new password shorter than the policy, with the length from the session', async () => {
      await create({ user: ALICE, passwordMinLength: 14 });
      fill({ new: 'thirteen-char', confirmation: 'thirteen-char' });

      await submit();

      expect(message('new')).toBe('Use at least 14 characters.');
      expect(auth.changePassword).not.toHaveBeenCalled();
      expect(focused()).toBe(input('new'));
    });

    it('rejects a confirmation that differs from the new password', async () => {
      await create();
      fill({ confirmation: 'something else entirely' });

      await submit();

      expect(message('confirmation')).toBe('The passwords do not match.');
      expect(input('confirmation').getAttribute('aria-invalid')).toBe('true');
      expect(auth.changePassword).not.toHaveBeenCalled();
      expect(focused()).toBe(input('confirmation'));
    });

    it('lets a message go as soon as the field is put right', async () => {
      await create();
      fill({ confirmation: 'something else entirely' });
      await submit();

      type('confirmation', 'a brand new password');
      await settle();

      expect(message('confirmation')).toBeUndefined();
    });

    it('does not reload the page for a submit: the form is handled here', async () => {
      await create();
      const event = new Event('submit', { cancelable: true });

      el().querySelector('form')!.dispatchEvent(event);
      await settle();

      expect(event.defaultPrevented).toBe(true);
    });
  });

  describe('changing the password', () => {
    it('sends the current and the new password and goes to the saga list', async () => {
      await create();
      auth.changePassword.mockImplementation(async () => auth.status.set('authenticated'));
      fill();

      await submit();

      expect(auth.changePassword).toHaveBeenCalledExactlyOnceWith(
        'the old password',
        'a brand new password',
      );
      expect(navigate).toHaveBeenCalledExactlyOnceWith('/sagas');
    });

    it('does not keep the passwords in the page once it has changed them', async () => {
      await create();
      fill();

      await submit();

      expect(fixture.componentInstance.currentPassword()).toBe('');
      expect(fixture.componentInstance.newPassword()).toBe('');
      expect(fixture.componentInstance.confirmation()).toBe('');
      // The emptied fields are not errors: the page may stay for a moment, or for good.
      expect(el().querySelector('.field-error')).toBeNull();
    });

    it('leaves for the saga list when the login page is the destination and cannot be reached', async () => {
      await create();
      auth.changePassword.mockImplementation(async () => auth.status.set('anonymous'));
      navigate.mockImplementation((url) =>
        url === '/login?reason=password' ? Promise.resolve(false) : Promise.resolve(true),
      );
      fill();

      await submit();

      expect(navigate.mock.calls.map(([url]) => url)).toEqual(['/login?reason=password', '/sagas']);
    });

    it('does not touch the view when it is left while the request runs, and refused afterwards', async () => {
      await create();
      let fail!: (err: unknown) => void;
      auth.changePassword.mockReturnValue(new Promise<void>((_, reject) => (fail = reject)));
      fill();

      const done = fixture.componentInstance.submit();
      await settle();
      fixture.destroy();
      fail(
        httpError(
          400,
          problem('invalid_credentials', WRONG_PASSWORD, { currentPassword: [WRONG_PASSWORD] }),
        ),
      );

      await expect(done).resolves.toBeUndefined();
    });

    it('does not trim the passwords', async () => {
      await create();
      fill({ current: ' old ', new: ' new password here ', confirmation: ' new password here ' });

      await submit();

      expect(auth.changePassword).toHaveBeenCalledWith(' old ', ' new password here ');
    });

    it('goes to the login page, with a notice, when the session ended with the change', async () => {
      await create();
      auth.changePassword.mockImplementation(async () => auth.status.set('anonymous'));
      fill();

      await submit();

      expect(navigate).toHaveBeenCalledExactlyOnceWith('/login?reason=password');
    });

    it('shows that it is working, and changes the password once however often it is submitted', async () => {
      await create();
      let finish!: () => void;
      auth.changePassword.mockReturnValue(new Promise<void>((resolve) => (finish = resolve)));
      fill();

      await submit();
      expect(unavailable(submitButton())).toBe(true);
      expect(submitButton()?.textContent).toContain('Changing');
      submitButton()!
        .closest('form')!
        .dispatchEvent(new Event('submit', { cancelable: true }));
      await settle();
      expect(auth.changePassword).toHaveBeenCalledTimes(1);

      finish();
      await settle();
      expect(navigate).toHaveBeenCalledTimes(1);
    });
  });

  describe('where the keyboard cursor is', () => {
    it('stays on the button while the password is changed: the button is not disabled, so the keyboard keeps its place', async () => {
      await create();
      let finish!: () => void;
      auth.changePassword.mockReturnValue(new Promise<void>((resolve) => (finish = resolve)));
      fill();
      // jsdom does not move the focus on a click: put it where a keyboard user has it.
      submitButton()!.focus();
      expect(focused()).toBe(submitButton());

      await submit();

      expect(auth.changePassword).toHaveBeenCalledTimes(1);
      expect(submitButton()?.disabled).toBe(false);
      expect(unavailable(submitButton())).toBe(true);
      expect(focused()).toBe(submitButton());

      finish();
      await settle();
    });
  });

  describe('what the API refuses', () => {
    it('puts a wrong current password under its field, clears it and puts the cursor in it', async () => {
      await create();
      auth.changePassword.mockRejectedValue(
        httpError(
          400,
          problem('invalid_credentials', WRONG_PASSWORD, { currentPassword: [WRONG_PASSWORD] }),
        ),
      );
      fill();

      await submit();

      expect(message('current')).toBe(WRONG_PASSWORD);
      expect(input('current').getAttribute('aria-invalid')).toBe('true');
      expect(input('current').getAttribute('aria-describedby')).toBe('account-current-error');
      expect(input('current').value).toBe('');
      expect(focused()).toBe(input('current'));
      expect(failureBanner()).toBeNull();
      // Nothing else needs typing again.
      expect(input('new').value).toBe('a brand new password');
      expect(input('confirmation').value).toBe('a brand new password');
      expect(unavailable(submitButton())).toBe(false);
      expect(navigate).not.toHaveBeenCalled();
    });

    it('puts a refusal of the new password under its field', async () => {
      await create();
      auth.changePassword.mockRejectedValue(
        httpError(
          400,
          problem('validation', 'The request is not valid.', {
            newPassword: ['The new password must differ from the current one.'],
          }),
        ),
      );
      fill();

      await submit();

      expect(message('new')).toBe('The new password must differ from the current one.');
      expect(input('new').getAttribute('aria-invalid')).toBe('true');
      expect(focused()).toBe(input('new'));
      expect(input('current').value).toBe('the old password');
    });

    it('does not lose a message for a member the form has no field for: the banner shows it', async () => {
      await create();
      auth.changePassword.mockRejectedValue(
        httpError(400, problem('validation', 'x', { other: ['Something else is wrong.'] })),
      );
      fill();

      await submit();

      expect(failureBanner()?.getAttribute('role')).toBe('alert');
      expect(failureBanner()?.textContent?.trim()).toBe('Something else is wrong.');
    });

    it('shows the API text when the account cannot change its password right now', async () => {
      await create();
      const detail =
        'The account is locked or disabled for now, so its password cannot be changed; try again later.';
      auth.changePassword.mockRejectedValue(
        httpError(400, problem('invalid_credentials', detail, { currentPassword: [detail] })),
      );
      fill();

      await submit();

      expect(message('current')).toBe(detail);
    });

    it('clears what the API said when the form is sent again', async () => {
      await create();
      auth.changePassword.mockRejectedValueOnce(
        httpError(
          400,
          problem('invalid_credentials', WRONG_PASSWORD, { currentPassword: [WRONG_PASSWORD] }),
        ),
      );
      auth.changePassword.mockImplementation(async () => auth.status.set('authenticated'));
      fill();
      await submit();
      expect(message('current')).toBe(WRONG_PASSWORD);

      type('current', 'the right password');
      await submit();

      expect(navigate).toHaveBeenCalledExactlyOnceWith('/sagas');
    });

    it('names the delay of a rate limit in the banner', async () => {
      await create();
      auth.changePassword.mockRejectedValue(
        httpError(429, problem('rate_limited', 'x'), { 'Retry-After': '25' }),
      );
      fill();

      await submit();

      expect(failureBanner()?.textContent?.trim()).toBe('Too many attempts. Try again in 25 s.');
    });

    it('says the API cannot be reached when no answer came', async () => {
      await create();
      auth.changePassword.mockRejectedValue(networkError());
      fill();

      await submit();

      expect(failureBanner()?.textContent?.trim()).toBe(CANNOT_REACH);
    });
  });
});
