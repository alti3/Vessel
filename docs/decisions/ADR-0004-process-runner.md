# ADR-0004: Process Runner

## Status

Accepted

## Context

Vessel must call external tools for Docker Compose, Git, diagnostics, compatibility paths, and remote workflows. Direct process execution scattered through the codebase would make cancellation, redaction, auditing, timeouts, and standard handle safety inconsistent.

## Decision

All external command execution goes through an Application-owned `IProcessRunner` abstraction with Infrastructure implementations. Commands use structured arguments and explicit policies for working directory, environment, output capture, streaming, redaction, timeout, and termination.

## Consequences

Web components, controllers, hubs, job classes, Domain, and ordinary Application services must not call process APIs directly. The process layer becomes a security-sensitive infrastructure boundary and must be covered with focused tests before production use.
