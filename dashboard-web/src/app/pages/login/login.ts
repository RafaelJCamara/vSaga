import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  Signal,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { safeReturnUrl } from '../../guards/auth.guards';
import { AuthService } from '../../services/auth.service';
import { failureText } from '../../util/failure-text';
import { problemOf } from '../../util/http-error';

/** How often the page asks the API again while it cannot be reached. */
export const LOGIN_POLL_MS = 3000;

/** The one sentence for a refused sign-in, whatever the reason (an unknown user, a wrong password, a
 *  locked or a disabled account): the API answers them alike, and so does the page. */
export const SIGN_IN_FAILED =
  'Sign-in failed. Check the username and password; repeated failures lock the account for a while.';

/** Why the visitor is here, from the `reason` query parameter that the pages and the auth service set. */
const NOTICES = new Map([
  ['expired', 'Your session expired. Sign in again to continue.'],
  ['setup', 'The administrator account was created. Sign in to continue.'],
  ['password', 'Your password was changed. Sign in with the new password.'],
]);

/** What a failed sign-in shows. A refusal that carries the API's `invalid_credentials` is the uniform
 *  sentence; a 401 without a code is not a refusal of the credentials (the auth service raises one when the
 *  server accepted the sign-in but the browser kept no session), so it says its own reason. */
function signInFailure(err: unknown): string {
  const problem = problemOf(err, '');
  if (
    problem.code === 'invalid_credentials' &&
    (problem.status === 401 || problem.status === 400)
  ) {
    return SIGN_IN_FAILED;
  }
  return failureText(err, 'Sign-in failed. Try again.');
}

/**
 * The sign-in page: username and password, the notice of why the visitor is here, and a banner in place
 * of the form while the API cannot be reached.
 *
 * The three pages of the sign-in flow are template-driven (`FormsModule`, `ngModel` bound to signals), with
 * two choices that keep them cheap: the form carries `ngNoForm`, so the fields are standalone controls and
 * `submit` is the native event, and the checks (required, lengths, the confirmation) are made here, not by
 * Angular's validator directives. `@angular/forms` lives in the initial bundle, so `NgForm` and its
 * validators, used by nothing else, would add about 7 kB to it.
 */
@Component({
  selector: 'app-login',
  imports: [FormsModule],
  templateUrl: './login.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './login.scss',
})
export class Login {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly query = toSignal(inject(ActivatedRoute).queryParamMap, { requireSync: true });

  private readonly usernameField = viewChild<ElementRef<HTMLInputElement>>('usernameField');
  private readonly passwordField = viewChild<ElementRef<HTMLInputElement>>('passwordField');

  readonly username = signal('');
  readonly password = signal('');
  readonly busy = signal(false);
  readonly failure = signal<string | null>(null);
  readonly usernameError = signal<string | null>(null);
  readonly passwordError = signal<string | null>(null);

  /** Why the visitor was sent here, when a page said so. */
  readonly notice = computed(() => NOTICES.get(this.query().get('reason') ?? ''));
  /** The first session read never got an answer: the form would lead nowhere, so a banner replaces it. */
  readonly unreachable = computed(() => this.auth.status() === 'unreachable');
  /** The API answered, but its identity store is down (see `AuthService.signInUnavailable`). */
  readonly signInUnavailable = this.auth.signInUnavailable;

  /** Where a sign-in goes on to: the page the visitor wanted, if it is a page of this app. */
  private readonly returnUrl = computed(() => safeReturnUrl(this.query().get('returnUrl')));

  constructor() {
    afterNextRender(() => this.usernameField()?.nativeElement.focus());

    // While the API cannot be reached, ask again until it answers. The banner and the form follow the
    // status by themselves; a session that turns out to exist, or a setup that turns out to be due, is
    // sent on from here, since the guards only judge a navigation.
    effect((onCleanup) => {
      if (!this.unreachable()) return;
      const timer = setInterval(() => void this.askAgain(), LOGIN_POLL_MS);
      onCleanup(() => clearInterval(timer));
    });
  }

  async submit(): Promise<void> {
    if (this.busy()) return;
    const username = this.username().trim();
    const password = this.password();
    this.usernameError.set(username === '' ? 'Enter your username.' : null);
    this.passwordError.set(password === '' ? 'Enter your password.' : null);
    if (username === '' || password === '') {
      this.focus(username === '' ? this.usernameField : this.passwordField);
      return;
    }

    this.failure.set(null);
    this.busy.set(true);
    try {
      await this.auth.login(username, password);
    } catch (err) {
      this.failure.set(signInFailure(err));
      this.password.set('');
      this.busy.set(false);
      this.focus(this.passwordField);
      return;
    }
    await this.goTo(this.returnUrl());
    this.busy.set(false);
  }

  private async askAgain(): Promise<void> {
    await this.auth.refresh();
    if (this.auth.setupRequired()) await this.goTo('/setup');
    else if (this.auth.isAuthenticated()) await this.goTo(this.returnUrl());
  }

  private async goTo(url: string): Promise<void> {
    await this.router.navigateByUrl(url).catch(() => false);
  }

  /** Focuses the field once the next render has put it on the page. */
  private focus(field: Signal<ElementRef<HTMLInputElement> | undefined>): void {
    afterNextRender(() => field()?.nativeElement.focus(), { injector: this.injector });
  }
}
