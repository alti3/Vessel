# Authentication and Permissions

Vessel uses a custom identity model backed by PostgreSQL. Passwords are hashed with Argon2id through `Konscious.Security.Cryptography.Argon2`; the ASP.NET Core Identity password hasher is not used.

## Authentication

Supported server-side flows:

- email/password registration
- login and logout
- lockout after repeated failed login attempts
- password reset token storage
- TOTP two-factor setup, confirmation, disablement, and recovery codes
- personal/API token creation, listing (active team), editing, pausing, rolling, revocation, abilities, scope limits, expiration, and bearer authentication
- team invitations, acceptance, member removal, role changes, and team switching

External sign-in (OIDC, GitHub, GitLab, Google) is described in [External Sign-In (IAM-07)](#external-sign-in-iam-07). `User.ExternalSubject` is deprecated and no longer used.

## Identity-Management Endpoints

API tokens authenticate automation; they do not manage identity. These endpoints are protected by dedicated policies (`VesselAuthorizationPolicies`):

| Endpoint | Policy |
| --- | --- |
| `GET/POST/DELETE /api/tokens` | `vessel.interactive-session-or-root-token` (cookie session, or a bearer token with `root`) |
| `POST /api/teams/{id}/switch`, invitations, role changes, member removal | `vessel.interactive-session` (cookie session only) |
| `POST /api/auth/2fa/setup`, `POST /api/auth/2fa/confirm`, `DELETE /api/auth/2fa` | `vessel.interactive-session` |

A principal is an interactive session only if it was not authenticated by the bearer handler and carries no token claims (`vessel:token_id`, `vessel:scope`, `vessel:permission`). Cookie sessions are always issued from an explicit claim allow-list (`VesselSessionPrincipal`); switching teams re-issues the session for the new team and never copies the caller's claims, so a session can neither outlive nor exceed a token.

Two-factor rules:

- `DELETE /api/auth/2fa` requires re-confirmation with a body of `{ "code": "<current TOTP>" }` or `{ "currentPassword": "..." }`. Missing or invalid confirmation returns `403` and leaves 2FA enabled. Wrong passwords count towards the login lockout.
- `POST /api/auth/2fa/setup` returns `409` while 2FA is enabled; disable it (with re-confirmation) before enrolling a new authenticator.

## Token Rules

Tokens are returned once as `<token-id>|<secret>`. Only the SHA-256 hash of the secret is stored. Tokens belong to one team (the active team when created), are listed per active team (`GET /api/tokens`), and can expire, be paused, be rolled or be revoked.

Abilities (IAM-04, Coolify v4.6 parity) are access levels with an implication hierarchy:

| Ability | Implies |
| --- | --- |
| `read` | read (secrets stay hidden) |
| `read:sensitive` | read + secrets/env values |
| `deploy` | read + deploy, cancel, start/stop/restart (`resources.lifecycle`) |
| `write` | read, read:sensitive, deploy + change settings and create resources |
| `delete` | read + delete (every destructive `DELETE` endpoint needs `*.delete`) |
| `root` | everything |
| `write:sensitive` | owner/admin-only extra ability (secret writes, future instance e-mail settings) |

Unknown abilities are rejected. **Migration data step:** every stored token that holds `write` also receives `delete` (`TokenScopeMapper.MigrateLegacyScopes`, upstream `2026_10_25_100000`); all other abilities keep their meaning.

Issuance and edits (`VesselTokenService.CreateAsync` / `UpdateAsync`):

- The issuer must be a member of the team; `root` and `write:sensitive` require Owner or Admin.
- The token's abilities applied to its scope limits (or the whole team) must be held by the issuer's current role **and grants** (a token never exceeds its owner).
- Optional scope limits (`access`: team/project/environment/resource grants) must point inside the team.
- A token-authenticated caller must hold `root`, have no scope limits, and may only mint abilities its own token covers.
- Expiration must be in the future. `PATCH /api/tokens/{id}` edits name, abilities, scope limits and expiry (secret unchanged); `POST /api/tokens/{id}/pause|resume|roll` pause, resume or issue a new secret with the same metadata. These lifecycle endpoints require an interactive session.

Validation (every bearer request):

- The token must exist, match its hash, and be neither revoked, expired nor paused.
- The owner must still be a member of the token's team; removing **or demoting** a member revokes their tokens for that team (promotions keep them).
- Effective permissions are the token's abilities intersected with what the owner holds **today** (role and grants). Application authorization also applies the token as a second layer (`IApiTokenAccessContext`), so scope limits are enforced on every `Demand` call, not only at endpoint attributes. A bearer token never falls back to its owner's role.

## Permission Catalog

`VesselPermissions.Rules` maps each policy to an access level and a reach:

| Permission | Level | Reach |
| --- | --- | --- |
| `projects.read`, `servers.read`, `applications.read`, `deployments.readLogs` | read | located |
| `projects.write`, `applications.write`, `secrets.write` | write | located |
| `projects.delete`, `applications.delete` | delete | located |
| `deployments.start`, `deployments.cancel`, `resources.lifecycle` | deploy | located |
| `secrets.read` | read:sensitive | located |
| `servers.write`, `notifications.write` | write | team-wide |
| `notifications.read` | read | team-wide |
| `settings.manage` | write | team admin (Owner/Admin role) |
| `teams.manage`, `terminals.open` | root | team admin (Owner/Admin role) |
| `instance.manage` | root | instance administrator |

"Located" permissions can be granted on a team, project, environment or resource; "team-wide" ones need a team-wide grant; "team admin" ones need the Owner or Admin role regardless of grants.

## Roles and Fine-Grained Access (IAM-03, IAM-05)

- **Owner / Admin:** unrestricted in their team.
- **Member (breaking change, Coolify v4.2 parity):** read-only by default (team-wide `read`). Members can no longer deploy or cancel unless a grant gives them `deploy`.
- **Role hierarchy:** only owners and admins manage members; nobody can invite, promote to, demote from, or remove a role above their own. Admins therefore cannot create owners, promote themselves or others to owner, or demote/remove owners. Every role change resets the member's grants.
- **Grants** (`team_memberships.access_grants`, `team_invitations.access_grants`, `personal_access_tokens.access_grants`, jsonb): `{scope: team|project|environment|resource, target, levels[]}`. A grant on a parent applies to its children and levels add up; `null` means the default. Presets: `viewer` (team read), `developer` (write + deploy on one project), `operator` (team write, deploy, delete), `full` (team root). Edit with `PUT /api/teams/{team}/members/{user}/access`; invitations accept the same `access` payload.
- **Evaluation:** `VesselAuthorizationService` delegates to `IAccessResolver`: the member layer and, for bearer requests, the token layer must both allow the level at the target's location. Creating projects/servers and other team-level operations need team-wide access. A scoped member may use a server only when it runs a resource they can reach.
- **Lists:** project, environment, application, database, server and deployment lists (`ResourceManagementService`, `AccessScopedCatalog`) show only what the actor can reach (parents of granted children are visible, siblings are not).
- **Sensitive responses:** environment-variable values (plain and secret) are masked unless the actor and the token hold `read:sensitive` (`CanReadSensitive`).
- Deviation from upstream: team-level `/api/v1/*` routes are not blocked wholesale for scoped tokens; each endpoint's reach (team-wide permissions, filtered lists) enforces the same outcome.

## Resource-Scoped Authorization

Every check on an existing resource goes through `VesselAuthorizationService.Demand`/`IsAuthorized(user, activeTeam, ResourceRef, permission)`. The service resolves the team that **owns** the resource (project, environment, server, application, deployment or database), requires it to be the caller's active team (the session's selected team or the token's team), and evaluates the caller's access in that team. Membership in any other team, including the caller's personal team, never authorizes access, and a token never works outside its own team. Missing resources and resources of another team fail identically ("... is outside the active team."). Application authorization failures are answered with `403`. Realtime group joins and environment-variable targets follow the same rule. The former team-agnostic `CanAccessProject/Server/Application/Deployment` helpers were removed; a unit test guards against reintroducing them.

## Instance Administrators and Registration (IAM-06)

`users.IsInstanceAdmin` is an instance-level privilege independent of team roles (the Coolify "root"/instance admin). The first registered user becomes the instance administrator.

- **Registration policy:** the instance setting `registration_enabled` (system-scoped `settings` row) defaults to **closed once the first user exists**. While closed, only the bootstrap account and emails with a pending, unexpired team invitation can self-register; others get `403`.
- **Instance endpoints** (interactive session of an instance administrator, `instance.manage`): `GET|PUT /api/v1/instance/settings`, `GET /api/v1/instance/admins`, `PUT|DELETE /api/v1/instance/admins/{userId}`. The last instance administrator cannot be demoted. Team owners are not instance administrators.

Instance administrators are required for host-local execution, which is host-root-equivalent:

- creating servers with a `Local` or `DockerSocket` connection;
- creating applications, databases, or services on such servers, and starting deployments on them;
- connectivity tests and manual health polls of such servers;
- opening host terminals;
- secret key-ring re-encryption.

Unattended work (webhook deployments, scheduled health polling) on a host-local server runs only when an Owner/Admin of the owning team is an instance administrator; otherwise deployments fail and polling records `Unknown` without touching the host runtime.

## Abuse Controls (SEC-13, SEC-07)

- **Global API budget:** every `/api` route shares the `Vessel:RateLimits:Api` fixed window per client address; named policies (`auth`, `terminal`, `webhooks`) still apply on top. All auth endpoints carry the `auth` policy.
- **Failed-request throttle:** 401/403 (and 404 without a valid token) count per token, or per client address without a valid token. After `Vessel:RateLimits:ApiFailedRequests:PermitLimit` (default 30, `0` disables) failures per `WindowSeconds` (60) the client gets `429` with `Retry-After` (`api_failed_request_rate_limit`) for 1 minute, then 5 and 15 minutes on repeat strikes within an hour. The token's team receives at most one `security.api_token_failing` notification per day.
- **Webhooks:** new or changed manual webhook secrets need 16-512 characters (existing rows keep working). Distinct failed signature/token attempts are counted per provider + client address + repository + branch (repeats count once; markers are keyed hashes); after 30 per minute further failures get `429`, but correctly signed deliveries are always processed.
- **Real client address:** `Vessel:ForwardedHeaders` (`Enabled`, `KnownProxies`, `KnownNetworks` in CIDR, `ForwardLimit`). Forwarded headers are honoured only from the listed proxies; the framework's loopback defaults are cleared, so nothing is trusted unless configured. Counters live in Redis when enabled, otherwise in-process.

## External Sign-In (IAM-07)

Instance administrators configure providers at `GET/POST/PUT/DELETE /api/v1/instance/auth-providers[/{id}]` (interactive session + `instance.manage`). Kinds: `Oidc` (any issuer with discovery), `GitHub` (github.com or a GitHub Enterprise base URL), `GitLab` (gitlab.com or self-managed, through OIDC), `Google` (OIDC).

- **Secrets:** the client secret is write-only. It is stored as a key-ring envelope (`ISecretCipher.Protect`, purpose `auth-provider.client-secret`), included in secret re-encryption, and never returned or audited. Provider URLs must pass the outbound URL policy (`Vessel:OutboundRequests`); internal identity providers have to be allow-listed there.
- **Flow:** `GET /auth/oauth/{id}/start` redirects to the provider with authorization code + PKCE (S256), `state` and (OIDC) `nonce`, kept in a 10-minute data-protected cookie. The callback `GET /auth/oauth/{id}/callback` is built from `Vessel:Host:PublicUrl` and never from the Host header; without a public URL external sign-in is unavailable. OIDC endpoints come from discovery and the discovered issuer must equal the configured one. The ID token comes straight from the token endpoint over TLS; `iss`, `aud`, `exp` (60 s skew) and `nonce` are validated (OIDC Core 3.1.3.7 (6)). GitHub uses the primary email of `/user/emails` and its `verified` flag.
- **Identities:** `oauth_identities` is keyed by provider instance + subject (unique), stores the issuer, and allows one linked identity per account. A known subject signs in whatever email the provider reports now; an identity seen under a different issuer is refused (`oauth_issuer_mismatch`).
- **Linking:** an existing account (matched by email) is linked only when the provider verified the email (`oauth_email_unverified`), and only when it has no linked identity yet (`oauth_already_linked`). With `requireVerifiedEmail` (default on) every sign-in needs a verified email.
- **Registration:** new accounts are created only when instance registration is open, the address has a pending invitation, or the provider's `allowRegistration` is on; otherwise `registration_disabled`. Invitations are never accepted implicitly. `autoJoinTeamId` adds new accounts to that team as **members**.
- **Domains:** `allowedDomains` restricts Google to those Workspace (`hd`) domains (`*` = any Workspace, no consumer accounts) and other providers to verified emails in those domains (`oauth_domain_not_allowed`).
- **Second factor:** accounts with 2FA get no session after the provider callback; a 5-minute pending cookie leads to `/login/two-factor`, and the TOTP or a recovery code is checked (`POST /auth/oauth/two-factor`, wrong codes count towards lockout).
- Denials redirect to `/login?error=<reason>` and are audited at warning level. The enabled providers are listed anonymously at `GET /api/auth/providers` for the login page.

## Sessions and the Active Team (IAM-09)

- Every interactive sign-in creates a server-side `user_sessions` row; the cookie carries its id (`vessel:session_id`). Each request re-validates the row (cached for 15 seconds per session); Blazor circuits re-check every minute. Cookies issued before this change have no session id and must sign in again.
- Sessions slide for `Vessel:Auth:SessionIdleDays` (14). Revoked and expired rows are deleted after `SessionRetentionDays` (30) by the daily `auth.sessions.prune` job.
- `GET /api/auth/sessions`, `DELETE /api/auth/sessions/{id}` and `POST /api/auth/sessions/revoke-others` (interactive sessions only) list and revoke the caller's sessions. Logout revokes the current session.
- A password reset revokes every session and every API token of the user. Enabling or disabling 2FA revokes the user's other sessions.
- The active team lives on the session row. Login lands in `users.CurrentTeamId` while the user is still a member, otherwise in the personal team (never a team the user is only invited to). Switching teams updates the session and remembers the team on the user, so a request that started before the switch cannot restore the old team. When a member is removed, their remembered team is cleared and their sessions move to their home team on the next request.

## Audit (IAM-08)

The Application layer records events through `IAuditWriter`:

- **Transactions:** `StageAsync` adds the row to the current unit of work so it commits with the change it records (and disappears if the change rolls back). `RecordAsync` stages and saves at once, for events without their own change (failed logins, denials). Auth, team, token, session and provider services stage their audit rows.
- **Context:** `source` (`ui`, `api` for bearer tokens, `webhook`, `job`, `system`), `actor_type` (`user`, `api_token`, `webhook`, `job`, `system`), actor name, API token id/name, trusted client address (forwarded headers only from configured proxies) and user agent (200 characters), level (`info`/`warning`/`error`; failures, denials, rejections and throttling default to warning), resource name, description and a `changes` before/after diff.
- **Redaction** (by key and by value): credential-like keys keep no value, content keys (command, compose, env, log, output, script, payload...) are omitted, email addresses are masked, and every string has URL credentials, bearer tokens, private keys, JWTs and known token formats removed. Sensitive fields in `changes` are recorded as changed without values. Failed logins and password-reset requests for unknown accounts use a one-way `email_hash` subject instead of the address.
- **Query:** `GET /api/v1/audit-events` (`audit.read`: owners and admins of the active/token team) with `search`, `action`, `source`, `level`, `actor_id`, `from`, `to`, `page`, `per_page` (≤ 100). Token id, client address, user agent, metadata, changes and correlation id are returned only to callers that may read sensitive data (sessions of owners/admins, `read:sensitive`/`root` tokens).
- **Retention:** the daily `audit.retention` job deletes events older than `Vessel:Audit:RetentionDays` (90; `0` keeps them).
- **Deleted users:** `AuditAnonymizationService.AnonymizeUserAsync` replaces the actor name, client address and user agent of the user's events, and their name in events about them, with `[deleted user]`; account deletion (IAM-13) must call it.

## API Token Expiry Warnings (IAM-04)

The hourly `auth.token-expiry-warnings` job raises `api_token.expiring` (Coolify `api_token_expiring`) for the token's team 7 days before a token expires, or 24 hours before for tokens that live 7 days or less. Each token is warned once (`ExpirationWarningSentAt`); editing the expiry re-arms it. Tokens whose owner left the team are skipped.
