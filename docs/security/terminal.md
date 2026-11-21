# Terminal Security

Terminal sessions are privileged operational access. Treat every open session as equivalent to direct access to the target server or container user.

## Access Control

- Opening a session requires the `terminals.open` permission and access to the target server (resolved against the server's owning team).
- Host terminals (servers with a `Local` or `DockerSocket` connection) open a shell on the Vessel control-plane host and are host-root-equivalent. They require an **instance administrator**; team Owner/Admin roles are not sufficient.
- SSH server terminals run `ssh -tt` over the server's own hardened connection (team key, pinned host key; see [remote-servers.md](./remote-servers.md)). Vessel never substitutes a control-plane shell for a remote server.
- Container terminals (`targetType: Container`) name the owning resource (`resourceKind` Application/Database/Service + `resourceId`), never a raw command. The container is resolved server-side (database container name, or `docker ps --filter label=vessel.application=<id>` / the service's compose project); a requested `containerName` must be one of the resource's containers. Modes:
  - `Shell`: `docker exec -it <container> sh -c '<bash → ash → sh fallback>'` (structured arguments; the container name is its own argument).
  - `Attach`: `docker attach --sig-proxy=false <container>`, preceded by the last 200 log lines (redacted).
- Each server has a `terminalAccessEnabled` switch (default on, `PATCH /api/v1/servers/{id}`).
- Session ownership is persisted; a user can only join or operate a terminal session they opened.
- Terminal API endpoints use the terminal rate-limit policy.

## Attach Tokens

`POST /api/v1/terminals` authorizes the target, records the session (`Opening`) and returns a single-use attach token valid for 60 seconds, bound to the user, active team and session. Only the SHA-256 hash is kept, in memory (terminal processes are node-local). The process starts when the token is redeemed through the hub (`JoinTerminal(sessionId, token)`); a forged, expired, reused or foreign token is refused. `POST /api/v1/terminals/{id}/token` issues a new token for a live session of the caller.

## Continuous Re-authorization

Every input and resize re-checks access. In addition `TerminalSessionRevalidationService` (every 15 s) ends a live session when the owner no longer exists, lost `terminals.open` or membership in the server's team, the server disabled terminals, a host-local session's owner is no longer an instance administrator, a container session's resource moved or left the team, the session was idle for 15 minutes, outlived its 2-hour lifetime, or was never attached. Revocations are audited as `terminal.session_revoked`. Closing the SignalR connection that joined a session closes the session.

## Audit Trail

Vessel records audit events for terminal open, close, failure, revocation and input activity. Input audit metadata records length only, not the submitted command text.

Terminal session rows include owner, team, target server, target type, mode, owning resource, container name when applicable, a command description, dimensions, timestamps, status, and failure reason.

## Process Boundary

Blazor components, controllers, and SignalR hubs do not execute shells or runtime commands. They call Application services. The Infrastructure terminal bridge owns process lifetime and cancellation.

On Linux the `PtyTerminalBridge` allocates a real pseudo-terminal (`posix_openpt`) and starts the child through `IProcessRunner` as `sh -c 'exec setsid -c -w "$@" <pts >pts 2>&1'`, so the child is a session leader with the PTY as its controlling terminal (requires util-linux `setsid`). Resize sets `TIOCSWINSZ` on the master, delivering `SIGWINCH` to the foreground program (also to `ssh -tt`, which forwards it). Other platforms fall back to the pipe-based bridge, which only supports host-local server shells.

The shell never inherits the daemon environment. `ProcessCommand.InheritEnvironment = false` clears the child environment and the bridge passes an explicit allowlist (`PATH`, `HOME`, `USER`, `LOGNAME`, `SHELL`, `LANG`, `LC_ALL`, `TZ`, Windows process essentials, plus `TERM`). Values such as `Secrets__MasterKey` or database connection strings are therefore not visible in a session. Host shells still run as the Vessel service user, so they can read files that user can read; this is why host terminals are limited to instance administrators.

## Redaction Limits

Terminal output is passed through the shared secret redactor before realtime publication. Redaction is best-effort:

- It can remove configured secret values and common secret-shaped text.
- It cannot guarantee removal of secrets that are transformed, encoded, split across chunks, or produced before a secret is known to Vessel.
- Users should not paste secrets into terminals unless operationally necessary.

## Operational Guidance

- Prefer scoped, short-lived terminal sessions.
- Close sessions after use; idle and lifetime limits are enforced by the revalidator and the bridge.
- Keep Docker socket access restricted. Access to `/var/run/docker.sock` is host-root-equivalent; this is why creating, targeting, probing, deploying to, or opening terminals on `Local`/`DockerSocket` servers requires an instance administrator.
- Review audit logs after emergency terminal use.
- Do not rely on terminal sessions for durable automation. Use Hangfire-backed jobs and Application workflows for repeatable operations.
