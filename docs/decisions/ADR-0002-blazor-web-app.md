# ADR-0002: Blazor Web App

## Status

Accepted

## Context

Vessel needs an operations-oriented control panel with interactive dashboards, deployment logs, terminal sessions, forms, and resource management. The chosen stack is .NET-first and should avoid a separate frontend runtime unless it becomes necessary.

## Decision

Use Blazor Web App with Interactive Server-first rendering for the primary dashboard. Static SSR can be used for simple unauthenticated or non-interactive pages. Controllers remain responsible for APIs and integration endpoints.

## Consequences

The UI can share the .NET host and deployment model. Blazor components must remain thin: they render state and call Application-facing services or clients, but they do not run deployments, Docker, SSH, Git, EF `DbContext`, or process execution directly.
