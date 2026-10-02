# Authentication, identity store and access enforcement on the server (improvement 6)

## Summary
- Two new non-packable projects: `VSaga.Dashboard.Identity` (models, `IDashboardIdentityStore`, EF Core context and store, services) and `VSaga.Dashboard.Identity.Sqlite` (generated migrations only). No new package versions are needed.
- A policy scheme sends each request to the API-key handler (when a key credential is present) or the cookie handler. Both write one shared 401 problem body, so the existing 401 tests pass unchanged.
- Authorization is a fallback "authenticated" policy plus three permission policies, evaluated against a `CallerAccess` loaded from the store on every request (stamp, enabled flag, grants), so revocation is immediate.
- Saga endpoints get route-scoped 403s, filtered lists, a bounded k-way merge for callers scoped to several types, and payload redaction without `sagas.data`.
- Hub: per-type list groups, per-call checks, connections aborted when access changes, payload-free timeline pushes.
- An unusable identity database never blocks start-up: `/health` shows `identity` as degraded (HTTP 200), auth endpoints answer 503, and an API key mapped to a built-in role keeps working.

## Design

### 1. Projects and tooling
- `dotnet/src/VSaga.Dashboard.Identity`: `Microsoft.NET.Sdk`, `IsPackable=false`, `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (for `PasswordHasher<T>` and `IXmlRepository`), packages `Microsoft.EntityFrameworkCore` and `.Relational`. Folders `Model/`, `Stores/`, `EFCore/`, `Services/`.
- `dotnet/src/VSaga.Dashboard.Identity.Sqlite`: the three analyzer switches of `VSaga.Persistence.EFCore.Postgres.csproj:7-11`, `IsPackable=false`, package `Microsoft.EntityFrameworkCore.Sqlite`, `Migrations/` only.
- The API references `.Sqlite` and calls `UseSqlite(conn, o => o.MigrationsAssembly("VSaga.Dashboard.Identity.Sqlite"))`, the shape of `Program.cs:56-57`.
- No design-time factory. dotnet-ef builds the context from the API host's service provider, as it does for `VSagaDbContext` (EF Design reference, `VSaga.Dashboard.Api.csproj:17-21`). Two contexts are now registered, so `--context` becomes mandatory for both:
  `dotnet ef migrations add InitialCreate --context DashboardIdentityDbContext --project dotnet/src/VSaga.Dashboard.Identity.Sqlite --startup-project dotnet/src/VSaga.Dashboard.Api --output-dir Migrations`

### 2. Model and EF mapping
Store-neutral records:
```csharp
record AccessGrant(Guid RoleId, bool AllSagaTypes, IReadOnlyList<string> SagaTypes);
record DashboardUser(Guid Id, string Username, string DisplayName, string PasswordHash, string SecurityStamp,
    bool IsEnabled, bool MustChangePassword, int FailedSignInCount, DateTimeOffset? LockoutEndUtc,
    DateTimeOffset? LastSignInAtUtc, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, IReadOnlyList<AccessGrant> Grants);
record DashboardTeam(Guid Id, string Name, string? Description, IReadOnlyList<Guid> MemberIds, IReadOnlyList<AccessGrant> Grants);
record DashboardRole(Guid Id, string Name, string? Description, bool IsBuiltIn, IReadOnlyList<string> Permissions);
```
`Permissions` holds the four keys; `sagas.data` and `sagas.retry` imply `sagas.view` for the same scope. `BuiltInRoles` has fixed ids (`a0000000-0000-0000-0000-00000000000{1,2,3}`) and code-defined permission sets.

| Table | Keys and indexes | Notes |
|---|---|---|
| `Users` | PK `Id`; unique `NormalizedUsername` | |
| `Teams` | PK `Id`; unique `NormalizedName` | |
| `TeamMembers` | PK `(TeamId, UserId)`; index `UserId` | both FKs cascade |
| `Roles` | PK `Id`; unique `NormalizedName` | `Permissions` as JSON |
| `UserGrants`, `TeamGrants` | PK `Id`; unique `(owner, RoleId)` | owner FK cascade, role FK restrict; `AllSagaTypes`, `SagaTypes` as JSON |
| `DataProtectionKeys` | PK `Id` autoincrement | `FriendlyName`, `Xml` |

- `Permissions` and `SagaTypes` are `List<string>` mapped as EF primitive collections (one JSON text column), so no hand-written converter. Evaluation is in memory; nothing queries inside them.
- SQLite caveats:
  - EF cannot order or compare `DateTimeOffset` on SQLite. `ConfigureConventions` applies a UTC `DateTime` converter as `VSagaDbContext.cs:17-20` does; that converter is `internal`, so copy it.
  - Default collation is case-sensitive and `NOCASE` is ASCII-only. Uniqueness goes through a `Normalized*` column (`Trim().ToUpperInvariant()`), which a non-relational store can reuse as its key.
  - No rowversion. Lockout counters use single atomic `ExecuteUpdateAsync` statements.
  - Invariant-checking writes run in a transaction; Microsoft.Data.Sqlite begins it as `BEGIN IMMEDIATE`, so check-then-write is serialised.
  - EF creates the file in WAL mode, so `-wal` and `-shm` files sit beside it on the volume.
- Connection: `SqliteConnectionStringBuilder { DataSource = path, Mode = ReadWriteCreate, ForeignKeys = true }`.

### 3. Store surface (aggregate-shaped: no `IQueryable`, no joins exposed)
```csharp
public interface IDashboardIdentityStore
{
    Task InitializeAsync(CancellationToken ct);                          // migrate or create indexes; idempotent
    Task<bool> CanConnectAsync(CancellationToken ct);
    Task<IIdentityWriteScope> BeginExclusiveAsync(CancellationToken ct); // IAsyncDisposable + CommitAsync
    Task<int> CountUsersAsync(CancellationToken ct);
    Task<DashboardUser?> FindUserAsync(Guid id, CancellationToken ct);
    Task<DashboardUser?> FindUserByNameAsync(string normalizedUsername, CancellationToken ct);
    Task<IReadOnlyList<DashboardUser>> ListUsersAsync(CancellationToken ct);
    Task CreateUserAsync(DashboardUser user, CancellationToken ct);      // IdentityConflictException on a duplicate
    Task UpdateUserAsync(DashboardUser user, CancellationToken ct);      // full replace, grants included
    Task DeleteUserAsync(Guid id, CancellationToken ct);                 // also leaves every team
    Task<DateTimeOffset?> RecordFailedSignInAsync(Guid id, int maxAttempts, DateTimeOffset now, TimeSpan lockout, CancellationToken ct);
    Task RecordSignInAsync(Guid id, DateTimeOffset now, string? rehash, CancellationToken ct);
    // teams: Find, List, ListForUser, Create, Update, Delete
    // roles: Find, FindByName, List, Create, Update, Delete, IsRoleInUseAsync
}
public interface IDashboardKeyRingStore { IReadOnlyList<KeyRingEntry> Load(); void Save(KeyRingEntry entry); }
```
The key-ring interface is synchronous because `IXmlRepository` is. Both are scoped. The EF store reads with `AsNoTracking` and maps entities to records.

### 4. Services
- `PasswordPolicy.Validate(username, password)`: length `MinLength` to 128, not equal to the username ignoring case. No composition rules.
- `CredentialVerifier.VerifyAsync(username, password)` returns the user or null:
  1. Look up by normalized name.
  2. Unknown, disabled or `LockoutEndUtc > now`: verify against a start-up dummy hash for timing parity, return null, count nothing.
  3. `PasswordHasher<DashboardUser>.VerifyHashedPassword`. A failure calls `RecordFailedSignInAsync`: increment; at `MaxFailedAttempts` set `LockoutEndUtc = now + Minutes` and reset the count.
  4. Success calls `RecordSignInAsync`: reset the count, store a rehash on `SuccessRehashNeeded`.
- Security stamp: 128 random bits as hex, rotated on password change, administrator reset, disable and enable. Cookie claims: `sub`, `name`, `vsaga:stamp`.
- `AccessEvaluator.Evaluate(user, teams, roles)` returns `EffectiveAccess`, a map from permission to `SagaTypeScope` (All, or an ordinal set). A disabled user gets nothing. For every grant of the user and of their teams, and each catalogue permission of its role: skip `access.manage` unless `AllSagaTypes`; union the scope; add the implied `sagas.view`. API: `Has(permission, sagaType)`, `HasAny(permission)`, `HasUnscoped(permission)`, `ScopeFor(permission)`.
- `ICallerAccessResolver.ResolveAsync(ClaimsPrincipal)` returns `CallerAccess(Kind, UserId, Username, DisplayName, MustChangePassword, Access)`, or null when the user is missing or disabled, the stamp differs, the API-key role is unknown, or the store is not ready. `AuditActor` is `dashboard:<username>` or `dashboard:api-key`. While `MustChangePassword` is true, `Access` is empty.
- `AccessAdministrationService`: each mutation runs inside `BeginExclusiveAsync`. It loads users, teams and roles, applies the change to that snapshot, validates, checks the invariant on the proposed snapshot (at least one enabled user with `HasUnscoped(access.manage)`), writes, commits, then notifies `IAccessChangeObserver` (the affected user ids, or everyone for a role change). Violations throw `IdentityRuleException(code)`, mapped to 409.
- Rule: nothing that needs Data Protection (`SignInAsync`, antiforgery) runs inside an exclusive scope, because the key ring uses its own connection.
- `IdentityStoreXmlRepository : IXmlRepository` (singleton, one DI scope per call) over `IDashboardKeyRingStore`, set through `IConfigureOptions<KeyManagementOptions>`; `AddDataProtection().SetApplicationName("VSaga.Dashboard")`.

### 5. Program.cs, in order
Services (grouped in `Auth/DashboardAuthExtensions.cs` to respect the method-length rule):
1. `TryAddSingleton(TimeProvider.System)`. It is only implicit today, and `SagaEndpoints.cs:128` needs it.
2. `Dashboard:Identity:Provider` switch after the persistence switch (`Program.cs:46-71`): `"Sqlite"` calls `AddDashboardIdentity(...)`; anything else throws `Unknown Dashboard:Identity:Provider '<x>'`.
3. `AddOptions<DashboardSecurityOptions>().BindConfiguration("Dashboard")` with range validation and `ValidateOnStart`.
4. Data Protection (section 4).
5. `AddAuthentication("Dashboard")` with three schemes:
   - `AddPolicyScheme("Dashboard", …)`: `ForwardDefaultSelector` returns `"ApiKey"` when the request has `X-Api-Key`, a Bearer header or `access_token`, otherwise `"Cookie"`.
   - `AddCookie("Cookie")`: HttpOnly, `SameSite=Strict`, `SecurePolicy=SameAsRequest`, `ExpireTimeSpan` from `IdleTimeoutMinutes`, sliding, non-persistent, `EventsType = DashboardCookieEvents`.
   - The existing `AddScheme<…>("ApiKey")` (`Program.cs:120-121`).
   `DashboardCookieEvents.ValidatePrincipal` calls the resolver and stores an `ICallerAccessFeature`; on null it calls `RejectPrincipal()`, signing out only when the store is ready. `RedirectToLogin` and `RedirectToAccessDenied` write problem bodies.
6. Authorization: `FallbackPolicy` and `DefaultPolicy` require an authenticated user. Policies `sagas.view`, `sagas.retry` and `access.manage` each hold a `PermissionRequirement(permission, requireUnscoped)`. `PermissionAuthorizationHandler` reads the feature from the `HttpContext` resource: with a `sagaType` route value it checks `Has(p, sagaType)`, without one `HasAny(p)`, and `HasUnscoped(p)` when `requireUnscoped`. A custom `IAuthorizationMiddlewareResultHandler` writes the 403 problem (`code`, `permission`, `sagaType`).
7. `AddAntiforgery`: `HeaderName = "X-XSRF-TOKEN"`, `SuppressReadingTokenFromFormBody = true`, cookie `<session cookie>.af`, Strict, SameAsRequest.
8. `AddRateLimiter`: policy `auth`, a fixed window per client IP of `Dashboard:RateLimit:AuthPerMinute`, 429 problem with `Retry-After`.
9. `Configure<ForwardedHeadersOptions>`: `XForwardedFor | XForwardedProto`, trusting `Dashboard:TrustedProxies` through `KnownProxies` and `KnownIPNetworks` (`KnownNetworks` is obsolete in .NET 10 and would fail the build); `*` clears both lists.
10. Health check `identity` with `failureStatus: Degraded`, beside `Program.cs:106`.
11. `HubConnectionRegistry` and the hub's `IAccessChangeObserver`.

Pipeline:
1. After the saga migration block (`Program.cs:131-142`): `await IdentityStartup.EnsureReadyAsync()` (section 6).
2. `UseForwardedHeaders()`, `MapOpenApi().AllowAnonymous()` in Development, `UseCors`.
3. `HubOriginGuard`, for `/hubs` only: allow no `Origin`, `Origin == scheme://Host`, or `Origin == Dashboard:WebOrigin`; otherwise 403. Cookies are not port-scoped and WebSockets bypass CORS, so this is what stops another localhost origin riding the session.
4. `UseRateLimiter()`, `UseAuthentication()`, `UseAuthorization()`.
5. `AntiforgeryEnforcement`: for every method other than GET, HEAD, OPTIONS and TRACE under `/api`, unless the identity's authentication type is `ApiKey`, require `IAntiforgery.IsRequestValidAsync`; a failure is 400 `antiforgery`. The framework's `UseAntiforgery()` targets form-bound endpoints and does not reject JSON requests itself, so it is not used.
6. `MapAuthEndpoints()`, `MapAdminEndpoints()`, `MapSagaEndpoints()`, `MapHub<SagaHub>("/hubs/saga", o => o.CloseOnAuthenticationExpiration = true).RequireAuthorization()`, `MapHealthChecks("/health", …).AllowAnonymous()`.

Antiforgery issuance, `XsrfCookie.Issue(ctx)`: `GetAndStoreTokens`, then append the request token as cookie `XSRF-TOKEN` (not HttpOnly, Strict, Secure on HTTPS). Tokens are bound to the `sub` claim, so login, setup and logout set `HttpContext.User` to the new principal before issuing.

### 6. Start-up, default path, health
- Default path: `{LocalApplicationData}/vSaga/dashboard/identity.db`, or `{AppContext.BaseDirectory}/data/identity.db` when that folder is unavailable. The image sets `/data/identity.db`. The path is read lazily through options.
- `IdentityStartup.EnsureReadyAsync` (singleton, serialised, 30-second timeout, never throws): create the directory, `InitializeAsync` (`MigrateAsync`), upsert built-in roles from code, seed the administrator, warm the key ring. On failure it logs the resolved path, leaves `IsReady` false, and retries at most every 10 seconds when the health check or an auth endpoint calls it. The `identity` check calls it, then `CanConnectAsync`.
- While not ready:
  - cookie principals are rejected without clearing the cookie (401);
  - `/api/auth/*` returns 503 `identity_unavailable`;
  - an API key with a built-in role still works, because built-ins resolve from code;
  - `/health` stays 200 with `identity` degraded, so `order-processing`'s `service_healthy` gate (`docker-compose.yml:74-75`) is not blocked.
- Tests: `DashboardApiFactory` calls `UseSetting("Dashboard:Identity:Sqlite:Path", <unique temp file>)` and on dispose runs `SqliteConnection.ClearAllPools()` and a best-effort delete. The three health test classes switch their fixture to `RealCompositionFactory : WebApplicationFactory<Program>`, which only sets that path.

### 7. Endpoints
Errors on these routes are `application/problem+json` with a `code` extension; validation adds `errors`.

| Route | Access | Success | Errors |
|---|---|---|---|
| `GET /api/auth/session` | anonymous | 200 `SessionResponse`, sets `XSRF-TOKEN`, `no-store` | 503 |
| `POST /api/auth/login` `{username,password}` | anonymous, `auth` limiter | 200 `SessionResponse` and the session cookie | 400 `validation` or `antiforgery`; 401 `invalid_credentials`, identical for unknown, wrong, locked and disabled; 429; 503 |
| `POST /api/auth/logout` | anonymous | 200 anonymous `SessionResponse` | 400 |
| `POST /api/auth/setup` `{username,displayName,password}` | anonymous, limiter | 200 `SessionResponse`, signed in | 400; 409 `setup_unavailable`; 429 |
| `POST /api/auth/password` `{currentPassword,newPassword}` | cookie user, limiter | 200 `SessionResponse`, cookie reissued | 400 `invalid_credentials` (not 401, so the SPA's 401 handling does not fire) or `validation`; 403 for an API key |
| `GET /api/admin/permissions` | `access.manage` | 200 `[{key,name,description,scopable,implies}]` | |
| `GET,POST /api/admin/roles`; `GET,PUT,DELETE /api/admin/roles/{id}` | `access.manage` | 200, 201, 204 | 400; 404; 409 `name_taken`, `role_immutable`, `role_in_use`, `last_administrator` |
| `GET,POST /api/admin/users`; `GET,PUT,DELETE /api/admin/users/{id}`; `POST …/{id}/password`; `POST …/{id}/unlock` | `access.manage` | 200, 201, 204 | 400; 404; 409 `username_taken`, `last_administrator` |
| `GET,POST /api/admin/teams`; `GET,PUT,DELETE /api/admin/teams/{id}` | `access.manage` | 200, 201, 204 | 400; 404; 409 `name_taken`, `last_administrator` |

DTOs (camelCase on the wire):
```csharp
record SessionResponse(bool Authenticated, bool SetupRequired, bool SetupAvailable, SessionUser? User, SessionAccess? Access);
record SessionUser(Guid Id, string Username, string DisplayName, bool MustChangePassword);
record SessionAccess(IReadOnlyList<string> Permissions, IReadOnlyList<ScopedPermissions> Scoped);
record ScopedPermissions(string SagaType, IReadOnlyList<string> Permissions);
record GrantDto(Guid RoleId, bool AllSagaTypes, IReadOnlyList<string>? SagaTypes);
record RoleRequest(string? Name, string? Description, IReadOnlyList<string>? Permissions);
record RoleResponse(Guid Id, string Name, string? Description, bool IsBuiltIn, IReadOnlyList<string> Permissions);
record CreateUserRequest(string? Username, string? DisplayName, string? Password, bool? MustChangePassword, IReadOnlyList<GrantDto>? Grants);
record UpdateUserRequest(string? DisplayName, bool? IsEnabled, IReadOnlyList<GrantDto>? Grants);
record ResetPasswordRequest(string? NewPassword, bool? MustChangePassword);
record UserResponse(Guid Id, string Username, string DisplayName, bool IsEnabled, bool MustChangePassword, DateTimeOffset? LockedUntilUtc,
    DateTimeOffset? LastSignInAtUtc, DateTimeOffset CreatedAtUtc, IReadOnlyList<GrantDto> Grants, IReadOnlyList<Guid> TeamIds);
record TeamRequest(string? Name, string? Description, IReadOnlyList<Guid>? MemberIds, IReadOnlyList<GrantDto>? Grants);
record TeamResponse(Guid Id, string Name, string? Description, IReadOnlyList<Guid> MemberIds, IReadOnlyList<GrantDto> Grants);
```
`access.permissions` lists permissions held for all saga types; `access.scoped` lists, per saga type, those held only there. For an API-key caller `user` is null.

Validation:
- Username: 3 to 64 of `[A-Za-z0-9._@+-]`, starting alphanumeric, unique ignoring case, immutable.
- Display name 1 to 128; role and team names 1 to 64, unique ignoring case; description up to 256.
- Role permissions: a non-empty subset of the catalogue. Built-in roles reject PUT and DELETE.
- Grants: at most 20 per subject, one per role, the role must exist. `allSagaTypes` means no `sagaTypes`; otherwise 1 to 100 distinct exact names of 1 to 200 characters (the `SagaType` column length).
- Team membership is written only through the team payload; `UserResponse.teamIds` is read-only.
- Administrator-created users and administrator resets default to `mustChangePassword = true`.

### 8. Enforcement on existing endpoints (`SagaEndpoints.cs`)
| Endpoint | Policy | Behaviour |
|---|---|---|
| list (`:27`, `:89-114`) | `sagas.view`, any scope | `ScopedSagaLister`, below |
| get (`:32-41`) | `sagas.view` | 403 before any read when the route type is out of scope; without `sagas.data`, `DataJson` is null and `GetDataJsonAsync` is skipped |
| timeline (`:43-45`) | `sagas.view` | without `sagas.data`, every entry `with { PayloadJson = null }`, which covers `StatePersisted` |
| map (`:47`) | `sagas.view` | unchanged; it carries no payloads |
| children (`:55-57`) | `sagas.view` on the parent type | result filtered to visible types |
| retry (`:59`) | `sagas.retry` | `SagaLogEntry.Create(…, sourceService: caller.AuditActor)` at `:160-161`; the map ignores it for this entry type (`SagaMapBuilder.cs:117-118`) |
| saga-types (`:72`), correlations (`:82`) | `sagas.view`, any scope | filtered to visible types |

`ScopedSagaLister.ListAsync(filter, scope)`:
1. Scope is All: `reader.ListAsync(filter)`, as today.
2. `sagaType` supplied: in scope, one call; out of scope, an empty page (200).
3. Otherwise `types` is the `GetSagaTypesAsync()` names inside the scope, distinct, ordinal ascending. None gives an empty page; one gives a single call with that type and no bound.
4. More than `MaxMergedTypes` (50), or `(long)page * pageSize > MaxMergeDepth` (10 000): 400 `{ error }` telling the caller to add `sagaType`.
5. Merge. `chunk = min(page * pageSize, MaxPageSize)`. Prime each type's stream with page 1 of `chunk`, sequentially (the reader is scoped, and an EF context cannot run queries in parallel). `TotalCount` is the sum of the streams' `TotalCount`. Repeatedly take the smallest head, refilling a drained stream with its next page; skip `(page-1)*pageSize`, take `pageSize`.
6. Head comparer per arm, mirroring `EfCoreSagaSummaryReader.cs:64-75`:
   - default, or `UpdatedAt` descending: `UpdatedAtUtc` descending;
   - `UpdatedAt` ascending: `UpdatedAtUtc` ascending;
   - `Status`: `(int)Status` in the requested direction, then `UpdatedAtUtc` descending.
   Ties across types break by saga type, ordinal ascending. Rows of one type never reorder, so each provider's own identity tie-break (ascending on EF Core and in-memory, walk direction on MongoDB and Redis) is preserved inside a type.
7. `RedisSearchScanLimitExceededException` from any stream still reaches the existing catch (`:107-113`).

Totals are sums of separate counts, not a snapshot, which is the looseness offset paging already has.

### 9. Hub
- Groups: `saga:list` for callers with unscoped view; `saga-list:{sagaType}` (new; the prefix cannot collide with `saga:{type}:{id}`); the instance group.
- `SubscribeToList()` returns `Task<bool>`. It resolves access afresh. All: join `saga:list`. Scoped: join one group per scoped type name, including types that have not run yet. None: return false. Joined names are kept in `Context.Items` for `UnsubscribeFromList`.
- `SubscribeToSaga` returns `Task<bool>` and joins only when `Has(sagas.view, sagaType)`. Neither method throws on denial, matching the malformed-id precedent (`SagaHub.cs:32-41`) and the SPA's fire-and-forget invokes.
- Pushes: `SagaHub.PushSagaUpdatedAsync(hub, summary)` sends to all three groups and is used by the notifier (`SignalRSagaChangeNotifier.cs:10-14`) and the poller (`SagaChangePollingService.cs:122-123`). `TimelineEntryAdded` sends `entry.PayloadJson is null ? entry : entry with { PayloadJson = null }`.
- Access changes: `HubConnectionRegistry` records each connection's subject in `OnConnectedAsync`. The observer aborts the affected users' connections, or all of them for a role change. The client reconnects, re-authenticates and resubscribes, so groups always reflect current access; a disabled user's negotiate gets 401.
- `CloseOnAuthenticationExpiration` closes a socket whose cookie ticket has expired; socket traffic does not slide the session.

### 10. API key and first administrator
- Handler (`ApiKeyAuthenticationHandler.cs:44-61`): once the key matches, resolve `Dashboard:ApiKeyRole` (default `Viewer`) against the built-ins in code, otherwise the store by name; an unknown role fails authentication. Claims `sub=api-key`, `name=api-key`; access is unscoped. `HandleChallengeAsync` and the cookie challenge both call `AuthProblems.WriteUnauthorizedAsync`, whose detail still names `X-Api-Key` and adds "sign in at /login".
- Seed: when no users exist and both `Dashboard:Admin:*` keys are set and pass the password policy, create an Administrator with `MustChangePassword = false`. It never touches an existing database. An invalid seed password logs an error and leaves setup available.
- Setup: `SetupWindow` captures `TimeProvider.GetUtcNow()` at start. `setupRequired` means no users; `setupAvailable` means required and inside the window. `POST /setup` re-checks both inside an exclusive scope, commits, then signs in.

### 11. Configuration
| Key | Default |
|---|---|
| `Dashboard:Identity:Provider` | `Sqlite` |
| `Dashboard:Identity:Sqlite:Path` | section 6 |
| `Dashboard:Admin:Username`, `Dashboard:Admin:Password` | unset |
| `Dashboard:Setup:WindowMinutes` | 15; 0 disables setup |
| `Dashboard:Session:IdleTimeoutMinutes` | 480 |
| `Dashboard:Session:CookieName` (new) | `vsaga.session` |
| `Dashboard:Password:MinLength` | 12 |
| `Dashboard:Lockout:MaxFailedAttempts`, `Dashboard:Lockout:Minutes` | 5 (0 disables), 15 |
| `Dashboard:ApiKey`, `Dashboard:ApiKeyRole` | empty, `Viewer` |
| `Dashboard:WebOrigin` | `http://localhost:4200` |
| `Dashboard:RateLimit:AuthPerMinute` (new) | 20 |
| `Dashboard:TrustedProxies` (new) | empty |

### 12. Compose and Dockerfile
`docker-compose.yml`, `dashboard-api` (`:35-40`), base file only:
```yaml
      Dashboard__Admin__Username: "admin"
      Dashboard__Admin__Password: "dev-local-only-change-me"
      Dashboard__Session__CookieName: "vsaga.session.${COMPOSE_PROJECT_NAME:-vsaga}"
    volumes:
      - vsaga-dashboard-identity:/data
```
plus the top-level volume. Volumes are per compose project, so each overlay gets its own users with no overlay edit. The project-suffixed cookie name stops side-by-side stacks on `localhost` overwriting each other's session.

Dockerfile, before line 15:
```
COPY src/VSaga.Dashboard.Identity/VSaga.Dashboard.Identity.csproj src/VSaga.Dashboard.Identity/
COPY src/VSaga.Dashboard.Identity.Sqlite/VSaga.Dashboard.Identity.Sqlite.csproj src/VSaga.Dashboard.Identity.Sqlite/
```
Runtime stage: `ENV Dashboard__Identity__Sqlite__Path=/data/identity.db`, `RUN mkdir -p /data && chown $APP_UID /data`, `USER $APP_UID`. A new named volume inherits that ownership.

### 13. ADR 0006 outline
Title: cookie sessions, a dashboard-owned SQLite identity store, permission-based access scoped by saga type.
- Context: reverses the shared-key decision in `docs/dashboard.md` (Authentication); retries have business effects.
- Options: keep the shared key; ASP.NET Core Identity with its EF stores; external OIDC only; bearer tokens held by the SPA; identity inside each `Persistence:Provider`; chosen, a small store behind `IDashboardIdentityStore`.
- Consequences: one API instance while on a file database; keys and password hashes in one file; a store read per request.
- Invalidated by: several API replicas, or a single-sign-on requirement.

## Files
Most critical (under `C:/Users/rafae/Documents/Projects/vSaga/dotnet/`): `src/VSaga.Dashboard.Api/Program.cs`, `src/VSaga.Dashboard.Api/Endpoints/SagaEndpoints.cs`, `src/VSaga.Dashboard.Api/Auth/ApiKeyAuthenticationHandler.cs`, `src/VSaga.Dashboard.Api/Hubs/SagaHub.cs`, `tests/VSaga.Dashboard.Api.Tests/DashboardApiFactory.cs`.

Create:
- `dotnet/src/VSaga.Dashboard.Identity/VSaga.Dashboard.Identity.csproj`
- `…/Model/IdentityModel.cs`, `Permissions.cs`, `BuiltInRoles.cs`
- `…/Stores/IDashboardIdentityStore.cs`, `IDashboardKeyRingStore.cs`, `IdentityExceptions.cs`
- `…/EFCore/DashboardIdentityDbContext.cs`, `Entities.cs`, `EfCoreDashboardIdentityStore.cs`, `UtcDateTimeConverter.cs`
- `…/Services/PasswordPolicy.cs`, `CredentialVerifier.cs`, `AccessEvaluator.cs`, `CallerAccessResolver.cs`, `AccessAdministrationService.cs`, `FirstAdministratorService.cs`, `IdentityStartup.cs`, `IdentityStoreXmlRepository.cs`
- `…/DashboardSecurityOptions.cs`, `ServiceCollectionExtensions.cs`
- `dotnet/src/VSaga.Dashboard.Identity.Sqlite/VSaga.Dashboard.Identity.Sqlite.csproj`, `Migrations/*` (generated)
- `dotnet/src/VSaga.Dashboard.Api/Auth/DashboardAuthExtensions.cs`, `DashboardCookieEvents.cs`, `PermissionAuthorization.cs`, `AuthProblems.cs`, `AntiforgeryEnforcement.cs`, `HubOriginGuard.cs`
- `dotnet/src/VSaga.Dashboard.Api/Endpoints/AuthEndpoints.cs`, `AdminEndpoints.cs`, `AccessDtos.cs`, `ScopedSagaLister.cs`
- `dotnet/src/VSaga.Dashboard.Api/HealthChecks/IdentityStoreHealthCheck.cs`, `Hubs/HubConnectionRegistry.cs`
- `dotnet/tests/VSaga.Dashboard.Identity.Tests/` (project and tests)
- `dotnet/tests/VSaga.Dashboard.Api.Tests/`: `DashboardTestClient.cs`, `RealCompositionFactory.cs` and the test classes below
- `docs/adr/0006-dashboard-authentication-and-identity-store.md`

Modify:
- `dotnet/VSaga.slnx`: three projects.
- `VSaga.Dashboard.Api.csproj`: project reference, Sqlite package.
- `Program.cs`, `Endpoints/SagaEndpoints.cs`, `Auth/ApiKeyAuthenticationHandler.cs`, `Hubs/SagaHub.cs`, `Hubs/SignalRSagaChangeNotifier.cs`, `SagaChangePollingService.cs`, `appsettings.json`, `Dockerfile`, `VSaga.Dashboard.Api.http`.
- `docker-compose.yml`.
- Tests: `DashboardApiFactory.cs`, `HealthEndpointTests.cs`, `SagaHubTests.cs`, `SignalRFakes.cs`, `SignalRSagaChangeNotifierTests.cs`, `SagaChangePollingServiceTests.cs`, the test csproj (`Microsoft.Extensions.TimeProvider.Testing`).

## Tests
`VSaga.Dashboard.Identity.Tests` (new; SQLite in memory with a keep-alive connection, as `SqliteProviderFixture.cs:19-43`):
- Abstract `DashboardIdentityStoreContractTests` with a SQLite subclass: round trips, case-insensitive uniqueness, grant replacement, delete cascades, role-in-use, parallel `RecordFailedSignInAsync` loses no increment, key-ring round trip.
- Migrations on a temp file: `MigrateAsync` builds the schema; `HasPendingModelChanges()` is false.
- Services: password policy; lockout threshold, expiry (`FakeTimeProvider`) and reset; rehash; evaluator (union, implied view, scoped `access.manage` ignored, team grants, disabled user); last-administrator invariant through every mutation path; built-in immutability; seed and setup window.

`VSaga.Dashboard.Api.Tests`, new:
- `AuthEndpointsTests`: session shapes; login sets the cookie; identical 401 bodies; unsafe request without a token is 400; a pre-login token is rejected after login and the login-issued one accepted; logout; password change ends the other session; lockout; 429.
- `SetupAndSeedingTests`: setup once, then 409; window expiry; seed; seed ignored when users exist; weak seed password.
- `AdminUsersEndpointsTests`, `AdminTeamsEndpointsTests`, `AdminRolesEndpointsTests`: each verb, validation, conflicts, 403 for non-administrators and for a scoped `access.manage`, 401 anonymous.
- `SagaAccessEnforcementTests`: Viewer cannot retry; out-of-scope route type is 403 on every per-instance route; saga-types, correlations and children filtered; redaction; default-Viewer API key; unknown `ApiKeyRole` is 401; attribution for a user and for the key.
- `ScopedSagaListerTests`: every arm against a full-sort oracle across pages; tie-break; `TotalCount`; single-type delegation; both bounds; the Redis exception; out-of-scope filter.
- `SagaHubAccessTests`: group choice per scope; denied subscribe returns false; abort on access change; payload stripped.
- `EndpointProtectionTests`: every mapped endpoint has explicit authorization metadata, and the anonymous set is exactly health, session, login, logout and setup.
- `IdentityUnavailableTests`: an unusable path still starts; `/health` 200 degraded; session 503; the API key still lists; an unknown `Dashboard:Identity:Provider` throws at start-up.

Existing tests that change:
- `DashboardApiFactory.cs:32-35`: add `Dashboard:ApiKeyRole=Operator` (the retry tests at `SagaEndpointsTests.cs:345-468` authenticate with the key) and the seed keys; the identity path; `PasswordHasherOptions.IterationCount = 1000`; dispose cleanup; `CreateSignedInClientAsync`.
- `HealthEndpointTests.cs:17-21`, `:50-54`, `:86-90`: fixture type.
- `SagaHubTests.cs:15-25`: build the hub with a stub resolver (full access) and a registry.
- `SignalRFakes.cs:90-96`: `TestHubCallerContext` accepts a principal.
- `SignalRSagaChangeNotifierTests.cs:32` (two groups become three) and `:49-54` (exclude `saga-list:` groups).
- `SagaChangePollingServiceTests.cs:99` and `:185-190`: the same two changes.
- Unchanged and still green: all of `ApiKeyAuthTests`, `SagaEndpointsTests.cs:484-491` and `:638-663`, `SearchScanLimitTests`.

## Commit sequence
1. Identity project: model, store contract, EF store, store tests; solution entries.
2. Identity services and their tests.
3. Migrations project with `InitialCreate`; identity registration, start-up initialisation and health check in the API; Dockerfile COPY lines; test factories isolate the database.
4. Cookie sessions: schemes, fallback policy, antiforgery, rate limiter, forwarded headers, session, login, logout and password endpoints; API-key role mapping.
5. First administrator: seeding, setup endpoint and window.
6. Permission policies, redaction, filtered lists, scoped merge and retry attribution on the saga endpoints.
7. Administration endpoints.
8. Hub groups, checks, origin guard, registry and payload stripping.
9. Compose environment and volume; image path and user.
10. ADR 0006.

## Verification
```
dotnet build dotnet/VSaga.slnx -c Release                      # zero warnings
dotnet test dotnet/tests/VSaga.Dashboard.Identity.Tests -c Release
dotnet test dotnet/tests/VSaga.Dashboard.Api.Tests -c Release  # neither needs Docker
dotnet ef migrations has-pending-model-changes --context DashboardIdentityDbContext --project dotnet/src/VSaga.Dashboard.Identity.Sqlite --startup-project dotnet/src/VSaga.Dashboard.Api
docker compose up -d --build
curl -s localhost:5080/health                                  # identity: healthy
curl -i localhost:5080/api/sagas                               # 401 problem+json naming X-Api-Key
curl -s -H "X-Api-Key: dev-local-only-change-me" localhost:5080/api/sagas   # 200
curl -s -c jar localhost:5080/api/auth/session                 # setupRequired false; XSRF-TOKEN set
curl -s -b jar -c jar -H "X-XSRF-TOKEN: <cookie value>" -H "Content-Type: application/json" -d '{"username":"admin","password":"dev-local-only-change-me"}' localhost:5080/api/auth/login
docker compose up -d --force-recreate dashboard-api
curl -s -b jar localhost:5080/api/auth/session                 # still authenticated
```
Also: a Viewer-key retry is 403; an `admin` retry writes `sourceService = dashboard:admin`; a scoped user sees and receives pushes only for their types; disabling that user gives 401 and drops the socket.

## Cross-assignment contracts
- nginx: `proxy_set_header Host $http_host`, `X-Forwarded-For`, `X-Forwarded-Proto`, WebSocket upgrade on `/hubs/`. The hub origin guard requires the preserved `Host`.
- Compose: the section 12 block in the base file only; overlays need nothing beyond their ports.
- SPA:
  - Cookies only, relative URLs, Angular's default `XSRF-TOKEN` and `X-XSRF-TOKEN`.
  - Call `GET /api/auth/session` before the first unsafe request. Login, logout, setup and password responses are a fresh `SessionResponse`.
  - On 400 `antiforgery`, refetch the session and retry once.
  - Problem `code` values: `unauthenticated`, `forbidden`, `password_change_required`, `antiforgery`, `invalid_credentials`, `validation`, `setup_unavailable`, `username_taken`, `name_taken`, `role_in_use`, `role_immutable`, `last_administrator`, `rate_limited`, `identity_unavailable`.
  - `can(permission, sagaType)`: in `access.permissions`, or in that type's `access.scoped` entry.
  - Without `sagas.data`, `dataJson` and `payloadJson` are null.
  - Scoped deep paging returns 400 `{ error }`, the shape of today's Redis 400.
  - Hub subscribe methods return a boolean; the server may abort the connection; stop reconnecting on 401.
- Per-step data: that assignment owns the `StatePersisted` append in `ResetAndRedriveAsync`; this one owns policies, redaction and the `sourceService` argument in the same file.
- Docs: keep the `#authentication` anchor (`ApiKeyAuthenticationHandler.cs:86`); the key is Viewer by default, so a curl retry needs `Dashboard__ApiKeyRole=Operator`; ADR number 0006 is taken here.
- Additions to the sketch: three configuration keys (section 11), `POST /api/admin/users/{id}/password` and `/unlock`, boolean hub results.

## Objections
None. Decision D is workable as fixed; the additions above are refinements.

## Risks
- Antiforgery tokens are bound to the signed-in identity and cached per request, so a token issued before login is invalid afterwards. Mitigation: issue the XSRF-TOKEN cookie only after setting HttpContext.User, in session, login, logout, setup and password; the SPA refetches the session and retries once on code 'antiforgery'; dedicated tests cover the pre-login and post-login tokens.
- EF Core's SQLite migration lock table (__EFMigrationsLock) can be left behind by a killed process and make MigrateAsync wait indefinitely. Mitigation: a 30-second timeout around identity initialisation, non-fatal start-up, the identity health check reports degraded with a message naming the table, and the manual fix is documented.
- An unusable identity database (bad path, read-only volume, corrupt file) means nobody can sign in. Mitigation: start-up never throws, /health stays 200 with identity degraded so order-processing's service_healthy gate still opens, auth endpoints return 503 identity_unavailable for the SPA to display, initialisation retries every 10 seconds, and an API key mapped to a built-in role keeps working.
- Browser cookies are not scoped by port, so overlays run side by side on localhost would overwrite each other's session. Mitigation: the session cookie name is suffixed with COMPOSE_PROJECT_NAME (with a default). Angular's fixed XSRF-TOKEN cookie can still collide between stacks; the refetch-and-retry-once rule heals it.
- Per-account lockout can be abused to keep the only administrator locked out, and a forgotten administrator password has no in-product recovery. Mitigation: lockout is temporary (15 minutes), login is rate limited per IP, administrators can unlock others, and recovery by resetting the identity volume is documented (see open questions).
- Behind nginx every browser shares the proxy's IP unless Dashboard:TrustedProxies is set, so the login rate limiter becomes one shared bucket; with '*' and the API port also published, X-Forwarded-For can be spoofed. Mitigation: per-account lockout is the real brute-force control; the trade-off is documented and compose leaves the key unset.
- The scoped list merge can issue up to 50 sequential per-type queries, its TotalCount is a sum of separate counts, and its cross-type tie order differs from the unscoped order. Mitigation: bounds of 50 types and 10 000 rows with a clear 400, a single-type fast path with no bound, and oracle tests for every sort arm.
- Build gates may reject idiomatic security code: Sonar hotspot rules on hard-coded credentials and cookie flags (S2068, S2092, S3330), Meziantou method length and string-comparison rules, NU1510 if Microsoft.Extensions packages are referenced directly beside the FrameworkReference, and the obsolete ForwardedHeadersOptions.KnownNetworks. Mitigation: neutral constant names or justified pragmas in the style of ApiKeyAuthenticationHandler.cs:26, split methods, no direct Microsoft.Extensions references in the identity project, and KnownIPNetworks.
- Every API test factory now creates a SQLite file, runs migrations and hashes a password, and Windows keeps pooled connections' file handles open. Mitigation: PasswordHasherOptions.IterationCount lowered in tests, a unique temp file per factory, SqliteConnection.ClearAllPools before a best-effort delete.
- Each request and each hub subscribe call reads the identity store (user, teams, roles). Mitigation: a local SQLite read is sub-millisecond and keeps revocation immediate; add a short cache keyed by security stamp only if measurement shows a need.
- Aborting hub connections on access changes makes affected clients reconnect, and the current client retries a failed start forever (startWithRetry in dashboard-web/src/app/services/saga-hub.service.ts), so a disabled user's tab would loop on 401. Mitigation: the SPA contract requires stopping the hub on 401; the server side is covered by SagaHubAccessTests.
- Data Protection keys are stored unencrypted in the same SQLite file as the password hashes. Mitigation: the container runs as a non-root user with the file on its own volume, the limitation is recorded in the ADR, and an XML encryptor can be added later without a schema change.
- A model change without a new migration makes MigrateAsync throw the pending-model-changes error, which the non-fatal start-up would turn into a silently degraded identity store. Mitigation: a test asserting HasPendingModelChanges is false, and the has-pending-model-changes command in verification.
- The API key maps to Viewer by default, so existing scripts or README examples that retry with the key start receiving 403. Mitigation: Dashboard:ApiKeyRole is documented, the test factory sets Operator explicitly, and a test pins the default.
- Retry attribution reuses SagaLogEntry.SourceService, whose documented meaning is the sending service. Mitigation: SagaMapBuilder ignores it for ManualRetryRequested (SagaMapBuilder.cs:117-118), usernames are capped at 64 characters so the 200-character column (VSagaDbContext.cs:35) holds, and the convention is recorded in the ADR.
- Switching the API image to a non-root user changes file-ownership expectations for the identity volume. Mitigation: /data is created and chowned in the image so a new named volume inherits the ownership; verified live with a fresh volume; the step is separable from the rest of the commit.
- Several framework behaviours are taken from documentation rather than verified in this repository: the scope of the built-in antiforgery middleware, BEGIN IMMEDIATE as the default SQLite transaction, antiforgery binding to the sub claim, COMPOSE_PROJECT_NAME interpolation, and TimeProvider registration by AddAuthentication. Mitigation: each is covered by a test or a verification step, TimeProvider is registered explicitly, and the compose variable has a default.

## Open questions
- Should the demo compose stack ship a seeded administrator (admin with a committed dev-only password, in the same spirit as today's committed API key), or start with the first-run setup screen so no credential is committed? The blueprint assumes the seeded administrator; the setup screen would have to be completed within 15 minutes of the API starting.
- If the only administrator's password is lost, the blueprint's recovery is to delete the identity database or volume, which also removes every user, team and custom role. Is that acceptable for now, or do you want a configuration-driven break-glass reset of an administrator password?
