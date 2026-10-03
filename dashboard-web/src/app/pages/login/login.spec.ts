import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  ActivatedRoute,
  ParamMap,
  Router,
  convertToParamMap,
  provideRouter,
} from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { MockInstance, vi } from 'vitest';
import {
  AuthMock,
  AuthMockOptions,
  createAuthMock,
  provideAuthMock,
} from '../../testing/auth-mock';
import { httpError, networkError, problem } from '../../testing/http-error';
import { CANNOT_REACH } from '../../util/failure-text';
import { LOGIN_POLL_MS, Login, SIGN_IN_FAILED } from './login';

const ANONYMOUS: AuthMockOptions = { status: 'anonymous', user: null, access: null };

describe('Login', () => {
  let auth: AuthMock;
  let fixture: ComponentFixture<Login>;
  let navigate: MockInstance<Router['navigateByUrl']>;
  let query: BehaviorSubject<ParamMap>;

  async function create(options: AuthMockOptions = ANONYMOUS, params: Record<string, string> = {}) {
    auth = createAuthMock(options);
    query = new BehaviorSubject(convertToParamMap(params));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideAuthMock(auth),
        { provide: ActivatedRoute, useValue: { queryParamMap: query } },
      ],
    });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    fixture = TestBed.createComponent(Login);
    fixture.detectChanges();
    await settle();
  }

  /** Lets the promises of the component and of its mocks finish, then renders. */
  async function settle(): Promise<void> {
    if (vi.isFakeTimers()) await vi.advanceTimersByTimeAsync(0);
    else await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
    await fixture.whenStable();
  }

  const el = () => fixture.nativeElement as HTMLElement;
  const username = () => el().querySelector<HTMLInputElement>('#login-username');
  const password = () => el().querySelector<HTMLInputElement>('#login-password');
  const submitButton = () => el().querySelector<HTMLButtonElement>('button[type="submit"]');
  const banner = (selector = '.banner') => el().querySelector<HTMLElement>(selector);
  const focused = () => document.activeElement;

  function type(input: HTMLInputElement | null, value: string): void {
    input!.value = value;
    input!.dispatchEvent(new Event('input'));
    // The app renders after an input event; a value the page resets later is compared with this one.
    fixture.detectChanges();
  }

  async function signIn(user = 'alice', pass = 'correct horse'): Promise<void> {
    type(username(), user);
    type(password(), pass);
    submitButton()!.click();
    await settle();
  }

  afterEach(() => {
    vi.useRealTimers();
  });

  it('labels every field and says what each is for, to browsers and password managers', async () => {
    await create();

    expect(el().querySelector('label[for="login-username"]')?.textContent).toContain('Username');
    expect(el().querySelector('label[for="login-password"]')?.textContent).toContain('Password');
    expect(username()?.getAttribute('autocomplete')).toBe('username');
    expect(password()?.getAttribute('autocomplete')).toBe('current-password');
    expect(password()?.type).toBe('password');
    expect(el().querySelector('h1')?.textContent).toBe('Sign in');
    // The form says nothing while nothing has gone wrong.
    expect(banner()).toBeNull();
    expect(el().querySelector('[aria-invalid]')).toBeNull();
  });

  it('puts the cursor in the username field once the page is rendered', async () => {
    await create();

    expect(focused()).toBe(username());
  });

  describe('signing in', () => {
    it('signs in with the username and password and goes to the saga list', async () => {
      await create();

      await signIn('  alice ', 'correct horse');

      // The username is trimmed (a pasted trailing space is never part of one), the password is not.
      expect(auth.login).toHaveBeenCalledExactlyOnceWith('alice', 'correct horse');
      expect(navigate).toHaveBeenCalledExactlyOnceWith('/sagas');
    });

    it('does not trim the password', async () => {
      await create();

      await signIn('alice', ' padded ');

      expect(auth.login).toHaveBeenCalledWith('alice', ' padded ');
    });

    it('goes back to the page the visitor was headed for', async () => {
      await create(ANONYMOUS, { returnUrl: '/sagas/OrderSaga/abc?tab=timeline' });

      await signIn();

      expect(navigate).toHaveBeenCalledExactlyOnceWith('/sagas/OrderSaga/abc?tab=timeline');
    });

    it.each([
      '//evil.example',
      '/\\evil.example',
      'https://evil.example',
      '/login',
      '/setup',
      'evil',
    ])('ignores the return URL %s and goes to the saga list', async (returnUrl) => {
      await create(ANONYMOUS, { returnUrl });

      await signIn();

      expect(navigate).toHaveBeenCalledExactlyOnceWith('/sagas');
    });

    it('follows a return URL that changes while the page is open', async () => {
      await create(ANONYMOUS, { returnUrl: '/sagas/A/1' });
      query.next(convertToParamMap({ returnUrl: '/account' }));

      await signIn();

      expect(navigate).toHaveBeenCalledExactlyOnceWith('/account');
    });

    it('shows that it is working, and signs in once however often it is submitted', async () => {
      await create();
      let finish!: () => void;
      auth.login.mockReturnValue(new Promise<void>((resolve) => (finish = resolve)));

      type(username(), 'alice');
      type(password(), 'correct horse');
      submitButton()!.click();
      await settle();
      expect(submitButton()?.disabled).toBe(true);
      expect(submitButton()?.textContent).toContain('Signing in');
      submitButton()!
        .closest('form')!
        .dispatchEvent(new Event('submit', { cancelable: true }));
      await settle();

      expect(auth.login).toHaveBeenCalledTimes(1);

      finish();
      await settle();

      expect(navigate).toHaveBeenCalledTimes(1);
      expect(submitButton()?.disabled).toBe(false);
    });

    it('does not reload the page for a submit: the form is handled here', async () => {
      await create();
      type(username(), 'alice');
      type(password(), 'pw');
      const submit = new Event('submit', { cancelable: true });

      el().querySelector('form')!.dispatchEvent(submit);
      await settle();

      expect(submit.defaultPrevented).toBe(true);
    });
  });

  describe('a refused sign-in', () => {
    it.each([
      [401, 'a 401'],
      [400, 'a 400'],
    ])('shows the one uniform text for %s with invalid_credentials', async (status) => {
      await create();
      auth.login.mockRejectedValue(
        httpError(
          status,
          problem(
            'invalid_credentials',
            'The username or password is not correct, or the account cannot sign in right now.',
          ),
        ),
      );

      await signIn('mallory', 'guess');

      const alert = banner('.banner--error');
      expect(alert?.getAttribute('role')).toBe('alert');
      expect(alert?.textContent?.trim()).toBe(SIGN_IN_FAILED);
    });

    it('says the same whatever the account state behind the refusal', async () => {
      await create();
      const texts = new Set<string>();
      for (const detail of ['unknown user', 'wrong password', 'locked', 'disabled']) {
        auth.login.mockRejectedValue(httpError(401, problem('invalid_credentials', detail)));
        await signIn('someone', 'something');
        texts.add(banner('.banner--error')!.textContent!.trim());
      }

      expect([...texts]).toEqual([SIGN_IN_FAILED]);
    });

    it('clears the password, keeps the username and puts the cursor back in the password field', async () => {
      await create();
      auth.login.mockRejectedValue(httpError(401, problem('invalid_credentials', 'nope')));

      await signIn('alice', 'wrong');

      expect(password()?.value).toBe('');
      expect(username()?.value).toBe('alice');
      expect(focused()).toBe(password());
      expect(navigate).not.toHaveBeenCalled();
      // The button is back, so the next attempt can be made.
      expect(submitButton()?.disabled).toBe(false);
    });

    it('clears the failure when the next attempt starts', async () => {
      await create();
      auth.login.mockRejectedValueOnce(httpError(401, problem('invalid_credentials', 'nope')));
      await signIn('alice', 'wrong');
      expect(banner('.banner--error')).not.toBeNull();

      type(password(), 'right');
      submitButton()!.click();
      await settle();

      expect(banner('.banner--error')).toBeNull();
      expect(navigate).toHaveBeenCalledTimes(1);
    });

    it('does not call a 401 without a code a wrong password: it says what the service said', async () => {
      await create();
      // What AuthService.login rejects with when the server accepted the sign-in but the session stayed anonymous.
      auth.login.mockRejectedValue(
        httpError(401, {
          title: 'Sign-in did not start a session',
          detail:
            'The server accepted the sign-in but no session was established. Check that the browser accepts cookies for this site.',
        }),
      );

      await signIn();

      const text = banner('.banner--error')?.textContent?.trim();
      expect(text).toBe(
        'The server accepted the sign-in but no session was established. Check that the browser accepts cookies for this site.',
      );
      expect(text).not.toBe(SIGN_IN_FAILED);
    });

    it('shows the could-not-confirm message when the session read after the sign-in failed', async () => {
      await create();
      auth.login.mockRejectedValue(
        httpError(0, {
          title: 'Sign-in not confirmed',
          detail: 'Signed in, but the session could not be confirmed; try again.',
        }),
      );

      await signIn();

      expect(banner('.banner--error')?.textContent?.trim()).toBe(
        'Signed in, but the session could not be confirmed; try again.',
      );
    });

    it('says the API cannot be reached when no answer came', async () => {
      await create();
      auth.login.mockRejectedValue(networkError());

      await signIn();

      expect(banner('.banner--error')?.textContent?.trim()).toBe(CANNOT_REACH);
    });

    it('says the API is not answering properly for a 5xx', async () => {
      await create();
      auth.login.mockRejectedValue(httpError(502, '<html>Bad Gateway</html>'));

      await signIn();

      expect(banner('.banner--error')?.textContent).toContain('HTTP 502');
    });

    it('names the delay of a rate limit, from Retry-After', async () => {
      await create();
      auth.login.mockRejectedValue(
        httpError(429, problem('rate_limited', 'Too many attempts; try again in 17 s.'), {
          'Retry-After': '17',
        }),
      );

      await signIn();

      expect(banner('.banner--error')?.textContent?.trim()).toBe(
        'Too many attempts. Try again in 17 s.',
      );
    });

    it('says sign-in is unavailable when the API is up but its identity store is not', async () => {
      await create();
      auth.login.mockRejectedValue(
        httpError(503, problem('identity_unavailable', 'The identity store is not ready.')),
      );

      await signIn();

      expect(banner('.banner--error')?.textContent).toContain('Sign-in is unavailable');
    });
  });

  describe('a form that is not filled in', () => {
    it('asks for both fields without signing in', async () => {
      await create();

      submitButton()!.click();
      await settle();

      expect(auth.login).not.toHaveBeenCalled();
      expect(el().querySelector('#login-username-error')?.textContent).toBe('Enter your username.');
      expect(el().querySelector('#login-password-error')?.textContent).toBe('Enter your password.');
    });

    it('marks the fields as invalid and ties each to its message', async () => {
      await create();

      submitButton()!.click();
      await settle();

      expect(username()?.getAttribute('aria-invalid')).toBe('true');
      expect(username()?.getAttribute('aria-describedby')).toBe('login-username-error');
      expect(password()?.getAttribute('aria-invalid')).toBe('true');
      expect(password()?.getAttribute('aria-describedby')).toBe('login-password-error');
    });

    it('puts the cursor in the first field that is empty', async () => {
      await create();
      type(username(), 'alice');

      submitButton()!.click();
      await settle();
      expect(focused()).toBe(password());

      type(username(), '   ');
      type(password(), 'x');
      submitButton()!.click();
      await settle();
      expect(focused()).toBe(username());
      expect(auth.login).not.toHaveBeenCalled();
    });

    it('takes a message away as soon as its field is typed in', async () => {
      await create();
      submitButton()!.click();
      await settle();

      type(username(), 'a');
      await settle();

      expect(el().querySelector('#login-username-error')).toBeNull();
      expect(username()?.hasAttribute('aria-invalid')).toBe(false);
      expect(el().querySelector('#login-password-error')).not.toBeNull();
    });
  });

  describe('the notice of why the visitor is here', () => {
    it('says that the session expired', async () => {
      await create(ANONYMOUS, { returnUrl: '/sagas', reason: 'expired' });

      const notice = banner('.banner--info');
      expect(notice?.getAttribute('role')).toBe('status');
      expect(notice?.textContent?.trim()).toBe('Your session expired. Sign in again to continue.');
      // The form is there under it.
      expect(username()).not.toBeNull();
    });

    it.each([
      ['setup', 'The administrator account was created. Sign in to continue.'],
      ['password', 'Your password was changed. Sign in with the new password.'],
    ])('says what happened for the reason %s', async (reason, text) => {
      await create(ANONYMOUS, { reason });

      expect(banner('.banner--info')?.textContent?.trim()).toBe(text);
    });

    it.each<Record<string, string>>([{}, { reason: 'nonsense' }, { reason: 'constructor' }])(
      'shows no notice for %j',
      async (params) => {
        await create(ANONYMOUS, params);

        expect(banner()).toBeNull();
      },
    );
  });

  describe('while the API cannot be reached', () => {
    const UNREACHABLE: AuthMockOptions = { ...ANONYMOUS, status: 'unreachable' };

    it('replaces the form with a banner that says so', async () => {
      await create(UNREACHABLE);

      const alert = banner('.banner--warning');
      expect(alert?.getAttribute('role')).toBe('alert');
      expect(alert?.textContent).toContain('Cannot reach the dashboard API');
      expect(alert?.textContent).toContain('tries again every few seconds');
      expect(el().querySelector('form')).toBeNull();
      expect(username()).toBeNull();
    });

    it('says sign-in is unavailable when the API answered 503 identity_unavailable', async () => {
      await create({ ...UNREACHABLE, signInUnavailable: true });

      expect(banner('.banner--warning')?.textContent).toContain('Sign-in is unavailable');
      expect(banner('.banner--warning')?.textContent).not.toContain('Cannot reach');
    });

    it('asks the session again every 3 seconds, and only while it cannot be reached', async () => {
      vi.useFakeTimers();
      await create(UNREACHABLE);
      expect(auth.refresh).not.toHaveBeenCalled();

      await vi.advanceTimersByTimeAsync(LOGIN_POLL_MS - 1);
      expect(auth.refresh).not.toHaveBeenCalled();
      await vi.advanceTimersByTimeAsync(1);
      expect(auth.refresh).toHaveBeenCalledTimes(1);
      await vi.advanceTimersByTimeAsync(LOGIN_POLL_MS);
      expect(auth.refresh).toHaveBeenCalledTimes(2);
      expect(LOGIN_POLL_MS).toBe(3000);
    });

    it('shows the form and stops asking once the API answers', async () => {
      vi.useFakeTimers();
      await create(UNREACHABLE);
      auth.refresh.mockImplementation(async () => {
        auth.status.set('anonymous');
        return 'anonymous';
      });

      await vi.advanceTimersByTimeAsync(LOGIN_POLL_MS);
      await settle();

      expect(banner('.banner--warning')).toBeNull();
      expect(username()).not.toBeNull();
      expect(navigate).not.toHaveBeenCalled();
      await vi.advanceTimersByTimeAsync(LOGIN_POLL_MS * 3);
      expect(auth.refresh).toHaveBeenCalledTimes(1);
    });

    it('goes to setup when the API turns out to be waiting for its first administrator', async () => {
      vi.useFakeTimers();
      await create(UNREACHABLE);
      auth.refresh.mockImplementation(async () => {
        auth.status.set('anonymous');
        auth.setupRequired.set(true);
        return 'anonymous';
      });

      await vi.advanceTimersByTimeAsync(LOGIN_POLL_MS);

      expect(navigate).toHaveBeenCalledExactlyOnceWith('/setup');
    });

    it('goes on to the return URL when the session turns out to exist', async () => {
      vi.useFakeTimers();
      await create(UNREACHABLE, { returnUrl: '/sagas/OrderSaga/abc' });
      auth.refresh.mockImplementation(async () => {
        auth.status.set('authenticated');
        return 'authenticated';
      });

      await vi.advanceTimersByTimeAsync(LOGIN_POLL_MS);

      expect(navigate).toHaveBeenCalledExactlyOnceWith('/sagas/OrderSaga/abc');
    });

    it('does not ask at all while the API is reachable, and stops when the page is left', async () => {
      vi.useFakeTimers();
      await create(ANONYMOUS);
      await vi.advanceTimersByTimeAsync(LOGIN_POLL_MS * 3);
      expect(auth.refresh).not.toHaveBeenCalled();

      auth.status.set('unreachable');
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(LOGIN_POLL_MS);
      expect(auth.refresh).toHaveBeenCalledTimes(1);

      fixture.destroy();
      await vi.advanceTimersByTimeAsync(LOGIN_POLL_MS * 3);
      expect(auth.refresh).toHaveBeenCalledTimes(1);
    });
  });
});
