import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChildren,
} from '@angular/core';
import { FormsModule, NgModel } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { failureText } from '../../util/failure-text';
import { problemOf } from '../../util/http-error';
import { leaveTo, trackDestroyed } from '../../util/page-lifecycle';

/** The fields of the form, top to bottom: the order focus goes to the first one with an error. */
const FIELDS = ['username', 'displayName', 'password', 'confirmation', 'code'] as const;
type Field = (typeof FIELDS)[number];

/** The members of the setup request that the API validates and names in `errors`. */
const SERVER_FIELDS: readonly Field[] = ['username', 'displayName', 'password', 'code'];

function isServerField(key: string): key is Field {
  return (SERVER_FIELDS as readonly string[]).includes(key);
}

/**
 * First-run setup: the form that creates the first administrator with the one-time code the API logged,
 * or, while setup cannot be completed, the notice that says why. Built like the login page (see there for
 * why the form has no `NgForm`).
 */
@Component({
  selector: 'app-setup',
  imports: [FormsModule, RouterLink],
  templateUrl: './setup.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './setup.scss',
})
export class Setup {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly destroyed = trackDestroyed();
  /** The five inputs in template order, which is the order of `FIELDS`. */
  private readonly inputs = viewChildren<NgModel, ElementRef<HTMLInputElement>>(NgModel, {
    read: ElementRef,
  });

  readonly username = signal('');
  readonly displayName = signal('');
  readonly password = signal('');
  readonly confirmation = signal('');
  readonly code = signal('');

  readonly busy = signal(false);
  readonly checking = signal(false);
  /** Set by the first submit: the form shows what is missing only after that, not while it is filled in. */
  readonly submitted = signal(false);
  /** What the API said about the fields of the request it refused, by field. */
  private readonly serverErrors = signal<Record<string, string[]>>({});
  /** A failure that no field explains: the network, a rate limit, a refusal naming a member unknown here. */
  readonly failure = signal<string | null>(null);
  /** Why a setup that was open when the page loaded is closed now: the API's answer to the attempt. */
  private readonly closedByAttempt = signal<string | null>(null);
  /** What "Check again" found when it found no change: setup still closed, or the check itself failed. */
  readonly checkMessage = signal<string | null>(null);

  /** The shortest password the policy accepts, from the session. */
  readonly minLength = this.auth.passwordMinLength;
  /** Setup cannot be completed: the session says so, or the API refused the attempt as 409 (its answer
   *  reaches the page before the session read that follows, which may be slow or fail). Not while a request
   *  is in flight: the session reads closed the moment the administrator exists, and the form must not give
   *  way to the notice on the way to the saga list. */
  readonly closed = computed(
    () => (!this.auth.setupAvailable() || this.closedByAttempt() !== null) && !this.busy(),
  );
  /** Why setup is closed, in the API's words: the session names it while setup is required, and an
   *  attempt that lost the race says it. */
  readonly closedDetail = computed(
    () => this.auth.setupProblem()?.detail ?? this.closedByAttempt() ?? null,
  );
  /** The confirmation differs from the password. */
  readonly mismatch = computed(() => this.confirmation() !== this.password());

  /** What is wrong with each field, once the form has been submitted; the API's message where it gave one. */
  readonly errors = computed<Partial<Record<Field, string>>>(() => {
    const server = this.serverErrors();
    const min = this.minLength();
    const checked = this.submitted();
    const password = this.password();
    const errors: Partial<Record<Field, string>> = {};
    if (checked) {
      if (this.username().trim() === '') errors.username = 'Enter a username.';
      if (this.displayName().trim() === '') errors.displayName = 'Enter a display name.';
      if (password === '') errors.password = 'Enter a password.';
      else if (min !== null && password.length < min) {
        errors.password = `Use at least ${min} characters.`;
      }
      if (this.mismatch()) errors.confirmation = 'The passwords do not match.';
      if (this.code().trim() === '') errors.code = 'Enter the setup code.';
    }
    // The API's message about a value beats the form's own about the same field.
    for (const field of SERVER_FIELDS) {
      const message = server[field]?.[0];
      if (message) errors[field] = message;
    }
    return errors;
  });

  async submit(): Promise<void> {
    if (this.busy()) return;
    this.submitted.set(true);
    this.serverErrors.set({});
    this.failure.set(null);
    if (this.focusFirstError()) return;

    this.busy.set(true);
    try {
      await this.auth.setup({
        username: this.username().trim(),
        displayName: this.displayName().trim(),
        password: this.password(),
        code: this.code().trim(),
      });
    } catch (err) {
      this.refused(err);
      this.busy.set(false);
      return;
    }
    // The API signs the new administrator in. Were it not to, the sign-in page is the next step.
    await this.goTo(this.auth.isAuthenticated() ? '/sagas' : '/login?reason=setup');
    this.busy.set(false);
  }

  /** The API's message about `field` is about the value that was sent: typing in it ends it. */
  forget(field: Field): void {
    if (!(field in this.serverErrors())) return;
    this.serverErrors.update((errors) =>
      Object.fromEntries(Object.entries(errors).filter(([key]) => key !== field)),
    );
  }

  /** Looks at the session again, for a setup that has been opened (or closed for good) meanwhile. */
  async checkAgain(): Promise<void> {
    if (this.checking()) return;
    this.checking.set(true);
    this.checkMessage.set(null);
    await this.auth.refresh();
    this.checking.set(false);
    if (this.destroyed()) return;
    if (this.auth.sessionReadFailed()) {
      // What the session says now is what it said before: that is no answer.
      this.checkMessage.set('Could not check. Try again.');
    } else if (this.auth.setupAvailable()) {
      this.closedByAttempt.set(null);
    } else if (this.auth.setupRequired()) {
      this.checkMessage.set('Setup is still closed.');
    } else {
      await this.goTo('/login');
    }
  }

  private refused(err: unknown): void {
    const problem = problemOf(err, '');
    if (problem.code === 'setup_unavailable') {
      // Closes the form at once, with the API's reason, whether or not the session read that follows
      // (and shows the same) has landed.
      this.closedByAttempt.set(problem.message || 'First-run setup is not available.');
      return;
    }

    this.serverErrors.set(problem.fieldErrors);
    // Messages for members this form has no field for are not lost: they join the banner.
    const unplaced = Object.entries(problem.fieldErrors)
      .filter(([key]) => !isServerField(key))
      .flatMap(([, messages]) => messages);
    const placed = Object.keys(problem.fieldErrors).some(isServerField);
    if (unplaced.length > 0) this.failure.set(unplaced.join(' '));
    else if (!placed) this.failure.set(failureText(err, 'Setup failed. Try again.'));
    this.focusFirstError();
  }

  /** Focuses the first field with an error once the next render shows it; false when there is none. */
  private focusFirstError(): boolean {
    const errors = this.errors();
    const first = FIELDS.findIndex((field) => errors[field]);
    if (first < 0) return false;
    if (!this.destroyed()) {
      afterNextRender(() => this.inputs()[first]?.nativeElement.focus(), {
        injector: this.injector,
      });
    }
    return true;
  }

  /** Away from the form: the passwords are not kept in the page (and the empty fields are not errors). */
  private async goTo(url: string): Promise<void> {
    this.submitted.set(false);
    this.password.set('');
    this.confirmation.set('');
    await leaveTo(this.router, url, () => !this.destroyed());
  }
}
