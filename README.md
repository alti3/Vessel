# Vessel

**Vessel** is a ground-up C#/.NET rewrite of [Coolify](https://coolify.io): a robust, high-performance, type-safe and self-hosted platform for deploying and managing applications, databases, services, logs, terminals, and related infrastructure.

> [!WARNING]
> **Project Status: Early Alpha / Work in Progress**
> This project is in active initial development. Many features are missing, and the architecture is subject to change. It is not yet suitable for production use.

## Product Direction

Vessel is designed to be **executable-first**.

The primary production shape is a host-native, self-contained `vessel` binary running as an OS-managed daemon, usually through `systemd` on Linux:

```text
Host OS
├─ vessel / vesseld
│  ├─ API
│  ├─ Blazor Web UI
│  ├─ scheduler and background jobs
│  ├─ deployment orchestration
│  ├─ reverse proxy management
│  └─ runtime adapters
├─ Docker / Podman / future OCI runtime
└─ application containers
```

Containerized deployment is still supported for development, demos, cloud templates, and operators who prefer Docker Compose, but it is not the preferred control-plane architecture.

## Architecture

Vessel is a **modular monolith** following **Clean Architecture** principles. The first product is one deployable application with strict internal boundaries, so worker, agent, gateway, or notification processes can be extracted later if needed.

Project responsibilities:

- **Vessel.Web**: Blazor Web App, APIs, SignalR hubs, middleware, auth wiring, and endpoint hosting.
- **Vessel.Application**: Use cases, orchestration, commands, queries, background job entry points, and infrastructure interfaces.
- **Vessel.Domain**: Entities, value objects, domain events, state machines, and business invariants.
- **Vessel.Infrastructure**: EF Core, PostgreSQL, Redis, Docker/Podman adapters, SSH, Git, process execution, Hangfire, storage, notifications, and external integrations.
- **Vessel.Shared**: Lightweight DTOs, contracts, and serialization-safe shared models.

## Runtime and Process Model

Vessel targets **.NET 11 after stable GA** and is intended to take advantage of modern .NET deployment options:

- self-contained Linux x64 and Linux arm64 binaries
- optional single-file publishing
- optional Native AOT and trimming where compatible
- .NET 11 process APIs through a centralized `IProcessRunner`

All external command execution must go through the infrastructure process layer. UI, controllers, hubs, jobs, and application services must not call `Process.Start`, Docker, Git, SSH, or shell commands directly.

## Container Runtime Access

Vessel should manage host runtimes from outside the workloads it creates.

For local Docker access on Linux, Vessel may use the host Docker socket:

```text
/var/run/docker.sock
```

Access to this socket is highly privileged because requests are handled by the host Docker daemon. A containerized Vessel deployment that mounts `docker.sock` must be treated as host-root-equivalent and documented accordingly.

The runtime layer should prefer stable APIs where practical, such as Docker Engine API, Podman API, or future containerd adapters. CLI execution remains useful for Docker Compose, builds, streaming output, diagnostics, and compatibility, but it should be structured, cancellable, auditable, and routed through `IProcessRunner`.

## Technology Stack

- **Runtime**: .NET 11, ASP.NET Core 11
- **Frontend**: Blazor Web App, Interactive Server-first
- **API/Realtime**: Controllers and SignalR
- **Database**: PostgreSQL, EF Core, Npgsql
- **Background Jobs**: Hangfire
- **Caching/Coordination**: Redis
- **Runtime Integration**: Docker first, Podman-compatible abstractions later
- **Remote Access**: SSH and Git
- **Observability**: OpenTelemetry and Serilog
- **Storage**: S3-compatible object storage

## Planned Install Model

The intended host install flow is:

```bash
curl -fsSL https://example.com/install.sh | sh
vessel server
```

The installer and updater should support versioned downloads, checksum verification, atomic replacement, service restart, health checks, and rollback:

```bash
vessel self-update
```

Reference install scripts for the eventual Vessel installer:

- Astral uv Bash installer: <https://releases.astral.sh/installers/uv/latest/uv-installer.sh>
- Bun Bash installer for Linux and macOS: <https://bun.sh/install>
- Bun PowerShell installer for Windows: <https://bun.sh/install.ps1>

## Key Features

- Application deployment from Git providers.
- Server and runtime management through Docker/Podman adapters and SSH.
- Database and service provisioning.
- Live deployment logs, status updates, and terminal sessions through SignalR.
- Background deployment execution through Hangfire.
- Reverse proxy integration for Traefik, Caddy, Nginx, or a custom provider.
- Auditable, redacted process execution and deployment logs.

## Requirements

Development requirements:

- .NET 11 SDK after stable GA
- Docker and Docker Compose
- PostgreSQL
- Redis

Production requirements will depend on install mode. The preferred production path is a host-native Vessel daemon with access to a supported container runtime.

## License

License information to be added.

For detailed architectural guidelines and agent instructions, see [AGENTS.md](./AGENTS.md).
