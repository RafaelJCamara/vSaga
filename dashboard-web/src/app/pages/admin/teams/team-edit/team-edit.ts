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
import {
  AdminFailure,
  adminFailure,
  belongsToField,
  placeFieldErrors,
} from '../../../../util/admin-failure';
import { leaveTo, trackDestroyed } from '../../../../util/page-lifecycle';
import { explainAccess, originsOf, sessionAccessOf } from '../../access-explain';
import { Grant, SaveTeam, Team } from '../../admin.model';
import { AdminStore } from '../../admin.store';
import { GrantsEditor, grantsBody, scopedWithoutTypes } from '../../grants-editor/grants-editor';

/** The fields of the form, top to bottom (named as the API names them in its errors): the order focus goes to the first one with an error. */
const FIELDS = ['name', 'description', 'memberIds', 'grants'] as const;
type Field = (typeof FIELDS)[number];

/** The API's limits (`AccessValidation`), so the form says so before sending. */
const MAX_NAME = 64;
const MAX_DESCRIPTION = 256;

/** The one member the preview asks about: the team is the only source of access it counts, and names it. */
const PREVIEW_MEMBER = 'member';

/**
 * One team: a new one (`/admin/teams/new`) or the one the URL names. A team has a name, a description, the users
 * who are members (a checkbox each, with a filter for a long list) and the grants every member holds (the same
 * `GrantsEditor` as a user's). Below the grants, what the team gives its members, computed from the draft by
 * `explainAccess`. An existing team can be deleted, after a question.
 *
 * Membership is written only here: `PUT /api/admin/teams/{id}` replaces the whole team, so Save always sends the
 * full member list and the full grants, and a user's own payload has no teams (`teamIds` on a user are read-only).
 * Users are listed by username; the ids the body carries follow that order, whatever order the boxes were ticked
 * in, and a member the lists no longer know (the user was deleted meanwhile) is shown, ticked and labelled, and
 * sent with the rest until it is unticked: nothing is dropped unseen, and the API says if it refuses it.
 * Saving replaces the team's members and access with what is shown here, and the page says so beside Save.
 *
 * Built like the role and user pages (see `RoleEdit`, `UserEdit`): `ngNoForm`, `ngModel` bound to signals, the
 * checks made here, and a failure shown where it belongs (`adminFailure`): a field's messages under the field
 * (`memberIds[1]` with the members, `grants[0].sagaTypes` with that grant), a rule (`last_administrator`, a taken
 * name) in a banner that keeps the draft (a taken name also marks the name field), a 404 as "This no longer
 * exists", a 403 as the lost permission. Save and Delete exclude each other, and the draft belongs to the user:
 * what is typed while a request runs is never overwritten by its answer (`revision` counts the changes of the
 * draft, and an answer re-seeds it only when none was made since the request was sent).
 */
@Component({
  selector: 'app-team-edit',
  imports: [FormsModule, RouterLink, AccessSummary, ConfirmButton, GrantsEditor],
  templateUrl: './team-edit.html',
  styleUrl: './team-edit.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class TeamEdit {
  protected readonly store = inject(AdminStore);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly destroyed = trackDestroyed();
  private readonly route = inject(ActivatedRoute);
  private readonly params = toSignal(this.route.paramMap, { requireSync: true });

  private readonly nameField = viewChild<ElementRef<HTMLInputElement>>('nameField');
  private readonly descriptionField =
    viewChild<ElementRef<HTMLTextAreaElement>>('descriptionField');
  private readonly filterField = viewChild<ElementRef<HTMLInputElement>>('filterField');
  private readonly editor = viewChild(GrantsEditor);
  /** The failure banner: it can sit far above the button that was used, so focus goes there when it appears. */
  private readonly failureBanner = viewChild<ElementRef<HTMLElement>>('failureBanner');
  /** The link of the "no longer exists" notice: focus goes there when it replaces the form. */
  private readonly goneLink = viewChild<ElementRef<HTMLAnchorElement>>('goneLink');

  /** The team in the URL; null on the page for a new one. */
  private readonly id = computed(() => this.params().get('id'));
  /** The team a delete is running for. The store reads the lists again before the delete returns, and the team
   *  is not in them any more: until the page has left, it must not turn into "This no longer exists". */
  private readonly pending = signal<Team | null>(null);
  /** The team the page edits; null for a new team, and for an id the store does not know (it is gone). */
  protected readonly team = computed<Team | null>(() => {
    const id = this.id();
    return id === null ? null : (this.store.teams().find((t) => t.id === id) ?? this.pending());
  });
  protected readonly creating = computed(() => this.id() === null);

  readonly name = signal('');
  readonly description = signal('');
  /** The ids of the users who are members, as drafted. */
  private readonly members = signal<readonly string[]>([]);
  readonly grants = signal<Grant[]>([]);
  /** Counts the changes of the draft (typing, ticking, grants, seeding): see the class comment. */
  private revision = 0;

  // The members' checkboxes.
  /** What the filter above the checkboxes says; it narrows the list, never the draft. */
  protected readonly filter = signal('');
  /** Every user, by username ignoring case. */
  private readonly everyone = computed(() =>
    [...this.store.users()].sort((a, b) =>
      a.username.toLowerCase().localeCompare(b.username.toLowerCase()),
    ),
  );
  protected readonly visible = computed(() => {
    const wanted = this.filter().trim().toLowerCase();
    if (wanted === '') return this.everyone();
    return this.everyone().filter((user) =>
      [user.username, user.displayName].some((text) => text.toLowerCase().includes(wanted)),
    );
  });
  protected readonly memberSet = computed(() => new Set(this.members()));
  /** The drafted members the user list does not know: they are shown, and sent, until unticked. */
  protected readonly unknownMembers = computed(() => {
    const known = new Set(this.store.users().map((u) => u.id));
    return this.members().filter((id) => !known.has(id));
  });
  /** "5 users", or "2 of 5 users" while the filter hides some: a live region says it. */
  protected readonly shown = computed(() => {
    const all = this.everyone().length;
    const some = this.visible().length;
    const users = `${all} ${all === 1 ? 'user' : 'users'}`;
    return some === all ? users : `${some} of ${users}`;
  });
  protected readonly selected = computed(() => {
    const count = this.members().length;
    return count === 0
      ? 'No members selected.'
      : `${count} ${count === 1 ? 'member' : 'members'} selected.`;
  });

  readonly busy = signal(false);
  readonly deleting = signal(false);
  /** One request at a time: Save and Delete each refuse while the other runs. */
  protected readonly working = computed(() => this.busy() || this.deleting());
  /** Set by the first save: the form shows what is missing only after that, not while it is filled in. */
  readonly submitted = signal(false);
  /** The API accepted the latest save of an existing team. */
  readonly saved = signal(false);
  /** What the API said about the request it refused, by request path (`memberIds[1]`, `grants[0].sagaTypes`). */
  private readonly serverErrors = signal<Record<string, string[]>>({});
  /** A failure that no field explains: a rule, the network, a 403 and the like. */
  readonly failure = signal<AdminFailure | null>(null);
  /** The API refused the name as taken (`name_taken`): the name field is the one to change. */
  protected readonly nameTaken = signal(false);
  /** The API answered 404: the team was deleted meanwhile. */
  private readonly gone = signal(false);
  protected readonly notFound = computed(
    () => this.gone() || (!this.creating() && this.team() === null),
  );

  protected readonly heading = computed(() => this.team()?.name ?? 'New team');
  protected readonly nameLimit = MAX_NAME;
  protected readonly descriptionLimit = MAX_DESCRIPTION;

  /** The grants the editor offers: every role, in the API's order. */
  protected readonly roles = this.store.roles;
  protected readonly sagaTypes = this.store.sagaTypes;
  /** The API's messages about the grants, for the editor to put with their grants. */
  protected readonly grantErrors = computed(() =>
    Object.fromEntries(
      Object.entries(this.serverErrors()).filter(([path]) => belongsToField(path, 'grants')),
    ),
  );

  /** What each member would hold through the team: the draft's grants, as a team that has the one member. */
  private readonly explanation = computed(() =>
    explainAccess({
      userId: PREVIEW_MEMBER,
      enabled: true,
      grants: [],
      teams: [
        {
          name: this.name().trim() || '(unnamed)',
          memberIds: [PREVIEW_MEMBER],
          grants: this.grants(),
        },
      ],
      roles: this.store.roles(),
      permissions: this.store.permissions(),
    }),
  );
  protected readonly access = computed(() => sessionAccessOf(this.explanation()));
  protected readonly origins = computed(() => originsOf(this.explanation()));

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
    }
    // The API's message about a value beats the form's own about the same field.
    for (const field of ['name', 'description', 'memberIds'] as const) {
      const messages = Object.entries(server)
        .filter(([path]) => belongsToField(path, field))
        .flatMap(([, texts]) => texts);
      // Each message once: `memberIds[0]` and `memberIds[2]` may say the same.
      if (messages.length > 0) errors[field] = [...new Set(messages)].join(' ');
    }
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

  protected setFilter(event: Event): void {
    this.filter.set((event.target as HTMLInputElement).value);
  }

  protected toggle(id: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.members.update((held) =>
      checked ? (held.includes(id) ? held : [...held, id]) : held.filter((m) => m !== id),
    );
    this.edited('memberIds');
  }

  protected setGrants(grants: Grant[]): void {
    this.grants.set(grants);
    this.edited('grants');
  }

  /**
   * `field` was changed. The API's message about it was about the value that was sent, so it ends, and the
   * draft is no longer the one that was saved.
   */
  protected edited(field: Field): void {
    this.revision++;
    this.saved.set(false);
    if (field === 'name') this.nameTaken.set(false);
    this.serverErrors.update((errors) =>
      Object.fromEntries(Object.entries(errors).filter(([path]) => !belongsToField(path, field))),
    );
  }

  protected async save(): Promise<void> {
    if (this.working()) return;
    this.submitted.set(true);
    this.serverErrors.set({});
    this.failure.set(null);
    this.nameTaken.set(false);
    this.saved.set(false);
    if (this.focusFirstError()) return;

    // The users in the order the list shows them, then the members it does not know, in the draft's order.
    const held = this.memberSet();
    const body: SaveTeam = {
      name: this.name().trim(),
      description: this.description().trim(),
      memberIds: [
        ...this.everyone()
          .filter((user) => held.has(user.id))
          .map((user) => user.id),
        ...this.unknownMembers(),
      ],
      grants: grantsBody(this.grants()),
    };
    const id = this.id();
    const revision = this.revision;
    this.busy.set(true);
    let team: Team;
    try {
      team = await this.store.saveTeam(id, body);
    } catch (err) {
      this.refused(adminFailure(err, 'The team could not be saved. Try again.'));
      this.busy.set(false);
      return;
    }
    if (this.destroyed()) return;
    if (id === null) {
      // Busy until the page has left: the form must not be usable again in between.
      await leaveTo(this.router, '/admin/teams', () => !this.destroyed());
      if (!this.destroyed()) this.busy.set(false);
      return;
    }
    this.busy.set(false);
    // What was typed while the request ran is the user's: the answer re-seeds the draft only if it is still the one that was sent.
    if (revision === this.revision) {
      this.load(team);
      this.saved.set(true);
    }
  }

  protected async remove(): Promise<void> {
    const team = this.team();
    if (team === null || this.working()) return;
    this.pending.set(team);
    this.deleting.set(true);
    this.failure.set(null);
    this.nameTaken.set(false);
    this.saved.set(false);
    try {
      await this.store.deleteTeam(team.id);
    } catch (err) {
      this.pending.set(null);
      this.deleting.set(false);
      this.refused(adminFailure(err, 'The team could not be deleted. Try again.'));
      return;
    }
    await leaveTo(this.router, '/admin/teams', () => !this.destroyed());
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
        this.focusAfterRender(this.nameField);
      } else {
        // The banner is at the top of a page that may be long: it takes the focus, as the name does for a taken name.
        this.focusAfterRender(this.failureBanner);
      }
      return;
    }
    this.serverErrors.set(failure.fieldErrors);
    const { unplaced } = placeFieldErrors(failure.fieldErrors, FIELDS);
    // Messages for request paths this form has no field for are not lost: they join the banner.
    if (unplaced.length > 0)
      this.failure.set({ ...failure, kind: 'failed', message: unplaced.join(' ') });
    // A member the API does not know may be a user deleted since the lists were read: read them again, so that the
    // page can label the member it is about (the unknown-member row) instead of leaving a bare id unexplained.
    if (Object.keys(failure.fieldErrors).some((path) => belongsToField(path, 'memberIds'))) {
      void this.store.refresh();
    }
    // With nothing to focus but the banner (only paths the form has no field for), the banner takes it.
    if (!this.focusFirstError() && unplaced.length > 0) this.focusAfterRender(this.failureBanner);
  }

  /** The draft for the page's URL: the team, or an empty one for a new team. */
  private seed(id: string | null): void {
    this.load(id === null ? null : (this.store.teams().find((t) => t.id === id) ?? null));
    this.gone.set(false);
    this.filter.set('');
  }

  /** Puts `team` (or an empty draft) in the form and clears what the last attempt left. */
  private load(team: Team | null): void {
    this.revision++;
    this.name.set(team?.name ?? '');
    this.description.set(team?.description ?? '');
    this.members.set(team ? [...team.memberIds] : []);
    this.grants.set(team ? team.grants.map((g) => ({ ...g, sagaTypes: [...g.sagaTypes] })) : []);
    this.submitted.set(false);
    this.serverErrors.set({});
    this.failure.set(null);
    this.nameTaken.set(false);
    this.saved.set(false);
  }

  /** Focuses the first field with an error once the next render shows it; false when there is none. */
  private focusFirstError(): boolean {
    const errors = this.errors();
    const grantsWrong =
      scopedWithoutTypes(this.grants()).length > 0 || Object.keys(this.grantErrors()).length > 0;
    const first = FIELDS.find((field) => (field === 'grants' ? grantsWrong : errors[field]));
    if (first === undefined) return false;
    if (!this.destroyed()) {
      // After the render: the API's messages reach the editor with it, and focusProblem reads what it shows.
      if (first === 'grants')
        afterNextRender(() => this.editor()?.focusProblem(), { injector: this.injector });
      else {
        // The members' message goes to the filter: the first checkbox may be one the filter hides.
        this.focusAfterRender(
          { name: this.nameField, description: this.descriptionField, memberIds: this.filterField }[
            first
          ],
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
