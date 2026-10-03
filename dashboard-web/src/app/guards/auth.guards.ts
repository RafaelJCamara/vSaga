import { inject } from '@angular/core';
import { CanActivateFn, CanMatchFn, Router, UrlTree } from '@angular/router';
import { AuthService } from '../services/auth.service';

/** Where a visit with no usable return URL ends up, and where a signed-in user without access goes. */
const HOME_URL = '/sagas';

/**
 * Whether the session was never read (`unknown`), so a guard must ask the server before it decides. An
 * `unreachable` session is not asked again: its first read already waited out the session timeout (8 s) for
 * an answer that never came, and asking once more would hold the visitor up for as long again before the
 * login page can say so. That page asks again by itself (it polls), and sends the visitor on when the API
 * answers.
 */
function mustAsk(auth: AuthService): boolean {
  return auth.status() === 'unknown';
}

/** The first path segment of an app URL, ignoring a query string, a fragment and matrix parameters. */
function firstSegment(url: string): string {
  return url.slice(1).split(/[?#]/, 1)[0].split(/[/;]/, 1)[0];
}

/**
 * The same-app path a sign-in may return to, from the untrusted `returnUrl` query parameter, or `/sagas`.
 * Pure. Only an absolute path of the app passes: not one that starts `//` or `/\` (the browser reads both
 * as another host), not a full URL (`https://x`, `javascript:`), not one with control characters, and not
 * the login or setup pages themselves (returning there would loop).
 */
export function safeReturnUrl(raw: string | null | undefined): string {
  if (typeof raw !== 'string') return HOME_URL;
  if (!/^\/(?![/\\])/.test(raw) || /[\u0000-\u001f\u007f]/.test(raw)) return HOME_URL;
  const first = firstSegment(raw);
  return first === 'login' || first === 'setup' ? HOME_URL : raw;
}

/**
 * Where the session sends a visit to `url` that needs a signed-in user, or null when it may go on:
 * `/setup` while no user exists, `/login` (carrying `url` to return to) with no session, and `/account`
 * for a user who must change their password first (their access is empty until they do).
 */
function redirectFor(auth: AuthService, router: Router, url: string): UrlTree | null {
  if (auth.setupRequired()) return router.createUrlTree(['/setup']);
  if (!auth.isAuthenticated()) {
    return router.createUrlTree(['/login'], { queryParams: { returnUrl: url } });
  }
  if (auth.user()?.mustChangePassword && firstSegment(url) !== 'account') {
    return router.createUrlTree(['/account']);
  }
  return null;
}

/**
 * A page that needs a signed-in user. Asks the server first when the session is not known yet, so a
 * deep link opened in a new tab is judged on the real session, then redirects as `redirectFor` says.
 * Each guard here injects before its first `await`, which is the only place injection works.
 */
export const authGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (mustAsk(auth)) await auth.refresh();
  return redirectFor(auth, router, state.url) ?? true;
};

/**
 * The login page: for a visitor with no session. Setup comes first while no user exists, and a visitor
 * who is signed in already is sent on to the page the sign-in was for. The session is read only when it
 * was never asked for: while the API is unreachable the page shows that and asks again itself, and a
 * guard that waited on a hung API for every navigation to it would hold the page back.
 */
export const anonymousGuard: CanActivateFn = async (route) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (auth.status() === 'unknown') await auth.refresh();
  if (auth.setupRequired()) return router.createUrlTree(['/setup']);
  if (auth.isAuthenticated()) {
    return router.parseUrl(safeReturnUrl(route.queryParamMap.get('returnUrl')));
  }
  return true;
};

/**
 * The setup page: only while no user exists (the page itself says when setup cannot be completed even
 * so). Anyone else, including a visitor to an API that cannot be reached, gets the login page, which
 * says what is wrong and sends them back here when setup turns out to be required.
 */
export const setupGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (auth.status() === 'unknown') await auth.refresh();
  return auth.setupRequired() ? true : router.createUrlTree(['/login']);
};

/**
 * The administration area, as a `canMatch` guard so that its chunk loads only for a user who may use it.
 * The session redirects of `authGuard` come first, with the return URL built from the segments being
 * matched; then `access.manage` for every saga type, or the saga list.
 */
export const adminGuard: CanMatchFn = async (_route, segments) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (mustAsk(auth)) await auth.refresh();
  const url = `/${segments.map((segment) => segment.toString()).join('/')}`;
  return (
    redirectFor(auth, router, url) ??
    (auth.canManageAccess() ? true : router.createUrlTree([HOME_URL]))
  );
};
