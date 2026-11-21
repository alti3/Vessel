# ADR-0009: Host metrics collection: agentless first, optional host-native agent later

## Status

Accepted

## Context

Operators expect CPU, memory, disk, load and network charts for every server, a fleet overview, the top
containers by usage, real resource status (running, exited, restarting, unhealthy, degraded) and alerts on disk
usage. Upstream Coolify gets all of this from **Sentinel**, a container it has made mandatory on every regular
server since v4.3.19:

- Sentinel runs with `/var/run/docker.sock` mounted, `--pid host` and `--userns host`. That is host-root-equivalent
  access held by a long-lived, network-reachable, third-party process on every managed host.
- It pushes container state and metrics to `POST /api/v1/sentinel/push` with a bearer token. That token is reversible
  encrypted JSON that names the server.
- It stores metrics on the server and serves them on `localhost:8888`, which Coolify reads over SSH.
- It has a CVE history (Sentinel 1.1.2 fixed CVE-2026-111943). Its image, update cadence and supply chain belong to
  upstream.

Vessel's architecture rules ([`AGENTS.md`](../../AGENTS.md), `PROJECT_REQUIREMENTS.md`) say four things that apply
here:

- Docker socket access is host-root-equivalent and must be documented.
- The control plane is host-native and is not a privileged container.
- Every external command goes through `IProcessRunner` or `IServerCommandRunner` with structured arguments.
- A separate deployable such as `Vessel.Agent` needs explicit approval.

Vessel already holds an SSH connection to every remote server, with pinned host keys and team keys, and it already
runs the runtime CLI over it for deployments.

## Decision

Vessel collects telemetry **agentlessly** (SRV-03, Phase A). It does not adopt the Sentinel image or any other
privileged agent container.

- **One read-only command per collection.** `AgentlessTelemetryCollector` runs one constant `sh -c` script per
  collection through `IServerCommandRunner`. For SSH servers it runs over the server's own connection, with the pinned
  host key and the team key. For host-local servers it runs on the control-plane host, and only when an instance
  administrator authorizes it.
- **What the script reads:**
  - two `/proc/stat`, `/proc/net/dev` and `/proc/uptime` readings one second apart, which give the CPU and network rates
  - `/proc/meminfo` and `/proc/loadavg`
  - `df -Pk` for `/`, the Docker and Podman data roots, `/var/lib/vessel` and `/data`
  - `docker stats --no-stream`
  - a structured container `inspect` that reads identity, image, labels, state, health and restart count, and never the
    environment, command or mounts
- **No interpolation.** The runtime CLI and the Go templates are passed as positional arguments and never
  interpolated into the script. Parsers are pure and lenient: a malformed section yields unknown values.
- **Scheduling.** A Hangfire dispatcher runs every minute and schedules one `CollectServerMetricsJob` per server. A
  stable per-server offset staggers the jobs. Collection is single-flight per server (distributed lock), has a
  60-second timeout, and waits out a 2-minute cooldown after a failure, so an unreachable host never blocks the fleet.
  Collection can be switched off per server, and the interval is configurable (30 s to 1 h).
- **Storage.** Data lives in PostgreSQL:
  - `server_metric_samples`: raw rows kept 24 hours, plus hourly rollups kept 30 days
  - `container_metric_samples`: kept 24 hours

  An hourly retention job creates the rollups idempotently and deletes expired rows with set-based deletes.
- **Consumers of the same snapshot:**
  - resource runtime status reconciliation (SRV-04); missing resources count as exited only when the inventory command
    succeeded
  - the disk-usage alert (SRV-08)
  - disk-threshold Docker cleanup (SRV-07)
- **API.** `GET /api/v1/servers/{id}/metrics` (time range, `raw`/`hourly`/`auto`), `GET /api/v1/metrics/fleet` (KPIs and
  the top 20 containers by CPU and by memory), `POST /api/v1/servers/{id}/metrics/collect`, and
  `GET`/`PATCH /api/v1/servers/{id}/monitoring`. Pressure levels follow upstream: CPU and memory 70/90 %, disk 80/90 %.

## Consequences

- **No new privileged surface.** Vessel adds no long-lived, privileged process on managed hosts. The access Vessel
  already has (SSH, plus the runtime CLI it already uses for deployments) is the only access used. Nothing on the host
  listens on a port or holds a Vessel credential.
- **Coarser data than Sentinel.** Resolution is the collection interval (default 60 s), not Sentinel's sub-minute
  push. Container status is as fresh as the last collection. Proxy access-log analytics, which Sentinel also ingests,
  are not covered.
- **Cost per collection.** Each collection opens an SSH command (multiplexed where the SSH layer allows) and runs
  `docker stats --no-stream`, which takes about 1–2 s on busy hosts. At fleet scale the stagger and single-flight
  limits keep that bounded.

### Conditions for a future host-native agent (Phase B)

A `Vessel.Agent` would be considered, with explicit approval, only when one of these holds:

- Fleets reach a size where per-minute SSH collection is a measurable control-plane bottleneck, or SSH connection
  limits are hit.
- Product needs sub-minute status or metrics (for example push-based health or log shipping) that polling cannot meet.
- Servers must be monitored without an inbound SSH path from the control plane (NAT or outbound-only networks).

If built, the agent must meet all of these:

- It is a host-native systemd binary, not a container with `docker.sock`, `--pid host` or `--userns host`.
- It reads only what this ADR's collector reads.
- It pushes to `POST /api/v1/agent/push` with a per-server **hashed** token delivered in a `0600` env file, never
  reversible encryption. It reports its version by header.
- The push endpoint mirrors upstream's Retry-After contract for bad tokens, unknown servers and non-functional servers.
- A heartbeat-driven liveness state (waiting since / live / out of sync, with the last push error) suppresses SSH
  polling only while the agent is live. Agentless collection stays the fallback.
- It has its own ADR covering update, rollback and supply-chain signing.
