# ADR-0006: SignalR

## Status

Accepted

## Context

Vessel needs realtime delivery for deployment logs, terminal sessions, deployment status, server health, resource events, notifications, and job progress. The primary UI is Blazor Server-first, and the host is already ASP.NET Core.

## Decision

Use SignalR for realtime communication. Hubs authenticate, authorize, join deterministic groups, and forward messages through Application-facing services. SignalR is a transport, not the source of truth.

## Consequences

Deployment state, logs, and operational records must still be persisted to PostgreSQL or appropriate durable storage. SignalR scale-out can be added later with Redis backplane and sticky-session guidance when multi-instance hosting is supported.
