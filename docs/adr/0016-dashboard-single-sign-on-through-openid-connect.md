# ADR 0016: The dashboard offers sign-in through an OpenID Connect provider beside local accounts; the permission model is unchanged and is fed from groups

**Status:** **Accepted** — 2026-11-09, by the maintainer. Written at the maintainer's request (stream F of the 2026-11-09 session);
accepted, and nothing in it is built yet.
**Date:** 2026-11-09
**Relates to:** [`0006-dashboard-authentication-and-identity-store.md`](0006-dashboard-authentication-and-identity-store.md)
(this record is the single-sign-on follow-up it rejected "for now"; it **extends** 0006 and supersedes none of
its sub-decisions, so 0006 is not edited, see "Relationship to ADR 0006");
[`0008-dashboard-retry-reruns-the-failed-step.md`](0008-dashboard-retry-reruns-the-failed-step.md) (a retry
records `dashboard:<username>`, which constrains the usernames a provider may create).
**Design:** [`../design/dashboard-usability-and-access.md`](../design/dashboard-usability-and-access.md) (§8
describes what exists; §14 lists single sign-on and the lockout device cookie as deferred).

---

## Context

ADR 0006 gave the dashboard its own users, a cookie session, four permissions granted per saga type, and a
SQLite identity store. It also rejected "External OpenID Connect only" as option B, for two reasons, and named
what would make it worth reopening. Those reasons and conditions are the starting point here, so each is
checked against the code as it stands (build `5253390`).

### What exists, and where

- **Sign-in and session.** `AuthEndpoints.LoginAsync`
  (`dotnet/src/VSaga.Dashboard.Api/Endpoints/AuthEndpoints.cs`) verifies a password through
  `CredentialVerifier` (`dotnet/src/VSaga.Dashboard.Identity/Services/CredentialVerifier.cs`), then
  `SignInAsync` issues the cookie: `DashboardClaims.ForUser` (`.../Services/DashboardClaims.cs`) puts only
  the user id, the username and the security stamp in it, and `DashboardCookieEvents.SetSignedInAt` records
  the sign-in time. Nothing about what the user may do is in the cookie.
- **Access is resolved per request.** `DashboardCookieEvents.ValidatePrincipal`
  (`.../Auth/DashboardCookieEvents.cs`) calls `CallerAccessResolver.ResolveAsync`
  (`.../Services/CallerAccessResolver.cs`), which reloads the user, rejects a missing or disabled user or a
  changed stamp, and evaluates the user's grants and their teams' grants with `AccessEvaluator`
  (`.../Services/AccessEvaluator.cs`). This is the property that makes federation cheap: whatever creates the
  session, every endpoint, the hub and the audit trail see an ordinary `CallerAccess`.
- **The grant model.** `AccessGrant`, `DashboardUser`, `DashboardTeam` and `DashboardRole`
  (`.../Model/IdentityModel.cs`); the built-in roles and the four permissions (`.../Model/BuiltInRoles.cs`,
  `Permissions.cs`); the store contract `IDashboardIdentityStore` (`.../Stores/IDashboardIdentityStore.cs`),
  aggregate-shaped on purpose.
- **The last-administrator rule.** `AccessAdministrationService.EnsureAdministratorRemains`
  (`.../Services/AccessAdministrationService.cs`, near line 448) evaluates a proposed snapshot inside
  `InScopeAsync`'s exclusive store scope and throws `last_administrator` (409) unless an enabled user would
  still hold `access.manage` for all saga types.
- **The lockout.** `CredentialVerifier.Refusal` refuses a locked account before the password is checked;
  `IDashboardIdentityStore.RecordFailedSignInAsync` counts and locks atomically; the limits are
  `Dashboard:Lockout:MaxFailedAttempts` and `Dashboard:Lockout:Minutes`
  (`.../DashboardSecuritySettings.cs`). The residual risk (a known username can be kept locked) is risk 1 of
  0006's accepted residual risks, and the device-cookie bypass is item "The device-cookie lockout bypass" in
  the design's §14.
- **The API key** is a separate scheme: the policy scheme in `DashboardAuthExtensions.AddSchemes`
  (`.../Auth/DashboardAuthExtensions.cs`) sends any request carrying `X-Api-Key` or `Authorization: Bearer`
  to `ApiKeyAuthenticationHandler`, and every other request to the cookie.
- **The edge.** `DashboardEdge` (`.../Hosting/DashboardEdge.cs`) owns CORS and forwarded headers; the SPA is
  served same-origin by nginx (`dashboard-web/nginx/default.conf.template`), which proxies `/api/` and
  `/hubs/`. Antiforgery is enforced per endpoint after routing (`.../Auth/AntiforgeryEnforcement.cs`).

No OpenID Connect code or package exists in the repository: a search for `OpenId`, `OIDC`, `SSO` and
`single sign` finds only the prose in 0006, the design document and `docs/dashboard.md`.

### Are the revisit conditions in ADR 0006 met?

ADR 0006's closing section, "What would invalidate this decision later", lists three conditions, and option B
gives two reasons for rejecting SSO. Status as of this record:

| ADR 0006 | Status |
| --- | --- |
| Condition 1: several API replicas | **Not met.** No compose file sets `replicas` or `scale`; `dashboard-api` is one container on one SQLite file. This record neither needs nor changes it. The OpenID Connect state and nonce cookies are Data Protection payloads, so SSO would inherit the single key ring's single-instance limit rather than add a new one (an inference from the design, not tested). |
| Condition 2: a single-sign-on requirement | **Met as a request, not as an in-repo need.** The maintainer asked for this record. Nothing in the repository names a provider, a tenant or a deployment that needs it, so which providers must work is an open question (below). |
| Condition 3: per-instance rather than per-type scoping | **Not met, and unrelated.** Grants stay keyed by saga type. |
| Option B reason 1: the demo and teams without an identity provider would have to run one | **Addressed by the shape of the decision**, not by the passage of time: SSO is optional, off unless configured, and the demo composes with no provider (decision 7). |
| Option B reason 2: the permission model would still be needed, "fed from claims instead of a local store" | **Addressed**: the model, the store and the last-administrator rule are kept; claims feed the existing teams (decision 3). |

ADR 0006's own "negative" consequences also list "no multi-factor authentication and no single sign-on". SSO
removes the second for people who use it, and brings the provider's multi-factor policy with it; local
accounts keep the first.

### Forces

1. **Offboarding and password lifecycle belong to the organisation's directory.** Today an administrator
   must also disable a leaver in the dashboard; a forgotten step leaves a live account whose password the
   organisation does not manage.
2. **The permission model is the valuable part and must not fork.** Roles, teams, per-saga-type grants,
   redaction, scoped lists, hub groups and retry attribution all hang off one `CallerAccess`. A second
   authorization mechanism would double the surface a security review has to cover.
3. **The demo must stay one `docker compose up`.** ADR 0006 seeded a local administrator for exactly this
   reason (decision 5).
4. **The session cookie is `SameSite=Strict`, and a provider redirects back from another site.** That
   interaction needs a decision, not a default (decision 4).
5. **Revocation is currently immediate** (stamp check on every request) and a federated session cannot be
   revoked by the provider at the same speed without extra machinery (decision 5, and a stated limitation).
6. **No new dependency without approval** (AGENTS.md rule 4). The framework ships cookie authentication and
   the generic OAuth 2.0 handler; the OpenID Connect handler is a NuGet package. I checked the installed
   shared framework (`Microsoft.AspNetCore.App` 10.0.9) and it contains
   `Microsoft.AspNetCore.Authentication.OAuth.dll` and `.Cookies.dll` but not an OpenID Connect assembly.

---

## Decision

**Add OpenID Connect sign-in (authorization code flow with PKCE, handled by ASP.NET Core's OpenID Connect
authentication handler) as a second way to obtain the same session cookie. A signed-in federated person is a
`DashboardUser` linked to the provider's `(issuer, subject)`; provider groups are mapped onto existing teams,
and the teams keep carrying the roles and saga-type grants. Local accounts, the API key, the first
administrator and the break-glass reset are unchanged. With no `Dashboard:Sso:*` key set, none of this exists
at run time.**

The package that implements it is not added by this record; it is the first open question.

### 1. A federated person is a local user record with an external identity

- `DashboardUser` gains an optional `ExternalIdentity` (`Issuer`, `Subject`), unique together where present.
  `IDashboardIdentityStore` gains `FindUserByExternalIdentityAsync(issuer, subject, ct)`; nothing else about
  the interface changes, and it stays implementable on a document store.
- The record is the same one an administrator edits: enable and disable, direct grants, team membership and
  deletion work as today, and the user appears in the audit trail as `dashboard:<username>`.
- `PasswordHash` stays non-null; a federated user is stored with an empty hash and `CredentialVerifier.Refusal`
  gains a case that refuses it before hashing (verifying against the dummy hash like the other refusals, so
  timing is unchanged). The password-change endpoint and the administrator password reset answer `409
  externally_managed` for such a user. How a build of the previous version treats an empty hash is not
  something I verified; rollback is covered in decision 11.
- **Binding is by `(issuer, subject)` only.** A username or e-mail address in a token never selects an
  existing account. The provider and the dashboard each have their own idea of who `alice` is, and silently
  merging them is how a provider-side rename or an unverified e-mail becomes an account takeover. A sign-in
  whose derived username equals an existing account's is refused and audited (`username_conflict`).
  Deliberate conversion of a local account is decision 8.

### 2. Claims mapping rules

Everything the provider says is reduced to four inputs, taken once at sign-in from the validated ID token
and, if the provider needs it, the userinfo response. Names are keys under `Dashboard:Sso:`; defaults in
parentheses.

| Input | Rule |
| --- | --- |
| Identity | `iss` must equal the configured provider's issuer (the handler validates it against discovery) and `sub` is the key of rule 1. No `sub`, no sign-in. |
| Username | `UsernameClaim` (`preferred_username`), else `email`. Taken **once, when the user is first created**, and never refreshed, so the actor recorded on retry entries (ADR 0008) and the audit log does not drift. It must pass `AccessValidation.IsValidUsername` (the same rule as any username: 64 characters at most, letters, digits and `. _ @ + -`, not `api-key`); otherwise the sign-in is refused and audited as `invalid_username` instead of being rewritten. A username taken from `email` additionally requires `email_verified` to be true. |
| Display name | `DisplayNameClaim` (`name`), else the username. Refreshed at each sign-in, trimmed, control characters refused. |
| Groups | `GroupsClaim` (`groups`), a list of strings, compared **ordinally and exactly** with the teams' external group names (as saga types are compared). Names longer than 200 characters are ignored. A provider that replaces a long list with a reference instead of the list yields no groups and a Warning, never a lookup against the provider's directory API. |

What the mapping does **not** do: it does not read a role, a permission or a saga type from a token; it does
not create teams, roles or grants; it stores no e-mail address, no token and no claim other than the four
inputs. Access stays administrator-authored in the dashboard, which is the page a reviewer reads to answer
"who can retry what".

### 3. Groups feed teams; teams keep carrying the grants

- `DashboardTeam` gains `ExternalGroups` (a list of provider group names, empty for today's teams). A team
  with a non-empty list is **externally managed** for its federated members.
- **At sign-in** (never per request), inside one exclusive store scope and on a snapshot, as
  `AccessAdministrationService.InScopeAsync` already does: the federated user is created or found, the
  display name is refreshed, and the user is added to every externally managed team whose group the token
  names and removed from every externally managed team whose group it does not. Direct grants and membership
  of ordinary teams are never touched. Local users in the same team are never touched.
- The team payload (`PUT /api/admin/teams/{id}`) may not add or remove a federated member of an externally
  managed team; it answers `409 externally_managed_membership`, because the next sign-in would undo the edit.
- A membership change made this way notifies `IAccessChangeObserver` exactly as an administrator's change
  does, so a user's open hub connections close and reconnect under the new access.
- **Creation of a user is just-in-time and off by default** (`AutoProvision`, `false`). When on, a person
  with no record is created only if at least one of their groups maps to a team; a stranger who maps to
  nothing gets no record, a refusal page and an audit event (`no_access`), so the user list does not fill with
  everyone the provider can authenticate. When off, an administrator must first mark a local account for
  conversion (decision 8), since the dashboard has no other way to create a record that waits for a provider
  identity.
  A person who already has a federated record always signs in, and sees "no access" like any grant-less local
  user if their groups no longer map.
- **Disabled is the dashboard's veto.** A disabled federated user is refused whatever the provider says
  (`disabled`), and a sign-in never re-enables one. Deleting a federated user makes the next sign-in a
  first sign-in, so "exclude this person" means disable, not delete.
- Whoever administers the mapped groups at the provider can therefore administer the dashboard, if one of the
  mapped teams holds the Administrator role. That is the point of SSO and also its risk; it is stated so the
  deployer chooses the mapping rather than discovers it.

### 4. The same session, issued by a different door

- The provider's callback ends in the same cookie as a password sign-in: the handler's ticket is replaced in
  its ticket-received step by `DashboardClaims.ForUser(user, CookieScheme)` with `DashboardCookieEvents.
  SetSignedInAt`, non-persistent. The external principal is discarded and **no ID, access or refresh token is
  saved** (`SaveTokens` stays false, `offline_access` is not requested), so the cookie, `ValidatePrincipal`,
  the security stamp, sliding idle expiry, absolute lifetime, the hub's ticket handling, antiforgery binding
  and `CallerAccess` are all unchanged.
- **Routes.** `GET /api/auth/sso/login?returnUrl=` starts the flow (anonymous; `returnUrl` must be a local
  absolute path, anything else is ignored); `GET /api/auth/sso/callback` is the handler's callback path. Both
  sit under `/api/` so the nginx proxy and the `ng serve` proxy already carry them, and 0006's
  anonymous set ("exactly `/health`, `GET /api/auth/session`, login, logout and setup") grows by these two and
  by nothing else; the test that asserts the set changes with them. A failure is a `302` to
  `/login?error=<code>` (a navigation cannot show a JSON problem body), with a short fixed set of codes
  (`sso_unavailable`, `sso_failed`, `sso_no_access`, `sso_disabled`) and the reason in the audit log only.
- **`SameSite`.** The session cookie stays `SameSite=Strict`. The cookie is *set* on the callback response,
  which browsers accept; the redirect that follows is a navigation in a chain that began at another site, so
  the browser may withhold the cookie from it, and that is harmless because the target is the SPA's static
  `index.html`, whose first call (`GET /api/auth/session`) is same-site and carries the cookie. The handler's
  own state and nonce cookies default to `SameSite=None`, which browsers accept only with `Secure` and so
  break plain-HTTP localhost; the flow therefore requests `response_mode=query` and sets those two cookies to
  `Lax`, which a top-level `GET` carries. The form-post mode (a cross-site `POST` to the callback) is not used.
  *This reasoning is from the cookie rules and has not been exercised in a browser; it is the first thing to
  verify live.*
- **CSRF.** The callback is authenticated by the handler's `state`, nonce and PKCE verifier, and it runs in
  `UseAuthentication`, ahead of `AntiforgeryEnforcement`, so it is not an antiforgery exemption to add. The
  login start is a safe `GET` that changes no dashboard state. After sign-in the SPA's first
  `GET /api/auth/session` issues the request token bound to the new identity, as after a password sign-in,
  so every unsafe request is validated exactly as today. Logout stays `POST /api/auth/logout` with the
  token.
- **Sign-out.** Local only in this record: the dashboard session ends, the provider's does not, and "Sign in
  with SSO" again signs the person straight back in. Ending the provider session (RP-initiated logout) is a
  separate switch if wanted (open question).
- **Redirect URI.** `Dashboard:Sso:PublicOrigin` (empty is an error when SSO is configured) names the origin
  the provider has registered, validated like `Dashboard:WebOrigin` in `DashboardEdge`, so the redirect URI is
  not guessed from request headers that a TLS terminator may hide. It is not a CORS origin.

### 5. Lifetime and revocation

- Idle and absolute limits are the existing `Dashboard:Session:*` keys. An optional
  `Dashboard:Sso:AbsoluteTimeoutHours` (default 8, capped by the global value) is stored on the ticket next to
  `.vsaga.signedInAt` and read by `DashboardCookieEvents`, so a federated session ends sooner than a local one.
- **This is a regression in revocation speed and it is stated as one.** Disabling someone at the provider
  does not reach the dashboard until their session ends (at most the absolute lifetime) because the dashboard
  holds no provider token to check. What is immediate is unchanged: disabling or deleting the *dashboard*
  record, rotating the stamp, and changing grants all take effect on the next request through the existing
  mechanisms. An operator who needs the provider's offboarding to be faster lowers the SSO lifetime or
  disables the record. OpenID Connect back-channel logout would close the gap; it needs a store of
  provider session ids and is out of scope here (open question).

### 6. Local accounts, the bootstrap administrator and the break-glass path

- **Nothing local is removed.** The first administrator (seed or setup code), `ResetOnStart`, the lockout,
  rate limits and the password pages work as in 0006 sub-decision 5. SSO does not seed administrators: the
  deployer signs in once with the local administrator, creates the teams and their external groups, and
  from then on groups carry membership. That keeps the bootstrap free of any rule that grants rights from a
  token before a human has chosen the mapping.
- `Dashboard:Sso:LocalSignIn` (`true`). When `false`, password sign-in is refused (the uniform 401, after
  the password is verified, counting nothing) for everyone who does not hold unscoped `access.manage`. The
  rule is expressed in the existing permission model instead of a new per-user flag, so the break-glass
  accounts are exactly the administrators and `ResetOnStart` keeps working. The login page then shows the
  password form only behind a "Sign in with a local account" disclosure.
- The API key is untouched (0006 sub-decision 4). IdP-issued bearer tokens for machines are not accepted:
  the policy scheme already treats any `Authorization: Bearer` as an API key, and a machine-token feature is
  a separate decision if it is ever wanted.
- A warning is logged at start, and `identity` reports `Degraded`, when SSO is on, `LocalSignIn` is `false`
  and no enabled local administrator exists: the deployment would have no way in if the provider were down.

### 7. The demo stays zero-dependency

- SSO is configured by `Dashboard:Sso:Authority`, `ClientId`, `ClientSecret` and `PublicOrigin`. **None is set
  in `docker-compose.yml` or any overlay**, so the demo needs no provider and starts as it does now. With none
  set, no authentication scheme is registered, `/api/auth/sso/*` answers `404`, nothing contacts a provider
  at start, and the session response says SSO is not available.
- Partial configuration fails at composition, naming the key (an `Authority` without a `ClientId`), like the
  other `Dashboard:*` settings; it never silently turns off. A provider that is unreachable does **not** stop
  the API: discovery happens on the first sign-in, the SSO route answers `sso_unavailable`, and a new `sso`
  health check reports `Degraded`, never `Unhealthy`, from the last discovery result without calling the
  provider on every probe. Password sign-in is unaffected.
- The one thing that is not zero is the build: the OpenID Connect assembly (decision on the package) would
  ship in the API image whether or not SSO is configured.

### 8. Linking an existing local account (second slice)

Merging is refused (decision 1), but a team that already has local accounts will want to keep a person's
username, direct grants and retry history. The proposal is an explicit administrator action, not a heuristic:
`POST /api/admin/users/{id}/convert-to-sso` marks the account *awaiting link* with the expected username
claim; the next SSO sign-in whose `UsernameClaim` equals the account's username binds `(issuer, subject)`,
clears the password hash and rotates the stamp, in one exclusive scope, and is audited. An account that is
not flagged is never linked. This slice is separable; it is listed as an open question because it adds an
administration endpoint and a state to the user record.

### 9. The last-administrator rule is kept exactly, and applies to the provider too

- **Administrators changing things:** `EnsureAdministratorRemains` already evaluates stored users and teams,
  so a federated user holding `access.manage` through an externally managed team counts as an administrator,
  and an administrator's edit that would leave none is still refused with `409 last_administrator`.
- **The provider changing things:** the sign-in reconcile (decision 3) runs the same
  `EnsureAdministratorRemains` over the proposed snapshot. If removing a membership would leave no enabled
  user with unscoped `access.manage`, **that removal is skipped**, the sign-in proceeds with the unchanged
  membership, a Warning and an audit event say so (`last_administrator_kept`), and `identity` reports
  `Degraded`. The rule is absolute in 0006 ("the last administrator cannot be removed") and a directory
  change is not exempt. The cost is that the provider can be out of step with the dashboard exactly until a
  human adds another administrator, which is what the rule is for.
- **Stated limit:** a federated administrator is counted from their stored membership, which is as fresh as
  their last sign-in. The rule therefore cannot tell that such a person has left the provider. The local
  break-glass administrator (decision 6) is the answer, and the start-up warning in decision 6 is how the
  deployer learns that there is none.

### 10. The lockout device cookie

The design's §14 defers a Data-Protected, HttpOnly "known device" cookie that lets a browser that has signed
in before skip a lock that someone else caused. This record does not build it, and gives the reasoning so
the deferral is a decision:

- **SSO removes most of the exposure.** A federated user has no dashboard password, so there is nothing to
  count or lock; the provider's own throttling applies. The `RecordFailedSignInAsync` counter and
  `CredentialVerifier.Refusal`'s lock check concern local accounts only.
- **With `LocalSignIn=false` the remaining local accounts are administrators**, and their usernames need not
  be advertised (the password form sits behind a disclosure). The griefing attack needs a known username; the
  per-address-and-username limiter (`AuthRateLimits`) still bounds the attacker.
- **If it is built later it is a new feature and gets its own ADR** (AGENTS.md rule 1). The shape to
  start from: a random per-device secret issued after a successful local sign-in, stored hashed in a new
  `KnownDevices` collection reachable through `IDashboardIdentityStore`, a lock check in
  `CredentialVerifier.Refusal` that a valid device proof for that user bypasses, a per-device failure counter
  so a stolen cookie is not an unlimited guesser, and rotation on use. The cookie would be read in
  `AuthEndpoints.LoginAsync` and passed to the verifier, which is why it cannot be a one-line change.
- Trigger to reopen: a deployment that keeps local sign-in for many users reachable from an untrusted
  network.

### 11. Migration, rollback and the zero case

- **Upgrade.** The two additions (decision 1's identity columns with a unique index, decision 3's external
  groups) are nullable or empty-by-default and come from a `dotnet ef`-generated migration in
  `VSaga.Dashboard.Identity.Sqlite`, applied by the existing start-up migration path
  (`IdentityStartup`). Existing users, teams, grants, the key ring and open sessions are untouched: the
  cookie's claims do not change, so nobody is signed out by the upgrade. The identity volume holds password
  hashes and the key ring, so it is backed up before the first upgrade as it already should be (0006,
  "A volume to back up").
- **Turning SSO on** is configuration plus a restart, in this order: set the `Dashboard:Sso:*` keys and the
  provider's redirect URI; sign in as the local administrator; create or edit teams to carry the roles and
  grants and list their external groups; have a first person sign in through the provider and check what
  they can see; only then consider `AutoProvision` or `LocalSignIn=false`. Local users keep signing in
  throughout.
- **Moving a person who already has a local account.** Without linking (decision 8) they sign in through the
  provider once, which creates a second, federated record under a username that must differ from the local
  one (the conflict rule refuses an equal one); an administrator then disables the local account. Retry
  entries made under the old username keep it. With linking, the record, username and direct grants carry over.
- **Turning SSO off again.** Removing the `Dashboard:Sso:*` keys removes the button and the routes. Federated
  users stay in the store with their teams and grants but cannot sign in: they have no password and the
  password endpoints refuse them. To give one back a way in, an administrator password reset on a federated
  user is accepted **only while SSO is not configured** and converts the account to a local one (the external
  identity is cleared, the new password is temporary and must be changed, the stamp rotates, the change is
  audited). That conversion is a proposal that adds a rule to the reset endpoint; the alternative is to
  create new local accounts.
- **Rolling the build back** after the migration: the migration is forward-only, an older build does not map
  the new columns, and federated records would look to it like users with an empty password hash. Whether the
  older hasher fails safely on an empty hash, and whether EF Core accepts a history row it does not know, I
  did not verify; restore the volume backup rather than rely on either.
- **The zero case is tested, not assumed.** With no `Dashboard:Sso:*` key: the registered authentication
  schemes are exactly today's, `/api/auth/sso/login` is `404`, the anonymous endpoint set is today's, and the
  session response reports SSO as unavailable. The existing sign-in, administration and hub suites run
  unchanged.

---

## Options considered

### A. Do nothing; keep local accounts only

Zero change and zero new dependency. Viable for a team that has no directory to federate with, and it is what
the repository does today. Rejected as the answer to this request, not as a general position: offboarding, MFA
and password lifecycle stay with each dashboard deployment, which is the cost ADR 0006 accepted "for now" and
named SSO as the trigger to reconsider. Because decision 7 makes SSO opt-in, choosing B costs the
local-accounts-only deployer nothing at run time except the assembly in the image.

### B. ASP.NET Core's OpenID Connect handler, additive, claims feed teams (chosen)

Discovery, key rotation, ID-token validation, `state`, nonce and PKCE are the handler's, not ours. The
integration is a few hundred lines at the seams described above (ticket-received, a reconcile method on
`AccessAdministrationService`, two routes, a session-response field, two columns), and every downstream
mechanism is reused. Cost: one package (question 1) and its transitive Microsoft.IdentityModel packages,
which the repository's `CentralPackageTransitivePinningEnabled` and NuGet audit would govern.

### C. The generic OAuth 2.0 handler with the userinfo endpoint (no package)

`Microsoft.AspNetCore.Authentication.OAuth` is in the framework, so this adds no dependency: authorization
code with PKCE, claims from the userinfo response (which arrives over TLS directly from the provider, so the
ID token's signature need not be checked for identity). Rejected as the primary route but kept as the
fallback if the maintainer declines the package: there is no discovery (three endpoint URLs configured by
hand), no ID-token validation or nonce, and some providers put groups only in the ID token, not in userinfo,
which would make the group mapping unusable on them. We would own more of OpenID Connect than we want to.

### D. A reverse proxy authenticates, the API trusts identity headers

An identity-aware proxy (oauth2-proxy, an auth-request module, a cloud gateway) in front of nginx sends
`X-Forwarded-User` and `X-Forwarded-Groups`. No package, and IdP-agnostic. Rejected as the primary mode,
worth keeping as a later addition: **identity becomes whatever a header says**, and 0006 already records that
the API's port "can be reached without nginx in front" (decision 10). The edge has the right primitive,
`DashboardEdgeSettings.TrustsPeer`, but every misconfiguration (a proxy that does not strip the client's own
header, a published port) is a full impersonation, whereas a mistake in decision B fails to authenticate.
There is no session for the dashboard to revoke, the WebSocket upgrade must carry the headers, and nothing
here can be tested without the proxy. If added, it should enter through the same internal seam as B: a
normalised `(issuer, subject, username, displayName, groups)` handed to one reconcile method, so decisions 1
to 3 and 9 apply unchanged.

### E. IdP-only: remove local accounts

The purest form of "single sign-on" and the one ADR 0006 option B described. Rejected: the demo would need a
provider, a provider outage would lock the operators out of the tool they need during an incident, the
break-glass and `ResetOnStart` recovery would disappear, and the API key would still need a role. The
`LocalSignIn=false` setting gives most of the benefit (no password for ordinary people) and keeps the way
back.

### F. Network-level access only

Put the dashboard behind a VPN or an identity-aware gateway and keep local accounts inside it. This is
compatible with every other option and is what some deployments will do regardless. It does not carry a
person's identity or groups into the dashboard, so retries are still attributed to a shared local login and
access is still hand-maintained. Not a substitute.

### G. Embed an identity provider, or accept SAML

An embedded provider (OpenIddict, Duende) makes vSaga an identity provider. SAML 2.0 has no framework support
and means a third-party library. Both are far larger than the need; a SAML-only organisation puts a bridge
that speaks OpenID Connect in front of its directory.

### H. Map claims directly to roles and saga types in configuration

Stateless, and no team columns: `Dashboard:Sso:Mappings` such as group `ops` to role `Operator` on
`OrderSaga`. Rejected: access then lives in `appsettings` where the administration pages cannot show it, a
change needs a restart, and two places would decide access. Keeping groups as an attribute of teams gives one
authoritative page and one evaluator.

---

## Consequences

### Positive

- People who sign in through the provider need no dashboard password and inherit its multi-factor policy;
  retries and audit events carry a name the organisation recognises.
- Access stays reviewable in one place, and nothing downstream of `CallerAccess` changes: saga endpoints,
  redaction, scoped lists, the hub and retry attribution are untouched.
- Deployments that do not configure SSO see no behavioural change (decision 7).
- The seam (a normalised external identity into one reconcile method) leaves room for the proxy mode (D) and
  for a later link or back-channel logout without redoing the model.

### Negative

- **A package, and a larger attack surface.** An internet-facing redirect flow, a new pair of anonymous
  routes, and a provider whose claims are trusted for team membership.
- **Slower revocation for federated sessions** (decision 5).
- **Group names become security-relevant strings.** A typo in an external group name silently grants nobody,
  and a group renamed at the provider silently removes access at the next sign-in.
- **Two kinds of user** in the lists and the administration pages: a "federated" marker, no password actions,
  and a team editor that refuses some membership edits.
- **Wire contract changes.** The session response gains an `sso` section and `localSignIn`; user and team
  responses and requests gain fields. Request records reject unknown members, so the golden JSON fixtures in
  .NET and Angular change together.
- **Operator burden.** Registering the redirect URI, the client secret in configuration (never logged),
  and the provider's group-claim settings are all outside the repository's control.
- **Stated limits that remain:** no single logout, no back-channel logout, groups in a token are as fresh as
  the sign-in, one API instance per identity file (0006 consequence unchanged).

### Neutral

- The Data Protection key ring and the identity store gain nothing but two columns and a unique index.
- Configuration gains a `Dashboard:Sso:*` group (listed under "Configuration added"); like the other
  dashboard settings it is read once and validated at composition.

---

## Relationship to ADR 0006

0006 says that on a single-sign-on requirement "sub-decisions 1, 2 and 5 would be revisited" and that the
permission model "would likely survive, mapped from the provider's claims". Revisited, and the result:

| 0006 sub-decision | Disposition |
| --- | --- |
| 1 Cookie sessions, antiforgery, passwords, throttling | Kept. The SSO door issues the same cookie; the callback relies on the handler's state and PKCE, not on antiforgery (decision 4). |
| 2 Dashboard-owned identity store | Kept; two columns and one lookup added (decisions 1, 3). |
| 3 Roles, permissions, grants, last administrator | Kept; the rule also governs provider-driven changes (decision 9). |
| 4 API key | Kept, no IdP bearer tokens (decision 6). |
| 5 First administrator | Kept as bootstrap and break-glass; SSO seeds nobody (decision 6). |
| 6 to 11 | Unchanged; audit (11) gains SSO events. |

Because this record **adds** a way in and replaces no decision, AGENTS.md's supersede rule (a one-line pointer
on the old record) does not apply and 0006 is not touched. If the maintainer instead chooses option E, that
would supersede 0006's sub-decisions 1 and 5 and 0006 would then need the pointer.

---

## Configuration added (proposed names)

`Dashboard:Sso:Authority`, `ClientId`, `ClientSecret`, `PublicOrigin`, `DisplayName` (the button label),
`Scopes` (`openid profile`, plus whatever the provider needs for groups), `UsernameClaim`, `DisplayNameClaim`,
`GroupsClaim`, `AutoProvision` (`false`), `LocalSignIn` (`true`), `AbsoluteTimeoutHours` (`8`). All validated
at composition; `ClientSecret` is never logged and never in a response. New audit events for sign-in,
refusal (with the code), provisioning, membership reconcile and `last_administrator_kept`, in
`DashboardAudit`'s numbering.

## Build order

1. Maintainer decisions on the open questions below. Add the package only after question 1.
2. Store: columns, unique index, `FindUserByExternalIdentityAsync` and a generated EF Core migration (never
   by hand, per CONTRIBUTING); store-neutral records and conformance cases for the identity store.
3. `AccessAdministrationService` reconcile with the last-administrator rule, tests that the rule holds under
   concurrency and that a skipped removal is reported.
4. Handler wiring, routes, session-response fields and an in-process fake provider for tests (discovery
   document and signing key written with the framework's cryptography, no package).
5. SPA: sign-in button and error codes, the user and team pages. The sentences that become false are part of
   the change (AGENTS.md rule 2): `docs/dashboard.md` ("no single sign-on"), `docs/configuration.md`,
   `docs/dashboard-guide.md`, the tour steps in `guide-tours.ts` and the `guide-areas.ts` version.
6. Live verification against a real provider in a compose overlay (AGENTS.md rule 3), in a browser with the
   console open, covering the `SameSite` reasoning of decision 4, a provider outage, a removed group, and the
   last-administrator case.

---

## Open questions for the maintainer

1. **Package.** Approve `Microsoft.AspNetCore.Authentication.OpenIdConnect` at the version matching the
   framework (10.0.x), for the benefit stated in option B: discovery, signing-key rotation, ID-token
   validation, state, nonce and PKCE are maintained by Microsoft instead of by us. It brings the
   Microsoft.IdentityModel packages with it. If not, the fallback is option C (no package, less capable).
2. **Which providers must work first** (and how do they deliver groups: in the ID token or only from
   userinfo)? It fixes the default scopes and claim names and whether option C is even usable.
3. **A provider for live verification.** Rule 3 needs a real flow in a browser. May I add a
   `docker-compose.sso.yml` overlay running a development provider container (an image, not a package; which
   one is your choice), or will you give me a tenant to test against?
4. **Just-in-time provisioning.** Off by default, on only with a group match (as written), or no JIT and
   administrators always pre-create users? The dashboard has no way to pre-create a record for a person it
   has not yet seen, so with JIT off and no linking (question 6) nobody new could sign in; one of the two is
   needed.
5. **Local sign-in once SSO is on.** Keep it for everyone (the default), restrict it to administrators
   (`LocalSignIn=false`, as written), or something else?
6. **Linking local accounts** (decision 8): wanted in the first release, later, or never?
7. **The last-administrator rule and the provider** (decision 9): skip-and-warn as written, or strict (a
   change at the provider that would leave no administrator refuses the sign-in)? And should the rule require
   at least one *local* administrator while SSO is on?
8. **Session lifetime.** Is a separate shorter `AbsoluteTimeoutHours` (8) right, and is back-channel logout
   wanted later?
9. **Sign-out at the provider.** Local only (as written), or end the provider session too?
10. **`PublicOrigin`.** Required explicit key (as written) or derived from the request through the trusted
    proxies?
11. **Usernames.** Is `preferred_username` then `email` the right default, and should an invalid username
    (a space, too long) be refused (as written) or replaced by a generated one?
12. **Device cookie.** Agree to keep it deferred behind this record (decision 10)?
13. **Machine tokens.** Agree that IdP-issued bearer tokens for scripts are out of scope here?

## What I could not verify

This record is design only; nothing was built or run, and no Docker was used. Taken from reading the code and
the framework documentation but not exercised: the OpenID Connect handler's default response mode and cookie
attributes (I have set them explicitly in the decision rather than rely on defaults); the browser behaviour of
a `Strict` session cookie across a redirect chain that began at the provider; how the previous build's
password hasher treats an empty stored hash; the package's current patched version and any advisory on its
transitive packages; and the group claim shapes of any particular provider.
