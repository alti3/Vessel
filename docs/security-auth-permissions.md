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

## Token Rules

Tokens are returned once as `<token-id>|<secret>`. Only the SHA-256 hash of the secret is stored. Tokens are scoped to the active team and can expire or be revoked.

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

## Audit

The Application layer records security events through `IAuditWriter`. Metadata keys containing `password`, `token`, or `secret` are redacted before persistence.
