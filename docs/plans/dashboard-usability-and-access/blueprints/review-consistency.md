# Review: consistency

VERDICT
The six blueprints fit together architecturally and between them deliver all six requests. They cannot be implemented exactly as written, because several contracts are stated twice with different values. I read the code each claim rests on; nothing was built or run.

- One blocker: the admin wire contract. The SPA's disable toggle would be a silent no-op while the user list shows the account as disabled.
- Eight majors, each needing one owner or one decision: identity path and API image; forwarded headers, CORS and the WebOrigin default; per-stack cookie names; CI and /health blind to a broken sign-in; retry escaping saga-type scope; three data-tour vocabularies; overlapping ownership of ADRs, history and docs; untested origin guard.
- Ten minors: naming, copy and sequencing refinements.
- What already agrees: the StatePersisted entry contract, the oversize marker and the snapshot join rule (engine-snapshots and detail-ux); UI and API ports; permission keys; routes; the session DTO; the antiforgery header convention. No fixed decision is unworkable.

SHARED FILES AND THE ORDER THAT RESOLVES THEM
- Program.cs: packaging 2 and 5, then engine 5 and 8, then auth-backend 3 to 8. Auth-backend calls UseDashboardEdge and drops its own forwarded-header and CORS lines.
- SagaEndpoints.cs: engine 5 (named timeline handler and redaction seam), engine 8 (reset recorder), then auth-backend 6 (policies, the boolean for the seam, attribution).
- SignalRSagaChangeNotifier.cs: engine 5 strips payloads; auth-backend 8 only adds the per-type group.
- docker-compose.yml and the API Dockerfile: packaging 4, then one identity commit (packaging 6, replacing auth-backend 9). Auth-backend 3 adds only the csproj COPY lines.
- saga-detail.*: detail-ux 3 to 7, then auth-frontend 6 (which is detail-ux 8), then the guide's anchors.
- app.html, app.scss, app.spec.ts: auth-frontend 5, then guidance 5.
- styles.scss: detail-ux 3, then auth-frontend 1.
- api-config.ts and saga-hub.service.ts: packaging 1, auth-frontend 2, auth-frontend 5.
- docs: guidance 1 first, then one documentation commit per slice.

GLOBAL COMMIT SEQUENCE (54 commits)
Every commit keeps dotnet build, dotnet test, ng build and ng test green. LIVE marks commits that need verification against docker compose before the next slice starts. Brackets name the source commit: P packaging, E engine-snapshots, D detail-ux, B auth-backend, F auth-frontend, G guidance-docs.

Records
1. [G1] Add the design document and ADRs 0006 and 0007 (written once, here).

Packaging (improvement 5)
2. [P1] Make the SPA same-origin: relative URLs, dev proxy, dev server on 4201; correct the README port lines it invalidates.
3. [P2] Make the dashboard's CORS policy opt-in.
4. [P3] Add the dashboard-web image and the index.html guard in the Angular CI job.
5. [P4] Run the dashboard UI from compose: base service plus six overlay ports. LIVE (base, one transport overlay, mongo)
6. [P5] Honour forwarded headers from trusted proxies. LIVE
7. [P7] Build the images and smoke-test the UI origin in CI. LIVE
8. [P8 first half, G2] Document the one-command demo.
9. [G9] Record the packaging slice (new history file).

Timeline, map jump, per-step data (improvements 2, 3, 4)
10. [D1] Pure helpers.
11. [D2] Model, fold and fixtures; exclude src/app/testing from the app build.
12. [D3] Labelled times and steps. LIVE (browser)
13. [D4] Jump from a timeline entry to the map. LIVE (browser)
14. [E1] SagaMapBuilder unit tests.
15. [E2] Detach a failed event-log append from EF's change tracker.
16. [E3] StatePersisted and SagaStateSnapshot, with conformance cases.
17. [E4] Skip StatePersisted in the map.
18. [E5] Timeline redaction seam; payload-free SignalR pushes.
19. [E6] Stamp the stored sequence number on pushed entries.
20. [E7] Record a snapshot after every committed transition. LIVE (base, chaos, mongo, redis; mutation checks)
21. [E8] Record the state a dashboard retry reset leaves. LIVE
22. [D5] Data inspector and per-step toggle.
23. [D6] Data overview; remove the Data tab.
24. [D7] Coalesced live refresh and load errors. LIVE (browser)
25. [E9, G3] Document snapshots, the labelled timeline and the map jump.
26. [E10, G9] Record the slice, with measured Redis and MongoDB storage figures.

Authentication and access (improvement 6)
27. [B1] Identity project: model, store contract, EF store.
28. [B2] Identity services.
29. [B3] Migrations project, registration, start-up, health check, Dockerfile COPY lines, test factories.
30. [B4 first half] Policy scheme, fallback policy, shared 401 and 403 bodies, API-key role mapping.
31. [B4 second half] Sign-in: session, login, logout, password; antiforgery; rate limit and lockout.
32. [B5] First administrator: seed and setup.
33. [P6, replaces B9] Identity volume, non-root API image, seeded demo administrator, per-project cookie name; ApiKeyRole Operator for now. LIVE
34. [B6] Permission checks, redaction, filtered lists, scoped merge, retry attribution.
35. [B7] Administration endpoints.
36. [B8] Hub access checks, per-type list groups, origin guard, abort on access change. LIVE (curl flows)
37. [new] Smoke-test sign-in, the origin guard and identity health in CI. LIVE
38. [F1] Promote the remaining shared styles.
39. [F2] Hub stopAndReset, resume and session probe.
40. [F3] Session models, problemOf, AuthService, auth mock.
41. [F4] Login, setup and account pages as lazy routes.
42. [F5] Require a session; remove the key from the SPA; ApiKeyRole back to Viewer; README sign-in rows. LIVE
43. [F6, D8] Gate retry and data by permission; 403 states. LIVE
44. [F7] Administration shell and roles.
45. [F8] Users, grants editor, effective access.
46. [F9] Teams. LIVE (two stacks side by side in one browser profile; down and up; down -v)
47. [G4, B10] Document sign-in, access control and the identity store.
48. [G9] Record the slice.

Guidance (improvement 1)
49. [G5] Guide mode: toggle, list tour, permission-check provider.
50. [G6] Detail tour.
51. [G7] Administration tour. LIVE (in the container: keyboard only, reduced motion, CSP console)
52. [G8] User guide, linked from the app, the README and the docs index.
53. [G9] Record the slice.
54. [G10] Mark the design and both ADRs implemented.

Conditions that keep the sequence green
- 11 and 12 land before 20, so the SPA never lists StatePersisted rows; 17 and 18 land before 20.
- 29 carries the csproj COPY lines, or the compose job added in 7 fails on the image build.
- 30 registers the cookie scheme with its problem-writing events, so the existing credential-less 401 tests keep passing.
- 33 lands the volume, the chown and USER together.
- 42 updates saga-hub.service.spec.ts:122-127 and app.spec.ts in the same commit.

### Critical Files for Implementation
- C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Program.cs
- C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Endpoints/SagaEndpoints.cs
- C:/Users/rafae/Documents/Projects/vSaga/docker-compose.yml
- C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Dockerfile
- C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-detail/saga-detail.html

## 1. [blocker] (auth-backend, auth-frontend)
The admin wire contract differs between the API and the SPA, and one difference is unsafe. The API serialises isEnabled, isBuiltIn and lastSignInAtUtc; the SPA models read and write enabled, builtIn and lastLoginAtUtc. The SPA also sends teamIds (and enabled on create) in user payloads, which the API's request records do not have, because membership is written only through the team payload. System.Text.Json ignores unknown properties, so PUT /api/admin/users/{id} with enabled:false returns success and changes nothing, while the list, reading an undefined enabled, would show every user as disabled: an administrator believes an account is off when it is not. Built-in roles would render as editable and the user page's team checkboxes would save nothing. Each side's unit tests pass, because each tests against its own model. Smaller gaps in the same contract: sagaTypes and description are nullable on the API and non-null in the SPA; the effective-access preview ignores the catalogue's implies and scopable fields (the server adds sagas.view to sagas.data and sagas.retry); the grants editor allows two grants with one role, which the API rejects.

**Evidence:** auth-backend section 7: RoleResponse(... bool IsBuiltIn ...), UserResponse(... bool IsEnabled ... DateTimeOffset? LastSignInAtUtc ...), UpdateUserRequest(string? DisplayName, bool? IsEnabled, IReadOnlyList<GrantDto>? Grants), GrantDto(... IReadOnlyList<string>? SagaTypes), and 'Team membership is written only through the team payload; UserResponse.teamIds is read-only'. auth-frontend section 3: Role.builtIn, AdminUser.enabled, AdminUser.lastLoginAtUtc, CreateUser and UpdateUser picking enabled and teamIds; its contract line 'team membership writable through both teamIds and memberIds'; section 11 effective access ('A scoped grant adds every permission except access.manage').

**Fix:** Freeze one contract before either side is built. Take the API records as canonical (isEnabled, isBuiltIn, lastSignInAtUtc; arrays always present, never null) and decide membership once: either the user page shows teams read-only, or TeamIds is added to the user requests and applied inside the same exclusive scope. Mark the admin request records with JsonUnmappedMemberHandling.Disallow so drift becomes a 400 instead of a silent no-op. Check golden JSON samples into the repo and assert them from both the .NET admin endpoint tests and admin-api.service.spec.ts. The preview applies implies and scopable from GET /api/admin/permissions.

## 2. [major] (packaging, auth-backend)
The identity database location and the API image's runtime stage are specified twice with different values, and both blueprints schedule the same change as their own commit (packaging 6, auth-backend 9). Packaging mounts the volume at /var/lib/vsaga-dashboard and sets the path in compose; auth-backend mounts it at /data, sets the path as ENV in the image and chowns /data. A mixed merge (compose from one, Dockerfile from the other) mounts the volume on a directory the image never created; Docker creates a missing mount point as root, and the process (uid 1654) cannot open the database. Auth-backend's fallback default, {AppContext.BaseDirectory}/data/identity.db, also breaks packaging's rule that an unset path must resolve outside /app and the source tree.

**Evidence:** packaging section 3 (Dashboard__Identity__Sqlite__Path: '/var/lib/vsaga-dashboard/identity.db'; volume vsaga-dashboard-identity:/var/lib/vsaga-dashboard) and section 6. auth-backend section 12 (vsaga-dashboard-identity:/data; ENV Dashboard__Identity__Sqlite__Path=/data/identity.db; RUN mkdir -p /data && chown $APP_UID /data) and section 6 ('or {AppContext.BaseDirectory}/data/identity.db when that folder is unavailable'). Current runtime stage, with no USER and no volume: C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Dockerfile:21-29.

**Fix:** One owner and one commit. Packaging owns the runtime-stage lines and the compose block, as its contract already says; auth-backend keeps only its two csproj COPY lines and drops its commit 9. Use one path, /var/lib/vsaga-dashboard/identity.db, set both as ENV in the image (so a bare docker run uses the chowned directory) and in compose. Replace the BaseDirectory fallback with reporting identity as degraded. Land volume, chown and USER together, after auth-backend 5.

## 3. [major] (packaging, auth-backend)
Forwarded headers and CORS are implemented twice with different rules. Packaging adds DashboardEdge (a list of addresses or CIDR networks, a malformed entry throws, ForwardLimit 1, middleware only when a proxy is trusted, CORS only when an origin is set, empty default) and sets Dashboard__TrustedProxies to the private ranges in compose. Auth-backend configures ForwardedHeadersOptions again ('*' clears both lists), calls UseForwardedHeaders and UseCors directly, keeps Dashboard:WebOrigin defaulting to http://localhost:4200 and says compose leaves the proxy key unset. If auth-backend's version wins: in a deployment that terminates TLS in front of nginx the scheme stays http, so the session cookie is not Secure and HubOriginGuard (Origin must equal scheme://Host) rejects every hub handshake; and with the 4200 default every overlay's API accepts the base stack's UI origin for credentialed CORS and for hub handshakes.

**Evidence:** packaging section 5 and its contract 'Pipeline. app.UseDashboardEdge() stays first'. auth-backend section 5, services step 9 and pipeline steps 2-3; section 11 row 'Dashboard:WebOrigin | http://localhost:4200'; its risk 'compose leaves the key unset'. Current code: C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Program.cs:31-34 and :147.

**Fix:** DashboardEdge is the only place that reads these two keys. Auth-backend removes its forwarded-headers step, calls app.UseDashboardEdge() first and gives HubOriginGuard the DashboardEdgeSettings.WebOrigin value. Keep packaging's grammar (no '*'), the empty WebOrigin default and the compose value. Order: packaging 2 and 5 land before auth-backend 4.

## 4. [major] (packaging, auth-backend, auth-frontend, guidance-docs)
Three different answers to the shared localhost cookie jar. Packaging proposes suffixing every cookie with the request's port on both sides. Auth-backend adds Dashboard:Session:CookieName, set per compose project, and lets XSRF-TOKEN collide and heal through the retry. Auth-frontend uses Angular's defaults and leaves the question open. Guidance-docs writes 'one stack per hostname or browser profile' unless names become per stack. The README already promises that overlays run alongside the plain stack. Packaging's port rule cannot be applied to the two framework cookies: cookie-authentication and antiforgery cookie names are fixed when options are built, as packaging itself notes.

**Evidence:** packaging contract 'Cookie names must be per stack'. auth-backend section 11 (Dashboard:Session:CookieName), section 12 (vsaga.session.${COMPOSE_PROJECT_NAME:-vsaga}) and its risk listing COMPOSE_PROJECT_NAME interpolation as unverified. auth-frontend section 1 ('No withXsrfConfiguration is needed') and its open question. guidance-docs contract 6. C:/Users/rafae/Documents/Projects/vSaga/README.md:112-113 and :124-126.

**Fix:** Adopt auth-backend's key. The compose block that packaging owns must carry Dashboard__Session__CookieName; it is missing there. Keep XSRF-TOKEN shared: a token issued by another stack fails validation, the API answers 400 with code antiforgery, and the interceptor refetches the session and retries once, so that code and that retry become load-bearing and need a test on each side. During live verification, render the wolverine overlay with docker compose -p vsaga-wolverine ... config to confirm the interpolated name, then sign in to 4200 and 4300 in one browser profile. Drop the port-suffix rule and the 127.0.0.1 advice; docs/transports/index.md gets one sentence.

## 5. [major] (packaging, auth-backend)
Sign-in can be broken while compose reports healthy and CI is green. Auth-backend keeps /health at 200 when the identity store is unusable, so order-processing still starts, and packaging's smoke test uses only the API key, which keeps working with a built-in role when the identity store is down. A wrong volume owner, a missed migration, or a broken cookie or antiforgery path through nginx therefore passes every automated check. The same smoke test sends no Origin header, so the hub origin guard and the Host pass-through it depends on are never exercised.

**Evidence:** auth-backend section 6 ('/health stays 200 with identity degraded'; 'an API key with a built-in role still works'). packaging section 7 step 5 (every call carries X-Api-Key) and section 3 ('The healthcheck reports on nginx alone'). C:/Users/rafae/Documents/Projects/vSaga/docker-compose.yml:46-51 and :74-75.

**Fix:** Extend the compose job once sign-in exists, as its own commit right after the hub commit. Assert that /health lists identity as healthy. Through port 4200: fetch /api/auth/session into a cookie jar, POST /api/auth/login with the X-XSRF-TOKEN header and the seeded credentials, GET /api/sagas with the cookie, negotiate the hub with the cookie and Origin http://localhost:4200, and expect 403 for another Origin.

## 6. [major] (auth-backend, auth-frontend, guidance-docs)
Saga-type scoping does not contain a retry. The retry endpoint republishes the recorded message under the same correlation id with a fresh message id, and every saga type and participant subscribed to that message type receives it. A user whose sagas.retry grant is scoped to one saga type can therefore drive steps in saga types outside the grant (in the sample, OrderSaga and PostShipmentChoreography share a correlation id and both consume OrderShipped). No blueprint states this limit or checks for it, and the tour and the user guide describe scope as a boundary.

**Evidence:** C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Endpoints/SagaEndpoints.cs:192-205 (the comment at 198-200). C:/Users/rafae/Documents/Projects/vSaga/docs/dashboard.md:42-47. auth-backend section 8, row 'retry | sagas.retry'. guidance-docs admin tour step 5 ('A grant pairs a role with a scope').

**Fix:** Decide the rule and record it in ADR 0006. Minimum: before publishing, read FindByCorrelationIdAsync(correlationId) and require sagas.retry for every saga type that tracks the id, answering 403 naming the missing type, with a test. Whatever cannot be enforced (types that would start from the message, participants) is stated in docs/dashboard.md under grants and scope, in the user guide's retry section and in the retry confirmation text.

## 7. [major] (detail-ux, auth-frontend, guidance-docs)
Three vocabularies for data-tour anchors. Guidance-docs says it adds every attribute itself, and its tours and GUIDE_ANCHORS use its own ids; detail-ux and auth-frontend each put differently named attributes in their templates. An element holds one data-tour value, so the guide commits would have to rename attributes that other commits had just added, and anything left under an old name makes a tour step fall back to a centred popover without failing a test.

**Evidence:** detail-ux contract 'Tour anchors added here: detail-summary, detail-data, detail-tabs, detail-timeline, detail-step-data (first step only), detail-map, map-focus, detail-retry' and its template (ol class tl with data-tour detail-timeline). auth-frontend contract 'Anchors: nav-admin, user-menu, admin-tabs, admin-users, admin-grants, admin-effective-access, admin-teams, admin-roles' and its section 8 template. guidance-docs contract 3 (timeline, timeline-entry, timeline-step-data, detail-tab-map, detail-tab-timeline, map-canvas, map-controls, admin-nav, admin-nav-users, admin-nav-teams, admin-nav-roles, admin-list) and contract 9 ('Mine: ... every data-tour attribute').

**Fix:** Guidance-docs' list is the only vocabulary. Detail-ux and auth-frontend add no data-tour attributes; the guide commits add them together with the anchor-contract specs. Mapping for elements the other two named: detail-timeline becomes timeline; detail-step-data becomes timeline-step-data on every step's toggle; detail-tabs is replaced by detail-tab-map and detail-tab-timeline on the two buttons; detail-map becomes map-canvas; admin-tabs becomes admin-nav; admin-users, admin-teams and admin-roles become admin-nav-users, admin-nav-teams and admin-nav-roles on the links, plus admin-list on each table; map-focus, nav-admin, user-menu, admin-grants and admin-effective-access are dropped unless a tour step is written for them.

## 8. [major] (engine-snapshots, auth-backend, packaging, guidance-docs)
The records and reference documents have overlapping owners and two filename mismatches. ADR 0006 is created by auth-backend as 0006-dashboard-authentication-and-identity-store.md and by guidance-docs as 0006-dashboard-sign-in-access-and-identity-store.md. ADR 0007 is created by both engine-snapshots (commit 9) and guidance-docs (commit 1). The snapshot history file is docs/history/state-snapshots.md in one blueprint and timeline-labels-map-jump-and-state-snapshots.md in the other. Documentation commits are duplicated: packaging 8 with guidance 2; engine 9 with guidance 3; auth-backend 10 with guidance 1 and 4. docs/dashboard.md gets the serving text under 'The SPA' from packaging and under a new 'Running it' from guidance-docs, and the capacity figures in docs/persistence.md are re-measured by two owners.

**Evidence:** auth-backend Files ('docs/adr/0006-dashboard-authentication-and-identity-store.md') and commit 10. guidance-docs section 8, commits 1-4 and 9, contract 9. engine-snapshots Files and commits 9-10. packaging sections 8-9 and commit 8. Existing records: C:/Users/rafae/Documents/Projects/vSaga/docs/adr (0001 to 0005). Unedited-history rule: C:/Users/rafae/Documents/Projects/vSaga/docs/README.md:85-92.

**Fix:** Guidance-docs owns every file under docs/ and the commit that touches it; the other blueprints' documentation sections are its source text. Use one ADR 0006 filename (I suggest auth-backend's, which matches the lead's wording and the #authentication anchor), write both ADRs once in the first commit, and use the four guidance history names (drop state-snapshots.md). Packaging 8 splits into the one-command documentation commit after the packaging slice and the sign-in rows, which land with the SPA switch-over. The serving text goes under 'The SPA', whose heading must survive; the measured numbers come from the engine slice's live run.

## 9. [major] (auth-backend)
Test gaps on the new security controls. HubOriginGuard is the only thing that stops another localhost origin from opening a hub socket with the session cookie (WebSockets bypass CORS, and SameSite treats every localhost port as one site), and no test is listed for it. The 401 and 403 bodies must keep 'See docs/dashboard.md#authentication.' (guidance contract 7), which is also unpinned. EndpointProtectionTests expects the anonymous set to be exactly health, session, login, logout and setup, but the test host runs in Development, where the OpenAPI document is mapped and anonymous. No mutation checks are listed for the authentication or packaging slices, which this repository treats as part of verification.

**Evidence:** auth-backend Tests ('SagaHubAccessTests: group choice per scope; denied subscribe returns false; abort on access change; payload stripped'; 'EndpointProtectionTests ... the anonymous set is exactly health, session, login, logout and setup') and section 5 pipeline step 3. C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Program.cs:144-145. C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Auth/ApiKeyAuthenticationHandler.cs:82-86. C:/Users/rafae/Documents/Projects/vSaga/CONTRIBUTING.md:74-78.

**Fix:** Add HubOriginGuardTests (no Origin, same origin, another localhost port, the configured WebOrigin, https through a trusted forwarded header), a test pinning the documentation pointer in both problem bodies, and account for the OpenAPI route in the protection test. List mutation checks for the slice: remove the retry policy, the origin guard, the security-stamp comparison and the payload redaction in turn, and confirm each fails only its own tests.

## 10. [minor] (auth-backend, auth-frontend)
Smaller mismatches in the authentication contract that end in generic messages, not failures.
- Session: the API answers 503 identity_unavailable; the SPA assumes the endpoint always answers 200, so the login page says the API cannot be reached.
- Setup code: setup_unavailable on the API, setup_closed in the SPA.
- Wrong current password: the API sends code invalid_credentials; the SPA expects errors.currentPassword.
- passwordMinLength is in the SPA's SessionInfo and absent from SessionResponse, so the forms have no client-side minimum.
- Validation: the API does not specify the keys of errors; the SPA expects request-property paths such as grants[0].sagaTypes.
- The list page maps every failure to 'Could not reach the vSaga Dashboard API', including the new 400 for scoped deep paging.

**Evidence:** auth-backend section 7 table, section 8 step 4 and the code list in its contracts. auth-frontend contract D, sections 10 and 12. C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-list/saga-list.ts:242-245.

**Fix:** The API's code list is canonical (setup_unavailable). Add passwordMinLength to SessionResponse. A wrong current password returns code invalid_credentials plus errors.currentPassword. Specify errors keys as camelCase request paths. The SPA shows a 503 on the session as 'sign-in is unavailable' and shows the server's error text for a 400 on the list.

## 11. [minor] (auth-backend, packaging, engine-snapshots, guidance-docs)
Dashboard settings are read in two styles. Packaging (DashboardEdgeSettings) and engine-snapshots (DashboardStateSnapshotOptions) read configuration once into plain singletons. Auth-backend binds DashboardSecurityOptions with AddOptions().BindConfiguration('Dashboard') and ValidateOnStart. The configuration reference states that nothing in the repository uses options binding apart from one framework-required exception, and guidance contract 7 asks for IConfiguration or a plain singleton.

**Evidence:** C:/Users/rafae/Documents/Projects/vSaga/docs/configuration.md:3-8 and :366-372. auth-backend section 5, services step 3. guidance-docs contract 7.

**Fix:** Read the security settings once, validate and throw at composition as the provider switches do, register a plain singleton, and set them in tests with UseSetting. Framework-owned options (cookie, antiforgery, forwarded headers, key management) stay as they are; the documentation sentence names them as the exception.

## 12. [minor] (detail-ux, auth-frontend, guidance-docs)
Shared shell and style hooks are named twice.
- Top-bar group: .topbar-end (auth-frontend) and .topbar-actions (guidance-docs).
- Quiet button: .btn-quiet (detail-ux) and .btn--quiet (auth-frontend); .muted is added by both.
- Banners: auth-frontend promotes .banner with modifiers taken from saga-list.scss, where the padding sits on the base class. saga-detail.html uses banner--warning alone, and detail-ux's new error banners follow that; once the local copies in saga-detail.scss go, those lose their padding.
- Pure helpers live in util/ (detail-ux) and shared/ (auth-frontend).
- The data gate is a canViewData input (detail-ux) and a canSeeData() method (auth-frontend).
- GuidePermission duplicates PermissionKey.

**Evidence:** auth-frontend sections 8 and 13; guidance-docs section 2; detail-ux sections 3 and 4. C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-detail/saga-detail.html:4 and :6. C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-detail/saga-detail.scss:14-30. C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-list/saga-list.scss:54-85.

**Fix:** One name each: .topbar-end; .btn with .btn--quiet; 'banner banner--warning' and 'banner banner--error' everywhere; util/; canViewData; PermissionKey. Detail-ux 3 adds only the global classes it needs; auth-frontend 1 adds the rest and updates saga-detail.html.

## 13. [minor] (detail-ux, auth-frontend, guidance-docs)
Three descriptions of the same states and labels.
- Without sagas.data: detail-ux disables the three data buttons beside 'Saga data is hidden for your role. It needs the sagas.data permission.'; auth-frontend hides them and shows 'State data and message payloads are hidden for your role.'; guidance-docs says the controls are hidden.
- Labels: the lead's decision and the tour copy say 'Data at start' and 'Data at end'; detail-ux renders 'At start', 'At end' or 'Current'.
- Offset: the tour says to hover for the UTC timestamp and the time since the first entry; detail-ux shows the offset inline.

**Evidence:** detail-ux section 3 (SagaDataOverview, SagaDataInspector note table, row template). auth-frontend section 9. guidance-docs contract 4 and detail tour steps 5 and 7.

**Fix:** Detail-ux owns the components, so its behaviour stands and auth-frontend only supplies the boolean. Fix the labels once: a 'Saga data' group with 'At start', 'At end' (or 'Current') and 'Compare', with aria-labels 'Data at start' and 'Data at end'. Guidance-docs writes its copy against the shipped labels after the detail commits.

## 14. [minor] (detail-ux, guidance-docs)
Interaction gaps against the user's wording and the tour copy.
- The user asked that clicking a timeline step opens the map at that step. After the regrouping only the entry rows inside a step are buttons; the step header does nothing.
- The list tour tells users to select a row and a column heading. Rows are tr elements with routerLink, focusable but not opened by Enter; sort headings are th elements with click handlers and cannot be focused. No blueprint changes either.

**Evidence:** detail-ux section 3, SagaTimeline template (tl-head holds only the Data toggle). C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-list/saga-list.html:63, :69 and :79. guidance-docs list tour steps 4 and 5.

**Fix:** Make the step title a button that jumps to the step's last entry, which is the state after the step. In the guide commit for the list, turn the sort headings into buttons and give rows an Enter and Space handler or a link in the first cell.

## 15. [minor] (detail-ux, auth-frontend, packaging)
Helpers under src/app/testing are compiled by the production build unless excluded. The app tsconfig includes every .ts file and excludes only specs. Auth-frontend adds the exclusion together with its mock, which uses vitest; detail-ux creates testing/timeline-fixtures.ts earlier and does not; packaging's .dockerignore drops only spec files.

**Evidence:** C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/tsconfig.app.json:9-14. detail-ux Files ('testing/timeline-fixtures.ts'). auth-frontend Files ('dashboard-web/tsconfig.app.json: exclude src/app/testing/**'). packaging section 1 (.dockerignore).

**Fix:** The first commit that creates src/app/testing (detail-ux 2 in the global order) adds the exclusion, and dashboard-web/.dockerignore lists src/app/testing as well.

## 16. [minor] (auth-backend, auth-frontend, packaging)
Two windows where every gate is green but the demo regresses.
- From the commit that enforces permissions until the SPA switch-over, the SPA still authenticates with the embedded key, which now maps to Viewer, so Retry returns 403.
- After packaging 1 the dev server is on 4201, while the README and dashboard-web/README.md still say 4200 until packaging 8.

**Evidence:** auth-frontend risk 'if the API lands first the old SPA's key maps to Viewer and Retry returns 403'. packaging commits 1 and 8. C:/Users/rafae/Documents/Projects/vSaga/README.md:96 and :104. C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/README.md:21 and :33.

**Fix:** Set Dashboard__ApiKeyRole to Operator in compose when the identity volume commit lands and change it to Viewer in the switch-over commit. Packaging 1 corrects the port lines it invalidates.

## 17. [minor] (auth-backend)
Two commits bundle several logical changes, against the one-logical-change rule. Commit 4 holds the policy scheme, the fallback policy, cookie sessions, antiforgery, the rate limiter, forwarded headers, four endpoints and the API-key role mapping. Commit 8 holds per-type groups, per-call checks, the origin guard, the connection registry and payload stripping.

**Evidence:** auth-backend commit sequence, items 4 and 8. C:/Users/rafae/Documents/Projects/vSaga/CONTRIBUTING.md:80-82.

**Fix:** Split 4 into 'authenticate through a policy scheme and protect every endpoint by default' (the cookie scheme registered with its problem-writing events, so credential-less requests keep their 401 body) and 'add sign-in' (session, login, logout, password, antiforgery, rate limit, lockout). Split 8 into access-checked subscriptions with per-type groups, the origin guard, and aborting connections on access change. Forwarded headers and payload stripping are already delivered by packaging 5 and engine 5.

## 18. [minor] (auth-backend, packaging)
Loose ends on the API surface.
- Dashboard:ApiKeyRole may name Administrator, which would give a shared secret with no lockout and no rate limit the access.manage permission.
- Packaging leaves Cache-Control: no-store on /api responses to the API; auth-backend sets it only on the session endpoint.
- With two DbContexts registered, every dotnet ef command needs --context, and nothing records the two commands.

**Evidence:** auth-backend section 10 (API key resolves Dashboard:ApiKeyRole, 'access is unscoped'), section 7 (session row: no-store) and section 1 ('--context becomes mandatory for both'). packaging contract 'Cache-Control: no-store on /api/ responses is the API's job'. C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/VSaga.Dashboard.Api.csproj:17-21.

**Fix:** The API-key principal never receives access.manage, and the documentation says so. One response filter sets no-store on everything under /api. CONTRIBUTING gains the two dotnet-ef commands.

## 19. [minor] (guidance-docs)
Small internal gaps.
- The overlay drops a step when its anchor, reveal control and fallback are all missing as the tour begins, but the summary says a missing anchor falls back to a centred popover and a step is never skipped silently.
- GUIDE_PERMISSION_CHECK is to be provided by authentication, and auth-frontend does not mention it.
- The configuration table assumes ten new Dashboard keys; with Session:CookieName, RateLimit:AuthPerMinute, TrustedProxies and StateSnapshots:MaxBytes there are fourteen.

**Evidence:** guidance-docs Summary bullet 3 against section 3 'Begin' and 'Show a step'; contract 2; contract 7 ('the ten new Dashboard:* keys'). auth-backend section 11. packaging contract 'New key: Dashboard:TrustedProxies'. engine-snapshots contract 'Dashboard:StateSnapshots:MaxBytes'.

**Fix:** State one rule: filter at begin, and use the centred fallback only for an anchor that disappears mid-tour. The first guide commit provides the token from AuthService.can. The configuration table lists all fourteen keys.
