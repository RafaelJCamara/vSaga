import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MockInstance, vi } from 'vitest';
import {
  AuthMock,
  AuthMockOptions,
  createAuthMock,
  provideAuthMock,
} from '../../testing/auth-mock';
import { httpError, networkError, problem } from '../../testing/http-error';
import { CANNOT_REACH } from '../../util/failure-text';
import { Setup } from './setup';

/** What the session says while the API waits for its first administrator and setup is open. */
const OPEN: AuthMockOptions = {
  status: 'anonymous',
  user: null,
  access: null,
  setupRequired: true,
  setupAvailable: true,
};

const WRONG_CODE =
  'The setup code is not correct. Copy it from the API log (it was logged at start), or from Dashboard:Setup:Code when that is set.';

describe('Setup', () => {
  let auth: AuthMock;
  let fixture: ComponentFixture<Setup>;
  let navigate: MockInstance<Router['navigateByUrl']>;

  async function create(options: AuthMockOptions = OPEN) {
    auth = createAuthMock(options);
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideAuthMock(auth)] });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    fixture = TestBed.createComponent(Setup);
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
  const input = (id: string) => el().querySelector<HTMLInputElement>(`#setup-${id}`)!;
  const message = (id: string) => el().querySelector(`#setup-${id}-error`)?.textContent?.trim();
  const submitButton = () => el().querySelector<HTMLButtonElement>('button[type="submit"]');
  const failureBanner = () => el().querySelector<HTMLElement>('.banner--error');
  const focused = () => document.activeElement;

  function type(id: string, value: string): void {
    const field = input(id);
    field.value = value;
    field.dispatchEvent(new Event('input'));
    // The app renders after an input event; a value the page resets later is compared with this one.
    fixture.detectChanges();
  }

  function fill(overrides: Partial<Record<string, string>> = {}): void {
    const values: Record<string, string> = {
      username: 'admin',
      'display-name': 'Ada Admin',
      password: 'a long enough password',
      confirmation: 'a long enough password',
      code: 'ABCD-EFGH-JKLM-NPQR',
      ...overrides,
    };
    for (const [id, value] of Object.entries(values)) type(id, value);
  }

  async function submit(): Promise<void> {
    submitButton()!.click();
    await settle();
  }

  /** The API signs the new administrator in, which the real service shows by reading the session again. */
  function setupSucceeds(): void {
    auth.setup.mockImplementation(async () => {
      auth.status.set('authenticated');
      auth.setupRequired.set(false);
      auth.setupAvailable.set(false);
    });
  }

  describe('the form', () => {
    it('asks for the username, display name, password, its confirmation and the setup code', async () => {
      await create();

      const labels = Array.from(el().querySelectorAll('label.label')).map((l) => [
        l.textContent?.trim(),
        l.getAttribute('for'),
      ]);
      expect(labels).toEqual([
        ['Username', 'setup-username'],
        ['Display name', 'setup-display-name'],
        ['Password', 'setup-password'],
        ['Repeat the password', 'setup-confirmation'],
        ['Setup code', 'setup-code'],
      ]);
      expect(el().querySelector('h1')?.textContent).toBe('Set up the dashboard');
    });

    it('says what each field is for, to browsers and password managers', async () => {
      await create();

      expect(input('username').autocomplete).toBe('username');
      expect(input('display-name').autocomplete).toBe('name');
      expect(input('password').autocomplete).toBe('new-password');
      expect(input('confirmation').autocomplete).toBe('new-password');
      expect(input('code').autocomplete).toBe('one-time-code');
      expect(input('password').type).toBe('password');
      expect(input('confirmation').type).toBe('password');
      expect(el().querySelectorAll('[aria-required="true"]')).toHaveLength(5);
    });

    it('takes the shortest password from the session, as a hint and as the minlength', async () => {
      await create({ ...OPEN, passwordMinLength: 16 });

      expect(input('password').getAttribute('minlength')).toBe('16');
      expect(el().querySelector('#setup-password-hint')?.textContent).toContain(
        'At least 16 characters.',
      );
      expect(input('password').getAttribute('aria-describedby')).toBe('setup-password-hint');
    });

    it('sets no minlength before the session says what the policy is', async () => {
      await create({ ...OPEN, passwordMinLength: null });

      expect(input('password').hasAttribute('minlength')).toBe(false);
      expect(el().querySelector('#setup-password-hint')?.textContent).toContain('long password');
    });

    it('says where to find the setup code', async () => {
      await create();

      const hint = el().querySelector('#setup-code-hint')?.textContent;
      expect(hint).toContain('API logged');
      expect(hint).toContain('docker compose logs dashboard-api');
      expect(hint).toContain('Dashboard:Setup:Code');
      expect(input('code').getAttribute('aria-describedby')).toBe('setup-code-hint');
    });

    it('shows no error until the form has been submitted', async () => {
      await create();
      type('password', 'x');
      type('confirmation', 'y');
      await settle();

      expect(el().querySelector('.field-error')).toBeNull();
      expect(el().querySelector('[aria-invalid]')).toBeNull();
    });
  });

  describe('a form that is not right', () => {
    it('names each missing field, without asking the API', async () => {
      await create();

      await submit();

      expect(auth.setup).not.toHaveBeenCalled();
      expect(message('username')).toBe('Enter a username.');
      expect(message('display-name')).toBe('Enter a display name.');
      expect(message('password')).toBe('Enter a password.');
      expect(message('code')).toBe('Enter the setup code.');
      expect(message('confirmation')).toBeUndefined();
    });

    it('marks each field in error as invalid and ties it to its message', async () => {
      await create();

      await submit();

      expect(input('username').getAttribute('aria-invalid')).toBe('true');
      expect(input('username').getAttribute('aria-describedby')).toBe('setup-username-error');
      expect(input('display-name').getAttribute('aria-describedby')).toBe(
        'setup-display-name-error',
      );
      expect(input('password').getAttribute('aria-describedby')).toBe(
        'setup-password-hint setup-password-error',
      );
      expect(input('code').getAttribute('aria-describedby')).toBe(
        'setup-code-hint setup-code-error',
      );
      expect(input('confirmation').hasAttribute('aria-invalid')).toBe(false);
    });

    it('puts the cursor in the first field in error', async () => {
      await create();
      fill({ username: '' });
      await submit();
      expect(focused()).toBe(input('username'));

      fill({ username: 'admin', 'display-name': '   ' });
      await submit();
      expect(focused()).toBe(input('display-name'));
    });

    it('rejects a password shorter than the policy, with the length from the session', async () => {
      await create({ ...OPEN, passwordMinLength: 14 });
      fill({ password: 'thirteen-char', confirmation: 'thirteen-char' });

      await submit();

      expect(message('password')).toBe('Use at least 14 characters.');
      expect(auth.setup).not.toHaveBeenCalled();
      expect(focused()).toBe(input('password'));
    });

    it('accepts a password of exactly the minimum length', async () => {
      await create({ ...OPEN, passwordMinLength: 12 });
      setupSucceeds();
      fill({ password: 'twelve-chars', confirmation: 'twelve-chars' });

      await submit();

      expect(auth.setup).toHaveBeenCalledTimes(1);
    });

    it('rejects a confirmation that differs from the password', async () => {
      await create();
      fill({ confirmation: 'something else entirely' });

      await submit();

      expect(message('confirmation')).toBe('The passwords do not match.');
      expect(input('confirmation').getAttribute('aria-invalid')).toBe('true');
      expect(auth.setup).not.toHaveBeenCalled();
      expect(focused()).toBe(input('confirmation'));
    });

    it('lets a message go as soon as the field is put right', async () => {
      await create();
      fill({ confirmation: 'something else entirely' });
      await submit();

      type('confirmation', 'a long enough password');
      await settle();

      expect(message('confirmation')).toBeUndefined();
    });
  });

  describe('creating the administrator', () => {
    it('sends the five values and goes to the saga list once the API has signed them in', async () => {
      await create();
      setupSucceeds();
      fill({ username: ' admin ', 'display-name': ' Ada Admin ', code: ' ABCD-EFGH-JKLM-NPQR ' });

      await submit();

      expect(auth.setup).toHaveBeenCalledExactlyOnceWith({
        username: 'admin',
        displayName: 'Ada Admin',
        password: 'a long enough password',
        code: 'ABCD-EFGH-JKLM-NPQR',
      });
      expect(navigate).toHaveBeenCalledExactlyOnceWith('/sagas');
    });

    it('goes to the login page, with a notice, when the session is not signed in afterwards', async () => {
      await create();
      fill();

      await submit();

      expect(navigate).toHaveBeenCalledExactlyOnceWith('/login?reason=setup');
    });

    it('shows that it is working, and creates the administrator once however often it is submitted', async () => {
      await create();
      let finish!: () => void;
      auth.setup.mockReturnValue(new Promise<void>((resolve) => (finish = resolve)));
      fill();

      await submit();
      expect(submitButton()?.disabled).toBe(true);
      expect(submitButton()?.textContent).toContain('Creating');
      submitButton()!
        .closest('form')!
        .dispatchEvent(new Event('submit', { cancelable: true }));
      await settle();
      expect(auth.setup).toHaveBeenCalledTimes(1);

      finish();
      await settle();
      expect(navigate).toHaveBeenCalledTimes(1);
    });

    it('keeps the form on the page while the administrator is created, though the session reads closed by then', async () => {
      await create();
      let finish!: () => void;
      auth.setup.mockImplementation(async () => {
        // What the auth service has done by the time the POST has been answered and the session read again.
        auth.status.set('authenticated');
        auth.setupRequired.set(false);
        auth.setupAvailable.set(false);
        await new Promise<void>((resolve) => (finish = resolve));
      });
      fill();

      await submit();

      expect(el().querySelector('h1')?.textContent).toBe('Set up the dashboard');
      expect(el().querySelector('form')).not.toBeNull();

      finish();
      await settle();
      expect(navigate).toHaveBeenCalledExactlyOnceWith('/sagas');
    });

    it('does not reload the page for a submit: the form is handled here', async () => {
      await create();
      const event = new Event('submit', { cancelable: true });

      el().querySelector('form')!.dispatchEvent(event);
      await settle();

      expect(event.defaultPrevented).toBe(true);
    });
  });

  describe('what the API refuses', () => {
    it('puts a wrong setup code under the code field and the cursor in it, keeping everything typed', async () => {
      await create();
      auth.setup.mockRejectedValue(
        httpError(400, problem('invalid_credentials', WRONG_CODE, { code: [WRONG_CODE] })),
      );
      fill({ code: 'WRONG-WRONG-WRON-GGGG' });

      await submit();

      expect(message('code')).toBe(WRONG_CODE);
      expect(input('code').getAttribute('aria-invalid')).toBe('true');
      expect(input('code').getAttribute('aria-describedby')).toBe(
        'setup-code-hint setup-code-error',
      );
      expect(focused()).toBe(input('code'));
      expect(failureBanner()).toBeNull();
      expect(input('password').value).toBe('a long enough password');
      expect(input('username').value).toBe('admin');
      expect(submitButton()?.disabled).toBe(false);
      expect(navigate).not.toHaveBeenCalled();
    });

    it('places each validation message under its field, the first of each', async () => {
      await create();
      auth.setup.mockRejectedValue(
        httpError(
          400,
          problem('validation', 'The request is not valid.', {
            username: [
              'Use 3 to 64 letters, digits or . _ @ + -, starting with a letter or a digit.',
            ],
            displayName: ['Enter 1 to 128 characters, with no control characters.'],
            password: ['The password is on a list of common passwords.', 'second message'],
          }),
        ),
      );
      fill();

      await submit();

      expect(message('username')).toBe(
        'Use 3 to 64 letters, digits or . _ @ + -, starting with a letter or a digit.',
      );
      expect(message('display-name')).toBe(
        'Enter 1 to 128 characters, with no control characters.',
      );
      expect(message('password')).toBe('The password is on a list of common passwords.');
      expect(failureBanner()).toBeNull();
      expect(focused()).toBe(input('username'));
    });

    it('does not lose a message for a member the form has no field for: the banner shows it', async () => {
      await create();
      auth.setup.mockRejectedValue(
        httpError(
          400,
          problem('validation', 'x', { unexpected: ['The request has an unexpected member.'] }),
        ),
      );
      fill();

      await submit();

      expect(failureBanner()?.getAttribute('role')).toBe('alert');
      expect(failureBanner()?.textContent?.trim()).toBe('The request has an unexpected member.');
    });

    it('clears what the API said when the form is sent again', async () => {
      await create();
      auth.setup.mockRejectedValueOnce(
        httpError(400, problem('invalid_credentials', WRONG_CODE, { code: [WRONG_CODE] })),
      );
      setupSucceeds();
      fill();
      await submit();
      expect(message('code')).toBe(WRONG_CODE);

      await submit();

      expect(navigate).toHaveBeenCalledTimes(1);
    });

    it('names the delay of a rate limit in the banner', async () => {
      await create();
      auth.setup.mockRejectedValue(
        httpError(429, problem('rate_limited', 'x'), { 'Retry-After': '9' }),
      );
      fill();

      await submit();

      expect(failureBanner()?.textContent?.trim()).toBe('Too many attempts. Try again in 9 s.');
    });

    it('says the API cannot be reached when no answer came', async () => {
      await create();
      auth.setup.mockRejectedValue(networkError());
      fill();

      await submit();

      expect(failureBanner()?.textContent?.trim()).toBe(CANNOT_REACH);
    });

    it('closes the form for a 409 setup_unavailable and says why, in the API words', async () => {
      await create();
      const reason =
        'First-run setup is over: a dashboard user already exists. Sign in, or ask an administrator for an account.';
      auth.setup.mockImplementation(async () => {
        // The auth service reads the session again before it rejects: setup is closed now.
        auth.setupRequired.set(false);
        auth.setupAvailable.set(false);
        throw httpError(409, problem('setup_unavailable', reason));
      });
      fill();

      await submit();

      expect(el().querySelector('form')).toBeNull();
      expect(el().querySelector('h1')?.textContent).toBe('First-run setup is closed');
      expect(el().querySelector('.banner--info')?.textContent?.trim()).toBe(reason);
    });
  });

  describe('when setup is closed', () => {
    const SEEDED = {
      code: 'setup_unavailable',
      detail:
        'First-run setup is not offered while Dashboard:Admin:Username or Dashboard:Admin:Password is set; restart the API to create that administrator.',
    };
    const CLOSED: AuthMockOptions = {
      ...OPEN,
      setupAvailable: false,
      setupProblem: SEEDED,
    };

    it('shows no form, and says an administrator exists or the seed keys are set', async () => {
      await create(CLOSED);

      expect(el().querySelector('form')).toBeNull();
      expect(el().querySelector('input')).toBeNull();
      expect(el().querySelector('h1')?.textContent).toBe('First-run setup is closed');
      const text = el().textContent;
      expect(text).toContain('an administrator already exists');
      expect(text).toContain('Dashboard:Admin:Username');
      expect(text).toContain('Dashboard:Admin:Password');
    });

    it("shows the API's reason, from the session", async () => {
      await create(CLOSED);

      expect(el().querySelector('.banner--info')?.textContent?.trim()).toBe(SEEDED.detail);
    });

    it('says how to find the setup code in the API log', async () => {
      await create(CLOSED);

      const text = el().textContent;
      expect(text).toContain('one-time setup code');
      expect(text).toContain('log');
      expect(text).toContain('docker compose logs dashboard-api');
      expect(text).toContain('Restart the API to get a new one');
    });

    it('links to the login page', async () => {
      await create(CLOSED);

      const link = el().querySelector<HTMLAnchorElement>('a[href="/login"]');
      expect(link?.textContent).toContain('Go to sign in');
    });

    it('goes to the login page when "Check again" finds that a user exists now', async () => {
      await create(CLOSED);
      auth.refresh.mockImplementation(async () => {
        auth.setupRequired.set(false);
        auth.setupProblem.set(null);
        return 'anonymous';
      });

      el().querySelector<HTMLButtonElement>('button.btn')!.click();
      await settle();

      expect(auth.refresh).toHaveBeenCalledTimes(1);
      expect(navigate).toHaveBeenCalledExactlyOnceWith('/login');
    });

    it('says it is still closed when "Check again" finds no change', async () => {
      await create(CLOSED);

      el().querySelector<HTMLButtonElement>('button.btn')!.click();
      await settle();

      expect(el().querySelector('p[role="status"]')?.textContent).toContain('still closed');
      expect(navigate).not.toHaveBeenCalled();
    });

    it('shows the form when "Check again" finds that setup is open', async () => {
      await create(CLOSED);
      auth.refresh.mockImplementation(async () => {
        auth.setupAvailable.set(true);
        auth.setupProblem.set(null);
        return 'anonymous';
      });

      el().querySelector<HTMLButtonElement>('button.btn')!.click();
      await settle();

      expect(el().querySelector('h1')?.textContent).toBe('Set up the dashboard');
      expect(el().querySelector('#setup-code')).not.toBeNull();
    });

    it('is disabled while it checks', async () => {
      await create(CLOSED);
      let finish!: () => void;
      auth.refresh.mockReturnValue(new Promise((resolve) => (finish = () => resolve('anonymous'))));

      const check = el().querySelector<HTMLButtonElement>('button.btn')!;
      check.click();
      fixture.detectChanges();
      expect(check.disabled).toBe(true);
      expect(check.textContent).toContain('Checking');

      finish();
      await settle();
      expect(check.disabled).toBe(false);
    });
  });
});
