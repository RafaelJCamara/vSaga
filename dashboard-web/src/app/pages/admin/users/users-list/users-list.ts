import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LocalTime } from '../../../../components/local-time/local-time';
import { AuthService } from '../../../../services/auth.service';
import { summarizeGrants } from '../../access-explain';
import { AdminUser } from '../../admin.model';
import { AdminStore } from '../../admin.store';
import { UserStatus, userStatus } from '../user-status';

interface UserRow {
  user: AdminUser;
  status: UserStatus;
  /** The teams the user is in (read-only here: membership is written on the team). */
  teams: { id: string; name: string }[];
  /** The user's own grants, one phrase each: "Operator · 2 types". */
  access: string[];
  /** The signed-in user's own record. */
  you: boolean;
}

/**
 * The users: username (a link to the user), display name, what is the matter with the account, the teams the
 * user is in, the grants the user holds directly, and the last sign-in, with a filter over the loaded list
 * (username, display name and team names, ignoring case) and "New user". The list is the store's, which the
 * shell has read before this page exists and which every change reads again.
 */
@Component({
  selector: 'app-users-list',
  imports: [RouterLink, LocalTime],
  templateUrl: './users-list.html',
  styleUrl: './users-list.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class UsersList {
  private readonly store = inject(AdminStore);
  private readonly auth = inject(AuthService);

  protected readonly filter = signal('');

  /** Every user, by username ignoring case, with what the table shows of each. */
  private readonly all = computed<UserRow[]>(() => {
    const teams = this.store.teams();
    const roles = this.store.roles();
    const me = this.auth.user()?.id;
    const now = Date.now();
    return [...this.store.users()]
      .sort((a, b) => a.username.toLowerCase().localeCompare(b.username.toLowerCase()))
      .map((user) => ({
        user,
        status: userStatus(user, now),
        teams: teams
          .filter((t) => t.memberIds.includes(user.id))
          .map((t) => ({ id: t.id, name: t.name })),
        access: summarizeGrants(user.grants, roles),
        you: user.id === me,
      }));
  });

  protected readonly rows = computed<UserRow[]>(() => {
    const wanted = this.filter().trim().toLowerCase();
    if (wanted === '') return this.all();
    return this.all().filter(({ user, teams }) =>
      [user.username, user.displayName, ...teams.map((t) => t.name)].some((text) =>
        text.toLowerCase().includes(wanted),
      ),
    );
  });

  /** "5 total", or "2 of 5" while the filter hides some. */
  protected readonly count = computed(() =>
    this.rows().length === this.all().length
      ? `${this.all().length} total`
      : `${this.rows().length} of ${this.all().length}`,
  );

  protected setFilter(event: Event): void {
    this.filter.set((event.target as HTMLInputElement).value);
  }
}
