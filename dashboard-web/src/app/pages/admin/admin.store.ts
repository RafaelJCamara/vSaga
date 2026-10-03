import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, TimeoutError, catchError, firstValueFrom, forkJoin, of, timeout } from 'rxjs';
import { SagaApiService } from '../../services/saga-api.service';
import { AdminFailure, AdminFailureKind, adminFailure } from '../../util/admin-failure';
import { problemOf } from '../../util/http-error';
import { AdminApiService } from './admin-api.service';
import {
  AdminUser,
  CreateUser,
  PermissionInfo,
  ResetPassword,
  Role,
  SaveRole,
  SaveTeam,
  Team,
  UpdateUser,
} from './admin.model';

/** How long a read of the area may take before it counts as failed: a hung API shows the error row with
 *  "Try again" instead of "Loading…" for good. */
export const READ_TIMEOUT_MS = 30000;

const TIMED_OUT = 'The dashboard API did not answer in time. Try again.';

/**
 * What the administration pages share: the users, teams and roles, the permission catalogue and the saga
 * types, loaded once for the shell and kept current by the pages' own changes.
 *
 * Provided by the admin shell's route, not by the root: the data lives while the area is open and is dropped
 * with `clear()` when the shell is left (a route's injector outlives its component, so nothing else would).
 *
 * - `load()` reads everything and never rejects: it sets `loaded`, or `loadError` (and `loadErrorKind`) with
 *   the sentence to show; a read that does not answer in `READ_TIMEOUT_MS` fails like any other. The saga
 *   types are a convenience (the grants editor offers them, and takes type names typed in), so no error on
 *   them fails the load: a manager who cannot view sagas gets a 403 for them, and the saga reader being down
 *   must not lock the manager out of users, teams and roles.
 * - Every change (`saveUser`, `deleteRole`, ...) awaits the API, then reloads the users, teams and roles, so
 *   what the pages show is what the server stores (a deleted role, the teams a user is now in, the
 *   grants it holds in its own order). It resolves with the API's answer after the reload, and rejects with
 *   the API's `HttpErrorResponse` (read it with `adminFailure`). A 404 or a 409 also reads the lists again
 *   (without making the rejection wait for it): what the page worked on is gone, or the rule that refused it
 *   (`role_in_use`, a taken name) is a fact the lists should show; any other refusal changed nothing. A reload
 *   that fails does not fail the change that was made: it sets `loadError` and the lists keep what they had.
 * - `refresh()` reads the lists again and nothing else: `loaded` stays true, so the pages (and what is typed in
 *   them) stay where they are. It is what the shell's "Try again" runs after a reload failed.
 */
@Injectable()
export class AdminStore {
  private readonly api = inject(AdminApiService);
  private readonly sagas = inject(SagaApiService);

  private readonly usersState = signal<AdminUser[]>([]);
  private readonly teamsState = signal<Team[]>([]);
  private readonly rolesState = signal<Role[]>([]);
  private readonly permissionsState = signal<PermissionInfo[]>([]);
  private readonly sagaTypesState = signal<string[]>([]);
  private readonly loadedState = signal(false);
  private readonly loadFailure = signal<AdminFailure | null>(null);
  /** Counts the reads started; an answer to one that is not the latest is dropped, whenever it arrives. */
  private generation = 0;
  /** False until `load()` and again after `clear()`: a change still in flight when the shell is left must
   *  not read the lists back into a store nobody shows. */
  private open = false;

  readonly users = this.usersState.asReadonly();
  readonly teams = this.teamsState.asReadonly();
  readonly roles = this.rolesState.asReadonly();
  /** The catalogue, in the API's order: what the role editor lists and the access preview reads. */
  readonly permissions = this.permissionsState.asReadonly();
  /** The saga types that have instances and that the caller may view, each once, in ordinal order. */
  readonly sagaTypes = this.sagaTypesState.asReadonly();
  /** True once everything has been read; false again while `load()` runs. */
  readonly loaded = this.loadedState.asReadonly();
  /** The sentence for the latest read that failed; null after one that succeeded. */
  readonly loadError = computed(() => this.loadFailure()?.message ?? null);
  /** What kind of failure `loadError` is: `forbidden` means asking again cannot help (the session no longer
   *  holds `access.manage`). Null when there is no error. */
  readonly loadErrorKind = computed<AdminFailureKind | null>(
    () => this.loadFailure()?.kind ?? null,
  );

  /** How many grants (of users and of teams) name each role, by role id: the roles list shows it, and a
   *  role that is granted anywhere cannot be deleted (409 `role_in_use`). A role nobody holds has no entry. */
  readonly roleUsage = computed(() => {
    const usage = new Map<string, number>();
    const grants = [...this.users(), ...this.teams()].flatMap((subject) => subject.grants);
    for (const grant of grants) usage.set(grant.roleId, (usage.get(grant.roleId) ?? 0) + 1);
    return usage;
  });

  /** Reads everything. Never rejects. Hides the pages (`loaded` is false) until it has an answer, so a
   *  shell that is opened again does not show what the area held when it was left. */
  async load(): Promise<void> {
    const generation = ++this.generation;
    this.open = true;
    this.loadedState.set(false);
    this.loadFailure.set(null);
    try {
      const everything = await firstValueFrom(
        forkJoin({
          permissions: this.api.listPermissions(),
          users: this.api.listUsers(),
          teams: this.api.listTeams(),
          roles: this.api.listRoles(),
          sagaTypes: this.sagas.getSagaTypes().pipe(catchError(() => of([]))),
        }).pipe(timeout(READ_TIMEOUT_MS)),
      );
      if (generation !== this.generation) return;
      this.permissionsState.set(everything.permissions);
      this.usersState.set(everything.users);
      this.teamsState.set(everything.teams);
      this.rolesState.set(everything.roles);
      this.sagaTypesState.set(
        [...new Set(everything.sagaTypes.map((s) => s.sagaType))].sort(ordinal),
      );
      this.loadedState.set(true);
    } catch (err) {
      if (generation !== this.generation) return;
      this.loadFailure.set(readFailure(err, 'The administration data could not be loaded.'));
    }
  }

  /** Reads the users, teams and roles again, leaving `loaded` as it is; a failure is `loadError`. Never
   *  rejects. While nothing has been read yet it is a full `load()`. */
  refresh(): Promise<void> {
    return this.reload();
  }

  /** Forgets everything and drops reads still in flight: the shell was left. */
  clear(): void {
    this.generation++;
    this.open = false;
    this.usersState.set([]);
    this.teamsState.set([]);
    this.rolesState.set([]);
    this.permissionsState.set([]);
    this.sagaTypesState.set([]);
    this.loadedState.set(false);
    this.loadFailure.set(null);
  }

  /** Creates the user (`id` null) or updates one. */
  saveUser(id: null, body: CreateUser): Promise<AdminUser>;
  saveUser(id: string, body: UpdateUser): Promise<AdminUser>;
  saveUser(id: string | null, body: CreateUser | UpdateUser): Promise<AdminUser> {
    return this.change(
      id === null
        ? this.api.createUser(body as CreateUser)
        : this.api.updateUser(id, body as UpdateUser),
    );
  }

  deleteUser(id: string): Promise<void> {
    return this.change(this.api.deleteUser(id));
  }

  resetPassword(id: string, body: ResetPassword): Promise<AdminUser> {
    return this.change(this.api.resetPassword(id, body));
  }

  unlockUser(id: string): Promise<AdminUser> {
    return this.change(this.api.unlockUser(id));
  }

  /** Creates the team (`id` null) or replaces one: members and grants are written together with it. */
  saveTeam(id: string | null, body: SaveTeam): Promise<Team> {
    return this.change(id === null ? this.api.createTeam(body) : this.api.updateTeam(id, body));
  }

  deleteTeam(id: string): Promise<void> {
    return this.change(this.api.deleteTeam(id));
  }

  /** Creates the role (`id` null) or replaces a custom one. */
  saveRole(id: string | null, body: SaveRole): Promise<Role> {
    return this.change(id === null ? this.api.createRole(body) : this.api.updateRole(id, body));
  }

  deleteRole(id: string): Promise<void> {
    return this.change(this.api.deleteRole(id));
  }

  /** Awaits one change, then reads the lists again (see the class comment). */
  private async change<T>(call: Observable<T>): Promise<T> {
    let answer: T;
    try {
      answer = await firstValueFrom(call);
    } catch (err) {
      const status = problemOf(err, '').status;
      // Not awaited: the page that made the change shows the refusal at once, and the lists catch up.
      if (status === 404 || status === 409) void this.reload();
      throw err;
    }
    await this.reload();
    return answer;
  }

  /** Reads the users, teams and roles again. Never rejects. */
  private async reload(): Promise<void> {
    if (!this.open) return;
    // A read of the lists alone would supersede the full one that is running (a change that was in flight when
    // the shell was left and entered again), and `loaded` would never become true: read everything instead.
    if (!this.loadedState()) {
      await this.load();
      return;
    }
    const generation = ++this.generation;
    try {
      const lists = await firstValueFrom(
        forkJoin({
          users: this.api.listUsers(),
          teams: this.api.listTeams(),
          roles: this.api.listRoles(),
        }).pipe(timeout(READ_TIMEOUT_MS)),
      );
      if (generation !== this.generation) return;
      this.usersState.set(lists.users);
      this.teamsState.set(lists.teams);
      this.rolesState.set(lists.roles);
      this.loadFailure.set(null);
    } catch (err) {
      if (generation !== this.generation) return;
      this.loadFailure.set(readFailure(err, 'The lists could not be refreshed.'));
    }
  }
}

/**
 * A failed read as the shell shows it. A 404 is not "this no longer exists" here (nothing the shell shows is
 * gone: the endpoint is), and a timeout is not the network being down.
 */
function readFailure(err: unknown, fallback: string): AdminFailure {
  if (err instanceof TimeoutError)
    return { kind: 'failed', code: null, message: TIMED_OUT, fieldErrors: {} };
  const failure = adminFailure(err, fallback);
  return failure.kind === 'gone' ? { ...failure, kind: 'failed', message: fallback } : failure;
}

/** Ordinal order, as the API compares saga types (`localeCompare` would fold case and accents). */
function ordinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
