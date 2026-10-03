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
/** Held for every saga type only: the API never scopes it, and a saga type has no use for it. */
const ACCESS_MANAGE: PermissionKey = 'access.manage';

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
 * the session holds there in all (the permissions of every saga type included, but `access.manage`: it
 * is not a permission on a saga type). Nothing is inferred here: the server has already applied
 * `implies`, and `access.manage` never appears in `scoped`.
 *
 * It also shows what an administrator would see of another user (`perspective="user"`: the wording is
 * about them, not "you"), and where each permission comes from (`origins`: "direct: Operator", "team
 * Payments: Viewer"), as the access preview of the user page does from `pages/admin/access-explain.ts`,
 * which shapes its rows into a `SessionAccess` for this component.
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
  /** Whose access it is: the signed-in user's own (the account page), or another user's (the access preview). */
  readonly perspective = input<'self' | 'user'>('self');
  /** What grants a permission in a row (`null` for the row of every saga type), shown beside it; none: no origins. */
  readonly origins = input<
    ((sagaType: string | null, permission: string) => readonly string[]) | null
  >(null);

  /** The sentences that say whose access it is. */
  readonly words = computed(() =>
    this.perspective() === 'self'
      ? {
          none: 'You hold no permissions yet. An administrator can grant you access.',
          caption:
            'Your permissions: for every saga type, and for each saga type you were granted on its own',
          column: 'What you may do',
          unlisted: 'Saga types that are not listed are not available to you.',
        }
      : {
          none: 'This user would hold no permissions.',
          caption:
            "The user's permissions: for every saga type, and for each saga type granted on its own",
          column: 'What they may do',
          unlisted: 'Saga types that are not listed are not available to this user.',
        },
  );

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
        permissions: chipsOf(
          [...access.permissions, ...scoped.permissions].filter((key) => key !== ACCESS_MANAGE),
        ),
      });
    }
    return rows;
  });

  /** True when some saga types are granted but not all: the ones not listed are hidden. Holding
   *  `access.manage` for every saga type is no access to a saga, so it does not make them all available. */
  readonly onlyListedTypes = computed(() => {
    const access = this.access();
    return (
      access !== null &&
      access.scoped.length > 0 &&
      access.permissions.every((key) => key === ACCESS_MANAGE)
    );
  });
}
