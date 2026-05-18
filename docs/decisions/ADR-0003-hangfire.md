# ADR-0003: Hangfire

## Status

Accepted

## Context

Vessel needs durable background execution for deployments, health checks, backups, cleanup, certificate renewal, monitoring, notification delivery, and webhook processing. These workflows need retries, scheduling, observability, and operational control.

## Decision

Use Hangfire for background job scheduling and execution. Hangfire job classes stay thin and delegate to Application services. PostgreSQL is the preferred Hangfire storage once persistence is added.

## Consequences

Durable work is kept inside the monolith initially while preserving a future path to separate workers. Jobs must record state transitions, support cancellation where practical, persist logs incrementally, emit realtime updates through abstractions, respect timeouts, and avoid embedding orchestration logic in the job class itself.
