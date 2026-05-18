# ADR-0001: Modular Monolith

## Status

Accepted

## Context

Vessel must ship as one coherent control plane while preserving clear boundaries for long-term maintainability. The product includes UI, APIs, background orchestration, persistence, realtime updates, and external runtime integrations. Starting with distributed services would add operational complexity before the domain and deployment workflows are stable.

## Decision

Vessel starts as a modular monolith with strict project boundaries:

- `Vessel.Web` hosts UI and HTTP/realtime endpoints.
- `Vessel.Application` owns use cases and integration abstractions.
- `Vessel.Domain` owns pure domain behavior.
- `Vessel.Infrastructure` implements external integrations.
- `Vessel.Shared` contains lightweight contracts only.

Separate deployables such as worker, agent, gateway, or notifications services are deferred until there is a clear operational need.

## Consequences

The first product has a simpler deployment and debugging model. Internal boundaries must be enforced through project references, code review, and tests so future extraction remains possible.
