# Phase 7 Resource Management

Phase 7 adds the first user-facing resource management layer without deployment execution.

Implemented boundaries:

- `Vessel.Application.Resources.ResourceManagementService` owns project, environment, server, application, database, environment variable, secret, and registry credential use cases.
- API controllers remain thin and delegate to the Application service.
- Blazor pages render forms and lists only; they do not access EF Core, Docker, Git, SSH, or process APIs.
- `Vessel.Infrastructure.Security.AesSecretVault` stores secret payloads as AES-GCM ciphertext in `secret_values`; plaintext is not stored in Domain entities or returned by list APIs.
- `SecretReference` remains metadata and ownership only. Revealing a value requires `secrets.read` and records an audit event.
- Server connectivity checks currently validate the registered resource and persist a status snapshot. Runtime probing remains for the deployment/runtime phases.

Coolify behavior consulted:

- `app/Models/EnvironmentVariable.php`
- `app/Models/SharedEnvironmentVariable.php`
- `app/Models/Server.php`
- `app/Models/Application.php`

Compatibility choices:

- Environment variables preserve Coolify-style flags for build-time, runtime, preview, literal, multiline, and shared/reference semantics.
- Secret values are write-only by default in API/UI lists.
- Project/environment/server/application/database ownership remains team scoped.
- Registry credentials are stored as a named resource with a secret reference instead of exposing passwords.

Operational notes:

- Configure `Secrets:MasterKey` as a base64-encoded 32-byte key for durable encrypted secret storage. The alpha fallback key is only suitable for local development.
- The Phase 7 migration adds `environment_variables`, `secret_values`, `registry_credentials`, and `server_status_snapshots`, plus `projects.IsArchived` and `servers.Labels`.
