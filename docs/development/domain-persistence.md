# Domain and Persistence Model

Phase 3 establishes Vessel's source-of-truth model for the control plane.

## Shape

- Domain entities live in `Vessel.Domain` and do not reference EF Core, ASP.NET Core, Hangfire, SignalR, Docker, SSH, Git, or infrastructure types.
- Application persistence contracts live in `Vessel.Application/Persistence`.
- EF Core implementation, PostgreSQL mappings, repositories, and migrations live in `Vessel.Infrastructure/Persistence`.
- Entity identifiers are strongly typed GUID value objects and are mapped explicitly in EF Core.
- Mutable source-of-truth records use `ConcurrencyStamp` as an optimistic concurrency token.

## Core Aggregates

The initial model covers users, teams, team memberships, projects, environments, servers, applications, database resources, deployments, secret references, notification targets, audit logs, and scoped settings.

Secret values are intentionally absent from domain entities. Domain and persistence records store only secret references, ownership metadata, and policy flags.

## Coolify Reference

The Phase 3 model was informed by the current Coolify upstream default branch at commit `49656aa`. The relevant areas inspected were:

- `app/Models/Team.php`, `User.php`, `Project.php`, `Environment.php`, `Server.php`
- `app/Models/Application.php`, `ApplicationDeploymentQueue.php`
- `app/Models/EnvironmentVariable.php`, `SharedEnvironmentVariable.php`
- `app/Models/StandalonePostgresql.php`
- Coolify migrations for users, teams, projects, environments, servers, applications, deployment queues, environment variables, standalone PostgreSQL, backups, settings, and shared variables

Vessel preserves the product semantics at this layer, but it does not port Laravel model events or PHP implementation patterns directly.
