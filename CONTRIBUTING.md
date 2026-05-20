# Contributing to Vessel

Vessel is an early-alpha, executable-first .NET rewrite of Coolify. Contributions should preserve the modular monolith architecture and the operational boundaries documented in `AGENTS.md` and `PROJECT_REQUIREMENTS.md`.

## Development Prerequisites

- Stable .NET 11 SDK after official GA.
- Docker or Podman-compatible runtime for infrastructure work.
- PostgreSQL and Redis for integration work.

Do not downgrade the target framework to .NET 10 to make local builds pass. If the stable .NET 11 SDK is unavailable, document the blocker and keep changes limited to docs, repo policy, or other non-production scaffolding.

## Build and Test

Once the solution scaffold exists and the stable .NET 11 SDK is installed, use:

```powershell
dotnet restore
dotnet build
dotnet test
```

For focused changes, prefer the narrowest project or test filter that covers the behavior.

## Architecture Expectations

- `Vessel.Web` hosts UI, controllers, hubs, middleware, auth wiring, and endpoint mapping.
- `Vessel.Application` owns use cases, orchestration, commands, queries, job entry points, and infrastructure interfaces.
- `Vessel.Domain` owns pure business rules and has no infrastructure dependencies.
- `Vessel.Infrastructure` implements persistence, process execution, Docker/Podman, SSH, Git, Redis, Hangfire, storage, and external integrations.
- `Vessel.Shared` stays small and serialization-safe.

External commands must go through `IProcessRunner`. Do not call process, Docker, SSH, Git, or shell APIs directly from Web, Application services, hubs, controllers, jobs, or Domain.

## Pull Requests

Pull requests should:

- Keep scope focused.
- Include tests for behavior changes when the relevant test project exists.
- Include EF Core migrations when changes modify persisted models, indexes, foreign keys, constraints, or other database schema.
- Update docs or ADRs when public behavior, deployment shape, or architecture changes.
- Avoid unrelated formatting churn.
- Note verification commands run and any commands that could not run.

When a contribution requires a schema change, create the migration from the repository root and commit the generated files. Use a descriptive migration name for the feature or phase, for example:

```powershell
dotnet ef migrations add Phase10ProxyDomainsTls --project src\Vessel.Infrastructure\Vessel.Infrastructure.csproj --startup-project src\Vessel.Web\Vessel.Web.csproj --output-dir Persistence\Migrations
```

## Security

Do not include secrets in code, tests, logs, docs, screenshots, exceptions, or fixtures. Dangerous features such as terminal access, Docker socket access, SSH, file uploads, backups, self-update, and webhooks require explicit authorization, redaction, audit, and operational review.
