# ADR-0005: PostgreSQL

## Status

Accepted

## Context

Vessel needs a reliable source of truth for teams, users, projects, servers, applications, deployments, logs, settings, audit records, notifications, jobs, and operational metadata. The platform also needs transactional consistency, indexing, migrations, and strong integration with .NET persistence tooling.

## Decision

Use PostgreSQL as the primary source of truth. Use EF Core and Npgsql for normal persistence, migrations, and transactional queries. Raw SQL or Dapper may be used only for hot paths, reporting, large log queries, batch operations, or PostgreSQL-specific features where EF Core is not a good fit.

## Consequences

Redis remains cache and coordination infrastructure, not the source of truth for critical records. Schema changes require migrations. Persistence implementation belongs in Infrastructure, with Application depending on interfaces where useful.
