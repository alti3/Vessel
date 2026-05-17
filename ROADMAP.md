# Vessel Roadmap

This roadmap converts the product and architecture requirements into implementation phases. It is intended to double as a working checklist for planning, delivery tracking, reviews, and release readiness.

Sources:

- [PROJECT_REQUIREMENTS.md](./PROJECT_REQUIREMENTS.md)
- [README.md](./README.md)
- [AGENTS.md](./AGENTS.md)

## Status Legend

Use the `Status` column as a checkbox:

- `[ ]` Not started
- `[~]` In progress
- `[x]` Complete
- `[!]` Blocked or needs decision
- `[?]` Needs research or validation

## Phase Gates

Each phase should satisfy these gates before being considered complete:

- Architecture boundaries remain valid: Web -> Application -> Domain, Infrastructure implements Application/Domain abstractions.
- No deployment, Docker, Git, SSH, EF Core, or process execution logic leaks into Blazor components, controllers, SignalR hubs, Hangfire jobs, or Domain.
- External command execution goes through `IProcessRunner`.
- Secrets are redacted from logs, API responses, SignalR messages, exceptions, and audit records.
- Behavior changes have focused tests when the relevant test project exists.
- Public behavior, operations, deployment shape, or architecture decisions are documented.
- Narrowest useful verification command has been run and recorded in notes.

---

## Phase 0: Product Framing and Repository Governance

Goal: Establish the operating model, project identity, architecture commitments, and contribution rules before production code grows.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [x] | 0.01 | Product | Confirm project name, purpose, and rewrite philosophy | README states Vessel is a ground-up C#/.NET rewrite of Coolify, not a line-by-line port | None | README and requirements confirm this. |
| [x] | 0.02 | Product | Confirm executable-first control-plane strategy | Docs state host-native daemon is preferred; Dockerized control plane is secondary | None | README and requirements confirm this. |
| [x] | 0.03 | Product | Confirm modular monolith strategy | Docs forbid initial microservice split unless explicitly requested | None | README, requirements, and ADR-0001 confirm this. |
| [x] | 0.04 | Product | Maintain complete requirements document | `PROJECT_REQUIREMENTS.md` captures runtime, architecture, UI, API, jobs, security, operations, testing, and migration strategy | None | Existing requirements document retained. |
| [x] | 0.05 | Governance | Maintain agent engineering rules | `AGENTS.md` captures coding boundaries, process execution rules, persistence rules, and verification expectations | None | Existing agent rules retained. |
| [x] | 0.06 | Governance | Define repository roadmap | `ROADMAP.md` exists and is updated when scope changes | None | Roadmap updated with Phase 0/1 status. |
| [x] | 0.07 | Governance | Add license | License file exists and README references it | Product decision | MIT license added after confirming Coolify uses Apache-2.0 and this is a ground-up rewrite. |
| [x] | 0.08 | Governance | Define contribution workflow | CONTRIBUTING or docs explain build, test, coding, issue, and PR expectations | Solution scaffold | `CONTRIBUTING.md` added. |
| [x] | 0.09 | Governance | Define security reporting process | SECURITY document describes supported versions and vulnerability reporting | Product decision | `SECURITY.md` added with early-alpha reporting guidance. |
| [x] | 0.10 | Governance | Define ADR process | `docs/decisions/` contains ADR template and naming convention | Docs scaffold | ADR README and template added. |
| [x] | 0.11 | Governance | Create initial ADRs | ADRs exist for modular monolith, Blazor Web App, Hangfire, process runner, PostgreSQL, SignalR, executable-first distribution | ADR process | ADR-0001 through ADR-0007 added. |
| [x] | 0.12 | Planning | Define release maturity labels | Alpha, beta, release candidate, and stable criteria are documented | Product decision | `docs/release/maturity.md` added. |

---

## Phase 1: Solution Scaffold and Build Foundation

Goal: Create the .NET solution structure and build policy without starting production implementation before the stable .NET 11 SDK is available.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [x] | 1.01 | Runtime | Verify stable .NET 11 SDK availability | Installed SDK is stable GA; exact SDK version is recorded | .NET 11 GA | User explicitly approved .NET 11 preview use. Local `dotnet --info` exposes SDK `11.0.100-preview.4.26230.115`; replace with stable SDK after GA. |
| [x] | 1.02 | Runtime | Add `global.json` | Pins stable .NET 11 SDK with `rollForward: latestFeature` | 1.01 | Added `global.json` for .NET 11 preview `11.0.100-preview.4.26230.115` with `allowPrerelease: true`; replace with stable SDK after GA. |
| [x] | 1.03 | Build | Create solution file | Solution includes Web, Application, Domain, Infrastructure, Shared, UnitTests, IntegrationTests, E2ETests | 1.01 | `Vessel.slnx` added with all projects. |
| [x] | 1.04 | Build | Add `Directory.Build.props` | Nullable enabled, implicit usings configured, warnings policy set, deterministic builds enabled where practical | 1.03 | `Directory.Build.props` added. |
| [x] | 1.05 | Build | Add `Directory.Packages.props` | Central package management configured for all projects | 1.03 | `Directory.Packages.props` added. |
| [x] | 1.06 | Build | Create `Vessel.Domain` | Targets `net11.0`; has no forbidden dependencies | 1.03 | Project created; target inherited from build props. |
| [x] | 1.07 | Build | Create `Vessel.Shared` | Targets `net11.0`; remains lightweight | 1.03 | Project created; target inherited from build props. |
| [x] | 1.08 | Build | Create `Vessel.Application` | References Domain and Shared only | 1.06, 1.07 | Project created with allowed references. |
| [x] | 1.09 | Build | Create `Vessel.Infrastructure` | References Application, Domain, and Shared; no Web reference | 1.06, 1.07, 1.08 | Project created with allowed references. |
| [x] | 1.10 | Build | Create `Vessel.Web` | References Application, Infrastructure, Shared; hosts ASP.NET Core | 1.08, 1.09 | ASP.NET Core host project created with allowed references. |
| [x] | 1.11 | Tests | Create unit test project | Unit tests can reference Domain/Application as appropriate | 1.03 | `Vessel.UnitTests` added. |
| [x] | 1.12 | Tests | Create integration test project | Integration tests can exercise infrastructure with controlled dependencies | 1.03 | `Vessel.IntegrationTests` added. |
| [x] | 1.13 | Tests | Create E2E test project | Playwright or equivalent E2E structure is ready | 1.03 | `Vessel.E2ETests` added with Playwright package reference. |
| [x] | 1.14 | Build | Validate project references | Forbidden references fail review or build validation | 1.06-1.10 | `tools/validate-project-references.ps1` added and passed locally. |
| [x] | 1.15 | Build | Add solution build verification | `dotnet restore`, `dotnet build`, `dotnet test` succeed when SDK exists | 1.03-1.13 | `dotnet restore Vessel.slnx`, `dotnet build Vessel.slnx --no-restore`, `dotnet test Vessel.slnx --no-build`, format check, and project reference validation pass locally with .NET 11 preview. |
| [x] | 1.16 | Repo | Create top-level folders | `src/`, `tests/`, `deploy/`, `docs/`, and `tools/` exist | None | Folders added with tracked placeholders where otherwise empty. |
| [x] | 1.17 | Repo | Add editor and formatting policy | `.editorconfig` or equivalent enforces C# style, newline, charset, and analyzers | 1.03 | `.editorconfig` added. |
| [x] | 1.18 | CI | Add initial CI workflow | Restore, build, test, and formatting checks run on supported branches | 1.15 | `.github/workflows/ci.yml` added for project reference validation, restore, format check, build, and test. |

---

## Phase 2: Web Host, Configuration, Diagnostics, and Health

Goal: Establish the ASP.NET Core host, configuration model, diagnostics baseline, and health endpoints.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 2.01 | Web Host | Create `Program.cs` composition root | Web host starts with clear service registration and endpoint mapping | Phase 1 |  |
| [ ] | 2.02 | Config | Add appsettings files | Base and development settings exist without secrets | 2.01 |  |
| [ ] | 2.03 | Config | Establish environment names | Development, Staging, Production, Testing are supported and documented | 2.02 |  |
| [ ] | 2.04 | Config | Add strongly typed options pattern | Options classes validate at startup for core infrastructure settings | 2.02 |  |
| [ ] | 2.05 | Logging | Integrate Serilog | Structured logs include environment, service, correlation, and safe exception data | 2.01 |  |
| [ ] | 2.06 | Observability | Integrate OpenTelemetry baseline | HTTP traces and logs correlation are configured | 2.01 |  |
| [ ] | 2.07 | Health | Add liveness endpoint | `/live` is lightweight and does not depend on external services | 2.01 |  |
| [ ] | 2.08 | Health | Add readiness endpoint | `/ready` checks PostgreSQL, Redis, Hangfire storage, and object storage when enabled | Infrastructure config |  |
| [ ] | 2.09 | Health | Add general health endpoint | `/health` returns safe aggregate status | 2.07, 2.08 |  |
| [ ] | 2.10 | Errors | Add API error model | Error responses use stable code/message/details shape and do not expose stack traces | 2.01 |  |
| [ ] | 2.11 | Errors | Add exception handling middleware | Unexpected failures are logged; user-facing responses are safe | 2.10 |  |
| [ ] | 2.12 | Security | Add secure headers baseline | Production responses include appropriate security headers | 2.01 |  |
| [ ] | 2.13 | Security | Add rate limiting baseline | Auth, webhooks, public API, and terminal endpoints can be limited | 2.01 |  |
| [ ] | 2.14 | Docs | Document local development config | README or docs explain required services and non-secret settings | 2.02 |  |

---

## Phase 3: Domain Core and Persistence Model

Goal: Build the source-of-truth model for teams, users, projects, servers, applications, databases, environments, deployments, secrets metadata, audit logs, and settings.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 3.01 | Domain | Define strongly typed ID pattern | IDs are serialization-safe and usable with EF mappings | Phase 1 |  |
| [ ] | 3.02 | Domain | Implement Team/Tenant aggregate | Team identity, membership boundary, roles, and ownership rules exist | 3.01 |  |
| [ ] | 3.03 | Domain | Implement User model boundary | User identity integrates with auth without coupling Domain to ASP.NET Identity | 3.01 |  |
| [ ] | 3.04 | Domain | Implement Project aggregate | Projects belong to teams and own environments/resources | 3.02 |  |
| [ ] | 3.05 | Domain | Implement Environment model | Production, staging, preview, and custom environment semantics are represented | 3.04 |  |
| [ ] | 3.06 | Domain | Implement Server aggregate | Server address, connection type, runtime capabilities, and status state exist | 3.02 |  |
| [ ] | 3.07 | Domain | Implement Application aggregate | Git source, build config, runtime config, deployment settings, domains, and env refs exist | 3.04, 3.05, 3.06 |  |
| [ ] | 3.08 | Domain | Implement Database resource aggregate | Database type, version, storage, credentials reference, backups, and health state exist | 3.04, 3.06 |  |
| [ ] | 3.09 | Domain | Implement Deployment aggregate | Deployment status, lifecycle transitions, actor, commit, logs, artifacts, rollback refs exist | 3.07 |  |
| [ ] | 3.10 | Domain | Implement Deployment state machine | Valid transitions are enforced and covered by unit tests | 3.09 |  |
| [ ] | 3.11 | Domain | Implement Secret metadata model | Secret values are not stored in Domain entities; references and policy are modeled | 3.02 |  |
| [ ] | 3.12 | Domain | Implement Notification target model | Channels, delivery policy, and ownership exist without provider coupling | 3.02 |  |
| [ ] | 3.13 | Domain | Implement Audit log model | Actor, action, target, correlation, timestamp, and redacted metadata are represented | 3.02 |  |
| [ ] | 3.14 | Domain | Implement Settings model | System, team, project, and resource settings boundaries are clear | 3.02 |  |
| [ ] | 3.15 | Domain | Implement value objects | Names, ports, domains, image tags, versions, server addresses, repository URLs, and resource limits validate invariants | 3.01 |  |
| [ ] | 3.16 | Domain | Implement domain events | Important changes emit events without infrastructure references | 3.02-3.14 |  |
| [ ] | 3.17 | Persistence | Add EF Core DbContext | PostgreSQL-backed context exists in Infrastructure | Phase 1 |  |
| [ ] | 3.18 | Persistence | Add entity configurations | EF mappings keep Domain free of EF attributes unless approved | 3.17 |  |
| [ ] | 3.19 | Persistence | Add initial migration | Schema covers core aggregates, indexes, constraints, and concurrency tokens | 3.17, 3.18 |  |
| [ ] | 3.20 | Persistence | Add repositories or query abstractions | Application depends on interfaces, Infrastructure implements them | 3.17 |  |
| [ ] | 3.21 | Persistence | Add optimistic concurrency where needed | Mutable configuration, memberships, and deployment records protect concurrent changes | 3.19 |  |
| [ ] | 3.22 | Tests | Add domain unit tests | Value objects, invariants, state transitions, and permissions are covered | 3.01-3.16 |  |
| [ ] | 3.23 | Tests | Add persistence integration tests | Mappings, constraints, indexes, and migration application are covered | 3.17-3.21 |  |

---

## Phase 4: Authentication, Authorization, Tenancy, and Audit

Goal: Build secure identity, team membership, permissions, resource authorization, and audit trails.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 4.01 | Auth | Choose Identity integration strategy | ASP.NET Core Identity or documented custom identity model is selected | Phase 3 |  |
| [ ] | 4.02 | Auth | Implement email/password auth | Registration or admin-created users, login, logout, lockout, and password reset work | 4.01 |  |
| [ ] | 4.03 | Auth | Implement 2FA and recovery codes | Users can enable, verify, disable, and recover 2FA safely | 4.02 |  |
| [ ] | 4.04 | Auth | Implement OIDC support | External OIDC providers can be configured without leaking secrets | 4.01 |  |
| [ ] | 4.05 | Auth | Implement GitHub/GitLab OAuth | Provider login supports team policy and account linking | 4.04 |  |
| [ ] | 4.06 | Auth | Implement personal access tokens | Token create, list, revoke, scope, expiration, and audit flows exist | 4.02 |  |
| [ ] | 4.07 | Auth | Implement API tokens | Machine-oriented tokens support explicit scopes and expiration | 4.06 |  |
| [ ] | 4.08 | Tenancy | Implement team membership management | Invite, accept, remove, role change, ownership transfer, and audit flows exist | 4.02 |  |
| [ ] | 4.09 | Authorization | Define permission catalog | Explicit permissions exist for projects, servers, apps, deployments, logs, terminals, secrets, settings, teams | 4.08 |  |
| [ ] | 4.10 | Authorization | Implement policy-based authorization | Web, API, hubs, and application commands use policies | 4.09 |  |
| [ ] | 4.11 | Authorization | Implement resource authorization | Tenant/team/project/server/app/database/environment access checks are enforced beyond route IDs | 4.10 |  |
| [ ] | 4.12 | Authorization | Implement secrets access policy | Reading secrets requires explicit permission; UI defaults to masked values | 4.11 |  |
| [ ] | 4.13 | Audit | Implement audit writer abstraction | Application can record security and resource events without infrastructure coupling | 3.13 |  |
| [ ] | 4.14 | Audit | Audit auth events | Login, logout, failed login, token create/revoke are recorded safely | 4.13 |  |
| [ ] | 4.15 | Audit | Audit team and resource changes | Team, server, application, deployment, terminal, secret, webhook, and settings changes are recorded | 4.13 |  |
| [ ] | 4.16 | Tests | Add authorization tests | Command/query/API access rules are covered for owner, member, unauthorized, and cross-tenant cases | 4.09-4.12 |  |
| [ ] | 4.17 | Docs | Document auth and permission model | User and operator docs describe auth options, tokens, roles, and permissions | 4.01-4.12 |  |

---

## Phase 5: Infrastructure Abstractions and Safe Process Execution

Goal: Build the integration foundation for external commands, redaction, Docker/Podman, SSH, Git, Redis, locks, storage, realtime, and background jobs.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 5.01 | Processes | Define `IProcessRunner` | Application-facing abstraction supports structured args, cancellation, timeout, working directory, env vars, stream policy, redaction, audit metadata | Phase 1 |  |
| [ ] | 5.02 | Processes | Define command/result models | Process command, text output, binary output, output line, exit info, stream policy, termination policy exist | 5.01 |  |
| [ ] | 5.03 | Processes | Implement .NET process runner | Infrastructure implementation uses stable .NET 11 APIs only after verification | .NET 11 stable SDK, 5.02 |  |
| [ ] | 5.04 | Processes | Enforce no inherited handles by default | Runner does not inherit unsafe handles unless explicitly configured | 5.03 |  |
| [ ] | 5.05 | Processes | Implement graceful termination | Timeout and cancellation attempt graceful termination before kill according to policy | 5.03 |  |
| [ ] | 5.06 | Processes | Implement process output streaming | stdout/stderr can stream incrementally with ordering metadata | 5.03 |  |
| [ ] | 5.07 | Processes | Implement binary capture | Binary output capture supports size limits and safe storage policy | 5.03 |  |
| [ ] | 5.08 | Processes | Implement redaction pipeline | All process output is redacted before persistence or broadcast | 5.06 |  |
| [ ] | 5.09 | Security | Define secret redactor | Patterns and explicit secret values can be redacted consistently | 5.08 |  |
| [ ] | 5.10 | Security | Add file path safety utilities | Owned paths are validated before work dirs, generated files, redirection, backups, artifacts | Phase 3 |  |
| [ ] | 5.11 | Docker | Define container runtime abstractions | Docker first; Podman-compatible design; API-first with CLI fallback through `IProcessRunner` | 5.01 |  |
| [ ] | 5.12 | Docker | Implement Docker API adapter | Can inspect daemon, containers, images, networks, volumes, and events using stable API | 5.11 |  |
| [ ] | 5.13 | Docker | Implement Docker CLI fallback | Compose/build/diagnostic commands go through `IProcessRunner` with structured args | 5.01, 5.11 |  |
| [ ] | 5.14 | Docker | Document Docker socket risk | `/var/run/docker.sock` access is documented as host-root-equivalent | 5.11 |  |
| [ ] | 5.15 | Podman | Preserve Podman extension point | Runtime abstraction avoids Docker-only assumptions where practical | 5.11 |  |
| [ ] | 5.16 | SSH | Define SSH abstraction | Supports connection testing, command execution policy, file transfer, key handling, cancellation | 5.01 |  |
| [ ] | 5.17 | SSH | Implement SSH adapter | Adapter redacts secrets, validates host identity policy, records audit metadata | 5.16 |  |
| [ ] | 5.18 | Git | Define Git abstraction | Clone, fetch, checkout, shallow clone, commit metadata, branch/tag selection, cancellation | 5.01 |  |
| [ ] | 5.19 | Git | Implement Git adapter | Uses structured process execution or library with redacted output and safe workdirs | 5.18 |  |
| [ ] | 5.20 | Redis | Define Redis abstraction | Cache, locks, ephemeral status, counters, and future SignalR backplane support are separated | Phase 2 |  |
| [ ] | 5.21 | Redis | Implement distributed locks | Deployment, server mutation, backup/restore, proxy reload, and certificate renewal can use locks | 5.20 |  |
| [ ] | 5.22 | Storage | Define `IObjectStorage` | S3-compatible API supports backups, artifacts, large logs, compose snapshots, bundles, exports | Phase 3 |  |
| [ ] | 5.23 | Storage | Implement local storage provider | Development-safe provider validates paths and permissions | 5.22 |  |
| [ ] | 5.24 | Storage | Implement S3-compatible provider | Provider supports credentials, buckets, prefixes, streaming, retries, and safe error handling | 5.22 |  |
| [ ] | 5.25 | Jobs | Configure Hangfire | PostgreSQL storage, dashboard policy, queues, retry policy, and worker count are configured | Persistence |  |
| [ ] | 5.26 | Jobs | Define background job abstraction | Application queues work without depending directly on Hangfire APIs | 5.25 |  |
| [ ] | 5.27 | Realtime | Define realtime notifier abstraction | Application can publish deployment, server, terminal, and notification events without SignalR coupling | Phase 2 |  |
| [ ] | 5.28 | Realtime | Implement SignalR notifier | Infrastructure/Web bridge sends messages to deterministic groups | 5.27 |  |
| [ ] | 5.29 | Tests | Add process runner tests | Covers cancellation, timeout, redaction, output streaming, binary limits, working dir safety | 5.01-5.09 |  |
| [ ] | 5.30 | Tests | Add integration adapter tests | Docker, Git, SSH, Redis, storage, Hangfire wiring are tested with fakes or Testcontainers where appropriate | 5.11-5.28 |  |

---

## Phase 6: UI Shell, API Surface, and Realtime Foundation

Goal: Establish the control panel shell, API conventions, typed endpoints, SignalR hubs, and reusable UI primitives before feature-heavy screens.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 6.01 | UI | Create Blazor Web App shell | App, Routes, layout, auth-aware navigation, and error boundary exist | Phase 2, Phase 4 |  |
| [ ] | 6.02 | UI | Select design system | Tailwind CSS or mature Blazor component library is selected and documented | 6.01 |  |
| [ ] | 6.03 | UI | Create reusable primitives | PageHeader, StatusBadge, DangerButton, ConfirmDialog, EmptyState, ResourceCard, SecretInput exist | 6.02 |  |
| [ ] | 6.04 | UI | Create dashboard frame | Dashboard home shows safe aggregate status and quick links | 6.01 |  |
| [ ] | 6.05 | UI | Implement auth pages | Login, logout, profile, 2FA, recovery, token management screens exist | Phase 4 |  |
| [ ] | 6.06 | UI | Implement team switcher | User can switch team context and see authorized resources only | Phase 4 |  |
| [ ] | 6.07 | API | Define API versioning | `/api/v1/...` route convention and typed responses are established | Phase 2 |  |
| [ ] | 6.08 | API | Create project endpoints skeleton | Thin controllers call Application commands/queries | Phase 3 |  |
| [ ] | 6.09 | API | Create server endpoints skeleton | Thin controllers call Application commands/queries | Phase 3 |  |
| [ ] | 6.10 | API | Create application endpoints skeleton | Thin controllers call Application commands/queries | Phase 3 |  |
| [ ] | 6.11 | API | Create deployment endpoints skeleton | Thin controllers call Application commands/queries | Phase 3 |  |
| [ ] | 6.12 | API | Create database endpoints skeleton | Thin controllers call Application commands/queries | Phase 3 |  |
| [ ] | 6.13 | API | Create notification endpoints skeleton | Thin controllers call Application commands/queries | Phase 3 |  |
| [ ] | 6.14 | API | Create settings endpoints skeleton | Thin controllers call Application commands/queries | Phase 3 |  |
| [ ] | 6.15 | SignalR | Add deployment log hub | Authenticates, authorizes, joins deployment groups, forwards messages only | Phase 5 |  |
| [ ] | 6.16 | SignalR | Add terminal hub skeleton | Authenticates, authorizes, joins terminal groups, delegates terminal operations | Phase 5 |  |
| [ ] | 6.17 | SignalR | Add server status hub | Authenticates, authorizes, joins server groups, forwards status only | Phase 5 |  |
| [ ] | 6.18 | SignalR | Add notification hub | Authenticates, authorizes, joins user/team groups, forwards notifications only | Phase 5 |  |
| [ ] | 6.19 | SignalR | Define deterministic group naming | tenant, project, server, application, deployment, terminal, user group helpers exist | 6.15-6.18 |  |
| [ ] | 6.20 | Tests | Add API smoke tests | Auth, error shape, route conventions, and thin controller behavior are covered | 6.07-6.14 |  |
| [ ] | 6.21 | Tests | Add hub authorization tests | Unauthorized, wrong-team, and authorized group join behavior are covered | 6.15-6.19 |  |

---

## Phase 7: Resource Management MVP

Goal: Implement user-facing CRUD and validation for projects, servers, applications, databases, environments, and environment variables without deployment execution yet.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 7.01 | Projects | Create project command/query flow | Create, edit, archive/delete, list, detail, and team authorization work | Phase 6 |  |
| [ ] | 7.02 | Projects | Create project UI | Project list, detail, settings, empty state, and validation errors work | 7.01 |  |
| [ ] | 7.03 | Environments | Create environment management | Create, rename, delete where safe, mark production/staging/preview semantics | 7.01 |  |
| [ ] | 7.04 | Servers | Add server registration | Server can be added with host, connection type, runtime preference, labels, and team ownership | Phase 5, Phase 6 |  |
| [ ] | 7.05 | Servers | Add server connectivity test | Connectivity and runtime availability are checked through Application/Infrastructure only | 7.04 |  |
| [ ] | 7.06 | Servers | Add server UI | List, detail, status, settings, labels, and connection test results render safely | 7.04, 7.05 |  |
| [ ] | 7.07 | Servers | Add server status snapshots | Disk, memory, CPU, container status, proxy status, certificate status can be persisted | 7.05 |  |
| [ ] | 7.08 | Applications | Add application creation | Git source, branch, build method, server, environment, domains, and variables can be configured | 7.01, 7.03, 7.04 |  |
| [ ] | 7.09 | Applications | Add application settings | Edit source, build, runtime, domains, health checks, deployment policy, and resource limits | 7.08 |  |
| [ ] | 7.10 | Applications | Add application UI | List, create wizard, detail, settings, domains, env, and status views exist | 7.08, 7.09 |  |
| [ ] | 7.11 | Databases | Add database resource creation | Database type, version, server, environment, storage, credentials reference, and backup settings can be configured | 7.01, 7.03, 7.04 |  |
| [ ] | 7.12 | Databases | Add database UI | List, detail, settings, credentials reference, health, backup state render safely | 7.11 |  |
| [ ] | 7.13 | Environment Variables | Implement variable model | Plain, secret, build-time, runtime, per-environment, and preview overrides are represented | Phase 3, Phase 4 |  |
| [ ] | 7.14 | Environment Variables | Implement variable editor | UI supports add/edit/delete/mask/reveal where authorized; secret values are write-only by default | 7.13 |  |
| [ ] | 7.15 | Secrets | Implement encrypted secret storage | SSH keys, tokens, registry credentials, DB passwords, TLS private keys, backup and S3 credentials are encrypted at rest | Phase 4, Phase 5 |  |
| [ ] | 7.16 | Secrets | Implement key rotation plan | Rotation mechanism or documented placeholder exists without exposing plaintext | 7.15 |  |
| [ ] | 7.17 | Registry | Add registry credentials model | Docker/OCI registry credentials can be stored and referenced by apps | 7.15 |  |
| [ ] | 7.18 | Tests | Add resource command tests | Project/server/app/database/env variable flows cover validation, auth, tenancy, and audit | 7.01-7.17 |  |
| [ ] | 7.19 | E2E | Add resource management E2E | Login, create project, add server, create application, configure variables | 7.01-7.17 |  |

---

## Phase 8: Deployment MVP

Goal: Deliver the first end-to-end deployment flow: connect Git, build or compose, create runtime resources, stream logs, persist state, and show status.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 8.01 | Deployment | Define deployment runner interface | Application owns orchestration contract; job class delegates to it | Phase 5, Phase 7 |  |
| [ ] | 8.02 | Deployment | Implement start deployment command | Validates auth, app config, server status, locks, and creates deployment record | 8.01 |  |
| [ ] | 8.03 | Deployment | Implement Hangfire job entry | Thin job receives deployment ID and calls Application runner with cancellation | 8.01 |  |
| [ ] | 8.04 | Deployment | Implement deployment lock | Prevents concurrent deployments for same app/server/proxy target | 5.21, 8.02 |  |
| [ ] | 8.05 | Deployment | Implement Git fetch/checkout step | Source is cloned/fetched into safe per-deployment workdir | 5.18, 5.19 |  |
| [ ] | 8.06 | Deployment | Capture commit metadata | Deployment records commit SHA, branch/tag, actor, repo, and timestamp | 8.05 |  |
| [ ] | 8.07 | Build | Implement Dockerfile build path | Docker build runs through runtime abstraction/process runner with redacted streaming logs | 5.11-5.13 |  |
| [ ] | 8.08 | Build | Implement Docker Compose path | Compose config is generated/validated/applied with structured execution | 5.13 |  |
| [ ] | 8.09 | Config Generation | Generate `.env` files safely | Deterministic output, safe permissions, secret redaction in logs, no path traversal | 7.13, 7.15 |  |
| [ ] | 8.10 | Config Generation | Generate compose snapshots | Store deployment compose/config snapshot for audit and rollback | 5.22, 8.08 |  |
| [ ] | 8.11 | Runtime | Create networks and volumes | Runtime resources are named deterministically and idempotently | 5.11 |  |
| [ ] | 8.12 | Runtime | Start or update containers | Containers are created/updated with labels, env, volumes, networks, health checks | 8.07, 8.08 |  |
| [ ] | 8.13 | Health | Implement deployment health check | Post-start checks determine success/failure with timeout and clear logs | 8.12 |  |
| [ ] | 8.14 | Logs | Persist deployment logs incrementally | Ordered, redacted logs are stored with timestamps, stream, step, and sequence | 5.08, 8.03 |  |
| [ ] | 8.15 | Logs | Stream deployment logs via SignalR | Authorized viewers receive live redacted logs for deployment group | 6.15, 8.14 |  |
| [ ] | 8.16 | Status | Emit realtime status updates | Deployment pending/running/succeeded/failed/canceled status reaches UI | 6.15, 8.03 |  |
| [ ] | 8.17 | UI | Add deployment start UI | Authorized users can start deployment and see accepted status | 8.02 |  |
| [ ] | 8.18 | UI | Add deployment details UI | Timeline, status, commit, logs, generated config references, and errors render | 8.14-8.16 |  |
| [ ] | 8.19 | UI | Add deployment list UI | Application and project deployment history supports pagination and status filtering | 8.02 |  |
| [ ] | 8.20 | Cancel | Implement cancellation request | Authorized user can request cancellation; runner observes cancellation | 8.03 |  |
| [ ] | 8.21 | Cleanup | Clean per-deployment workdirs | Cleanup respects audit/snapshot retention and safe path validation | 5.10, 8.05 |  |
| [ ] | 8.22 | Tests | Add deployment state tests | Start/run/succeed/fail/cancel transitions are covered | 8.01-8.20 |  |
| [ ] | 8.23 | Tests | Add golden config tests | Compose, env, labels, and deployment scripts use deterministic snapshots | 8.08-8.10 |  |
| [ ] | 8.24 | E2E | Add deployment MVP E2E | Create app, start deployment, view logs, verify final status | 8.17-8.19 |  |

---

## Phase 9: Webhooks, Git Providers, and Preview Deployments

Goal: Automate deployment triggers from source providers and support preview workflows.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 9.01 | Webhooks | Add webhook event model | Receipt, provider, event ID, signature status, dedupe key, payload reference, processing status are stored | Phase 8 |  |
| [ ] | 9.02 | Webhooks | Implement GitHub webhook endpoint | Signature verified, rate limited, persisted, deduped, enqueued quickly | 9.01 |  |
| [ ] | 9.03 | Webhooks | Implement GitLab webhook endpoint | Token/signature verified where supported, persisted, deduped, enqueued quickly | 9.01 |  |
| [ ] | 9.04 | Webhooks | Implement Gitea webhook endpoint | Signature verified where supported, persisted, deduped, enqueued quickly | 9.01 |  |
| [ ] | 9.05 | Webhooks | Implement Bitbucket webhook endpoint | Signature verified where supported, persisted, deduped, enqueued quickly | 9.01 |  |
| [ ] | 9.06 | Webhooks | Implement generic webhook endpoint | Secret-protected generic trigger supports limited deployment actions | 9.01 |  |
| [ ] | 9.07 | Webhooks | Add webhook processing job | Thin job delegates to Application processing service | 5.25, 9.01 |  |
| [ ] | 9.08 | Git Providers | Add repository connection UI | User can connect or configure Git providers without exposing tokens | Phase 7 |  |
| [ ] | 9.09 | Git Providers | Add branch/tag discovery | Application can list refs through provider or Git abstraction | 5.18, 9.08 |  |
| [ ] | 9.10 | Deployment | Add branch push trigger | Configured branch push starts deployment according to policy | 9.02-9.07 |  |
| [ ] | 9.11 | Deployment | Add manual redeploy by commit | User can redeploy a known commit if still available | Phase 8 |  |
| [ ] | 9.12 | Preview | Add preview environment model | Pull request/merge request previews have isolated env, domain, variables, and lifecycle | Phase 7 |  |
| [ ] | 9.13 | Preview | Add preview deployment trigger | PR/MR events create/update preview deployments | 9.02-9.07, 9.12 |  |
| [ ] | 9.14 | Preview | Add preview cleanup | Closed/merged PR/MR removes or archives preview resources according to policy | 9.13 |  |
| [ ] | 9.15 | Security | Harden webhook abuse controls | Rate limiting, replay prevention, payload size limits, and audit logs are enforced | 9.01-9.07 |  |
| [ ] | 9.16 | Tests | Add webhook tests | Signature, dedupe, quick return, queueing, auth failure, and trigger behavior are covered | 9.02-9.07 |  |
| [ ] | 9.17 | E2E | Add webhook deployment E2E | Simulated provider event triggers deployment and UI shows status/logs | 9.10 |  |

---

## Phase 10: Reverse Proxy, Domains, TLS, and Routing

Goal: Provide deterministic, validated, auditable routing from domains to deployed applications.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 10.01 | Proxy | Define `IProxyProvider` | Application can generate, validate, apply, reload, rollback proxy configuration through abstraction | Phase 8 |  |
| [ ] | 10.02 | Proxy | Choose first provider | Traefik, Caddy, Nginx, or custom provider is selected for MVP and documented | 10.01 |  |
| [ ] | 10.03 | Proxy | Implement config generator | Output is deterministic, versioned, validated before apply, and reversible where possible | 10.02 |  |
| [ ] | 10.04 | Proxy | Implement config validation | Invalid config blocks apply and emits safe actionable errors | 10.03 |  |
| [ ] | 10.05 | Proxy | Implement apply/reload | Apply is protected by locks and audited; reload failure preserves previous config where possible | 5.21, 10.03 |  |
| [ ] | 10.06 | Proxy | Implement rollback metadata | Previous config/version can be restored after failed apply or deployment rollback | 10.05 |  |
| [ ] | 10.07 | Domains | Add domain management | Apps can add/remove domains, redirects, canonical host policy, and port mapping | Phase 7 |  |
| [ ] | 10.08 | TLS | Add certificate management model | Certificate status, provider, renewal date, errors, and secret refs are stored | 7.15 |  |
| [ ] | 10.09 | TLS | Implement certificate issuance | Provider-specific issuance is queued, audited, locked, and redacted | 10.08 |  |
| [ ] | 10.10 | TLS | Implement certificate renewal job | Scheduled renewal is retry-safe, locked, and reports status | 5.25, 10.09 |  |
| [ ] | 10.11 | Routing | Integrate deployment with proxy switch | Successful deployment updates routes after health check according to policy | Phase 8, 10.05 |  |
| [ ] | 10.12 | UI | Add domain and routing UI | Users can manage domains, TLS, redirects, and see proxy/cert status | 10.07-10.10 |  |
| [ ] | 10.13 | Tests | Add golden proxy tests | Generated Nginx/Caddy/Traefik config or labels are snapshot-tested | 10.03 |  |
| [ ] | 10.14 | Tests | Add proxy apply tests | Validation failure, reload failure, lock behavior, and rollback metadata are covered | 10.04-10.06 |  |

---

## Phase 11: Managed Services, Databases, Backups, and Restore

Goal: Manage databases and service templates with durable backup/restore workflows.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 11.01 | Databases | Implement PostgreSQL resource deployment | Provision container/service, credentials, storage, env injection, health checks | Phase 8 |  |
| [ ] | 11.02 | Databases | Implement MySQL/MariaDB resource deployment | Provision container/service, credentials, storage, env injection, health checks | Phase 8 |  |
| [ ] | 11.03 | Databases | Implement Redis resource deployment | Provision container/service, credentials, storage policy, health checks | Phase 8 |  |
| [ ] | 11.04 | Databases | Implement database lifecycle actions | Start, stop, restart, delete, scale where applicable, and inspect status | 11.01-11.03 |  |
| [ ] | 11.05 | Services | Define service template model | Templates represent image, env, ports, volumes, health checks, docs, and upgrade policy | Phase 7 |  |
| [ ] | 11.06 | Services | Add initial service templates | Common services can be created from validated templates | 11.05 |  |
| [ ] | 11.07 | Services | Add template UI | Users can select template, configure inputs, validate, and deploy service | 11.05, 11.06 |  |
| [ ] | 11.08 | Backups | Define backup abstraction | Database and volume backups can target object storage/local storage with metadata | 5.22 |  |
| [ ] | 11.09 | Backups | Implement PostgreSQL backup | Queued backup captures dump safely, redacts credentials, stores metadata and artifact | 11.01, 11.08 |  |
| [ ] | 11.10 | Backups | Implement backup schedules | Scheduled jobs with retention, retry, lock, and audit support | 5.25, 11.08 |  |
| [ ] | 11.11 | Backups | Implement backup retention | Old backups are pruned according to policy without deleting protected artifacts | 11.10 |  |
| [ ] | 11.12 | Restore | Implement restore workflow | Restore is explicit, audited, locked, validates target, supports dry-run where practical | 11.08 |  |
| [ ] | 11.13 | Restore | Add restore safety prompts | UI and API prevent accidental overwrite and show impact summary | 11.12 |  |
| [ ] | 11.14 | UI | Add backup/restore UI | Schedules, history, artifacts, restore flow, and failures are visible | 11.08-11.13 |  |
| [ ] | 11.15 | Tests | Add backup/restore tests | Artifact metadata, retention, redaction, locks, restore validation, and failure paths are covered | 11.08-11.13 |  |

---

## Phase 12: Terminal, Logs, Monitoring, and Operational Views

Goal: Provide safe operational access to live state, logs, and terminals without bypassing application boundaries.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 12.01 | Logs | Implement log query API | Deployment logs support pagination, search, streaming resume, retention, and redaction | Phase 8 |  |
| [ ] | 12.02 | Logs | Add log retention policy | Retention, archival, compression if needed, and deletion jobs are configured | 12.01 |  |
| [ ] | 12.03 | Logs | Add large log handling | Queries avoid loading entire logs and use indexes/projections | 12.01 |  |
| [ ] | 12.04 | UI | Implement log viewer | Live tail, search, filters, pagination, download/export where authorized | 12.01 |  |
| [ ] | 12.05 | Terminal | Define terminal session model | Session owner, target, command policy, started/ended timestamps, audit, and status exist | Phase 4, Phase 5 |  |
| [ ] | 12.06 | Terminal | Implement terminal authorization | Opening terminal requires explicit permission and target access | 12.05 |  |
| [ ] | 12.07 | Terminal | Implement terminal process bridge | Terminal execution delegates to Application/Infrastructure, supports cancellation and no unsafe fire-and-forget | 12.05, 12.06 |  |
| [ ] | 12.08 | Terminal | Implement terminal SignalR flow | Hub only authorizes/groups/forwards input/output; no process logic in hub | 6.16, 12.07 |  |
| [ ] | 12.09 | Terminal | Add terminal UI | Authorized users can open, interact, resize, close, and view session status | 12.08 |  |
| [ ] | 12.10 | Terminal | Add terminal security docs | Risks, permissions, audit, secret redaction limits, and operational guidance are documented | 12.05-12.09 |  |
| [ ] | 12.11 | Monitoring | Implement server health polling | Scheduled jobs collect connectivity, Docker, disk, memory, CPU, containers, proxy, certificates | 5.25, 7.07 |  |
| [ ] | 12.12 | Monitoring | Implement dashboard metrics | Active deployments, failures, queue length, server health, notifications, terminal sessions are visible | 12.11 |  |
| [ ] | 12.13 | Monitoring | Add resource event stream | Resource changes and status updates flow to authorized users | 5.27, 5.28 |  |
| [ ] | 12.14 | Tests | Add terminal safety tests | Authorization, process boundary, cancellation, audit, and redaction are covered | 12.05-12.09 |  |
| [ ] | 12.15 | E2E | Add operations E2E | View logs, filter logs, see server health, open/close terminal where safe | 12.04, 12.09, 12.12 |  |

---

## Phase 13: Notifications and Event Delivery

Goal: Deliver reliable in-app and external notifications with retry, audit, and channel-specific configuration.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 13.01 | Notifications | Define notification event model | Event type, severity, target, channel, payload, delivery status, attempts, and ownership are stored | Phase 3 |  |
| [ ] | 13.02 | Notifications | Implement in-app notifications | Database-backed notifications show unread/read/archive state | 13.01 |  |
| [ ] | 13.03 | Notifications | Implement email provider | Configurable SMTP/provider sends queued notifications with retries | 13.01 |  |
| [ ] | 13.04 | Notifications | Implement webhook provider | Outbound webhooks sign payloads where configured and record delivery attempts | 13.01 |  |
| [ ] | 13.05 | Notifications | Implement Discord provider | Discord-compatible webhook messages support deployment/resource events | 13.01 |  |
| [ ] | 13.06 | Notifications | Implement Telegram provider | Bot/channel configuration supports deployment/resource events | 13.01 |  |
| [ ] | 13.07 | Notifications | Implement Slack-compatible provider | Slack-compatible webhook messages support deployment/resource events | 13.01 |  |
| [ ] | 13.08 | Jobs | Add notification dispatch job | Queue dispatch, retry, and failure state are implemented | 5.25, 13.01 |  |
| [ ] | 13.09 | UI | Add notification settings UI | Team/project/user notification rules and channels can be configured | 13.01-13.07 |  |
| [ ] | 13.10 | UI | Add notification center | User can view, mark read, archive, and navigate to resource context | 13.02 |  |
| [ ] | 13.11 | Realtime | Stream in-app notifications | Authorized users receive new notifications through SignalR | 6.18, 13.02 |  |
| [ ] | 13.12 | Tests | Add notification tests | Routing, retries, redaction, delivery attempts, and provider failures are covered | 13.01-13.08 |  |

---

## Phase 14: Production Distribution, Installer, Self-Update, and Operations

Goal: Ship Vessel as a host-native, self-contained executable with deterministic install, update, rollback, and operational docs.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 14.01 | Publish | Add self-contained publish profiles | Linux x64 and Linux arm64 artifacts are produced | Phase 1 |  |
| [ ] | 14.02 | Publish | Evaluate single-file publishing | Single-file mode is enabled only if compatible with ASP.NET Core, EF, diagnostics, Hangfire, serialization | 14.01 |  |
| [ ] | 14.03 | Publish | Evaluate trimming/AOT | Trimming/AOT are documented and only enabled if runtime behavior remains correct | 14.01 |  |
| [ ] | 14.04 | CLI | Add `vessel server` command | Host-native executable can start the web/control-plane process | Phase 2 |  |
| [ ] | 14.05 | CLI | Add configuration commands | Commands inspect config paths, validate config, and show effective safe config | 14.04 |  |
| [ ] | 14.06 | Linux | Add systemd unit template | Unit uses predictable paths, service user, restart policy, journald, env file, and security hardening | 14.04 |  |
| [ ] | 14.07 | Filesystem | Define host paths | `/usr/local/bin/vessel`, `/etc/vessel/`, `/var/lib/vessel/`, `/var/log/vessel/` policy is documented | 14.06 |  |
| [ ] | 14.08 | Installer | Add Linux install script | Detects OS/arch, downloads versioned artifact, verifies checksum/signature when available, installs safely, avoids config overwrite | 14.01, 14.06 |  |
| [ ] | 14.09 | Installer | Add non-interactive install mode | Automation can install or update without prompts while preserving safety | 14.08 |  |
| [ ] | 14.10 | Installer | Add uninstall guidance | Docs explain service stop, binary removal, config/data retention or deletion options | 14.08 |  |
| [ ] | 14.11 | Self-update | Add `vessel self-update` command | Downloads versioned binary, verifies checksum/signature, stages beside install | 14.01 |  |
| [ ] | 14.12 | Self-update | Implement atomic swap | Uses symlink or platform-safe equivalent; no unmanaged detached process | 14.11 |  |
| [ ] | 14.13 | Self-update | Implement service restart | Restarts through service manager with clear lifecycle and failure reporting | 14.12 |  |
| [ ] | 14.14 | Self-update | Implement post-update health check | Update validates startup and readiness after restart | 14.13 |  |
| [ ] | 14.15 | Self-update | Implement rollback | Failed startup or health check restores previous binary and records audit metadata | 14.14 |  |
| [ ] | 14.16 | Dockerized Mode | Add Docker Compose deployment | Development/demo compose supports Vessel, PostgreSQL, Redis, and optional runtime access | Phase 2 |  |
| [ ] | 14.17 | Dockerized Mode | Document `docker.sock` risk | Containerized mode clearly states host-root-equivalent socket exposure | 14.16 |  |
| [ ] | 14.18 | Operations | Add host-native install docs | Operator docs cover install, config, service management, logs, updates, backup, restore | 14.08-14.15 |  |
| [ ] | 14.19 | Operations | Add containerized install docs | Docs cover demo/dev/compatibility use, runtime access, limits, and risks | 14.16 |  |
| [ ] | 14.20 | Operations | Add backup operations docs | Backup, retention, restore, object storage, and disaster recovery runbooks exist | Phase 11 |  |
| [ ] | 14.21 | Tests | Add installer smoke tests | Scripts validate arch selection, checksum failure, config preservation, service template output | 14.08 |  |
| [ ] | 14.22 | Tests | Add self-update tests | Stage, verify, swap, restart, health failure, rollback, and audit metadata are covered | 14.11-14.15 |  |

---

## Phase 15: Security Hardening and Compliance Baseline

Goal: Review dangerous features, enforce security controls, and build repeatable evidence that secrets and tenant data remain protected.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 15.01 | Security | Perform threat model | Docker socket, terminal, webhooks, SSH, secrets, backups, proxy config, self-update, file uploads are reviewed | Major features implemented |  |
| [ ] | 15.02 | Security | Verify auth coverage | All UI, API, hubs, jobs, and commands enforce identity and resource authorization | Phase 4 |  |
| [ ] | 15.03 | Security | Verify CSRF protections | Interactive and form endpoints are protected where applicable | Phase 6 |  |
| [ ] | 15.04 | Security | Verify rate limits | Auth, webhooks, API, terminal, and expensive operations are rate limited | Phase 2, Phase 9, Phase 12 |  |
| [ ] | 15.05 | Security | Verify input validation | API requests, commands, domain invariants, file paths, provider payloads validate at boundaries | All features |  |
| [ ] | 15.06 | Security | Verify output encoding | UI and API do not expose unsafe HTML/script content from logs, provider payloads, or user input | Phase 6+ |  |
| [ ] | 15.07 | Security | Verify secret redaction | Logs, process output, API responses, SignalR messages, deployment logs, exceptions, and audits are covered | Phase 5+ |  |
| [ ] | 15.08 | Security | Verify encryption at rest | Secret values and sensitive credentials are encrypted and key handling is documented | Phase 7 |  |
| [ ] | 15.09 | Security | Verify audit coverage | Required events are recorded without secret values | Phase 4+ |  |
| [ ] | 15.10 | Security | Verify safe file handling | Workdirs, generated files, redirects, backups, uploads, and artifacts reject traversal and unsafe permissions | Phase 5+ |  |
| [ ] | 15.11 | Security | Verify safe process execution | Forbidden direct process APIs are absent outside Infrastructure/Processes | Phase 5+ |  |
| [ ] | 15.12 | Security | Verify Docker/SSH/Git boundaries | Web/Application/Domain do not execute Docker, SSH, Git, or shell directly | Phase 5+ |  |
| [ ] | 15.13 | Security | Verify update trust chain | Installer and self-update validate checksum/signature and rollback safely | Phase 14 |  |
| [ ] | 15.14 | Compliance | Add dependency review process | Package license, maintenance, security posture, and transitive risk are reviewed before additions | Phase 1 |  |
| [ ] | 15.15 | Compliance | Add vulnerability scanning | CI checks dependencies and container artifacts where applicable | CI foundation |  |
| [ ] | 15.16 | Tests | Add safety test suite | Redaction, authorization, path safety, dangerous endpoint access, and audit rules have automated tests | 15.01-15.13 |  |
| [ ] | 15.17 | Docs | Publish security docs | Secrets, terminal, Docker socket, backups, tokens, webhooks, and self-update trust model are documented | 15.01-15.13 |  |

---

## Phase 16: Performance, Scale, and Reliability

Goal: Make the monolith reliable under real deployment, logging, dashboard, and background-job load.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 16.01 | Database | Add performance indexes | Queries for dashboards, deployments, logs, resources, audit, webhooks, and notifications use appropriate indexes | Feature schemas |  |
| [ ] | 16.02 | Database | Add projection queries | Large lists and dashboards use `AsNoTracking`, projections, pagination, and compiled queries where useful | Feature queries |  |
| [ ] | 16.03 | Logs | Load test deployment logs | Streaming, persistence, pagination, and search handle large logs within defined limits | Phase 12 |  |
| [ ] | 16.04 | Jobs | Tune Hangfire queues | Deployment, health, notifications, cleanup, backup, and webhook queues have concurrency and retry policy | Phase 5+ |  |
| [ ] | 16.05 | Jobs | Add retry safety review | Long-running jobs are idempotent or explicitly compensate on retry | Phase 8+ |  |
| [ ] | 16.06 | Locks | Validate lock behavior | Deployment, server mutation, backup/restore, proxy reload, cert renewal locks handle timeout and crash recovery | Phase 5+ |  |
| [ ] | 16.07 | SignalR | Evaluate scale-out needs | Redis backplane and sticky session requirements are documented or implemented for multi-instance mode | Phase 6+ |  |
| [ ] | 16.08 | Observability | Add metrics coverage | Deployment count/duration/failure, active deployments, queues, server health, hub connections, terminal sessions, process duration, Docker failures, DB duration | Phase 2+ |  |
| [ ] | 16.09 | Observability | Add tracing spans | StartDeployment, RunDeployment, GitClone, DockerBuild, ComposeUp, HealthCheck, ProxySwitch, SendNotification are traced | Phase 8+ |  |
| [ ] | 16.10 | Reliability | Add startup validation | Config, paths, permissions, DB migrations, Redis, storage, and runtime access fail clearly | Phase 14 |  |
| [ ] | 16.11 | Reliability | Add graceful shutdown | Web host, SignalR, jobs, process runner, terminal sessions, and deployment cancellation behave predictably | Phase 5+ |  |
| [ ] | 16.12 | Reliability | Add disaster recovery drill | Backup restore and service rebuild are verified through documented procedure | Phase 11, Phase 14 |  |
| [ ] | 16.13 | Tests | Add performance smoke tests | Representative dashboard/log/job queries stay within defined thresholds | 16.01-16.04 |  |

---

## Phase 17: Migration From Coolify and Compatibility

Goal: Preserve important product behavior while using idiomatic .NET architecture.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 17.01 | Research | Inventory Coolify behavior | Deployment lifecycle, resources, server provisioning, compose generation, env vars, templates, webhooks, notifications, auth, updates, backups, proxy, health checks are cataloged | Product decision |  |
| [ ] | 17.02 | Migration | Define import scope | Decide which Coolify data/config can be imported and which requires manual recreation | 17.01 |  |
| [ ] | 17.03 | Migration | Define import format | Export/import file or direct source DB strategy is documented | 17.02 |  |
| [ ] | 17.04 | Migration | Implement project import | Projects, environments, and metadata import with validation and audit | 17.03, Phase 7 |  |
| [ ] | 17.05 | Migration | Implement server import | Server metadata imports without unsafe credential exposure | 17.03, Phase 7 |  |
| [ ] | 17.06 | Migration | Implement application import | App source, build/runtime settings, domains, env refs, and deployment policy import | 17.03, Phase 7 |  |
| [ ] | 17.07 | Migration | Implement environment variable import | Plain/secret variables import through secure write-only path | 17.03, Phase 7 |  |
| [ ] | 17.08 | Migration | Implement service template mapping | Known Coolify service templates map to Vessel templates where supported | Phase 11 |  |
| [ ] | 17.09 | Migration | Implement webhook import | Webhook settings import with regenerated secrets where necessary | Phase 9 |  |
| [ ] | 17.10 | Migration | Implement notification import | Notification channels import where compatible and secret-safe | Phase 13 |  |
| [ ] | 17.11 | Migration | Implement backup/proxy mapping | Backup and proxy settings import where compatible and safe | Phase 10, Phase 11 |  |
| [ ] | 17.12 | Compatibility | Add behavior comparison tests | Critical generated configs and lifecycle semantics match intended compatibility decisions | 17.01 |  |
| [ ] | 17.13 | Docs | Add migration guide | Guide explains what is preserved, what changes, prerequisites, dry run, rollback, and validation | 17.02-17.11 |  |

---

## Phase 18: Release Readiness and Stable Operations

Goal: Prepare beta/stable release criteria, packaging, docs, verification, and operational support.

| Status | ID | Area | Feature / Task | Deliverable / Acceptance Criteria | Dependencies | Notes |
|---|---:|---|---|---|---|---|
| [ ] | 18.01 | Release | Define alpha exit criteria | Foundation, auth, resource MVP, deployment MVP, logs, and core docs are complete enough for early testers | Phases 1-8 |  |
| [ ] | 18.02 | Release | Define beta entry criteria | Security baseline, installer, backups, proxy, notifications, E2E tests, and migration docs meet documented bar | Phases 9-17 |  |
| [ ] | 18.03 | Release | Define stable criteria | Upgrade/rollback, DR, performance, security review, docs, support matrix, and critical bug bar are satisfied | Phases 14-17 |  |
| [ ] | 18.04 | Release | Add changelog | Releases have user-facing changes, breaking changes, migration notes, and known issues | Release process |  |
| [ ] | 18.05 | Release | Add versioning policy | Semantic versioning or documented alternative is applied to artifacts, DB schema, and APIs | Release process |  |
| [ ] | 18.06 | Release | Add artifact checksums | Release artifacts publish checksums and signatures when available | Phase 14 |  |
| [ ] | 18.07 | Docs | Complete architecture docs | Overview, modules, deployment flow, process runner, persistence, realtime, jobs, proxy, security are documented | All architecture |  |
| [ ] | 18.08 | Docs | Complete operations docs | Host-native install, containerized install, updates, backups, restore, logs, monitoring, troubleshooting are documented | Phase 14+ |  |
| [ ] | 18.09 | Docs | Complete user docs | Projects, servers, apps, deployments, env vars, secrets, webhooks, backups, terminal, notifications are documented | Product features |  |
| [ ] | 18.10 | QA | Run full automated test suite | Restore, build, unit, integration, E2E, golden, safety, and performance smoke tests pass | All tests |  |
| [ ] | 18.11 | QA | Run install/update validation | Fresh install, upgrade, failed upgrade rollback, and uninstall guidance are validated on supported platforms | Phase 14 |  |
| [ ] | 18.12 | QA | Run deployment validation matrix | Dockerfile, Compose, database, service template, webhook, preview, rollback, proxy/TLS flows are validated | Product features |  |
| [ ] | 18.13 | Support | Add troubleshooting guide | Common install, database, Redis, Docker socket, SSH, Git, proxy, TLS, deployment, and backup issues are covered | Docs |  |
| [ ] | 18.14 | Support | Add diagnostic bundle command | Safe diagnostic collection redacts secrets and gathers config, logs, health, versions, and runtime status | Observability |  |
| [ ] | 18.15 | Support | Add support matrix | OS, CPU architecture, Docker/Podman versions, PostgreSQL, Redis, browsers, and upgrade paths are listed | Release decision |  |

---

## Cross-Cutting Checklist

Use this checklist for every non-trivial feature, regardless of phase.

| Status | Area | Checklist Item | Notes |
|---|---|---|---|
| [ ] | Architecture | Feature belongs to the correct module |  |
| [ ] | Architecture | No business logic in Blazor components, controllers, hubs, job classes, or `Program.cs` |  |
| [ ] | Architecture | Domain has no infrastructure, HTTP, EF, Hangfire, SignalR, Docker, SSH, or direct time dependency |  |
| [ ] | Architecture | Application has no Web reference and owns integration interfaces |  |
| [ ] | Architecture | Infrastructure has no Web reference |  |
| [ ] | Processes | No direct forbidden process APIs outside Infrastructure/Processes |  |
| [ ] | Processes | External commands use structured arguments and safe working directories |  |
| [ ] | Processes | Process execution supports cancellation and timeout |  |
| [ ] | Processes | No detached/fire-and-forget process without reviewed lifecycle policy |  |
| [ ] | Runtime | Docker, Git, SSH, shell, and process operations are routed through approved abstractions |  |
| [ ] | Persistence | PostgreSQL remains the source of truth for critical records |  |
| [ ] | Persistence | Schema changes include migrations |  |
| [ ] | Persistence | Queries enforce tenant/team access |  |
| [ ] | Persistence | Mutable configuration and memberships use concurrency protection where appropriate |  |
| [ ] | Security | Authentication is required where appropriate |  |
| [ ] | Security | Authorization checks resource ownership and permissions, not just route IDs |  |
| [ ] | Security | Secrets are encrypted at rest and masked by default |  |
| [ ] | Security | Secrets are redacted from logs, process output, API responses, SignalR messages, exceptions, and audit logs |  |
| [ ] | Security | Dangerous features have rate limits, audit logs, and explicit permissions |  |
| [ ] | Security | File paths are validated against owned directories |  |
| [ ] | Operations | Long-running work records state transitions and logs incrementally |  |
| [ ] | Operations | Jobs are retry-safe or have explicit compensation behavior |  |
| [ ] | Operations | Exclusive workflows use advisory or Redis locks |  |
| [ ] | Operations | Errors are clear, actionable, safe, and do not leak internals |  |
| [ ] | Observability | Logs are structured and correlated |  |
| [ ] | Observability | Important operations emit metrics and traces |  |
| [ ] | Tests | Unit tests cover domain/application behavior |  |
| [ ] | Tests | Integration tests cover persistence/infrastructure wiring |  |
| [ ] | Tests | E2E tests cover user-facing workflow when relevant |  |
| [ ] | Tests | Golden tests cover generated files when relevant |  |
| [ ] | Docs | Docs or ADRs are updated for public behavior, operations, or architecture changes |  |
| [ ] | Verification | Narrowest useful verification command has been run |  |
