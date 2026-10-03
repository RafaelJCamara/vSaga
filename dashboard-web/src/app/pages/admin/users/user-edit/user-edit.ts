import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AccessSummary } from '../../../../components/access-summary/access-summary';
import { ConfirmButton } from '../../../../components/confirm-button/confirm-button';
import { LocalTime } from '../../../../components/local-time/local-time';
import { AuthService } from '../../../../services/auth.service';
import {
  AdminFailure,
  adminFailure,
  belongsToField,
  placeFieldErrors,
} from '../../../../util/admin-failure';
import { leaveTo, trackDestroyed } from '../../../../util/page-lifecycle';
import { explainAccess, originsOf, sessionAccessOf } from '../../access-explain';
import { AdminUser, Grant } from '../../admin.model';
import { AdminStore } from '../../admin.store';
import { GrantsEditor, grantsBody, scopedWithoutTypes } from '../../grants-editor/grants-editor';
import { UserStatus, userStatus } from '../user-status';

/** The fields of the form, top to bottom: the order focus goes to the first one with an error. */
const FIELDS = ['username', 'displayName', 'password', 'confirmation', 'grants'] as const;
type Field = (typeof FIELDS)[number];
/** The members of a user request the API names in its errors (`confirmation` is only this form's). */
const REQUEST_FIELDS = ['username', 'displayName', 'password', 'grants'];

/** The API's limits (`AccessValidation`, `PasswordPolicy`), so the form says so before sending. */
const MAX_USERNAME = 64;
const MAX_DISPLAY_NAME = 128;
const MAX_PASSWORD = 128;

/**
 * One user: a new one (`/admin/users/new`) or the one the URL names. A new user has a username (which cannot
 * change afterwards), a display name, a password with its confirmation and "Require a change at next sign-in"
 * (on by default); an existing one has a display name, Enabled, and, below the form, Reset password (an inline
 * sub-form), Unlock while a lockout is in force, and Delete. Both have the grants editor and the effective
 * access: what the draft would hold, with the grants of the user's teams, computed here by `explainAccess` and
 * shown by `AccessSummary` (so it follows the draft, saved or not). The teams are shown, with a link to each,
 * and cannot be changed here: membership is written through the team, and a user payload has no `teamIds`.
 *
 * On the signed-in user's own record Enabled and Delete are off, each with its reason. Nothing else is special
 * there: editing one's own grants is allowed, and the API refuses what would leave nobody to manage access.
 *
 * Built like the role page (see `RoleEdit`): `ngNoForm`, `ngModel` bound to signals, the checks made here, and
 * a failure shown where it belongs (`adminFailure`): a field's messages under the field (and `grants[0]...`
 * with that grant), a rule (`last_administrator`, a taken username) in a banner that keeps the draft, 404 as
 * "This no longer exists", 403 as the lost permission. The draft belongs to the user: what is typed while a
 * request runs is never overwritten by its answer (`revision` counts the changes of the draft). One request
 * runs at a time: saving, resetting the password, unlocking and deleting each refuse while another is running.
 */
@Component({
  selector: 'app-user-edit',
  imports: [FormsModule, RouterLink, AccessSummary, ConfirmButton, GrantsEditor, LocalTime],
  templateUrl: './user-edit.html',
  styleUrl: './user-edit.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class UserEdit {
  protected readonly store = inject(AdminStore);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly destroyed = trackDestroyed();
  private readonly route = inject(ActivatedRoute);
  private readonly params = toSignal(this.route.paramMap, { requireSync: true });

  private readonly usernameField = viewChild<ElementRef<HTMLInputElement>>('usernameField');
  private readonly displayNameField = viewChild<ElementRef<HTMLInputElement>>('displayNameField');
  private readonly passwordField = viewChild<ElementRef<HTMLInputElement>>('passwordField');
  private readonly confirmationField = viewChild<ElementRef<HTMLInputElement>>('confirmationField');
  private readonly editor = viewChild(GrantsEditor);
  private readonly resetButton = viewChild<ElementRef<HTMLButtonElement>>('resetButton');
  private readonly newPasswordField = viewChild<ElementRef<HTMLInputElement>>('newPasswordField');
  private readonly resetConfirmationField =
    viewChild<ElementRef<HTMLInputElement>>('resetConfirmationField');
  /** The link of the "no longer exists" notice: focus goes there when it replaces the form. */
  private readonly goneLink = viewChild<ElementRef<HTMLAnchorElement>>('goneLink');

  /** The user in the URL; null on the page for a new one. */
  private readonly id = computed(() => this.params().get('id'));
  /** The user a delete is running for: the store reads the lists again before the delete returns, and the user
   *  is not in them any more; until the page has left, it must not turn into "This no longer exists". */
  private readonly pending = signal<AdminUser | null>(null);
  /** The user the page edits; null for a new user, and for an id the store does not know (it is gone). */
  protected readonly user = computed<AdminUser | null>(() => {
    const id = this.id();
    return id === null ? null : (this.store.users().find((u) => u.id === id) ?? this.pending());
  });
  protected readonly creating = computed(() => this.id() === null);
  protected readonly status = computed<UserStatus | null>(() => {
    const user = this.user();
    return user === null ? null : userStatus(user);
  });
  /** The signed-in user's own record. */
  protected readonly own = computed(() => {
    const user = this.user();
    return user !== null && this.auth.user()?.id === user.id;
  });
  /** The teams the user is in: shown, not edited. */
  protected readonly teams = computed(() => {
    const id = this.id();
    return id === null ? [] : this.store.teams().filter((t) => t.memberIds.includes(id));
  });

  readonly username = signal('');
  readonly displayName = signal('');
  readonly password = signal('');
  readonly confirmation = signal('');
  readonly requireChange = signal(true);
  readonly isEnabled = signal(true);
  readonly grants = signal<Grant[]>([]);
  /** Counts the changes of the draft (typing, ticking, grants, seeding): an answer re-seeds it only if none came since. */
  private revision = 0;

  readonly busy = signal(false);
  readonly deleting = signal(false);
  private readonly unlocking = signal(false);
  protected readonly resetting = signal(false);
  /** One request at a time: whatever runs keeps the others from starting. */
  protected readonly working = computed(
    () => this.busy() || this.deleting() || this.unlocking() || this.resetting(),
  );
  /** Set by the first save: the form shows what is missing only after that, not while it is filled in. */
  readonly submitted = signal(false);
  /** The API accepted the latest save of an existing user. */
  readonly saved = signal(false);
  /** What the API said about the request it refused, by request path (`grants[0].sagaTypes` included). */
  private readonly serverErrors = signal<Record<string, string[]>>({});
  /** A failure that no field explains: a rule, the network, a 403 and the like. */
  readonly failure = signal<AdminFailure | null>(null);
  /** The API refused the username as taken (`username_taken`): the username field is the one to change. */
  protected readonly usernameTaken = signal(false);
  /** The API answered 404: the user was deleted meanwhile. */
  private readonly gone = signal(false);
  protected readonly notFound = computed(
    () => this.gone() || (!this.creating() && this.user() === null),
  );

  // The reset-password sub-form, the unlock and the delete: below the form, with a banner and a status of their own.
  protected readonly resetOpen = signal(false);
  readonly newPassword = signal('');
  readonly newConfirmation = signal('');
  readonly resetRequireChange = signal(true);
  private readonly resetSubmitted = signal(false);
  private readonly resetServerErrors = signal<Record<string, string[]>>({});
  readonly actionFailure = signal<AdminFailure | null>(null);
  /** What the latest reset or unlock did, for the status region. */
  readonly notice = signal('');

  protected readonly heading = computed(() => this.user()?.username ?? 'New user');
  protected readonly usernameLimit = MAX_USERNAME;
  protected readonly displayNameLimit = MAX_DISPLAY_NAME;
  protected readonly passwordLimit = MAX_PASSWORD;
  /** The shortest password the policy accepts, from the session. */
  protected readonly minLength = this.auth.passwordMinLength;

  /** The grants the editor offers: every role, in the API's order. */
  protected readonly roles = this.store.roles;
  protected readonly sagaTypes = this.store.sagaTypes;
  /** The API's messages about the grants, for the editor to put with their grants. */
  protected readonly grantErrors = computed(() =>
    Object.fromEntries(
      Object.entries(this.serverErrors()).filter(([path]) => belongsToField(path, 'grants')),
    ),
  );

  /** Whether the user would hold nothing until they choose a password: the API says so of the user as stored. */
  protected readonly mustChange = computed(() =>
    this.creating() ? this.requireChange() : (this.user()?.mustChangePassword ?? false),
  );
  private readonly explanation = computed(() =>
    explainAccess({
      userId: this.id(),
      enabled: this.isEnabled(),
      grants: this.grants(),
      teams: this.store.teams(),
      roles: this.store.roles(),
      permissions: this.store.permissions(),
    }),
  );
  /** The access the draft confers, in the shape `AccessSummary` takes, and where each permission comes from. */
  protected readonly access = computed(() => sessionAccessOf(this.explanation()));
  protected readonly origins = computed(() => originsOf(this.explanation()));

  /** What is wrong with each field, once the form has been submitted; the API's message where it gave one. */
  protected readonly errors = computed<Partial<Record<Field, string>>>(() => {
    const server = this.serverErrors();
    const errors: Partial<Record<Field, string>> = {};
    if (this.submitted()) {
      const creating = this.creating();
      const username = this.username().trim();
      if (creating && username === '') errors.username = 'Enter a username.';
      else if (creating && username.length > MAX_USERNAME) {
        errors.username = `Use at most ${MAX_USERNAME} characters.`;
      }
      const name = this.displayName().trim();
      if (name === '') errors.displayName = 'Enter a display name.';
      else if (name.length > MAX_DISPLAY_NAME) {
        errors.displayName = `Use at most ${MAX_DISPLAY_NAME} characters.`;
      }
      if (creating) {
        const message = this.passwordProblem(this.password());
        if (message !== null) errors.password = message;
        if (this.password() !== this.confirmation())
          errors.confirmation = 'The passwords do not match.';
      }
    }
    // The API's message about a value beats the form's own about the same field.
    for (const field of ['username', 'displayName', 'password'] as const) {
      const messages = server[field];
      if (messages?.length) errors[field] = messages.join(' ');
    }
    return errors;
  });

  /** What is wrong with the reset form, once it has been submitted. */
  protected readonly resetErrors = computed<{ newPassword?: string; confirmation?: string }>(() => {
    const errors: { newPassword?: string; confirmation?: string } = {};
    if (this.resetSubmitted()) {
      const message = this.passwordProblem(this.newPassword());
      if (message !== null) errors.newPassword = message;
      if (this.newPassword() !== this.newConfirmation())
        errors.confirmation = 'The passwords do not match.';
    }
    const server = this.resetServerErrors()['newPassword'];
    if (server?.length) errors.newPassword = server.join(' ');
    return errors;
  });

  constructor() {
    // The router reuses this component when only the id changes, so the draft follows the URL.
    effect(() => {
      const id = this.id();
      untracked(() => this.seed(id));
    });
    // The notice replaces the form, and the focus was in it: it goes to the way out, not to the page's top.
    effect(() => {
      if (this.notFound()) {
        afterNextRender(() => this.goneLink()?.nativeElement.focus(), { injector: this.injector });
      }
    });
  }

  /** `field` was changed. The API's message about it was about the value that was sent, so it ends. */
  protected edited(field: string): void {
    this.revision++;
    this.saved.set(false);
    if (field === 'username') this.usernameTaken.set(false);
    this.serverErrors.update((errors) =>
      Object.fromEntries(Object.entries(errors).filter(([path]) => !belongsToField(path, field))),
    );
  }

  protected setEnabled(event: Event): void {
    this.isEnabled.set((event.target as HTMLInputElement).checked);
    this.edited('isEnabled');
  }

  protected setRequireChange(event: Event): void {
    this.requireChange.set((event.target as HTMLInputElement).checked);
    this.edited('mustChangePassword');
  }

  protected setResetRequireChange(event: Event): void {
    this.resetRequireChange.set((event.target as HTMLInputElement).checked);
  }

  protected setGrants(grants: Grant[]): void {
    this.grants.set(grants);
    this.edited('grants');
  }

  protected async save(): Promise<void> {
    if (this.working()) return;
    this.submitted.set(true);
    this.serverErrors.set({});
    this.failure.set(null);
    this.usernameTaken.set(false);
    this.saved.set(false);
    if (this.focusFirstError()) return;

    const id = this.id();
    const revision = this.revision;
    const grants = grantsBody(this.grants());
    this.busy.set(true);
    let user: AdminUser;
    try {
      user =
        id === null
          ? await this.store.saveUser(null, {
              username: this.username().trim(),
              displayName: this.displayName().trim(),
              password: this.password(),
              mustChangePassword: this.requireChange(),
              grants,
            })
          : await this.store.saveUser(id, {
              displayName: this.displayName().trim(),
              isEnabled: this.isEnabled(),
              grants,
            });
    } catch (err) {
      this.refused(adminFailure(err, 'The user could not be saved. Try again.'));
      this.busy.set(false);
      return;
    }
    if (this.destroyed()) return;
    if (id === null) {
      // Busy until the page has left: the form must not be usable again in between.
      this.password.set('');
      this.confirmation.set('');
      await leaveTo(this.router, '/admin/users', () => !this.destroyed());
      if (!this.destroyed()) this.busy.set(false);
      return;
    }
    this.busy.set(false);
    // What was typed while the request ran is the user's: the answer re-seeds the draft only if it is still the one that was sent.
    if (revision === this.revision) {
      this.load(user);
      this.saved.set(true);
    }
  }

  protected async remove(): Promise<void> {
    const user = this.user();
    if (user === null || this.working() || this.own()) return;
    this.pending.set(user);
    this.deleting.set(true);
    this.actionFailure.set(null);
    this.notice.set('');
    try {
      await this.store.deleteUser(user.id);
    } catch (err) {
      this.pending.set(null);
      this.deleting.set(false);
      this.refusedAction(adminFailure(err, 'The user could not be deleted. Try again.'));
      return;
    }
    await leaveTo(this.router, '/admin/users', () => !this.destroyed());
    if (!this.destroyed()) {
      this.pending.set(null);
      this.deleting.set(false);
    }
  }

  protected async unlock(): Promise<void> {
    const user = this.user();
    if (user === null || this.working()) return;
    this.unlocking.set(true);
    this.actionFailure.set(null);
    this.notice.set('');
    try {
      await this.store.unlockUser(user.id);
    } catch (err) {
      this.unlocking.set(false);
      this.refusedAction(adminFailure(err, 'The user could not be unlocked. Try again.'));
      return;
    }
    this.unlocking.set(false);
    if (this.destroyed()) return;
    this.notice.set(`${user.username} is unlocked.`);
    // The Unlock button is gone (nothing is locked now), and it held the focus.
    this.focusAfterRender(this.resetButton);
  }

  protected toggleReset(): void {
    if (this.resetting()) return;
    if (this.resetOpen()) {
      this.closeReset();
      this.focusAfterRender(this.resetButton);
      return;
    }
    this.notice.set('');
    this.actionFailure.set(null);
    this.resetOpen.set(true);
    this.focusAfterRender(this.newPasswordField);
  }

  /** The reset form's fields were typed in: what the API said about the password no longer applies. */
  protected resetEdited(): void {
    if ('newPassword' in this.resetServerErrors()) this.resetServerErrors.set({});
  }

  protected async reset(): Promise<void> {
    const user = this.user();
    if (user === null || this.working()) return;
    this.resetSubmitted.set(true);
    this.resetServerErrors.set({});
    this.actionFailure.set(null);
    this.notice.set('');
    const errors = this.resetErrors();
    if (errors.newPassword || errors.confirmation) {
      this.focusAfterRender(
        errors.newPassword ? this.newPasswordField : this.resetConfirmationField,
      );
      return;
    }

    const mustChangePassword = this.resetRequireChange();
    this.resetting.set(true);
    try {
      await this.store.resetPassword(user.id, {
        newPassword: this.newPassword(),
        mustChangePassword,
      });
    } catch (err) {
      const failure = adminFailure(err, 'The password could not be reset. Try again.');
      this.resetting.set(false);
      if (failure.kind === 'validation') {
        const { placed, unplaced } = placeFieldErrors(failure.fieldErrors, ['newPassword']);
        this.resetServerErrors.set(placed);
        if (unplaced.length > 0)
          this.actionFailure.set({ ...failure, kind: 'failed', message: unplaced.join(' ') });
        else this.focusAfterRender(this.newPasswordField);
      } else this.refusedAction(failure);
      return;
    }
    this.resetting.set(false);
    if (this.destroyed()) return;
    this.closeReset();
    this.notice.set(
      `The password of ${user.username} was reset and their sessions have ended.` +
        (mustChangePassword ? ' They must choose a new one at next sign-in.' : ''),
    );
    this.focusAfterRender(this.resetButton);
  }

  /** The whole-form check of a password against the session's policy, or null when it passes. */
  private passwordProblem(password: string): string | null {
    const min = this.minLength();
    if (password === '') return 'Enter a password.';
    if (min !== null && password.length < min) return `Use at least ${min} characters.`;
    if (password.length > MAX_PASSWORD) return `Use at most ${MAX_PASSWORD} characters.`;
    return null;
  }

  /** Shows a refusal of Save where it belongs. The draft is never touched: the user fixes it or tries again. */
  private refused(failure: AdminFailure): void {
    if (failure.kind === 'gone') {
      this.gone.set(true);
      return;
    }
    if (failure.kind !== 'validation') {
      this.failure.set(failure);
      if (failure.kind === 'conflict' && failure.code === 'username_taken') {
        this.usernameTaken.set(true);
        this.focusAfterRender(this.usernameField);
      }
      return;
    }
    this.serverErrors.set(failure.fieldErrors);
    const { unplaced } = placeFieldErrors(failure.fieldErrors, REQUEST_FIELDS);
    // Messages for request paths this form has no field for are not lost: they join the banner.
    if (unplaced.length > 0)
      this.failure.set({ ...failure, kind: 'failed', message: unplaced.join(' ') });
    this.focusFirstError();
  }

  /** Shows a refusal of Reset, Unlock or Delete in the account section. */
  private refusedAction(failure: AdminFailure): void {
    if (failure.kind === 'gone') this.gone.set(true);
    else this.actionFailure.set(failure);
  }

  /** The draft for the page's URL: the user, or an empty one for a new user. */
  private seed(id: string | null): void {
    this.load(id === null ? null : (this.store.users().find((u) => u.id === id) ?? null));
    this.gone.set(false);
    this.closeReset();
    this.actionFailure.set(null);
    this.notice.set('');
  }

  /** Puts `user` (or an empty draft) in the form and clears what the last attempt left. */
  private load(user: AdminUser | null): void {
    this.revision++;
    this.username.set(user?.username ?? '');
    this.displayName.set(user?.displayName ?? '');
    this.password.set('');
    this.confirmation.set('');
    this.requireChange.set(true);
    this.isEnabled.set(user?.isEnabled ?? true);
    this.grants.set(user ? user.grants.map((g) => ({ ...g, sagaTypes: [...g.sagaTypes] })) : []);
    this.submitted.set(false);
    this.serverErrors.set({});
    this.failure.set(null);
    this.usernameTaken.set(false);
    this.saved.set(false);
  }

  private closeReset(): void {
    this.resetOpen.set(false);
    this.newPassword.set('');
    this.newConfirmation.set('');
    this.resetRequireChange.set(true);
    this.resetSubmitted.set(false);
    this.resetServerErrors.set({});
  }

  /** Focuses the first field with an error once the next render shows it; false when there is none. */
  private focusFirstError(): boolean {
    const errors = this.errors();
    const grantsWrong =
      scopedWithoutTypes(this.grants()).length > 0 || Object.keys(this.grantErrors()).length > 0;
    const first = FIELDS.find((field) => (field === 'grants' ? grantsWrong : errors[field]));
    if (first === undefined) return false;
    if (!this.destroyed()) {
      if (first === 'grants') this.editor()?.focusProblem();
      else {
        this.focusAfterRender(
          {
            username: this.usernameField,
            displayName: this.displayNameField,
            password: this.passwordField,
            confirmation: this.confirmationField,
          }[first],
        );
      }
    }
    return true;
  }

  private focusAfterRender(target: () => ElementRef<HTMLElement> | undefined): void {
    if (this.destroyed()) return;
    afterNextRender(() => target()?.nativeElement.focus(), { injector: this.injector });
  }
}
