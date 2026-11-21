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

OIDC, GitHub, and GitLab settings are represented in configuration without exposing client secrets from APIs or logs. External login callbacks should link to `User.ExternalSubject`.

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

## Audit

The Application layer records security events through `IAuditWriter`. Metadata keys containing `password`, `token`, or `secret` are redacted before persistence.
