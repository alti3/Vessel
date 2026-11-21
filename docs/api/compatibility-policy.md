# API contract and compatibility policy

Status: accepted (Wave 2, API-01). Applies to every HTTP endpoint served by `Vessel.Web`.

## 1. The contract

Vessel publishes a **Vessel-native** REST API. It is not a copy of Coolify's API:

- **Base path:** `/api/v1/...` for resource APIs. Identity endpoints that are session-oriented live under
  `/api/auth`, `/api/teams` and `/api/tokens`. Browser form posts (`/auth/*`, `/teams/invitations/accept`) and inbound
  webhooks (`/webhooks/{provider}`) are listed in the document but are not a client API.
- **Identifiers:** GUIDs (`3fa85f64-5717-4562-b3fc-2c963f66afa6`) in paths and bodies. Coolify uses opaque `uuid`
  strings. Clients must treat ids as opaque.
- **JSON:** `System.Text.Json` web defaults: camelCase properties, enums as declared by each DTO, ISO-8601 timestamps
  with an offset (UTC).
- **Errors:** errors raised by the platform use the `ApiErrorResponse` envelope:

  ```json
  { "error": { "code": "forbidden", "message": "You do not have access to this resource.",
               "details": { "correlationId": "..." } } }
  ```

  Codes in use include `forbidden` (403 for an application authorization denial; the message never says whether the
  resource exists in another team) and `unexpected_error` (500, with `correlationId` and `traceId`). Validation and
  conflict responses from controllers use RFC 7807 `ProblemDetails`. Clients should branch on the HTTP status first,
  then on `error.code` or `title`.
- **Verbs:** state changes use `POST`, `PUT`, `PATCH` or `DELETE`, never `GET`. This matches Coolify v4.5+, where the
  legacy `GET` start/stop routes return 405.
- **Authentication:** an API token (`Authorization: Bearer <token>`, security scheme `bearer`) or the interactive
  browser session (cookie `__Host-Vessel`, security scheme `cookie`). Throttling: `/api` has a per-client budget and
  failed requests are throttled (SEC-13). A throttled request receives 429 with `Retry-After`.

### Source of truth: `/openapi/v1.json`

The OpenAPI 3.1 document is generated at runtime from the controllers by the built-in ASP.NET Core OpenAPI support
(`Microsoft.AspNetCore.OpenApi`, `AddOpenApi`/`MapOpenApi`; see `src/Vessel.Web/OpenApi`). It is served at
**`GET /openapi/v1.json`** to **authenticated callers only**. A session or any valid token can read it. Anonymous
requests are challenged. The document lists every operation's access requirements:

| Extension | Meaning |
|---|---|
| `x-vessel-permissions` | Permission policies the operation requires (all of them), e.g. `projects.delete`. The full catalog is in `VesselPermissions`. |
| `x-vessel-token-abilities` | Token abilities that can satisfy those permissions, derived from the ability hierarchy (`write` ⊇ deploy, read:sensitive, read; `delete` ⊇ read; `deploy` ⊇ read; `root` ⊇ all). The token owner's team role, access grants and token scope limits still apply at runtime. |
| `x-vessel-session-only` | `true` when an interactive session is required. API tokens are rejected (identity management, instance administration, token lifecycle). |
| `x-vessel-policies` | Other named policies, e.g. `vessel.interactive-session-or-root-token`. |
| `x-vessel-anonymous` | `true` for unauthenticated endpoints (login, registration, password reset, webhooks). |

Every operation has per-operation `security`. `cookie` always applies. `bearer` is added unless the operation is
session-only. A contract test (`tests/Vessel.E2ETests/Api/OpenApiDocumentTests.cs`) fails the build when a mutating
action under `/api/v1`, `/api/teams` or `/api/tokens` declares no permission, session policy or `[AllowAnonymous]`.

## 2. Versioning

- **v1 is additive-only.** These changes are allowed within `v1`:
  - new endpoints;
  - new optional request fields;
  - new response fields;
  - new enum values in responses (clients must tolerate unknown values);
  - new error codes;
  - relaxed validation.
- **Breaking changes need a new major version** (`/api/v2/...`, document `/openapi/v2.json`). These count as breaking:
  - removing or renaming an endpoint, field or enum value;
  - changing a field's type or meaning;
  - making an optional input required;
  - tightening authorization so that previously valid ability and permission combinations fail.

  Exception: fixes for security vulnerabilities may tighten authorization inside v1. These fixes are announced in the
  changelog with the advisory.
- During a migration window, `v1` and `v2` are served side by side. A major version is supported for at least
  **12 months** after its successor is released.
- The `info.version` of the document is the Vessel release version. It is informational and is not the API major
  version.

## 3. Deprecation

1. A deprecated operation is marked `deprecated: true` in the OpenAPI document. Its responses carry
   `Deprecation: @<unix-time>` (RFC 9745) and, once a removal date is set, `Sunset: <HTTP-date>` (RFC 8594) with a
   `Link: <docs-url>; rel="deprecation"` header.
2. Minimum notice before removal: **6 months** for endpoints and fields within a major version, and never less than
   two minor releases.
3. Every deprecation and removal is recorded in the release notes with the replacement endpoint.
4. Deprecated operations must log their use with the token id (never the token value), so operators can find
   clients that still use them.

## 4. Coolify compatibility

Vessel does not bend its native API to Coolify's shape: UUID paths, `{uuid}/start` verbs, and per-channel
notification documents. Migration clients that need Coolify's paths can use a separate **Coolify-compatible facade**
under `/api/coolify/v1`, which is planned for Phase 17. The facade:

- translates Coolify UUIDs and request shapes to Vessel commands;
- maps Coolify abilities to Vessel abilities one-to-one (`read`, `read:sensitive`, `deploy`, `write`,
  `write:sensitive`, `delete`, `root`);
- returns Coolify-style `{"success": false, "message": ...}` denials.

The facade has its own OpenAPI document and does not follow this policy's v1 guarantees until it is declared stable.

### Endpoint mapping (Vessel ↔ Coolify v4.7 `/api/v1`)

Status: **implemented** means a Vessel endpoint exists (path and shape may differ). **Partial** means some of the
upstream behavior exists. **Planned** means it is on the roadmap. **Not planned** means it is out of scope for the
native API (it may still appear in the facade).

#### Applications

| Coolify v4.7 | Vessel | Status |
|---|---|---|
| `GET /applications` | `GET /api/v1/applications` | implemented |
| `POST /applications/{public,private-github-app,private-deploy-key,private-gitea-app,dockerfile,dockerimage}` | `POST /api/v1/applications` (source type in body) | partial |
| `GET/PATCH/DELETE /applications/{uuid}` | — | planned |
| `POST /applications/{uuid}/{start,stop,restart}` | `POST /api/v1/deployments` (deploy); lifecycle | partial |
| `GET /applications/{uuid}/logs` | — (deployment logs: `GET /api/v1/deployments/{id}/logs`) | planned |
| `GET/POST/PATCH /applications/{uuid}/envs`, `PATCH .../envs/bulk`, `DELETE .../envs/{env_uuid}` | `GET/POST /api/v1/environment-variables`, `GET .../{id}/reveal` | partial |
| `GET/POST/PATCH/DELETE /applications/{uuid}/storages[/...]` + storage backups | — | planned |
| `GET/POST/DELETE /applications/{uuid}/tags[/{tag_uuid}]` | — | planned |
| `POST /applications/{uuid}/{move,migrate,clone}` | — | planned |
| `POST /applications/{uuid}/rollback`, `GET .../rollback-images` | `POST /api/v1/applications/{id}/rollback`, `GET .../rollback-targets` | implemented |
| `GET/POST/DELETE /applications/{uuid}/destinations[/...]` | — | planned |
| `GET/POST/PATCH/DELETE /applications/{uuid}/previews[/{pr}]`, `GET .../logs` | `GET /api/v1/applications/{id}/previews[/{pr}]`, `DELETE .../{pr}`, `POST .../{pr}/{deploy,stop}` | partial |
| `GET/POST/PATCH/DELETE /applications/{uuid}/scheduled-tasks[/...]`, `.../execute`, `.../executions` | — | planned |
| `GET/POST /applications/{uuid}/dns`, `POST .../dns/replace`, `DELETE .../dns/{hostname}` | `GET/PUT/DELETE /api/v1/applications/{id}/domains[/{host}]`, `.../certificates[/issue]` | implemented (different shape) |
| `GET /applications/{uuid}/vulnerabilities` | — | planned |
| — | `GET/PATCH /api/v1/applications/{id}/deployment-settings`, `GET/PUT .../webhooks`, `GET .../git/refs` | Vessel-only |

#### Databases

| Coolify v4.7 | Vessel | Status |
|---|---|---|
| `GET /databases`, `POST /databases/{postgresql,mysql,mariadb,mongodb,redis,keydb,dragonfly,clickhouse,sqlite}` | `GET/POST /api/v1/databases` (engine in body) | partial (no sqlite) |
| `GET/PATCH/DELETE /databases/{uuid}` | — | planned |
| `POST /databases/{uuid}/{start,stop,restart}` | `POST /api/v1/databases/{id}/lifecycle/{action}` | implemented |
| `GET/POST /databases/{uuid}/backups`, `PATCH/DELETE .../backups/{id}`, `GET .../executions`, `DELETE .../executions/{id}` | `POST/GET /api/v1/databases/{id}/backups`, `GET .../backup-schedules`, `POST/PATCH/DELETE /api/v1/databases/backup-schedules[/{id}]`, `GET .../executions`, `DELETE /api/v1/databases/backups/{id}`, `GET .../download`, `POST .../restore/{target}` | implemented |
| `POST /databases/{uuid}/imports[/uploads]`, `GET .../imports/{activity}` | restore from backup only (`GET /api/v1/databases/restores/{id}`) | partial |
| `GET /databases/{uuid}/logs`, envs, storages, tags, `move/migrate/clone`, vulnerabilities | — | planned |

#### Services

| Coolify v4.7 | Vessel | Status |
|---|---|---|
| `GET/POST /services` | `GET /api/v1/service-templates`, `POST /api/v1/service-templates/resources` | partial |
| `GET/PATCH/DELETE /services/{uuid}`, `start/stop/restart`, `logs`, envs, storages, tags, `move/clone` | — | planned |
| `.../applications[/{app_uuid}]` and `.../databases[/{db_uuid}]` (lifecycle, logs, imports) | — | planned |
| `GET/POST /services/{uuid}/dns`, `DELETE .../dns/{hostname}` | `GET/PUT/DELETE /api/v1/services/{id}/domains` | implemented (different shape) |

#### Servers, proxy, destinations

| Coolify v4.7 | Vessel | Status |
|---|---|---|
| `GET/POST /servers`, `GET/PATCH/DELETE /servers/{uuid}` | `GET/POST /api/v1/servers`, `GET/PATCH/DELETE /api/v1/servers/{id}` | implemented |
| `GET/POST /servers/{uuid}/validate` | `POST /api/v1/servers/{id}/validate`, `POST .../connectivity-test` | implemented |
| `POST /servers/{uuid}/host-key/reset` | `POST /api/v1/servers/{id}/host-key/{trust,reset}` | implemented |
| `GET/PATCH /servers/{uuid}/proxy`, `PUT .../proxy/configuration`, `POST .../proxy/restart` | `GET/PATCH /api/v1/servers/{id}/proxy`, `POST .../proxy/{start,stop,restart}`, `/api/v1/servers/{id}/proxy-configurations[...]` | implemented |
| `GET /servers/{uuid}/domains`, `GET .../resources` | — | planned |
| `GET/POST/PATCH/DELETE /servers/{uuid}/envs[/...]` | — | planned |
| `GET/PATCH /servers/{uuid}/sentinel`, `POST /sentinel/push` | `GET /api/v1/server-health`, `POST .../{id}/poll`, `GET /api/v1/servers/{id}/snapshots` | partial (Vessel-native monitoring) |
| `GET/PATCH /servers/{uuid}/docker-cleanup`, `POST .../run`, `GET .../executions` | — | planned |
| `GET/PATCH /servers/{uuid}/log-drains` | — | planned |
| `GET/POST/DELETE /servers/{uuid}/registries[/...]`, `POST .../check` | `POST /api/v1/registry-credentials` | partial |
| `GET/PATCH /servers/{uuid}/image-scanning` | — | planned |
| `GET/PATCH /servers/{uuid}/cloudflare-tunnel`, `POST .../{enable,disable}` | — | planned |
| `GET /servers/{uuid}/transfer`, `POST .../transfer/complete`, `POST /servers/import`, `GET /servers/import/check`, `POST .../migrate` | — | planned |
| `GET/POST /servers/{uuid}/destinations`, `GET/PATCH/DELETE /destinations/{uuid}` | — | planned |
| `POST /servers/{hetzner,digitalocean,vultr,hostinger}`, provider catalog `GET`s, `/cloud-tokens[...]`, `/cloud-init-scripts[...]` | — | planned |
| — | `GET/PUT /api/v1/servers/{id}/deployment-limits`, `/api/v1/servers/{id}/certificates[...]` | Vessel-only |

#### Projects, environments, shared variables

| Coolify v4.7 | Vessel | Status |
|---|---|---|
| `GET/POST /projects`, `GET/PATCH/DELETE /projects/{uuid}` | `GET/POST /api/v1/projects`, `GET/PUT/DELETE /api/v1/projects/{id}` | implemented |
| `GET/POST /projects/{uuid}/environments`, `PATCH/DELETE .../{env}`, `GET /projects/{uuid}/{env}` | — (Blazor UI only) | planned |
| `/projects/{uuid}/envs[...]`, `/projects/{uuid}/environments/{env}/envs[...]`, `/team/envs[...]` | `/api/v1/environment-variables` (resource variables) | planned (shared variables) |
| `GET /resources` | `GET /api/v1/dashboard` | partial |

#### Deployments

| Coolify v4.7 | Vessel | Status |
|---|---|---|
| `GET/POST /deploy` (uuid or tag, `cancel_previous`) | `POST /api/v1/deployments` | partial (no tag fan-out) |
| `GET /deployments`, `GET /deployments/{uuid}` | `GET /api/v1/deployments`, `GET /api/v1/deployments/{id}` | implemented |
| `GET /deployments/applications/{uuid}` | — (`GET /api/v1/deployments` lists the team's deployments, unfiltered) | planned |
| `POST /deployments/{uuid}/cancel` | `POST /api/v1/deployments/{id}/cancel` | implemented |
| — | `GET /api/v1/deployments/{id}/logs`, `GET .../logs/export` | Vessel-only |

#### Team, members, tokens, identity

| Coolify v4.7 | Vessel | Status |
|---|---|---|
| `GET /teams`, `GET /teams/{id}` | `GET /api/teams` | partial |
| `GET/PATCH /team`, `GET /team/members`, `GET /teams/{id}/members` | `GET /api/teams/{id}/members`, `POST .../invitations`, `PUT .../members/{user}/{role,access}`, `DELETE .../members/{user}`, `POST /api/teams/{id}/switch` | partial (session-only) |
| `GET /teams/current`, `GET /teams/current/members` (removed upstream) | — | not planned |
| tokens (Coolify manages them in the UI only) | `GET/POST /api/tokens`, `PATCH/DELETE /api/tokens/{id}`, `POST .../{pause,resume,roll}` | Vessel-only |
| — (web routes) | `POST /api/auth/{login,register,logout}`, password reset, email verification, `POST/DELETE /api/auth/2fa[...]` | Vessel-only |
| — (web routes) | `GET /api/auth/sessions`, `DELETE /api/auth/sessions/{id}`, `POST /api/auth/sessions/revoke-others` | implemented (IAM-09, Vessel-only) |
| — (Coolify does OAuth via web routes) | `GET /api/auth/providers`, `GET /auth/oauth/{providerId}/start`, `GET /auth/oauth/{providerId}/callback` | implemented (IAM-07, Vessel-only) |
| — (instance settings UI) | `GET/POST /api/v1/instance/auth-providers`, `GET/PUT/DELETE /api/v1/instance/auth-providers/{id}` | implemented (IAM-07, Vessel-only) |
| `GET /audit-events` (search, action, source, `per_page` ≤ 100) | `GET /api/v1/audit-events` | implemented (IAM-08) |

#### Notifications

| Coolify v4.7 | Vessel | Status |
|---|---|---|
| `GET/PATCH /notifications/{email,discord,slack,telegram,pushover,webhook}` | `GET/PUT /api/v1/notifications/targets`, `POST .../targets/{id}/test`, `GET .../event-types` | implemented (generic targets, different shape) |
| — | `GET /api/v1/notifications`, `GET .../in-app`, `POST .../in-app/{id}/{read,archive}` | Vessel-only |

#### Settings, instance, security keys, integrations

| Coolify v4.7 | Vessel | Status |
|---|---|---|
| `GET/PATCH /settings/email` | `GET/PATCH /api/v1/settings/email`, `GET .../presets`, `POST .../test` | implemented |
| `GET/POST /enable`, `GET/POST /disable` (API on/off) | — | not planned (API availability is a deployment setting) |
| `POST /mcp/{enable,disable}` | — | planned (API-02) |
| — | `GET /api/v1/settings`, `GET/PUT /api/v1/instance/settings`, `GET/PUT/DELETE /api/v1/instance/admins[/{user}]`, `POST /api/v1/admin/secrets/reencrypt` | Vessel-only |
| `GET/POST /security/keys`, `GET/PATCH/DELETE /security/keys/{uuid}` | `GET/POST /api/v1/security/keys`, `GET/PATCH/DELETE .../{id}`, `POST .../generate`, `POST .../{id}/rotate` | implemented |
| `/security/integration-tokens[...]` (+ `sync-zones`), `GET /dns/zones` | — | planned |
| `GET/POST /s3-storages`, `GET/PATCH/DELETE /s3-storages/{uuid}`, `POST .../validate` | `GET/POST /api/v1/backup-destinations`, `GET/PATCH/DELETE .../{id}`, `POST .../{id}/validate` | implemented (different name) |
| `GET/POST /tags`, `PATCH/DELETE /tags/{uuid}` | — | planned |
| `/github-apps[...]`, `/gitlab-apps[...]`, `GET /gitea-apps` | — (inbound `POST /webhooks/{github,gitlab,gitea,bitbucket,generic}` only) | planned |
| `POST /feedback` | — | not planned |

#### Terminals, health, version

| Coolify v4.7 | Vessel | Status |
|---|---|---|
| — (Coolify terminal is websocket-only) | `POST /api/v1/terminals`, `GET .../{id}`, `POST .../{id}/{token,input,resize,close}` | Vessel-only |
| `GET /health` | `GET /live`, `GET /ready`, `GET /health` (not in the OpenAPI document) | implemented |
| `GET /version` | — | planned |

Track each **planned** row in its domain's roadmap phase. When an endpoint ships, update its row in this table in the
same change.
