# Authentication and Permissions

Vessel uses a custom identity model backed by PostgreSQL. Passwords are hashed with Argon2id through `Konscious.Security.Cryptography.Argon2`; the ASP.NET Core Identity password hasher is not used.

## Authentication

Supported server-side flows:

- email/password registration
- login and logout
- lockout after repeated failed login attempts
- password reset token storage
- TOTP two-factor setup, confirmation, disablement, and recovery codes
- personal/API token creation, listing, revocation, scopes, expiration, and bearer authentication
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

Tokens are returned once as `<token-id>|<secret>`. Only the SHA-256 hash of the secret is stored. Tokens are scoped to the active team and can expire or be revoked.

Issuance (`VesselTokenService.CreateAsync`):

- The issuer must be a member of the team. Every requested scope must be covered by the issuer's role in that team; `root` and `write:sensitive` require Owner or Admin.
- A token-authenticated caller must hold `root`, and may only mint scopes its own token covers. A token can therefore never mint a token with more abilities than itself or its owner.
- Expiration must be in the future.

Validation (every bearer request):

- The token must exist, match its hash, and be neither revoked nor expired.
- The owner must still be a member of the token's team; removing a member also revokes their tokens for that team.
- Effective permissions are the token's scope permissions intersected with the owner's **current** role, and the permission policy additionally re-checks the role. A bearer token never falls back to its owner's role for permissions its scopes do not grant.

Supported token scopes:

- `root`
- `read`
- `read:sensitive`
- `write`
- `write:sensitive`
- `deploy`

## Permission Catalog

Vessel uses explicit policy names:

- `projects.read`
- `projects.write`
- `servers.read`
- `servers.write`
- `applications.read`
- `applications.write`
- `deployments.start`
- `deployments.cancel`
- `deployments.readLogs`
- `terminals.open`
- `secrets.read`
- `secrets.write`
- `settings.manage`
- `teams.manage`

Owners and admins receive all permissions. Members receive read-oriented resource permissions plus deployment start/cancel and deployment log access. Secret read/write requires explicit permission and must not be inferred from route IDs alone.

## Resource-Scoped Authorization

Every check on an existing resource goes through `VesselAuthorizationService.Demand`/`IsAuthorized(user, activeTeam, ResourceRef, permission)`. The service resolves the team that **owns** the resource (project, environment, server, application, deployment or database), requires it to be the caller's active team (the session's selected team or the token's team), and evaluates the permission with the caller's role in that team. Membership in any other team, including the caller's personal team, never authorizes access. Missing resources and resources of another team fail identically ("... is outside the active team."). Realtime group joins and environment-variable targets follow the same rule. The former team-agnostic `CanAccessProject/Server/Application/Deployment` helpers were removed; a unit test guards against reintroducing them.

## Audit

The Application layer records security events through `IAuditWriter`. Metadata keys containing `password`, `token`, or `secret` are redacted before persistence.
