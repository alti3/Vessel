# ADR-0007: Executable-First Distribution

## Status

Accepted

## Context

Vessel manages external container runtimes and host resources. Running the control plane only inside the container runtime it manages would make production operations, debugging, service management, and privilege boundaries harder to reason about.

## Decision

The preferred production distribution is a host-native, self-contained `vessel` executable managed by the host OS, normally through systemd on Linux. Dockerized control-plane mode is allowed for development, demos, compatibility, and operator preference, but it is secondary.

## Consequences

Production design favors predictable host paths, journald/systemd integration, atomic update and rollback, and direct host debuggability. Any Dockerized mode that mounts the Docker socket must document that `/var/run/docker.sock` is host-root-equivalent.
