# Login, session handling and the administration area in the SPA (improvement 6)

## Summary

- The SPA becomes a same-origin cookie-session client. `AuthService` (signals) loads `GET /api/auth/session` in an app initializer, so guards choose `/setup`, `/login`, `/account` (forced password change) or the requested page before the first navigation.
- `apiKeyInterceptor` and `DASHBOARD_API_KEY` are deleted. One `authInterceptor` handles 401 (local sign-out, then `/login?returnUrl=`), 403 (refresh access) and antiforgery 400 (re-issue the token, retry once). The XSRF header itself comes from Angular's built-in interceptor, which fires only for same-origin URLs.
- `SagaHubService` loses `accessTokenFactory` and gains `stopAndReset()`, `resume()` and a session probe: a failed negotiate asks the session endpoint and stops for good when the session is gone.
- Top bar: brand, primary nav (Sagas; Administration only with `access.manage`), then the guide-toggle slot and a user menu (Account, Sign out).
- Retry, data affordances and cross-links are gated by `auth.can(permission, sagaType)`; the detail page gains a no-access state for 403.
- Administration is a lazy route tree (users, teams, roles: list and routed edit pages) over a route-scoped signal store, with a grants editor, a live effective-access preview and inline confirms.
- Template-driven forms, no new dependency; shared primitives in the global stylesheet; login, setup, account and admin are lazy (about 10 kB added to the initial bundle).

## Design

Paths without a `dashboard-web/` or `dotnet/` prefix are relative to `C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/`. Bare names of existing files (`saga-detail.ts`, `saga-list.html`, `saga-hub.service.spec.ts`) mean the file of that name under `pages/saga-detail/`, `pages/saga-list/` or `services/`.

### 1. Prerequisite and verified facts

- Assignment A's relative URLs (`API_BASE_URL = ''`, `HUB_URL = '/hubs/saga'`, dev proxy) land first: cookies and XSRF need same-origin.
- Angular 21.2.24 `xsrfInterceptorFn` skips GET/HEAD and any URL whose origin differs from the page (`dashboard-web/node_modules/@angular/common/fesm2022/_module-chunk.mjs:1371-1397`), is registered ahead of user interceptors (`:2003-2010`) and defaults to `XSRF-TOKEN` / `X-XSRF-TOKEN` (`:1286-1292`). No `withXsrfConfiguration` is needed.
- signalR 10.0.11 (`dashboard-web/node_modules/@microsoft/signalr/dist/esm/`): `withCredentials` defaults to true (`HttpConnection.js:24-25`); a negotiate 401 is rethrown as `FailedToNegotiateWithServerError` with the status only inside the message (`:248-257`); `stop()` cancels a reconnect delay (`HubConnection.js:204-225`).

### 2. Routes (replaces `app.routes.ts:5-11`)

```ts
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'sagas' },
  { path: 'login', canActivate: [anonymousGuard], loadComponent: () => import('./pages/login/login').then((m) => m.Login) },
  { path: 'setup', canActivate: [setupGuard], loadComponent: () => import('./pages/setup/setup').then((m) => m.Setup) },
  { path: 'sagas', component: SagaList, canActivate: [authGuard] },
  { path: 'sagas/:sagaType/:id', component: SagaDetail, canActivate: [authGuard] },
  { path: 'account', canActivate: [authGuard], loadComponent: () => import('./pages/account/account').then((m) => m.Account) },
  { path: 'admin', canMatch: [adminGuard], loadChildren: () => import('./pages/admin/admin.routes').then((m) => m.ADMIN_ROUTES) },
  { path: '**', redirectTo: 'sagas' },
];
```

`ADMIN_ROUTES`: one parent `{ path: '', component: AdminShell, providers: [AdminStore] }` with children `''` (redirect to `users`), `users`, `users/new`, `users/:id`, `teams`, `teams/new`, `teams/:id`, `roles`, `roles/new`, `roles/:id`.

`app.config.ts:8-14` becomes: `provideRouter(routes, withNavigationErrorHandler(reloadOnceOnStaleChunk))`, `provideHttpClient(withInterceptors([authInterceptor]))`, `provideAppInitializer(() => inject(AuthService).bootstrap())`. `reloadOnceOnStaleChunk` reloads the page when a lazy import fails (message matches `/dynamically imported module|module script/i`), at most once a minute (timestamp in `sessionStorage`).

### 3. Models

`models/auth.model.ts`:

```ts
export type PermissionKey = 'sagas.view' | 'sagas.data' | 'sagas.retry' | 'access.manage';
export type SessionStatus = 'unknown' | 'anonymous' | 'authenticated' | 'unreachable';
export interface SessionUser { id: string; username: string; displayName: string; mustChangePassword: boolean }
export interface SessionAccess { permissions: string[]; scoped: { sagaType: string; permissions: string[] }[] }
export interface SessionInfo {
  authenticated: boolean; setupRequired: boolean; setupAvailable: boolean;
  user: SessionUser | null; access: SessionAccess | null; passwordMinLength?: number;
}
```

`pages/admin/admin.model.ts` (lazy):

```ts
export interface PermissionInfo { key: string; name: string; description: string }
export interface Role { id: string; name: string; description: string; builtIn: boolean; permissions: string[] }
export interface Grant { roleId: string; allSagaTypes: boolean; sagaTypes: string[] }
export interface AdminUser {
  id: string; username: string; displayName: string; enabled: boolean; mustChangePassword: boolean;
  lockedUntilUtc: string | null; lastLoginAtUtc: string | null; teamIds: string[]; grants: Grant[];
}
export interface Team { id: string; name: string; description: string; memberIds: string[]; grants: Grant[] }
export type CreateUser = Pick<AdminUser, 'username' | 'displayName' | 'enabled' | 'mustChangePassword' | 'teamIds' | 'grants'> & { password: string };
export type UpdateUser = Pick<AdminUser, 'displayName' | 'enabled' | 'teamIds' | 'grants'>;
export type SaveTeam = Omit<Team, 'id'>;
export type SaveRole = Pick<Role, 'name' | 'description' | 'permissions'>;
```

### 4. AuthService (`services/auth.service.ts`, root)

```ts
readonly status: Signal<SessionStatus>;
readonly user: Signal<SessionUser | null>;
readonly access: Signal<SessionAccess | null>;
readonly isAuthenticated: Signal<boolean>;   // also setupRequired, setupAvailable, canManageAccess
readonly passwordMinLength: Signal<number | null>;
bootstrap(): Promise<void>;                  // never rejects; 8 s timeout
refresh(): Promise<SessionStatus>;           // single-flight GET /api/auth/session; never rejects
login(username: string, password: string): Promise<void>;   // rejects with the HttpErrorResponse
logout(): Promise<void>;
setup(body: { username: string; displayName: string; password: string }): Promise<void>;
changePassword(currentPassword: string, newPassword: string): Promise<void>;
can(permission: PermissionKey, sagaType?: string): boolean;
canAny(permission: PermissionKey): boolean;
handleUnauthorized(): void;                  // interceptor
noteForbidden(): void;                       // interceptor
```

- `can`: true if `access.permissions` has it, or `sagaType` is given and a `scoped` entry with `sagaType === sagaType` (ordinal) has it. `canAny`: unscoped or any scoped entry.
- Promises here (chained flows, guards, initializer); HTTP services stay Observable like `SagaApiService`.
- `refresh()` failure (network, timeout, non-2xx): status becomes `'unreachable'` only from `'unknown'`; otherwise the last known session is kept, so an API restart never signs the UI out.
- `login`, `logout`, `setup`, `changePassword` do their POST, then `reload()` (await any in-flight refresh, then a fresh one). This is required, not cosmetic: ASP.NET antiforgery tokens are bound to the identity, so `XSRF-TOKEN` must be re-issued after every identity change. POST response bodies are ignored.
- `login` rejects when the reloaded session is not authenticated. `setup` and `changePassword` accept either server behaviour: authenticated afterwards means continue, otherwise go to `/login` with a notice.
- `logout`: `await hub.stopAndReset()`, set `'anonymous'` locally, POST (errors swallowed), `reload()`, navigate `/login`.
- Session lost (`handleUnauthorized()`, or a refresh that flips authenticated to anonymous), idempotent: `hub.stopAndReset()`; navigate `/login?returnUrl=<router.url>&reason=expired` unless already on `/login` or `/setup`; `refresh()` to obtain the anonymous XSRF token.
- A refresh returning a different `user.id` (another tab signed in as someone else) does a full page reload.
- `noteForbidden()` (at most once per 5 s) and a `visibilitychange` listener (at most once per 60 s) call `refresh()`, so permission-aware UI and a tab signed out elsewhere catch up.
- The constructor calls `hub.setSessionProbe(async () => (await this.refresh()) === 'authenticated')`. The dependency is one-way, auth to hub.

### 5. Guards (`guards/auth.guards.ts`)

Each injects before any `await`.

- `authGuard: CanActivateFn`: when status is `unknown` or `unreachable`, `await auth.refresh()` first. Then, in order: `setupRequired` redirects to `/setup`; no session redirects to `/login?returnUrl=state.url`; `user.mustChangePassword` on any target but `/account` redirects to `/account`; otherwise true.
- `anonymousGuard`: `setupRequired` redirects to `/setup`; a live session redirects to `safeReturnUrl(returnUrl)`; otherwise true.
- `setupGuard`: true only when `setupRequired`; otherwise `/login`.
- `adminGuard: CanMatchFn`: the `authGuard` redirects (return URL built from the segments), then `canManageAccess()` or `/sagas`. `canMatch` keeps the admin chunk from loading for anyone else.
- `safeReturnUrl(raw)`: `raw` only if it matches `^/(?![/\\])` and is not `/login` or `/setup`; otherwise `/sagas`.

### 6. Interceptor (`interceptors/auth.interceptor.ts`; `api-key.interceptor.ts` deleted)

Acts only when `new URL(req.url, document.baseURI).pathname` starts with `/api/`. Adds nothing to requests.

- 401 on a path other than `/api/auth/{login,logout,setup,session}`: call `auth.handleUnauthorized()`, rethrow.
- 403: call `auth.noteForbidden()`, rethrow.
- 400 with problem `code === 'antiforgery'`, not yet retried (an `HttpContextToken`): `await auth.refresh()`, then `next()` a clone whose `X-XSRF-TOKEN` is replaced from `HttpXsrfTokenExtractor.getToken()`. The clone is needed because the built-in interceptor ran first and stamped the stale token.

### 7. SagaHubService (`saga-hub.service.ts`)

- `:50` becomes `.withUrl(HUB_URL)`; the `DASHBOARD_API_KEY` import goes.
- New state: `generation = 0`, `active = true`, `sessionProbe = () => Promise.resolve(true)`. The defaults preserve today's behaviour, so the existing specs stand.
- `setSessionProbe(probe: () => Promise<boolean>): void`; `resume(): void` sets `active = true` (called by `AuthService` whenever a refresh yields an authenticated session).
- `stopAndReset(): Promise<void>`: `generation++`, `active = false`, null `connection` and `startPromise`, clear `listSubscribed` and `sagaSubscriptions`, emit `'disconnected'`, `await old?.stop()` inside try/catch. Never rejects.
- `ensureStarted()` (`:47-75`) returns at once when `!active`. Lifecycle handlers ignore a connection that is no longer `this.connection`.
- `startWithRetry(connection, generation)` (`:87-100`): after a failed `start()`, `if (!(await this.sessionProbe())) { await this.stopAndReset(); return; }`. Every await is followed by `if (generation !== this.generation) return;`.
- The retry policy (`:26-30`) moves into the class. When `previousRetryCount > 0` (a reconnect attempt failed) it runs the same probe in the background and calls `stopAndReset()` on false. It still always returns a delay.
- `subscribeToList` and `subscribeToSaga` (`:114-137`) re-check `generation` after `ensureStarted()` before recording the subscription.
- Each `invoke` gets an inline try/catch: with per-method checks the hub may refuse `SubscribeToSaga`, and callers are fire-and-forget (`saga-detail.ts:71`). Do not route them through an async helper: the extra microtask hop breaks `saga-hub.service.spec.ts:307-321`, which waits exactly two ticks.

Result: the connection is still created lazily by the first subscribe and exists only while a session does. API down: the probe answers true and the loop retries as today. Session gone: the probe answers false, the hub stops, the user lands on `/login` and negotiate calls cease.

### 8. Shell and top bar (`app.html:2-7`, `app.ts:10`)

```html
<header class="topbar">
  <a class="brand" routerLink="/sagas"><!-- unchanged --></a>
  @if (auth.isAuthenticated()) {
    <nav class="topnav" aria-label="Primary">
      <a routerLink="/sagas" routerLinkActive="active">Sagas</a>
      @if (auth.canManageAccess()) {
        <a routerLink="/admin" routerLinkActive="active" data-tour="nav-admin">Administration</a>
      }
    </nav>
  }
  <div class="topbar-end">
    <!-- guide toggle (assignment E) sits here, left of the user menu -->
    @if (auth.isAuthenticated()) { <app-user-menu data-tour="user-menu" /> }
  </div>
</header>
```

`App` stays the shell, so login and setup render under a brand-only bar and `app.spec.ts:23` holds. `UserMenu` (`components/user-menu`): a trigger button showing the display name (`aria-haspopup="menu"`, `aria-expanded`); a panel with name, username, an `Account` link and a `Sign out` button; closes on outside click (`(document:click)` host listener), Escape and selection; state in `open` and `signingOut` signals.

### 9. Permission-aware UI

`saga-detail.ts` injects `AuthService` and adds `canRetry()` (`auth.can('sagas.retry', sagaType)`), `canSeeData()` (`'sagas.data'`) and `forbidden = signal(false)`.

- Retry row (`saga-detail.html:85-100`): the existing block renders only when `canRetry()`; otherwise the row reads "You do not have permission to retry {{ sagaType }} sagas."
- `retry()` (`saga-detail.ts:283-286`) uses `problemOf(err, 'Retry failed.').message`, which reads `{ error }` first, then `detail`, then `title`.
- `load()` error (`:136-140`): on status 403 set `forbidden` and leave `error` null, so the reconnect reload at `:86` is not triggered. The template gains `@else if (forbidden())`: "You do not have access to {{ sagaType }} sagas. Ask an administrator for sagas.view on this saga type.", plus the back link.
- "Started by" (`saga-detail.html:57-67`) is plain text with "(no access)" when `!auth.can('sagas.view', parentSagaType)`.
- Data affordances (assignment C's inspector and start/end/compare; until then the Data tab button at `:106`) render only when `canSeeData()`. Otherwise one muted line: "State data and message payloads are hidden for your role."

`saga-list.ts`: the saga-type options need no change, because `/api/saga-types` is filtered by the server (`saga-list.html:17-20`). When `!auth.canAny('sagas.view')`, `ngOnInit` (`:69-95`) skips its calls and the template shows "Your account has no access to any saga type yet." The list error handler (`:242-245`) maps 403 to "You do not have access to these sagas." and keeps today's text otherwise.

### 10. Login, setup, account (lazy)

- Login: username and password (`autocomplete="username"` / `"current-password"`), focus set with `afterNextRender`. Submit calls `auth.login`, then `router.navigateByUrl(safeReturnUrl(returnUrl))`. A 400 or 401 shows one uniform line, "Sign-in failed. Check the username and password; repeated failures lock the account for a while."; a 429 shows "Too many attempts. Try again in N s." from `Retry-After`; 0 or 5xx shows that the API cannot be reached. The password is cleared and refocused. `reason=expired` shows "Your session expired." While status is `unreachable` a banner replaces the form and `refresh()` runs every 3 s.
- Setup: username, display name, password, confirmation. When `!setupAvailable()`: no form, a notice that the setup window has closed (restart the API or set `Dashboard:Admin:Username` and `Dashboard:Admin:Password`) and a "Check again" button. Success goes to `/sagas`.
- Account: read-only identity; change-password form (current, new, confirmation); "Your access" rendered by `AccessSummary` from `session.access`; a banner when `mustChangePassword`. Success goes to `/sagas`.

### 11. Administration (lazy)

- `AdminApiService` (Observable, in the style of `saga-api.service.ts`): `GET /api/admin/permissions`; `GET|POST /api/admin/{users,teams,roles}`; `PUT|DELETE /api/admin/{users,teams,roles}/{id}`; `POST /api/admin/users/{id}/password { newPassword, mustChangePassword }`; `POST /api/admin/users/{id}/unlock`.
- `AdminStore` (route-scoped): signals `users`, `teams`, `roles`, `permissions`, `sagaTypes`, `loaded`, `loadError`; `load()` (`forkJoin` of the four lists plus `/api/saga-types`); `saveUser(id | null, body)`, `deleteUser`, `resetPassword`, `unlockUser`, `saveTeam`, `deleteTeam`, `saveRole`, `deleteRole`. Each awaits the API, then reloads users, teams and roles. `AdminShell` renders Users / Teams / Roles sub-tabs and shows the outlet only when `loaded()`.
- Lists (`.data-table`):
  - Users: username (link), display name, status chips (Disabled, Locked, Must change password), teams, access summary such as "Operator · 2 types", last sign-in; a client-side text filter; "New user".
  - Teams: name, member count, access summary.
  - Roles: name, Built-in or Custom, permission chips, "used by N grants".
- Edit pages read the id with `toSignal(route.paramMap)`. The draft is a signal seeded once from the store and re-seeded after a successful save.
  - User: username (create only), display name, enabled, password with confirmation and "require a change at next sign-in" (create only, default on), team checkboxes, `<app-grants-editor [(grants)]="grants" />`, the effective-access preview, Save and Cancel. On edit also Reset password (inline sub-form), Unlock and Delete. Enabled and Delete are disabled on the signed-in user's own record.
  - Team: name, description, member checkboxes with a filter, grants editor, Delete.
  - Role: name, description, one checkbox per catalogue entry with its description. Built-ins render read-only with "Duplicate as custom role" (`/admin/roles/new?from=<id>`). Delete is disabled while the role is in use.
- `GrantsEditor`: `grants = model.required<Grant[]>()`; inputs `roles`, `sagaTypes`, `errors`. Per grant: a role `<select>`; radios "All saga types" / "Selected saga types"; when selected, a checkbox per known type and per already-granted unknown type, plus an "exact saga type name" input, because `/api/saga-types` lists only types that already have instances (`dotnet/src/VSaga.Persistence.EFCore/EfCoreSagaSummaryReader.cs:105-117`). Inline messages: "Pick at least one saga type" (blocks Save) and "access.manage is ignored in a scoped grant". "Add grant" and "Remove".
- Effective access: pure `explainAccess({ enabled, grants, teams, roles }): AccessRow[]` in `access-explain.ts`. It unions the user's own grants with those of each team passed in. An all-types grant adds every role permission to the "All saga types" row. A scoped grant adds every permission except `access.manage` to each named type. Unknown role ids are ignored and a disabled user yields nothing. Each permission keeps its origins ("direct: Operator", "team Payments: Viewer"). `AccessSummary` renders scope by permission as a table with the origins in `title`. It is computed from the draft, so it previews unsaved edits.
- `ConfirmButton` (inputs `label`, `prompt`, `confirmLabel`, `busy`, `disabled`; output `confirmed`) is the retry row's two-step pattern (`saga-detail.html:86-95`) as a component, used for every delete.

### 12. Forms and server errors

- `FormsModule` with `[(ngModel)]` bound to writable signals, `#f="ngForm"`, `required`, `minlength` from `auth.passwordMinLength()`, confirmation match as a `computed`, submit disabled while `busy()`. `FormsModule` is already in the initial bundle (`saga-list.ts:3`); `@angular/forms/signals` is still `@experimental 21.0.0` (`dashboard-web/node_modules/@angular/forms/types/signals.d.ts:26`).
- `shared/http-error.ts`: `problemOf(err, fallback): { status, code, message, fieldErrors, retryAfterSeconds }`. `message` is `body.error ?? body.detail ?? body.title ?? fallback`; `fieldErrors` is `body.errors` with keys lower-camelled.
- Validation (400 with `errors`): each message under its field (`.field-error`, `aria-describedby`, `aria-invalid`); `grants[i].*` keys go to that grant row; unmatched keys are listed in the form banner.
- Last administrator (409, `code: 'last_administrator'`): a `role="alert"` banner at the top of the form with the server `detail` and "Give another enabled user an all-saga-types grant whose role includes access.manage, then try again." The draft is kept.
- Other 409s (`role_in_use`, duplicate name): banner with `detail`. 404 on save: "This no longer exists", back to the list. 403: "You no longer have permission to manage access."

### 13. Styles

Added once to `dashboard-web/src/styles.scss` (global, so outside the 4 kB component budget; about 3 kB of the 96 kB initial headroom): `.muted`, `.empty`, `.banner` with `--error|--warning|--info|--success` (values copied from `saga-list.scss:54-91,144-146`), `.btn` with `--quiet|--danger`, `.field`, `.label`, `.input`, `.field-hint`, `.field-error`, `.card`, `.auth-card`, `.data-table`, `.chip` with `--muted|--danger`, `.page-header`, `.toolbar`, `.subtabs`, `.menu`, and a focus-visible outline. New component stylesheets hold layout only, under 1 kB each. `app.scss` gains `.topnav` and `.topbar-end { margin-left: auto }`. Global `.muted` also styles the so-far unstyled `class="muted"` in `saga-detail.html`.

## Files

Create:
- `models/auth.model.ts`: session DTOs, `PermissionKey`.
- `services/auth.service.ts` and spec.
- `guards/auth.guards.ts` and spec: four guards, `safeReturnUrl`.
- `interceptors/auth.interceptor.ts` and spec.
- `shared/http-error.ts` and spec.
- `testing/auth-mock.ts`: `createAuthMock(overrides)`, `provideAuthMock()`.
- `components/user-menu/*`, `components/confirm-button/*`, `components/access-summary/*`, each with a spec.
- `pages/login/*`, `pages/setup/*`, `pages/account/*`, each with a spec.
- `pages/admin/`: `admin.routes.ts`, `admin.model.ts`, `admin-api.service.ts`, `admin.store.ts`, `access-explain.ts`, `admin-shell/*`, `grants-editor/*`, and list and edit components under `users/`, `teams/`, `roles/`, with specs.

Modify:
- `app.config.ts`, `app.routes.ts`, `app.ts`, `app.html`, `app.scss`, `app.spec.ts`.
- `api-config.ts`: remove `DASHBOARD_API_KEY`, rewrite the comment.
- `saga-hub.service.ts` and spec.
- `saga-detail.{ts,html}` and spec; `saga-list.{ts,html}` and spec.
- `dashboard-web/src/styles.scss`.
- `dashboard-web/tsconfig.app.json`: exclude `src/app/testing/**` (`:12-14`).

Delete: `interceptors/api-key.interceptor.ts`.

## Tests

Existing specs that change:
- `app.spec.ts:6-11`: add `provideAuthMock()`; `:19-24` stays. Add: no nav and no user menu when anonymous; Administration link only with `access.manage`.
- `saga-hub.service.spec.ts:3`: drop the `DASHBOARD_API_KEY` import. `:122-127` becomes "builds against HUB_URL with no access token factory". The other 21 pass unchanged.
- `saga-detail.spec.ts:104-117`, `:187-195`, `:248-256` and `saga-list.spec.ts:45-52`, `:81-88`, `:111-118`, `:146-153`, `:512-519`, `:541-563`, `:591-602`: add `provideAuthMock()`. It grants every permission by default, so `saga-detail.spec.ts:291-295` still finds the retry button.
- `saga-api.service.spec.ts`: no change.

New cases:
- `auth.service.spec.ts`: bootstrap authenticated, anonymous, setup-required and network error (never rejects, `'unreachable'`); refresh is single-flight; a failed refresh keeps an authenticated session; login is POST then a fresh session GET, rejects on 401 with state untouched, rejects when the session stays anonymous; logout order (hub stop, POST, reload, navigate) and survival of a failing POST; `can` (unscoped, scoped exact type, case-sensitive, no session) and `canAny`; `handleUnauthorized` navigates once with `returnUrl`, is a no-op the second time, adds no `returnUrl` on `/login`; a changed user id reloads the page; the probe is true when unreachable and false when signed out.
- `auth.interceptor.spec.ts`: never sends `X-Api-Key` or `Authorization`; 401 on `/api/sagas` calls `handleUnauthorized` and still errors; 401 on `/api/auth/login` does not; 403 calls `noteForbidden`; non-API URLs are ignored; an antiforgery 400 triggers a refresh and one retry carrying the new cookie value, and a second failure propagates; a same-origin POST carries `X-XSRF-TOKEN` from `document.cookie` and a GET does not.
- `auth.guards.spec.ts` (`TestBed.runInInjectionContext`): every branch of the four guards; `safeReturnUrl` rejects `//evil`, `/\evil`, `https://x`, `/login`.
- `saga-hub.service.spec.ts` additions: `stopAndReset` stops, emits `disconnected`, forgets subscriptions and blocks subscribing until `resume()`; after `resume()` a new connection is built; failed start with probe false leaves `startCount` at 1 and never emits `connected`; `stopAndReset` during the back-off ends the loop (fake timers); a policy call with `previousRetryCount: 1` and probe false stops the connection; a rejected `invoke` does not reject `subscribeToSaga`; events from a replaced connection are ignored.
- `saga-detail.spec.ts` additions: without `sagas.retry` the button is absent and the hint shown; 403 sets `forbidden()`, shows no error banner and causes no reload on reconnect; a 403 from retry shows the problem `detail`; the parent link is text without `sagas.view`.
- `saga-list.spec.ts` additions: the no-access state skips `api.list`; the 403 message.
- `login.spec.ts`, `setup.spec.ts`, `account.spec.ts`: happy paths; uniform failure text; 429; expired notice; unreachable polling; confirmation mismatch; field errors; closed setup window; `currentPassword` error; forced-change banner.
- `access-explain.spec.ts`: direct plus team union; scoped `access.manage` dropped; disabled user; unknown role; origins.
- Admin and shared specs (`grants-editor`, the edit and list pages, `admin.store`, `admin-api.service`, `confirm-button`, `user-menu`, `http-error`): scope toggle; custom type; an empty scoped grant blocks Save; the `last_administrator` banner keeps the draft; field errors are placed; own-record Delete and Enabled are disabled; built-in roles are read-only; delete needs confirmation; verbs and URLs.

## Commit sequence

Prerequisites: assignment A's relative-URL commit, and the API's auth endpoints before step 5 for a working demo.

1. Promote the shared banner, button, form and table styles to the global stylesheet.
2. Give `SagaHubService` `stopAndReset`, `resume`, a session probe and guarded invokes (the key is still sent).
3. Add the session models, `problemOf`, `AuthService` and the auth test helper (not yet wired).
4. Add the login, setup and account pages with their guards as lazy routes (saga routes still open).
5. Require a session: `authGuard` on the saga routes, `authInterceptor`, the app initializer, top-bar nav and user menu; delete the API-key interceptor and `DASHBOARD_API_KEY`; hub without a token factory.
6. Gate retry, data affordances and cross-links by permission; add the 403 states.
7. Add the administration shell, API service, store and the roles screens.
8. Add user management with the grants editor and the effective-access preview.
9. Add team management.

Every step passes `npx ng build` and `npx ng test --watch=false`.

## Verification

- `cd dashboard-web && npm ci && npm audit --audit-level=low && npx ng build && npx ng test --watch=false`: no budget warnings; the build lists lazy chunks for login, setup, account and admin.
- Searching `dashboard-web/src` and `dashboard-web/dist/dashboard-web/browser` for `dev-local-only-change-me` or `X-Api-Key` finds nothing.
- `docker compose up -d --build`, then `http://localhost:4200`:
  - With no seed the app opens on `/setup`, and creating the administrator lands on `/sagas`. With `Dashboard__Admin__Username` and `Dashboard__Admin__Password` set it opens on `/login`.
  - DevTools: the session cookie is HttpOnly; `XSRF-TOKEN` is readable; `POST …/retry` carries `X-XSRF-TOKEN`; `/hubs/saga` upgrades with no `access_token` in the URL.
  - A detail URL opened in a private window goes to `/login?returnUrl=…` and returns to that saga after sign-in.
  - Sign out: the socket closes and no further negotiate requests appear.
  - A Viewer scoped to one saga type sees only that type in the list and the filter, has no retry button, gets the no-access state on another type's URL, and is sent from `/admin` to `/sagas`.
  - Disabling or deleting the only administrator shows the banner and loses nothing.
  - `docker compose restart dashboard-api`: "Reconnecting…", then recovery without a new login.
  - `docker compose down -v && docker compose up -d` with the tab left open: the tab ends on `/setup` or `/login`, not on a permanent "Reconnecting".
- `npx ng serve`: the same flows work through the dev proxy.

## Cross-assignment contracts

- A (packaging): relative URLs and a `ws` proxy first. The SPA is same-origin only; `Dashboard:WebOrigin` no longer serves it. nginx: `index.html` with `Cache-Control: no-cache`; a missing `*.js` must return 404, not `index.html`.
- D (API):
  - `GET /api/auth/session` always answers 200 and re-issues `XSRF-TOKEN` (readable by script, `Path=/`) for the current principal on every call. Proposed extra field: `passwordMinLength`.
  - Antiforgery failure is 400 `application/problem+json` with `code: "antiforgery"`. `/hubs/**` is exempt, because negotiate is a POST that carries no XSRF header.
  - Login failure is a uniform 401; rate limiting is 429 with `Retry-After`. A wrong current password on `/api/auth/password` is 400 with `errors.currentPassword`, never 401.
  - Problems carry `detail`, an optional `code` (`last_administrator`, `role_in_use`, `setup_closed`) and `errors` keyed by request property (`grants[0].sagaTypes`).
  - Admin DTOs as in section 3; the two extra endpoints in section 11; lists unpaged; ids are strings; team membership writable through both `teamIds` and `memberIds`.
  - Hub method names and arguments are unchanged; a refused subscription may throw.
- B and C (detail page): use `AuthService.can('sagas.data', sagaType)` and `provideAuthMock()`. Whoever lands first adds the section 13 primitives to the global stylesheet; the other reuses them.
- E (guidance): the toggle goes in `.topbar-end` before `<app-user-menu>`. Anchors: `nav-admin`, `user-menu`, `admin-tabs`, `admin-users`, `admin-grants`, `admin-effective-access`, `admin-teams`, `admin-roles`. Admin steps only when `auth.canManageAccess()`.
- F (docs): `dashboard-web/README.md:39-65` and `:67-79` describe the removed key and interceptor.
- Routes beyond the sketch: `/admin/{users,teams,roles}/new` and `/admin/{users,teams,roles}/:id`.

## Objections

None. Two consequences of fixed decisions are handled as contracts and risks instead: hub negotiate must be exempt from antiforgery, and browsers share cookies across localhost ports.

## Critical Files for Implementation

- C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/services/saga-hub.service.ts
- C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/app.config.ts
- C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/app.routes.ts
- C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/app.html
- C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-detail/saga-detail.ts

## Risks
- Browsers do not isolate cookies by port, so the base stack on localhost:4200 and any overlay on localhost:4300-4800 share one cookie jar, although README.md:112-113 and :125 say the overlays can run alongside the plain stack. Signing in to one stack replaces the other's session and XSRF-TOKEN cookies. Mitigation: the SPA recovers cleanly (a 401 lands on /login with a return URL; an antiforgery 400 re-issues the token and retries once); the README should tell users to open a second stack as http://127.0.0.1:<port>, which is a separate cookie host; if simultaneous sign-in matters, the API names its cookies per stack from one configuration key set in each overlay.
- SignalR's negotiate is a POST that carries no X-XSRF-TOKEN header. If the API enforces antiforgery on every unsafe request, the hub never connects and the pages sit on 'Reconnecting'. Mitigation: the contract exempts /hubs/**; the compose verification checks the WebSocket upgrade; the fallback is passing the token through the headers option of withUrl.
- ASP.NET antiforgery tokens are bound to the identity, so a token issued before sign-in, or before a session expired, fails on the next unsafe request. Mitigation: AuthService re-reads the session after every identity change and after a 401, and the interceptor retries once on problem code 'antiforgery'. If the API omits that code the retry never fires and the user has to reload, which is why the code is listed as a contract.
- The hub specs depend on microtask timing: saga-hub.service.spec.ts:307-321 waits exactly two ticks for three invokes, so routing invoke through an async helper makes it fail. Mitigation: inline try/catch only, no added awaits on the success path, and the existing 22 hub tests run unchanged as the gate for commit 2.
- A missing AuthService mock fails silently. HttpClient is providedIn 'root' in Angular 21.2.24 (dashboard-web/node_modules/@angular/common/fesm2022/_module-chunk.mjs:1750-1756), so a page spec without the mock gets a real service whose can() returns false, and the retry-button tests fail for a non-obvious reason. Mitigation: provideAuthMock(), which grants every permission by default, is added at the ten TestBed sites listed and in app.spec.ts.
- The effective-access preview is computed in the browser and could drift from the server's rule. Mitigation: one pure function with spec cases mirroring the server's (union, team origin, scoped access.manage ignored, disabled user); the account page shows the server-computed access; a GET /api/admin/users/{id}/access endpoint can replace the computation later without UI changes.
- Permissions held by the SPA and hub group membership can be stale after an administrator changes grants: the SPA learns on the next 403, tab focus or sign-in, and hub groups are fixed at subscribe time. Mitigation: the server enforces every request; the SPA refreshes access on 403 and on visibility change; the API should abort a user's hub connections when their grants or security stamp change, because the client then reconnects and rejoins under the new access.
- Budgets: the 96 kB of initial-bundle headroom is shared with the timeline, per-step data and guidance work, and component styles warn at 4 kB. Mitigation: only AuthService, the guards, the interceptor and the user menu are eager; the pages and the admin area are lazy; shared CSS is global; the build output is checked in every commit.
- Lazy chunks introduce a stale-tab failure that the app does not have today: after the web container is rebuilt, an open tab requests chunk names that no longer exist, and an index.html fallback would answer a script request with HTML. Mitigation: a navigation error handler reloads the page once; nginx returns 404 for missing assets and serves index.html with no-cache (contract with packaging).
- Changing the hub retry loop could regress the existing 'an API restart recovers on its own' behaviour. Mitigation: the probe answers true by default and on any network or 5xx failure; the existing retry specs stay as they are; 'docker compose restart dashboard-api' is a verification step.
- Sequencing across assignments: the switch-over commit needs the relative URLs and the API's auth endpoints. If the SPA lands first nobody can sign in; if the API lands first the old SPA's key maps to Viewer and Retry returns 403. Mitigation: SPA commits 1 to 4 are inert; land packaging, then the API, then commit 5 together.
- returnUrl is attacker-controlled, and sign-in error text could reveal which accounts exist. Mitigation: safeReturnUrl accepts only same-app paths and is covered by specs; the sign-in failure line is identical for every cause.
- Idle timeout and live pages interact unevenly: a list tab only receives pushes, so its cookie expires and the next click lands on /login, while a busy detail page refetches on every push and keeps sliding the session. Mitigation: the return URL preserves the user's place; the behaviour is documented; the API can close hub connections when the cookie expires.
- An access manager without sagas.view gets an empty /api/saga-types, and saga types with no instances are never listed, so the scope picker may show no suggestions. Mitigation: the grants editor accepts an exact saga type name; an unfiltered admin list of saga types can be added later.

## Open questions
- README.md:112-113 and :125 present the overlay stacks as able to run alongside the plain one, but browsers share cookies across localhost ports, so with cookie sessions signing in to one stack signs you out of another. Is it enough to document opening the second stack as http://127.0.0.1:<port>, or should each stack get its own cookie names (one extra configuration key, set in every overlay)?
