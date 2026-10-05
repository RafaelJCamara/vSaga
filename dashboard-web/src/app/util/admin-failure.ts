import { failureText } from './failure-text';
import { problemOf } from './http-error';

/** What a page says when the API answers 403 to an administration call: the session lost `access.manage`. */
export const ACCESS_LOST = 'You no longer have permission to manage access.';

/** What a page says when the API answers 401: the session ended (the interceptor is already taking the user to sign in). */
export const SESSION_ENDED = 'Your session has ended. Sign in again.';

/** What a page says when the API answers 403 `password_change_required`: not a missing permission, a password to change first. */
export const MUST_CHANGE_PASSWORD = 'You must change your password before you can manage access.';

/** What a page says when the user, team or role it works on is gone (404): someone deleted it meanwhile. */
export const GONE = 'This no longer exists. It may have been deleted by someone else.';

/** What follows the server's own words when a change would leave no administrator (409 `last_administrator`). */
export const LAST_ADMINISTRATOR_ADVICE =
  'Give another enabled user an all-saga-types grant whose role includes access.manage, then try again.';

/**
 * How a refused administration call is shown, by what the page does about it:
 * - `validation`: a 400 with `errors`, keyed by request path; `placeFieldErrors` puts them under their fields.
 * - `last_administrator`: a 409 that would leave nobody able to manage access; the draft is kept.
 * - `conflict`: any other 409 (`role_in_use`, `role_immutable`, a name or username already taken); the
 *   draft is kept and `message` is the server's own `detail`.
 * - `gone`: a 404; the page shows `GONE` and offers the way back to the list.
 * - `forbidden`: a 403; the session no longer holds `access.manage`.
 * - `failed`: anything else (the network, a rate limit, a 5xx, a 400 with no field named).
 */
export type AdminFailureKind =
  'validation' | 'last_administrator' | 'conflict' | 'gone' | 'forbidden' | 'failed';

export interface AdminFailure {
  kind: AdminFailureKind;
  /** The API's problem `code` (`name_taken`, `role_in_use`, ...), or null when it sent none. */
  code: string | null;
  /** The sentence for the banner. For `validation` it is the server's general text, not a field's. */
  message: string;
  /** The API's `errors` with camelCase keys; empty unless the failure is `validation`. */
  fieldErrors: Record<string, string[]>;
}

/**
 * A failed `/api/admin` call as the pages show it (design 8.9). Pure.
 * `fallback` is the sentence for a failure that said nothing of its own ("The role could not be saved.").
 */
export function adminFailure(err: unknown, fallback: string): AdminFailure {
  const problem = problemOf(err, '');
  const { code } = problem;
  switch (problem.status) {
    case 401:
      return { kind: 'failed', code, message: SESSION_ENDED, fieldErrors: {} };
    case 403:
      // A user who must change the password is not short of a permission: the API says which it is.
      return {
        kind: 'forbidden',
        code,
        message: code === 'password_change_required' ? MUST_CHANGE_PASSWORD : ACCESS_LOST,
        fieldErrors: {},
      };
    case 404:
      return { kind: 'gone', code, message: GONE, fieldErrors: {} };
    case 409:
      return code === 'last_administrator'
        ? {
            kind: 'last_administrator',
            code,
            message: `${problem.message} ${LAST_ADMINISTRATOR_ADVICE}`.trim(),
            fieldErrors: {},
          }
        : { kind: 'conflict', code, message: problem.message || fallback, fieldErrors: {} };
    case 400:
      if (Object.keys(problem.fieldErrors).length > 0) {
        return {
          kind: 'validation',
          code,
          message: problem.message,
          fieldErrors: problem.fieldErrors,
        };
      }
      break;
  }
  return { kind: 'failed', code, message: failureText(err, fallback), fieldErrors: {} };
}

/** The API's messages of a form, split by where they go. */
export interface PlacedErrors {
  /** The messages for each of the form's fields, by field name. */
  placed: Record<string, string[]>;
  /** The messages for request paths that no field of the form stands for: the banner lists them. */
  unplaced: string[];
}

/** Whether the request path `path` is the field `field` or a member or element below it (`grants[0].sagaTypes` is below `grants`). */
export function belongsToField(path: string, field: string): boolean {
  return path === field || path.startsWith(`${field}[`) || path.startsWith(`${field}.`);
}

/**
 * Sorts the API's `errors` (keyed by request path) into the fields of a form. A path belongs to a field when it
 * is the field or a member or element below it: `permissions[2]` and `grants[0].sagaTypes` belong to
 * `permissions` and `grants`. Pure.
 */
export function placeFieldErrors(
  fieldErrors: Record<string, string[]>,
  fields: readonly string[],
): PlacedErrors {
  const placed: Record<string, string[]> = {};
  const unplaced: string[] = [];
  for (const [path, messages] of Object.entries(fieldErrors)) {
    const field = fields.find((name) => belongsToField(path, name));
    if (field === undefined) unplaced.push(...messages);
    else placed[field] = [...(placed[field] ?? []), ...messages];
  }
  return { placed, unplaced };
}
