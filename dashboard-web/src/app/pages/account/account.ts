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
import { Router } from '@angular/router';
import { AccessSummary } from '../../components/access-summary/access-summary';
import { AuthService } from '../../services/auth.service';
import { failureText } from '../../util/failure-text';
import { problemOf } from '../../util/http-error';
import { leaveTo, trackDestroyed } from '../../util/page-lifecycle';

/** The fields of the password form, top to bottom: the order focus goes to the first one with an error. */
const FIELDS = ['currentPassword', 'newPassword', 'confirmation'] as const;
type Field = (typeof FIELDS)[number];

function isServerField(key: string): key is 'currentPassword' | 'newPassword' {
  return key === 'currentPassword' || key === 'newPassword';
}

/**
 * The signed-in user's account: who they are, a form to change the password (the only thing a user who
 * must change it can do), and what they may do, as the server computed it. Built like the login page (see
 * there for why the form has no `NgForm`).
 */
@Component({
  selector: 'app-account',
  imports: [FormsModule, AccessSummary],
  templateUrl: './account.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './account.scss',
})
export class Account {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly destroyed = trackDestroyed();
  /** The three inputs in template order, which is the order of `FIELDS`. */
  private readonly inputs = viewChildren<NgModel, ElementRef<HTMLInputElement>>(NgModel, {
    read: ElementRef,
  });

  readonly currentPassword = signal('');
  readonly newPassword = signal('');
  readonly confirmation = signal('');

  readonly busy = signal(false);
  /** Set by the first submit: the form shows what is missing only after that, not while it is filled in. */
  readonly submitted = signal(false);
  /** What the API said about the fields of the request it refused, by field. */
  private readonly serverErrors = signal<Record<string, string[]>>({});
  /** A failure that no field explains: the network, a rate limit, a refusal naming a member unknown here. */
  readonly failure = signal<string | null>(null);

  readonly user = this.auth.user;
  /** True until the user has chosen a new password: their access is empty, and this is the only page open to them. */
  readonly mustChangePassword = computed(() => this.auth.user()?.mustChangePassword ?? false);
  /** The shortest password the policy accepts, from the session. */
  readonly minLength = this.auth.passwordMinLength;
  /** The confirmation differs from the new password. */
  readonly mismatch = computed(() => this.confirmation() !== this.newPassword());

  /** What is wrong with each field, once the form has been submitted; the API's message where it gave one. */
  readonly errors = computed<Partial<Record<Field, string>>>(() => {
    const server = this.serverErrors();
    const min = this.minLength();
    const next = this.newPassword();
    const errors: Partial<Record<Field, string>> = {};
    if (this.submitted()) {
      if (this.currentPassword() === '') errors.currentPassword = 'Enter your current password.';
      if (next === '') errors.newPassword = 'Enter a new password.';
      else if (min !== null && next.length < min) {
        errors.newPassword = `Use at least ${min} characters.`;
      }
      if (this.mismatch()) errors.confirmation = 'The passwords do not match.';
    }
    // The API's message about a value beats the form's own about the same field.
    for (const field of ['currentPassword', 'newPassword'] as const) {
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
      await this.auth.changePassword(this.currentPassword(), this.newPassword());
    } catch (err) {
      this.refused(err);
      this.busy.set(false);
      return;
    }
    // The API signs the user in again under the new password. Were it not to (the session ended with
    // the change), the sign-in page is the next step, and it says why.
    await this.goTo(this.auth.isAuthenticated() ? '/sagas' : '/login?reason=password');
    this.busy.set(false);
  }

  /** The API's message about `field` is about the value that was sent: typing in it ends it. */
  forget(field: Field): void {
    if (!(field in this.serverErrors())) return;
    this.serverErrors.update((errors) =>
      Object.fromEntries(Object.entries(errors).filter(([key]) => key !== field)),
    );
  }

  private refused(err: unknown): void {
    const problem = problemOf(err, '');
    this.serverErrors.set(problem.fieldErrors);
    // Messages for members this form has no field for are not lost: they join the banner.
    const unplaced = Object.entries(problem.fieldErrors)
      .filter(([key]) => !isServerField(key))
      .flatMap(([, messages]) => messages);
    const placed = Object.keys(problem.fieldErrors).some(isServerField);
    if (unplaced.length > 0) this.failure.set(unplaced.join(' '));
    else if (!placed)
      this.failure.set(failureText(err, 'The password could not be changed. Try again.'));
    // A wrong current password is typed again; nothing else about the form needs retyping.
    if (problem.fieldErrors['currentPassword']) this.currentPassword.set('');
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
    this.currentPassword.set('');
    this.newPassword.set('');
    this.confirmation.set('');
    await leaveTo(this.router, url, () => !this.destroyed());
  }
}
