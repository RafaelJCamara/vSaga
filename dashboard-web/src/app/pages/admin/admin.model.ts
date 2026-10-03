// The administration contract, as `/api/admin` speaks it. The API's records are canonical
// (`AccessDtos.cs` in `VSaga.Dashboard.Api/Endpoints`): the names below are theirs (`isEnabled`,
// `isBuiltIn`, `lastSignInAtUtc`), every array in a response is present and never null, and the golden
// fixtures under `src/app/testing/contracts/admin/` pin the JSON from both suites (the .NET endpoint tests
// and `admin-api.service.spec.ts`). Every request record refuses unknown members with a 400, so a request
// type here lists exactly the members the API reads: in particular a user payload has no `teamIds`, because
// team membership is written only through the team payload (`SaveTeam.memberIds`).

/** One entry of `GET /api/admin/permissions`: the catalogue the role editor and the access preview read. */
export interface PermissionInfo {
  /** The stable key, such as `sagas.view`. */
  key: string;
  /** The label the dashboard shows. */
  name: string;
  description: string;
  /** False for `access.manage`: it counts only in a grant for every saga type. */
  scopable: boolean;
  /** Keys held for the same scope whenever this one is. */
  implies: string[];
}

/** One role held for every saga type or for named ones, in a user's or a team's `grants`. */
export interface Grant {
  roleId: string;
  allSagaTypes: boolean;
  /** The exact saga type names; empty for a grant for every saga type. */
  sagaTypes: string[];
}

/** The built-in roles (Administrator, Operator, Viewer) cannot be changed or deleted. */
export interface Role {
  id: string;
  /** Unique ignoring case. */
  name: string;
  description: string | null;
  isBuiltIn: boolean;
  /** Permission keys, in catalogue order. */
  permissions: string[];
}

export interface AdminUser {
  id: string;
  /** Immutable once created. */
  username: string;
  displayName: string;
  /** False for a disabled account, which cannot sign in and holds no access. */
  isEnabled: boolean;
  /** True while the user must choose a new password before holding any access. */
  mustChangePassword: boolean;
  /** When a lockout in force ends; null when the account is not locked now. */
  lockedUntilUtc: string | null;
  /** The last successful sign-in; null if never. */
  lastSignInAtUtc: string | null;
  createdAtUtc: string;
  /** The user's own grants (the teams' come on top). */
  grants: Grant[];
  /** The teams the user is in. Read-only here: membership is written through the team. */
  teamIds: string[];
}

export interface Team {
  id: string;
  /** Unique ignoring case. */
  name: string;
  description: string | null;
  memberIds: string[];
  /** Grants every member holds. */
  grants: Grant[];
}

/** `POST /api/admin/users`. */
export interface CreateUser {
  /** 3 to 64 of letters, digits and `. _ @ + -`, starting with a letter or a digit. */
  username: string;
  displayName: string;
  password: string;
  /** True: the user must change the password at first sign-in (the API's default when left out). */
  mustChangePassword: boolean;
  grants: Grant[];
}

/** `PUT /api/admin/users/{id}`. */
export interface UpdateUser {
  displayName: string;
  isEnabled: boolean;
  /** Replaces every grant the user holds directly. */
  grants: Grant[];
}

/** `POST /api/admin/users/{id}/password`. */
export interface ResetPassword {
  newPassword: string;
  /** True: the user must change it at next sign-in (the API's default when left out). */
  mustChangePassword: boolean;
}

/** `POST /api/admin/teams` and `PUT /api/admin/teams/{id}`: the whole team, members included. */
export interface SaveTeam {
  name: string;
  /** At most 256 characters; blank for none. */
  description: string;
  /** The users in the team: the only way membership is written. */
  memberIds: string[];
  grants: Grant[];
}

/** `POST /api/admin/roles` and `PUT /api/admin/roles/{id}`: a custom role as a whole. */
export interface SaveRole {
  name: string;
  /** At most 256 characters; blank for none. */
  description: string;
  /** A non-empty subset of the catalogue. */
  permissions: string[];
}
