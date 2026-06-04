# Terminal Security

Terminal sessions are privileged operational access. Treat every open session as equivalent to direct access to the target server or container user.

## Access Control

- Opening a session requires the `terminals.open` permission.
- The caller must also have access to the target server.
- Session ownership is persisted; a user can only join or operate a terminal session they opened.
- Terminal API endpoints use the terminal rate-limit policy.

## Audit Trail

Vessel records audit events for terminal open, close, failure, and input activity. Input audit metadata records length only, not the submitted command text.

Terminal session rows include owner, team, target server, target type, container name when applicable, command policy, dimensions, timestamps, status, and failure reason.

## Process Boundary

Blazor components, controllers, and SignalR hubs do not execute shells or runtime commands. They call Application services. The Infrastructure terminal bridge owns process lifetime and cancellation.

The current bridge supports local server shells through the Infrastructure process layer. Container terminal sessions are modeled and authorized, but the Infrastructure bridge rejects them until a reviewed Docker exec PTY/SSH bridge is added.

## Redaction Limits

Terminal output is passed through the shared secret redactor before persistence-adjacent messages and realtime publication. Redaction is best-effort:

- It can remove configured secret values and common secret-shaped text.
- It cannot guarantee removal of secrets that are transformed, encoded, split across chunks, or produced before a secret is known to Vessel.
- Users should not paste secrets into terminals unless operationally necessary.

## Operational Guidance

- Prefer scoped, short-lived terminal sessions.
- Close sessions after use; the bridge also enforces a maximum lifetime.
- Keep Docker socket access restricted. Access to `/var/run/docker.sock` is host-root-equivalent.
- Review audit logs after emergency terminal use.
- Do not rely on terminal sessions for durable automation. Use Hangfire-backed jobs and Application workflows for repeatable operations.
