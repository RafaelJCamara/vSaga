import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { PermissionKey, SessionAccess } from '../../models/auth.model';

/** The catalogue's labels (design 8.2), in the order the catalogue lists the permissions. */
const PERMISSION_LABELS: Record<PermissionKey, string> = {
  'sagas.view': 'View sagas',
  'sagas.data': 'View saga data',
  'sagas.retry': 'Retry sagas',
  'access.manage': 'Manage access',
};
const CATALOGUE_ORDER = Object.keys(PERMISSION_LABELS);

interface PermissionChip {
  key: string;
  label: string;
}

interface AccessRow {
  /** The saga type the row is about; null for the row of every saga type. */
  sagaType: string | null;
  permissions: PermissionChip[];
}

/** `keys` once each, in the catalogue's order, a key the catalogue does not know after them. */
function chipsOf(keys: Iterable<string>): PermissionChip[] {
  const held = new Set(keys);
  const known = CATALOGUE_ORDER.filter((key) => held.has(key));
  const unknown = [...held].filter((key) => !CATALOGUE_ORDER.includes(key));
  return [...known, ...unknown].map((key) => ({
    key,
    label: (PERMISSION_LABELS as Record<string, string | undefined>)[key] ?? key,
  }));
}

/**
 * What a session may do, as the server computed it (`SessionAccess`): one row for the permissions held
 * for every saga type, then one row per saga type that was granted something on its own, listing what
 * the session holds there in all (the permissions of every saga type included). Nothing is inferred
 * here: the server has already applied `implies`, and `access.manage` never appears in a scoped row.
 */
@Component({
  selector: 'app-access-summary',
  changeDetection: ChangeDetectionStrategy.Eager,
  templateUrl: './access-summary.html',
  styleUrl: './access-summary.scss',
})
export class AccessSummary {
  /** The session's access; null while anonymous. */
  readonly access = input.required<SessionAccess | null>();

  readonly rows = computed<AccessRow[]>(() => {
    const access = this.access();
    if (!access) return [];
    const rows: AccessRow[] = [];
    if (access.permissions.length > 0) {
      rows.push({ sagaType: null, permissions: chipsOf(access.permissions) });
    }
    for (const scoped of access.scoped) {
      rows.push({
        sagaType: scoped.sagaType,
        permissions: chipsOf([...access.permissions, ...scoped.permissions]),
      });
    }
    return rows;
  });

  /** True when some saga types are granted but not all: the ones not listed are hidden. */
  readonly onlyListedTypes = computed(() => {
    const rows = this.rows();
    return rows.length > 0 && rows[0].sagaType !== null;
  });
}
