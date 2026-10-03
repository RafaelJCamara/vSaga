import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { summarizeGrants } from '../../access-explain';
import { Team } from '../../admin.model';
import { AdminStore } from '../../admin.store';

interface TeamRow {
  team: Team;
  /** The grants every member holds, one phrase each: "Operator · 2 types". */
  access: string[];
}

/**
 * The teams: name (a link to the team, with its description), how many users are members, and the grants the
 * team gives each of them, plus "New team". The list is the store's, which the shell has read before this page
 * exists and which every change reads again.
 */
@Component({
  selector: 'app-teams-list',
  imports: [RouterLink],
  templateUrl: './teams-list.html',
  styleUrl: './teams-list.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class TeamsList {
  private readonly store = inject(AdminStore);

  /** Every team, by name ignoring case. */
  protected readonly rows = computed<TeamRow[]>(() => {
    const roles = this.store.roles();
    return [...this.store.teams()]
      .sort((a, b) => a.name.toLowerCase().localeCompare(b.name.toLowerCase()))
      .map((team) => ({ team, access: summarizeGrants(team.grants, roles) }));
  });
}
