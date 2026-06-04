# Phase 12: Terminal, Logs, Monitoring, and Operational Views

Phase 12 adds safer operational visibility for deployment logs, terminal sessions, server health, realtime status, and dashboard metrics.

## Upstream Coolify Areas Consulted

Coolify behavior was used as the product reference for deployment log viewing, server/container terminal workflows, and operator status surfaces. Vessel preserves the user-facing semantics: authenticated operators can inspect deployment logs, authorized operators can open terminal-like operational sessions, and server/runtime health should surface in the dashboard. The implementation keeps Vessel's .NET modular-monolith boundaries instead of porting Coolify internals.

## Deployment Logs

- `DeploymentQueryService.GetLogs` supports authorized paging, stream filtering, search, resume after sequence, descending queries, and redaction.
- The API exposes `GET /api/v1/deployments/{deploymentId}/logs`.
- The API exposes `GET /api/v1/deployments/{deploymentId}/logs/export` for an authorized text export of the filtered page.
- The deployment details UI includes search, stream filters, and incremental loading.
- `DeploymentLogRetentionJob` is registered as `deployments.logs.prune` and runs daily with the default retention policy.
- Query models return projected log entries instead of requiring callers to load a deployment details view.

## Terminal Sessions

- `TerminalSession` persists owner, team, server, target type, command, status, dimensions, timestamps, and failure reason.
- `TerminalSessionManager` owns authorization, session lifecycle, audit, redaction, bridge delegation, and realtime terminal messages.
- `TerminalHub` only authorizes, joins groups, and forwards input, resize, and close requests.
- `TerminalsController` exposes open, inspect, input, resize, and close operations under the terminal rate-limit policy.
- The Blazor terminal page can open, resize, send input, close, and view current session status.
- The current Infrastructure bridge supports local server shells. Container terminals are intentionally rejected until a reviewed Docker exec PTY/SSH bridge is implemented.

## Monitoring

- `ServerHealthPollingJob` is registered as `servers.health.poll` every five minutes.
- Manual polling requires `servers.write`, team membership, and access to the target server.
- Polling records runtime reachability, running container count, proxy status, certificate status, and server status snapshots.
- CPU, memory, and disk fields are modeled and currently reported as unknown until a host metrics adapter is added.
- Dashboard metrics include active deployments, failures, queue length placeholder, unhealthy servers, active terminal sessions, and latest server health.

## Realtime Events

- Deployment log/status messages flow to deployment groups.
- Terminal output/status messages flow to terminal groups.
- Server health updates flow to server groups.
- Hubs remain transport-facing and do not contain deployment, Docker, shell, or terminal process logic.

## Verification

Phase gate verification should include:

```powershell
dotnet build Vessel.slnx --artifacts-path artifacts\phase12-build --verbosity minimal
dotnet test Vessel.slnx --no-restore --artifacts-path artifacts\phase12-build --verbosity minimal
tools\validate-project-references.ps1
```

Also check direct process usage remains limited to the approved Infrastructure process layer.
