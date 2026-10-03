import { DOCUMENT } from '@angular/common';
import {
  HttpContext,
  HttpContextToken,
  HttpErrorResponse,
  HttpEvent,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
  HttpXsrfTokenExtractor,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { Observable, catchError, from, switchMap, throwError } from 'rxjs';
import { AuthService } from '../services/auth.service';

/** Marks the one retry of a request that the API refused for its antiforgery token. */
const ANTIFORGERY_RETRY = new HttpContextToken<boolean>(() => false);

/** The header the API reads the antiforgery token from (the built-in XSRF interceptor's default). */
const XSRF_HEADER = 'X-XSRF-TOKEN';

/** The `ProblemDetails` code of a request refused for a missing or stale antiforgery token. */
const ANTIFORGERY_CODE = 'antiforgery';

/** The sign-in endpoints: a 401 from them is an answer to the call, not a session that ended. */
const SESSION_ENDPOINTS = new Set([
  '/api/auth/login',
  '/api/auth/logout',
  '/api/auth/setup',
  '/api/auth/session',
]);

/**
 * Keeps the SPA in step with the sign-in session. The session cookie and the `XSRF-TOKEN` cookie ride on
 * the browser's own requests and the built-in XSRF interceptor, which runs before this one, copies the
 * cookie into `X-XSRF-TOKEN` on same-origin unsafe requests; this interceptor adds no credential of its
 * own (no `Authorization`, no API key). It reacts to what the API answers, for URLs under `/api/` only:
 *
 * - 401 (outside the sign-in endpoints): the session ended behind the SPA's back, so the user is sent to
 *   the login page. The identity epoch is read when the request is sent, so a 401 to a request sent before
 *   the latest sign-in, sign-out or password change cannot end the session that replaced it.
 * - 403: access may have changed, so the session is read again.
 * - 400 with the code `antiforgery`: the token was missing, stale or another identity's (sign-in, a
 *   sign-out in another tab, the cookie of a second stack on the same host). The session is read again,
 *   which re-issues the cookie, and the request is sent once more with the new token. The built-in
 *   interceptor already stamped the old token, so the clone replaces the header. The failure of that second
 *   request is the caller's.
 *
 * Every failure reaches the caller unchanged.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const path = pathOf(req.url, inject(DOCUMENT).baseURI);
  if (!path.startsWith('/api/')) return next(req);
  const endsSession = !SESSION_ENDPOINTS.has(path);
  return send(req, next, endsSession, inject(AuthService), inject(HttpXsrfTokenExtractor));
};

/** Sends `req` and reacts to a refusal. `endsSession` is false for the sign-in endpoints, whose 401 is an
 *  answer to the call. */
function send(
  req: HttpRequest<unknown>,
  next: HttpHandlerFn,
  endsSession: boolean,
  auth: AuthService,
  xsrf: HttpXsrfTokenExtractor,
): Observable<HttpEvent<unknown>> {
  // Read when the request is sent, not when it fails: whatever changed the identity in between is
  // exactly what makes this request's 401 stale.
  const epoch = auth.currentIdentityEpoch();

  return next(req).pipe(
    catchError((err: unknown) => {
      if (!(err instanceof HttpErrorResponse)) return throwError(() => err);

      if (err.status === 401) {
        if (endsSession) auth.handleUnauthorized(epoch);
      } else if (err.status === 403) {
        auth.noteForbidden();
      } else if (
        err.status === 400 &&
        isUnsafe(req) &&
        isAntiforgeryRefusal(err) &&
        !req.context.get(ANTIFORGERY_RETRY)
      ) {
        return from(auth.refresh()).pipe(
          switchMap(() => {
            const token = xsrf.getToken();
            // No token even after the session was read: a retry would be refused the same way.
            if (token === null) return throwError(() => err);
            return send(retryWith(req, token), next, endsSession, auth, xsrf);
          }),
        );
      }
      return throwError(() => err);
    }),
  );
}

/** `req` again with `token` in place of whatever the built-in interceptor stamped, marked as the retry.
 *  The context is copied: `HttpContext.set` mutates, and the same request is sent again by anyone who
 *  subscribes to the call a second time. */
function retryWith(req: HttpRequest<unknown>, token: string): HttpRequest<unknown> {
  const context = new HttpContext();
  for (const key of req.context.keys()) context.set(key, req.context.get(key));
  context.set(ANTIFORGERY_RETRY, true);
  return req.clone({ headers: req.headers.set(XSRF_HEADER, token), context });
}

/** Whether the API checks `req` for an antiforgery token: every method but the safe ones, which the
 *  built-in interceptor leaves unstamped too. Besides being true to the API, this keeps the session read
 *  that the retry waits for (a GET) from ever waiting for itself. */
function isUnsafe(req: HttpRequest<unknown>): boolean {
  return req.method !== 'GET' && req.method !== 'HEAD';
}

function isAntiforgeryRefusal(err: HttpErrorResponse): boolean {
  const body: unknown = err.error;
  return (
    typeof body === 'object' &&
    body !== null &&
    (body as Record<string, unknown>)['code'] === ANTIFORGERY_CODE
  );
}

/** The path of `url` as the browser resolves it against the page's base; '' for a URL that cannot be read. */
function pathOf(url: string, base: string): string {
  try {
    return new URL(url, base).pathname;
  } catch {
    return '';
  }
}
