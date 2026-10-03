import { DOCUMENT } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom, timeout } from 'rxjs';
import { API_BASE_URL } from '../api-config';
import { PermissionKey, SessionInfo, SessionStatus, SetupRequest } from '../models/auth.model';
import { problemOf } from '../util/http-error';
import { holds, holdsAccessManage, holdsAny } from '../util/session-access';
import { SagaHubService } from './saga-hub.service';

/**
 * Reloads the page. A token so a spec can observe the reload instead of performing it: jsdom cannot
 * navigate, and the reload is the one effect of the service that leaves the app.
 */
export const PAGE_RELOAD = new InjectionToken<() => void>('PAGE_RELOAD', {
  providedIn: 'root',
  factory: () => {
    const doc = inject(DOCUMENT);
    return () => doc.location.reload();
  },
});

const AUTH_URL = `${API_BASE_URL}/api/auth`;
const SESSION_URL = `${AUTH_URL}/session`;

/** How long one session request may take before it counts as failed: an app that starts under this
 *  budget is never held on a hung API, and a refresh can never stay in flight for good. */
const SESSION_TIMEOUT_MS = 8000;
/** A 403 refreshes access at most this often: a page that fires many forbidden requests asks once. */
const FORBIDDEN_REFRESH_INTERVAL_MS = 5000;
/** Returning to the tab refreshes the session at most this often. */
const VISIBLE_REFRESH_INTERVAL_MS = 60000;

/**
 * The sign-in session: who the caller is and what they may do, loaded from `GET /api/auth/session`.
 * Everything the UI needs is a signal; the flows are promises (guards, the app initializer and chained
 * steps await them), and the HTTP services elsewhere stay Observable.
 *
 * Rules that hold for every method:
 * - A failed refresh never signs anyone out. It keeps the last known session, and only an app that has
 *   no session at all yet (`unknown`) is marked `unreachable`: an API restart must not end the UI's session.
 * - After every identity change (sign-in, sign-out, setup, password change) the session is read again,
 *   which also re-issues the `XSRF-TOKEN` cookie: the API binds antiforgery tokens to the identity, so a
 *   token read before the change fails the next unsafe request.
 * - The dependency on the hub is one way: this service stops, resumes and probes it; the hub knows nothing
 *   of this service.
 *
 * Not wired into the app by itself: the app initializer, the guards and the interceptor that call it come
 * with the pages that need a session.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly hub = inject(SagaHubService);
  private readonly document = inject(DOCUMENT);
  private readonly reloadPage = inject(PAGE_RELOAD);

  private readonly statusState = signal<SessionStatus>('unknown');
  /** The last session the server described, kept through failed refreshes. Null until the first answer. */
  private readonly sessionState = signal<SessionInfo | null>(null);
  private readonly unavailableState = signal(false);

  private inFlight: Promise<SessionStatus> | null = null;
  private lastForbiddenRefreshAt = Number.NEGATIVE_INFINITY;
  private lastVisibleRefreshAt = Number.NEGATIVE_INFINITY;

  readonly status = this.statusState.asReadonly();
  readonly isAuthenticated = computed(() => this.statusState() === 'authenticated');
  /** The signed-in user; null when anonymous, before the first answer, and for the API key. */
  readonly user = computed(() => this.sessionState()?.user ?? null);
  readonly access = computed(() => this.sessionState()?.access ?? null);
  /** True while no user exists. */
  readonly setupRequired = computed(() => this.sessionState()?.setupRequired ?? false);
  /** True when first-run setup can be completed now, with the one-time code. */
  readonly setupAvailable = computed(() => this.sessionState()?.setupAvailable ?? false);
  /** Why setup cannot be completed while it is required; null otherwise. */
  readonly setupProblem = computed(() => this.sessionState()?.setupProblem ?? null);
  /** The shortest password the policy accepts; null until the first answer. */
  readonly passwordMinLength = computed(() => this.sessionState()?.passwordMinLength ?? null);
  readonly canManageAccess = computed(() => holdsAccessManage(this.access()));
  /** True while the latest session request was answered 503: the API is up but its identity store is not,
   *  so nobody can sign in. A login page shows "sign-in is unavailable" for this, and "cannot reach the
   *  API" for `unreachable` otherwise. Cleared by the next session the server describes. */
  readonly signInUnavailable = this.unavailableState.asReadonly();

  constructor() {
    // "Gone" only when the server said so: an unreachable API, a 5xx and a session request that timed out
    // all leave the last known status in place (and `unreachable` is not `anonymous`), so the probe answers
    // true and the hub keeps retrying; a probe that answered false on an API restart would end live updates.
    // It always settles: the refresh behind it is bounded by the session timeout, and the hub's start loop
    // waits for the answer before its next attempt.
    this.hub.setSessionProbe(async () => (await this.refresh()) !== 'anonymous');

    const onVisibilityChange = (): void => {
      if (this.document.visibilityState !== 'visible') return;
      const now = Date.now();
      if (now - this.lastVisibleRefreshAt < VISIBLE_REFRESH_INTERVAL_MS) return;
      this.lastVisibleRefreshAt = now;
      void this.refresh();
    };
    this.document.addEventListener('visibilitychange', onVisibilityChange);
    inject(DestroyRef).onDestroy(() =>
      this.document.removeEventListener('visibilitychange', onVisibilityChange),
    );
  }

  /** Loads the session for the app initializer. Never rejects: whatever happens the app starts, and the
   *  guards decide what to show from the status. Bounded by the session request's 8 s timeout. */
  async bootstrap(): Promise<void> {
    await this.refresh();
  }

  /** Reads the session again. One request at a time: callers that arrive while one is in flight share it.
   *  Never rejects; resolves with the status afterwards. */
  refresh(): Promise<SessionStatus> {
    this.inFlight ??= this.fetchSession().finally(() => {
      this.inFlight = null;
    });
    return this.inFlight;
  }

  /** Rejects with the `HttpErrorResponse` when the server refuses, and when the session is still not
   *  authenticated afterwards (a cookie the browser dropped, say). */
  async login(username: string, password: string): Promise<void> {
    await firstValueFrom(this.http.post(`${AUTH_URL}/login`, { username, password }));
    await this.reload();
    if (this.statusState() !== 'authenticated') {
      throw new HttpErrorResponse({
        status: 401,
        statusText: 'Unauthorized',
        url: `${AUTH_URL}/login`,
        error: {
          title: 'Sign-in did not start a session',
          detail:
            'The server accepted the sign-in but no session was established. Check that the browser accepts cookies for this site.',
        },
      });
    }
  }

  /**
   * Ends the session: the hub first (it must not reconnect with a cookie that is about to die), then local
   * state, then the server (a failing POST changes nothing: the cookie may outlive it, and the next read of
   * the session says so), then a fresh session for the anonymous `XSRF-TOKEN`, then the login page. Never
   * rejects.
   */
  async logout(): Promise<void> {
    await this.hub.stopAndReset();
    this.signOutLocally();
    try {
      await firstValueFrom(this.http.post(`${AUTH_URL}/logout`, {}));
    } catch {
      // The user asked to leave: nothing to show for a server that did not hear it.
    }
    await this.reload();
    await this.router.navigate(['/login']).catch(() => false);
  }

  /** First-run setup: creates the first administrator, who the API signs in. Rejects with the
   *  `HttpErrorResponse` when refused (a wrong code is a 400 with `errors.code`). Resolves whatever the
   *  session is afterwards: the caller checks `isAuthenticated()`. */
  async setup(body: SetupRequest): Promise<void> {
    await firstValueFrom(this.http.post(`${AUTH_URL}/setup`, body));
    await this.reload();
  }

  /** Rejects with the `HttpErrorResponse` when refused (a wrong current password is a 400 with
   *  `errors.currentPassword`). The API signs the user in again under the new password. */
  async changePassword(currentPassword: string, newPassword: string): Promise<void> {
    await firstValueFrom(this.http.post(`${AUTH_URL}/password`, { currentPassword, newPassword }));
    await this.reload();
  }

  /** Whether the session holds `permission`, for `sagaType` when one is given (see `holds`). */
  can(permission: PermissionKey, sagaType?: string): boolean {
    return holds(this.access(), permission, sagaType);
  }

  /** Whether the session holds `permission` for every saga type or at least one. */
  canAny(permission: PermissionKey): boolean {
    return holdsAny(this.access(), permission);
  }

  /**
   * For the interceptor: a request answered 401 means the session ended behind the SPA's back. Signs out
   * locally (the guards must see an anonymous session before the login route is activated), stops the hub,
   * goes to `/login` carrying the page to come back to, and reads the session again for the anonymous
   * `XSRF-TOKEN`. Idempotent: the requests in flight when a session ends all fail together, and only the
   * first call finds a session to end.
   */
  handleUnauthorized(): void {
    if (this.statusState() !== 'authenticated') return;
    void this.hub.stopAndReset();
    this.signOutLocally();
    this.goToLoginAsExpired();
    void this.reload();
  }

  /** For the interceptor: a request answered 403, so access may have changed. Refreshes the session, at
   *  most once per 5 s. */
  noteForbidden(): void {
    const now = Date.now();
    if (now - this.lastForbiddenRefreshAt < FORBIDDEN_REFRESH_INTERVAL_MS) return;
    this.lastForbiddenRefreshAt = now;
    void this.refresh();
  }

  /** Waits for a refresh in flight, then reads the session afresh: an answer that was already on its way
   *  may describe the identity from before the change the caller just made. */
  private async reload(): Promise<SessionStatus> {
    await this.inFlight;
    return this.refresh();
  }

  private async fetchSession(): Promise<SessionStatus> {
    let session: SessionInfo;
    try {
      session = await firstValueFrom(
        this.http.get<SessionInfo>(SESSION_URL).pipe(timeout(SESSION_TIMEOUT_MS)),
      );
      if (!isSessionInfo(session))
        throw new Error('The session endpoint did not answer a session.');
    } catch (err) {
      this.markFailed(err);
      return this.statusState();
    }
    this.accept(session);
    return this.statusState();
  }

  /** The server described the session: adopt it, and notice what changed since the last one. */
  private accept(next: SessionInfo): void {
    const wasAuthenticated = this.statusState() === 'authenticated';
    const previousUserId = this.sessionState()?.user?.id ?? null;

    this.sessionState.set(next);
    this.statusState.set(next.authenticated ? 'authenticated' : 'anonymous');
    this.unavailableState.set(false);

    if (next.authenticated) {
      this.hub.resume();
      // Another tab signed in as someone else: the cookie is shared, so this tab's requests are already
      // that user's, but everything it shows was loaded as the previous one. Start over.
      if (previousUserId !== null && next.user !== null && next.user.id !== previousUserId) {
        this.reloadPage();
      }
    } else if (wasAuthenticated) {
      // Signed out elsewhere, or the session expired, and nothing was requested in between to say so. The
      // session read already carried the anonymous XSRF token, so unlike `handleUnauthorized` it reads nothing more.
      void this.hub.stopAndReset();
      this.goToLoginAsExpired();
    }
  }

  /** The session could not be read. Keep what was known; mark `unreachable` only when nothing was. */
  private markFailed(err: unknown): void {
    this.unavailableState.set(problemOf(err, '').status === 503);
    if (this.statusState() === 'unknown') this.statusState.set('unreachable');
  }

  private signOutLocally(): void {
    this.sessionState.update((s) =>
      s ? { ...s, authenticated: false, user: null, access: null } : s,
    );
    this.statusState.set('anonymous');
  }

  /** To `/login?returnUrl=<where the user was>&reason=expired`; nowhere when already on `/login` or `/setup`. */
  private goToLoginAsExpired(): void {
    const url = this.router.url;
    const path = url.split(/[?#]/, 1)[0];
    if (path === '/login' || path === '/setup') return;
    void this.router
      .navigate(['/login'], { queryParams: { returnUrl: url, reason: 'expired' } })
      .catch(() => false);
  }
}

/** A session the server described, as opposed to whatever else answered 200 (a proxy's fallback page). */
function isSessionInfo(value: unknown): value is SessionInfo {
  const session = value as Partial<SessionInfo> | null;
  return (
    typeof session === 'object' &&
    session !== null &&
    typeof session.authenticated === 'boolean' &&
    typeof session.setupRequired === 'boolean' &&
    typeof session.setupAvailable === 'boolean'
  );
}
