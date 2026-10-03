import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { PermissionKey, SessionAccess } from '../../models/auth.model';

/** The catalogue's labels (design 8.2), in the order the catalogue lists the permissions: what the table says
 *  when it is not given the catalogue itself (the account page, which the API's catalogue endpoint is not for). */
const PERMISSION_LABELS: Record<PermissionKey, string> = {
  'sagas.view': 'View sagas',
  'sagas.data': 'View saga data',
  'sagas.retry': 'Retry sagas',
  'access.manage': 'Manage access',
};
const CATALOGUE_ORDER = Object.keys(PERMISSION_LABELS);
/** Held for every saga type only: the API never scopes it, and a saga type has no use for it. */
const ACCESS_MANAGE: PermissionKey = 'access.manage';

/** What the table reads of a catalogue entry (`PermissionInfo` of the administration area has more). */
export interface CataloguePermission {
  key: string;
  /** The label shown on the chip. */
  name: string;
  /** False for a permission that counts only for every saga type (`access.manage`): not a permission on a saga type. */
  scopable: boolean;
}

/** The sentences that say whose access a table shows. */
const WORDS = {
  self: {
    none: 'You hold no permissions yet. An administrator can grant you access.',
    caption:
      'Your permissions: for every saga type, and for each saga type you were granted on its own',
    column: 'What you may do',
    unlisted: 'Saga types that are not listed are not available to you.',
  },
  user: {
    none: 'This user would hold no permissions.',
    caption:
      "The user's permissions: for every saga type, and for each saga type granted on its own",
    column: 'What they may do',
    unlisted: 'Saga types that are not listed are not available to this user.',
  },
  team: {
    none: 'This team would grant no permissions.',
    caption:
      'What the team grants its members: for every saga type, and for each saga type granted on its own',
    column: 'What members may do',
    unlisted: 'Saga types that are not listed are not granted by this team.',
  },
};

interface PermissionChip {
  key: string;
  label: string;
}

interface AccessRow {
  /** The saga type the row is about; null for the row of every saga type. */
  sagaType: string | null;
  permissions: PermissionChip[];
}

/** `keys` once each, in the order of `order`, a key it does not list after them, labelled by `labels`. */
function chipsOf(
  keys: Iterable<string>,
  order: readonly string[],
  labels: ReadonlyMap<string, string>,
): PermissionChip[] {
  const held = new Set(keys);
  const known = order.filter((key) => held.has(key));
  const unknown = [...held].filter((key) => !order.includes(key));
  return [...known, ...unknown].map((key) => ({ key, label: labels.get(key) ?? key }));
}

/**
 * What a session may do, as the server computed it (`SessionAccess`): one row for the permissions held
 * for every saga type, then one row per saga type that was granted something on its own, listing what
 * the session holds there in all (the permissions of every saga type included, but `access.manage`: it
 * is not a permission on a saga type). Nothing is inferred here: the server has already applied
 * `implies`, and `access.manage` never appears in `scoped`.
 *
 * The labels, their order and which permission is no permission on a saga type are the built-in four unless it is
 * given the `catalogue` (the administration pages pass the API's, so that a catalogue that changes changes the
 * table with it, as it changes `explainAccess`).
 *
 * It also shows what an administrator would see of another user (`perspective="user"`: the wording is
 * about them, not "you") or of what a team grants (`perspective="team"`), and where each permission comes
 * from (`origins`: "direct: Operator", "team Payments: Viewer"), as the access previews of the user and the
 * team page do from `pages/admin/access-explain.ts`, which shapes its rows into a `SessionAccess` for this
 * component.
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
  /** Whose access it is: the signed-in user's own (the account page), another user's (the access preview) or what a team grants its members (the team page). */
  readonly perspective = input<'self' | 'user' | 'team'>('self');
  /** What grants a permission in a row (`null` for the row of every saga type), shown beside it; none: no origins. */
  readonly origins = input<
    ((sagaType: string | null, permission: string) => readonly string[]) | null
  >(null);

  /** The API's permission catalogue, when the page has it: its labels, order and `scopable` flags win over the built-in four. */
  readonly catalogue = input<readonly CataloguePermission[] | null>(null);

  /** The sentences that say whose access it is. */
  readonly words = computed(() => WORDS[this.perspective()]);

  private readonly order = computed(() => this.catalogue()?.map((p) => p.key) ?? CATALOGUE_ORDER);
  private readonly labels = computed<ReadonlyMap<string, string>>(
    () =>
      new Map(this.catalogue()?.map((p) => [p.key, p.name]) ?? Object.entries(PERMISSION_LABELS)),
  );
  /** The keys that are no permission on a saga type: held for every saga type only. */
  private readonly unscoped = computed<ReadonlySet<string>>(
    () =>
      new Set(
        this.catalogue()
          ?.filter((p) => !p.scopable)
          .map((p) => p.key) ?? [ACCESS_MANAGE],
      ),
  );

  readonly rows = computed<AccessRow[]>(() => {
    const access = this.access();
    if (!access) return [];
    const order = this.order();
    const labels = this.labels();
    const unscoped = this.unscoped();
    const rows: AccessRow[] = [];
    if (access.permissions.length > 0) {
      rows.push({ sagaType: null, permissions: chipsOf(access.permissions, order, labels) });
    }
    for (const scoped of access.scoped) {
      rows.push({
        sagaType: scoped.sagaType,
        permissions: chipsOf(
          [...access.permissions, ...scoped.permissions].filter((key) => !unscoped.has(key)),
          order,
          labels,
        ),
      });
    }
    return rows;
  });

  /** True when some saga types are granted but not all: the ones not listed are hidden. Holding
   *  `access.manage` for every saga type is no access to a saga, so it does not make them all available. */
  readonly onlyListedTypes = computed(() => {
    const access = this.access();
    const unscoped = this.unscoped();
    return (
      access !== null &&
      access.scoped.length > 0 &&
      access.permissions.every((key) => unscoped.has(key))
    );
  });
}
