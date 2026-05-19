# Phase 8 Deployment MVP

Phase 8 wires the first end-to-end deployment path while preserving Vessel's modular monolith boundaries.

## Behavior

- `StartDeploymentService` validates `deployments.start`, resource ownership, and active deployment state before creating a queued deployment.
- `RunDeploymentJob` is intentionally thin and delegates execution to `IDeploymentRunner`.
- `DeploymentRunner` records state transitions, performs Git clone/checkout through `IGitClient`, writes deterministic `.env` and Compose snapshots through `IDeploymentWorkspaceManager`, and starts containers through `IContainerRuntimeClient`.
- Deployment logs are persisted incrementally on the deployment aggregate and published to the deployment SignalR group as redacted `deployment.log` messages.
- Status changes are persisted and published to deployment and application groups as `deployment.status` messages.
- Cancellation moves running deployments to `CancelRequested`; the runner observes this between major steps and then records `CanceledByUser`.

## Runtime Scope

The MVP supports local Docker/Podman targets through the runtime abstraction. SSH execution remains behind the runtime boundary for a later phase rather than being implemented directly in Web, jobs, or Application code.

Generated files are written under the Infrastructure-owned deployment workspace:

```text
storage/deployments/{deploymentId}/
  .env
  docker-compose.yml
  snapshots/
    docker-compose.redacted.yml
    env.redacted
```

The repository checkout directory is removed after the run. Redacted snapshots and deployment logs are retained for audit and rollback visibility.

## Coolify Reference

Coolify upstream default branch was inspected for `ApplicationDeploymentJob`, `ApplicationDeploymentQueue`, deployment pages, deployment queue helpers, and Docker Compose/database start actions. Vessel preserves the user-facing queue, status, cancellation, redacted ordered log, commit capture, generated config snapshot, and Compose lifecycle semantics while implementing them through .NET application contracts and infrastructure adapters rather than Laravel jobs/models.
