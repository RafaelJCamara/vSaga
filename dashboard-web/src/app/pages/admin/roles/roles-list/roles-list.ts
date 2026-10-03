import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminStore } from '../../admin.store';
import { Role } from '../../admin.model';

interface RoleRow {
  role: Role;
  /** The role's permissions as the catalogue names them (the key where the catalogue does not know it). */
  permissions: { key: string; label: string }[];
  /** How many grants of users and teams name the role. */
  usedBy: number;
}

/**
 * The roles: the built-in ones first (in the API's order), then the custom ones by name, each with its
 * permissions and how many grants hold it. A role that is held anywhere cannot be deleted, so the count is
 * what the edit page's Delete waits on too.
 */
@Component({
  selector: 'app-roles-list',
  imports: [RouterLink],
  templateUrl: './roles-list.html',
  styleUrl: './roles-list.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class RolesList {
  private readonly store = inject(AdminStore);

  protected readonly rows = computed<RoleRow[]>(() => {
    const names = new Map(this.store.permissions().map((p) => [p.key, p.name]));
    const usage = this.store.roleUsage();
    const ordered = [...this.store.roles()].sort(
      (a, b) =>
        Number(b.isBuiltIn) - Number(a.isBuiltIn) ||
        (a.isBuiltIn ? 0 : a.name.localeCompare(b.name)),
    );
    return ordered.map((role) => ({
      role,
      permissions: role.permissions.map((key) => ({ key, label: names.get(key) ?? key })),
      usedBy: usage.get(role.id) ?? 0,
    }));
  });
}
