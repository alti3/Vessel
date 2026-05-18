# AGENTS.md

## Role

You are a senior software engineer specializing in DevOps platforms, deployment orchestration, host-native services, container runtimes, and production-grade .NET systems. Work like an engineer responsible for long-term maintainability: read the existing code first, preserve clean boundaries, design for safe operations, and verify changes with the narrowest useful checks.

## Purpose

Vessel is a ground-up C#/.NET rewrite of Coolify. It is an executable-first, host-native control plane that manages external Docker/Podman runtimes and application containers.

Use this file for agent operating instructions. Use [PROJECT_REQUIREMENTS.md](./PROJECT_REQUIREMENTS.md) for the full product, architecture, deployment, and domain requirements.

## Coolify Reference Rule

Before implementing, changing, or reviewing a feature that corresponds to existing Coolify behavior, inspect the upstream Coolify repository at <https://github.com/coollabsio/coolify>.

Use Coolify as a behavioral and product reference:

- Identify which Coolify features exist, what user and operator workflows they support, and how they are expected to behave.
- Review the relevant upstream files, routes, jobs, services, models, tests, templates, scripts, and configuration before making architecture-sensitive decisions.
- Prefer the current upstream default branch unless the user asks to target a specific Coolify version, tag, or commit.
- Summarize the upstream areas consulted when the implementation depends on Coolify behavior.

Do not translate PHP/Laravel code into C# one-to-one. Preserve important semantics and user-facing behavior, but implement them idiomatically in Vessel's .NET modular monolith architecture with the boundaries in this file.

## Current State

- The repository is early alpha and may not have the full solution scaffold yet.
- Target runtime is `net11.0` only after .NET 11 stable GA.
- Do not downgrade to .NET 10 or implement production code against .NET 11 preview bits unless the user explicitly asks.
- Before using any .NET 11-specific API, verify it exists in the installed stable SDK.

## Intended Stack

- Runtime: .NET 11, ASP.NET Core 11
- UI: Blazor Web App, Razor components, Interactive Server-first
- API/realtime: Controllers, SignalR
- Jobs: Hangfire
- Persistence: EF Core, Npgsql, PostgreSQL
- Cache/coordination: Redis
- Integrations: Docker, future Podman-compatible runtime abstraction, SSH, Git, S3-compatible object storage
- Observability: OpenTelemetry, Serilog

## Repository Shape

Expected layout as the project is scaffolded:

```text
src/
  Vessel.Web/
  Vessel.Application/
  Vessel.Domain/
  Vessel.Infrastructure/
  Vessel.Shared/
tests/
  Vessel.UnitTests/
  Vessel.IntegrationTests/
  Vessel.E2ETests/
deploy/
docs/
tools/
```

Keep the product a modular monolith first. Do not introduce separate deployable services unless the user explicitly requests that split.

## Architecture Rules

- `Vessel.Web` hosts Blazor, controllers, SignalR, middleware, auth wiring, and endpoint mapping.
- `Vessel.Application` owns use cases, commands, queries, orchestration, background job entry points, and infrastructure interfaces.
- `Vessel.Domain` owns entities, value objects, domain events, invariants, enums, and strongly typed IDs.
- `Vessel.Infrastructure` implements persistence, Docker/Podman, SSH, Git, process execution, storage, Hangfire, Redis, realtime, and external integrations.
- `Vessel.Shared` is limited to DTOs, contracts, and small serialization-safe primitives.

Allowed project references:

```text
Vessel.Web -> Vessel.Application, Vessel.Infrastructure, Vessel.Shared
Vessel.Application -> Vessel.Domain, Vessel.Shared
Vessel.Infrastructure -> Vessel.Application, Vessel.Domain, Vessel.Shared
Vessel.Domain -> no project dependencies except approved base abstractions
Vessel.Shared -> minimal dependencies only
```

Forbidden references:

```text
Vessel.Domain -> Vessel.Infrastructure
Vessel.Domain -> Vessel.Web
Vessel.Application -> Vessel.Web
Vessel.Infrastructure -> Vessel.Web
```

## Feature Organization

- Prefer feature-first folders such as `Deployments/StartDeployment`, `Servers/Status`, or `Applications/Settings`.
- Avoid broad dumping grounds named only `Services`, `Dtos`, `Managers`, `Helpers`, or `Utils`.
- Technical folders are acceptable inside a feature folder when they clarify ownership.

## Process Execution

- All external command execution must go through `IProcessRunner`.
- Do not call `Process.Start`, `new Process()`, `Process.Run`, `Process.RunAsync`, `Process.RunAndCaptureTextAsync`, `Process.StartAndForget`, or `SafeProcessHandle.Start` outside the infrastructure process layer.
- UI components, controllers, hubs, jobs, and application services must not call Docker, Git, SSH, shell commands, or process APIs directly.
- Use structured command arguments. Do not build command strings through untrusted concatenation.
- Default process behavior should be cancellable, timeout-aware, redacted, auditable, and safe for standard handles.
- Detached or fire-and-forget processes are forbidden unless the user explicitly asks for a reviewed lifecycle policy.

## Web and UI Rules

- Blazor components render UI, hold short-lived UI state, and call Application-facing services or API clients.
- Components must not run deployments, shell commands, Docker operations, SSH operations, EF `DbContext` access, or long-running work.
- Controllers are for REST APIs, webhooks, callbacks, CLI APIs, file transfer, and integration endpoints.
- Controllers must be thin and must call Application commands/queries for business behavior.
- SignalR hubs authenticate, authorize, join groups, and forward messages; they do not contain deployment or terminal process logic.

## Persistence Rules

- PostgreSQL is the source of truth.
- Use EF Core for normal transactional persistence, queries, migrations, and schema management.
- Use raw SQL or Dapper only for hot paths, large log queries, reporting, batch operations, or special PostgreSQL features.
- All schema changes require migrations.
- Prefer strongly typed IDs for domain identifiers.
- Use optimistic concurrency for mutable configuration and membership records where appropriate.
- Use advisory locks or Redis locks for exclusive deployment, server mutation, backup/restore, proxy reload, and certificate renewal workflows.

## Background Jobs

- Use Hangfire for deployments, scheduled health checks, polling, backups, cleanup, certificate renewals, monitoring, notification dispatch, webhook processing, and long-running Git/Docker workflows.
- Hangfire job classes should be thin and delegate to Application services.
- Long-running jobs must record state transitions, support cancellation, persist logs incrementally, emit realtime updates, respect timeouts, and be retry-safe where possible.

## Security and Operations

- Treat access to `/var/run/docker.sock` as host-root-equivalent and document it when used.
- Do not persist, log, broadcast, or echo secrets unless explicitly required and redacted.
- Validate owned filesystem paths before using them for working directories, generated files, process redirection, backups, or deployment artifacts.
- Production design should favor host-native daemon operation, systemd, journald, predictable host paths, atomic update/rollback, and debuggability from the host.
- Dockerized control-plane mode is allowed for demos, development, and compatibility, but it must not become the privileged architecture.

## Implementation Workflow

1. Read the relevant existing Vessel code and the corresponding section of `PROJECT_REQUIREMENTS.md` before changing architecture-sensitive behavior.
2. For any feature that exists or likely exists in Coolify, inspect <https://github.com/coollabsio/coolify> to understand the feature set, behavior, workflows, edge cases, and generated artifacts before designing the Vessel implementation.
3. Keep edits scoped to the requested feature or bug.
4. Preserve user changes already present in the worktree.
5. Add or update tests for behavior changes when the test project exists.
6. Update docs when public behavior, deployment shape, commands, or architectural rules change.
7. Run the narrowest useful verification command after changes and report anything that could not be run.

## Build and Test Commands

Use these when the solution exists:

```powershell
dotnet restore
dotnet build
dotnet test
```

For focused work, prefer the specific project or test filter that covers the change. If the .NET 11 stable SDK is not installed, report that clearly instead of changing target frameworks.

## Dependency Rules

- Prefer built-in .NET, ASP.NET Core, EF Core, and established project dependencies.
- Do not add heavy dependencies without a clear need.
- Keep `Vessel.Shared` lightweight.
- Keep `Vessel.Domain` free of infrastructure, HTTP, EF-specific, Hangfire, SignalR, Docker, SSH, and direct time dependencies.

## When Unsure

- Check the upstream Coolify repository to clarify intended product behavior before inventing new semantics.
- Follow existing repository patterns once they exist.
- Choose the modular monolith path over a distributed service split.
- Prefer explicit interfaces in Application and implementations in Infrastructure.
- Ask the user before making irreversible data, migration, deployment, or security-sensitive decisions.
