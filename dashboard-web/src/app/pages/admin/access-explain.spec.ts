import {
  ADMINISTRATOR_ID,
  BUILT_IN_ROLES,
  OPERATOR_ID,
  PERMISSIONS,
  VIEWER_ID,
  grant,
  role,
} from '../../testing/admin';
import {
  AccessRow,
  ExplainInput,
  explainAccess,
  originsOf,
  sessionAccessOf,
  summarizeGrants,
} from './access-explain';
import { Grant, PermissionInfo } from './admin.model';

// The cases of `AccessEvaluatorTests` (dotnet/tests/VSaga.Dashboard.Identity.Tests/Services), ported: the
// preview must say what the server will do, so each of its cases is here, in the same words, with the same
// roles. The server's answer is a scope per permission; this one is rows of saga types, with origins.

const RETRY_ONLY = role({
  id: 'role-retry-only',
  name: 'Retry only',
  permissions: ['sagas.retry'],
});
const MANAGE_ONLY = role({
  id: 'role-manage-only',
  name: 'Manage only',
  permissions: ['access.manage'],
});
const ROLES = [...BUILT_IN_ROLES, RETRY_ONLY, MANAGE_ONLY];

const USER_ID = 'u-someone';

function explain(overrides: Partial<ExplainInput> = {}): AccessRow[] {
  return explainAccess({
    userId: USER_ID,
    enabled: true,
    grants: [],
    teams: [],
    roles: ROLES,
    permissions: PERMISSIONS,
    ...overrides,
  });
}

/** A row as one line: the saga type (`*` for every type) and its permission keys. */
function plain(rows: AccessRow[]): Record<string, string[]> {
  return Object.fromEntries(
    rows.map((row) => [row.sagaType ?? '*', row.permissions.map((p) => p.key)]),
  );
}

const team = (name: string, memberIds: string[], ...grants: Grant[]) => ({
  name,
  memberIds,
  grants,
});

describe('explainAccess', () => {
  describe("the server evaluator's cases", () => {
    // Evaluate_UnscopedAdministrator_HoldsEverythingEverywhere
    it('gives an administrator for every saga type every permission, in catalogue order, in one row', () => {
      const rows = explain({ grants: [grant(ADMINISTRATOR_ID)] });

      expect(plain(rows)).toEqual({
        '*': ['sagas.view', 'sagas.data', 'sagas.retry', 'access.manage'],
      });
    });

    // Evaluate_ScopedGrant_HoldsOnlyTheNamedTypesComparedOrdinally
    it('gives a scoped grant only the named types, compared ordinally, and nothing for every type', () => {
      const rows = explain({ grants: [grant(VIEWER_ID, ['OrderSaga'])] });

      expect(plain(rows)).toEqual({ OrderSaga: ['sagas.view', 'sagas.data'] });
      // `orderSaga` and `PaymentSaga` hold nothing, and there is no row for every type.
      expect(rows.find((r) => r.sagaType === 'orderSaga')).toBeUndefined();
      expect(rows.find((r) => r.sagaType === null)).toBeUndefined();
    });

    // Evaluate_ImpliesViewForTheSameScope
    it('adds what a permission implies, for the same scope', () => {
      const rows = explain({ grants: [grant(RETRY_ONLY.id, ['OrderSaga'])] });

      expect(plain(rows)).toEqual({ OrderSaga: ['sagas.view', 'sagas.retry'] });
      // `sagas.data` is not implied: it is not held anywhere.
      expect(JSON.stringify(rows)).not.toContain('sagas.data');
    });

    it('adds what a permission implies for every saga type too', () => {
      expect(plain(explain({ grants: [grant(RETRY_ONLY.id)] }))).toEqual({
        '*': ['sagas.view', 'sagas.retry'],
      });
    });

    // Evaluate_IgnoresAccessManageInAScopedGrant
    it('ignores access.manage in a scoped grant, and a scoped role that holds only it confers nothing', () => {
      const administrator = explain({ grants: [grant(ADMINISTRATOR_ID, ['OrderSaga'])] });
      const manageOnly = explain({ grants: [grant(MANAGE_ONLY.id, ['OrderSaga'])] });

      expect(plain(administrator)).toEqual({
        OrderSaga: ['sagas.view', 'sagas.data', 'sagas.retry'],
      });
      expect(manageOnly).toEqual([]);
    });

    it('counts access.manage only in a grant for every saga type, and keeps it out of the rows of saga types', () => {
      const rows = explain({
        grants: [grant(MANAGE_ONLY.id), grant(OPERATOR_ID, ['OrderSaga'])],
      });

      expect(plain(rows)).toEqual({
        '*': ['access.manage'],
        OrderSaga: ['sagas.view', 'sagas.data', 'sagas.retry'],
      });
    });

    // Evaluate_IsTheUnionOfTheUserAndTheirTeams
    it("is the union of the user's grants and those of the teams the user is in, each with its origin", () => {
      const rows = explain({
        grants: [grant(VIEWER_ID, ['OrderSaga'])],
        teams: [
          team(
            'Payments',
            [USER_ID],
            grant(OPERATOR_ID, ['PaymentSaga']),
            grant(VIEWER_ID, ['ShippingSaga']),
          ),
        ],
      });

      expect(plain(rows)).toEqual({
        OrderSaga: ['sagas.view', 'sagas.data'],
        PaymentSaga: ['sagas.view', 'sagas.data', 'sagas.retry'],
        ShippingSaga: ['sagas.view', 'sagas.data'],
      });
      const origins = originsOf(rows);
      expect(origins('OrderSaga', 'sagas.data')).toEqual(['direct: Viewer']);
      expect(origins('PaymentSaga', 'sagas.retry')).toEqual(['team Payments: Operator']);
      expect(origins('ShippingSaga', 'sagas.data')).toEqual(['team Payments: Viewer']);
    });

    // Evaluate_AnUnscopedGrantWinsOverScopedOnesForTheSamePermission
    it('lets a grant for every saga type win over scoped ones for the same permission, and keeps both origins', () => {
      const rows = explain({
        grants: [grant(OPERATOR_ID, ['OrderSaga'])],
        teams: [team('Everyone', [USER_ID], grant(VIEWER_ID))],
      });

      // View and data are held for every saga type; only retry is OrderSaga's own.
      expect(plain(rows)).toEqual({
        '*': ['sagas.view', 'sagas.data'],
        OrderSaga: ['sagas.view', 'sagas.data', 'sagas.retry'],
      });
      expect(sessionAccessOf(rows)).toEqual({
        permissions: ['sagas.view', 'sagas.data'],
        scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.retry'] }],
      });
      const origins = originsOf(rows);
      expect(origins(null, 'sagas.data')).toEqual(['team Everyone: Viewer']);
      // Where it is held anyway, a scoped grant of the same permission is still said.
      expect(origins('OrderSaga', 'sagas.data')).toEqual([
        'team Everyone: Viewer',
        'direct: Operator',
      ]);
      expect(origins('OrderSaga', 'sagas.retry')).toEqual(['direct: Operator']);
    });

    // Evaluate_IgnoresTeamsTheUserIsNotIn
    it('ignores teams the user is not a member of', () => {
      const rows = explain({
        teams: [team('Others', ['someone-else'], grant(ADMINISTRATOR_ID))],
      });

      expect(rows).toEqual([]);
    });

    it('counts no team for a user that does not exist yet', () => {
      const rows = explain({
        userId: null,
        teams: [team('Everyone', [], grant(ADMINISTRATOR_ID))],
        grants: [grant(VIEWER_ID)],
      });

      expect(plain(rows)).toEqual({ '*': ['sagas.view', 'sagas.data'] });
    });

    // Evaluate_ADisabledUser_HoldsNothing
    it('gives a disabled user nothing, whatever the grants and the teams', () => {
      const rows = explain({
        enabled: false,
        grants: [grant(ADMINISTRATOR_ID)],
        teams: [team('Everyone', [USER_ID], grant(VIEWER_ID))],
      });

      expect(rows).toEqual([]);
    });

    // Evaluate_IgnoresUnknownRolesAndUnknownPermissionKeys
    it('ignores a role that is not known and a permission key the catalogue does not list', () => {
      const odd = role({
        id: 'role-odd',
        name: 'Odd',
        permissions: ['sagas.delete', 'sagas.view'],
      });

      const rows = explain({
        roles: [odd],
        grants: [grant('no-such-role'), grant(odd.id, ['OrderSaga'])],
      });

      expect(plain(rows)).toEqual({ OrderSaga: ['sagas.view'] });
    });

    // EvaluateGrants_ANamedGrantWithNoTypes_ConfersNothing
    it('gives a scoped grant that names no saga type nothing', () => {
      expect(explain({ grants: [grant(VIEWER_ID, [])] })).toEqual([]);
    });

    // The server's evaluator unions every grant's scope per permission.
    it('unions several grants of different roles on the same saga type', () => {
      const rows = explain({
        grants: [
          grant(VIEWER_ID, ['OrderSaga']),
          grant(RETRY_ONLY.id, ['OrderSaga', 'PaymentSaga']),
        ],
      });

      expect(plain(rows)).toEqual({
        OrderSaga: ['sagas.view', 'sagas.data', 'sagas.retry'],
        PaymentSaga: ['sagas.view', 'sagas.retry'],
      });
      const origins = originsOf(rows);
      // `sagas.view` comes from both roles, in grant order, each once.
      expect(origins('OrderSaga', 'sagas.view')).toEqual(['direct: Viewer', 'direct: Retry only']);
      expect(origins('OrderSaga', 'sagas.data')).toEqual(['direct: Viewer']);
      expect(origins('OrderSaga', 'sagas.retry')).toEqual(['direct: Retry only']);
      expect(origins('PaymentSaga', 'sagas.view')).toEqual(['direct: Retry only']);
    });
  });

  describe('origins', () => {
    it('names the user\'s own grants "direct: <role>" and a team\'s "team <name>: <role>", own grants first', () => {
      const rows = explain({
        // The team is listed before the user's grant: the order of the origins is direct first all the same.
        teams: [team('Payments', [USER_ID], grant(VIEWER_ID))],
        grants: [grant(OPERATOR_ID)],
      });

      expect(originsOf(rows)(null, 'sagas.view')).toEqual([
        'direct: Operator',
        'team Payments: Viewer',
      ]);
      expect(originsOf(rows)(null, 'sagas.retry')).toEqual(['direct: Operator']);
    });

    it('lists the origins of several teams in the order the teams were given', () => {
      const rows = explain({
        teams: [
          team('Zeta', [USER_ID], grant(VIEWER_ID)),
          team('Alpha', [USER_ID], grant(VIEWER_ID)),
        ],
      });

      expect(originsOf(rows)(null, 'sagas.view')).toEqual([
        'team Zeta: Viewer',
        'team Alpha: Viewer',
      ]);
    });

    it('names what implies a permission too: view held through retry has the origin of the retry grant', () => {
      const rows = explain({ grants: [grant(RETRY_ONLY.id)] });

      expect(originsOf(rows)(null, 'sagas.view')).toEqual(['direct: Retry only']);
    });

    it('answers nothing for a row or a permission that is not there', () => {
      const origins = originsOf(explain({ grants: [grant(VIEWER_ID, ['OrderSaga'])] }));

      expect(origins(null, 'sagas.view')).toEqual([]);
      expect(origins('OrderSaga', 'sagas.retry')).toEqual([]);
      expect(origins('Other', 'sagas.view')).toEqual([]);
    });
  });

  describe('rows', () => {
    it('lists the row of every saga type first, then the saga types in ordinal order', () => {
      const rows = explain({
        grants: [grant(RETRY_ONLY.id, ['beta', 'Zeta', 'Alpha']), grant(VIEWER_ID)],
      });

      // Ordinal: capitals before lower case, as the server compares saga types.
      expect(rows.map((r) => r.sagaType)).toEqual([null, 'Alpha', 'Zeta', 'beta']);
    });

    it('leaves out a saga type whose permissions are all held for every saga type already', () => {
      const rows = explain({ grants: [grant(VIEWER_ID), grant(VIEWER_ID, ['OrderSaga'])] });

      expect(plain(rows)).toEqual({ '*': ['sagas.view', 'sagas.data'] });
    });
  });

  describe('what the catalogue says', () => {
    const catalogue = (change: (p: PermissionInfo) => PermissionInfo): PermissionInfo[] =>
      PERMISSIONS.map(change);

    it('follows `implies`: a catalogue in which retry implies nothing gives no view with it', () => {
      const permissions = catalogue((p) => (p.key === 'sagas.retry' ? { ...p, implies: [] } : p));

      expect(
        plain(explain({ permissions, grants: [grant(RETRY_ONLY.id, ['OrderSaga'])] })),
      ).toEqual({
        OrderSaga: ['sagas.retry'],
      });
    });

    it('follows `implies` over several keys', () => {
      const permissions = catalogue((p) =>
        p.key === 'sagas.retry' ? { ...p, implies: ['sagas.view', 'sagas.data'] } : p,
      );

      expect(plain(explain({ permissions, grants: [grant(RETRY_ONLY.id)] }))).toEqual({
        '*': ['sagas.view', 'sagas.data', 'sagas.retry'],
      });
    });

    it('follows `scopable`: a permission the catalogue does not scope counts only for every saga type', () => {
      const permissions = catalogue((p) =>
        p.key === 'sagas.data' ? { ...p, scopable: false } : p,
      );

      const scoped = explain({ permissions, grants: [grant(VIEWER_ID, ['OrderSaga'])] });
      const everywhere = explain({ permissions, grants: [grant(VIEWER_ID)] });

      // Data confers itself only for every type (its `implies` is not consulted in a scoped grant either).
      expect(plain(scoped)).toEqual({ OrderSaga: ['sagas.view'] });
      expect(plain(everywhere)).toEqual({ '*': ['sagas.view', 'sagas.data'] });
    });

    it('follows `scopable` for access.manage the same way, so a catalogue that scopes it would show it', () => {
      const permissions = catalogue((p) =>
        p.key === 'access.manage' ? { ...p, scopable: true } : p,
      );

      const rows = explain({ permissions, grants: [grant(MANAGE_ONLY.id, ['OrderSaga'])] });

      expect(plain(rows)).toEqual({ OrderSaga: ['access.manage'] });
    });

    it('reports no key the catalogue does not list, an implied one included', () => {
      const permissions = catalogue((p) =>
        p.key === 'sagas.retry' ? { ...p, implies: ['sagas.view', 'sagas.purge'] } : p,
      );

      expect(
        plain(explain({ permissions, grants: [grant(RETRY_ONLY.id, ['OrderSaga'])] })),
      ).toEqual({
        OrderSaga: ['sagas.view', 'sagas.retry'],
      });
    });

    it('lists permissions in the catalogue order, not the role order or the grant order', () => {
      const permissions = [...PERMISSIONS].reverse();

      const rows = explain({ permissions, grants: [grant(VIEWER_ID)] });

      expect(plain(rows)).toEqual({ '*': ['sagas.data', 'sagas.view'] });
    });
  });
});

describe('sessionAccessOf', () => {
  it('shapes rows as the session endpoint does: what is held everywhere, then each type beyond that', () => {
    const rows = explain({
      grants: [grant(VIEWER_ID), grant(OPERATOR_ID, ['OrderSaga']), grant(MANAGE_ONLY.id)],
    });

    expect(sessionAccessOf(rows)).toEqual({
      permissions: ['sagas.view', 'sagas.data', 'access.manage'],
      scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.retry'] }],
    });
  });

  it('has no permission held everywhere when there is no such row', () => {
    expect(sessionAccessOf(explain({ grants: [grant(VIEWER_ID, ['A', 'B'])] }))).toEqual({
      permissions: [],
      scoped: [
        { sagaType: 'A', permissions: ['sagas.view', 'sagas.data'] },
        { sagaType: 'B', permissions: ['sagas.view', 'sagas.data'] },
      ],
    });
  });

  it('is empty for no rows', () => {
    expect(sessionAccessOf([])).toEqual({ permissions: [], scoped: [] });
  });
});

describe('summarizeGrants', () => {
  const roles = [...BUILT_IN_ROLES, RETRY_ONLY];

  it('says each grant as the role and its scope: all types, or how many', () => {
    expect(
      summarizeGrants(
        [grant(VIEWER_ID), grant(OPERATOR_ID, ['A', 'B']), grant(RETRY_ONLY.id, ['A'])],
        roles,
      ),
    ).toEqual(['Viewer · all types', 'Operator · 2 types', 'Retry only · 1 type']);
  });

  it('says nothing for no grants, and leaves out a grant of a role that is not there', () => {
    expect(summarizeGrants([], roles)).toEqual([]);
    expect(summarizeGrants([grant('gone'), grant(VIEWER_ID)], roles)).toEqual([
      'Viewer · all types',
    ]);
  });
});
