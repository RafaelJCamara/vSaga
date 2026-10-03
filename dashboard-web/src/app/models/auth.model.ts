// The sign-in session, as `GET /api/auth/session` (and every endpoint that signs in or out) answers it.
// The API's records are canonical (`SessionResponse` in `VSaga.Dashboard.Api/Endpoints/AccessDtos.cs`):
// every property below is always present, `null` where a value does not apply, and arrays are never
// null. The administration models come with the administration area.

/**
 * The four permissions a role can combine (the API's catalogue; nothing else is a valid key). The one
 * permission type of the SPA: code that asks "may I?" names a key from here.
 */
export type PermissionKey = 'sagas.view' | 'sagas.data' | 'sagas.retry' | 'access.manage';

/**
 * Where the SPA stands with the session: `unknown` until the first answer; `unreachable` when the first
 * answer never came (a later failure keeps the last known session instead, so an API restart never signs
 * the UI out); `anonymous` and `authenticated` as the server last said.
 */
export type SessionStatus = 'unknown' | 'anonymous' | 'authenticated' | 'unreachable';

/** The signed-in user. Null in a session that is the API key, which has no user. */
export interface SessionUser {
  id: string;
  username: string;
  displayName: string;
  /** While true the user may only change their password: their access is empty. */
  mustChangePassword: boolean;
}

/** What the caller may do on one saga type beyond `SessionAccess.permissions`. */
export interface ScopedPermissions {
  /** The exact saga type name. Compared ordinally (case-sensitively). */
  sagaType: string;
  permissions: string[];
}

/**
 * The caller's effective access. `permissions` are held for every saga type; `scoped` lists, per saga
 * type, the permissions held only for that type. The server has already applied `implies` (a holder of
 * `sagas.data` for a scope holds `sagas.view` for it), and `access.manage` never appears in `scoped`.
 */
export interface SessionAccess {
  permissions: string[];
  scoped: ScopedPermissions[];
}

/**
 * Why first-run setup cannot be completed while it is required (a seed that could not be applied, say).
 * `code` is `setup_unavailable`; `detail` names the setting to fix, never its value.
 */
export interface SetupProblem {
  code: string;
  detail: string;
}

export interface SessionInfo {
  /** True for a signed-in user and for the API key. */
  authenticated: boolean;
  /** True while no user exists. */
  setupRequired: boolean;
  /** True when first-run setup can be completed now, with the one-time code. */
  setupAvailable: boolean;
  /** Set when setup is required but not available; otherwise null. */
  setupProblem: SetupProblem | null;
  user: SessionUser | null;
  /** Null when anonymous. */
  access: SessionAccess | null;
  /** The shortest password the policy accepts, for the forms. */
  passwordMinLength: number;
}

/** `POST /api/auth/setup`: the first administrator, and the one-time code the API logged at start. */
export interface SetupRequest {
  username: string;
  displayName: string;
  password: string;
  code: string;
}
