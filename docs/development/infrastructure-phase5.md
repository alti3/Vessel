# Phase 5 Infrastructure Core

Phase 5 establishes the host-integration boundary for Vessel. Application owns the contracts; Infrastructure owns process execution and external adapters.

## Process execution

All local command execution goes through `IProcessRunner`. The Infrastructure implementation uses .NET 11 Process APIs:

- `Process.RunAndCaptureTextAsync` for deadlock-safe text capture.
- `Process.RunAsync` for no-output commands.
- `Process.ReadAllBytesAsync` for binary capture.
- `Process.ReadAllLinesAsync` for ordered stdout/stderr streaming.
- `ProcessStartInfo.InheritedHandles = []` so child processes inherit only explicit standard handles.
- `File.OpenNullHandle()` for no-output execution.
- `ProcessStartInfo.KillOnParentExit` where supported by the installed SDK/runtime.

Detached process APIs such as `StartAndForget`, `StartDetached`, and `SafeProcessHandle.Start` are intentionally not used. Vessel requires managed lifecycle, cancellation, timeout, redaction, and audit metadata for external commands.

## External adapters

The first adapters are intentionally narrow and composable:

- Docker runtime API adapter uses Docker.DotNet for daemon inspection and inventory, with Docker/Podman CLI fallback for Compose/event workflows through `IProcessRunner`.
- Git operations use structured `git` invocations through `IProcessRunner`.
- SSH operations use structured `ssh`/`scp` invocations through `IProcessRunner`; host key policy is explicit.
- Redis cache and lock interfaces separate ephemeral coordination from durable PostgreSQL state.
- Object storage supports local development storage and S3-compatible storage.
- Hangfire is registered behind `IBackgroundJobDispatcher`.
- SignalR publishing is behind `IRealtimeNotifier` and uses deterministic group names.

## Safety notes

Access to `/var/run/docker.sock` is host-root-equivalent. Any deployment mode that mounts that socket gives Vessel the ability to control the host container runtime and must be treated as privileged.

The local object storage provider validates all object paths under its configured root before writing or reading. Process output is redacted before being returned to callers, persisted, or broadcast by higher layers.

## Upstream Coolify reference

Coolify upstream commit `49656aa` was inspected for Phase 5 behavior. The relevant areas were `bootstrap/helpers/remoteProcess.php`, `app/Traits/ExecuteRemoteCommand.php`, `bootstrap/helpers/docker.php`, `app/Helpers/SshMultiplexingHelper.php`, install/upgrade scripts, and tests covering redaction and Docker Compose command behavior. Vessel preserves the centralized remote execution, Docker/Compose, SSH, and redacted-log semantics while implementing them as Application-owned contracts with Infrastructure implementations.
