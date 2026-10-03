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
import { ConfirmButton } from '../../../../components/confirm-button/confirm-button';
import { AdminFailure, adminFailure, placeFieldErrors } from '../../../../util/admin-failure';
import { leaveTo, trackDestroyed } from '../../../../util/page-lifecycle';
import { Role, SaveRole } from '../../admin.model';
import { AdminStore } from '../../admin.store';

/** The fields of the form, top to bottom: the order focus goes to the first one with an error. */
const FIELDS = ['name', 'description', 'permissions'] as const;
type Field = (typeof FIELDS)[number];

/** The API's limits (`AccessValidation`), so the form says so before sending. */
const MAX_NAME = 64;
const MAX_DESCRIPTION = 256;

/**
 * One role: a new one (`/admin/roles/new`, optionally `?from=<id>` to start from another role's permissions),
 * or the one named in the URL. A built-in role is shown read-only, with a way to start a custom role from it;
 * a custom role can be saved and, while no grant holds it, deleted.
 *
 * The draft is a set of signals seeded from the store when the page is created (or the URL names another
 * role) and again from the API's answer after a save; the store reloading behind it never touches what is
 * being typed. The form is built like the sign-in pages' (see `Login`): `ngNoForm`, `ngModel` bound to
 * signals, and the checks made here. Failures are shown as `adminFailure` sorts them: a field's messages
 * under the field, a rule (`role_in_use`, `last_administrator`, a duplicate name) in a banner that keeps the
 * draft (a duplicate name also marks the name field), a 404 as "This no longer exists", a 403 as the lost
 * permission.
 *
 * Save and Delete exclude each other, and the draft belongs to the user: what is typed while a request runs
 * is never overwritten by its answer (`revision` counts the changes of the draft, and an answer re-seeds it
 * only when none was made since the request was sent).
 */
@Component({
  selector: 'app-role-edit',
  imports: [FormsModule, RouterLink, ConfirmButton],
  templateUrl: './role-edit.html',
  styleUrl: './role-edit.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class RoleEdit {
  protected readonly store = inject(AdminStore);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly destroyed = trackDestroyed();
  private readonly route = inject(ActivatedRoute);
  private readonly params = toSignal(this.route.paramMap, { requireSync: true });
  private readonly query = toSignal(this.route.queryParamMap, { requireSync: true });

  private readonly nameField = viewChild<ElementRef<HTMLInputElement>>('nameField');
  private readonly descriptionField =
    viewChild<ElementRef<HTMLTextAreaElement>>('descriptionField');
  /** The first permission checkbox (the template marks every one; the first is returned). */
  private readonly firstPermission = viewChild<ElementRef<HTMLInputElement>>('firstPermission');
  /** The link of the "no longer exists" notice: focus goes there when it replaces the form. */
  private readonly goneLink = viewChild<ElementRef<HTMLAnchorElement>>('goneLink');

  /** The role in the URL; null on the page for a new one. */
  private readonly id = computed(() => this.params().get('id'));
  /** The role a new one starts from (`?from=<id>`); null when there is none. */
  private readonly from = computed(() => this.query().get('from'));
  /** The role a delete is running for. The store reads the lists again before the delete returns, and the role
   *  is not in them any more: until the page has left, it must not turn into "This no longer exists". */
  private readonly pending = signal<Role | null>(null);
  /** The role the page edits; null for a new role, and for an id the store does not know (it is gone). */
  protected readonly role = computed<Role | null>(() => {
    const id = this.id();
    return id === null ? null : (this.store.roles().find((r) => r.id === id) ?? this.pending());
  });
  protected readonly creating = computed(() => this.id() === null);
  /** A built-in role cannot be changed or deleted: the form only shows it. */
  protected readonly readOnly = computed(() => this.role()?.isBuiltIn === true);
  /** How many grants name this role: while there are any, it cannot be deleted. */
  protected readonly usedBy = computed(() => {
    const role = this.role();
    return role === null ? 0 : (this.store.roleUsage().get(role.id) ?? 0);
  });

  readonly name = signal('');
  readonly description = signal('');
  private readonly permissions = signal<readonly string[]>([]);
  /** Counts the changes of the draft (typing, ticking, seeding): see the class comment. */
  private revision = 0;
  /** The permissions the role holds that the catalogue does not list (an API older or newer than the role): they
   *  are shown, so that nothing is dropped unseen, and sent as they are; the API says if it refuses them. */
  protected readonly unknownPermissions = computed(() => {
    const known = new Set(this.store.permissions().map((p) => p.key));
    return this.permissions().filter((key) => !known.has(key));
  });

  readonly busy = signal(false);
  readonly deleting = signal(false);
  /** Set by the first save: the form shows what is missing only after that, not while it is filled in. */
  readonly submitted = signal(false);
  /** The API accepted the latest save of an existing role. */
  readonly saved = signal(false);
  /** What the API said about the fields of the request it refused, by field. */
  private readonly serverErrors = signal<Record<string, string[]>>({});
  /** A failure that no field explains: a rule, the network, a 403 and the like. */
  readonly failure = signal<AdminFailure | null>(null);
  /** The API refused the name as taken (`name_taken`): the name field is the one to change. */
  protected readonly nameTaken = signal(false);
  /** The API answered 404: the role was deleted meanwhile. */
  private readonly gone = signal(false);
  protected readonly notFound = computed(
    () => this.gone() || (!this.creating() && this.role() === null),
  );

  protected readonly heading = computed(() => this.role()?.name ?? 'New role');
  protected readonly nameLimit = MAX_NAME;
  protected readonly descriptionLimit = MAX_DESCRIPTION;

  /** What is wrong with each field, once the form has been submitted; the API's message where it gave one. */
  protected readonly errors = computed<Partial<Record<Field, string>>>(() => {
    const server = this.serverErrors();
    const errors: Partial<Record<Field, string>> = {};
    if (this.submitted()) {
      const name = this.name().trim();
      if (name === '') errors.name = 'Enter a name.';
      else if (name.length > MAX_NAME) errors.name = `Use at most ${MAX_NAME} characters.`;
      if (this.description().trim().length > MAX_DESCRIPTION) {
        errors.description = `Use at most ${MAX_DESCRIPTION} characters.`;
      }
      if (this.permissions().length === 0) errors.permissions = 'Choose at least one permission.';
    }
    // The API's message about a value beats the form's own about the same field.
    for (const field of FIELDS) {
      const messages = server[field];
      if (messages?.length) errors[field] = messages.join(' ');
    }
    return errors;
  });

  constructor() {
    // The router reuses this component when only the id changes, so the draft follows the URL.
    effect(() => {
      const id = this.id();
      const from = this.from();
      untracked(() => this.seed(id, from));
    });
    // The notice replaces the form, and the focus was in it: it goes to the way out, not to the page's top.
    effect(() => {
      if (this.notFound()) {
        afterNextRender(() => this.goneLink()?.nativeElement.focus(), { injector: this.injector });
      }
    });
  }

  protected has(key: string): boolean {
    return this.permissions().includes(key);
  }

  protected toggle(key: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.permissions.update((held) =>
      checked ? (held.includes(key) ? held : [...held, key]) : held.filter((k) => k !== key),
    );
    this.edited('permissions');
  }

  /**
   * `field` was changed. The API's message about it was about the value that was sent, so it ends, and the
   * draft is no longer the one that was saved.
   */
  protected edited(field: Field): void {
    this.revision++;
    this.saved.set(false);
    if (field === 'name') this.nameTaken.set(false);
    if (!(field in this.serverErrors())) return;
    this.serverErrors.update((errors) =>
      Object.fromEntries(Object.entries(errors).filter(([key]) => key !== field)),
    );
  }

  protected async save(): Promise<void> {
    if (this.busy() || this.deleting() || this.readOnly()) return;
    this.submitted.set(true);
    this.serverErrors.set({});
    this.failure.set(null);
    this.nameTaken.set(false);
    this.saved.set(false);
    if (this.focusFirstError()) return;

    // The catalogue's permissions in its order, whatever order the boxes were ticked in, then any it does not list.
    const held = this.permissions();
    const catalogue = this.store.permissions().map((p) => p.key);
    const body: SaveRole = {
      name: this.name().trim(),
      description: this.description().trim(),
      permissions: [
        ...catalogue.filter((key) => held.includes(key)),
        ...held.filter((key) => !catalogue.includes(key)),
      ],
    };
    const creating = this.creating();
    const revision = this.revision;
    this.busy.set(true);
    let role: Role;
    try {
      role = await this.store.saveRole(this.id(), body);
    } catch (err) {
      this.refused(adminFailure(err, 'The role could not be saved. Try again.'));
      this.busy.set(false);
      return;
    }
    if (this.destroyed()) return;
    if (creating) {
      // Busy until the page has left: the form must not be usable again in between.
      await leaveTo(this.router, '/admin/roles', () => !this.destroyed());
      if (!this.destroyed()) this.busy.set(false);
      return;
    }
    this.busy.set(false);
    // What was typed while the request ran is the user's: the answer re-seeds the draft only if it is still the one that was sent.
    if (revision === this.revision) {
      this.load(role);
      this.saved.set(true);
    }
  }

  protected async remove(): Promise<void> {
    const role = this.role();
    if (role === null || this.deleting() || this.busy()) return;
    this.pending.set(role);
    this.deleting.set(true);
    this.failure.set(null);
    this.nameTaken.set(false);
    this.saved.set(false);
    try {
      await this.store.deleteRole(role.id);
    } catch (err) {
      this.pending.set(null);
      this.deleting.set(false);
      this.refused(adminFailure(err, 'The role could not be deleted. Try again.'));
      return;
    }
    await leaveTo(this.router, '/admin/roles', () => !this.destroyed());
    if (!this.destroyed()) {
      this.pending.set(null);
      this.deleting.set(false);
    }
  }

  /** Shows a refusal where it belongs. The draft is never touched: the user fixes it or tries again. */
  private refused(failure: AdminFailure): void {
    if (failure.kind === 'gone') {
      this.gone.set(true);
      return;
    }
    if (failure.kind !== 'validation') {
      this.failure.set(failure);
      if (failure.kind === 'conflict' && failure.code === 'name_taken') {
        this.nameTaken.set(true);
        if (!this.destroyed()) {
          afterNextRender(() => this.nameField()?.nativeElement.focus(), {
            injector: this.injector,
          });
        }
      }
      return;
    }
    const { placed, unplaced } = placeFieldErrors(failure.fieldErrors, FIELDS);
    this.serverErrors.set(placed);
    // Messages for request paths this form has no field for are not lost: they join the banner.
    if (unplaced.length > 0)
      this.failure.set({ ...failure, kind: 'failed', message: unplaced.join(' ') });
    this.focusFirstError();
  }

  /** The draft for the page's URL: the role itself, or for a new role the one it starts from, or nothing. */
  private seed(id: string | null, from: string | null): void {
    const roles = this.store.roles();
    const find = (wanted: string | null) => roles.find((r) => r.id === wanted) ?? null;
    const source = id !== null ? find(id) : find(from);
    if (id === null && source !== null) {
      // A copy needs a name of its own: the API refuses a duplicate, ignoring case.
      this.load({ ...source, name: `${source.name} copy`.slice(0, MAX_NAME) });
    } else {
      this.load(source);
    }
    this.gone.set(false);
  }

  /** Puts `role` (or an empty draft) in the form and clears what the last attempt left. */
  private load(role: Pick<Role, 'name' | 'description' | 'permissions'> | null): void {
    this.revision++;
    this.name.set(role?.name ?? '');
    this.description.set(role?.description ?? '');
    this.permissions.set(role ? [...role.permissions] : []);
    this.submitted.set(false);
    this.serverErrors.set({});
    this.failure.set(null);
    this.nameTaken.set(false);
    this.saved.set(false);
  }

  /** Focuses the first field with an error once the next render shows it; false when there is none. */
  private focusFirstError(): boolean {
    const errors = this.errors();
    const first = FIELDS.find((field) => errors[field]);
    if (first === undefined) return false;
    if (!this.destroyed()) {
      const target = {
        name: this.nameField,
        description: this.descriptionField,
        permissions: this.firstPermission,
      }[first];
      afterNextRender(() => target()?.nativeElement.focus(), { injector: this.injector });
    }
    return true;
  }
}
