# Security Policy

Vessel is in early alpha and is not yet suitable for production use.

## Supported Versions

No stable release is supported yet. Security fixes will be handled on the active development branch until the project publishes versioned releases.

| Version | Supported |
|---|---|
| Unreleased alpha | Best effort |
| Stable releases | Not available yet |

## Reporting a Vulnerability

Do not open a public issue for a vulnerability that could expose hosts, secrets, deployment credentials, terminal sessions, tokens, backups, or tenant data.

Until a dedicated security contact is published, report vulnerabilities privately to the repository owner or maintainer through the hosting platform's private contact mechanism. Include:

- Affected component or workflow.
- Reproduction steps.
- Expected impact.
- Whether secrets, host access, or tenant data may be exposed.
- Suggested fix, if known.

## Security Expectations

Vessel treats the following as high-risk areas:

- Docker socket access, including `/var/run/docker.sock`, which is host-root-equivalent.
- Terminal and remote command execution.
- SSH keys and remote server provisioning.
- Environment variables and secret storage.
- Webhooks and callback endpoints.
- Backups, restores, exported bundles, and object storage.
- Self-update and installer trust chain.

Secrets must be redacted from logs, process output, API responses, SignalR messages, exceptions, audit records, and documentation examples.
