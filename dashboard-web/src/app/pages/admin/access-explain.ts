import { SessionAccess } from '../../models/auth.model';
import { Grant, PermissionInfo, Role, Team } from './admin.model';

// The effective-access preview and the one-line summaries of a user's or a team's grants. Pure.
//
// `explainAccess` is the browser's copy of the server's rule (`AccessEvaluator` in the Identity project, whose
// cases `access-explain.spec.ts` ports), built from what `GET /api/admin/permissions` says (`scopable`,
// `implies`), so a catalogue that changes changes the preview with it. It adds what the server does not
// return: where each permission comes from.

/** One permission held in a row, and what grants it: `direct: Operator`, `team Payments: Viewer`. */
export interface ExplainedPermission {
  key: string;
  /** Each once: the user's own grants before the teams' (in the order the teams were given). In the row of a saga
   *  type, what holds the permission for every saga type comes before what holds it for that type alone. */
  origins: string[];
}

/**
 * What is held for one scope. `sagaType` null is the row of every saga type; a named row lists everything held
 * for that type in all (what is held for every type included, but a permission that is not scopable: that is
 * no permission on a saga type), and exists only when the type was granted something that is not held for
 * every type anyway. Permissions are in the catalogue's order.
 */
export interface AccessRow {
  sagaType: string | null;
  permissions: ExplainedPermission[];
}

export interface ExplainInput {
  /** The user's id, which says which teams count; null for a user that does not exist yet (no team). */
  userId: string | null;
  /** A disabled user holds nothing. */
  enabled: boolean;
  /** The user's own grants, as drafted. */
  grants: readonly Grant[];
  /** Every team; the ones that list `userId` as a member count. */
  teams: readonly Pick<Team, 'name' | 'memberIds' | 'grants'>[];
  /** The roles the grants may name; a grant naming another confers nothing. */
  roles: readonly Pick<Role, 'id' | 'name' | 'permissions'>[];
  /** The catalogue, in its order. A role's key that it does not list confers nothing. */
  permissions: readonly PermissionInfo[];
}

/**
 * The access `input` confers, as the server computes it, with the origins of each permission. For every grant
 * (the user's, then those of the teams the user is in) and each catalogue permission of its role: a permission
 * that is not scopable counts only in a grant for every saga type; the grant's scope joins the permission's;
 * and every permission it implies joins with the same scope. A permission held for every saga type swallows
 * the named types of the same permission. A scoped grant that names no type confers nothing. A disabled user
 * holds nothing (the server also holds nothing while the user must change the password: the page says so,
 * this function answers for what the user holds afterwards).
 */
export function explainAccess(input: ExplainInput): AccessRow[] {
  if (!input.enabled) return [];

  const catalogue = new Map(input.permissions.map((p) => [p.key, p]));
  const roles = new Map(input.roles.map((r) => [r.id, r]));
  const sources = [
    { label: 'direct', grants: input.grants },
    ...input.teams
      .filter((t) => input.userId !== null && t.memberIds.includes(input.userId))
      .map((t) => ({ label: `team ${t.name}`, grants: t.grants })),
  ];

  const everywhere = new Map<string, Set<string>>();
  const named = new Map<string, Map<string, Set<string>>>();
  for (const { label, grants } of sources) {
    for (const grant of grants) {
      const role = roles.get(grant.roleId);
      if (role === undefined) continue;
      const origin = `${label}: ${role.name}`;
      for (const key of role.permissions) {
        const permission = catalogue.get(key);
        if (permission === undefined) continue;
        if (!permission.scopable && !grant.allSagaTypes) continue;
        for (const held of [permission.key, ...permission.implies]) {
          if (grant.allSagaTypes) getOrCreate(everywhere, held, () => new Set()).add(origin);
          else {
            const types = getOrCreate(named, held, () => new Map<string, Set<string>>());
            for (const sagaType of grant.sagaTypes) {
              getOrCreate(types, sagaType, () => new Set()).add(origin);
            }
          }
        }
      }
    }
  }

  // The rows are built from the catalogue's keys, so a key it does not list (an implied one included) is never reported.
  const keys = input.permissions.map((p) => p.key);
  const rows: AccessRow[] = [];
  const everyType = keys.filter((key) => everywhere.has(key));
  if (everyType.length > 0) {
    rows.push({
      sagaType: null,
      permissions: everyType.map((key) => ({ key, origins: [...everywhere.get(key)!] })),
    });
  }

  // The types granted something on their own, in ordinal order (the server compares saga types so).
  const types = new Set<string>();
  for (const key of keys) {
    if (everywhere.has(key)) continue;
    for (const sagaType of named.get(key)?.keys() ?? []) types.add(sagaType);
  }
  for (const sagaType of [...types].sort(ordinal)) {
    const permissions: ExplainedPermission[] = [];
    for (const key of keys) {
      const onItsOwn = named.get(key)?.get(sagaType);
      const everywhereToo = catalogue.get(key)!.scopable ? everywhere.get(key) : undefined;
      if (onItsOwn === undefined && everywhereToo === undefined) continue;
      permissions.push({
        key,
        origins: [...new Set([...(everywhereToo ?? []), ...(onItsOwn ?? [])])],
      });
    }
    rows.push({ sagaType, permissions });
  }
  return rows;
}

/**
 * Whether `rows` hold, for every saga type, a permission the catalogue does not scope (`access.manage`): what
 * lets a user into the administration. Pure.
 */
export function holdsUnscopedPermission(
  rows: readonly AccessRow[],
  permissions: readonly PermissionInfo[],
): boolean {
  const unscoped = new Set(permissions.filter((p) => !p.scopable).map((p) => p.key));
  return (
    rows.find((row) => row.sagaType === null)?.permissions.some((p) => unscoped.has(p.key)) ?? false
  );
}

/**
 * The rows as the session endpoint shapes access, which `AccessSummary` takes: what is held for every saga type,
 * then per saga type what is held for it beyond that (`SessionAccess` in `AccessDtos.cs`). The component adds
 * the first to each type's row again, so it shows the rows `explainAccess` returned.
 */
export function sessionAccessOf(rows: readonly AccessRow[]): SessionAccess {
  const everyType = rows.find((row) => row.sagaType === null)?.permissions.map((p) => p.key) ?? [];
  return {
    permissions: everyType,
    scoped: rows.flatMap((row) =>
      row.sagaType === null
        ? []
        : [
            {
              sagaType: row.sagaType,
              permissions: row.permissions
                .map((p) => p.key)
                .filter((key) => !everyType.includes(key)),
            },
          ],
    ),
  };
}

/** What `AccessSummary` shows beside each permission of a row: the origins `explainAccess` gave it. */
export function originsOf(
  rows: readonly AccessRow[],
): (sagaType: string | null, permission: string) => readonly string[] {
  return (sagaType, permission) =>
    rows.find((row) => row.sagaType === sagaType)?.permissions.find((p) => p.key === permission)
      ?.origins ?? [];
}

/**
 * One short phrase per grant, for the lists: `Operator · 2 types`, `Viewer · all types`. A grant that names a
 * role not in `roles` is left out (it confers nothing, and the API never stores one).
 */
export function summarizeGrants(
  grants: readonly Grant[],
  roles: readonly Pick<Role, 'id' | 'name'>[],
): string[] {
  const names = new Map(roles.map((r) => [r.id, r.name]));
  return grants.flatMap((grant) => {
    const name = names.get(grant.roleId);
    if (name === undefined) return [];
    const count = grant.sagaTypes.length;
    return [
      `${name} · ${grant.allSagaTypes ? 'all types' : `${count} ${count === 1 ? 'type' : 'types'}`}`,
    ];
  });
}

function getOrCreate<K, V>(map: Map<K, V>, key: K, create: () => V): V {
  let value = map.get(key);
  if (value === undefined) {
    value = create();
    map.set(key, value);
  }
  return value;
}

/** Ordinal order, as the server compares saga types (`localeCompare` would fold case and accents). */
function ordinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
