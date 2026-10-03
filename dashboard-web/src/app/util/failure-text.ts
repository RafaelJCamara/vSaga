import { problemOf } from './http-error';

/** What a page says when no answer came: the API is down, or the network is. */
export const CANNOT_REACH =
  'Cannot reach the dashboard API. Check that it is running, then try again.';

/** What a page says when the API answered 503 `identity_unavailable`: it is up, but nobody can sign in. */
export const SIGN_IN_UNAVAILABLE =
  "Sign-in is unavailable: the API is running, but its identity store is not ready. Ask an administrator to check the identity entry of the API's /health.";

/**
 * One sentence for a failed call that no field of a form explains, in the words every sign-in page uses:
 * "too many attempts" with the delay the server named, "sign-in is unavailable", "cannot reach the API".
 * Anything else is the server's own text (`problemOf`'s `message`), or `fallback` when it sent none.
 * Pure; a status-0 error that carries its own text (the auth service's "Signed in, but the session
 * could not be confirmed") keeps it.
 */
export function failureText(err: unknown, fallback: string): string {
  const problem = problemOf(err, '');
  if (problem.status === 429) {
    return problem.retryAfterSeconds === null
      ? 'Too many attempts. Try again in a moment.'
      : `Too many attempts. Try again in ${problem.retryAfterSeconds} s.`;
  }
  if (problem.code === 'identity_unavailable') return SIGN_IN_UNAVAILABLE;
  if (problem.status === 0) return problem.message || CANNOT_REACH;
  if (problem.status >= 500) {
    return `The dashboard API is not answering properly (HTTP ${problem.status}). Try again in a moment.`;
  }
  return problem.message || fallback;
}
