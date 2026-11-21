# Coolify Upstream Gap Report — v4.1.0 → v4.7.6

| | |
|---|---|
| Report date | 2026-11-20 |
| Vessel baseline | `a8a659a` (Phase 13, 2026-06-05); roadmap Phases 0–13 complete, 14–18 not started |
| Coolify baseline | `49656aa` = **v4.1.0** (2026-05-18), the commit Phases 3–13 were modelled on |
| Coolify target | `094f9ada2` on `main` (2026-11-20); latest release **v4.7.6** (2026-11-19) |
| Upstream delta | 3,494 commits · 52 releases · 201 migrations · ~1,100 changed app files |
| Gap items | **150** (144 domain + 6 platform) — **P0 18 · P1 63 · P2 49 · P3 20** |

## 1. Executive Summary

Over the five and a half months since Vessel was last worked on, Coolify shipped seven minor release lines (v4.2 → v4.7), a coordinated security backport to every maintained line on 2026-10-21 (v4.0.1, v4.1.3, v4.2.1, v4.3.24, v4.4.7), and a preview of a v5 rewrite (Inertia + Svelte on the `next` branch). The changes fall into four groups.

1. **Security-driven breaking changes that should shape Vessel's current design.**
   - The **Member role is read-only** (v4.2.0).
   - **State-changing API calls are POST-only** (v4.2.0).
   - API tokens gained **`deploy`, `delete` and `read:sensitive` abilities** (v4.5.0–v4.6.0).
   - **Per-project, environment and resource access grants** apply to members and tokens (v4.6.0–v4.7.0).
   - **SSH host keys are pinned** for servers and Git hosts (v4.7.0).
   - A **failed-request rate limit** applies per token or IP (v4.7.0).
   - Secrets are **encrypted at rest**, with key re-encryption (v4.5.0).
   - **Compose `scale`/`replicas` > 1 is rejected** (v4.5.0).
   - **`host_path` volumes were removed** (v4.3.22).
   - **Older queued webhook deployments are auto-cancelled** when a newer push arrives (v4.7.0).
2. **New subsystems.**
   - An MCP server (82 files).
   - Image and host vulnerability scanning with deploy gates (Syft/Grype, CISA KEV and EPSS).
   - Managed Cloudflare DNS, with per-domain DNS status checks.
   - Scheduled volume backups to S3, SFTP, FTPS or WebDAV, with S3 replicas.
   - Database import, plus SQLite as a new engine.
   - Passkeys, an OAuth/OIDC overhaul, and team-required 2FA.
   - Sentinel, now mandatory, with fleet metrics and traffic analytics.
   - Server transfer between instances.
   - Secret managers (Vault, Infisical, Doppler).
   - Gitea and GitLab App sources, and GitHub Actions runners.
   - Server patching, cloud provisioning (DigitalOcean, Vultr, Hostinger), and Cloudflare Tunnel.
   - Maintenance mode and custom error pages.
   - Update channels, and a backup of the Coolify database before each update.
3. **Semantic refinements to features Vessel already has.**
   - Redirects are temporary by default.
   - Every TLS host gets an HTTP router.
   - Router names are stable and hashed.
   - Deployment queueing and cancellation.
   - Preview isolation and fork gating.
   - Notification throttling and failure tracking.
   - Backup compression and streaming.
   - Database health checks.
   - Pinned default images, with Valkey as the default Redis image.
4. **Catalog churn.** 36 templates added, 2 removed and 334 modified (about 390 templates in total).

**The most important finding is about Vessel itself, not upstream.** Checking each upstream behavior against Vessel's actual code exposed **16 P0 defects in Vessel's existing Phase 4–13 implementation**:

- Cross-tenant webhook verification (SEC-01).
- Team mixing in authorization (SEC-02).
- A host shell exposed to any tenant (SEC-03, SRV-01).
- A hard-coded fallback encryption key (SEC-04).
- Bearer tokens that can mint root tokens (IAM-01).
- Unauthenticated notification-target changes (IAM-02, NTF-02).
- Webhook and commit deployments that cannot succeed (DEP-01).
- Previews that overwrite production (DEP-02).
- Invalid generated Traefik YAML (NET-01).
- No proxy lifecycle on remote servers (NET-02).
- Backups capped at 10 MiB (DATA-01).
- An unsafe synchronous restore (DATA-02).
- A notification pipeline with no producers (NTF-01).

Several roadmap phases marked `[x]` are therefore not functionally complete. These items come first, ahead of any new upstream feature.

Platform: **.NET 11 reached GA on 2026-11-10.** The repository was pinned to `11.0.100-preview.4` and has been moved to the GA SDK (PLT-01). One of the pinned dependencies (SSH.NET 2025.1.0) carries two high-severity advisories (PLT-02).

### Top priorities (Wave 1, P0)

| # | Item | Why it matters |
|---|---|---|
| 1 | SEC-01, SEC-02, IAM-01, IAM-02/NTF-02 | Cross-tenant and privilege-escalation paths in authn/authz |
| 2 | SEC-03 / SRV-01, SEC-05 | Tenants can reach the control-plane host (shell, local Docker, symlinked repository files) |
| 3 | SEC-04 | A known fallback key decrypts every stored secret |
| 4 | DEP-01, DEP-02 | Webhook, commit and preview deployments are broken, and previews overwrite production |
| 5 | NET-01, NET-02 | Routing produces invalid configuration and never reaches remote servers |
| 6 | DATA-01, DATA-02 | Backups fail above 10 MiB, and restore is unsafe |
| 7 | NTF-01 | No notification is ever raised by a real event |
| 8 | PLT-01, PLT-02 | Build on .NET 11 GA; remove a vulnerable SSH library |

## 2. Upstream Timeline (v4.1.0 → v4.7.6)

| Version | Date | Headline changes |
|---|---|---|
| v4.1.1 – v4.1.2 | 2026-05-27 – 06-04 | Fixes on the v4.1 line (Vessel's late phases saw some of these) |
| v4.2.0 (pre-release) | 2026-07-19 | **Breaking:** Member role read-only; POST-only lifecycle endpoints. Scheduled volume backups, logs, move and destination APIs; service app/DB API; tags API; Vultr and DigitalOcean provisioning; MCP per-team toggle; protected DNS resolution for webhooks; hidden sensitive API fields |
| v4.3.0 – v4.3.23 | 2026-08-12 – 09-18 | Redesigned UI; domain DNS checks and automatic Cloudflare DNS; noindex domains; GitLab App source; MCP diagnostics; avatars; Traefik 3.7. **Breaking:** stable Compose router names (4.3.0), Sentinel mandatory (4.3.19), `host_path` volumes removed (4.3.22) |
| v4.4-rc.1 / v4.4.0 – v4.4.6 | 2026-08-19 – 10-09 | Large security pass (terminal session tokens, OAuth 2FA, git ref/env/container-name validation, Compose injection, path containment, webhook throttling, team-owned resource scoping). Integration tokens; managed DNS records; traffic analytics; ACME certificate management; Reverb replaces Soketi; SQLite; GitHub runners; secret managers (early) |
| Security backport | 2026-10-21 | v4.0.1 / v4.1.3 / v4.2.1 / v4.3.24 / v4.4.7: PHP 8.4.27 (PHP2Shell CVEs), cloudflared 2026.10.3, Sentinel 1.1.2 CVE |
| v4.5.0 – v4.5.3 | 2026-10-23 – 10-28 | Gitea App; server patching; fleet metrics; canvas; file browser; passkeys; maintenance mode and error pages; basic-auth hashing; permanent redirects (opt-in); certificate resolver per server; Hostinger; Cloudflare Tunnel; backup before update; GitLab system hooks; MCP management tools; Smart scan; secrets encrypted at rest; `app-key:reencrypt`. **Breaking:** `deploy` token ability, Compose `scale` > 1 rejected, `/enable` removed, `delete_from_coolify_only` removed, Highlight log drain removed |
| v4.6.0 | 2026-11-02 | **Breaking:** `delete` token ability. Access grants (read / read sensitive / deploy / write / delete / root) for members and tokens; update channels (stable/preview/nightly/custom); CI supply-chain hardening; locked env values hidden in list APIs |
| v4.7.0 – v4.7.6 | 2026-11-09 – 11-19 | **Breaking:** auto-cancel superseded webhook deployments; `cancel_previous` on deploy; cloud token API limited to admins; Resend sender fields renamed. SSH host-key pinning (server and Git); Ed25519 keys; failed-API-request throttle; image/host vulnerability scanning with deploy gate; Valkey default; PII-free audit events; PHP-FPM bound to loopback |

## 3. Upstream Breaking Changes and Their Effect on Vessel

| Upstream change | Version | Vessel today | Action | Item |
|---|---|---|---|---|
| Member role read-only | v4.2.0 | Member can deploy and cancel (`VesselPermissions.cs`) | Adopt: Member = read; new Developer/Operator presets via grants | IAM-03 |
| State-changing endpoints POST-only | v4.2.0 | Vessel controllers already use POST for mutations; verify webhook-style GET deploy triggers | Verify and lock with tests | API-01 |
| Stable hashed router names | v4.3.0 | Router names truncated and colliding | **Fixed** (NET-01) | NET-01 |
| Sentinel mandatory | v4.3.19 | No host agent; metrics null | Collect over SSH first (ADR); no privileged agent container | SRV-03 |
| `host_path` volumes removed | v4.3.22 | No persistent storage model yet | Design without host paths; owned volume paths only | DATA-12 |
| `deploy` token ability; `/enable` removed; Compose scale > 1 rejected | v4.5.0 | No ability model beyond read/write/root; Compose passed through unparsed | Add abilities; parse and validate Compose | IAM-04, DEP-11 |
| `delete` token ability | v4.6.0 | No delete scope | Add | IAM-04 |
| Access grants | v4.6.0 | Role-only checks, split across layers | Introduce `IAccessResolver` in Application | IAM-05 |
| Auto-cancel superseded webhook deployments | v4.7.0 | No queue at all; a second deploy throws | Build queue first, then cancellation policy | DEP-04, DEP-05 |
| Resend sender fields renamed | v4.7.0 | No Resend channel | Use the new field names from the start | NTF-06 |
| Cloud provider token API admin-only | v4.7.0 | No cloud provisioning | Apply when implemented | SRV-04 |

## 4. How to Read the Gap Items

Each item has: **Upstream** evidence (version, PR, files), **Coolify behavior (now)**, **Vessel today** (Missing / Partial / Drifted / Covered, with `path:line`), **Gap type**, **Priority**, **Recommended Vessel approach** (layer placement, idiomatic .NET, not a 1:1 PHP translation), **Roadmap fit**, and **Effort**.

- **P0:** security or correctness defect Vessel has or would ship with. Fix before anything else.
- **P1:** core parity or a breaking semantic that should shape current design.
- **P2:** valuable parity.
- **P3:** nice-to-have or niche.
- **N/A:** deliberately out of scope (listed per domain).
- **Effort:** S < 2 days · M ≈ 1 week · L ≈ 2–3 weeks · XL > 3 weeks.

**Overlaps.** Some defects were found independently by two domain reviews and are tracked once for implementation: IAM-02 ≡ NTF-02 (notification authorization), and SEC-03 ≈ SRV-01 (host-local execution). Cross-references inside the items name the owning item.

**Line numbers** in "Vessel today" refer to the `a8a659a` baseline. They shift as the items are implemented.

## 5. Implementation Plan

Work proceeds in priority waves. Every change follows the AGENTS.md rules: Domain/Application/Infrastructure boundaries, `IProcessRunner` only, EF Core migrations for every schema change, focused tests, and documentation updates. Status is tracked in the index (§6).

| Wave | Scope | Exit criteria |
|---|---|---|
| 0 | PLT-01 – PLT-02 (toolchain), NET-01 | Builds on .NET 11 GA; no vulnerable packages; valid Traefik output |
| 1 | All remaining P0 | No known cross-tenant, privilege-escalation or host-escape path; deployments, previews, routing, backup and restore work end to end on the supported path |
| 2 | P1 (breaking semantics first: IAM-03/04/05, DEP-04/05, DEP-10/11, NET-03/05/07, DATA-03/04/05) | Roles, abilities, grants, queueing and Compose validation match upstream semantics; core parity features present |
| 3 | P2 | Valuable parity features |
| 4 | P3 | Niche or optional features, or an explicit deferral with an ADR |

## 6. Gap Index

| ID | Title | Priority | Effort | Roadmap fit | Status |
|---|---|---|---|---|---|
| DATA-01 | Backups are buffered in memory, capped at 10 MiB, uncompressed and short-timed out | P0 | M | 11.08/11.09 (reopen), 16.04, 16.05 | Done |
| DATA-02 | Restore pipeline is synchronous, format-blind and unsafe | P0 | L | 11.12/11.13 (reopen), 15.10 | Done |
| DEP-01 | Commit-pinned and webhook deployments fail: shallow clone uses `--branch <sha>`, and PR refs are never fetched | P0 | S–M | Fixes 8.05/9.10/9.11; prerequisite for DEP-02/DEP-07. | Done |
| DEP-02 | Preview deployments are not isolated from production and fork PRs are not gated | P0 | M | Completes 9.12/9.13; cross-ref Phase 15.05. | Done |
| IAM-01 | Identity-management endpoints accept any bearer token, so a token can escalate itself or strip 2FA | P0 | S | 4.06/4.07/4.10 (reopen); 15.02. | Done |
| IAM-02 | Notification targets and notification events have no permission check | P0 | S | 13.x follow-up; 15.02, 15.16. | Done |
| NET-01 | Generated Traefik config is incorrect, and its blast radius is the whole server | P0 | M | Fix inside Phase 10 (10.03/10.04/10.13 regression), plus 15.05 and… | Done |
| NET-02 | Proxy lifecycle per server (static config, container, networks, status, NONE type) | P0 | L | Phase 10 gap (10.05 follow-up); 16.06 lock validation. | Done (network reconcile in health poll and Hangfire restart job remain, tracked with SRV-02) |
| NTF-01 | The notification pipeline is not connected to any producer | P0 | M | 13.01/13.08 (reopen); prerequisite for NTF-03..05. | Done |
| NTF-02 | Notification API and settings lack authorization, SSRF protection, and secret-safe URLs | P0 | M | 13.09 (reopen), 15.x security hardening. | Done |
| PLT-01 | Move from .NET 11 preview 4 to .NET 11 GA | P0 | S | Phase 1, PR §2 | Done |
| PLT-02 | Vulnerable and outdated dependencies | P0 | S | 15.14, 15.15 | Done (StackExchange.Redis 3.x and xunit.v3 4.x majors deferred to evaluation) |
| SEC-01 | Cross-tenant webhook signature bypass (verified-once, trusted-for-all) | P0 | S | Phase 9 (fix), 15.02/15.16. | Done |
| SEC-02 | Authorization mixes the active team's role with membership in any team | P0 | M | 15.02, 15.16 (move ahead of Phase 14). | Done |
| SEC-03 | Host-local execution available to every tenant (localhost/terminal boundary) | P0 | M | 15.01/15.02, plus Phase 4 follow-up (instance admin). | Done |
| SEC-04 | Secret encryption key management: hard-coded fallback key, no rotation or re-encryption, plaintext TOTP secrets | P0 | M | 15.08, Phase 14 (installer key generation). | Done |
| SEC-05 | Repository file reads follow symlinks; path containment is lexical only | P0 | S | 15.10, Phase 8 follow-up. | Done |
| SRV-01 | Server-targeted operations silently run on the Vessel host (health, connectivity, terminal) | P0 | S | 12.07, 12.11, 7.05 (re-open); 15.01/15.12. | Done |
| API-01 | No OpenAPI document and no API compatibility policy; upstream REST surface grew from 96 to 233 paths | P1 | M | 6.07 follow-up; New: Phase 6b "Public API completeness"; 17.x for… | Open |
| DATA-03 | Backup, restore and lifecycle operations do not exclude each other | P1 | S | 11.04, 11.12, 16.06 | Partial (shared database-op lock; Skipped status remains) |
| DATA-04 | Database health is never actually probed | P1 | M | 11.01-11.03 (reopen), 12.x monitoring | Open |
| DATA-05 | Credentials with `$` break in the generated `.env`, and the Redis password quoting semantics are missing | P1 | S | 11.01-11.03 | Open |
| DATA-06 | Team-scoped backup destinations (S3 storages) with validation, usability state and multiple destinations | P1 | L | 5.22/5.24 (extend), 11.08; New: Phase 11b "Backup destinations" | Open |
| DATA-07 | Backup schedule model parity (database selection, formats, retention, timeout, edit/delete) | P1 | L | 11.10, 11.11 (reopen) | Open |
| DATA-08 | Backup notifications, missing-backup alerts and failure throttling | P1 | M | 13.x (event producers), 11.10 | Open |
| DATA-09 | Scheduled volume and directory backups | P1 | XL | 11.08 (reopen); New: Phase 11b | Open |
| DATA-10 | Database import from upload, server path or S3 (API and UI) | P1 | L | 11.12; 17.x (migration) | Open |
| DATA-11 | Default images, Valkey and the "latest" default | P1 | S | 11.01-11.03 | Open |
| DATA-12 | Application and service persistent storage model (volumes and file mounts) | P1 | XL | 7.x/8.11 (extend); New: Phase 11b or a dedicated storage phase | Open |
| DATA-13 | Backup the control-plane database before self-update, and instance backup settings | P1 | M | 14.11-14.15, 14.20, 16.12 | Open |
| DATA-14 | Backup API contract (list, update, delete, executions, downloads) and sensitivity rules | P1 | M | 6.x API, 11.14 | Open |
| DEP-03 | Deployment log streaming redacts only known patterns, not environment values (`redact_all_env_values_in_logs`) | P1 | S | 15.07; 8.14. | Done |
| DEP-04 | Deployment queue admission: queue instead of reject, dedupe, limits, trigger source, stuck recovery | P1 | M | 8.02/8.04 rework; 16.05/16.06. | Done |
| DEP-05 | Auto-cancel superseded deployments (v4.7.0 breaking default) | P1 | M | New items in Phase 8/9 rework (8.20 extension). | Done |
| DEP-06 | Rollback (local and registry images, restricted targets) | P1 | M | New Phase 8 follow-up (or Phase 9b); 18.12. | Partial (local images; registry rollback needs registry push support) |
| DEP-07 | Preview lifecycle: cleanup, stop, API and settings | P1 | M | 9.14 completion; Phase 10 for preview domains (cross-ref Proxy… | Done |
| DEP-08 | Private Git sources (GitHub App, GitLab App, Gitea/Forgejo App, deploy keys, HTTPS credentials, SSH host-key pinning) | P1 | XL | 9.08 (marked done but only refs discovery exists) → new Phase 9b… | Open |
| DEP-09 | Build packs: Railpack (and Static) unimplemented, Nixpacks deprecated, build-time variables never passed | P1 | L | New Phase 8b "Build packs"; 17.06 import mapping. | Open |
| DEP-10 | Environment variable value semantics (exact values, literal/multiline, empty values, order, preview copies, duplicates) | P1 | M | 7.13/8.09 rework; 17.07. | Open |
| DEP-11 | Docker Compose build pack: parse and validate instead of raw passthrough | P1 | L | 8.08 rework; new Compose items; Phase 10 (routing) cross-ref. | Open |
| DEP-12 | Webhook fan-out and repository matching correctness | P1 | S–M | 9.10 fix. | Done |
| IAM-03 | Member role is read-only upstream; Vessel members can still deploy and cancel, and role hierarchy is not enforced | P1 | S–M | 4.08/4.09/4.16 (reopen). | Open |
| IAM-04 | Token ability model is behind (no `delete`, wrong hierarchy, no pause/roll/edit, no sensitive-response policy) | P1 | M | 4.06/4.07 (reopen). | Open |
| IAM-05 | Fine-grained access grants (team, project, environment and resource scopes) for members, invitations and tokens | P1 | XL | New: Phase 4b "Fine-grained access" (before Phase 15). | Open |
| IAM-06 | No instance-admin / root-team concept and no registration policy | P1 | M | 4.02 follow-up; 14.05 (CLI). | Open |
| IAM-07 | OIDC/OAuth sign-in with provider identities and registration policy (ROADMAP 4.04/4.05) | P1 | L | 4.04, 4.05. | Open |
| IAM-08 | Audit log falls short of upstream's team audit events (context, transactional writes, retention, query API/UI) | P1 | M–L | 4.13–4.15 (reopen); 15.09; 16.01. | Open |
| IAM-09 | Sessions and the active team: no revocation, no persisted current team, and login lands in an invited team the user is not a member of | P1 | M | 4.02/4.08 (reopen). | Open |
| IAM-10 | Transactional auth emails are not delivered (password reset, invitations, verification) | P1 | M | 4.02 (reopen); 13.x. | Done |
| NET-03 | Domain identity, validation and conflicts | P1 | M | Phase 10 (10.07) and 15.05. | Open |
| NET-04 | TLS certificate truth from Traefik `acme.json` (inventory, delete/unused, backup/restore) | P1 | M | Phase 10 (10.08–10.10 rework); new task, Phase 10 follow-up. | Open |
| NET-05 | Per-route HTTP features: force HTTPS, permanent redirects, www modes, compression, basic auth, noindex | P1 | M | Phase 10 (10.07/10.12 extension) | Open |
| NET-06 | Routing coverage for service-template resources and preview deployments | P1 | L | Phase 9 (9.12) and Phase 11 (11.05) follow-ups; Phase 10 | Open |
| NET-07 | Proxy configuration history semantics and custom dynamic configs | P1 | M | Phase 10 (10.06) | Open |
| NTF-03 | Channel delivery-failure tracking, auto-disable, and response classification | P1 | S | 13.08 (extend). | Done |
| NTF-04 | Notification throttling and dedupe | P1 | M | New: 13.13–13.14 (or Phase 16 reliability). | Partial (delayed resource-stopped recheck needs SRV-04 producer) |
| NTF-05 | Granular event catalog and per-event routing | P1 | M | 13.09 (extend), 17.10. | Done |
| NTF-06 | Email provider parity: instance transactional email, named SMTP servers, Resend, sender split, TLS modes, EHLO | P1 | L | 13.03 (reopen); auth Phase 4 password reset. | Done |
| NTF-07 | User-defined scheduled tasks (cron commands in application/service containers) | P1 | L | New (suggest Phase 11/12 follow-up, "11.16–11.20 Scheduled… | Open |
| NTF-08 | Scheduled-job runtime semantics (durable runs, missed runs, retries, timezone) | P1 | L | 11.10 (reopen), 5.25 (jobs foundation), 16.x reliability. | Open |
| OPS-01 | Update channels, version manifest, and automatic-update policy for Phase 14 | P1 | M | 14.11–14.15 (extend), new 14.23 "update channels & auto-update… | Open |
| OPS-02 | Pre-update control-plane database backup, upgrade status, job draining, and single-flight upgrades | P1 | M | 14.11–14.15, 14.20, 16.12. | Open |
| PLT-03 | YAML handling through a real parser/emitter | P1 | S | Phase 5 / 15.05 | Done |
| PLT-05 | CI supply-chain hardening | P1 | S | 15.14, 15.15, 18.06 | Done |
| PLT-06 | Ecosystem image pins for runtime components Vessel deploys | P1 | S–M | 10.x, 11.x, 15.14 | Open |
| SEC-06 | Preview deployments from forks run untrusted code without an opt-in | P1 | S | Phase 9 follow-up, 15.05. | Done |
| SEC-07 | Manual webhook secret strength, failure throttling, and real client IP | P1 | S | 15.04, Phase 9 follow-up. | Open |
| SEC-08 | Outbound URL / SSRF guard for notifications and SMTP | P1 | M | Phase 13 follow-up, 15.05. | Done |
| SEC-09 | Deployment and command log redaction lacks the secret context; redactor pattern parity | P1 | S | 15.07, 15.16. | Done |
| SEC-10 | Boundary validation for values passed to git, docker, ssh and generated YAML (git refs, branches, container names, host and user, Dockerfile path) | P1 | M | 15.05, Phase 8/9 follow-up. | Open |
| SEC-11 | Git-based Docker Compose validation and host-path policy | P1 | M | 15.05/15.10, Phase 8 follow-up. | Open |
| SEC-12 | SSH connection hardening foundation (host key pinning, fixed options, key lifecycle) | P1 | L | New items under Phase 15 (and the SSH runtime phase), 15.12. | Open |
| SEC-13 | API abuse controls: failed-request throttle and rate-limit coverage | P1 | S | 15.04, Phase 13. | Open |
| SRV-02 | Remote server execution foundation: private keys, real validation and install, server lifecycle API | P1 | L. | 7.04/7.05 (extend), new Phase 12b "Remote servers"; Security agent… | Open |
| SRV-03 | Host and container metrics, fleet Metrics page, and the host-agent decision (Sentinel equivalent) | P1 | L | 12.11/12.12 (re-open), 16.08; new ADR; Phase B is a new phase item. | Open |
| SRV-04 | Resource runtime status reconciliation, restart limits, and stop notifications | P1 | M. | 12.13 (extend); new item under Phase 12b. | Open |
| SRV-05 | Runtime (container) logs for applications, databases and services: API, UI, merging, safe export | P1 | M. | 12.01/12.04 (extend to runtime logs). | Open |
| SRV-06 | Container terminal (PTY exec/attach), short-lived session tokens, and continuous re-authorization | P1 | M. | 12.07–12.10 (re-open the container PTY). | Open |
| SRV-07 | Docker cleanup (scheduled, threshold-based, safe) and the server Images page | P1 | M. | New Phase 12b item; 16.04. | Open |
| SRV-08 | Disk-usage and reachability alerts (event producers for server notifications) | P1 | S–M. | 13.x follow-up; 12.11. | Open |
| TPL-01 | Service template format compatibility with the Coolify catalog | P1 | XL | 11.05–11.07 (reopen), 17.08, 17.12. | Open |
| API-02 | MCP server (83 tools, 2 prompts, 2 resources) with per-team toggle, IP allowlist and `is_mcp` deployments | P2 | L | New: Phase 19 "MCP server" (after Phase 6b). | Open |
| DATA-15 | Non-S3 backup storage types (SFTP, FTPS, WebDAV) | P2 | L | New: Phase 11b | Open |
| DATA-16 | Additional engines: MongoDB, KeyDB, Dragonfly, ClickHouse (with scheduled backups) and SQLite | P2 | L | 11.01-11.04 (extend) | Open |
| DATA-17 | File browser for applications, services and databases | P2 | L | 12.x; New: Phase 12b | Open |
| DATA-18 | Clone and migrate databases with volume data | P2 | M | New: Phase 11b; 17.x | Open |
| DATA-19 | Backup job reliability, housekeeping, indexes and table tuning | P2 | M | 16.01, 16.04, 16.05, 16.06 | Open |
| DATA-20 | Managed-service workspace location and atomic, owner-only file writes | P2 | S | 14.07, 15.10 | Open |
| DEP-13 | Rolling updates, drain delay and health-check semantics | P2 | L | 8.12/8.13 rework; 10.x proxy switch. | Open |
| DEP-14 | Restart limits and crash-loop tracking (opt-in) | P2 | M | Phase 12 monitoring follow-up + Phase 13 event type. | Open |
| DEP-15 | Move, clone and migrate resources | P2 | M | New Phase 7b items; Phase 11 data (cross-ref Data agent for volume… | Open |
| DEP-16 | Deploy API and UX parity (deploy params, preview/commit authorization, history, commit details) | P2 | S–M | 6.x API conventions, 8.17-8.19, 13.x. | Open |
| DEP-17 | Smart scan, repository detection for new Git applications | P2 | S–M | New Phase 7/9 UX item. | Open |
| DEP-18 | Build servers, concurrent build limits and multi-server application deployments | P2 | L | New phase after 14 (or a Phase 8c); cross-ref Servers agent. | Open |
| IAM-11 | API IP allowlist and instance API enable/disable | P2 | S | 2.13 / 15.04. | Open |
| IAM-12 | Team-required 2FA, 2FA status visibility, and step-up re-authentication | P2 | M | 4.03 follow-up. | Open |
| IAM-13 | Account and team deletion lifecycle | P2 | M | 4.08 follow-up. | Open |
| IAM-14 | Integration tokens (team-scoped third-party credentials for Cloudflare, Doppler, Infisical, Vault) | P2 | M | New (with the DNS / secret-manager phase). | Open |
| IAM-15 | Passkeys (WebAuthn) sign-in | P2 | M | 4.03 follow-up. | Open |
| INT-01 | External secret managers (HashiCorp Vault, Infisical, Doppler) as `{{vault.KEY}}` references | P2 | L | New (Phase 15 or a new "Integrations" phase); 17.07 import of… | Open |
| NET-08 | Domain DNS status checks, public IPs, resolver settings | P2 | M | New: Phase 10 follow-up (could sit in Phase 12/monitoring) | Open |
| NET-09 | Managed Cloudflare DNS records (integration tokens, zones, ownership, release) | P2 | L | New phase (Edge/DNS integrations) or a Phase 10 extension | Open |
| NET-10 | Maintenance mode and custom proxy error pages | P2 | M | New: Phase 10 follow-up | Open |
| NET-11 | Traefik certificate resolver per server, with per-resource override | P2 | S | Phase 10 (10.09) | Open |
| NET-12 | Traefik version pinning, outdated tracking and security-patch cadence | P2 | S | Phase 10 / Phase 13; 15.14 | Open |
| NET-13 | Server proxy API contract | P2 | S | Phase 6 API surface plus Phase 10 | Open |
| NET-14 | Default/wildcard generated domains (sslip.io) and catch-all redirect | P2 | S | Phase 10 / Phase 7 (7.08) | Open |
| NTF-09 | Channel feature parity and operator UX (Pushover, test send, typed forms, per-channel API) | P2 | M | 13.03–13.09 (extend). | Open |
| OPS-03 | Realtime team scoping: stale team after a switch, team-channel payload exposure, scale-out | P2 | M | 6.18/6.21 (extend), 13.11, Phase 16. | Open |
| OPS-04 | Secret re-encryption command after master-key rotation | P2 | S | 14.05 (CLI), 15.x. | Open |
| OPS-05 | Installer and upgrade-script lessons for the Phase 14 installer | P2 | S | 14.06, 14.08, 14.09, 14.21. | Open |
| PLT-04 | Official C# MCP SDK available for the MCP server | P2 | S | New (API-02) | Open |
| SCAN-01 | Container image and host package vulnerability scanning (scanner pipeline, SBOMs, rescans, results) | P2 | XL. | New phase ("Supply-chain security"); relates to 15.15. | Open |
| SCAN-02 | Scan policies, deploy gate (off/on/strict), ignores, and vulnerability notifications | P2 | L. | New phase (with SCAN-01). | Open |
| SEC-14 | Git host key pinning for clones and fetches | P2 | M | Phase 9 follow-up / Phase 15. | Open |
| SEC-15 | Terminal sessions must end when access is lost | P2 | S | Phase 12 follow-up, 15.02. | Open |
| SEC-16 | Audit events: PII and credential hygiene, anonymization on user delete | P2 | S | 15.09. | Open |
| SEC-17 | Supply chain and runtime pinning: CI hardening, lockfiles, pinned images, CVE response | P2 | M | 15.14, 15.15, 18.04–18.06, Phase 14. | Open |
| SEC-18 | Host-header-independent URLs for links and callbacks | P2 | S | Phase 14 (installer config), 15.05. | Open |
| SRV-09 | Server roles (deployment / build / both), build fallback, and build server restrictions | P2 | S | New, Phase 12b/Deployments. | Open |
| SRV-10 | SSH key lifecycle: rotation across servers, authorized_keys management, unused key cleanup | P2 | M. | New (Phase 15 adjacent). | Open |
| SRV-11 | Server patching: patch checks, automatic security updates, reboot, and removal of unused packages | P2 | L. | New (Phase 12b / host management). | Open |
| SRV-12 | Log drains (New Relic, Axiom with edge domain, AWS CloudWatch, custom Fluent Bit with validation) | P2 | M. | New (Phase 12b / Ops). | Open |
| SRV-13 | Docker registry logins per server vs. Vessel's stored registry credentials | P2 | M. | 7.17 (complete), 8.x. | Open |
| SRV-14 | Import from Coolify's server-transfer bundle (Phase 17 path) | P2 | XL | 17.02, 17.03, 17.05, 17.06, 17.07, 17.13. | Open |
| SRV-15 | Cloud server provisioning (Hetzner extras, DigitalOcean, Vultr, Hostinger) and cloud tokens | P2 | L | New phase; 17.01 inventory. | Open |
| TPL-02 | Template security response: pinned tags, disable/outdated flags, and catalog churn | P2 | M | 11.06 (extend), 17.08. | Open |
| TPL-03 | Template catalog distribution and refresh | P2 | M | 11.06 (extend), 14.x. | Open |
| UX-01 | Global search and command palette | P2 | M | New (Phase 6 follow-up). | Open |
| UX-02 | Tags: management, assignment during creation, bulk tagging, tag pages, tag API | P2 | M | New (Phase 7 follow-up). | Open |
| DATA-21 | Cache flush for Redis-family databases | P3 | S | 11.04 | Open |
| DATA-22 | Database TLS, public TCP proxy and restart-limit semantics (baseline gap; upstream semantics moved) | P3 | L | New: later phase | Open |
| DEP-19 | Readable container names and a container name prefix | P3 | S | DEP-13 follow-up. | Open |
| DEP-20 | GitHub Deployments sync | P3 | M | After DEP-08. | Open |
| DEP-21 | GitHub Actions self-hosted runners on build servers (beta) | P3 | XL | New post-1.0 phase. | Open |
| DEP-22 | Git error diagnostics and helper hygiene in deployments | P3 | S | 12.x/18.13. | Open |
| IAM-16 | Profile and team avatars | P3 | S | New (UI polish). | Open |
| NET-15 | Cloudflare Tunnel for server connectivity (automated setup, updates) | P3 | L | New (Servers/connectivity) | Open |
| NET-16 | Edge traffic analytics (proxy access logs through Sentinel) | P3 | XL | New phase (Monitoring v2) | Open |
| NET-17 | Caddy (and NONE) as alternative proxy providers | P3 | XL | Phase 10 (10.02 follow-up) / post-beta | Open |
| OPS-06 | HTML no-transform header for proxies such as Cloudflare Rocket Loader | P3 | S | Phase 2/16. | Open |
| OPS-07 | Coolify v5 direction (strategic alignment only) | P3 | S | 17.01. | Open |
| SEC-19 | Default process timeout when none is specified | P3 | S | 15.11. | Open |
| SRV-16 | Health polling drift: per-poll audit rows, serial polling, no stagger or timeout | P3 | S. | 12.11, 16.04. | Open |
| SRV-17 | Small host facts: GPU detection, Sysbox install, Docker and Compose versions in the UI | P3 | S. | SRV-02 follow-up. | Open |
| UX-03 | Stars and favourites | P3 | S | New (UI backlog). | Open |
| UX-04 | Canvas views and resource links | P3 | L | New (UI backlog). | Open |
| UX-05 | Project icons and image storage / CDN URL | P3 | S | New (UI backlog). | Open |
| UX-06 | `coolify.json` config-as-code (unreleased upstream) | P3 | XL | 17.02–17.03. | Open |
| UX-07 | New-resource flow (list view, logos, smart scan) | P3 | S | New (UI backlog). | Open |

## 7. Platform and Toolchain (PLT)

### Summary

These items are not Coolify features. They are the platform changes that happened in the same window and that this update has to absorb. .NET 11 went GA on 2026-11-10. The ecosystem packages Vessel depends on shipped matching stable releases. The official C# MCP SDK is stable. Coolify's CI supply-chain hardening (v4.6.0) is directly applicable to Vessel's own GitHub Actions workflow.

### PLT-01: Move from .NET 11 preview 4 to .NET 11 GA
- **Upstream:** n/a (Microsoft release metadata: `11.0.0`, SDK `11.0.100`, 2026-11-10, STS)
- **Vessel today:** Drifted — `global.json` pinned `11.0.100-preview.4.26230.115` with `allowPrerelease: true`; `LangVersion` was `preview`; `Microsoft.*`, EF Core and Npgsql packages were on `11.0.0-preview.4`. The EF snapshot carried the preview-only `LatestMigrationId` override.
- **Gap type:** Platform
- **Priority:** P0 — AGENTS.md and PROJECT_REQUIREMENTS.md require stable .NET 11 for production code.
- **Recommended Vessel approach:**
  - Pin SDK `11.0.100` with `allowPrerelease: false`.
  - Set `LangVersion` to `latest`.
  - Move every preview package to `11.0.0`.
  - Remove the generated `LatestMigrationId` override.
  - Update `docs/development/dotnet-sdk.md`.
  - CI uses `global.json` through `setup-dotnet`.
- **Roadmap fit:** Phase 1 (1.x toolchain), PROJECT_REQUIREMENTS §2
- **Effort:** S

### PLT-02: Vulnerable and outdated dependencies
- **Upstream:** n/a (`dotnet restore` NU1903)
- **Vessel today:**
  - **Vulnerable:** `SSH.NET 2025.1.0` has two high-severity advisories (`GHSA-mggc-4xg6-vcxf`, `GHSA-q939-rpr3-3284`).
  - **Outdated:** Hangfire 1.8.23 (1.8.25 available), OpenTelemetry 1.15 (1.21), Serilog.AspNetCore 10 (11), AWSSDK.S3 4.0 (4.1), StackExchange.Redis 2.13 (3.4, a major version).
- **Gap type:** Security hardening
- **Priority:** P0 for SSH.NET; P2 for the rest.
- **Recommended Vessel approach:**
  - Bump SSH.NET to `2026.0.1` and Hangfire to `1.8.25` now.
  - Bump OpenTelemetry, Serilog and AWSSDK.S3 in one batch with a full test run.
  - Evaluate StackExchange.Redis 3.x separately (a major version; review the lock/RedLock and pub/sub API changes first).
  - Add `dotnet list package --vulnerable` to CI (see PLT-05).
- **Roadmap fit:** 15.14, 15.15
- **Effort:** S

### PLT-03: YAML handling through a real parser and emitter
- **Upstream:** Coolify parses and validates Compose and proxy YAML before it saves or deploys them (v4.4.0 #11923; Compose injection validation; v4.5.0 scale/replicas rejection).
- **Vessel today:** Missing — Traefik and Compose YAML were built with `StringBuilder`, and user Compose files were passed through unparsed with a `..` substring check.
- **Gap type:** Platform / Security hardening
- **Priority:** P1 — a prerequisite for NET-01, DEP-11 and SEC-10.
- **Recommended Vessel approach:**
  - Adopt `YamlDotNet` (18.x, MIT, no transitive dependencies) in Infrastructure only.
  - Application keeps typed models; Infrastructure renders and parses them.
  - Never deserialize untrusted YAML into arbitrary types: use the representation model plus a schema walk.
- **Roadmap fit:** Phase 5 / 15.05
- **Effort:** S (library); usage is counted in the consuming items

### PLT-04: Official C# MCP SDK is available for the MCP server
- **Upstream:** Coolify's MCP server (API-02).
- **Vessel today:** Missing. `ModelContextProtocol.AspNetCore` 2.4.0 is stable on NuGet.
- **Gap type:** Platform
- **Priority:** P2 — enables API-02 without a custom protocol implementation.
- **Recommended Vessel approach:**
  - Host the MCP endpoint in `Vessel.Web` with the SDK's ASP.NET Core transport.
  - Tools call Application commands and queries only, behind the same token abilities and access grants as the REST API.
- **Roadmap fit:** New (API-02)
- **Effort:** S (decision); the feature is tracked in API-02

### PLT-05: CI supply-chain hardening
- **Upstream:** v4.6.0:
  - Every action is pinned to a full commit SHA, enforced by a lockfile.
  - Jobs get least-privilege `permissions`.
  - No shared caches between runs.
  - Release secrets live in protected environments.
  - Release tags and branches are protected.
- **Vessel today:** Partial — `.github/workflows/ci.yml` uses `actions/checkout@v4` and `actions/setup-dotnet@v4` by moving tag. There is no `permissions:` block and no vulnerability check.
- **Gap type:** Security hardening
- **Priority:** P1 — cheap to fix, and it closes a real supply-chain path.
- **Recommended Vessel approach:**
  - Pin actions to commit SHAs.
  - Add `permissions: contents: read`.
  - Add `dotnet list package --vulnerable --include-transitive` as a failing step.
  - Document branch and tag protection in `docs/release`.
- **Roadmap fit:** 15.14, 15.15, 18.06
- **Effort:** S

### PLT-06: Ecosystem image pins for runtime components Vessel deploys
- **Upstream:**
  - Traefik 3.7.x patch tracking.
  - cloudflared 2026.11.1 (eMudhra certificate compromise).
  - Valkey as the Redis default.
  - Pinned database default images (v4.7.0 `update_standalone_database_default_images`).
- **Vessel today:** Drifted — templates and managed databases use `:latest` or floating major tags (`eqalpha/keydb:latest`, `cloudflare/cloudflared:latest`, `postgres:16-alpine`). There is no proxy image pin at all (NET-02).
- **Gap type:** Security hardening
- **Priority:** P1
- **Recommended Vessel approach:**
  - Add a single versioned manifest (`ComponentVersions`) in Application/Shared that lists every image Vessel deploys (proxy, database defaults, helper images).
  - Templates and lifecycle code read from the manifest.
  - A recurring job flags outdated running components (NET-12).
- **Roadmap fit:** 10.x, 11.x, 15.14
- **Effort:** S–M

## 8. SEC — Security Hardening

### Summary

Between v4.1.0 and v4.7.6 Coolify made security hardening its main theme. v4.4.0 alone has 40 security bullets, and v4.5.0, v4.6.0, v4.7.0 and v4.7.2 add more. The work falls into these groups:
- **Command and argument injection.** Git refs, env var names, container names, network names, images and paths are validated and quoted.
- **Path containment.** No symlink writes, and no deletes outside the resource directory.
- **Webhooks.** Fork previews only when public previews are on, secrets of at least 16 characters, throttling of distinct signature failures, and SSRF-guarded outbound URLs.
- **Tenancy.** Permissions use the team that owns the resource, not the session team.
- **Secrets.** Encryption at rest, re-encryption on key rotation, stronger redaction, and audit events without PII.
- **SSH.** Per-server host key pinning, fixed connection options, Ed25519 keys, key rotation, and Git host key pinning.
- **API and terminal.** Throttling of failing clients, and terminal sessions that end when access is lost.
- **Supply chain.** CI actions pinned to SHAs, protected tags and branches, and CVE-driven runtime and image bumps.

Overall Vessel position: the structured-argument `IProcessRunner` design rules out the shell-injection class that drove most upstream fixes. But reading the code turned up **several Vessel-specific critical issues**:
- A cross-tenant webhook signature bypass.
- Permission checks that mix the active team with any team the user belongs to.
- With open registration, any new user can get a host shell and local Docker access on the control plane.
- A hard-coded fallback encryption key.
- Symlink-following reads from cloned repositories.

SSH, Git host keys and outbound HTTP (SSRF) are not hardened yet. Most SSH gaps are latent because SSH runtime execution is not wired yet, but they should shape the design now.

Counts: **P0: 5 · P1: 8 · P2: 5 · P3: 1** (19 items).

### Gap items

#### SEC-01: Cross-tenant webhook signature bypass (verified-once, trusted-for-all)
- **Upstream:** v4.4.0 · #11989, #11922 · `app/Http/Controllers/Webhook/Concerns/MatchesManualWebhookApplications.php`, `ThrottlesManualWebhookFailures.php`, `app/Http/Controllers/Webhook/{Github,Gitlab,Gitea,Bitbucket}.php`
- **Coolify behavior (now):** Manual webhooks select the applications that match the repository and branch. Each application's own secret is checked (HMAC or token compared in constant time) before that application is deployed. Matching applications are hidden from the response. A valid signature for one application never authorizes another application, so tenants with the same repository stay isolated.
- **Vessel today:** **Drifted (critical bug).** The bypass works like this:
  1. `WebhookReceiptService` builds candidates from **all teams**. It matches any app whose repository URL merely *contains* `parsed.Repository` (`src/Vessel.Application/Webhooks/WebhookReceiptService.cs:117-122`). If **any** candidate's secret verifies, the event is marked `Verified` (`:152`, `:84`).
  2. `WebhookProcessingService.VerifyApplicationSecretAsync` then returns `true` for **every** matched app once the event is `Verified` (`src/Vessel.Application/Webhooks/WebhookProcessingService.cs:269-270`).
  3. Push matching excludes apps with auto-deploy off (`:259`), but receipt verification does not.

  So an attacker registers, creates an app with the victim's repository URL and branch, turns auto-deploy off, and sets a secret they know. They then sign a push payload. The victim's app (another team) is deployed at the attacker-chosen `after` ref, which goes into `git clone --branch`. Pull-request events can open previews the same way. `RepositoryMatches` also uses substring `Contains` (`:364`), so `org/app` matches `org/app-private`.
- **Gap type:** Security hardening
- **Priority:** P0. A cross-tenant deployment trigger reachable by any registered user.
- **Recommended Vessel approach:**
  - Remove the event-level `Verified` short-circuit. Record verification per `(event, application)`, for example in a `webhook_event_matches` table, or compute it in-process.
  - Verify inside the processing loop, against each application's own secret, before any action.
  - Match repositories on an exact normalized `owner/repo` identity: parse `RepositoryUrl` into host + path in a Domain value object, and compare case-insensitively with the host.
  - Never let receipt-time verification select apps of other teams.
  - Unit tests: two teams with the same repository, a wrong secret on the victim, a valid secret on the attacker, and the victim not deployed.
- **Roadmap fit:** Phase 9 (fix), 15.02/15.16.
- **Effort:** S

#### SEC-02: Authorization mixes the active team's role with membership in any team
- **Upstream:** v4.3.9 #11239 · v4.4-rc.1 #11405 · v4.4.0 ("Use the team that owns a resource instead of the currently selected team…", #11913/#11920/#11960/#11966/#11989) · v4.5.0 #12195 · v4.7.0 #12460 · `app/Services/Access/AccessResolver.php`, `app/Models/Scopes/VisibleThroughAccessGrants.php`, the `ownedByCurrentTeam()` scopes
- **Coolify behavior (now):** Every permission check, secret-redaction decision and selectable credential uses the team that **owns the resource**. Switching teams in another tab cannot mix teams. Deployment queries are scoped to the team's applications.
- **Vessel today:** **Drifted (critical).** The services call `HasPermission(user, activeTeamId, perm)` and then `CanAccessApplication/Server/Project/Deployment(user, id)`. The second call succeeds when the user is a member of **any** team owning the resource (`src/Vessel.Application/Authorization/VesselAuthorizationService.cs:17-25, 33-92`). Examples:
  - `RequireApplication`/`RequireServer`/`RequireProject` in `Webhooks/ApplicationWebhookConfigurationService.cs:113-119`, `Resources/ResourceManagementService.cs:416-434`, `Proxy/ProxyConfigurationService.cs:200-206`, `Proxy/DomainRoutingService.cs:99-104`, `ManagedServices/ManagedDatabaseService.cs:526-545`.
  - `Deployments/StartDeploymentService.cs:38-42, 127-131`.

  Every user owns a personal team (`Auth/VesselAuthenticationService.cs:55`), so a read-only Member of team B who keeps their own team active passes owner-level checks on team B's resources. They can rewrite apps, proxy config and webhook secrets. Only `TerminalSessionManager` re-checks `server.TeamId != teamId` (`Terminals/TerminalSessionManager.cs:39-40`). The only cross-team test uses a non-member (`tests/Vessel.UnitTests/Application/Phase10ProxyServiceTests.cs:86-100`).
- **Gap type:** Security hardening
- **Priority:** P0. Privilege escalation inside every multi-member team.
- **Recommended Vessel approach:**
  - Replace the two-step pattern with resource-scoped checks in `VesselAuthorizationService`, for example `Authorize(user, ResourceRef, permission)`. It resolves the owning `TeamId` and evaluates the role **in that team**, or requires `owningTeam == activeTeam` and fails closed.
  - Make the `CanAccess*` helpers private, or have them return the owning team.
  - Add an analyzer or architecture test that forbids `HasPermission` and `CanAccess*` pairs.
  - Regression tests: a user who is admin in team A and member in team B, for each mutating service.
  - Coordinate with the Identity/Access agent's grant model.
- **Roadmap fit:** 15.02, 15.16 (move ahead of Phase 14).
- **Effort:** M

#### SEC-03: Host-local execution available to every tenant (localhost/terminal boundary)
- **Upstream:** v4.4.0 (terminal session tokens #11942, "Restricted web terminal SSH commands to approved hosts, keys, ports, and options") · v4.7.0 ("the localhost server needs an instance admin", "Ended terminal sessions when access to the server is lost") · v4.7.2 · `app/Services/TerminalSessionService.php`, `app/Http/Middleware/EnsureTeamWideAccess.php`, `isInstanceAdmin()` in `bootstrap/helpers/shared.php`
- **Coolify behavior (now):** The localhost server (the Coolify host) belongs to the root team, and using or selecting it needs an instance admin. Terminal targets are resolved on the server to an approved host, key, port and options, through short-lived server-issued tokens. Registration is an instance setting.
- **Vessel today:** **Drifted (critical).** Three facts combine:
  - Registration is open, and every new user becomes Owner of a personal team (`src/Vessel.Application/Auth/VesselAuthenticationService.cs:40-58`).
  - Owners hold every permission (`Authorization/VesselPermissions.cs:44`).
  - `CreateServerAsync` accepts any `ConnectionType`, including `Local` and `DockerSocket`, with only `servers.write` (`Resources/ResourceManagementService.cs:193-205`).

  Two paths follow:
  - **Terminal.** `TerminalSessionManager.BuildCommand` ignores the server. For any non-container session it uses `TerminalSecurityPolicy.Default.DefaultShellCommand` (`/bin/sh`) (`Terminals/TerminalSessionManager.cs:44, 196-202`). `InteractiveTerminalProcessBridge` starts that shell **on the Vessel host** (`src/Vessel.Infrastructure/Processes/InteractiveTerminalProcessBridge.cs:22-44`). Any registered user can create a server, open a terminal and get a control-plane host shell.
  - **Deployment.** The runner sends every non-SSH server to the local Docker/Podman runtime (`Deployments/DeploymentRunner.cs:77-81, 532-537`), so arbitrary user compose (privileged, `docker.sock` or `/` mounts) runs on the control-plane host.
- **Gap type:** Security hardening / Breaking change
- **Priority:** P0. This is unauthenticated-to-host-root via self-registration.
- **Recommended Vessel approach:**
  - Introduce an instance-admin principal and a registration-enabled setting (cross-ref Identity agent). The first user is root, and registration is off by default after that.
  - Model the control-plane host as a single system-owned `Local` server. Creating or using `Local`/`DockerSocket` servers, and selecting them as a deploy target, requires instance admin.
  - The terminal bridge must resolve the target from the persisted `Server` (local requires instance admin; SSH goes through `ISshClient` with pinned options) and never default to a host shell.
  - Document the `docker.sock` root-equivalence in `docs/security/terminal.md`.
  - Tests: a non-admin owner cannot create a `Local` server or open a host terminal.
- **Roadmap fit:** 15.01/15.02, plus Phase 4 follow-up (instance admin).
- **Effort:** M

#### SEC-04: Secret encryption key management: hard-coded fallback key, no rotation or re-encryption, plaintext TOTP secrets
- **Upstream:** v4.5.0 "Plaintext secrets are now encrypted at rest" (975048bad, migration `2026_10_19_083521_encrypt_plaintext_secrets.php`), "`app-key:reencrypt`" (`app/Console/Commands/ReencryptWithAppKey.php`, d404da904)
- **Coolify behavior (now):** All credential-like columns are encrypted at rest (GitHub/GitLab app secrets, server settings tokens, database passwords, invitation links). An existing-data migration encrypts the old plaintext rows. After `APP_KEY` rotation, `app-key:reencrypt` re-encrypts every stored secret with the new key, using previous keys for decryption.
- **Vessel today:** **Partial / Drifted.**
  - `AesSecretVault.GetKey()` silently falls back to `SHA256("vessel-alpha-development-secret-storage-key")` when `Secrets:MasterKey` is empty, in **any environment** (`src/Vessel.Infrastructure/Security/AesSecretVault.cs:126-135`). Options validation explicitly allows an empty key (`src/Vessel.Infrastructure/Extensions/InfrastructureServiceCollectionExtensions.cs:88-94`), and no appsettings file sets one.
  - `KeyVersion` is stored but `Decrypt` ignores it (one key only), so rotation would break every secret. `docs/security/secrets.md:22` defers re-encryption.
  - The TOTP shared secret is stored in plaintext (`src/Vessel.Domain/Users/User.cs:41,127-129`; `Persistence/VesselDbContext.cs:300`, no converter).

  Covered: tokens, password reset tokens and invitation tokens are hashed (`Domain/Teams/TeamInvitation.cs:35`). Webhook, notification, registry and database credentials go through the vault.
- **Gap type:** Security hardening / Schema
- **Priority:** P0. Every default install encrypts with a public key.
- **Recommended Vessel approach:**
  - Fail start-up outside Development when `MasterKey` is missing. The installer (Phase 14) generates the key into a root-only host file or a systemd credential.
  - Add a keyring (`Secrets:Keys:{version}`, `ActiveKeyVersion`) so `Decrypt` selects the key by `SecretValue.KeyVersion`.
  - Add a `vessel secrets reencrypt` CLI/Hangfire job: batch, idempotent, audited, no plaintext logging.
  - Move `TwoFactorSecret` into the vault or an EF value converter backed by the same keyring, with a data migration.
  - Tests: missing key in Production fails; rotate then reencrypt round-trips.
- **Roadmap fit:** 15.08, Phase 14 (installer key generation).
- **Effort:** M

#### SEC-05: Repository file reads follow symlinks; path containment is lexical only
- **Upstream:** v4.4.0 #11911/#11943/#11944/#11993 ("never deletes host paths outside it or writes through symbolic links") · v4.5.1 #12324 ("write regular deployment files when the repository has symlinks") · v4.7.0 (`/data` symlink handling) · `confinePathToBase()` and `validateShellSafePath()` in `bootstrap/helpers/shared.php`, `app/Support/AtomicFilesystem.php`
- **Coolify behavior (now):** Compose build paths, file-storage paths and volume paths are validated and confined to the resource directory. Coolify never writes through, and never deletes along, a symlink inside it. Deployment files are written as regular files even when the cloned repository contains symlinks.
- **Vessel today:** **Partial.**
  - `PathSafetyService.IsWithinRoot` compares `Path.GetFullPath` strings only and never resolves link targets (`src/Vessel.Infrastructure/Files/PathSafetyService.cs:56-63`).
  - `CreateComposePassthroughAsync` reads `docker-compose.yml` (or `DockerfilePath`) from the cloned repository through `ReadTextAsync` (`src/Vessel.Infrastructure/Deployments/LocalDeploymentWorkspaceManager.cs:45-52`; `src/Vessel.Application/Deployments/DeploymentRunner.cs:290-307`).

  A repository can commit `docker-compose.yml -> /etc/vessel/appsettings.Production.json` or `/root/.ssh/id_ed25519`. Vessel then reads the host file and writes it into `snapshots/docker-compose.redacted.yml` and compose error output. `WriteTextAsync` would likewise write through a symlinked parent. Path validation is a crude `Contains("..")` (`DeploymentRunner.cs:302, 549-555`), which also wrongly rejects legitimate compose files that contain `..` in any value.
- **Gap type:** Security hardening
- **Priority:** P0. Any repository pusher can read host files.
- **Recommended Vessel approach:**
  - Harden `IPathSafetyService`: resolve each existing segment with `FileSystemInfo.LinkTarget`/`ResolveLinkTarget(true)`, add `EnsureNoSymlinks(root, path)`, and open files with `FileOptions` plus a post-open check of the handle path (or `O_NOFOLLOW` through a small Infrastructure helper).
  - Reject repository-sourced compose and Dockerfile paths that are symlinks or resolve outside the repository.
  - Write generated files atomically (temp file + rename) to non-link paths.
  - Replace the `..` substring check with segment-based validation.
  - Tests: symlink compose file, symlinked parent directory, traversal segments.
- **Roadmap fit:** 15.10, Phase 8 follow-up.
- **Effort:** S

#### SEC-06: Preview deployments from forks run untrusted code without an opt-in
- **Upstream:** v4.4.0 #11922 · v4.7.0 (preview restrictions), v4.7.4 (no production runtime vars for public previews) · `app/Http/Controllers/Webhook/Concerns/ValidatesPreviewDeploymentRepository.php`
- **Coolify behavior (now):** A PR or MR preview deploys only when the source repository equals the target repository. A fork deploys only when public preview deployments are enabled. The target and webhook repository identities must match (constant-time compare). Only same-repository branches deploy their own compose file. Public-access previews do not receive production runtime variables. Opening a preview or changing its PR URL, Git type or Docker tag via the API needs write access, and a deploy token can only redeploy an existing preview.
- **Vessel today:** **Missing.** The PR parsers never read the head or source repository (GitHub `pull_request.head.repo.full_name`, GitLab `object_attributes.source_project_id`, Bitbucket `pullrequest.source.repository.full_name`) (`src/Vessel.Application/Webhooks/WebhookPayloadReader.cs:133-145, 162-174, 190-200`). `ProcessPullRequestAsync` opens a preview whenever `PreviewDeploymentsEnabled` is set (`WebhookProcessingService.cs:150-170`). There is no public or fork flag in `DeploymentSettings`.
- **Gap type:** Security hardening / Behavior change
- **Priority:** P1. Fork PRs could run arbitrary code with team secrets once previews work end to end.
- **Recommended Vessel approach:**
  - Add `SourceRepository` and `TargetRepository` to `ParsedWebhook`.
  - Add a Domain `PreviewAccessPolicy` (`SameRepositoryOnly` default, `Public`) on `DeploymentSettings`, with a migration.
  - Gate in the Application layer. Public previews get a reduced env set (no production runtime secrets) and use the base repository's build definition, not the fork's compose file.
  - Tests for each provider payload.
- **Roadmap fit:** Phase 9 follow-up, 15.05.
- **Effort:** S

#### SEC-07: Manual webhook secret strength, failure throttling, and real client IP
- **Upstream:** v4.4.0 ("Require at least 16 characters…", "throttle repeated distinct signature failures per client address, repository, and branch, without blocking correctly signed deliveries" #11989) · v4.4-rc.1 #11515 (rate limits per real client behind proxies and Cloudflare) · `app/Rules/ManualWebhookSecret.php` (`MIN_LENGTH = 16`), `ThrottlesManualWebhookFailures.php` (30 distinct failures / 60 s; keyed hashes; X-Forwarded-For ignored)
- **Coolify behavior (now):** New or changed manual webhook secrets need at least 16 characters. Failed authentications are counted per provider, client address, repository and branch. Repeats of the same wrong token or payload count once. Correctly signed deliveries still process while the bucket is locked.
- **Vessel today:** **Partial.**
  - The secret only has to be non-empty and at most 512 characters (`src/Vessel.Application/Webhooks/ApplicationWebhookConfigurationService.cs:52-53`).
  - The only protection is a fixed-window limiter of 120 requests per minute per `RemoteIpAddress` over **all** webhook requests (`src/Vessel.Web/Configuration/RateLimitOptions.cs:13-17`; `Extensions/ServiceCollectionExtensions.cs:313, 322-336`). Git hosts' shared egress IPs can therefore throttle valid deliveries, while signature guessing is not counted separately.
  - There is no `ForwardedHeaders` configuration anywhere in `src/` (grep). Behind Traefik, every client shares the proxy's IP bucket for the auth, API and webhook limiters.
- **Gap type:** Security hardening / Behavior change
- **Priority:** P1.
- **Recommended Vessel approach:**
  - Add a Domain `WebhookSecret` value object (16 to 512 characters) and enforce it only on create or change, keeping existing rows working.
  - Add an Application `IWebhookFailureThrottle` (Redis, keyed HMAC of the attempt, scope = provider, IP, repository, branch) checked before secret verification, which never blocks valid signatures.
  - Configure `ForwardedHeadersOptions` with an explicit `KnownProxies`/`KnownNetworks` list (the managed Traefik network). Do not trust arbitrary `X-Forwarded-For`.
- **Roadmap fit:** 15.04, Phase 9 follow-up.
- **Effort:** S

#### SEC-08: Outbound URL / SSRF guard for notifications and SMTP
- **Upstream:** v4.2.0 #10833, #10835 · v4.4.0 #11964/#11967 (Git sources via the guard, NAT64 blocked) · v4.7.0 ("Validated webhook URLs, limited DNS lookups", "SMTP hosts are validated, and Coolify connects only to the checked address") · `app/Rules/SafeWebhookUrl.php`, `SafeExternalUrl.php`, `SafeSmtpHost.php`, `InstanceSettings.webhook_allowed_internal_hosts`
- **Coolify behavior (now):** Outbound webhook and notification URLs are resolved through protected DNS. Loopback, link-local (metadata), reserved and NAT64 targets are blocked, and private ranges are blocked unless the operator lists the host in an internal-endpoint allowlist (Git sources may use private networks on self-hosted installs). The HTTP client is pinned to the validated IPs. Redirects are allowed only to the same origin (at most 5). SMTP connects only to the checked address, and the error explains why a host is refused.
- **Vessel today:** **Missing.**
  - `HttpWebhookNotificationProvider` posts to an arbitrary `secret.Url` with default `HttpClient` settings, which follow redirects (`src/Vessel.Infrastructure/Notifications/HttpWebhookNotificationProvider.cs:33-41`). The same applies to Discord and Slack.
  - `EmailNotificationProvider` connects to an arbitrary `SmtpHost:SmtpPort` (`EmailNotificationProvider.cs:27`).
  - `NotificationTargetService` only normalizes JSON (`src/Vessel.Application/Notifications/NotificationTargetService.cs:51-63`).
  - No IP-range check exists anywhere in `src/` (grep for loopback, private network or `ConnectCallback` finds nothing).
- **Gap type:** Security hardening / New feature
- **Priority:** P1. SSRF to cloud metadata, the Docker TCP API, Redis or Postgres from the control plane.
- **Recommended Vessel approach:**
  - Add `IOutboundEndpointGuard` in Application and an Infrastructure implementation: resolve, classify each IP (`IPNetwork` lists, including `64:ff9b::/96`), apply the allowlist from instance settings, and return the pinned addresses.
  - Use a named `SocketsHttpHandler` with a `ConnectCallback` that connects only to the pinned IPs, `AllowAutoRedirect=false`, and a same-origin redirect handler.
  - Validate when a target is created or updated, and again at send time to catch DNS rebinding.
  - For SMTP, connect a socket to the validated IP (MailKit or a custom stream) with SNI or EHLO set to the original host.
- **Roadmap fit:** Phase 13 follow-up, 15.05.
- **Effort:** M

#### SEC-09: Deployment and command log redaction lacks the secret context; redactor pattern parity
- **Upstream:** v4.4.0 #11974 ("Keep secrets out of deployment logs when a command fails, including multiline and literal values", "Redacted dotted GitHub tokens") · v4.5.0 #12151, #12238 (URL redaction scoped to a line, log exports with secrets hidden) · v4.7.0 (credentials removed from URLs in audit changes, ecf969e35) · `app/Services/Security/SensitiveDataRedactor.php`
- **Coolify behavior (now):** One redactor handles:
  - Known values; short values are replaced only at word boundaries.
  - Locked values, always replaced.
  - Key-based assignments (`KEY=`, `"key":`, YAML) and Authorization headers.
  - `x-access-token:` and `oauth2:` URLs, and URL credentials for many schemes, limited to one line.
  - GitHub tokens (including dotted and `github_pat_`), GitLab `glpat-`, and AWS keys.
  - PEM private key blocks across lines and chunk boundaries, after ANSI and control characters are stripped.

  Exports and downloads are redacted, and admins can configure redaction per resource.
- **Vessel today:** **Partial.**
  - `DeploymentRunner.AppendAsync` calls `redactor.Redact(message)` **without** the resolved env or secret values (`src/Vessel.Application/Deployments/DeploymentRunner.cs:465-470`).
  - `ComposeCommand` carries no redaction profile (`src/Vessel.Application/Docker/ContainerRuntimeModels.cs:37-42`; only build args are passed, `Infrastructure/Docker/DockerCliContainerRuntimeClient.cs:161`). Secrets echoed by `docker compose config/up` or by build output reach the persisted log and SignalR unredacted, unless they match one of four patterns.
  - `SecretRedactor` has four patterns (`src/Vessel.Infrastructure/Security/SecretRedactor.cs:11-17`) and is missing PEM blocks, `github_pat_`/`glpat-`, AWS keys, JSON/YAML keys, ANSI stripping and short-value word boundaries. Multiline secrets split across streamed lines are never matched.
  - Covered: URL-credential redaction is already line-scoped (`[^\s]`), and the env/compose snapshots are redacted with the context (`DeploymentRunner.cs:262, 283`).
- **Gap type:** Security hardening
- **Priority:** P1.
- **Recommended Vessel approach:**
  - Carry a per-deployment `RedactionContext` (the resolved env values plus per-line fragments of multiline secrets) on `ComposeCommand`, `DockerBuildCommand` and `AppendAsync`.
  - Extend `SecretRedactor` with the upstream pattern set, PEM state across lines, and a minimum length for exact-value matching.
  - Golden tests drawn from upstream's redactor test fixtures.
- **Roadmap fit:** 15.07, 15.16.
- **Effort:** S

#### SEC-10: Boundary validation for values passed to git, docker, ssh and generated YAML (git refs, branches, container names, host and user, Dockerfile path)
- **Upstream:** v4.4.0 #11905 (git refs validated before queueing and again at start), #11912/#11914/#11958 (custom internal container names, Compose network names, static images), #11970/#11979/#11983 (quoted paths, owners, modes), #11917 (env names) · v4.7.0 (`git_repository` and `docker_tag` validated like the UI; rollbacks only to listed images) · `validateGitRef()` and `validateShellSafePath()` in `bootstrap/helpers/shared.php`, `escapedUserAtHost()` in `app/Helpers/SshMultiplexingHelper.php:503-512`, `app/Rules/ValidGitBranch.php`
- **Coolify behavior (now):** Git refs must match `^[a-zA-Z0-9][a-zA-Z0-9._\-/]*$` (or `HEAD`) and must not start with `-`. They are checked both when queued and when the job starts. SSH user and IP must not start with `-`. Container names, network names, images and paths are validated and quoted. Env names must match `[A-Za-z_][A-Za-z0-9_.]*`; invalid build-time names fail the build and runtime names warn.
- **Vessel today:** **Partial.**
  - Structured `ProcessCommand` arguments remove shell injection locally.
  - Env keys are stricter than upstream: `^[A-Za-z_][A-Za-z0-9_]*$`, with no `.` (`src/Vessel.Domain/EnvironmentVariables/EnvironmentVariableKey.cs:28`). Note the drift if `.` keys must be imported from Coolify.

  The rest is unvalidated:
  - `GitSource.Branch`/`CommitSha` (`src/Vessel.Domain/Applications/GitSource.cs:5-13`). `NormalizeCommit` only checks length ≤ 80 (`Deployments/StartDeploymentService.cs:113-119`). Webhook commits are only `.Trim()`ed (`Webhooks/WebhookProcessingService.cs:228-230`) and reach `git clone --branch` and `git checkout --force <ref>` (`src/Vessel.Infrastructure/Git/GitProcessClient.cs:15-16, 38-45`); a ref beginning with `-` is option injection for `checkout` and `show`.
  - `ServerAddress` host and user are not validated (`src/Vessel.Domain/ValueObjects/ServerAddress.cs:9-14`). `SshProcessClient` passes `{user}@{host}` without `--`, so a user of `-oProxyCommand=…` becomes local RCE once SSH is wired (`src/Vessel.Infrastructure/Ssh/SshProcessClient.cs:53-58`).
  - Database `ContainerName` gets only a length check (`Domain/Databases/DatabaseResource.cs:95`) and is used as a `docker exec` argument (`Infrastructure/ManagedServices/ProcessDatabaseBackupProvider.cs:63-70`).
  - The terminal builds `docker exec -it {containerName} sh` from request input (`Terminals/TerminalSessionManager.cs:199`).
  - `BuildConfiguration.DockerfilePath` is unvalidated and interpolated unquoted into generated YAML (`dockerfile: {…}`, `Deployments/DeploymentRunner.cs:326`), so newlines inject compose keys.
- **Gap type:** Security hardening
- **Priority:** P1 (latent today; becomes P0 when the SSH runtime lands).
- **Recommended Vessel approach:**
  - Add Domain value objects: `GitRef` (upstream allowlist, no leading `-`, no `..`, no `@{`, length ≤ 255), `ContainerName` (`^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$`), `SshUser`/`HostName` (RFC 1123 or IP, no leading `-`), and `RelativeRepoPath`.
  - Validate at queue time and again in `DeploymentRunner` at start.
  - Always insert `--` before positional arguments in git and ssh calls.
  - Generate compose with a YAML serializer (YamlDotNet) instead of string interpolation.
  - Validate the terminal container against the team's own containers.
- **Roadmap fit:** 15.05, Phase 8/9 follow-up.
- **Effort:** M

#### SEC-11: Git-based Docker Compose validation and host-path policy
- **Upstream:** v4.4.0 ("Validate Git-based Docker Compose applications against Compose injection when they are loaded, saved, and deployed"; "API tokens need the `deploy` permission for mounts outside it") · v4.4.0 Fixed (external volumes, `../` bind sources) · `validateDockerComposeForInjection()` in `bootstrap/helpers/parsers.php:70-155`, `app/Http/Controllers/Api/Concerns/RequiresDeployForOutsideHostPaths.php`
- **Coolify behavior (now):** Repository compose files are parsed (BOM-safe, alias-limited) and checked on load, save and deploy. The checks cover service names, string and long-form volume sources and targets (no variables in targets), network keys and names, and external volumes. Bind sources outside the resource directory are allowed only for principals with deploy rights.
- **Vessel today:** **Partial.** Compose from the repository is passed through raw. The only check is `raw.Contains("..")` (`src/Vessel.Application/Deployments/DeploymentRunner.cs:300-303`). There is no YAML parse and no policy for host bind mounts, `/var/run/docker.sock`, `privileged`, `network_mode/pid: host`, `cap_add` or `devices`. Combined with SEC-03, this is host takeover.
- **Gap type:** Security hardening
- **Priority:** P1.
- **Recommended Vessel approach:**
  - Add an Application `ComposeSecurityValidator` (YamlDotNet with an alias limit) that runs on save (API/UI) and in `DeploymentRunner` before `config`.
  - Produce a typed `ComposePolicyReport` (violations and warnings).
  - Host bind sources must resolve inside the resource workspace. Otherwise an elevated permission is required (proposed `deployments.hostMounts`, coordinate with Identity), and `docker.sock`, `privileged` and host namespaces need instance admin on shared servers.
  - Log policy decisions in the deployment log and audit.
- **Roadmap fit:** 15.05/15.10, Phase 8 follow-up.
- **Effort:** M

#### SEC-12: SSH connection hardening foundation (host key pinning, fixed options, key lifecycle)
- **Upstream:** v4.7.0 #12460 ("Pinned the SSH host key of a server on the first connection, the team gets a notification when it changes; `SSH_HOST_KEY_CHECKING` warn|enforce|off"; "Enforced fixed SSH connection options on every connection, also for health checks"; "Generated new SSH keys as Ed25519 and validated private keys more strictly"; "Kept stored private keys out of the browser"; "Rotate key"; hourly cleanup of unused key files) · v4.4.0 (same-team private keys only, keys kept out of onboarding) · `app/Helpers/SshMultiplexingHelper.php:555-640`, `app/Notifications/Server/HostKeyChanged.php`, `app/Jobs/RotatePrivateKeyJob.php`, `app/Jobs/CleanupUnusedSshKeys.php`, migrations `2026_10_30_120000_add_ssh_host_key_to_server_settings_table.php`, `2026_11_06_194613_add_ssh_host_key_check_enforced_to_server_settings_table.php`
- **Coolify behavior (now):**
  - **Host keys.** Each server has a pinned host key in its own `known_hosts_<uuid>` file and a `HostKeyAlias`. `warn` (default) connects and reports a change, `enforce` refuses, and `off` skips the check. A team can make its server stricter but never looser than an instance `off`. A changed key is saved as pending and the team is notified.
  - **Fixed options on every connection.** `-F /dev/null -o IdentitiesOnly=yes -o PasswordAuthentication=no -o ClearAllForwardings=yes -o ForwardAgent=no -o RequestTTY=no`, with connect and alive timeouts.
  - **Keys.** Ed25519 generation. Key files are written 0600 and resynced from the database. A key must belong to the same team as the server.
  - **Rotation.** The new key is added to `authorized_keys`, then the old one is removed, with undo.
- **Vessel today:** **Missing (latent).**
  - `ISshClient`/`SshProcessClient` exist but nothing uses them (registered only: `Infrastructure/Extensions/InfrastructureServiceCollectionExtensions.cs:134`).
  - `SshHostIdentityPolicy.TrustOnFirstUse` maps to `StrictHostKeyChecking=accept-new` against the process user's global `known_hosts`. There is no per-server pinning or persistence and no change notification (`src/Vessel.Infrastructure/Ssh/SshProcessClient.cs:69-92`).
  - None of the fixed options are passed (`-F /dev/null`, `IdentitiesOnly`, no forwarding).
  - `Server` has no SSH key, host-key or fingerprint fields (`src/Vessel.Domain/Servers/Server.cs`), and there is no private-key entity, generation or rotation.
  - `SSH.NET` is referenced (`src/Vessel.Infrastructure/Vessel.Infrastructure.csproj:23`) but unused.
- **Gap type:** Security hardening / Schema / New feature
- **Priority:** P1. Design this before SSH runtime execution ships.
- **Recommended Vessel approach:**
  - Domain: `SshPrivateKey` aggregate (team-owned, vault reference, Ed25519 default, fingerprint) and `ServerHostKey` (pinned, pending, state, first seen, changed at).
  - Migration for both, plus `servers.private_key_id` and the host-key mode.
  - Infrastructure: write per-connection `known_hosts` and key files under a validated runtime directory with 0600, always add the fixed options and `--`, and map the `Vessel:Ssh:HostKeyChecking` option (`warn|enforce|off`) to `StrictHostKeyChecking` with the per-server override.
  - Raise a `ServerHostKeyChanged` domain event and send the notification through Phase 13.
  - Add a Hangfire `RotateSshKeyJob` and cleanup.
  - Never return key material in DTOs; a public key or fingerprint is enough.
- **Roadmap fit:** New items under Phase 15 (and the SSH runtime phase), 15.12.
- **Effort:** L

#### SEC-13: API abuse controls: failed-request throttle and rate-limit coverage
- **Upstream:** v4.7.0 (13298591d: "Limited API clients that keep failing to 30 failed requests per minute for one token or IP… `429`, `API_FAILED_REQUEST_RATE_LIMIT`… one notification a day") · v4.4.0 (logins rate-limited per normalized email) · v4.5.1 #12301 (Cloudflare client IP for allow-lists and audit) · `app/Http/Middleware/ThrottleFailedApiRequests.php`, `app/Notifications/ApiTokenFailingNotification.php`
- **Coolify behavior (now):** Each token, or each IP when no valid token is sent, may fail (401/403/404/422, and so on) up to N times per minute (default 30, configurable). Beyond that the client gets `429`. The token's team gets at most one "token failing" notification per day.
- **Vessel today:** **Partial.**
  - The `api` limiter (600 requests per minute per IP) is applied only to `DeploymentsController` (grep `EnableRateLimiting`: 2 uses). Every other `/api/v1/*` controller is unlimited.
  - `VesselBearerAuthenticationHandler` returns `Fail` on a bad token without counting it (`src/Vessel.Web/Security/VesselBearerAuthenticationHandler.cs:28-32`).
  - The IP comes from `RemoteIpAddress` without forwarded-header support (see SEC-07).
  - There is no per-email login throttle beyond account lockout. That belongs to the Identity agent, which may also map tokens to scopes.
- **Gap type:** Security hardening
- **Priority:** P1.
- **Recommended Vessel approach:**
  - Apply a global `api` rate-limit policy to the `/api` route group.
  - Add a failed-request middleware after authentication and authorization. It counts responses with status ≥ 400 (except 429) per token id, or per IP when there is no token, in Redis, using the `Vessel:RateLimits:ApiFailedRequests` option (default 30 per minute), and returns `429` with `Retry-After`.
  - Raise `ApiTokenFailing` through Phase 13 with a 24-hour notification throttle.
- **Roadmap fit:** 15.04, Phase 13.
- **Effort:** S

#### SEC-14: Git host key pinning for clones and fetches
- **Upstream:** v4.7.0 #12460 ("Pinned the SSH host keys of Git hosts across deployments; Coolify shows the published host keys of GitHub, GitLab and Bitbucket") · migration `2026_10_30_120000_add_git_ssh_known_hosts_to_applications_table.php` · `gitProviderHostKeys()`/`fetchGitProviderHostKeys()` in `bootstrap/helpers/applications.php:743-930`
- **Coolify behavior (now):** SSH clones to GitHub, GitLab or Bitbucket accept only the published host keys. Those keys are built in, and "Fetch current host key" refreshes them over HTTPS. Other hosts store per-application `known_hosts` lines, pinned on first use and kept across deployments, and each deployment writes a per-application known-hosts file.
- **Vessel today:** **Missing.** `GitProcessClient` runs `git clone` with no `GIT_SSH_COMMAND` or `core.sshCommand` and no known-hosts handling (`src/Vessel.Infrastructure/Git/GitProcessClient.cs:9-27`). SSH URLs (`git@…`) are accepted (`src/Vessel.Domain/ValueObjects/RepositoryUrl.cs:14-15`), so security depends on the host user's default ssh config.
- **Gap type:** Security hardening / Schema
- **Priority:** P2.
- **Recommended Vessel approach:**
  - Add `GitHostKeyCatalog` in Infrastructure (embedded published keys, with refresh over the guarded HTTPS client from SEC-08) and an `applications.git_known_hosts` column.
  - Set `GIT_SSH_COMMAND` to `ssh -F /dev/null -o UserKnownHostsFile=<per-deploy file> -o StrictHostKeyChecking=yes -o IdentitiesOnly=yes -i <deploy key>` through `ProcessCommand.Environment`.
  - Pin on first use with an audit entry, and show the fingerprint in the UI.
- **Roadmap fit:** Phase 9 follow-up / Phase 15.
- **Effort:** M

#### SEC-15: Terminal sessions must end when access is lost
- **Upstream:** v4.7.0 ("Ended terminal sessions when access to the server is lost") · v4.4.0 #11942 (server-issued short-lived terminal tokens) · `app/Services/TerminalSessionService.php`
- **Coolify behavior (now):** Terminal access is authorized through server-issued tokens. When membership, role or a grant is removed, or the server leaves the team, open sessions are closed. The terminal server rejects stale tokens.
- **Vessel today:** **Partial.** The design is already server-side: there are no browser-supplied commands, and input re-checks permission and ownership (`src/Vessel.Application/Terminals/TerminalSessionManager.cs:182-194`; `src/Vessel.Web/Hubs/TerminalHub.cs:25-33`). But a revoked user's process keeps running, and its output keeps streaming to the SignalR group, until idle (15 min) or max lifetime (2 h) (`Terminals/TerminalModels.cs:51-55`). No membership-change handler closes sessions.
- **Gap type:** Security hardening
- **Priority:** P2.
- **Recommended Vessel approach:**
  - Handle the `TeamMemberRemoved`/`RoleChanged`/`ServerDeleted` domain events in the Application layer to close the user's sessions (`bridge.CloseAsync`, audit, realtime "closed").
  - Re-authorize periodically, every N seconds, in the output pump.
  - Remove the connections from SignalR groups on revocation.
- **Roadmap fit:** Phase 12 follow-up, 15.02.
- **Effort:** S

#### SEC-16: Audit events: PII and credential hygiene, anonymization on user delete
- **Upstream:** v4.7.0 (3379fa448 "keep personal data and credentials out of audit events"; "The audit events of a deleted user are anonymized"; ecf969e35 credentials removed from URLs; audit events written at commit time) · `app/Models/AuditEvent.php`, `app/Traits/Auditable.php`
- **Coolify behavior (now):** Audit payloads go through the redactor. Only the actor's email is kept, and credentials in URLs, values and changes are removed. Deleting a user anonymizes their audit rows. Events are written in the same transaction as the change.
- **Vessel today:** **Partial.**
  - `EfAuditWriter` redacts only metadata **keys** containing `password`, `token` or `secret` (`src/Vessel.Infrastructure/Auditing/EfAuditWriter.cs:36-48`). Values such as URLs with credentials, or commit refs carrying tokens, pass through.
  - Auth events store emails, including the **attempted** email on password-reset and failed paths (`src/Vessel.Application/Auth/VesselAuthenticationService.cs:67,130,155-162,300`; `VesselTeamService.cs:71`). A password mistakenly typed into the email field is persisted.
  - There is no user deletion or anonymization flow.
- **Gap type:** Security hardening / Schema
- **Priority:** P2.
- **Recommended Vessel approach:**
  - Pass audit metadata values through `ISecretRedactor` (with the upstream patterns from SEC-09).
  - Store a keyed hash (HMAC) of unverified emails instead of the raw value.
  - Add `AnonymizeUserAuditTrail(userId)` to the user-deletion use case, which replaces actor ids with a tombstone and scrubs emails.
  - Document the audit retention policy.
- **Roadmap fit:** 15.09.
- **Effort:** S

#### SEC-17: Supply chain and runtime pinning: CI hardening, lockfiles, pinned images, CVE response
- **Upstream:** v4.6.0 (every action pinned to a full SHA plus `.github/workflows/actions.lock` with owner and repository IDs, no CI caches, least-privilege `permissions`, `release`/`cleanup` environments, tag protection, branch protection) · v4.3.0 #11029 (Traefik security patch versions) and #11145 (cloudflared) · v4.4.7/v4.3.24/v4.2.1/v4.1.3/v4.0.1 and v4.7.6 (CVE releases for PHP, Node, nginx, cloudflared 2026.10.3 → 2026.11.1, Sentinel 1.1.2) · template commits 3f9af193d and e9c24ae7d ("pin moving tags")
- **Coolify behavior (now):** CI pins every action to a commit SHA, checked against a lockfile. Release secrets are isolated in protected environments. Tags and release branches are immutable. The proxy, cloudflared and service-template images are pinned to patched versions and bumped quickly when a CVE lands, with backport releases for older lines.
- **Vessel today:** **Missing.**
  - `.github/workflows/ci.yml:25,28` uses `actions/checkout@v4` and `actions/setup-dotnet@v4` (tags, not SHAs), has no `permissions:` block, and installs the **preview** .NET SDK (`dotnet-quality: preview`). That contradicts AGENTS.md "no .NET 11 preview bits".
  - There is no NuGet lockfile (`RestorePackagesWithLockFile` is absent from `Directory.Build.props`/`Directory.Packages.props`).
  - `ServiceTemplateCatalog` has **52 `:latest` images**, including `cloudflare/cloudflared:latest` (`src/Vessel.Application/ManagedServices/ServiceTemplateCatalog.cs:1097`).
  - No Traefik image or version policy is defined in Vessel.
  - There is no documented CVE or backport process.
- **Gap type:** Security hardening
- **Priority:** P2 (P1 before the first public release).
- **Recommended Vessel approach:**
  - Pin actions by SHA (Dependabot or Renovate for updates), add `permissions: contents: read`, and use the stable SDK from `global.json`.
  - Enable NuGet lock files with `--locked-mode` in CI, and enable `NuGetAudit` with `NuGetAuditMode=all` as an error in CI.
  - Pin template images to version tags (optionally digests) behind a catalog test that rejects `:latest`.
  - Pin the Traefik and cloudflared versions Vessel deploys in one versioned manifest.
  - Define .NET runtime patch adoption (monthly Patch Tuesday SLA), tag protection and a security-release or backport policy in `SECURITY.md`.
- **Roadmap fit:** 15.14, 15.15, 18.04–18.06, Phase 14.
- **Effort:** M

#### SEC-18: Host-header-independent URLs for links and callbacks
- **Upstream:** v4.4.0 ("Build email verification links and the default OAuth callback URL from the instance URL instead of the request's `Host` header"; removed the no-op `TrustHosts` middleware) · v4.7.0 ("Built team invitation links from the instance URL instead of the request host")
- **Coolify behavior (now):** Every absolute URL that leaves the system (verification, invitation, password reset, OAuth callback, notification resource links) comes from the configured instance URL, never from `Host`.
- **Vessel today:** **Partial (latent).** No code builds links from `Request.Host` yet (grep finds none). But `AllowedHosts` is `"*"` (`src/Vessel.Web/appsettings.json:105`), and there is no instance public-URL setting for the link builders still to come (invites, resets, `NotificationEvent.ResourceUrl`).
- **Gap type:** Security hardening
- **Priority:** P2.
- **Recommended Vessel approach:**
  - Add a `Vessel:PublicUrl` option, required in Production and validated on start.
  - Add an `IPublicUrlBuilder` in Application that every link generator uses.
  - Set `AllowedHosts` from the configured domains in the installer.
  - Cross-ref the Identity agent for OAuth callbacks.
- **Roadmap fit:** Phase 14 (installer config), 15.05.
- **Effort:** S

#### SEC-19: Default process timeout when none is specified
- **Upstream:** v4.4.0 ("`SSH_COMMAND_TIMEOUT=0` or an invalid value no longer removes the time limit of remote commands. The default of 3600 seconds is used instead.") · v4.3.3 #11183 (long backups honor configured timeouts)
- **Coolify behavior (now):** Remote commands always have a time limit. Zero or invalid configuration falls back to 3600 s.
- **Vessel today:** **Partial.** `DotNetProcessRunner.CreateTimeoutToken` applies a timeout only when `ProcessCommand.Timeout` is not null (`src/Vessel.Infrastructure/Processes/DotNetProcessRunner.cs:295-299`; `src/Vessel.Application/Processes/ProcessModels.cs:22`). A caller that omits it runs unbounded. Most current callers do set timeouts.
- **Gap type:** Security hardening (availability)
- **Priority:** P3.
- **Recommended Vessel approach:** Add a `Vessel:Processes:DefaultTimeout` option (default 1 h) applied in the runner when `Timeout` is null or ≤ 0. Interactive terminals keep an explicit policy. Add a unit test.
- **Roadmap fit:** 15.11.
- **Effort:** S

### Reviewed and not applicable

- **Shell quoting of container names, paths, owners and modes in backup, start, stop and scheduled-task commands (#11970/#11979/#11983/#11989):** N/A as a direct port. Vessel passes structured argv through `IProcessRunner` (for example `src/Vessel.Infrastructure/ManagedServices/ProcessDatabaseBackupProvider.cs:19-25`) and has no shell strings. The remaining argument-injection angle is SEC-10.
- **`docker run -e` value quoting (#11917):** Covered by design. Vessel uses an env file and compose `env_file`, and `QuoteEnv` escapes `"`, `\` and whitespace (`Deployments/DeploymentRunner.cs:386-402`). Note: `$` interpolation in double-quoted compose `.env` values is not escaped. Fold into SEC-10 tests.
- **Encrypting remote commands stored for queued tasks:** Covered. Hangfire job arguments are IDs only (for example `Webhooks/WebhookProcessingService.cs:239`).
- **Sentinel token kept out of process arguments:** N/A. Vessel has no Sentinel agent. The same principle already holds: DB credentials go through the env (`ProcessDatabaseBackupProvider.cs:21-25`).
- **Traefik dashboard no longer exposed; proxy config validated before save (#11923):** Covered. Vessel generates the dynamic config, validates it, then applies it (`Application/Proxy/ProxyConfigurationService.cs:89-98`), and exposes no dashboard router.
- **Reverb only on the `/app/` path:** N/A (Laravel Reverb). The Vessel SignalR hubs are mapped explicitly (`Web/Extensions/EndpointRouteBuilderExtensions.cs:19-22`) and authenticated.
- **Hiding matching apps in manual webhook responses:** Covered. `InboundWebhooksController` returns only event id, status and message (`Web/Controllers/Webhooks/InboundWebhooksController.cs:56-64`). The real issue is SEC-01.
- **Locked env values hidden in list APIs (`GET /resources`, `GET /projects/{uuid}/{env}`):** Covered. Secret env vars are masked in list flows (`docs/security/secrets.md`). Locked shared-variable semantics belong to the Env/Config agent.
- **URL redaction spanning lines (#12238):** Covered. The pattern excludes whitespace (`Infrastructure/Security/SecretRedactor.cs:13`).
- **Security response headers, PHP-FPM bound to loopback, nginx dotfile 403s (v4.7.6):** Covered or N/A. `SecurityHeadersMiddleware` sets XFO, XCTO, Referrer-Policy, Permissions-Policy and CSP (`Web/Middleware/SecurityHeadersMiddleware.cs:14-22`). There is no PHP or nginx.
- **PHP, Node, nginx, `league/commonmark` and `serversideup/php` CVE bumps:** N/A as components. The equivalent .NET runtime and NuGet patch policy is in SEC-17.
- **Stripe webhook rejection when unconfigured; S3 avatar public URLs; Coolify Cloud server-validation rate limits:** N/A (Cloud and billing).
- **Removed `PUT /user/profile-information`; OAuth 2FA challenge; verified-email-only linking; Google hosted domain; impersonation and admin view; per-email login rate limit:** Identity/Access agent.
- **API token abilities (`read:sensitive`, `deploy` for `instant_deploy`, registry, proxy and log drains, `delete` ability in v4.6.0, scoped grants, token pause and roll, MCP allowed IPs, admin-only API or MCP toggling):** Identity/Access/API agent. Vessel scopes are listed in `docs/security-auth-permissions.md`.
- **Member role read-only since v4.2.0:** Identity agent. Note the drift: Vessel Members can still start and cancel deployments (`Authorization/VesselPermissions.cs:46-54`).
- **Livewire-state leaks of private keys and GitHub App secrets; locking domain validation state against browser changes:** Livewire-specific. Blazor Server keeps state server-side, but the principle (never bind secrets to component state for unauthorized users) applies to SEC-12 UI work.
- **System-wide Git sources and Gitea sources admin-only; GitHub App webhook signature enforcement in local env; source app lists scoped to team:** Vessel has no Git source or App entities yet (Git provider agent). Apply the SEC-02 owning-team rule when they are built.
- **PostgreSQL init-script filename containment (a39370dbb); SFTP host key saved only by admin validation; restore SQL scan bypasses (#12333); server-transfer bundle holes:** These features do not exist in Vessel (DB, backup and migration agents). Reuse `IPathSafetyService` and the SEC-12 host-key model when they are added.
- **Container image vulnerability scanning (v4.7.0 Syft/Grype, deploy gate):** Product feature, not hardening of existing behavior. Deferred to the monitoring/features agent; it maps to 15.15 only for Vessel's own artifacts.

## 9. IAM / API — Identity, Access, API and MCP

### Summary

Between v4.1.0 (`49656aa`) and v4.7.6, Coolify rebuilt its access model. The changes, in order:
1. **Member became read-only** (v4.2.0).
2. Sensitive fields are hidden behind `read:sensitive`.
3. A new **`delete`** ability (v4.6.0), and `deploy` now gates every side effect.
4. **Fine-grained access grants** (team / project / environment / resource × read, read:sensitive, deploy, write, delete, root) for members, invitations and tokens (v4.6.0).
5. Tokens can be paused, rolled and edited.
6. Instance-admin-only operations.

The same period added:
- First-class **OIDC/OAuth identities** with registration policy (v4.4.0).
- **Passkeys** (v4.5.0) and **team-enforced 2FA** (v4.5.1).
- A real **team audit log** with API/MCP listing (v4.4.0).
- **Integration tokens** (v4.4.0/v4.6.0).
- The REST surface grew from 96 to 233 OpenAPI paths.
- The **MCP server** grew from 10 read-only tools to 83 tools, 2 prompts and 2 resources, with a per-team toggle and IP allowlist.

Vessel's Phase 4 matches v4.1.0-era basics: custom identity, TOTP 2FA, hashed scoped tokens, 3 roles, an audit writer. Since then it has drifted:
- Its **Member role can still deploy and cancel**.
- Tokens have **no `delete` scope**.
- There is **no instance-admin concept**, no OIDC/OAuth, no MCP, no OpenAPI document, no audit query surface, and no access grants.

While reviewing Vessel code I also found two authorization holes it ships today: identity endpoints accept any bearer token, and the notification-target API has no permission check.

**Counts:** P0 = 2 · P1 = 9 · P2 = 6 · P3 = 1 (18 items).

### Gap items

#### IAM-01: Identity-management endpoints accept any bearer token, so a token can escalate itself or strip 2FA
- **Upstream:** All versions. API token creation and management happen only in the session UI (`app/Livewire/Security/ApiTokens.php`). Since v4.5.0/v4.6.0, `EnsureTokenBelongsToCurrentTeamMember` (`app/Http/Middleware/EnsureTokenBelongsToCurrentTeamMember.php`) rejects tokens whose abilities exceed the owner's access. `write:sensitive` can only be granted by admins (`ApiTokens.php:284`). Disabling 2FA requires password confirmation (`RequirePassword`).
- **Coolify behavior (now):** No API route can mint tokens, change memberships or disable 2FA (see `routes/api.php`). A token is checked against its creator's current role and grants on every request. If it has more access than the creator, it is rejected with 403, or with JSON-RPC error -32003 on `/mcp`. Abilities other than access levels (`write:sensitive`, `*`) are honored only for owners and admins. Removing or demoting a member revokes their team tokens (`RevokeUserTeamTokens`).
- **Vessel today:** **Drifted (security bug).** The `Smart` policy scheme sends any `Authorization: Bearer` request to the bearer handler (`src/Vessel.Web/Extensions/ServiceCollectionExtensions.cs:149-156`). Endpoints that carry only `[Authorize]` therefore accept tokens of any scope:
  - `POST /api/tokens` (`src/Vessel.Web/Controllers/TokensController.cs:11,28-43`) lets a `read` token mint a `root` token. `VesselTokenService.CreateAsync` only checks membership (`src/Vessel.Application/Auth/VesselTokenService.cs:36`), not the creator's role or the presenting token's scopes. For an owner or admin, a leaked read-only token becomes full access.
  - `DELETE /api/auth/2fa` (`src/Vessel.Web/Controllers/AuthController.cs:178-187`) disables TOTP with no password or TOTP re-confirmation, and this works through a bearer token.
  - `POST /api/teams/{id}/switch` (`src/Vessel.Web/Controllers/TeamsController.cs:34-49`) copies the bearer principal's claims, including `Permission` and `TokenId`, into a **cookie session**. The derived session survives token revocation. `PermissionAuthorizationHandler` honors claim-based permissions first (`src/Vessel.Web/Security/PermissionAuthorizationHandler.cs:19`).
  - Token validation never re-checks team membership (`VesselTokenService.cs:110-144`).
- **Gap type:** Security hardening
- **Priority:** P0. Escalation from read to root and 2FA removal via a token.
- **Recommended Vessel approach:**
  - **Web:** add a `SessionOnly` authorization policy (`AuthenticationSchemes = Cookie`) and apply it to `TokensController`, `TeamsController`, `AuthController` 2FA/profile actions and future account/passkey endpoints. Never call `SignInAsync` from a bearer-authenticated principal.
  - **Application:** in `VesselTokenService.CreateAsync`, validate the requested scopes against the creator's role or grants: `root` and `write:sensitive` for owner/admin only, and members limited to what they hold. In `ValidateAsync`, require a current membership in `token.TeamId` and re-evaluate the token against the owner's current access (see IAM-05).
  - Require password, TOTP or passkey re-confirmation to disable 2FA.
  - **Tests:** a bearer token gets 403 on `/api/tokens`, `/api/teams/*` and `/api/auth/2fa`; a member cannot create a `root` token; a token of a removed member fails.
- **Roadmap fit:** 4.06/4.07/4.10 (reopen); 15.02.
- **Effort:** S

#### IAM-02: Notification targets and notification events have no permission check
- **Upstream:** v4.2.0 #8628 (consistent authorization) and v4.4.0 #11913/#11960 (team permissions on notification settings). `app/Policies/NotificationPolicy.php`: `update`, `manage` and `sendTest` require `isAdminOfTeam`.
- **Coolify behavior (now):** Members can view notification settings. Only admins and owners can change channels, credentials or webhook URLs, or send tests. API `PATCH /notifications/{channel}` requires the `write` ability plus an admin role.
- **Vessel today:** **Missing check.**
  - `NotificationsController` has only a class-level `[Authorize]` (`src/Vessel.Web/Controllers/Api/V1/NotificationsController.cs:10,34-41,65-70`).
  - `NotificationTargetService.UpsertTargetAsync` performs no role check (`src/Vessel.Application/Notifications/NotificationTargetService.cs:36-60`) and writes `SecretJson` to the vault.
  - As a result, any member or any `read` token can replace channel credentials or webhook URLs (redirecting alerts), and `POST /api/v1/notifications/events` can raise arbitrary team notification events.
- **Gap type:** Security hardening
- **Priority:** P0. A read-only actor can take over outbound notification channels.
- **Recommended Vessel approach:**
  - Add `notifications.manage`, or reuse `settings.manage`, and require it in the Application service (role layer) and in the Web policy (token layer).
  - Make `RaiseAsync` internal-only: remove it from the public API or require `root`.
  - Do a sweep for every controller action with only `[Authorize]` and every Application service without `Require(...)`, and add a reflection test asserting that mutating actions carry a permission policy.
- **Roadmap fit:** 13.x follow-up; 15.02, 15.16.
- **Effort:** S

#### IAM-03: Member role is read-only upstream; Vessel members can still deploy and cancel, and role hierarchy is not enforced
- **Upstream:**
  - v4.2.0 breaking change (Member read-only) and #8628.
  - v4.3.8/v4.3.15: mutable controls blocked for members.
  - v4.3.10: team deletion limited to owners; ownerless teams get an owner (migration `2026_08_20_070831_promote_first_team_member_when_team_has_no_owner.php`).
  - v4.5.1 #12342: tokens kept on promotion.
  - v4.6.0: demoting a member to read-only clears grants and revokes tokens.
  - Key files: `app/Livewire/Team/Member.php`, `app/Livewire/Team/InviteLink.php:84-89`, `app/Policies/TeamPolicy.php`, `app/Services/Access/AccessResolver.php` (`defaultMemberGrants()` is team-wide `Read`).
- **Coolify behavior (now):**
  - A member without grants has team-wide **Read** only: no create, update, delete, deploy, start, stop or restart.
  - Admins cannot invite owners. Members cannot invite admins or owners.
  - Nobody can change or remove a member whose role is higher than their own.
  - Team delete and the Danger Zone are owner-only.
  - Removing a member detaches them, revokes their team tokens and clears their stored current team, all in one transaction. A demotion revokes tokens; a promotion keeps them.
- **Vessel today:** **Drifted.**
  - `VesselPermissions.ForRole(Member)` grants `DeploymentsStart` and `DeploymentsCancel` (`src/Vessel.Application/Authorization/VesselPermissions.cs:45-52`), and the docs state this as intended (`docs/security-auth-permissions.md`, "Members receive … deployment start/cancel").
  - `VesselTeamService.EnsureCanManageTeam` treats Admin = Owner (`src/Vessel.Application/Auth/VesselTeamService.cs:170-179`). An admin can invite owners (`:41-58`), promote themselves or others to owner, and demote or remove owners (`:119-162`).
  - `RemoveMemberAsync` and `ChangeRoleAsync` do not revoke tokens.
  - The domain's "at least one owner" invariant is covered (`src/Vessel.Domain/Teams/Team.cs:78-100`).
- **Gap type:** Breaking change / Behavior change
- **Priority:** P1. This is a breaking semantic that should shape the permission catalog before more features land.
- **Recommended Vessel approach:**
  - **Application:**
    - Make Member read-only by default: remove deploy and cancel from the role. Deploy access then comes only from grants (IAM-05).
    - Add a `TeamRolePolicy` (pure Domain service or Application helper) with rank checks: inviting, promoting, demoting or removing requires a rank ≥ the target's current and new roles; only owners grant Owner; self-promotion is forbidden.
    - On demotion or removal, revoke the user's tokens for that team in the same `SaveChanges`.
  - **Migration:** none needed for roles. Document the breaking change in the changelog and docs.
  - **Tests:** a role-matrix test across invite, change and remove, plus a token-revocation test.
- **Roadmap fit:** 4.08/4.09/4.16 (reopen).
- **Effort:** S–M

#### IAM-04: Token ability model is behind (no `delete`, wrong hierarchy, no pause/roll/edit, no sensitive-response policy)
- **Upstream:**
  - v4.2.0 #9893: sensitive fields hidden.
  - v4.4.0 #11969/#11981: `instant_deploy` needs `deploy`; only known abilities are accepted.
  - v4.5.0 #12194/#12198/#12151: `deploy` gates scheduled tasks, proxy, log drains, deletion side effects, storage and imports; backup messages need `read:sensitive`.
  - v4.5.2: app commands and DB config need `read:sensitive`.
  - v4.6.0: the `delete` ability; pause, roll and edit; expiry warning 7 days before.
  - v4.3.10 #11396: tokens listed and revoked per active team.
  - Key files: `app/Enums/AccessLevel.php`, `app/Http/Middleware/ApiAbility.php`, `ApiSensitiveData.php`, `app/Models/PersonalAccessToken.php` (`paused_at`, `roll()`), migrations `2026_10_25_100000_add_access_grants_to_team_members_and_tokens.php` (adds `delete` to every existing `write` token) and `2026_10_27_142736_add_paused_at_to_personal_access_tokens_table.php`.
- **Coolify behavior (now):**
  - Abilities: `read`, `read:sensitive`, `deploy`, `write`, `write:sensitive`, `delete`, `root`. Hierarchy: `write` ⊇ {deploy, read:sensitive, read}; `delete` ⊇ {read}; `deploy` ⊇ {read}.
  - Every API and MCP `DELETE` needs `delete`. Start, stop, restart, deploy, cancel and rollback need `deploy`. Server side effects need `write` and `deploy`.
  - `read:sensitive` returns secrets only when the token owner is also admin or owner (`ApiSensitiveData`). Container, app, DB and service logs, saved proxy config and validation logs, DB URLs, commands and configuration all require `read:sensitive`.
  - Tokens can be edited (description, permissions, access, expiry) while the secret stays the same. They can be paused (`paused_at`; a paused token cannot authenticate to REST or MCP) and rolled (new secret, same metadata).
  - Lists show the active team's tokens only. An expiry warning is sent 7 days ahead, or 1 day ahead for tokens that live 7 days or less.
- **Vessel today:** **Partial / Drifted.**
  - Scopes are `root|read|read:sensitive|write|write:sensitive|deploy` with no `delete` (`src/Vessel.Application/Authorization/TokenScopeMapper.cs:5-20`).
  - `write` maps to `TeamsManage` and `SettingsManage` but not to read (`:38-47`). `deploy` maps only to `DeploymentsStart`, not cancel (`:50`).
  - Deletes use `*.write` (e.g. `ProjectsController.cs:53-54`). Database lifecycle start and stop needs `ProjectsWrite` rather than deploy (`DatabasesController.cs:39-40`).
  - No `PausedAt`, roll or edit on `PersonalAccessToken` (`src/Vessel.Domain/Users/PersonalAccessToken.cs`).
  - `List(userId)` returns tokens across all teams (`VesselTokenService.cs:67-81`).
  - No expiry-warning job. Unknown scopes are rejected (covered, `TokenScopeMapper.cs:70-71`).
- **Gap type:** API contract / Schema / Behavior change
- **Priority:** P1. This is the authorization contract for every future endpoint and the MCP server.
- **Recommended Vessel approach:**
  - **Application:**
    - Replace `TokenScopeMapper` with an `AccessLevel` enum (Read, ReadSensitive, Deploy, Write, Delete, Root) that carries an `Includes()` hierarchy.
    - Map permissions as `*.delete` for destructive commands and `deployments.*` plus `resources.lifecycle` for deploy-level actions.
    - Add a sensitive-response policy: an `ISensitiveFieldPolicy` consulted by query DTO builders, requiring ReadSensitive and an admin role (or a grant).
  - **Domain:** `PersonalAccessToken.Pause()`, `Resume()`, `Roll(newHash)` and `Update(name, scopes, expiresAt)`.
  - **Infrastructure migration:** add a `paused_at` column, and add `delete` to existing tokens that hold `write`, matching the upstream data migration.
  - **Jobs:** a Hangfire recurring job for expiry warnings through the notifications module.
  - **Web:** `PATCH /api/tokens/{id}`, `POST /api/tokens/{id}/pause|resume|roll` (session-only, see IAM-01).
  - **Tests:** a hierarchy matrix; a paused token fails authentication.
- **Roadmap fit:** 4.06/4.07 (reopen).
- **Effort:** M

#### IAM-05: Fine-grained access grants (team, project, environment and resource scopes) for members, invitations and tokens
- **Upstream:**
  - v4.6.0 and v4.7.0 ("Applied the limits of API tokens to more permission checks"; server and credential checks for scoped members).
  - Key files: `app/Services/Access/{Grant,AccessGrants,AccessResolver}.php`, `app/Enums/{AccessLevel,AccessScope}.php`, `app/Models/Scopes/VisibleThroughAccessGrants.php`, `app/Http/Middleware/EnsureTeamWideAccess.php`, `ApiAbility::SCOPED_ACCESS_ROUTES`, `CoolifyServer::TEAM_LEVEL_TOOLS`.
  - Migrations: `team_user.access` (json), `personal_access_tokens.scopes` (json), `team_invitations.access` (json).
- **Coolify behavior (now):**
  - A `Grant` is `{scope: team|project|environment|resource, uuid, levels[]}`. A grant on a parent applies to its children, and levels add up.
  - Owners and admins are unrestricted. A member with `access = null` gets team-wide Read.
  - Tokens are a second layer: an action must pass both the member layer and the token layer. A token never gets more than its owner and stops working when the owner loses access.
  - Scoped actors see only the projects, environments, resources, deployments and search results they can reach.
  - Scoped actors are blocked from team-wide pages and API routes, i.e. every `/api/v1/*` segment outside `applications|services|databases|projects|deploy|deployments|resources|version|vulnerabilities`. They also lose the team-level MCP tools.
  - Since v4.7.0:
    - New, cloned or migrated resources need a server the member can use, and the localhost server needs an instance admin.
    - Server lists are filtered.
    - Team private keys, Git apps and new S3 storages need team-wide write.
    - Server data is limited in app, service and DB responses.
    - Terminal sessions end when access is lost.
  - Presets: viewer, developer, operator, full access. Invitations carry grants.
- **Vessel today:** **Missing.**
  - Authorization is role × team only (`src/Vessel.Application/Authorization/VesselAuthorizationService.cs:17-93`).
  - The token layer is enforced only at Web attributes, and the role layer only inside Application `Require(...)` (e.g. `src/Vessel.Application/Resources/ResourceManagementService.cs:416-426`). Application services cannot see the token at all.
  - `TeamInvitation` and `TeamMembership` have no access payload.
- **Gap type:** New feature / Schema
- **Priority:** P1. Building more endpoints, MCP and UI on the current split role/token checks will be expensive to retrofit. Introduce the access context now, even if the grant editor UI comes later.
- **Recommended Vessel approach:**
  - **Domain:** value objects `AccessScope` and `AccessLevel`, plus `AccessGrant(scope, targetId, levels)`. Store grants on `TeamMembership`, `TeamInvitation` and `PersonalAccessToken` as owned JSON collections (jsonb) keyed by strongly typed IDs.
  - **Application:**
    - An `IAccessContext` / `ICurrentActor` (user, team, optional token grants, source = ui|api|mcp|webhook|system) resolved per request. An `AccessEvaluator.Allows(level, ResourceLocation)` that intersects the layers.
    - A `VisibleResources` query filter: an EF global query filter, or explicit `Where` composition in queries.
    - Replace `HasPermission` / `Require(...)` with `Authorize(level, target)`.
  - **Web:**
    - Populate `IAccessContext` from cookie or bearer claims.
    - Add a `TeamWide` endpoint filter for team-level controllers.
  - **Migration:** add the jsonb columns and backfill null to mean defaults.
  - **Tests:** a grant inheritance matrix, the token ≤ owner invariant, and list filtering.
- **Roadmap fit:** New: Phase 4b "Fine-grained access" (before Phase 15).
- **Effort:** XL

#### IAM-06: No instance-admin / root-team concept and no registration policy
- **Upstream:**
  - Baseline: root team id 0 and `is_registration_enabled`.
  - v4.4.0 #11989: impersonation and team admin view need an instance admin; root team admins cannot delete the root user.
  - v4.5.0 #12164: system-wide Git sources are instance-admin only.
  - v4.5.1: first root registration allowed only within 10 minutes of start.
  - v4.6.0: disabling the API or toggling MCP requires an instance admin.
  - v4.7.2: setup window fix.
  - v4.7.3: root-team owners and admins are denied instance-backup download.
  - Key files: `app/Policies/InstanceSettingsPolicy.php`, `routes/api.php:60-68`.
- **Coolify behavior (now):**
  - The root team (id 0) owns instance settings. Instance admin = admin or owner of the root team.
  - Instance-admin operations: API and MCP enable/disable, OAuth/OIDC settings, system-wide Git sources, the localhost server, impersonation, team admin view, and the email/instance settings API (`write:sensitive`).
  - Self-hosted registration can be disabled. The very first account can only be created within a 10-minute window after start (restart to reopen). OAuth providers can disable password registration ("Only OAuth registration is enabled").
- **Vessel today:** **Missing.**
  - No root or instance-admin notion. `RegisterAsync` always creates a user and a personal team with no policy check (`src/Vessel.Application/Auth/VesselAuthenticationService.cs:40-71`; endpoints at `AuthController.cs:34-48,90-106`).
  - `settings.manage` is a per-team permission (`VesselPermissions.cs:19`), while system-scoped settings exist (`src/Vessel.Domain/Settings/SettingScope.cs`).
- **Gap type:** New feature / Security hardening
- **Priority:** P1. An internet-facing control plane needs closed registration and a distinct instance-admin role before beta.
- **Recommended Vessel approach:**
  - **Domain:** `User.IsInstanceAdmin`, or an `InstanceRole` enum on User. Keep it separate from teams instead of reusing a magic team id.
  - **Application:**
    - `RegistrationPolicy` stored in system settings: Open, InviteOnly or Disabled, plus OAuth-only.
    - First-user bootstrap: the first registered user becomes instance admin. Allow it only while no users exist and within a configurable window, or through a CLI `vessel admin create` (Phase 14 CLI), which fits host-native operation.
    - Add an `instance.manage` permission checked by system settings, API/MCP toggles and future system-wide sources.
  - **Migration:** promote the earliest user to instance admin.
- **Roadmap fit:** 4.02 follow-up; 14.05 (CLI).
- **Effort:** M

#### IAM-07: OIDC/OAuth sign-in with provider identities and registration policy (ROADMAP 4.04/4.05)
- **Upstream:**
  - v4.4.0 (OIDC, #10543 unified Authentication page; #11907/#11989: 2FA challenge on OAuth, verified-email-only linking, Google Workspace domain; GitLab API v4).
  - v4.3.9 #8210 / #11360: settings preserved and transactional seeding.
  - v4.7.0: redirect URI fixes.
  - Key files: `app/Auth/Oidc/*` (discovery, JWKS, token validator), `app/Services/Auth/OauthLoginService.php`, `OauthIdentityIssuer.php`, `app/Http/Controllers/OauthController.php`, `app/Console/Commands/{RekeyOauthIdentities,UserAllowSsoLink}.php`.
  - Migrations: `create_oauth_identities_table` (unique provider + issuer + provider_user_id); `add_oidc_fields_to_oauth_settings_table` (custom_label, scopes, allow_registration, require_email_verified=true, use_pkce=true, clock_skew_seconds=60); `add_auto_join_root_team`; `add_oauth_registration_policy_to_instance_settings`; `add_created_before_oauth_identities_to_users`; `rekey_oauth_identity_issuers`.
- **Coolify behavior (now):**
  - Providers: authentik, azure, bitbucket, clerk, discord, github, gitlab, google, infomaniak, zitadel and generic OIDC.
  - One linked identity per account, keyed by (provider, issuer, subject) rather than email. The first OAuth login links the account. Linking an existing account requires a provider-verified email (Entra: `xms_edov`). Legacy password-less accounts are an exception.
  - Per-provider registration policy (allow registration, auto-join new users to the root team). Optionally disable password registration.
  - A 2FA challenge after OAuth when enabled. SSO users can enable 2FA without a password and confirm destructive actions with typed confirmation only.
  - Provider-managed emails cannot be changed.
  - Denied logins show a reason code (`auth.failed.oauth_email_unverified`, `oauth_already_linked`, `registration_disabled`).
  - The default callback URL is built from the instance URL, not the Host header.
  - Admin commands: rekey identities after an issuer change; allow a user to SSO-link.
- **Vessel today:** **Missing.**
  - Only config keys exist (`src/Vessel.Application/Auth/AuthOptions.cs:179-193`).
  - A single `User.ExternalSubject` column (`src/Vessel.Domain/Users/User.cs:25,60-64`) cannot hold issuer or provider. ADR-0008 prescribes linking to `ExternalSubject`.
  - ROADMAP 4.04/4.05 are unchecked.
- **Gap type:** New feature / Schema
- **Priority:** P1. This is core parity for teams, and the identity table shape should be fixed before any OAuth code lands.
- **Recommended Vessel approach:**
  - **Domain:** an `ExternalIdentity` entity (UserId, Provider, Issuer, Subject, Email, LastLoginAt) with a unique (Provider, Issuer, Subject). Drop or deprecate `ExternalSubject`.
  - **Settings:** an `AuthProviderSettings` aggregate (DB-backed; client secret through `ISecretVault`; instance-admin only).
  - **Application:** an `ExternalLoginService` implementing the link and registration rules above, with a 2FA handoff.
  - **Web:** ASP.NET Core `AddOpenIdConnect` (PKCE, discovery, JWKS built in) and `AddOAuth` for GitHub/GitLab. Register schemes dynamically from DB settings with `IAuthenticationSchemeProvider` / `IOptionsMonitorCache`.
  - **Docs:** amend ADR-0008.
  - **CLI:** `vessel auth rekey`.
- **Roadmap fit:** 4.04, 4.05.
- **Effort:** L

#### IAM-08: Audit log falls short of upstream's team audit events (context, transactional writes, retention, query API/UI)
- **Upstream:**
  - v4.4.0 #11462/#11788 (team audit log, `GET /audit-events`).
  - v4.5.1 #12301: Cloudflare client IP in audit.
  - v4.7.0: PII and credentials kept out; deleted users anonymized; writes at commit; MCP actions recorded as `mcp`; duplicate and empty rows fixed.
  - Key files: `app/Models/AuditEvent.php`, `app/Traits/{Auditable,AuditsApplicationSettings}.php`, `app/Logging/RedactSensitiveAuditData.php`, `app/Http/Controllers/Api/AuditEventsController.php`, `app/Mcp/Tools/ListAuditEvents.php`, `app/Livewire/Team/AuditLog.php`.
  - Migrations: `create_audit_events_table`, `add_changes_to_audit_events_table`, `add_level_to_audit_events_table`, `drop_unused_indexes_from_audit_events_table`.
- **Coolify behavior (now):**
  - `audit_events` columns: team_id, event (e.g. `api.auth.ability_denied`), source (ui|api|mcp|webhook|system|scheduler), action, level (info|warning|error), actor_type (user|api_token|mcp|webhook|system|scheduler), actor_id/name/email, actor_token_id/name, resource_type/uuid/name, description, metadata (redacted), changes (before/after), ip_address (trusted client IP), user_agent (200 chars), created_at.
  - Redaction is by key and also by value field: commands, compose, configs, env, DB URLs, logs; URL credentials are stripped.
  - Events are written through `DB::afterCommit`, pruned after 90 days, and anonymized when a user is deleted.
  - Admins and owners only: `GET /api/v1/audit-events` (search, action, source, per_page ≤ 100) and the `list_audit_events` MCP tool.
  - Auth failures and denials are audited at warning level.
- **Vessel today:** **Partial.**
  - `AuditLog` has team, actor user, action, `type:id` target, correlation and metadata only (`src/Vessel.Domain/Auditing/AuditLog.cs:11-60`).
  - `EfAuditWriter` calls its own `SaveChangesAsync` after the business save (`src/Vessel.Infrastructure/Auditing/EfAuditWriter.cs:29-33`), so the business change and its audit row are not atomic. Redaction is key-name-only (`:36-49`).
  - Login, register and failed-login events store the raw email in metadata (`VesselAuthenticationService.cs:67,130,46+254`).
  - No IP, user agent, source, token or level. No query service, API or UI (`AuditLogs` is used only by the DbContext mapping). No retention job.
- **Gap type:** New feature / Schema / Security hardening
- **Priority:** P1. Audit is a security baseline (15.09), and making the writes atomic plus keeping PII out of metadata should happen before more events are added.
- **Recommended Vessel approach:**
  - **Domain:** extend `AuditLog` with Source, Level, ActorType, ActorTokenId/Name, ResourceName, Description, Changes (jsonb), IpAddress and UserAgent.
  - **Application:**
    - `IAuditWriter` takes an `AuditContext` from `IAccessContext` (IAM-05).
    - Stage the entity in the same unit of work: no separate `SaveChanges`, or an EF `SaveChanges` interceptor.
    - Value-aware redaction through the existing redaction profiles.
    - An `AuditQueryService` (admin-only, keyset pagination).
  - **Infrastructure:** migration and indexes on (team_id, created_at, id), (team_id, action, …) and (team_id, source, …); a Hangfire prune job with configurable retention (default 90 days); an anonymize step in account deletion (IAM-13).
  - **Web:** `GET /api/v1/audit-events` and a Team → Audit Log page.
- **Roadmap fit:** 4.13–4.15 (reopen); 15.09; 16.01.
- **Effort:** M–L

#### IAM-09: Sessions and the active team: no revocation, no persisted current team, and login lands in an invited team the user is not a member of
- **Upstream:**
  - Baseline: `DeletesUserSessions` (a password change deletes sessions).
  - v4.3.10: preserve page on team switch.
  - v4.3.19: restore last active team after login, with a team picker.
  - v4.3.22: team switch redirects to the dashboard.
  - v4.4.0: login no longer accepts pending invitations automatically.
  - v4.5.0: `KeepConcurrentTeamSwitch`.
  - v4.6.0: password reset clears the forced reset.
  - v4.7.0: expired sessions pruned.
  - Key files: migration `add_current_team_id_to_users_table`, `app/Traits/DeletesUserSessions.php` (also revokes all tokens), `app/Http/Middleware/KeepConcurrentTeamSwitch.php`.
- **Coolify behavior (now):**
  - `users.current_team_id` is persisted and restored at login. If it is missing, a team picker is shown.
  - A password change or reset invalidates all sessions and revokes all tokens.
  - Invitations are accepted only on the invitation page, with explicit confirmation (v4.3.10 #11413).
  - A removed member's stored team is cleared.
- **Vessel today:** **Drifted / bug.**
  - `ResolveLoginTeam` returns the first team with a pending invitation for the user's email, even though no membership exists (`src/Vessel.Application/Auth/VesselAuthenticationService.cs:257-269`). The cookie's TeamId claim then points at a team where every permission check fails.
  - The active team lives only in the cookie claim (`TeamsController.cs:34-49`).
  - Cookie authentication has no `OnValidatePrincipal` or security stamp (`ServiceCollectionExtensions.cs:158-167`), so password reset, 2FA changes, removal or "log out everywhere" cannot invalidate sessions. `ResetPasswordAsync` does not revoke tokens (`VesselAuthenticationService.cs:165-196`).
- **Gap type:** Behavior change / Security hardening
- **Priority:** P1. There is a visible login bug, and there is no way to kill a compromised session.
- **Recommended Vessel approach:**
  - **Domain:** `User.CurrentTeamId` and `User.SecurityStamp` (rotated on password, 2FA or passkey change and on "sign out everywhere").
  - **Application:**
    - Login picks CurrentTeamId if the user is still a member, otherwise the personal team. Never pick an invitation team.
    - Switching persists `CurrentTeamId`.
    - Password reset rotates the stamp and revokes tokens.
  - **Web:** `CookieAuthenticationEvents.OnValidatePrincipal` compares the stamp claim (cached briefly) and re-checks membership of the TeamId claim.
  - **Migration:** add columns.
- **Roadmap fit:** 4.02/4.08 (reopen).
- **Effort:** M

#### IAM-10: Transactional auth emails are not delivered (password reset, invitations, verification)
- **Upstream:**
  - v4.3.10 #11393: sender name and address applied to password-reset and transactional mail.
  - v4.3.12: verification mail is queued with retries (Cloud).
  - v4.4.0: verification links and invitation links built from the instance URL.
  - v4.6.0: working invitation links for password-less invitees.
  - v4.7.0: invitation links from the instance URL when no domain is set.
- **Coolify behavior (now):**
  - Password-reset and invitation emails are sent through the instance mailer, as queued jobs with retries. Links come from the configured instance URL, never the request `Host`.
  - Invitation magic links require explicit confirmation before they are redeemed.
- **Vessel today:** **Missing.**
  - `RequestPasswordResetAsync` generates a token and drops it, so password reset cannot complete for anyone (`src/Vessel.Application/Auth/VesselAuthenticationService.cs:136-163`).
  - Invitations return the token only in the API response to the inviter (`VesselTeamService.cs:76-77`).
  - `User.EmailVerifiedAt` exists (`User.cs:23`) but nothing sets it.
  - An email provider exists only for notifications (`src/Vessel.Infrastructure/Notifications/EmailNotificationProvider.cs`).
- **Gap type:** New feature
- **Priority:** P1. Password reset as implemented is non-functional.
- **Recommended Vessel approach:**
  - **Application:** an `IAuthMailer` (templates: reset, invitation, verify) reusing the instance SMTP/Resend settings from the notifications module, dispatched through Hangfire with retries. Build links from a validated `VesselHostOptions.PublicUrl`.
  - Keep the POST-to-accept invitation flow (already scanner-safe) and add a GET landing page that shows a confirm button.
  - **Tests:** the reset token is delivered, the link uses PublicUrl, and the token is not logged.
- **Roadmap fit:** 4.02 (reopen); 13.x.
- **Effort:** M

#### API-01: No OpenAPI document and no API compatibility policy; upstream REST surface grew from 96 to 233 paths
- **Upstream:** `openapi.yaml` diff `49656aa..HEAD` (+18k lines). Removed: `/enable` (v4.5.0 #12163), `/teams/current` and `/teams/current/members` (replaced by `/team` and `/team/members`). v4.5.1 #10449: OpenAPI list responses documented. v4.5.1 #12343: `success:false` on denials.
- **Coolify behavior (now):** UUID-addressed REST under `/api/v1`. Abilities are attached per route (`routes/api.php:60-492`). Global middleware: `api.ip.allowed`, `auth:sanctum`, `api.token.team`, `ApiAllowed`, `api.sensitive`, plus failed-request throttling. Every state change is POST, PATCH, PUT or DELETE; legacy GETs return 405 through `post_required`. New path groups, mapped to Vessel:

| Upstream group (new since v4.1.0) | Abilities | Vessel controller(s) | Status |
|---|---|---|---|
| Applications: get/update/delete, start/stop/restart, logs, envs, storages + volume backups, tags, move/migrate/clone, rollback(-images), destinations, previews(+logs), scheduled tasks(+execute), dns, secret-manager, vulnerabilities, `private-gitea-app` | read / write / deploy / delete / read:sensitive | `Api/V1/ApplicationsController.cs` (list, create only), `ApplicationDomainsController.cs`, `ApplicationWebhooksController.cs`, `EnvironmentVariablesController.cs`, `DeploymentsController.cs` | Partial (list/create/domains/webhooks/env/deploy only) |
| Databases: per-type create (+sqlite), get/update/delete, logs, imports, storages + volume backups, tags, move/migrate/clone, backups/executions | same | `Api/V1/DatabasesController.cs` (list, create, lifecycle, backup, schedules, restore) | Partial |
| Services + service applications/databases (lifecycle, logs, imports), dns, tags, move/clone | same | `Api/V1/ServiceTemplatesController.cs` (templates, create) | Missing (beyond create) |
| Servers: get/update/delete, validate, domains/resources, envs, proxy (+configuration, restart), sentinel, docker-cleanup (+run, executions), log-drains, registries (+check), image-scanning, cloudflare-tunnel, transfer/import/migrate, host-key reset, destinations | read / write / deploy / delete / read:sensitive | `ServersController.cs` (list, create, connectivity test, snapshots), `ServerHealthController.cs`, `ProxyConfigurationsController.cs`, `TerminalsController.cs` | Partial |
| Cloud providers (Hetzner extras, Vultr, DigitalOcean, Hostinger, `cloud-tokens`, `cloud-init-scripts`) | read / write | none | Missing (owned by Servers agent) |
| Projects: environments CRUD, shared envs at project/environment/server/team | read / write / delete | `ProjectsController.cs` (CRUD), Blazor `SharedVariables.razor` | Partial |
| Deployments: `/deploy` (uuid or tag, `cancel_previous`), per-application history, cancel | deploy / read | `DeploymentsController.cs` | Partial (cross-ref Deployments agent) |
| Team: `GET/PATCH /team`, `/team/members`, `/teams/{id}` | read / write | `TeamsController.cs` (`/api/teams`, not v1) | Partial |
| Notifications per channel (`/notifications/{email,discord,slack,telegram,pushover,webhook}`) | read / write | `NotificationsController.cs` (generic targets) | Partial (shape differs; see IAM-02) |
| Instance email settings (`/settings/email`) | read / write:sensitive | `SettingsController.cs` (GET only) | Partial |
| Security keys, integration tokens (+sync-zones), DNS zones, S3 storages, tags, destinations, GitHub/GitLab/Gitea apps | read / write / delete | `RegistryCredentialsController.cs` only; Blazor pages `SecurityKeys`, `Storages`, `Tags`, `Destinations`, `Sources` have no REST API | Missing |
| Audit events, vulnerabilities, resources, version | read | none (health endpoints exist at `/live`, `/ready`, `/health`) | Missing |

- **Vessel today:** **Partial.**
  - About 70 actions across 20 controllers (`src/Vessel.Web/Controllers/**`). Routes are GUID-addressed and Vessel-shaped, not Coolify-shaped.
  - No `AddOpenApi` / `MapOpenApi` anywhere in `src`. PROJECT_REQUIREMENTS §6 lists only 7 base route groups.
  - State-changing routes are already POST, PUT or DELETE (Covered).
- **Gap type:** API contract
- **Priority:** P1. Without a published contract and a policy decision, every domain will add incompatible endpoints, and MCP (API-02) has no schema source.
- **Recommended Vessel approach:**
  - **ADR "Public API contract":** keep a Vessel-native `/api/v1` contract (GUID IDs, `ApiErrorResponse`). Optionally add a Coolify-compatible facade later under `/api/coolify/v1` for migration clients (Phase 17), rather than bending the native API.
  - Generate OpenAPI with the built-in `Microsoft.AspNetCore.OpenApi` (`AddOpenApi`/`MapOpenApi`). Verify the API exists in the stable .NET 11 SDK.
  - Attach `x-vessel-access-level` to each operation, sourced from the policy metadata.
  - Add an API test that every mutating action declares an access level (ties to IAM-02).
  - Track the per-group endpoint backlog above in each domain's roadmap phase.
- **Roadmap fit:** 6.07 follow-up; New: Phase 6b "Public API completeness"; 17.x for the facade.
- **Effort:** M (OpenAPI + ADR + tests); endpoint groups are L–XL, spread across domains.

#### API-02: MCP server (83 tools, 2 prompts, 2 resources) with per-team toggle, IP allowlist and `is_mcp` deployments
- **Upstream:**
  - Baseline: 10 read-only tools and an instance toggle.
  - v4.2.0 #10771: per-team toggle.
  - v4.3.0 #11000: diagnostics and deployment tools.
  - v4.3.7 #11330: reliable cancel.
  - v4.5.0 #12163: manage tools; IP allowlist enforced.
  - v4.5.1 #12295: only permitted tools in help.
  - v4.5.3: `read:sensitive` visibility fix.
  - v4.6.0: DNS, integration-token, secret-manager and tunnel tools; `delete` ability.
  - v4.7.0: `is_mcp` on deployments; MCP audit source; MCP deploy queues behind running deployments; vulnerability tools.
  - Key files:
    - `app/Mcp/Servers/CoolifyServer.php` (TOOLS, TEAM_LEVEL_TOOLS, instructions)
    - `app/Mcp/Concerns/{CallsRestApi,ChecksTokenAbilities,ResolvesTeam,ResolvesResource,BuildsResponse}.php`
    - `app/Mcp/Tools/*` (84 files incl. the `RestActionTool` base)
    - `app/Mcp/Prompts/{TroubleshootApplication,ExplainFailedDeploy}.php`
    - `app/Mcp/Resources/{InfrastructureOverviewResource,ApplicationResource}.php`
    - `routes/ai.php`
    - `EnsureMcpEnabled`, `EnsureTeamMcpEnabled`
    - migrations `add_is_mcp_server_enabled_to_teams_table` (default true) and `add_is_mcp_to_application_deployment_queues_table`
- **Coolify behavior (now):**
  - `/mcp` uses Streamable HTTP with middleware `mcp.enabled` (instance, 404 when off), `api.ip.allowed`, `auth:sanctum` (API tokens), `api.token.team` and `mcp.team.enabled` (403).
  - Enable/disable via `POST /api/v1/mcp/{enable,disable}` requires an instance admin.
  - Tools hide themselves through `shouldRegister` unless the token has the needed ability. Team-level tools are hidden for scoped tokens.
  - Write tools proxy internally to the REST routes, so validation and permissions are identical. Blocked paths: enable/disable, mcp, feedback, sentinel, settings, teams. Bodies are limited to 1 MB.
  - Responses never contain keys matching private_key, secret, api_key, password, token, webhook_url or db_url. Env values come only from `get_env_values` (read:sensitive plus admin). Full deploy logs are never returned.
  - Destructive actions need `confirm=true`.
  - Deployments started through MCP are tagged `is_mcp` and shown with an `MCP` source filter. Audit events are recorded with source `mcp`.
  - **Tool catalog by category:**
    - Orientation: `coolify_help`, `get_infrastructure_overview`, `search_resources`, `list_unhealthy_resources`, `get_current_team`, `list_team_members`.
    - Read infrastructure: `list_servers`, `get_server`, `get_server_domains`, `get_server_resources`, `get_server_metrics`, `list_destinations`, `get_destination`.
    - Read resources: `list_projects`, `get_project`, `get_environment`, `list_resources`, `list_applications`, `get_application`, `list_application_previews`, `get_application_preview`, `list_databases`, `get_database`, `list_database_backups`, `list_backup_executions`, `list_services`, `get_service`, `list_service_applications`, `get_service_application`, `list_service_databases`, `get_service_database`, `list_storages`, `list_resource_tags`, `list_tags`, `list_scheduled_tasks`, `list_scheduled_task_executions`, `list_service_templates`, `get_database_import`.
    - Deployments and logs: `list_deployments`, `get_deployment` (optional log summary), `get_logs`, `list_rollback_images`.
    - Env: `list_env_keys`, `list_shared_env_keys` (names only), `get_env_values` (sensitive).
    - Git: `list_github_apps`, `list_github_repositories`, `list_github_branches`.
    - Lifecycle (deploy): `control` (start/stop/restart), `deploy` (uuid or tag, `cancel_previous`), `cancel_deployment`, `rollback_application`.
    - Create and manage (write/delete): `manage_project`, `create_application`, `manage_application`, `create_database`, `manage_database`, `create_service`, `manage_service`, `manage_env`, `manage_shared_env`, `manage_preview`, `manage_scheduled_task`, `manage_backup`, `manage_storage`, `manage_tags`.
    - Infrastructure management: `manage_server`, `manage_proxy`, `manage_cloudflare_tunnel`, `docker_cleanup`, `manage_destination`, `manage_registry`, `manage_private_key`, `manage_s3_storage`, `manage_git_source`, `cloud_provider`, `manage_image_scanning`.
    - Integrations, security and observability: `manage_dns`, `manage_secret_manager`, `manage_integration_tokens`, `manage_notifications`, `list_audit_events`, `get_vulnerabilities`.
  - Prompts: `troubleshoot_application`, `explain_failed_deploy`. Resources: `coolify://overview`, `coolify://application/{uuid}`.
- **Vessel today:** **Missing.** No `mcp` or `ModelContextProtocol` references in `src` or docs. No deployment trigger source `mcp`, no team or instance toggles.
- **Gap type:** New feature
- **Priority:** P2. This is a valuable differentiator, but it depends on IAM-04/05 and API-01 being in place first.
- **Recommended Vessel approach:**
  - **Web:** an ASP.NET Core MCP endpoint built on the official C# SDK (`ModelContextProtocol.AspNetCore`: `AddMcpServer().WithHttpTransport().WithTools<…>()`, `app.MapMcp("/mcp")`). Before adding the dependency, check that a stable release targets net11 (dependency rule).
    - Pipeline: bearer-only `RequireAuthorization`, then an endpoint filter for instance toggle → IP allowlist (IAM-11) → team toggle → token ≤ owner (IAM-01/05).
    - Tool classes are thin, live in `src/Vessel.Web/Mcp/<Category>/`, and call the **same Application queries and commands** as the controllers. They do not loop back over HTTP as upstream does, because Vessel's shared `IAccessContext` (with Source = Mcp) already gives identical authorization.
    - Filter `tools/list` by the evaluated access level, return JSON-RPC errors in an HTTP 200 envelope, require `confirm` for destructive tools, and use a central response redactor shared with API DTOs.
  - **Domain:** `Team.IsMcpEnabled` (default true), `SystemSettings.McpEnabled`, and `Deployment.TriggerSource = Mcp`.
  - **Application:** an `McpQueuePolicy` so MCP deploys queue like API deploys.
  - **Tests:** tool visibility per ability, toggles (404 vs 403), no secret keys in output, audit source.
  - **Order:** ship the read-only and lifecycle categories first.
- **Roadmap fit:** New: Phase 19 "MCP server" (after Phase 6b).
- **Effort:** L

#### IAM-11: API IP allowlist and instance API enable/disable
- **Upstream:**
  - Baseline: `allowed_ips` and `is_api_enabled`.
  - v4.5.0: MCP enforces the allowlist; `ApiIpAllowed` on the API.
  - v4.5.1 #12301: Cloudflare client IP used for allowlists and audit.
  - v4.6.0: only an instance admin can disable the API.
  - Key files: `app/Http/Middleware/{ApiAllowed,ApiIpAllowed}.php`, `checkIPAgainstAllowlist`, `client_ip()`.
- **Coolify behavior (now):** `allowed_ips` is a comma list of IPs and CIDRs; empty or `0.0.0.0` means allow all. A blocked request gets 403 `{success:false}`. The client IP is resolved through trusted proxies, including Cloudflare's `CF-Connecting-IP` when the request comes from Cloudflare ranges. When the API is disabled, REST returns 403; MCP token creation still works.
- **Vessel today:** **Missing.** No allowlist or API toggle. The rate-limiting policies are named `api` etc. (ROADMAP 2.13).
- **Gap type:** Security hardening
- **Priority:** P2. This is a common operator expectation for an exposed control plane.
- **Recommended Vessel approach:**
  - **Application:** an `ApiAccessPolicy` in system settings (instance-admin, IAM-06).
  - **Web:** an endpoint filter or middleware on `/api/v1` and `/mcp` using `System.Net.IPNetwork` (built into .NET 8+), with `ForwardedHeadersOptions` and `KnownNetworks` for trusted proxies.
  - Audit denials at warning level.
  - Cross-ref the Security agent for failed-request throttling (30/min per token or IP, v4.7.0) and per-email login limits.
- **Roadmap fit:** 2.13 / 15.04.
- **Effort:** S

#### IAM-12: Team-required 2FA, 2FA status visibility, and step-up re-authentication
- **Upstream:**
  - v4.3.0 #11192: member 2FA status in the team view.
  - v4.3.19: duplicate challenge submissions handled.
  - v4.5.1: owners and admins can require 2FA for all members (`teams.is_two_factor_required`, `app/Http/Middleware/RequireTeamTwoFactor.php`).
  - `app/Http/Middleware/RequirePassword.php`: password re-confirmation skipped for SSO users.
- **Coolify behavior (now):**
  - When the current team requires 2FA, users without confirmed 2FA get redirected to setup, or 403 for JSON and Livewire requests, except on the allowed setup paths. The flag is read from the DB on every request.
  - The team member list shows 2FA status.
  - Sensitive operations need a recent password confirmation (typed confirmation for OAuth-linked users).
- **Vessel today:** **Missing.**
  - No team flag or member 2FA status.
  - The 2FA login step is stateless: the client resubmits password plus code (`VesselAuthenticationService.cs:106-118`). Wrong TOTP codes are audited but not counted toward lockout (`:113-117`).
  - Destructive DB restore uses a typed `RESTORE` (ROADMAP 11.13). Nothing else re-authenticates.
- **Gap type:** New feature / Security hardening
- **Priority:** P2.
- **Recommended Vessel approach:**
  - **Domain:** `Team.RequireTwoFactor` with an audit trail.
  - **Web:** an authorization requirement `TwoFactorSatisfied` evaluated against the active team, for cookie sessions only.
  - **Application:**
    - A short-lived, signed 2FA challenge token instead of password resubmission.
    - Count TOTP failures toward lockout.
    - Add `IStepUpAuthService` (password, TOTP or passkey within N minutes, stored as an `auth_time` claim) for token creation, 2FA disable, member removal and deletes.
- **Roadmap fit:** 4.03 follow-up.
- **Effort:** M

#### IAM-13: Account and team deletion lifecycle
- **Upstream:**
  - v4.3.10: team deletion owner-only.
  - v4.3.11: teams with only unused keys or shared sources can be deleted.
  - v4.4.0: delete your own account.
  - v4.6.0: revoked or expired invitations no longer delete users.
  - v4.7.0: audit anonymization.
  - Key files: `app/Actions/User/{DeleteUserAccount,DeleteUserTeams,DeleteTeamlessUser,RevokeUserTeamTokens}.php`, `app/Actions/Team/{DeleteTeam,DeleteTeamData}.php`; migration `add_created_by_invitation_to_users_table`.
- **Coolify behavior (now):**
  - A user can delete their own account. Teams where they are the only member are deleted; they are removed from other teams; all their tokens and sessions are revoked; their audit events are anonymized.
  - Deletion is blocked for the root user, while a team they own alone still has servers, projects or Git sources, and while a shared team would be left without an admin or owner.
  - Team deletion is owner-only and lists the blocking resources.
- **Vessel today:** **Missing.** No delete-account or delete-team use case (no matches for DeleteTeam, DeleteUser or DeleteAccount in `src`). Audit FKs use `SetNull` (`VesselDbContext.cs:1119-1146`), which keeps the copied email in metadata.
- **Gap type:** New feature
- **Priority:** P2.
- **Recommended Vessel approach:** In Application, add `DeleteAccountCommand` and `DeleteTeamCommand` with a `DeletionBlockers` query, one transaction, token and session revocation (IAM-09 stamp), and audit anonymization (IAM-08). Gate it behind step-up auth (IAM-12).
- **Roadmap fit:** 4.08 follow-up.
- **Effort:** M

#### IAM-14: Integration tokens (team-scoped third-party credentials for Cloudflare, Doppler, Infisical, Vault)
- **Upstream:**
  - v4.4-rc.1 / v4.4.0 #11359 (Cloudflare integration tokens).
  - v4.6.0: list, update, rotate, delete and sync-zones through API and MCP; create Cloudflare tokens with DNS and tunnel capabilities.
  - v4.7.0: provider dashboard links.
  - Key files: `app/Models/IntegrationToken.php`, `app/Actions/IntegrationToken/{Create,Update}IntegrationToken.php`, `app/Services/IntegrationTokenValidator.php`, `app/Policies/IntegrationTokenPolicy.php` (admin-only), `app/Http/Controllers/Api/IntegrationTokensController.php`, `app/Mcp/Tools/ManageIntegrationTokens.php`; migration `create_integration_tokens_table` (uuid, team_id, provider, name, encrypted token, capabilities json).
- **Coolify behavior (now):**
  - Providers: cloudflare (capabilities dns, tunnel; automatic or proxied DNS metadata), doppler, infisical (base_url) and vault.
  - Tokens are validated against the provider before saving and encrypted at rest. Secret values are never returned.
  - Deletion is blocked while DNS records or secret-manager links reference the token.
  - Only admins and owners can view or manage them. API: `/security/integration-tokens[/{uuid}[/sync-zones]]` with read, write and delete abilities.
- **Vessel today:** **Missing** (no integration-token model; secrets go through `ISecretVault` per feature).
- **Gap type:** New feature / Schema
- **Priority:** P2. This is a prerequisite for the DNS and secret-manager features owned by other agents.
- **Recommended Vessel approach:**
  - **Domain:** an `IntegrationCredential` aggregate (Provider, Capabilities, SecretReferenceId), team-owned.
  - **Application:** create, validate, rotate and delete, with deletion blockers.
  - **Infrastructure:** provider validators over `HttpClient` with an outbound URL guard (cross-ref Security agent).
  - **Web:** admin-only API and UI under Keys & Tokens.
- **Roadmap fit:** New (with the DNS / secret-manager phase).
- **Effort:** M

#### IAM-15: Passkeys (WebAuthn) sign-in
- **Upstream:** v4.5.0 #12154. Key files: `app/Support/PasskeyRelyingParty.php`, `app/Http/Middleware/ConfigurePasskeys.php`, `app/Livewire/Profile/Passkeys.php`, `config/fortify.php` (passkeys feature); migration `create_passkeys_table` (user_id, name, unique credential_id, credential json, last_used_at; `instance_settings.is_passkey_enabled` default true).
- **Coolify behavior (now):**
  - Users add and name passkeys on their profile and sign in with them.
  - The relying-party ID is the instance FQDN host, and only over HTTPS. IP hosts are rejected; `localhost` is allowed for development. Custom ports are added to the allowed origins.
  - An instance setting can turn passkeys off.
- **Vessel today:** **Missing.**
- **Gap type:** New feature / Schema
- **Priority:** P2.
- **Recommended Vessel approach:**
  - **Domain:** a `Passkey` entity (UserId, Name, CredentialId unique, PublicKey/COSE, SignCount, Transports, LastUsedAt).
  - **Infrastructure:** a WebAuthn verifier behind `IPasskeyVerifier`. Evaluate ASP.NET Core's built-in passkey primitives (added to Identity in .NET 10; check whether they can be used without `SignInManager` in .NET 11) against an established library such as fido2-net-lib.
  - **Configuration:** derive the RP ID from `VesselHostOptions.PublicUrl`.
  - Treat passkeys as satisfying 2FA and step-up (IAM-12).
- **Roadmap fit:** 4.03 follow-up.
- **Effort:** M

#### IAM-16: Profile and team avatars
- **Upstream:** v4.3.0 (profile pictures). v4.3.7: private persistent storage. v4.3.13 / v4.3.18: served from S3 or an optional CDN (`image_cdn_url`). v4.5.0 #11865: S3 avatars no longer use unauthenticated public URLs. v4.6.0: cache-busting URLs. Migrations `add_avatar_columns_to_users_table` and `add_avatar_columns_to_teams_table` (avatar_path, storage_type, s3_storage_id).
- **Coolify behavior (now):** User and team avatars are stored locally or on S3 and served through authenticated routes or signed URLs (or a CDN). The URL changes when the image is replaced.
- **Vessel today:** **Missing.**
- **Gap type:** New feature
- **Priority:** P3. Cosmetic.
- **Recommended Vessel approach:** In Application, an `AvatarService` storing through `IObjectStorage` (local or S3; validate type and size; strip metadata). Serve it from an authorized controller with ETags.
- **Roadmap fit:** New (UI polish).
- **Effort:** S

### Reviewed and not applicable

- **Already covered — state-changing endpoints POST-only (v4.2.0):** every Vessel mutation is POST, PUT or DELETE (`grep '[Http*'` over `src/Vessel.Web/Controllers`). No mutating GETs, so no 405 shims are needed.
- **Already covered — "accept only known API token permissions" (v4.4.0 #11969):** `TokenScopeMapper.Serialize` throws on unknown scopes (`TokenScopeMapper.cs:70-71`).
- **Already covered — `promote_first_team_member_when_team_has_no_owner`:** Vessel's domain invariant prevents ownerless teams (`Team.cs:78-100`). No data fix is needed.
- **Already covered — invitation scanner protection (v4.3.10 #11413):** acceptance is an authenticated POST with an email match (`VesselTeamService.cs:80-117`). There is no GET that consumes the token. IAM-10 adds the confirm page.
- **`POST /api/v1/enable` removed (v4.5.0):** Vessel never had an API-enable endpoint. N/A, though IAM-11 adds an instance-admin-only toggle.
- **`delete_from_coolify_only` rejected with 422 (v4.5.1):** Vessel has no service delete API yet. When it is added, do not offer a "remove from Vessel only" mode (cross-ref the Services/Resources agent).
- **`cancel_previous` on `/deploy` and the MCP deploy tool (v4.7.0):** owned by the Deployments agent. IAM-04 only defines that it needs `deploy`.
- **Failed-request throttle (30 per minute per token or IP, v4.7.0), per-email login rate limit (v4.4.0), terminal session tokens, SSH host-key pinning, outbound URL guard, secret encryption and redaction:** owned by the Security agent, cross-referenced from IAM-11 and IAM-14.
- **Cloud provider token API restricted to admins and owners (v4.7.0), and the Hetzner, Vultr, DigitalOcean and Hostinger endpoints:** owned by the Servers/Provisioning agent. Apply the IAM-03/05 role rules when that feature is built.
- **`UseSessionTeamApiToken` (canvas `X-Canvas-Team` synthetic token):** this is how Livewire and the Canvas UI reach the REST API with a session. Vessel's Blazor UI calls Application services directly, so no equivalent is needed.
- **Coolify Cloud items** (subscription access for Cloud admins, checkout reminder emails, Cloud registration verification queue, data retention for unsubscribed teams, Stripe): out of scope for self-hosted Vessel.
- **`HORIZON_ALLOWED_EMAILS` (Horizon dashboard access):** Laravel-specific. Vessel does not expose a Hangfire dashboard (no `UseHangfireDashboard` in `src`).
- **Livewire hardening** (v4.3.15: blocking direct invocation of protected Livewire mutations; reduced remotely callable Livewire API) and **`PreventHtmlTransform`, `TrustHosts` removal:** Laravel/Livewire framework internals. The Vessel equivalent is IAM-02's rule that every Application command checks authorization.
- **Admin impersonation and the team admin view (v4.4.0 #11989):** these depend on IAM-06. Implement them only if a product decision asks for impersonation. If it does, it must require an instance admin and be audited.
- **`oauth:rekey` and `user:allow-sso-link` artisan commands:** folded into IAM-07 as `vessel auth` CLI subcommands rather than separate items.
- **Team broadcast-event trait and Reverb `/app/` routing (v4.6.0):** Laravel realtime internals. Vessel SignalR hubs already scope groups by team.

## 10. DEP — Deployments, Builds, Git and Previews

### Summary

Between v4.1.0 (`49656aa`) and v4.7.6, upstream rewrote most of the deployment pipeline. `app/Jobs/ApplicationDeploymentJob.php` changed by ~4.2k lines (diffstat) across 205 commits, and `bootstrap/helpers/{applications,docker,shared,parsers}.php` by about 8k more lines. The main themes:
(1) **Queue admission is now transactional.** Upstream dedupes per commit, enforces per-server queue and build limits, auto-cancels superseded webhook deployments (v4.7.0 breaking), and marks the deploy phase with `deploy_started_at`.
(2) **Previews are hardened.** Each preview has its own variables and Compose project. Fork PRs are trusted only with public previews, using `is_same_repository` and `keeps_production_values`.
(3) **Rollback is a real feature.** It can use registry images, but only to listed images or successful production commits.
(4) **There are new Git sources:** Gitea/Forgejo App, GitLab OAuth app plus system hooks, HTTPS credentials, pinned SSH known hosts, and admin-only system-wide sources.
(5) **Environment values are passed exactly as entered.** Escaping is opt-in legacy, and preview vs production values and build-time key rules are enforced.
(6) **Compose files are now validated:** scale/replicas > 1 is rejected, along with build contexts outside the repo; external volumes and driver options are kept; previews get per-preview projects.
(7) Other additions: restart limits (opt-in), rolling-update drain, Smart scan, move/clone/migrate APIs, GitHub Deployments sync, and GitHub Actions runners.

**Vessel position.** Vessel's Phase 8/9 pipeline is an MVP. It only targets local runtimes, builds Dockerfile apps, and passes repo Compose files through unchanged. Previews have a model and a trigger but no runtime isolation. Rollback, private Git sources, queueing, rolling updates, restart tracking and move/clone don't exist. Two correctness defects need fixing first:
- A webhook or commit-pinned deployment uses `git clone --branch <sha>`, which fails.
- Preview deployments reuse the production Compose project and environment. Once cloning is fixed, a PR, including one from a fork, would replace the production containers.

**Counts:** P0: 2 · P1: 10 · P2: 6 · P3: 4 (22 items).

#### Upstream `ApplicationDeploymentJob.php` diff 49656aa..HEAD: semantic summary
(205 commits; 7485 lines at HEAD vs 4839; about 95 new methods, none removed.)
- **Lifecycle/queue.** `transitionToStatus()` is the single status writer. A side-effect failure after success (a notification, for example) no longer flips the deployment to failed and doesn't stop `queue_next_deployment()`. `startDeployPhase()` sets `deploy_started_at` before containers change, using a conditional update that races safely with auto-cancel. A deployment that can't be dispatched is marked failed. Cancel stops the helper container on both the build server and the deploy server (no `kill -9` of a PID from inside the Coolify container), and the helper of a failed deployment is removed. Multi-server child deployments carry `parent_deployment_uuid`, and one notification is sent after all servers finish.
- **Build.** A build server with free concurrency slots is reserved when the deployment starts (`selectBuildServer`), and additional servers never build. An existing image is reused on a restart of a Dockerfile app. For Compose, the image build is skipped when the build fingerprint matches (`docker_compose_build_fingerprint`); a changed build-secret value forces a rebuild. Railpack builder bootstrap is serialized and the idle BuildKit builder is stopped. Buildx gets an isolated `DOCKER_CONFIG`/`HOME`, and reserved Docker client env keys are stripped from build args. The host CA bundle is trusted in git/curl. `--add-host` excludes preview, short-lived and build-server hosts, and `include_network_hosts_in_build` can turn it off.
- **Env.** Values are exact (`uses_legacy_escaping`), and `.env` keys follow the order of the variables page. Build-time keys are validated only when building; legacy runtime-only names deploy with a warning and a suggested name. Remote secret references (`{{vault.KEY}}`) are substituted and redacted. Previews: `previewUsesProductionValues()`, `previewGetsProductionRuntimeFallback()` and `warnAboutPreviewVariablesNotPassed()`. Paths and ports are stripped from `COOLIFY_FQDN`. `SERVICE_URL_<NAME>_<PORT>` is added, and `COOLIFY_BRANCH=pull/<n>/head` is set in all preview parts.
- **Git.** Transient GitHub failures are retried, and fetches can no longer hang. `save_git_ssh_known_hosts()` pins host keys. HTTPS credentials are supported, as are short SHAs with shallow clones. LFS is skipped when disabled. The commit message is read from the repo root, and when a deploy doesn't clone, the previous commit message is reused. There is a hint for an unreachable Git host and Bitbucket PR head resolution.
- **Compose.** The file is loaded from Git objects and submodules. A PR's Compose file is used only for same-repository previews. Build contexts must stay inside the repository. Each preview gets its own project name and the original service name as a network alias. Env files are kept. Content files are written before `up`. Images are pulled before stop (locally built images are not), and the generated file is written after the build.
- **Health/rolling.** `wait_for_proxy_to_route_new_container()` adds a drain wait (default 5 s, 0–300) and skips it when the proxy labels changed. A new container that is healthy is kept when a late step fails; a new container that fails is removed. The health check honours the expected code and response text, uses `-I` for HEAD and `-k` for HTTPS. The Dockerfile `HEALTHCHECK` is parsed from the built stage.
- **Security gates (Security agent).** Git refs, env names, container names, network names and paths are validated and quoted. There is an image-scan deploy gate (`imageScan`, `scan_gate_*`).

### Gap items

#### DEP-01: Commit-pinned and webhook deployments fail: shallow clone uses `--branch <sha>`, and PR refs are never fetched
- **Upstream:** v4.5.1 (#9926 short SHAs with shallow clones, #12321 fetch hang, #10360 LFS), v4.4.0 (Gitea preview uses PR head commit, git ref validation #11905), v4.7.0 (#9572 `pull/<n>/head`), v4.5.1 (#10806 Bitbucket PR commit) · `ApplicationDeploymentJob::gitCommandDefinitions`, `gitLsRemoteCommand`, `shouldResolveBranchHeadCommit`, `resolve_bitbucket_pull_request_commit`; `bootstrap/helpers/shared.php` (`validateGitRef`)
- **Coolify behavior (now):**
  - Coolify clones the branch, or does a shallow fetch of the exact commit (short SHAs too) and checks it out.
  - Previews fetch the provider's PR ref (`pull/<n>/head` on GitHub/Gitea, `merge-requests/<n>/head` on GitLab), or the Bitbucket source commit.
  - Fetches run non-interactively with timeouts, so they can't hang. Refs are validated before queueing and again at start.
- **Vessel today:** **Drifted/broken.**
  - `GitProcessClient.CloneAsync` always passes `--branch <BranchOrTag>` (`src/Vessel.Infrastructure/Git/GitProcessClient.cs:14-16`), and `DeploymentRunner` passes the deployment's commit SHA as that value (`src/Vessel.Application/Deployments/DeploymentRunner.cs:85-92`).
  - Webhook pushes and PRs always queue the payload SHA (`src/Vessel.Application/Webhooks/WebhookPayloadReader.cs:129,140`; `WebhookProcessingService.cs:228-233`), and `git clone --branch <40-hex>` fails with "Remote branch … not found". So every webhook-triggered deploy and every "redeploy by commit" (ROADMAP 9.11) fails.
  - PR refs are never fetched, so fork commits are unreachable. `GIT_TERMINAL_PROMPT=0` is not set.
  - No test covers clone arguments (no matches under `tests/` for `CloneAsync`).
- **Gap type:** Behavior change (correctness)
- **Priority:** P0 — the advertised push-to-deploy and redeploy-by-commit flows cannot succeed.
- **Recommended Vessel approach:**
  - Extend `IGitClient` with a `CheckoutAsync(GitCheckoutRequest{ RepositoryUrl, Ref, RefKind: Branch|Tag|Commit|PullRequest(provider,n), Depth, Submodules, Lfs, Credentials })`.
  - In Infrastructure, implement it as `git init` → `git fetch --depth=1 origin <ref-or-sha>` → `git checkout --detach FETCH_HEAD`. Set `GIT_TERMINAL_PROMPT=0`, a non-interactive SSH command and a bounded timeout. Fall back to a full fetch when the server rejects a fetch by SHA.
  - Add a shared `GitRef` value object in Domain that validates refs (cross-ref Security), so a ref cannot start with `-`.
  - Unit-test the generated arguments; add an integration test against a local bare repo.
- **Roadmap fit:** Fixes 8.05/9.10/9.11; prerequisite for DEP-02/DEP-07.
- **Effort:** S–M

#### DEP-02: Preview deployments are not isolated from production and fork PRs are not gated
- **Upstream:** v4.4.0 (#11922 fork PRs only with public previews), v4.5.1 (#12325 previews use their own variables; preview Docker tags keyed by preview ID), v4.5.2 (`keeps_production_values`), v4.7.0 (#9014 own Compose project per preview; #8030 original name as alias), v4.7.4 (production runtime fallback unless public), HEAD (`is_same_repository`: only a same-repo PR deploys its own Compose file) · `ValidatesPreviewDeploymentRepository.php`; `ApplicationDeploymentJob::previewUsesProductionValues`, `previewGetsProductionRuntimeFallback`, `warnAboutPreviewVariablesNotPassed`, `composeProjectName`; migrations `2026_10_28_103619_add_keeps_production_values…`, `2026_11_17_161116_add_is_same_repository…`
- **Coolify behavior (now):**
  - A PR deploys only if the target repository equals the webhook repository. The source repository must also equal the target, unless `is_pr_deployments_public_enabled`.
  - Previews get their preview variables. When public previews are off, production runtime variables without a preview copy also pass through, so a fork never sees production values. The deploy log lists the variables that were not passed.
  - Each preview gets its own containers, image tag, Compose project and domain, and only a same-repository PR's Compose file is trusted.
- **Vessel today:** **Missing (unsafe).**
  - `DeploymentRunner` never reads `Deployment.PreviewId`. The project name `vessel-{appId}` (`DeploymentRunner.cs:263`), image name (`:265`), port mapping (`:334-335`) and environment (`:343-370`, only `IsRuntime` filtered) are identical to production. A preview deployment would therefore replace the production stack and receive production secrets.
  - The webhook parser doesn't read `pull_request.head.repo` or the source project, so there is no same-repository or fork check (`WebhookPayloadReader.cs:136-143`; `WebhookProcessingService.cs:148-168`).
  - `ApplicationPreview.PreviewUrl` is never set.
  - The env-var unique index (`src/Vessel.Infrastructure/Persistence/VesselDbContext.cs:945-955`) leaves out `IsPreview`, so an application can't hold both a production and a preview value for the same key.
  - This is masked today only by DEP-01.
- **Gap type:** Security hardening + Behavior change (cross-ref Security agent for fork gating)
- **Priority:** P0 — fixing DEP-01 without this lets any PR, including a fork PR, overwrite production with production secrets.
- **Recommended Vessel approach:**
  - **Domain.** Add `ApplicationPreview.IsSameRepository`, `KeepsProductionValues` (true for rows that already exist) and `Status` values (Queued/Running/Exited/Closed). Give `DeploymentSettings` a `PublicPreviewsEnabled` flag (default false).
  - **Webhooks.** In `WebhookPayloadReader`, parse the source and target repository identities. In `WebhookProcessingService`, refuse untrusted PRs and record them as skipped with a reason.
  - **Application.** Extract a `DeploymentPlanBuilder` (pure, unit-tested) from `DeploymentRunner`. For previews it derives the project, network alias, image tag (`pr-{n}-{previewId}`), port strategy and preview hostname, and resolves preview variables per the upstream rules.
  - **Persistence.** Replace the env-var unique index with one that includes `IsPreview` and uses `NULLS NOT DISTINCT`. Migration required.
- **Roadmap fit:** Completes 9.12/9.13; cross-ref Phase 15.05.
- **Effort:** M

#### DEP-03: Deployment log streaming redacts only known patterns, not environment values (`redact_all_env_values_in_logs`)
- **Upstream:** v4.4.0 (#11974 secrets out of failed command logs, multiline/literal), v4.5.0 (#12151 strengthened redaction), v4.3.11 (dotted GitHub tokens), migration `2026_09_25_105051_add_redact_all_env_values_in_logs_to_resources` (default **true**), `2026_10_13_101731…standalone_databases`
- **Coolify behavior (now):** By default every environment value is masked in deployment logs and exports. With the option off, only locked or secret values are masked. Failed-command output and multiline or literal values are also covered.
- **Vessel today:** **Partial.**
  - `DeploymentRunner.AppendAsync` calls `redactor.Redact(message)` without a `RedactionContext` (`DeploymentRunner.cs:465-470`), and `SecretRedactor` then applies only named regex patterns (`src/Vessel.Infrastructure/Security/SecretRedactor.cs:19-36`).
  - Env values are only used to redact the snapshot files (`DeploymentRunner.cs:262,283`).
  - Build, compose and exception output (`:150`) can echo raw secret values. The export endpoint serves these stored lines as they are (`src/Vessel.Web/Controllers/Api/V1/DeploymentsController.cs:63-87`).
- **Gap type:** Security hardening (cross-ref Security agent)
- **Priority:** P1 — it becomes P0 as soon as build-time variables are wired (DEP-09), because `docker build` echoes ARG values.
- **Recommended Vessel approach:**
  - Have the runner build a per-deployment `RedactionContext` from all resolved values (secret values always; plain values when `RedactAllEnvValuesInLogs`, default true) and pass it to every log append.
  - Cover multiline values line by line and the base64/URL-encoded variants.
  - Add a `RedactAllEnvValuesInLogs` setting to `DeploymentSettings` (migration).
  - Unit-test with build output that echoes values.
- **Roadmap fit:** 15.07; 8.14.
- **Effort:** S

#### DEP-04: Deployment queue admission: queue instead of reject, dedupe, limits, trigger source, stuck recovery
- **Upstream:** v4.4.0 (#11987 queue limit, no duplicate commit, no double start; undispatchable → failed; server delete → queued failed), v4.4.1, v4.3.7 (#11330 cancellation advances queue), v4.7.0 (#7649 build-server concurrency; #12442 MCP queues behind; `is_mcp`), v4.7.0 startup marks stuck deployments failed; migrations `…parent_deployment_uuid…`, `…is_mcp…`, `2026_11_15_130000_backfill_finished_at…` · `bootstrap/helpers/applications.php` (`queue_application_deployment`, `start_queued_deployment`, `queue_next_deployment`, `next_queuable`, `fail_undispatchable_deployment`)
- **Coolify behavior (now):**
  - Admission runs in one transaction that locks the application and server rows. A full server queue (`deployment_queue_limit`, default 25) is rejected with `queue_full`.
  - An active deployment with the same commit, PR and docker tag returns `skipped` with the existing UUID, unless the request is a force, a rollback or a "no questions asked" deploy.
  - Otherwise a row is inserted with its trigger flags (`is_webhook`/`is_api`/`is_mcp`/`restart_only`/`rollback`) and started only if per-app and per-server or build concurrency allows.
  - Every terminal transition advances the queue. A stale `queued` or `in_progress` row is failed at startup, or when its server is deleted.
- **Vessel today:** **Missing / Drifted.**
  - A second request throws instead of queueing: `StartDeploymentService.cs:52-58` throws "already queued or running", and webhooks do the same (`WebhookProcessingService.cs:218-225`), which marks the webhook event Failed.
  - The two checks disagree: the manual one ignores `PreviewId`, the webhook one filters by it.
  - The runner lock is per application with zero wait (`DeploymentRunner.cs:51-56`). On lock miss it calls `FailAsync` on a `Queued` deployment, but `CanTransition(Queued, Failed)` is false (`src/Vessel.Domain/Deployments/Deployment.cs:172`). The resulting `DomainException` escapes outside the `try`, Hangfire retries, and the `ActiveDeployments` gauge incremented at `:46` is never decremented.
  - There is no trigger-source field (only `ActorUserId`/`WebhookEventId`), no per-server limit, and no `Skipped` status (`DeploymentStatus.cs`).
  - Unreachable servers are rejected from the stored status without a recheck (`StartDeploymentService.cs:49-50`).
- **Gap type:** Behavior change + Schema
- **Priority:** P1 — rapid pushes and a manual deploy during a running deploy are normal workflows; today they fail.
- **Recommended Vessel approach:**
  - **Domain.** Add `DeploymentTrigger` (Manual/Api/Webhook/Rollback/Restart/Mcp-reserved) and the statuses `Skipped` and `Queued → Failed`. Add a `ServerSettings.DeploymentQueueLimit` setting and a build concurrency setting.
  - **Application.** A `DeploymentAdmissionService` runs one serializable transaction, or takes a PostgreSQL advisory lock on (appId) and (serverId), to dedupe, insert, decide start, and enqueue. A `DeploymentQueueAdvancer` is called from every terminal transition.
  - **Jobs.** Add a Hangfire recurring `StaleDeploymentReaper`, and run a check at startup that fails orphaned rows.
  - **Tests.** Concurrency tests against the PostgreSQL test container.
- **Roadmap fit:** 8.02/8.04 rework; 16.05/16.06.
- **Effort:** M

#### DEP-05: Auto-cancel superseded deployments (v4.7.0 breaking default)
- **Upstream:** v4.7.0 (discussions #10921, #7678) · `app/Jobs/CancelSupersededDeploymentJob.php`, `app/Enums/CancelPreviousDeployments.php`, `app/Actions/Application/CleanupCancelledDeployment.php`, `bootstrap/helpers/applications.php::cancel_superseded_deployments`, `ApplicationDeploymentJob::startDeployPhase`; migration `2026_11_03_072701_add_auto_cancel_superseded_deployments`; `POST /api/v1/deploy` + MCP `deploy` param `cancel_previous`
- **Coolify behavior (now):**
  - **Scope.** Each (application, pull request, server) is handled separately.
  - **Webhook deployments.** A webhook deployment cancels older *queued* webhook deployments when `is_auto_cancel_queued_deployments_enabled` (default **true**). With `is_auto_cancel_building_deployments_enabled` (default **false**) it also cancels in-progress ones whose `deploy_started_at` is null, so a deployment already replacing containers always finishes.
  - **API/MCP deployments.** These cancel older webhook, API and MCP deployments only when `cancel_previous` is `queued` or `building` (default `none`).
  - **Never cancelled.** Manual UI, restart and rollback deployments are never cancelled and never cancel others.
  - **Mechanics.** Cancellation uses a conditional update to `cancelled-by-user` with `finished_at`. A cancelled build is stopped by a queued cleanup job, which then advances the queue. A log entry names the replacing deployment.
- **Vessel today:** **Missing.**
  - There is no queue (DEP-04) and no trigger source.
  - Cancellation is user-only (`StartDeploymentService.cs:121-149`).
  - The runner checks for cancellation even *after* `compose up` and during health checks (`DeploymentRunner.cs:125,244`), so a canceled status can be recorded on a half-replaced stack. There is no deploy-phase marker.
- **Gap type:** Breaking change + New feature + API contract
- **Priority:** P1 — it shapes the queue design now; webhook-heavy users expect only the latest commit to build.
- **Recommended Vessel approach:**
  - Add `DeploymentSettings.AutoCancelQueued` (default true) and `AutoCancelBuilding` (default false), and `Deployment.DeployPhaseStartedAt`, set through a domain method with an EF optimistic-concurrency token.
  - Implement the policy as a pure Application function applied inside the DEP-04 admission transaction. Run a thin Hangfire `CancelSupersededDeploymentJob` that triggers the run's `CancellationToken` via the job's cancellation registry and stops runtime processes.
  - Add `cancelPrevious` (none|queued|building) to `StartDeploymentRequest`, and test the policy matrix.
- **Roadmap fit:** New items in Phase 8/9 rework (8.20 extension).
- **Effort:** M (after DEP-04)

#### DEP-06: Rollback (local and registry images, restricted targets)
- **Upstream:** v4.3.0 (#11003 rollback image handling), v4.3.11 (#11479 honor selected rollback tags), v4.5.0 (rollback to registry images), v4.5.1 (#12296 any container name), v4.7.0 (rollback only to a listed image or successful production deployment; #12446 Compose commit images) · `app/Actions/Application/GetRollbackImages.php`; `GET /applications/{uuid}/rollback-images` (read), `POST /applications/{uuid}/rollback` (deploy) are new since 49656aa; MCP `ListRollbackImages`, `RollbackApplication`
- **Coolify behavior (now):**
  - Rollback targets are the local images whose repository exactly matches the app image (for Compose, `<uuid>_<service>`; the newest per tag), plus registry tags of the last 10 successful production deployments, which are pulled again if missing. Registry targets don't exist for docker-image, Compose or inline-Dockerfile apps.
  - The current tag is taken from running containers via owner labels.
  - `allows()` accepts only a listed tag or the commit of a successful non-PR, non-rollback deployment. Any other commit is rejected, because a rollback rebuilds from source if the image is missing.
  - Rollbacks are never auto-cancelled.
- **Vessel today:** **Missing.**
  - `Deployment.IsRollback` and `RollbackDeploymentId` exist but nothing sets them (`Deployment.cs:61-63,95`).
  - Images are tagged by deployment ID, `vessel/{appId:N}:{deploymentId:N}` (`DeploymentRunner.cs:265`), and never pushed to a registry.
  - The UI mentions a "rollback target" (`src/Vessel.Web/Components/Pages/ApplicationDetails.razor:29`), but there is no command or API.
- **Gap type:** New feature + API contract
- **Priority:** P1 — core Coolify parity, and ROADMAP 18.12 requires rollback validation.
- **Recommended Vessel approach:**
  - Tag images by commit SHA (keep the deployment ID as a label) and keep N images (`docker_images_to_keep`).
  - Optionally push to a configured `RegistryCredential`.
  - Add a `RollbackService` with `ListTargets` and `QueueRollback(commit)` that validates against listed or succeeded production deployments. It queues a `Trigger=Rollback` deployment that skips clone/build when the image exists.
  - Add the endpoints `GET /api/v1/applications/{id}/rollback-targets` and `POST …/rollback`.
- **Roadmap fit:** New Phase 8 follow-up (or Phase 9b); 18.12.
- **Effort:** M

#### DEP-07: Preview lifecycle: cleanup, stop, API and settings
- **Upstream:** v4.2.0 (#10180 closed-PR cleanup even if status update fails; #10873 skip flags on PR sync; #10188 preview settings in app API), v4.3.11 (#11455 remove persistent volumes on preview delete), v4.3.18 (#11634 cleanup after base branch change), v4.3.22 (PR settings modal), v4.3.23 (#11884 preview runtime logs API), v4.4.0 (preview API; only own networks/volumes; PR 1 vs 12 prefix bug), v4.5.0 (#12006/#12008/#12012/#12013 cleanup for Bitbucket/GitLab/Gitea), v4.5.1 (#6536 keep custom preview domains), v4.7.0 (write needed to open/alter previews; deploy token can only redeploy) · `app/Actions/Application/StopApplicationPreview.php`, `Api/ApplicationPreviewsController.php`, routes `GET/POST /applications/{uuid}/previews`, `GET/PATCH/DELETE …/previews/{pull_request_id}`, `GET …/previews/{id}/logs`; `SyncGithubDeploymentJob::deactivatePullRequest`
- **Coolify behavior (now):**
  - When a PR is closed, merged or retargeted, Coolify stops and removes its containers (exact-match PR suffix), removes only the networks and volumes it created for that preview, deletes its storage records and marks the GitHub deployments inactive.
  - Previews can be listed, opened, redeployed and patched (domain, Docker tag) through the API. Opening or patching needs write; a deploy token can only redeploy an existing preview.
  - Skip markers are honoured, and skipped PR deployments are recorded with a reason.
- **Vessel today:** **Partial.**
  - A closing event only calls `preview.Archive()` (`WebhookProcessingService.cs:131-146`), and `docs/deployment/phase-9-webhooks-preview-deployments.md` says "Container removal remains a later runtime cleanup concern."
  - Preview statuses are only Open and Archived (`src/Vessel.Domain/Webhooks/ApplicationPreviewStatus.cs:5-6`).
  - There is no preview API, stop action or domain management.
- **Gap type:** New feature + API contract
- **Priority:** P1 — without cleanup, previews leak containers, volumes and ports once DEP-02 isolation exists.
- **Recommended Vessel approach:**
  - Add a `PreviewCleanupService` (Application) and a Hangfire `CleanupPreviewJob` that is retry-safe. It goes through `IContainerRuntimeClient` (`compose down --volumes` for the preview project only) and the proxy route removal.
  - Add a `PreviewsController` with read/deploy/write permission splits.
  - Store a skip reason on `WebhookEvent`.
- **Roadmap fit:** 9.14 completion; Phase 10 for preview domains (cross-ref Proxy agent).
- **Effort:** M

#### DEP-08: Private Git sources (GitHub App, GitLab App, Gitea/Forgejo App, deploy keys, HTTPS credentials, SSH host-key pinning)
- **Upstream:**
  - v4.2.0 (#10576/#10610 GitHub Enterprise paths/API URL), v4.3.0 (#10538 self-hosted GitLab private repos; OAuth fields migration `2026_06_03_000000…gitlab_apps`), v4.3.11 (#11453 system-wide GitHub Apps across teams), v4.3.19 (#11685 custom SSH usernames in SCP-style URLs), v4.4.0 (same-team private keys only).
  - v4.5.0 (#12234 Gitea App sources, table `gitea_apps`; #12185 GitLab **system hooks**; #12164 system-wide sources admin-only; GitHub webhooks routed to installation's source; `ssh://` custom ports), v4.5.1 (system-wide Gitea admin-only; owner/repo slug for SSH URLs #10175).
  - v4.6.0 (HTTPS username + password/token, migration `…add_git_https_credentials_to_applications_table`), v4.7.0 (Forgejo/Codeberg providers; `GET /gitea-apps`, `POST /applications/private-gitea-app`, `gitea_app_uuid`; pinned SSH host keys `git_ssh_known_hosts` with published GitHub/GitLab/Bitbucket keys; 4096-bit RSA deploy keys #12461; GitHub reads retried 3x on 5xx, clock skew checked only on rejection), HEAD (`installation_account` on github_apps).
  - Files: `app/Models/GiteaApp.php`, `Api/GiteaController.php`, `Api/GitlabController.php`, `Webhook/Gitlab.php::normalizeSystemHookPayload`, `ApplicationDeploymentJob::save_git_ssh_known_hosts`.
- **Coolify behavior (now):**
  - **Source types.** Applications use one of these sources: public, GitHub App (installation tokens, Enterprise), GitLab App (OAuth app with refresh tokens; webhook secret; system hooks cover all projects), Gitea App (OAuth; Gitea.com, Codeberg or Forgejo presets; custom SSH port and user), deploy key (team private key), or HTTPS credentials (encrypted).
  - **Visibility.** System-wide sources can be created only by instance admins and are visible across teams.
  - **SSH host keys.** Keys are recorded on first use per application, then enforced.
- **Vessel today:** **Missing.**
  - `GitSource` is just `{RepositoryUrl, Branch, CommitSha}` (`src/Vessel.Domain/Applications/GitSource.cs:5-14`), and the Sources page is a placeholder: "GitHub apps, deploy keys … will appear here when source management is implemented" (`src/Vessel.Web/Components/Pages/Sources.razor:9`).
  - Clone takes no credentials (`GitProcessClient.cs:10-27`).
  - Webhook secrets are per application only (`ApplicationWebhookConfiguration.cs`).
  - Only public repositories can be deployed.
- **Gap type:** New feature + Schema + API contract (cross-ref Security agent for host-key pinning and admin-only system-wide sources)
- **Priority:** P1 — deploying private repositories is core parity; most real users need it.
- **Recommended Vessel approach:**
  - **Domain.** Add a `GitSourceConnection` aggregate (kind: GitHubApp|GitLabApp|GiteaApp|DeployKey|HttpsCredential, `IsSystemWide`, `TeamId`) with secrets in `ISecretVault`, and reference it from `Application.GitSource`.
  - **Application.** Add an `IGitCredentialProvider` that produces short-lived credentials: a JWT → installation token for GitHub, an OAuth refresh for GitLab/Gitea, a key file for deploy keys. Credentials are injected through `GIT_ASKPASS` or a temp `GIT_SSH_COMMAND`, never in the URL.
  - **Known hosts.** Store them per application or source, with first-use pinning and an operator override.
  - **Webhooks.** Add `/webhooks/gitlab/system` and per-source secrets. Restrict system-wide sources to instance admins.
  - Deliver in stages: deploy key + HTTPS first (S/M), then GitHub App, then GitLab/Gitea.
- **Roadmap fit:** 9.08 (marked done but only refs discovery exists) → new Phase 9b "Git sources".
- **Effort:** XL

#### DEP-09: Build packs: Railpack (and Static) unimplemented, Nixpacks deprecated, build-time variables never passed
- **Upstream:** v4.2.0 (#10768 Railpack build-time env interpolation; #10840 Buildx isolated from Docker client env), v4.3.15 (#11575 reject invalid build-time names before builds), v4.4.3 (#8238 static PR previews), v4.4.4 (Railpack frontend matches helper CLI), v4.5.0 (#12097 no idle BuildKit builder; **Nixpacks marked deprecated, Railpack recommended**; #12025 start-time buildpack vars kept in runtime), v4.5.1 (helper downloads SHA256-verified), v4.5.3 (concurrent Railpack builds), v4.7.0 (#9726 build-time `HOME`/`DOCKER_CONFIG` broke buildx) · `ApplicationDeploymentJob::ensure_helper_docker_buildx_available_for_railpack`, `without_reserved_docker_client_variables`, `validatedBuildtimeEnvironmentVariableKey`, `withoutDottedBuildVariables`. Unmerged: `origin/add-coolpack-buildpack` (Dec 2025, not merged).
- **Coolify behavior (now):**
  - **Railpack** is the recommended auto-detecting build pack. Builds are serialized while the builder bootstraps, the idle builder is removed afterwards, and env keys reserved for the Docker client are stripped. Build-time variables must be valid shell names (no dots) and are rejected with rename guidance.
  - **Static** builds generate a Dockerfile, including for previews.
  - **Nixpacks** still works but is deprecated.
- **Vessel today:** **Drifted.**
  - `ApplicationBuildPack` offers `Nixpacks`, `Railpack`, `Static`, `Dockerfile`, `DockerCompose` (`src/Vessel.Domain/Applications/ApplicationBuildPack.cs:5-9`), but the runner only special-cases Dockerfile and DockerCompose (`DeploymentRunner.cs:114,272`).
  - Nixpacks, Railpack and Static apps silently fall into `CreateDockerfileCompose`, which builds the repo's `Dockerfile` (`:312-341`) or fails.
  - `docker build` gets an empty build-args dictionary (`:185`); `IsBuildTime` variables are never used.
  - `InstallCommand`, `BuildCommand` and `StartCommand` on `BuildConfiguration` are ignored.
- **Gap type:** New feature + Behavior change
- **Priority:** P1 — Vessel accepts build packs it can't build, and Railpack is now the upstream default recommendation.
- **Recommended Vessel approach:**
  - Reject unsupported build packs at create/update time until they are implemented.
  - Add an `IBuildPackStrategy` per pack in Application, with planner output = build steps, and run the CLI tools through `IProcessRunner` in Infrastructure.
  - Implement Railpack (pinned version, SHA256-verified helper, a per-server BuildKit lock, isolated `DOCKER_CONFIG`) and Static (generated nginx Dockerfile).
  - Pass build-time variables as `--build-arg` or BuildKit secrets with key validation and redaction (DEP-03).
  - Do not implement Nixpacks unless needed for imports; Coolpack is out of scope until it lands upstream.
- **Roadmap fit:** New Phase 8b "Build packs"; 17.06 import mapping.
- **Effort:** L

#### DEP-10: Environment variable value semantics (exact values, literal/multiline, empty values, order, preview copies, duplicates)
- **Upstream:**
  - v4.2.0 (#10773 key validation; #10850 empty service values), v4.4.0 (names `[A-Za-z_][A-Za-z0-9_.]*`; legacy runtime-only names deploy with warning), v4.5.0 (#12024 **values passed exactly as entered**, existing keep legacy escaping, migration `…uses_legacy_escaping…`; #11794 Compose semantics), v4.5.1 (#12240 PATCH keeps omitted flags; #10406 quoted values; `# coolify:` directives #12349), v4.5.0/v4.6.0 per-resource "include Coolify default variables" (`…include_coolify_default_variables_to_resources`, default true).
  - v4.6.0 (Compose: undoubled backslashes, build secrets out of runtime, `SERVICE_URL_<NAME>_<PORT>`), migration `…preserve_compose_null…`, v4.7.0 (#9438 trailing newline of multiline kept; #8279 multi-line JSON; #8041 duplicate env vars removed, `2026_11_15_140000_remove_duplicate_environment_variables`), HEAD `e2bb747a4` (.env written in page order).
- **Coolify behavior (now):**
  - Values reach containers byte-exact: multiline values keep their trailing newline, `$` isn't interpolated unless intended, and empty values are allowed.
  - Variables created before v4.5 keep the old escaping through a flag.
  - Compose `null` vs empty is preserved. Duplicates are prevented.
  - Coolify default variables (`COOLIFY_FQDN`, `COOLIFY_URL`, `COOLIFY_BRANCH`, `COOLIFY_RESOURCE_UUID`, `SOURCE_COMMIT`, …) can be turned off per resource.
- **Vessel today:** **Drifted.**
  - Plain values go through `DomainValidation.Required`, which rejects empty or whitespace values and trims them, losing trailing newlines (`src/Vessel.Domain/EnvironmentVariables/EnvironmentVariable.cs:168-171`; `src/Vessel.Domain/Common/DomainValidation.cs:7-9`).
  - `QuoteEnv` neither escapes nor single-quotes `$`, so `docker compose` interpolates `$FOO` in env_file values. `IsLiteral` and `IsMultiline` are never consulted (`DeploymentRunner.cs:394-402`; usage only in `ResourceManagementService.cs:337-368`).
  - Keys are sorted ordinally, not in page order (`:389`).
  - The key regex disallows dots (`src/Vessel.Domain/EnvironmentVariables/EnvironmentVariableKey.cs:27`; Coolify allows them for runtime variables).
  - The unique index includes nullable `ProjectId`/`EnvironmentId`/`ServerId`/`ApplicationId` with default NULLS DISTINCT, so team-, project- and server-scoped duplicates are possible (`VesselDbContext.cs:945-955`; migration `20260519095632_Phase7ResourceManagement.cs:174`).
  - Default `VESSEL_*` variables are always injected with no toggle (`DeploymentRunner.cs:350-355`).
- **Gap type:** Behavior change + Schema
- **Priority:** P1 — wrong values reaching containers (`$`, empty values, multiline keys/certs) is a correctness bug users hit immediately.
- **Recommended Vessel approach:**
  - Allow empty values and stop trimming plain values (validate length only).
  - Write the env file through an `EnvFileWriter` that single-quotes or escapes per the Compose env_file grammar, so values are exact; literal means no interpolation.
  - Store a display order; allow dots for runtime-only keys while build-time keys require shell-safe names.
  - Add `IncludeDefaultVariables`, and fix the index with `NULLS NOT DISTINCT` (PostgreSQL 15+) or a computed scope key. Migration plus a dedupe script.
  - Golden tests for the env file.
- **Roadmap fit:** 7.13/8.09 rework; 17.07.
- **Effort:** M

#### DEP-11: Docker Compose build pack: parse and validate instead of raw passthrough
- **Upstream:**
  - v4.3.0 (#11040 stable router suffix for dotted/hyphenated names — cross-ref Proxy agent), v4.3.19 (atomic Compose env file; pull image services before stop), v4.4.0 (external volumes used as declared; `driver`/`driver_opts`/labels kept → `ignores_compose_driver_options`; preview delete removes only own networks/volumes; content files before start; Compose injection validation — Security), v4.4.2 (large files, YAML aliases).
  - v4.5.0 (**reject `scale`/`deploy.replicas` > 1**, #12213; UTF-8 BOM stripped #12223; explicit file paths and volumes removed by name #12199; `../` binds resolved), v4.5.1 (skip image build when inputs match; Compose from Git objects and submodules #12320; reject Compose without services), v4.5.3 (#12363 required runtime-only vars in build).
  - v4.7.0 (warning plus a move button when the Compose file sits in a subdirectory of the base dir — Compose runs in the base dir; empty-file error #12477; per-preview project #9014; invalid service names rejected on save; services removed from compose marked `is_removed_from_compose`), v4.3.22 (host_path removal — cross-ref Data agent). Unmerged: `origin/feat-compose-include-support` (flatten `include:`).
  - Files: `bootstrap/helpers/parsers.php::validateComposeServiceScale`, `ensure_compose_build_contexts_inside_repository`, `HasComposeVolumeWarnings.php`.
- **Coolify behavior (now):**
  - Coolify parses the Compose file and normalizes it: container names, labels, networks and volume renaming, per-service domains and ports.
  - It validates the file: scale/replicas, build contexts inside the repository, service names, non-empty `services:` map.
  - It keeps declared volume semantics and runs `docker compose` with explicit `-f` and the project directory set to the base directory.
- **Vessel today:** **Partial.**
  - `CreateComposePassthroughAsync` reads the repo file and rejects it if the raw text contains `".."` anywhere. That produces false positives: `"Loading..."`, image tags, comments (`DeploymentRunner.cs:298-304`).
  - A missing Compose file silently falls back to the generated Dockerfile stack instead of erroring (`:306-309`).
  - The path reuses `DockerfilePath` as the Compose path (`:298`).
  - Nothing is parsed (no YAML dependency in any `.csproj`), so there is no scale check, no label injection on repo services, and no volume/network namespacing. Proxy routes can't target Compose services.
  - Phase 8.08 claims "Docker Compose path" as done (`ROADMAP.md:338`).
- **Gap type:** Behavior change + Breaking change (scale rejection)
- **Priority:** P1 — Compose is a primary Coolify build pack, and Vessel's passthrough can't be routed or cleaned up safely.
- **Recommended Vessel approach:**
  - Add a `ComposeDocumentNormalizer` in Application behind an `IComposeParser` port, with YamlDotNet or similar in Infrastructure (justify the dependency).
  - It produces a typed model, validates (scale/replicas policy, build context ⊂ repo, `../` binds resolved and checked against owned paths, BOM, empty services, service-name charset), injects `vessel.*` labels and networks, and namespaces volumes. External volumes are kept, and so are driver options for new volumes (keep the existing ones flagged).
  - Decide explicitly whether Vessel sets `container_name`. If it does, reject replicas > 1 like Coolify; if not, document replica support and port conflicts.
  - Golden snapshot tests (ROADMAP 8.23).
- **Roadmap fit:** 8.08 rework; new Compose items; Phase 10 (routing) cross-ref.
- **Effort:** L

#### DEP-12: Webhook fan-out and repository matching correctness
- **Upstream:** v4.4.0 (pushes without commits → clean response; hide matching apps in manual webhook responses — Security), v4.5.0 (GitHub watch paths use the **net change** of a push and paginate PR files; GitHub webhooks routed to the installation's source), v4.3.17 (invalid repo URLs → no malformed commit links) · `app/Http/Controllers/Webhook/{Github,Gitlab,Gitea,Bitbucket}.php`
- **Coolify behavior (now):**
  - Every application whose repository (exact full-name match) and branch match is evaluated independently. Each one's skip, watch-path and auto-deploy decision applies only to that application, and all matching apps deploy.
  - Watch paths use the push's net file change.
- **Vessel today:** **Drifted.**
  - `ProcessPushAsync` returns after queueing the first verified application. A skip marker or watch-path miss on one app ends processing for all apps (`WebhookProcessingService.cs:83-102`), so two apps on the same repo and branch (staging/prod in different environments, monorepo services) won't both deploy.
  - `RepositoryMatches` uses substring `Contains` (`:362-365`), so `org/app` matches `org/app-backend`; the cross-team impact is mitigated only by per-app signature checks.
  - The preview path has the same first-match return (`:118-179`).
- **Gap type:** Behavior change (correctness; cross-ref Security for matching)
- **Priority:** P1 — monorepos and multi-environment setups silently skip deployments.
- **Recommended Vessel approach:**
  - Normalize repository identity (host + owner/repo, case-insensitive) on the Application and compare it exactly.
  - Loop over every match and record per-application outcomes (a child table `WebhookEventDelivery`, or a JSON outcome list) instead of a single `MarkProcessed`.
  - Compute the net change set for watch paths and unit-test the fan-out.
- **Roadmap fit:** 9.10 fix.
- **Effort:** S–M

#### DEP-13: Rolling updates, drain delay and health-check semantics
- **Upstream:** v4.4.0 (an error after the new container passes its health check no longer removes it or marks failure; proxy connected to destination network before start), v4.5.1 (#10974 Coolify healthcheck replaced Dockerfile `HEALTHCHECK`), v4.5.3 (migration `…keep_dockerfile_healthcheck_for_existing_applications`), v4.7.0 (#12455 HEALTHCHECK options parsed like Docker; #9475 from built stage; `running:starting` state), HEAD (`e9dfd0dfb` expected code and response text; HEAD uses `-I`; HTTPS `-k`; `a3712f890` stop 502s during rolling updates), migration `…rolling_update_drain_seconds…` (default **5**, 0–300) · `ApplicationDeploymentJob::wait_for_proxy_to_route_new_container`, `proxyLabelsChanged`, `markNewVersionHealthy`
- **Coolify behavior (now):**
  - The new container starts beside the old one and is health-checked inside the container: 2xx/3xx pass, a configured 4xx/5xx code also passes, and the optional body text must match.
  - Once it's healthy, Coolify waits `rolling_update_drain_seconds` for the proxy to route (skipped if proxy labels changed), then stops the old container.
  - A failure late in the deployment keeps the healthy new container; a failed new container is removed.
  - Existing apps with a Dockerfile `HEALTHCHECK` keep it.
- **Vessel today:** **Missing / Drifted.**
  - `compose up --detach --remove-orphans --build` replaces in place, which causes downtime (`DeploymentRunner.cs:123-124`).
  - The health check runs from the control plane against `http://localhost:{port}` (`:267-271`), so it only works for local servers with host-published ports, and it passes any status `< 500`, including 404 (`:231`). There's no expected code or text, method, or Docker `HEALTHCHECK` awareness.
  - A proxy-apply exception after a passing health check marks the deployment Failed (`:127-131`, catch at `:148-153`).
- **Gap type:** Behavior change + New feature
- **Priority:** P2 — zero-downtime deploys are expected parity, but this pre-dates v4.1. The new upstream semantics (drain, health rules) should shape it.
- **Recommended Vessel approach:**
  - Implement blue/green within the Compose project: a new service instance with a deployment-scoped name, then health via `docker inspect` health or `docker exec` curl through `IContainerRuntimeClient`, then proxy switch, then drain delay, then old stop.
  - Add `HealthCheckPolicy` (method, path, expected code, text, interval, retries, start period) to `RuntimeConfiguration`, and a `RollingUpdateDrainSeconds` setting (default 5, 0–300).
  - Distinguish "healthy-then-later-error" from failure in the domain.
- **Roadmap fit:** 8.12/8.13 rework; 10.x proxy switch.
- **Effort:** L

#### DEP-14: Restart limits and crash-loop tracking (opt-in)
- **Upstream:** v4.3.15 (#11566 stopped containers stay visible at their limit; retry/remove actions), v4.3.19 (#11705 email toggle works), v4.3.21 (**made opt-in**; resources still at the old default of 10 reset to unlimited), v4.5.1 (#10761 stop again while restarting past the limit), migrations `2026_03_27_000000_add_max_restart_count…`, `…restart_limit_reached…`, `…restart_limit_to_application_previews/service_applications`, `2026_09_04_132827_remove_restart_limits_from_databases`, `2026_09_15_105627_make_restart_limits_opt_in`, `…container_present…`, `…restart_limit_reached_notifications_to_*` · `app/Traits/HasRestartLimit.php`, `app/Services/RestartCountTracker.php`, `app/Notifications/Application/RestartLimitReached.php`, `Actions/Docker/GetContainersStatus.php`
- **Coolify behavior (now):**
  - Applications, previews and service apps (not databases) have `max_restart_count` (default **0** = unlimited), plus `restart_count`, `restart_limit_reached`, `last_restart_at` and `last_restart_type`.
  - The status poller tracks Docker `RestartCount`. When the limit is reached it atomically claims the flag, stops the container but keeps it (`container_present=true`), and sends the `RestartLimitReached` notification (per-channel toggle).
  - A deploy, start or stop resets the counter.
- **Vessel today:** **Missing.** There is no restart tracking in Domain or Application (no matches for `RestartCount`/`RestartLimit` in `src/`). The server poller only counts running containers (`src/Vessel.Application/Monitoring/ServerHealthPollingService.cs:72`), and there are no application start/stop/restart commands.
- **Gap type:** New feature + Schema
- **Priority:** P2 — an opt-in safety feature that depends on per-app status polling.
- **Recommended Vessel approach:**
  - Add a `RestartPolicy` value object on Application (MaxRestarts = 0 by default) with a pure `RestartCountEvaluator` in Domain (port `RestartCountTracker` semantics, including the new-generation handling).
  - Add a status-poll job step, a `RestartLimitReached` notification event type (cross-ref Notifications agent), and an application lifecycle service (start/stop/restart) through `IContainerRuntimeClient`.
- **Roadmap fit:** Phase 12 monitoring follow-up + Phase 13 event type.
- **Effort:** M

#### DEP-15: Move, clone and migrate resources
- **Upstream:** v4.2.0 (#8968 move apps, databases and services between environments via API), v4.3.0 (#11003 duplication fixes), v4.4.4 (clone preserves source values, no duplicate DB env vars), v4.5.1 (confirmation and source-stopped copy for clone with volume data; clone suffix replaced, not appended #9976), v4.6.0 (Compose labels on cloned volumes), v4.7.0 (clone/new resource needs a usable server — localhost needs admin; clone doesn't copy previews; Cloudflare DNS records for clone domains) · routes `POST /applications/{uuid}/move`, `/clone`, plus `MigrateResourceToDestination.php`, `FinalizeResourceMigrationJob.php`, `HostPathCloneJob.php`, `CloneVolumeDataWithSourceStopped.php`, `StagesCloneArchives.php`
- **Coolify behavior (now):**
  - **Move** re-parents a resource to another authorized environment.
  - **Clone** copies the configuration, with name suffix replacement and no previews. It optionally copies volume data: the source is stopped, the data copied, then the source restarted even if a step fails.
  - **Migrate to server** stops the app, optionally transfers volumes and bind host paths between managed servers, then repoints the destination. A redeploy is needed afterwards.
- **Vessel today:** **Missing.** `ResourceManagementService` has create/update/archive for projects and environments but no move or clone for apps (`src/Vessel.Application/Resources/ResourceManagementService.cs:57-395`, public method list).
- **Gap type:** New feature + API contract
- **Priority:** P2 — a valued operator workflow, not on the critical path.
- **Recommended Vessel approach:**
  - Add `MoveResourceCommand` (pure DB re-parent with authorization on both environments plus audit) first.
  - Then `CloneApplicationCommand` (config only; secrets re-encrypted via vault).
  - Then a Hangfire-orchestrated `ResourceMigrationWorkflow` with a server-mutation lock and volume transfer over SSH/rsync through `IProcessRunner`.
- **Roadmap fit:** New Phase 7b items; Phase 11 data (cross-ref Data agent for volume copy).
- **Effort:** M (move + clone) / L (migrate)

#### DEP-16: Deploy API and UX parity (deploy params, preview/commit authorization, history, commit details)
- **Upstream:** v4.2.0 (POST-only `/deploy` — breaking; #9909 `force=false` no cache bust; #10551 source-commit setting), v4.3.0 (#11023 deploy, redeploy and force-deploy no longer confirm), v4.3.22/v4.4.0 (PR filter in history; reopen the live deployment log; running-deployments indicator), v4.4.0 (`POST /deploy` with `pull_request_id` builds the PR; PR id range validation), v4.4.3 (#12116 keep recorded commit message when a redeploy skips clone), v4.5.0 (compact history beside logs; Deployment/Service selectors with merged replica logs; #12030 commit details in deployment emails → `IncludesDeploymentCommitDetails`), v4.7.0 (`docker_tag` validated; preview open or commit choice needs write; deploy token only redeploys) · `Api/DeployController.php`, `app/Traits/IncludesDeploymentCommitDetails.php`
- **Coolify behavior (now):**
  - `POST /api/v1/deploy?uuid|tag&force&pr&docker_tag&cancel_previous` returns per-resource `queued` / `skipped` / `queue_full`.
  - Choosing an arbitrary commit, or opening or changing a preview, requires write, not just deploy.
  - Deployment emails can include the SHA, branch and message.
- **Vessel today:** **Partial.**
  - `POST /api/v1/deployments` is already POST-only, and the UI deploys without a confirmation (covered).
  - `StartDeploymentRequest` accepts any `PreviewId` and `WebhookEventId` from API clients, and neither is validated against the application (`src/Vessel.Application/Deployments/DeploymentModels.cs:6-11`; `StartDeploymentService.cs:61-68`). Arbitrary `CommitSha` is allowed with only a length check (`:113-119`), under the single `deployments.start` permission (`src/Vessel.Application/Authorization/VesselPermissions.cs:13`).
  - There's no tag-based deploy, PR deploy, `docker_tag` or `cancelPrevious`, and no commit-details notification option.
  - Log export exists (`DeploymentsController.cs:63-87`).
- **Gap type:** API contract + Security hardening (cross-ref Security/Authorization agent)
- **Priority:** P2 — the contract shape matters before the public API stabilizes. The PreviewId/WebhookEventId injection should be closed with DEP-02.
- **Recommended Vessel approach:**
  - Split the public request DTO (in `Vessel.Shared`) from the internal command. Remove `WebhookEventId` and `PreviewId` from the public API and add `pullRequestNumber` resolved server-side.
  - Require `applications.write` for an explicit commit or a new preview.
  - Return admission results from DEP-04, and add `IncludeCommitDetails` to deployment notifications (cross-ref Notifications agent).
- **Roadmap fit:** 6.x API conventions, 8.17-8.19, 13.x.
- **Effort:** S–M

#### DEP-17: Smart scan, repository detection for new Git applications
- **Upstream:** v4.5.0 (#8502) · `app/Services/RepositoryDetector.php`, `app/Traits/HasRepositoryDetection.php`
- **Coolify behavior (now):**
  - When a Git app is created, Coolify lists the repo tree via `git ls-tree` without a full checkout. It finds Dockerfiles (and their `EXPOSE` port) and Compose files (preferring the default names), and reads `.env.example/.sample/.template/.dist` files up to 64 KiB.
  - It then suggests a build pack, Dockerfile path, Compose path, port and environment variable keys.
- **Vessel today:** **Missing.** `ApplicationCreateGit.razor` takes a build pack choice; there's no detection service (`src/Vessel.Application/Git/IGitClient.cs:3-16` has no tree or scan operation).
- **Gap type:** New feature
- **Priority:** P2 — a meaningful onboarding UX win and cheap once DEP-01's fetch primitives exist.
- **Recommended Vessel approach:** Add `IGitClient.ListTreeAsync` and `ReadBlobAsync` (blobless shallow fetch) and a pure `RepositoryDetector` in Application returning a `RepositoryDetectionResult`. The UI pre-fills fields, and env-example values are imported as keys only, never auto-saved as secrets.
- **Roadmap fit:** New Phase 7/9 UX item.
- **Effort:** S–M

#### DEP-18: Build servers, concurrent build limits and multi-server application deployments
- **Upstream:** v4.4.0 (server roles: deployments-only / builds-only / both; `server_role` API; team build-server fallback; multi-server apps pull the image the primary pushed; one notification; queued deployments on additional servers start after the running one), v4.7.0 (#7649 concurrent build limit per build server; freed slots start waiting deployments on other servers) · migration `…parent_deployment_uuid…`, `…is_build_server_fallback_enabled_to_teams_table`, `…remove_duplicate_additional_destinations`; `ApplicationDeploymentJob::selectBuildServer`, `pull_image_for_additional_server`, `sendMultiServerDeploymentNotification`
- **Coolify behavior (now):** An app can deploy to a primary server plus additional servers. Builds run on a build server with a free slot (or fall back to the deployment server if the team allows), are pushed to a registry, and are pulled by deploy-only servers. Child deployments link through `parent_deployment_uuid`.
- **Vessel today:** **Missing.**
  - An application has one `ServerId` (`src/Vessel.Domain/Applications/Application.cs:37`).
  - The runner refuses SSH servers entirely: "Phase 8 deployment runner currently supports local Docker/Podman targets" (`DeploymentRunner.cs:77-79`). That is a prerequisite gap the Servers agent should own.
- **Gap type:** New feature + Schema
- **Priority:** P2 — remote execution is the prerequisite (P1 in the Servers domain); build servers and multi-server can follow.
- **Recommended Vessel approach:** After the SSH runtime target lands, add `ServerRole` and a build-slot reservation using a per-server Redis or advisory-lock semaphore. Add `ApplicationDestination` (primary + additional) and a `ParentDeploymentId` on Deployment. Registry push/pull goes through `IContainerRuntimeClient`.
- **Roadmap fit:** New phase after 14 (or a Phase 8c); cross-ref Servers agent.
- **Effort:** L

#### DEP-19: Readable container names and a container name prefix
- **Upstream:** v4.3.13 (custom names kept in consistent mode), v4.4.0 (#11704 `custom_container_name_prefix`, slugified, ≤30 chars, unique per server; #11702 ISO-8601 suffix `<name>-20260908T141530`; #11701 custom names applied to Compose services; labels `coolify.applicationUuid` etc. instead of numeric IDs), migrations `2026_09_08_214510_align_consistent_container_name…`, `2026_09_08_214513_add_custom_container_name_prefix…`
- **Coolify behavior (now):** Generated container names are `<prefix or uuid>[-service]-<ISO timestamp>`. The prefix must be unique per server and the name stays within the DNS-label limit. Containers are identified by UUID labels, not names.
- **Vessel today:** **Partial.** Vessel relies on the Compose project `vessel-{appId:N}` and the service named from the sanitized app name (`DeploymentRunner.cs:263-264,557-564`), with `vessel.*` labels (`:539-546`). It already identifies by labels; there's no prefix option.
- **Gap type:** New feature
- **Priority:** P3 — cosmetic or operator convenience; Vessel's label-first identity already matches the intent.
- **Recommended Vessel approach:** Add an optional `ContainerNamePrefix` (validated slug, unique per server) used in the project and service names once rolling updates (DEP-13) introduce per-deployment instances.
- **Roadmap fit:** DEP-13 follow-up.
- **Effort:** S

#### DEP-20: GitHub Deployments sync
- **Upstream:** v4.7.0 · `app/Jobs/SyncGithubDeploymentJob.php`, migration `2026_11_03_195902_add_github_deployments_columns` (`github_apps.deployments`, `application_deployment_queues.github_deployment_id/state`)
- **Coolify behavior (now):**
  - For a private GitHub App with `Deployments: write`, each deployment and preview is mirrored to a GitHub Deployment: environment `<app>` or `<app> (preview)`, transient for previews, with `log_url` and `environment_url` on success.
  - The job is idempotent and converges, with a per-deployment lock and 3 retries. Closed PRs are marked `inactive`, which is terminal.
- **Vessel today:** **Missing.** It depends on the GitHub App source (DEP-08).
- **Gap type:** New feature
- **Priority:** P3 — a nice integration once GitHub App sources exist.
- **Recommended Vessel approach:** Add an `IGitHubDeploymentsClient` (Infrastructure) and a Hangfire `SyncGitHubDeploymentJob` that subscribes to the `DeploymentStatusChangedEvent` domain event (`src/Vessel.Domain/Deployments/DeploymentEvents.cs`). Store the external ID and state on Deployment.
- **Roadmap fit:** After DEP-08.
- **Effort:** M

#### DEP-21: GitHub Actions self-hosted runners on build servers (beta)
- **Upstream:** v4.4.0 (#12054), v4.4.2 (#12109 runners on deploy+build servers), migrations `…github_runner_fields_to_github_apps_table`, `…create_github_runner_configs_table`, `…create_github_runner_executions_table`, `…allow_pull_requests…`, `…is_pull_request…`, `…index_for_active_github_runner_executions` · `GithubRunnerConfig`/`GithubRunnerExecution` models, `Provision/Deregister/Reconcile/CleanupGithubRunnerJob`, `GithubRunnerDockerMode` (privileged DinD / Sysbox / none), `GithubRunnerApi`, `GithubRunnerContainer`
- **Coolify behavior (now):**
  - A builds-only server can run ephemeral runner containers for an organization GitHub App: one container per job, removed at the end.
  - It's configurable: labels, parallelism, CPU and memory, image, timeouts, and the Docker-in-job mode. PR-originated jobs are refused unless allowed.
  - The server role is locked while runners are enabled.
- **Vessel today:** **Missing.**
- **Gap type:** New feature
- **Priority:** P3 — a beta, niche feature; privileged DinD conflicts with Vessel's host-root caution. Defer and revisit after DEP-08 and DEP-18.
- **Recommended Vessel approach:** If adopted, use a reconcile loop (Hangfire recurring) with an explicit lifecycle policy (AGENTS.md forbids unmanaged detached processes). Default to Sysbox or no-Docker, document the privileged risk, and keep it isolated in Infrastructure.
- **Roadmap fit:** New post-1.0 phase.
- **Effort:** XL

#### DEP-22: Git error diagnostics and helper hygiene in deployments
- **Upstream:** v4.7.0 (missing Git error reason; `gitHostUnreachableHint`; #7568 trust the server CA bundle for TLS-inspecting proxies; #9785 GitHub API retries), HEAD `d551f29c7` (commit message read from repo root), `28f7d91ac` (remove helper of failed deployment), v4.6.0 (helper cleanup removes only finished deployments' helpers), v4.3.9 (stuck container removal deferred and retried)
- **Coolify behavior (now):** Clone and fetch failures surface the Git stderr reason plus a reachability hint. Builds trust the host CA bundle. Per-deployment helper containers are removed on failure and cancellation, and only those helpers.
- **Vessel today:** **Partial.** `ProcessExecutionException` carries the result, but the runner logs only `ex.Message` (`DeploymentRunner.cs:150`), so the Git stderr is not shown. There are no helper containers; the workspace is cleaned in `finally` (`:155-158`), which is covered.
- **Gap type:** Behavior change
- **Priority:** P3 — debuggability.
- **Recommended Vessel approach:** Log the redacted stderr tail for Git and Compose failures with a categorized hint (DNS, auth, host key). Allow an operator CA bundle path for Git through config.
- **Roadmap fit:** 12.x/18.13.
- **Effort:** S

### Reviewed and not applicable
- **MCP server deployment tools** (`is_mcp` on the queue, MCP `deploy`/`ListRollbackImages`/`ManagePreview`/…, `ExplainFailedDeploy` prompt): Vessel has no MCP surface. The `Trigger` enum in DEP-04 reserves `Mcp` if one is added later.
- **`include_network_hosts_in_build` / `--add-host` exclusions** (v4.7.0 #12452): Vessel builds don't inject `--add-host`, so there's no cache-busting problem to fix. Revisit only if build-time network host resolution is added.
- **Coolpack build pack** (`origin/add-coolpack-buildpack`, Dec 2025, unmerged) and **Compose `include:` support** (`origin/feat-compose-include-support`, 2 commits, unmerged as of HEAD): not shipped upstream. Watch, but don't plan yet.
- **Worker host recording** (`2026_10_28_204953_add_worker_host_to_scheduled_job_executions`, `horizon_job_worker`): Laravel Horizon worker bookkeeping. Hangfire already records the server that processed each job.
- **`backfill_finished_at_of_ended_deployments`**: fixes upstream rows missing `finished_at`. Vessel already sets `FinishedAt` on every terminal transition (`Deployment.cs:163`), so this is covered.
- **Deploy confirmation removal** (v4.3.0 #11023): Vessel's UI never had a confirmation step, so it's covered.
- **POST-only `/deploy` and lifecycle endpoints** (v4.2.0 breaking): Vessel's deploy and cancel endpoints are `[HttpPost]` (`DeploymentsController.cs:100,114`), so they're covered. Start/stop/restart endpoints don't exist yet (DEP-14).
- **Service parts `is_removed_from_compose`** (v4.7.0 #9591): Vessel's `ServiceResource` has no per-part rows (`src/Vessel.Domain/Services/ServiceResource.cs`). Defer to the Services/templates agent.
- **Image scanning deploy gate** (`scan_gate_*`, ImageScan*): belongs to the Security/scanning domain agent.
- **Stable router suffix for dotted/hyphenated Compose service names** (v4.3.0 breaking #11040): proxy label generation is the Proxy agent's area. Vessel has no Compose service routing yet (DEP-11).
- **Persistent volume `host_path` removal** (v4.3.22 breaking) and **Compose volume warnings** (`HasComposeVolumeWarnings`): storage semantics belong to the Data agent. DEP-11 covers parser-side volume declaration handling.
- **Laravel/Horizon specifics** (`ShouldBeEncrypted` job payloads, `Cache`-based new-version-healthy flag, Livewire deployment selectors and canvas): implementation details. The semantics are captured in DEP-04, DEP-05, DEP-13 and DEP-16.
- **Docker version-aware stop flags, helper image Docker/Buildx/Railpack version bumps, SHA256-verified helper downloads**: Vessel has no helper image. Pin and verify tool versions as part of DEP-09 if Railpack is shipped.
- **Coolify Cloud subscription, Stripe, and release-channel/GHCR CI changes in the v4.6.0 notes**: out of scope.

## 11. NET — Proxy, Domains, DNS and TLS

### Summary

Between v4.1.0 and v4.7.6, Coolify turned domains into a full edge-management area. The main additions:

- **Managed DNS:** Cloudflare integration tokens, zones, owned/referenced managed records with a hostname lock, conflict/replace flow, release on delete/edit, and a DNS API (v4.4.0–v4.7.0).
- **DNS status checks:** asynchronous checks per domain on apps, service apps and previews, with Cloudflare-aware matching, per-server public IPv4/IPv6, resolver timeouts, fallback and TCP retry.
- **Per-domain controls:** noindex, internal port overrides, opt-in permanent redirects, force-HTTPS on service apps, wildcard/underscore/IP hostnames, stable router names for Compose services (v4.3.0 breaking).
- **Server-side proxy features:** custom error pages and maintenance mode, basic-auth hash options, a certificate resolver per server with a per-resource override, `acme.json` inventory with backup/restore/delete-unused, validation before save, no Traefik dashboard, Traefik 3.7 patch tracking, and connecting the proxy to networks only when needed (the hourly job was removed).
- **Other:** automated Cloudflare Tunnel setup for SSH, and Sentinel-based traffic analytics.

**Vessel's position.** Vessel has one Traefik file-provider generator with per-server versions, locks and audit. It only covers Applications, assumes a proxy container that nothing creates, and writes the config to the control-plane host only. Reading the generated YAML turned up several correctness bugs:

- invalid YAML escapes (`\.`)
- a literal `$${1}` in redirect replacements
- router-name truncation and collisions
- upstreams addressed by the app's display name on a network shared by every app on the server
- TLS routes with no HTTP entry point

**Counts:** P0 2 · P1 5 · P2 7 · P3 3 · N/A (listed below).

### Gap items

#### NET-01: Generated Traefik config is incorrect, and its blast radius is the whole server
- **Upstream:** v4.3.0 #11040 (stable router names with hash suffix), v4.3.7 (Traefik redirect capture-group fix, commits `47da47d8e`, `9d64b3061`), v4.4.0 #11923 (validate proxy config before save) · `bootstrap/helpers/docker.php` (`traefikSafeServiceNameSegment()`, `fqdnLabelsForTraefik()`; `$redirect_capture_prefix` is `$$` only when the labels pass through Compose interpolation), `app/Actions/Proxy/SaveProxyConfiguration.php`
- **Coolify behavior (now):**
  - Router and service names are `http-{i}-{uuid}[-{safeService}-{md5[0..4]}]`. Dots, underscores and hyphens are normalized, and a hash is always appended so `api.test` and `api-test` never collide.
  - The redirect replacement uses `${1}` and is escaped to `$${1}` only inside Compose labels.
  - Every domain gets both an `http` and an `https` router. The `http` router either carries `redirect-to-https` or serves plain HTTP.
  - Upstream targets are unique container names, and each resource's labels are independent. A bad domain on one resource does not stop another resource from routing.
  - Proxy configuration is parsed and validated before it is saved.
- **Vessel today:** Drifted, and buggy.
  - `TraefikProxyProvider.cs:81` writes `regex: "^https?://{Regex.Escape(host)}(.*)"` into a YAML double-quoted scalar. `\.` is not a valid YAML escape, so the whole dynamic file fails to parse in Traefik as soon as any redirect exists.
  - `:82` emits `replacement: "https://host$${1}"`. In a file provider (no Compose interpolation), Go's regexp turns `$$` into `$`, so the redirect target ends in a literal `${1}`. The test `tests/Vessel.IntegrationTests/Proxy/TraefikProxyProviderTests.cs:52` asserts this wrong output.
  - `:187-191` `RouteName` truncates to `39 + host.Length`, but the prefix is 40 characters, so the last character of every host is always dropped. Hosts that differ only in their last character, or in `.` vs `-`, or after the 63-character cut, collide for the same app.
  - `:33-38`: a TLS route is bound only to `websecure`, so `http://host` returns 404. There is no HTTP→HTTPS redirect.
  - `ProxyConfigurationService.cs:333-341` + `DeploymentRunner.cs:264,266`: the upstream is `http://{sanitized app display name}:{port}` on the network `vessel-{serverId}`, which every app on the server shares. Two apps named `web` (even in different teams) both register DNS alias `web`, so Docker round-robins traffic between them. That is cross-tenant traffic leakage.
  - `TraefikProxyProvider.cs:104-106` validation is substring-based. Any config containing the word `password` is rejected, which would block future basic-auth middlewares, and the YAML is never parsed.
  - `ProxyConfigurationService.cs:70-76,89-96`: one invalid domain, or a concurrent apply (lock wait is `TimeSpan.Zero`), throws inside `DeploymentRunner.cs:128` and fails every deployment on that server.
- **Gap type:** Behavior change / Security hardening (tenant isolation)
- **Priority:** P0 — routing is broken or misdirected today: invalid YAML, wrong redirects, and cross-app upstream aliasing.
- **Recommended Vessel approach:**
  - Generate Traefik YAML through a typed model serialized with a YAML emitter (for example YamlDotNet), not a `StringBuilder`.
  - Router and service keys: `{appIdN}-{routeIndex}-{hostSlug}-{sha256(host|path)[..6]}`, kept stable across versions.
  - Upstream: a unique per-deployment container alias such as `vessel-{appIdN}`, or a per-app network the proxy joins. Never the display name.
  - Always emit an `http` router per domain, carrying a redirect-scheme middleware when TLS is on or force-HTTPS is set.
  - Parse the YAML in `Validate`, and replace the `password` substring heuristic with secret-reference checks.
  - In the deployment path, wait on the proxy lock with a bounded wait, and validate per route: skip or quarantine an invalid route, mark only that app's route failed, and keep the rest of the server routing.
  - Golden tests should compare against real Traefik parse output, for example via `traefik` image validation in the E2E suite.
- **Roadmap fit:** Fix inside Phase 10 (10.03/10.04/10.13 regression), plus 15.05 and 15.12.
- **Effort:** M

#### NET-02: Proxy lifecycle per server (static config, container, networks, status, NONE type)
- **Upstream:**
  - v4.4.0: the hourly `ConnectProxyToNetworksJob` and `PROXY_CONNECT_NETWORKS_INTERVAL_SECONDS` were removed. The proxy is now connected when a destination is created, when a resource is deployed (before its containers start), and when the proxy starts or restarts. New proxies use `traefik:v3.7` / caddy-docker-proxy 2.13, and new configs stop exposing the Traefik dashboard (migration `2026_09_25_210000_remove_legacy_traefik_dashboard_labels`).
  - v4.3.7 reconciles proxy networks from running containers; v4.3.11 #11476 restores them after a host reboot.
  - v4.5.0 #12225: the proxy check requires container name `coolify-proxy`.
  - v4.3.9/4.3.11/4.4.0 add pending-proxy-config / restart-vs-update status and restart-loop notification throttling.
  - Files: `bootstrap/helpers/proxy.php` (`generateDefaultProxyConfiguration`, `connectProxyToNetworks`, `ensureProxyNetworksExist`), `app/Actions/Proxy/{StartProxy,StopProxy,CheckProxy}.php`, `app/Services/ProxyPortParser.php`, `app/Enums/ProxyTypes.php` (NONE/TRAEFIK/CADDY).
- **Coolify behavior (now):**
  - Each server has a proxy compose file under its proxy path. Traefik is started with entrypoints `http:80` and `https:443` (HTTP/3), the file provider at `/traefik/dynamic` with `watch=true`, the `letsencrypt` HTTP-01 resolver with `/traefik/acme.json`, and `ping`.
  - The proxy port mapping is parsed (`ProxyPortParser`) and conflicts are checked before saving.
  - Proxy status comes from the real container, from Sentinel pushes, and from reconciliation.
  - A server can have proxy type NONE.
- **Vessel today:** Partial / Missing.
  - Nothing creates the proxy. The only reference to `vessel-proxy` is `TraefikProxyProvider.cs:156`.
  - No static config defines the `letsencrypt` resolver that `:37` references.
  - The dynamic file is written to `AppContext.BaseDirectory/proxy/traefik` on the control-plane host (`:16,134-140`), and `docker kill` runs against the local daemon only (`:154-157`). Remote SSH servers never receive routes.
  - A missing container is reported as success (`:161-163`).
  - Reload uses `SIGHUP`. Traefik reloads file-provider config through `watch`, not through a signal.
  - `ProxyHealthy` is derived from the DB version status, not from the container (`ServerHealthPollingService.cs:85-88`).
  - There is no per-server proxy type (`ProxyProviderKind` exists only on config versions).
- **Gap type:** New feature / Behavior change
- **Priority:** P0 — Phase 10 is marked done, but routing cannot work on any remote server and TLS can never be issued.
- **Recommended Vessel approach:**
  - Domain: `ServerProxySettings` (type Traefik/Caddy/None, pinned image, ports, desired state, last-applied hashes).
  - Application: `ProxyLifecycleService` (ensure / start / stop / restart / status) under the existing `proxy:{serverId}` lock.
  - Infrastructure: render a static Traefik compose (dashboard off, file provider with watch, ACME storage) and deliver files through the server's runtime target (SSH/SFTP, atomic temp+rename). Ensure the proxy is attached to the app networks at deploy time, before containers start, and reconcile networks in the health poll.
  - Hangfire job for restarts.
  - Tests: compose golden test, network attach on deploy, remote-target file delivery.
- **Roadmap fit:** Phase 10 gap (10.05 follow-up); 16.06 lock validation.
- **Effort:** L

#### NET-03: Domain identity, validation and conflicts
- **Upstream:**
  - v4.2.0: #10663 accepts underscores in HTTP(S) hostnames; #10836 preserves case-sensitive paths.
  - v4.3.14: single-label hostnames rejected, IP addresses accepted.
  - v4.3.18: #11683 wildcard domains; the service API applies the same rules as the UI, including oversized URLs.
  - v4.3.19: Compose domain conflicts detected across services, and HTTP and HTTPS forms of a domain are treated as the same routing identity.
  - v4.5.0: #12032 `force_domain_override`; `noindex` on domains with ports.
  - v4.4.3: IPv6 server addresses in URLs and routing; Traefik matches IPv6 hosts without brackets.
  - v4.7.x migration `2026_11_15_150000_rewrite_websocket_schemes_of_domains` (ws/wss rewritten to http/https).
  - Files: `bootstrap/helpers/domains.php`, `app/Support/DomainUrlParts.php`, `fqdnLabelsForTraefik()` (`$traefikHost = trim($host,'[]')`).
- **Coolify behavior (now):**
  - A domain is a URL: scheme http/https (ws/wss are normalized), a host (DNS name with `_` allowed, wildcard `*.`, IPv4/IPv6), and an optional path routed as `PathPrefix` with its case preserved.
  - Single-label names are rejected.
  - Domain usage is checked across all resources at save time. http and https forms of a host are the same identity, and `force_domain_override` lets the user override the conflict explicitly.
- **Vessel today:** Partial / Drifted.
  - `DomainName.cs:25` regex `^(?!-)([a-z0-9-]{1,63}\.)+[a-z]{2,63}$` rejects underscores, IPs, wildcards and IDN TLDs such as `xn--p1ai`. It already rejects single-label names (matches upstream).
  - `DomainRoutingService.cs:114-115` rejects paths and ports.
  - Uniqueness is only the composite key `(ApplicationId, DomainName)` (`VesselDbContext.cs:519`). Two apps can claim the same host. On one server this surfaces later as a server-wide apply failure (`TraefikProxyProvider.cs:116-122`); across servers it is silently allowed.
- **Gap type:** Behavior change / Schema
- **Priority:** P1 — users expect conflict detection and these host forms; today a conflict breaks the whole server late instead of at save time.
- **Recommended Vessel approach:**
  - Domain value object `RouteAddress` (Scheme, Host kind DNS/Wildcard/IPv4/IPv6, Path). Keep `DomainName` for DNS names, relaxed for `_`, and add punycode support.
  - Application `DomainConflictChecker` querying apps, services and previews, with an explicit override flag that is audited.
  - Migration: a normalized host column plus an index for lookups.
  - Unit tests for the edge forms listed above.
- **Roadmap fit:** Phase 10 (10.07) and 15.05.
- **Effort:** M

#### NET-04: TLS certificate truth from Traefik `acme.json` (inventory, delete/unused, backup/restore)
- **Upstream:** v4.4.0 #12015; v4.7.0 #12447 ("Not used" label and "Delete unused") · `app/Services/TraefikAcmeService.php`, `app/Actions/Proxy/{GetTraefikCertificates,DeleteTraefikCertificate,SaveTraefikAcmeFile,ListTraefikAcmeBackups,RestoreTraefikAcmeBackup,DeleteTraefikAcmeBackup}.php`, `app/Livewire/Server/Proxy/Certificates*.php`
- **Coolify behavior (now):**
  - The server Proxy menu has a TLS Certificates page that parses `acme.json` per resolver: main domain, SANs, expiry.
  - A certificate is "unused" when no route or resource hostname on the server matches it (wildcards included). "Delete unused" removes those in one step.
  - Deleting a certificate does not revoke it. Traefik keeps renewing every certificate in `acme.json` until it is deleted and the proxy restarted.
  - Every write backs up `acme.json` first. Writes go through a 0600 temp file uploaded via scp and an atomic `mv`. The last 10 backups are kept and can be restored or deleted.
- **Vessel today:** Drifted.
  - `Certificate` rows are created as Pending when a domain is saved (`DomainRoutingService.cs:54-58`), but `CertificateManagementService.FinalizeIssuanceAsync` (`:135-148`) has no caller. Status and expiry therefore never reflect reality.
  - `RenewDueAsync` (`:104-133`) only flips DB state. Traefik does the actual renewal.
  - There is no `acme.json` access, backup or restore.
  - Removing a domain leaves its certificate row behind.
- **Gap type:** New feature / Behavior change
- **Priority:** P1 — certificate status shown to users is fictional, and losing `acme.json` risks Let's Encrypt rate limits with no recovery path.
- **Recommended Vessel approach:**
  - Infrastructure: `ITraefikAcmeStore` (read, write-with-backup, list/restore/delete backups) over the server file channel, behind the `proxy:{serverId}` lock.
  - Application: a sync job (Hangfire, in the health poll) that reconciles `Certificate` rows from `acme.json` (issued/expiry/SANs/in-use), plus delete-unused, delete and restore commands.
  - Drop the fake renewal state machine for Traefik-managed certificates, or turn it into an "expiring soon" alert.
  - Never persist key material in the DB. Redact in logs.
- **Roadmap fit:** Phase 10 (10.08–10.10 rework); new task, Phase 10 follow-up.
- **Effort:** M

#### NET-05: Per-route HTTP features: force HTTPS, permanent redirects, www modes, compression, basic auth, noindex
- **Upstream:**
  - v4.3.0 #10908 noindex, and the `www` redirect fix.
  - v4.3.7: configurable HTTPS redirects for service apps (`17d9d1a37`), redirect capture groups.
  - v4.3.1 #11196: basic-auth API changes regenerate labels.
  - v4.5.0: #12086 basic-auth hash options; #12233 permanent redirects (opt-in); #12229 SSE excluded from compression.
  - Migrations: `2026_07_12_090000_add_noindex_domains…`, `2026_07_29_100747_add_redirect_to_service_applications_table`, `2026_10_02_170101_add_http_basic_auth_hash_options…`, `2026_10_19_191912_add_is_permanent_redirect_enabled…`, `2026_10_19_210000_change_http_basic_auth_password_to_text…`.
  - Files: `app/Traits/HasNoindexDomains.php`, `app/Enums/{HttpBasicAuthHashAlgorithm,RedirectTypes}.php`, `bootstrap/helpers/constants.php` (`TRAEFIK_GZIP_MIDDLEWARE='gzip-no-media'`, excluded types).
- **Coolify behavior (now):**
  - **Force HTTPS** (apps and service apps): the http router carries `redirect-to-https`. It becomes `redirect-to-https-permanent` only when `is_permanent_redirect_enabled` is set; the default is false, so redirects are temporary.
  - **www handling:** `redirect` is www / non-www / both (default `both`) on apps and service apps.
  - **Compression:** gzip middleware `gzip-no-media` excludes `text/event-stream`, video/audio, images, archives and PDF.
  - **Basic auth:** algorithm bcrypt (cost default 10) or argon2id (memory 65536, time 4). argon2id is used for Caddy; Traefik uses bcrypt. The password column is text and is hidden from members without update permission.
  - **noindex:** `noindex_domains` (JSON) adds `X-Robots-Tag: noindex, nofollow` per domain, including domains with ports.
- **Vessel today:** Missing or drifted.
  - The only feature is canonical redirect, hard-coded `permanent: true` (`TraefikProxyProvider.cs:83`). That contradicts upstream's temporary-by-default semantics: browsers cache 301 responses indefinitely.
  - No grep hits in `src` for force-HTTPS, gzip/compress, basic auth or noindex.
- **Gap type:** New feature / Behavior change / Schema
- **Priority:** P1 — HTTP→HTTPS and basic auth are core parity, and the permanent-redirect default should change now, before users accumulate cached 301s.
- **Recommended Vessel approach:**
  - Domain: a `RoutePolicy` value object per domain or resource: `ForceHttps`, `RedirectMode` (www/non-www/both/canonical), `PermanentRedirect=false`, `Compression`, `NoIndex`, `BasicAuth` (username plus a secret reference, algorithm, cost).
  - Hash in Infrastructure at render time from `ISecretVault`, never stored in clear.
  - Traefik middlewares named with a version suffix, so definition changes don't conflict.
  - Migration for the new columns; golden tests for each middleware; cross-ref Security on hiding the basic-auth secret.
- **Roadmap fit:** Phase 10 (10.07/10.12 extension)
- **Effort:** M

#### NET-06: Routing coverage for service-template resources and preview deployments
- **Upstream:** v4.3.15 (preview domain management and DNS status, #11594 per-domain internal port overrides for apps, Compose services and previews); v4.3.16 / v4.3.18 (Compose routing uses each service's port); v4.3.22 #11812 (separate Traefik rules for comma-separated preview domains); v4.5.1 #6536 (custom preview domains kept on redeploy) · `app/Support/DomainPortOverrides.php`, migrations `2026_09_0{1,2}_*_add_domain_port_overrides_*`, `app/Livewire/Project/Application/PreviewDomains.php`, `app/Livewire/Project/Service/Domains.php`
- **Coolify behavior (now):**
  - Applications, Compose services, service-template sub-applications and previews all have domains.
  - `domain_port_overrides` maps a portless URL to an internal port. Public URLs stay portless, and the UI warns about unrecognized ports.
  - Previews get generated or custom domains, plus DNS statuses.
- **Vessel today:** Partial.
  - Per-domain internal port exists for applications (`ApplicationDomain.TargetPort`, `ApplicationDomain.cs:26`). That part is covered for apps.
  - `RoutesForServer` only reads Applications (`ProxyConfigurationService.cs:290-316`). `ServiceResource` has no domains (`src/Vessel.Domain/Services/ServiceResource.cs`).
  - `ApplicationPreview.SetPreviewUrl` (`ApplicationPreview.cs:94`) is never called, so previews are never routed.
  - Compose apps route only to the app's single exposed port.
- **Gap type:** New feature
- **Priority:** P1 — previews and services without routing are a visible hole in features Vessel already ships.
- **Recommended Vessel approach:**
  - Generalize to a `RoutableEndpoint` (owner kind App/Service/Preview, target service and port) that every resource type projects into `ProxyRoute`.
  - A preview host template on the app (`{pr}.{domain}`) and a per-Compose-service port map.
  - Cross-ref the Deployments/Previews and Services agents.
- **Roadmap fit:** Phase 9 (9.12) and Phase 11 (11.05) follow-ups; Phase 10
- **Effort:** L

#### NET-07: Proxy configuration history semantics and custom dynamic configs
- **Upstream:**
  - Unreleased branch `origin/feat/proxy-config-history` (commit `87af6bf17`, 2026-11-13): each changed configuration is saved as an encrypted version (newest 20 per server, the applied version always kept), compared with a diff, and restored as a new current version.
  - Released since earlier: 10 file backups of the proxy compose (`SaveProxyConfiguration.php` `MAX_BACKUPS=10`); user-editable dynamic configuration files (`app/Livewire/Server/Proxy/DynamicConfigurations.php`, `app/Rules/ValidProxyConfigFilename.php`) with YAML highlighting (v4.5.0).
  - v4.4.0: saved proxy config is returned in the API only with `read:sensitive`.
- **Coolify behavior (now):** Full configuration text is treated as sensitive: encrypted at rest and gated by permission. History is bounded. A restore never rewrites history; it creates a new version. Users can add their own dynamic configs, which are validated by file name and YAML.
- **Vessel today:** Partial.
  - `ProxyConfigurationVersion` stores the full config in plaintext with no retention (`src/Vessel.Domain/Proxy/ProxyConfigurationVersion.cs`).
  - Rollback marks the old version rolled back instead of creating a new current version (`ProxyConfigurationService.cs:278-283`).
  - There is no diff and no user dynamic configs.
  - The list API omits the content, which is good (`ProxyModels.cs:35-46`).
- **Gap type:** Behavior change / Security hardening
- **Priority:** P1 — adopt the restore-as-new-version and retention semantics now, while the model is young. Encryption is cross-ref Security.
- **Recommended Vessel approach:**
  - Keep `ProxyConfigurationVersion` and add a `Source` field (Generated/UserDynamic/Restore).
  - Retention: keep the newest N plus the applied version, enforced by a Hangfire cleanup job.
  - Encrypt the `Configuration` column through `ISecretVault`.
  - A restore creates a new version linked through `RestoredFromId`.
  - A `UserDynamicConfig` entity whose content is validated (YAML parse, file-name rule).
- **Roadmap fit:** Phase 10 (10.06)
- **Effort:** M

#### NET-08: Domain DNS status checks, public IPs, resolver settings
- **Upstream:**
  - v4.3.0 #11119 (DNS checks in the redesigned UI).
  - v4.3.8 #11378 (5-second query timeouts); v4.3.11 (asynchronous checks); v4.3.19 (checks only after hostname or scheme changes).
  - v4.3.22 #11811 (system resolver fallback); v4.4.3 #12076 (TCP retry for truncated UDP).
  - v4.4.0 #11951 (a re-check replaces the stored result; IPv6 compared by value); v4.5.0 (Detect button for public IPs; sslip.io resolves); v4.5.1 #12275 (keep the detected IP when the other family's lookup times out).
  - v4.7.0 #12436 (Cloudflare-proxied record that points to another server shows `DNS mismatch`; CNAME accepted; closest wildcard record checked).
  - Migrations `2026_07_24_*_add_domain_dns_statuses_*`, `2026_08_28_193100_…previews`, `2026_11_16_120000_add_public_ips_to_server_settings_table`.
  - Files: `app/Actions/Shared/CheckDomainDns.php`, `app/Jobs/CheckDomainDnsJob.php`, `app/Services/Dns/{DnsResolver,ServerDnsAddresses}.php`, `app/Support/DnsRecordHints.php`.
- **Coolify behavior (now):**
  - `domain_dns_statuses` is JSON per resource, keyed per domain: `{status: checking|ok|failed|skipped, message, expected_ip, checked_at, check_id}`.
  - The queued job runs once with a 30-second timeout. It writes its result only if `check_id` still matches, under a row lock.
  - The query type is A or AAAA, chosen by the family of the expected IP.
  - Resolution order: the instance's `custom_dns_servers`, each queried with a 5-second budget; then the system resolver if no address record came back.
  - A Cloudflare IP counts as a match unless the team's Cloudflare zone sends the host to another server.
  - The check is skipped when instance DNS validation is off, and for multi-server apps.
  - Expected IP: the server's public IPv4/IPv6 (`server_settings.public_ipv4` / `public_ipv6`, or instance settings for localhost), else the server IP of that family.
- **Vessel today:** Missing. No DNS resolver, status, public-IP or custom-DNS settings (grep for `Dns`/`PublicIp` in `src` finds nothing outside migrations). `Server` has only `Address` (`Server.cs:38`).
- **Gap type:** New feature / Schema
- **Priority:** P2 — a high-value UX safeguard (it catches the main cause of failed ACME), but routing works without it.
- **Recommended Vessel approach:**
  - Application: `IDnsResolver` (Infrastructure uses DnsClient.NET or `System.Net.Dns` plus custom resolvers with a timeout and TCP fallback); `DomainDnsCheckService` with a Hangfire job; a check-id race guard; a status record per route (an owned table rather than JSON).
  - Server: `PublicIpv4` / `PublicIpv6`, plus a detection command.
  - Settings: a DNS validation toggle and custom resolvers.
  - Realtime update through SignalR.
  - Validate resolver addresses (SSRF / internal-resolver policy, cross-ref Security #10835).
- **Roadmap fit:** New: Phase 10 follow-up (could sit in Phase 12/monitoring)
- **Effort:** M

#### NET-09: Managed Cloudflare DNS records (integration tokens, zones, ownership, release)
- **Upstream:**
  - v4.3.0 #11119 (automatic Cloudflare DNS setup in the UI).
  - v4.4.0 #11359 (integration tokens), #11699 (DNS record management); v4.4.3 (optional proxied mode); v4.5.1 (Cloudflare API retries for idempotent requests).
  - v4.6.0 (DNS API and MCP, auto-create on `PATCH /applications|services/{uuid}`); v4.7.0 (clones get records; editing a domain auto-adds a record or opens Configure DNS).
  - Migrations `2026_08_15_000000_create_integration_tokens_table`, `2026_08_24_00000{0,1}_*`, `2026_09_25_15412{1,2}_*`.
  - Files: `app/Services/Dns/{CloudflareDnsProvider,ManagedDnsHostnameLock,ManagedDnsRecordCleanup,ResourceDnsRecords}.php`, `app/Jobs/{ConfigureDnsRecordJob,ReleaseManagedDnsRecordsJob,ReleaseRemovedDnsHostnamesJob}.php`, `app/Traits/ReleasesManagedDnsRecords.php`, `app/Console/Commands/ReleaseOrphanedManagedDnsRecords.php`, `app/Services/CloudflareTokenValidator.php`, `app/Http/Controllers/Api/DnsController.php`, routes `GET /dns/zones`, `GET|POST /applications/{uuid}/dns`, `POST …/dns/replace`, `DELETE …/dns/{hostname}` (same set for services), `/security/integration-tokens[/{uuid}[/sync-zones]]`.
- **Coolify behavior (now):**
  - **Tokens and zones:** team `integration_tokens` (provider, name, encrypted token, capabilities `dns` / `tunnel`; `tunnel` requires `dns`). The token is validated against Cloudflare. `dns_provider_zones` are synced per token.
  - **Creating a record:** a hostname is matched to a reachable zone, and an A, AAAA or CNAME record is created with TTL auto, optional `proxied`, and comment `managed-by: coolify {instanceId}/{recordUuid}`.
  - **Ownership:** a `managed_dns_records` row carries `owned`. A record without a matching ownership comment is never modified or deleted.
  - **References:** `managed_dns_record_references` lets several resources share one record.
  - **Conflicts:** an existing record with another value raises a conflict. It is replaced automatically only if Coolify owns it and nothing else uses it; otherwise the user must confirm the replace.
  - **Locking:** every create, replace or release runs under the lock `managed-dns:{team}:{host}` (TTL 60 s, wait 5 s). A busy lock is retried three times with backoff.
  - **Release:** records are released after a resource is deleted (queued after commit) and after a domain edit removes a hostname. An artisan command releases orphans.
  - A non-public IP blocks record creation.
- **Vessel today:** Missing (no DNS provider, integration tokens or Cloudflare code; the only Cloudflare reference is the cloudflared template at `ServiceTemplateCatalog.cs:1089-1098`).
- **Gap type:** New feature / Schema / API contract
- **Priority:** P2 — strong differentiator and now central to the Coolify domain UX, but not needed for correctness.
- **Recommended Vessel approach:**
  - Application: `IDnsProvider` (zones, find/create/replace/delete records) with a Cloudflare implementation in Infrastructure (typed HttpClient, retry for idempotent calls only).
  - Domain: `IntegrationToken` (secret reference, capabilities), `DnsZone`, `ManagedDnsRecord` (ownership marker, owned flag) with a `ManagedDnsRecordReference` join.
  - Reuse `IDistributedLockManager` with the key `dns:{team}:{host}`.
  - Hangfire jobs for configure and release; an EF `SaveChanges` interceptor or domain event triggers release after commit; a recurring orphan sweep.
  - Thin controllers with the upstream route shapes.
  - Cross-ref the Security/Integrations agent for the shared integration-token model (also used by secret managers).
- **Roadmap fit:** New phase (Edge/DNS integrations) or a Phase 10 extension
- **Effort:** L

#### NET-10: Maintenance mode and custom proxy error pages
- **Upstream:** v4.5.0 #12112; v4.7.0 (maintenance gets its own page with toggle confirmation; #12498 honors custom labels and wildcard domains) · migrations `2026_10_01_132654_add_proxy_error_page_to_servers_table`, `2026_10_01_161644_add_maintenance_mode_to_applications_and_services_tables` · `app/Traits/HasMaintenancePage.php`, `app/Models/Server.php` (`PROXY_ERROR_PAGE_CONTAINER='coolify-proxy-error-pages'`, `busybox:musl`, 1 MiB max page, `setupMaintenancePages()`, `maintenanceTraefikConfiguration()`), `app/Livewire/Project/Shared/Maintenance.php`
- **Coolify behavior (now):**
  - `servers.proxy_error_page` (text) replaces the catch-all 503 page.
  - Applications and services have `is_maintenance_enabled` and `maintenance_page`. While maintenance is on, the containers keep running, and the proxy serves the page with status 503 from a busybox httpd container on the proxy network.
  - Traefik gets `coolify-maintenance.yaml`: one router per domain or label rule, `coolify-maintenance-headers`, and a replace-path to `maintenance-{uuid}.html`.
  - Hosts and paths are sanitized before they enter the config. Swarm is not supported.
- **Vessel today:** Missing (no grep hits).
- **Gap type:** New feature / Schema
- **Priority:** P2 — a valued operational feature; it depends on NET-02.
- **Recommended Vessel approach:**
  - Domain: `MaintenanceMode` on the resource; `ProxyErrorPage` on the server proxy settings, with a size limit.
  - Infrastructure: render a separate dynamic file at higher router priority, plus a static-page container (or Traefik `errors` middleware) delivered through NET-02's file channel.
  - An audited toggle.
- **Roadmap fit:** New: Phase 10 follow-up
- **Effort:** M

#### NET-11: Traefik certificate resolver per server, with per-resource override
- **Upstream:** v4.5.0 · migrations `2026_10_11_075120_add_traefik_certificate_resolver_to_server_settings_table` (default `letsencrypt`; set to null when the server's proxy config has no letsencrypt resolver), `2026_10_11_145529_…application_and_service_settings` · `Server::traefikCertificateResolver()`, `ServerSetting::TRAEFIK_CERTIFICATE_RESOLVER_PATTERN='/^[A-Za-z0-9_-]+$/'`, sentinel value `none`
- **Coolify behavior (now):** The resolver is the resource override if set, otherwise the server setting. `none` means no `certresolver` label, for users who bring their own certificates, wildcard DNS-01, or Cloudflare origin certificates. Exposed in `PATCH /servers/{uuid}/proxy`.
- **Vessel today:** Drifted — `letsencrypt` is hard-coded (`TraefikProxyProvider.cs:37`), and there is no wildcard or DNS-01 path.
- **Gap type:** New feature / Schema
- **Priority:** P2 — needed for wildcard certificates and Cloudflare-proxied setups.
- **Recommended Vessel approach:** A resolver name (validated by regex) on the server proxy settings and an optional override on the route policy, with `None` as an explicit enum value. The NET-02 static config can declare additional resolvers (DNS-01 with a provider credential taken from a secret reference).
- **Roadmap fit:** Phase 10 (10.09)
- **Effort:** S

#### NET-12: Traefik version pinning, outdated tracking and security-patch cadence
- **Upstream:** v4.3.0 #11029 (Traefik 3.7 support and security patch versions); v4.3.11 #11496 (stale branch warnings cleared); v4.3.17 (stale version data cleared); v4.3.20 (checks run on every update check); v4.3.21 / v4.5.0 / v4.5.x (patch pins 3.7.13 → 3.7.16, 2.11.60); v4.6.0 (pins moved to `upstream.json`); v4.7.0 #9727 (Traefik Outdated notification for patch versions) · `app/Jobs/CheckTraefikVersion*Job.php`, `app/Notifications/Server/TraefikVersionOutdated.php`
- **Coolify behavior (now):** Coolify records the running Traefik version per server, compares it with the supported patch of the same branch and with newer branches, raises warnings in the server status, and sends a notification.
- **Vessel today:** Missing (no proxy image at all, see NET-02).
- **Gap type:** Security hardening
- **Priority:** P2 — Traefik CVEs are frequent, and the pin has to exist before Vessel deploys proxies.
- **Recommended Vessel approach:** Keep a supported-version manifest shipped with Vessel (in Shared or config); a recurring Hangfire job inspects the proxy image per server; a notification event goes through the Phase 13 dispatcher.
- **Roadmap fit:** Phase 10 / Phase 13; 15.14
- **Effort:** S

#### NET-13: Server proxy API contract
- **Upstream:** v4.4.0+ `app/Http/Controllers/Api/ServerProxyController.php`: `GET /servers/{uuid}/proxy` (proxy_type, status, redirect_enabled/redirect_url catch-all, proxy_error_page, generate_exact_labels, traefik_certificate_resolver; saved config only with `read:sensitive`), `PATCH /servers/{uuid}/proxy`, `PUT /servers/{uuid}/proxy/configuration` (base64; validated), `POST /servers/{uuid}/proxy/restart` (ability `deploy`, v4.5.0); `GET /servers/{uuid}/domains` (v4.4.0 filter fix).
- **Coolify behavior (now):** Proxy settings are a first-class server sub-resource. Every state change is a POST, PATCH or PUT and is audited with the changed fields.
- **Vessel today:** Partial — only `GET/POST apply/POST rollback` under `api/v1/servers/{id}/proxy-configurations` (`ProxyConfigurationsController.cs:12-38`), gated by `servers.write`.
- **Gap type:** API contract
- **Priority:** P2 — falls out of NET-02, NET-07, NET-10 and NET-11.
- **Recommended Vessel approach:** Add a `ServerProxyController` (`/api/v1/servers/{id}/proxy`) with GET/PATCH, `PUT configuration`, `POST restart`, and certificates/backups sub-routes (NET-04). Add a deploy-level permission for restart (cross-ref the Security/API agent on token abilities).
- **Roadmap fit:** Phase 6 API surface plus Phase 10
- **Effort:** S

#### NET-14: Default/wildcard generated domains (sslip.io) and catch-all redirect
- **Upstream:** Existed before v4.1.0; v4.5.0 changed it (sslip.io domains resolve the server hostname to an IP); v4.3.19 (hostname regeneration controls in the domain editors); v4.3.2 (stopped suggesting unconfigured `www` counterparts). Server `redirect_url` / `redirect_enabled` catch-all (ServerProxyController).
- **Coolify behavior (now):** A server wildcard domain, or sslip.io, generates a default domain per resource, and the hostname can be regenerated. Unknown hosts get the catch-all redirect or the custom error page.
- **Vessel today:** Missing — domains are manual only (`DomainRoutingService.ConfigureAsync`), and there is no catch-all router.
- **Gap type:** New feature
- **Priority:** P2 — onboarding convenience and a sane default for unknown hosts.
- **Recommended Vessel approach:** A wildcard-domain setting on the server; a generator in Application (`{slug}-{shortId}.{wildcard}`, or `{ip}.sslip.io`); a low-priority catch-all router in the NET-02 static/dynamic config.
- **Roadmap fit:** Phase 10 / Phase 7 (7.08)
- **Effort:** S

#### NET-15: Cloudflare Tunnel for server connectivity (automated setup, updates)
- **Upstream:** v4.3.0 #11145, v4.4.7 (cloudflared 2026.10.3, eMudhra certificate compromise), v4.7.x (2026.11.1); v4.4.1 (updates from server settings with progress logs); v4.4.2 (startup timeout; config stored outside temp directories); v4.5.0 #12157 (tunnel setup with stored API tokens); v4.5.1 #12312 (Access service token for terminal SSH); v4.3.18 #11638 (PATH preserved for the SSH ProxyCommand) · `app/Services/CloudflareTunnelService.php`, `app/Actions/Server/{ConfigureCloudflared,UpdateCloudflared}.php`, `app/Http/Controllers/Api/ServerCloudflareTunnelController.php` (`GET/PATCH /servers/{uuid}/cloudflare-tunnel`, `POST …/enable|disable`), migration `2026_10_07_125311_add_cloudflare_tunnel_api_fields_to_server_settings`
- **Coolify behavior (now):**
  - Using a token with the `tunnel` capability, Coolify creates or reuses a remotely managed tunnel named after the server and routes a hostname to the server's SSH port.
  - It creates a proxied CNAME (a managed DNS record), then starts cloudflared. SSH runs through `cloudflared access` as the ProxyCommand.
  - The tunnel token is never stored.
  - Teardown stops cloudflared, deletes the tunnel once its connector is gone, and releases the DNS records.
- **Vessel today:** Missing — `ServerConnectionType` is Local/Ssh/DockerSocket only (`src/Vessel.Domain/Servers/ServerConnectionType.cs`). There is a cloudflared service template for app ingress (`ServiceTemplateCatalog.cs:1089-1098`), which is a different use case. That template uses `cloudflare/cloudflared:latest` unpinned, which conflicts with upstream's security-pinning practice.
- **Gap type:** New feature
- **Priority:** P3 — niche (servers behind NAT); it depends on NET-09 tokens. Pinning the template image is a quick win.
- **Recommended Vessel approach:** An SSH transport option `ProxyCommand=cloudflared access ssh` in the Infrastructure SSH layer through `IProcessRunner`; a `CloudflareTunnelService` in Application over `IDnsProvider` and a Cloudflare API client; a pinned cloudflared version in the version manifest. Cross-ref the Servers/SSH agent.
- **Roadmap fit:** New (Servers/connectivity)
- **Effort:** L

#### NET-16: Edge traffic analytics (proxy access logs through Sentinel)
- **Upstream:** v4.4.0 #11292 (Traefik and Caddy, Rust Sentinel); v4.4.1 `traffic_ip_mode` full/anonymized/off; v4.4.2 #12117 (Caddy); v4.5.0 #12158 (city level); v4.5.1 #12335 (container tooling user agents hidden) · migrations `2026_08_10_191228_add_traffic_analytics_to_server_settings` (toggle off by default, topN 50, retention 30 d at 1 h / 395 d at 1 d, GeoIP with optional MaxMind key), `2026_10_07_092441_add_traffic_ip_mode…` · `app/Actions/Server/ConfigureTrafficAnalytics.php`, `app/Services/{SentinelTrafficClient,TrafficAnalyticsAggregator,TrafficResource}.php`, `app/Enums/TrafficIpMode.php`
- **Coolify behavior (now):**
  - Turning analytics on per server rewrites the proxy config to emit a JSON access log, then restarts the proxy and Sentinel.
  - Sentinel stores aggregate rollups only, and raw logs are rotated.
  - Coolify queries Sentinel for requests, bandwidth, unique visitors, p95 latency, status codes, country/city, device, path, host, referrer and browser, per server or per resource. Resources are keyed by router name (`https-{i}-{key}@docker`).
- **Vessel today:** Missing (no Sentinel or access-log pipeline; grep finds nothing).
- **Gap type:** New feature
- **Priority:** P3 — attractive but large, and it depends on a metrics agent. It belongs with the Monitoring domain; the edge part is the proxy access-log config plus stable router names (NET-01).
- **Recommended Vessel approach:** Defer. When it is picked up, make the NET-02 static config able to emit a JSON access log, and keep router names parseable back to the resource ID. Aggregate in a host agent or a Hangfire ingestion into PostgreSQL rollups; IP mode must be privacy-by-default (anonymized). Cross-ref the Monitoring/Sentinel agent.
- **Roadmap fit:** New phase (Monitoring v2)
- **Effort:** XL

#### NET-17: Caddy (and NONE) as alternative proxy providers
- **Upstream:** Caddy remains first-class in Coolify:
  - v4.3.10 #11397 (Caddy labels restored); v4.4.0 (caddy-docker-proxy 2.13; Caddy analytics with 2.9+).
  - v4.5.0: `basic_auth` directive with Caddy ≥ 2.8, argon2id.
  - v4.7.0 #7942: `stream_close_delay` so Caddy reloads don't drop WebSockets.
  - Maintenance blocks in the base Caddyfile.
  - `ProxyTypes` NONE/TRAEFIK/CADDY (NGINX is listed but not supported for analytics).
- **Coolify behavior (now):** Every routing feature (port overrides, noindex, redirects, basic auth, maintenance, analytics) is implemented for both Traefik and Caddy. NONE leaves routing to the user.
- **Vessel today:** Missing — `ProxyProviderKind.Caddy` exists only as an enum value (`src/Vessel.Domain/Proxy/ProxyProviderKind.cs:6`). Validation hard-requires Traefik (`TraefikProxyProvider.cs:96-97`). There is no NONE mode.
- **Gap type:** New feature
- **Priority:** P3 for Caddy (Traefik covers the MVP and keeping one provider halves the test matrix). NONE mode belongs in NET-02 (P0).
- **Recommended Vessel approach:** Keep `IProxyProvider`, but move the feature model (`RoutePolicy`, maintenance, resolver) into provider-neutral Application types so a Caddy provider (Caddyfile or JSON via the admin API, `stream_close_delay`) can be added later without schema changes.
- **Roadmap fit:** Phase 10 (10.02 follow-up) / post-beta
- **Effort:** XL

### Reviewed and not applicable
- **Domain Connect** (`app/Support/DomainConnect/CloudflareDomainConnect.php`, `instance_settings.domain_connect_private_key`): documented as a Coolify Cloud feature that needs a Cloudflare-issued provider signing key. Not usable by self-hosted Vessel.
- **Dashboard-exposure items:**
  - v4.3.8: instance setting for forced HTTPS on the dashboard, and dashboard compression.
  - v4.5.1 #11707 and v4.6.0: Reverb WebSocket routing on the dashboard port and only under `/app/`; v4.4.0: Soketi removed.
  - v4.7.0: nginx header buffers for large cookies (#11404).

  These concern Coolify's own containerized dashboard behind its own proxy. Vessel is host-native Kestrel. Cross-ref the Platform/Install agent for TLS termination and forwarded headers.
- **Real client IP behind proxies/Cloudflare** (v4.3.12 #11515 rate limits; v4.5.1 #12301 allow-lists/audit) and **webhook DNS rebinding protection** (v4.2.0 #10835): Security agent. Note that Vessel has no `ForwardedHeaders` configuration anywhere in `src`.
- **Hiding basic-auth passwords, proxy config and `read:sensitive`** (v4.4.0 #11961) and **API `deploy` ability for proxy changes** (v4.5.0): Security/API agent. Only the data-shape impact is noted in NET-05, NET-07 and NET-13.
- **Rolling-update proxy retries and drain time** (v4.5.1 #8627, retry labels removed again in v4.5.2): the retry labels were reverted. Drain time is a Deployments concern; cross-ref.
- **Branch `fix/configurable-proxy-timeout`:** about the database TCP proxy (nginx) timeout and its normalization, not the HTTP edge. Belongs to the Databases agent.
- **`DomainFavicon`** (v4.4.2 #12107: no favicon fetch for private hosts): UI-only nicety. If Vessel's Blazor UI ever loads favicons for domains, apply the same private-address rule.
- **Applications on multiple servers requiring the same proxy type** (v4.4.0 #12053): Vessel has no multi-server applications. Revisit if it adds them.
- **MCP tools** (`ManageDns`, `ManageProxy`, `ManageCloudflareTunnel`, `GetServerDomains`): Coolify's MCP server. Vessel's equivalent would sit on the same Application services; out of scope here.
- **`SeedDevelopmentTraefikCertificates` command, release/CDN pinning mechanics (`upstream.json`), Livewire key and refresh fixes, domain-table layout fixes:** PHP, Livewire or Coolify-release tooling only.

## 12. DATA — Databases, Backups, Volumes and Storage

### Summary

Between v4.1.0 and v4.7.6, upstream changed this domain more than almost any other. It added **scheduled volume/directory backups** (v4.2.0 and later), **multiple backup destinations with per-destination replicas** (v4.5.0) and **SFTP/FTPS/WebDAV storage types** (v4.7.0). It added a **database import API** with a rewritten, format-detecting, transactional restore pipeline (v4.4.0 to v4.5.2). It also added **SQLite** (v4.4.0), **Valkey as the Redis default** with new pinned default images (v4.7.0), real per-database **health checks** (v4.1.2, later fixed), **missing-backup alerts** and throttled backup notifications, a **file browser** (v4.5.0), **backup of the Coolify DB before update** (v4.5.0) and a large set of reliability fixes for backups: timeouts, lost and stale runs, exclusivity with start/restart/import, and S3 edge cases.

Vessel's Phase 11 slice is thin and has real correctness defects. Every backup is read fully into memory through `RunBinaryAsync`, which has a **10 MiB output cap**, so any backup above 10 MiB fails. Restore runs synchronously inside the HTTP request with a 2-minute timeout and pipes untyped SQL or RDB into a superuser client. Health is never probed. Backup and lifecycle locks do not exclude each other. Backup notifications are filter-wired but never raised. Only one instance-wide object store exists, configured in appsettings. There is **no application/service persistent-storage model** at all, so volume backups, file mounts and the file browser have no foundation.

**Engines Vessel supports today:** provisioning, backup and restore work for **PostgreSQL, MySQL, MariaDB and Redis** only (`ManagedDatabaseService.cs:415-423`, `ProcessDatabaseBackupProvider.cs:67-84`). The enum also lists MongoDB, KeyDB, Dragonfly and ClickHouse (`DatabaseEngine.cs:3-13`), but starting any of them throws. Upstream now supports 9 engines: those 8 plus SQLite.

**Counts:** P0 = 2, P1 = 12, P2 = 6, P3 = 2 (22 items).

### Gap items

#### DATA-01: Backups are buffered in memory, capped at 10 MiB, uncompressed and short-timed out
- **Upstream:** v4.3.3 (CPU limit for compression), v4.3.7 (parallel gzip), v4.3.9 #11358 (10 h volume timeout), v4.3.18 #11642 (stream S3-only volume archives), v4.4.0 (timeouts fail instead of staying open), v4.7.0 (per-step remaining deadline, exit-code-124 explanation, >1000-part uploads, presigned curl) · `app/Support/BackupCompression.php`, `app/Jobs/DatabaseBackupJob.php` (`commandTimeout()`, `buildCompressedDumpCommand()`, `exitCodeReason()`), `server_settings.backup_compression_cpu_percentage` (migration `2026_08_14_074053`), `scheduled_volume_backups.timeout` default 36000 (`2026_08_15_000000`)
- **Coolify behavior (now):** Dumps are written to a file on the server through `pigz -3 -p N`, where N comes from the server's `backup_compression_cpu_percentage` (allowed values 25/50/75/100, default 25), falling back to `gzip -3`. They are then uploaded from that file. With S3-only volume backups, the archive is streamed straight to S3. Each schedule has a `timeout` (DB default 3600 s, volume default 36000 s). The timeout covers the whole run, and each step gets only the time that remains. When it runs out, the run fails with an explained exit code 124 and cleans up the helper container and dump file. Large uploads are sized to stay at or under 1000 multipart parts.
- **Vessel today:** **Drifted / broken.** `ProcessDatabaseBackupProvider.cs:19-25` runs `docker exec … pg_dump/mysqldump/redis-cli --rdb -` with `OutputMode.Binary` and a 10-minute timeout, then copies the result into a `MemoryStream` (`:28-30`). `ProcessCommand.MaxOutputBytes` defaults to `10 * 1024 * 1024` (`Vessel.Application/Processes/ProcessModels.cs:26`). `DotNetProcessRunner.cs:91-93` reads all bytes and only then enforces that limit, so any dump over 10 MiB is fully buffered and then rejected. This contradicts PROJECT_REQUIREMENTS §10.5 ("Use `File` for … backups … anything that may exceed safe in-memory limits"). There is no compression. The S3 client timeout is 5 s (`ObjectStorageOptions.cs` `TimeoutSeconds = 5`, applied at `S3ObjectStorage.cs:393`). Restore has a hard 2-minute timeout (`ProcessDatabaseBackupProvider.cs:57`). The backup provider calls the `docker` binary directly (`:20`), which ignores Podman targets and SSH servers.
- **Gap type:** Behavior change (correctness)
- **Priority:** P0: backups of any realistically sized database fail today.
- **Recommended Vessel approach:** Add a streaming/file output mode to `IProcessRunner`, or a `RunToFileAsync` that writes to an owned temp path validated by `IPathSafetyService`. Have `IDatabaseBackupProvider` produce a `BackupArtifactFile` (path, size, SHA-256 computed while streaming). Do compression in the dump pipeline through the container runtime abstraction (`IContainerRuntimeClient.ExecAsync` with stdout to file) and keep the gzip format. Add `BackupCompressionCpuPercentage` to server settings (Domain value object restricted to 25/50/75/100). Upload from the file to `IObjectStorage` with multipart, using `TransferUtility` with a part size computed for ≤1000 parts, and set a per-call timeout instead of the 5 s client timeout. Add a per-schedule `Timeout` and a `BackupDeadline` passed through the steps as a linked CancellationToken. Write tests for >10 MiB artifacts, deadline expiry and temp-file cleanup.
- **Roadmap fit:** 11.08/11.09 (reopen), 16.04, 16.05
- **Effort:** M

#### DATA-02: Restore pipeline is synchronous, format-blind and unsafe
- **Upstream:** v4.3.12 #11481 (pg_restore for custom format), v4.4.0 #11699 (read-only import command, no `--clean` by default, `replace_existing`, `keep_owners`, `restore_mysql_users`, `sqlite_database`, format detection before any change, single-transaction PG restores, replace-swap for SQL dumps, 409 on concurrent ops, interrupted imports cleaned up, no 10-minute cut-off), v4.5.1 #12333 (block restore-scan bypasses, stop on SQL errors), v4.5.2 #12352 (MySQL/MariaDB check before change) · `app/Actions/Database/StartDatabaseImport.php`, `app/Support/DatabaseImport/DatabaseImportCommandBuilder.php` (`buildNormalizeScript`, `buildPostgresRestoreScanScript`, `postgresqlSingle`, `mysql`, `mongodb`, `sqlite`), `app/Support/DatabaseBackupFileValidator.php` (`containsPostgresqlProgramExecution`), `app/Support/PostgresUser.php`
- **Coolify behavior (now):** An import runs as a queued activity (`LOCK_SECONDS = 1800`), and a second start, restart or import is rejected with `409`. Before anything is touched, the input is normalized: bz2, xz and single-file zip are decompressed, and gzip is integrity-tested (`gunzip -t`). The format is then detected. PostgreSQL custom and tar archives go through `pg_restore --exit-on-error --single-transaction`, and SQL goes through `psql -v ON_ERROR_STOP=1 --single-transaction`. `--clean --if-exists` is used only with `replace_existing`, owners and privileges are kept only with `keep_owners`, and replace restores of SQL load into a new database and swap it in only on success. PostgreSQL inputs are scanned for program execution (`COPY … PROGRAM`, `\!`, large-object import) and rejected. The PostgreSQL user is resolved from `POSTGRES_USER`/`PGUSER`/`POSTGRESQL_USERNAME`, and Supabase images get special handling. A full-cluster restore warns that it can overwrite admin passwords.
- **Vessel today:** **Drifted / unsafe.** `DatabasesController.cs:80-96` calls `ManagedDatabaseService.RestoreAsync` inline in the request (`ManagedDatabaseService.cs:320-360`), which violates the AGENTS.md rule that long-running work belongs in Hangfire. `ProcessDatabaseBackupProvider.cs:87-108` pipes the artifact into `psql --dbname postgres --set ON_ERROR_STOP=on` with no `--single-transaction` and no format detection, as hard-coded user `postgres`. It runs `mysql --user root` and, for Redis, `redis-cli --pipe`. That Redis path is **invalid**: `--rdb` output is an RDB snapshot, not RESP, so it cannot be replayed with `--pipe`. Upstream deliberately offers no Redis backup or restore. The "dry run" only runs `pg_dump --version` (`:110-120`) and never reads the artifact. Source and target engines are not checked for compatibility (`ValidateRestoreAsync`, `:285-318`), so a MySQL dump can be "restored" into PostgreSQL. Superuser `psql` executes `\!` meta-commands inside the container (security cross-ref).
- **Gap type:** Behavior change / Security hardening
- **Priority:** P0: a destructive operation with no transactionality, broken Redis semantics and code execution through restored SQL.
- **Recommended Vessel approach:** Model `DatabaseImport` as an Application command queued to a Hangfire `RunDatabaseImportJob` with an `ImportExecution` record (status, log, source, options). Build an Infrastructure `DatabaseRestoreScriptBuilder` per engine, with tested scripts that run in a helper container via `IContainerRuntimeClient`. Order the work as: normalize → detect → validate/scan → restore in one transaction. Expose `ReplaceExisting`, `KeepOwners`, `RestoreMysqlUsers` and `SqliteDatabase` options. Enforce engine equality between artifact and target. Drop Redis/KeyDB/Dragonfly restore, or reimplement it as "stop, replace dump.rdb in the volume, start" behind an explicit flag. Hand the PostgreSQL program-execution scan to the Security agent. Add tests per engine script and for the 409 path.
- **Roadmap fit:** 11.12/11.13 (reopen), 15.10
- **Effort:** L

#### DATA-03: Backup, restore and lifecycle operations do not exclude each other
- **Upstream:** v4.4.0 ("a backup is skipped while the database starts, restarts, or imports"; "a second start, restart, or import while one is running is rejected (409)"; "Two backups … in the same second no longer write the same file") · `app/Support/DatabaseOperationReservation.php`, `DatabaseBackupJob::databaseOperationInProgress()`/`recordSkippedBackup()`
- **Coolify behavior (now):** An atomic Redis `SET NX` reservation (`database-operation-pending:{uuid}`, TTL = queued-stale limit) is taken at request time for start, restart and import. It is renewed when the job runs and released only by the holder. Backups check for the reservation and for in-progress activities, and record a "skipped" execution instead of running. Backup filenames include a per-run UUID to avoid collisions within the same second.
- **Vessel today:** **Drifted.** Lifecycle actions lock `database:{id}` (`ManagedDatabaseService.cs:73-76`), while backup and restore lock `backup:{id}` (`:247-249`, `:334-336`), so a backup can run during stop, restart or delete (`down --volumes`). The Phase 11 doc says the opposite ("locks named by database ID so backup, restore, and lifecycle actions cannot overlap"; `docs/deployment/phase-11-managed-services-backups-restore.md`). Queueing a lifecycle action (`:44-61`) takes no reservation, so two parallel requests both enqueue. Artifact keys use second resolution plus a hash (`ProcessDatabaseBackupProvider.cs:31`). That avoids collisions, so filenames are fine.
- **Gap type:** Behavior change (correctness)
- **Priority:** P1: concurrent restore and restart or delete can corrupt or destroy data. The doc claims a guarantee the code does not give.
- **Recommended Vessel approach:** Use a single `DatabaseOperationLock` key `database-op:{id}` (Application constant) for lifecycle, backup, import and flush. Backups should use a try-acquire and record a `Skipped` execution status with a reason (new enum value plus migration). Add a request-time reservation through `IDistributedLockManager` with token renewal, returning `409 Conflict` from the API. Add tests for overlap rejection.
- **Roadmap fit:** 11.04, 11.12, 16.06
- **Effort:** S

#### DATA-04: Database health is never actually probed
- **Upstream:** v4.1.2 (`add_health_check_to_standalone_databases`: `health_check_enabled`=true, interval 15 s, timeout 5 s, retries 5, start_period 5 s on all 8 engines), v4.4.0 #11730 (MongoDB health check pings), v4.7.0 (Redis health check pings, starting checks shown as `running:starting`), v4.5.0 #12224 (PG health checks set `PGPASSWORD`), v4.7.x `clear_unchecked_health_check_response_texts` · `app/Traits/HasDatabaseHealthCheck.php`, `app/Actions/Database/Start*.php`
- **Coolify behavior (now):** Every standalone DB compose file gets a `healthcheck` with an engine-specific probe. PostgreSQL uses `psql -U <user> -d <db> -c 'SELECT 1'` with port arguments parsed from the custom config. MySQL uses `mysqladmin ping`, MariaDB `healthcheck.sh --connect --innodb_initialized`, Redis/Dragonfly `redis-cli … ping`, KeyDB `keydb-cli`, ClickHouse `SELECT 1` and SQLite `healthcheck.sh`. The timings are user-configurable and the probe can be disabled. Status reflects Docker health (`running:healthy`, `running:starting`, `running:unhealthy`).
- **Vessel today:** **Missing / drifted.** The generated compose has no `healthcheck` (`ManagedDatabaseService.cs:427-443`). `MarkRunning` sets `HealthState = Healthy` unconditionally (`DatabaseResource.cs:99`), and the `Inspect` action just sets Healthy (`ManagedDatabaseService.cs:100-102`). Nothing else calls `ChangeHealth` (repo-wide grep). Roadmap 11.01 to 11.03 claim "health checks".
- **Gap type:** Behavior change / Schema
- **Priority:** P1: health is part of the Phase 11 acceptance criteria, and fake "Healthy" status misleads operators and dashboards.
- **Recommended Vessel approach:** Add a Domain `DatabaseHealthCheckPolicy` value object (Enabled, Interval, Timeout, Retries, StartPeriod) persisted on `database_resources` through a migration with upstream defaults. Generate the engine probe in a per-engine provisioning strategy. Map `Inspect` and the existing server health poller (`Monitoring/ServerHealthPollingService`) to `IContainerRuntimeClient.InspectContainer` health and update `DatabaseHealthState` (add a `Starting` value). Set `PGPASSWORD`/`REDISCLI_AUTH` through the environment, not argv.
- **Roadmap fit:** 11.01-11.03 (reopen), 12.x monitoring
- **Effort:** M

#### DATA-05: Credentials with `$` break in the generated `.env`, and the Redis password quoting semantics are missing
- **Upstream:** v4.4.0 (`add_legacy_password_quoting_to_standalone_redis`: new Redis DBs resolve and quote `REDIS_PASSWORD`, while existing ones keep `legacy_password_quoting=true`), v4.4.0 (KeyDB/Dragonfly `REDIS_PASSWORD` env sets the server password), v4.4.4 (duplicate `REDIS_USERNAME` env cleanup), v4.5.0 ("Database updates apply `redis_password`"), v4.4.0 #11970-11989 (quote container names, paths and owners in DB start commands)
- **Coolify behavior (now):** Passwords are resolved (including from secret managers) and quoted, both in the start command and in the connection URLs. A per-row flag preserves old behavior so that existing databases keep their working server password.
- **Vessel today:** **Drifted (bug).** `.env` values are quoted only when they contain whitespace, `"` or `#` (`ManagedDatabaseService.cs:457-465`). They are passed with `--env-file` (`DockerCliContainerRuntimeClient.cs:193-194`), where Compose applies `${VAR}` interpolation to unquoted and double-quoted values. So a user-supplied password containing `$` (entered in `DatabaseCreate.razor`) is silently truncated or altered in `POSTGRES_PASSWORD`/`MYSQL_ROOT_PASSWORD`/`REDIS_PASSWORD` and in the Redis `--requirepass ${REDIS_PASSWORD}` command (`:435`). The backup and restore path then uses the real password, so authentication fails.
- **Gap type:** Behavior change (correctness)
- **Priority:** P1: a silent credential mismatch that breaks backups and clients, with a trivial fix.
- **Recommended Vessel approach:** Emit `.env` values single-quoted with `'` escaped (Compose literal semantics), or escape `$` as `$$`, in one `ComposeEnvFileWriter` in Infrastructure. Add unit tests for `$`, quotes, newlines and `#`. If existing databases already run with a mangled password, add a migration flag like upstream's `legacy_password_quoting` so they are not broken on the next start. Raise this with the Security agent for the related argv-exposure issue (passwords in healthcheck argv).
- **Roadmap fit:** 11.01-11.03
- **Effort:** S

#### DATA-06: Team-scoped backup destinations (S3 storages) with validation, usability state and multiple destinations
- **Upstream:** baseline `s3_storages` plus v4.2.0 #10832 (S3 selection/validation), v4.3.3 #11297 (test uses form values), v4.3.4 (endpoint normalization), v4.3.19 (uppercase bucket names accepted), v4.4.0 (hourly revalidation of unusable storages), v4.5.0 #12215 (Content-MD5 for Object Lock), v4.5.1 (`use_path_style_endpoint`, default true; #12264 region signing), v4.5.2 (always S3v4 plus retry), v4.6.0 (IPv6-only pinning), v4.7.0 (`skip_tls_verification`; path endpoints like Supabase `/storage/v1/s3` via presigned curl), v4.5.0 `create_backup_s3_destination_tables` · `app/Models/S3Storage.php`, `app/Traits/HasS3Destinations.php`, `HasS3Replicas.php`, `app/Models/DatabaseBackupS3Replica.php`, `VolumeBackupS3Replica.php`, `app/Jobs/RevalidateUnusableS3StoragesJob.php`, `app/Rules/ValidS3BucketName.php`, `app/Http/Controllers/Api/S3StoragesController.php` (`/s3-storages`, `/s3-storages/{uuid}/validate`)
- **Coolify behavior (now):** Each team owns N storages (`name`, `endpoint`, `bucket`, `region`, `key`/`secret` encrypted, `use_path_style_endpoint`, `skip_tls_verification`, `is_usable`, `unusable_email_sent`, `type`). The connection test runs on save and sets `is_usable`; a failure notifies the team once, and a job re-tests unusable storages every hour. A DB or volume backup schedule selects many destinations (pivot tables `scheduled_database_backup_s3_storage` and `scheduled_volume_backup_s3_storage`). Each execution records one replica row per destination (`s3_uploaded`, `s3_storage_deleted`, `message`). Retention and deletion are applied per replica, and deleting a storage detaches it from backups. Creating storages from a schedule needs team-wide write (v4.7.0).
- **Vessel today:** **Partial (instance-only).** There is exactly one `IObjectStorage`, chosen from appsettings (`InfrastructureServiceCollectionExtensions.cs:166-179`; `ObjectStorageOptions.cs`). The S3 client uses ambient AWS credentials (`S3ObjectStorage.cs:400`) and has no TLS-skip option. The bucket name is hard-coded to `"vessel-backups"` in the provider (`ProcessDatabaseBackupProvider.cs:32`), which ignores `ObjectStorageOptions.BucketName`. `BackupStorageKind.Local` vs `ObjectStorage` is stored but ignored: every run writes to the one store (`ManagedDatabaseService.cs:258`), and manual backups hard-code `ObjectStorage` (`:210`, `DatabaseDetails.razor:108`). `/storages` is a placeholder page (`Storages.razor:8-9`). There is no destination entity, test, usability state or API.
- **Gap type:** New feature / Schema / API contract
- **Priority:** P1: per-team destinations are table stakes for multi-tenant backups, and the current hard-coded bucket and ignored storage kind are latent bugs.
- **Recommended Vessel approach:** Add a Domain `BackupDestination` aggregate (TeamId, Name, Kind, Endpoint, Bucket, Region, PathStyle, SkipTlsVerification, CredentialSecretReferenceId, IsUsable, LastValidatedAt, UnusableNotifiedAt) and a `BackupScheduleDestination` join plus an `BackupArtifactReplica` child of `BackupExecution`. In Application, define `IBackupDestinationClientFactory` returning a scoped `IObjectStorage` per destination, and keep the instance store for system artifacts. Infrastructure keeps the AWSSDK provider, configured per destination (ForcePathStyle, a custom `HttpClientFactory` for TLS skip, SigV4, Content-MD5, multipart). Add a Hangfire `RevalidateUnusableDestinationsJob` (hourly) and a `ValidBucketName` validator that accepts uppercase for existing buckets. API: `GET/POST/PATCH/DELETE /api/v1/backup-destinations` and `POST …/{id}/validate`. Endpoint SSRF pinning (upstream `SafeWebhookUrl::pinnedAddress`) belongs to the Security agent.
- **Roadmap fit:** 5.22/5.24 (extend), 11.08; New: Phase 11b "Backup destinations"
- **Effort:** L

#### DATA-07: Backup schedule model parity (database selection, formats, retention, timeout, edit/delete)
- **Upstream:** baseline fields plus v4.3.8 #11331 (complete "All databases" dumps and multi-DB selections), v4.4.0 (all-databases dumps fail on dump error; per-database result and finish time), v4.5.0 #12212 (use configured databases when env missing), v4.5.1 #12244 (keep privileges in PG backups), v4.6.0 (service DBs choose databases), v4.7.0 (`save_s3` default fix; Odoo all-DB default; "All databases" label), main `ce06a7981` (cascade executions on schedule delete, migration `2026_11_15_120000`) · `app/Models/ScheduledDatabaseBackup.php`, `app/Jobs/DatabaseBackupJob.php` (`backup_standalone_postgresql`, `databasesToBackup`), `app/Actions/Database/DeleteScheduledDatabaseBackup.php`, OpenAPI `POST /databases/{uuid}/backups`
- **Coolify behavior (now):** A schedule has `frequency` (cron or alias), `enabled`, `databases_to_backup` / `dump_all`, `save_s3`, `s3_storage_uuids[]`, `disable_local_backup`, and retention for local and S3 separately by count, days and max storage in GB. It also has `timeout`, `missing_backup_notification_days` and `backup_now`. A single PostgreSQL DB is dumped with `pg_dump --format=custom` (owners kept), all DBs with `pg_dumpall`. MySQL uses `mysqldump --single-transaction --quick --lock-tables=false`, MariaDB uses `mariadb-dump`, MongoDB uses `mongodump --gzip --archive` with collection exclusion, ClickHouse uses `BACKUP DATABASE … TO File()`, and SQLite uses `VACUUM INTO`. Deleting a schedule removes its executions (and, on request, its files).
- **Vessel today:** **Partial.** `BackupSchedule` has only cron, retention count (1-500) and storage kind (`BackupSchedule.cs:12-29`). PostgreSQL dumps only the `postgres` DB in plain format with `--no-owner --no-privileges` (`ProcessDatabaseBackupProvider.cs:69-73`), so application databases created under other names are **not backed up at all**. MySQL/MariaDB always use `--all-databases`. Retention prunes by count, using the newest enabled schedule of the DB across all executions (`ManagedDatabaseService.cs:467-495`). There is no update or delete use case or API: `BackupSchedule.Update` is never called, and the Hangfire recurring job `backup:{id}` (`:192-195`) is never removed, so a disabled schedule keeps firing and throws (`:228`). The schedule→execution FK is `SetNull` (`VesselDbContext.cs:744-747`). A second, redundant schedule model also exists: `BackupPolicy` / `database_backup_policies` (`BackupPolicy.cs`, `DatabaseResource.cs:158-162`).
- **Gap type:** Behavior change / Schema / API contract
- **Priority:** P1: backups that silently skip the real application database are a core-parity failure.
- **Recommended Vessel approach:** Extend `BackupSchedule` with `DatabaseSelection` (All | Named list, validated names), `RetentionPolicy` value objects for local and remote (Count, Days, MaxBytes), `Timeout`, `DisableLocalCopy`, `Destinations` and `MissingAlertDays`. Make the Infrastructure dump strategies per engine (custom format by default, owners kept), with each DB producing its own `BackupExecutionItem` (result, size, finish time). Add `UpdateBackupSchedule`, `DisableBackupSchedule` and `DeleteBackupSchedule` commands that call `IRecurringJobScheduler.Remove`, with optional artifact deletion choices for local and remote. Remove `BackupPolicy` through a migration. Change the FK to cascade, or soft-archive executions explicitly.
- **Roadmap fit:** 11.10, 11.11 (reopen)
- **Effort:** L

#### DATA-08: Backup notifications, missing-backup alerts and failure throttling
- **Upstream:** v4.3.18 #11433 (missing DB backup alerts: `missing_backup_notification_days`, `missing_backup_notification_sent_at`, `last_execution_at`), v4.4.1 (volume backup success/failure/warning notifications; missing volume backup alerts, off by default; missed scheduled starts recorded as failed and notified), v4.7.0 (failures notify on first failure, then at most once a day; one "works again" notice; S3-upload failures throttled; backups never auto-disabled), v4.5.1 #12285 / v4.7.2 (instance URL and links in notifications) · `app/Jobs/CheckMissingDatabaseBackupsJob.php`, `CheckMissingVolumeBackupsJob.php` (hourly, `onOneServer`), `app/Notifications/Database/BackupMissing.php`, `app/Notifications/VolumeBackup/{BackupSuccess,BackupFailed,BackupMissing,RecoveryFailed}.php`
- **Coolify behavior (now):** An hourly job finds enabled schedules with `missing_backup_notification_days > 0` whose last execution (or creation) is older than N days. It sends `BackupMissing` once per idle period, to teams with backup-failure channels enabled, and skips servers that were transferred away. Success and failure notifications go to the team backup channels and are throttled to the first failure plus one per day.
- **Vessel today:** **Missing.** `NotificationDispatchService.cs:136-143` filters `backup.*` events and the notification policy has `BackupEventsEnabled`, but nothing raises them. `NotificationEventService.RaiseAsync` is only called from `NotificationsController` (repo grep). `ManagedDatabaseService` writes audit records only (`:271-274`). There is no missing-backup check.
- **Gap type:** New feature / Schema
- **Priority:** P1: an unnoticed backup failure is the most common way to lose data. The Phase 13 plumbing exists but nothing produces events.
- **Recommended Vessel approach:** Raise `backup.succeeded`, `backup.failed`, `backup.missing` and `backup.recovered` domain events from `BackupExecution` state transitions and dispatch them via `NotificationEventService` in an Application handler. Add `LastExecutionAt`, `MissingAlertDays` and `MissingAlertSentAt` to schedules, and a Hangfire `CheckMissingBackupsJob` (hourly, Redis-locked). Throttle with a `FailureStreak` (FailingSince, LastNotifiedAt) on the schedule. Coordinate with the Notifications agent.
- **Roadmap fit:** 13.x (event producers), 11.10
- **Effort:** M

#### DATA-09: Scheduled volume and directory backups
- **Upstream:** v4.2.0 #10946 (closes #7701), v4.3.0 (combined DB and storage backup page), v4.3.3 (CPU-limited compression), v4.3.9 #11358 (10 h default), v4.3.18 #11642 (stream S3-only), v4.3.22 (delete local and S3 archives on schedule removal; once per occurrence across schedulers), v4.4.0 (Needs attention plus retry; recovery not piling up), v4.5.0 (tar exit 1 accepted; skip removed containers; Content-MD5) · `app/Models/ScheduledVolumeBackup.php`, `ScheduledVolumeBackupExecution.php`, `app/Jobs/VolumeBackupJob.php`, `VolumeBackupRecoveryJob.php`, `app/Actions/Shared/DeleteScheduledVolumeBackup.php`, `app/Http/Controllers/Api/VolumeBackupsController.php` (`/{applications|databases|services}/{uuid}/storages/{storage_uuid}/backups[/run]`), migrations `2026_07_15_102537/8`, `2026_10_03_172204` (recovery bounds)
- **Coolify behavior (now):** There is one schedule per backupable item (a persistent volume, or a directory mount that is not a host file), with a unique `(backupable_type, backupable_id)`. Fields are `frequency`, `enabled`, `save_s3`, `disable_local_backup`, `stop_during_backup`, retention for local and S3 (amount 7, days, max storage), `timeout` 36000 and `missing_backup_notification_days`. A run tars `/volume` in a `docker run --rm` helper using `tar -I "<pigz|gzip>"`, where exit code 1 counts as success. It can stop the containers that mount the volume and restart them afterwards (recorded in `stop_container_ids` and `stop_recovery_pending`). With S3-only and a single destination, the archive streams directly. Executions track `s3_cleanup_pending`, `recovery_last_attempt_at`, `recovery_error` and `recovery_needs_attention`. A recovery job restarts containers that were left stopped and cleans up half-finished uploads, with bounded retries.
- **Vessel today:** **Missing.** No volume backup entity, job or API exists. `BackupExecution` is hard-bound to `DatabaseResourceId` (`BackupExecution.cs:13-14`). Roadmap 11.08 claims "Database and volume backups can target object storage", but only database dumps exist. This also depends on DATA-13, since there is no app/service storage model.
- **Gap type:** New feature / Schema / API contract
- **Priority:** P1: a headline upstream feature (most-requested #7701), and the only way to back up non-database state.
- **Recommended Vessel approach:** Generalize to a `BackupTarget` discriminated value (DatabaseResource | Volume | DirectoryMount) on schedules and executions, or add a parallel `VolumeBackupSchedule` aggregate that shares `RetentionPolicy` and destinations. Put the archive helper in Infrastructure (a runtime-agnostic `docker|podman run --rm -v vol:/volume:ro`) using the DATA-01 streaming path. Make container stop and restart a compensating step stored on the execution, with a `VolumeBackupRecoveryJob` (bounded attempts, `NeedsAttention` state plus a retry command). Use a Redis lock per volume. API: per-storage `GET/POST/PATCH/DELETE …/storages/{id}/backups` plus `POST …/run`.
- **Roadmap fit:** 11.08 (reopen); New: Phase 11b
- **Effort:** XL

#### DATA-10: Database import from upload, server path or S3 (API and UI)
- **Upstream:** v4.4.0 #11699, v4.5.0 #12194/#12198 (`deploy` ability for imports), v4.6.0 (permission checks before file checks), v4.7.0 (Import button on the Backups page, dialog) · `app/Actions/Database/StartDatabaseImport.php` (`MAX_BYTES = 10 GiB`, `LOCK_SECONDS = 1800`), `app/Http/Controllers/Api/Concerns/HandlesDatabaseImportsApi.php`, `app/Support/DatabaseImport/DatabaseImportSource.php`, `DatabaseImportCleanup.php`, `app/Console/Commands/CleanupDatabaseImportUploads.php` (staged uploads deleted after 24 h), `app/Events/DatabaseImportFinished.php`, MCP `Tools/GetDatabaseImport.php`
- **Coolify behavior (now):** `POST /databases/{uuid}/imports/uploads` takes chunked uploads (`upload_id`, max 10 GiB, extension allow-list through `DatabaseBackupFileValidator`). `POST /databases/{uuid}/imports` takes `source` = `upload|s3|server`, `upload_id | path + s3_storage_uuid`, `dump_all`, `replace_existing`, `keep_owners`, `restore_mysql_users` and `sqlite_database`. `GET /databases/{uuid}/imports/{activity_id}` returns status. The same routes exist under `/services/{uuid}/databases/{database_uuid}`. Upload and start need `write`, and a server-path source also needs `deploy`. Without team-wide write, members can import only backups of the same database. A finished-import event refreshes the UI.
- **Vessel today:** **Missing.** Only "restore execution X into DB Y" exists (`DatabasesController.cs:80-96`). There are no external sources, uploads, status resource or upload cleanup.
- **Gap type:** New feature / API contract
- **Priority:** P1: migrating existing data into a managed DB is a primary onboarding path, and Coolify-migration users (Phase 17) need it.
- **Recommended Vessel approach:** Build on DATA-02's `DatabaseImport` command. Add an `ImportSource` discriminated union (Upload(stagingId) | ServerPath(path) | Destination(destId, key)). Implement a chunked upload controller in Web that streams to owned staging (`IPathSafetyService`) with a size cap, and a Hangfire `CleanupImportStagingJob` (24 h). Add a status resource (`GET …/imports/{id}`) and SignalR progress. Tie authorization to the API-token abilities from the API agent.
- **Roadmap fit:** 11.12; 17.x (migration)
- **Effort:** L

#### DATA-11: Default images, Valkey and the "latest" default
- **Upstream:** v4.7.0 (`update_standalone_database_default_images`, `set_valkey_as_standalone_redis_default_image`, Redis/Valkey step in the new-DB wizard), v4.5.1 #12337 (database image picker) · migrations `2026_11_06_083240`, `2026_11_06_113843`
- **Coolify behavior (now):** New-database defaults are `postgres:18-alpine`, `valkey/valkey:9-alpine` (Redis type; plain Redis stays selectable and moved from `redis:7.2` to `redis:8`), `mongo:9`, `mysql:9.7`, `mariadb:12.3`, `clickhouse/clickhouse-server:26.8` and SQLite `peakimages/sqlite:3.54.1-v0.2.0`. The new resource page also offers Supabase PostgreSQL 17.11.0.018 and PostGIS 18-3.7-alpine. Existing databases keep their image.
- **Vessel today:** **Drifted.** The create UI defaults `Version = "latest"` for every engine (`DatabaseCreate.razor:114`), and the image is built as `postgres:{version}` / `redis:{version}` (`ManagedDatabaseService.cs:415-420`). An unpinned `latest` will cross major versions on recreate, and PostgreSQL major upgrades refuse to start on an existing data directory. There is no full-image override (Supabase/PostGIS) and no Valkey option for the database type. Valkey exists only as a service template (`ServiceTemplateCatalog.cs:1321`).
- **Gap type:** Behavior change
- **Priority:** P1: `latest` combined with a persistent volume is a data-availability hazard, and the fix is small.
- **Recommended Vessel approach:** Add a Domain `DatabaseImage` value object (repository plus tag; an explicit "custom image" variant) instead of `Version`. Add a per-engine default catalog in Application (single source, with tests asserting the upstream defaults). Add a `RedisFlavor` (Valkey default | Redis). Write a migration that converts existing `Version` values to explicit images unchanged. Reject `latest` for stateful engines, or warn.
- **Roadmap fit:** 11.01-11.03
- **Effort:** S

#### DATA-12: Application and service persistent storage model (volumes and file mounts)
- **Upstream:** v4.2.0 (`is_host_file` on `local_file_volumes`, #10831 mount-path handling, #10843 file-volume state), v4.3.7 (block deletion of Compose-managed volumes), v4.3.12 #11455 (remove preview volumes), v4.3.22 (**`host_path` removed from UI and API; requests containing it are rejected**), v4.4.0 #7591 (named volumes by default, also on Swarm; volumes and dirs separated in UI; external volumes used as declared; driver opts kept, `ignores_compose_driver_options` migration; relative mounts inside the resource dir; `EnsureContentFilesOnServer` before `compose up`), v4.4.4 (`AtomicFilesystem`), v4.5.1 #12248 (write content on API PATCH), v4.6.0 (path containment, no symlink writes, `deploy` ability for mounts outside the resource dir; cross-ref Security), v4.7.0 (rename-volume data-loss dialog; deleted volume resurrected by image `VOLUME`; UUID prefix explanation), main `171eae00a` (`variable_fs_path`, which keeps the saved host path when a variable later resolves) · `app/Models/LocalPersistentVolume.php`, `LocalFileVolume.php` (`MAX_CONTENT_SIZE = 5 MiB`), `app/Actions/Shared/EnsureContentFilesOnServer.php`, API `/{applications|databases|services}/{uuid}/storages`
- **Coolify behavior (now):** A resource has named persistent volumes (name, mount_path, unique per server with a UUID prefix) and file or directory mounts (`fs_path`, `mount_path`, `content`, `is_directory`, `is_host_file`, `is_based_on_git`). The API rejects `name`/`host_path` where they don't apply. Content files are written before containers start, but only when missing or an empty directory. Relative paths must stay inside the resource directory.
- **Vessel today:** **Missing.** Only `DatabaseResource.Storage` (one volume name and mount path; `StorageConfiguration.cs`) exists. Applications and services have no volume or file-mount entity (repo grep for `PersistentVolume|FileStorage|HostPath` returns nothing). The runtime abstraction only lists volumes (`IContainerRuntimeClient.cs:29`).
- **Gap type:** New feature / Schema / API contract
- **Priority:** P1: a prerequisite for stateful apps, volume backups (DATA-09) and the file browser (DATA-14). Building it now lets Vessel adopt the post-v4.3.22 semantics (named volumes, no host_path) without a later breaking change.
- **Recommended Vessel approach:** In Domain, add `PersistentVolume` (Name, MountPath, ResourceRef) and `FileMount` (Path relative to the owned resource dir, MountPath, Content ≤5 MiB, IsDirectory, IsHostFile) owned by Application, Service and Database aggregates. Do not expose `host_path` in the API; host-file mounts should be a separately authorized capability (Security). Infrastructure writes content atomically (temp file plus rename) before `compose up`, using `IPathSafetyService` with symlink checks. Confirm volume renames ("new name = new empty volume"), and protect volumes used by other resources.
- **Roadmap fit:** 7.x/8.11 (extend); New: Phase 11b or a dedicated storage phase
- **Effort:** XL

#### DATA-13: Backup the control-plane database before self-update, and instance backup settings
- **Upstream:** v4.5.0 #12093 (`is_backup_before_update_enabled`, default true), v4.7.0 (instance DB backup always runs on the Coolify host; a failed pre-update backup shows why; a disabled schedule is skipped), v4.7.2 (root team owners and admins can change and download the instance backup), v4.3.5 #11249 (legacy ID 0 records) · `app/Actions/Server/UpdateCoolify.php::backupDatabase()`, `app/Livewire/SettingsBackup.php`
- **Coolify behavior (now):** Before the upgrade script runs, `UpdateCoolify` synchronously runs the instance DB backup job, bypassing overlap middleware. If the backup fails, the update is aborted with "The database backup failed, so the update was not started", and the setting can be turned off. Settings → Backup manages the instance DB schedule, destinations and retention, and works even when no schedule exists.
- **Vessel today:** **Missing.** Vessel's own PostgreSQL has no managed backup schedule or settings page, and Phase 14 self-update (14.11-14.15) is not started. `Settings.razor` has no backup section (grep).
- **Gap type:** New feature
- **Priority:** P1: shapes the design of the Phase 14 update and rollback flow. A binary rollback without a pre-migration DB snapshot cannot undo EF migrations.
- **Recommended Vessel approach:** Add an Application `ControlPlaneBackupService` that uses the same dump and destination pipeline (DATA-01/06) against the configured Vessel connection string, through `IProcessRunner` (`pg_dump --format=custom`) or Npgsql `COPY`. Expose an `InstanceBackupSettings` record (Enabled, BeforeUpdate=true, Schedule, Destinations, Retention). `vessel self-update` runs the backup, then migrates, and on health failure rolls back the binary and offers the snapshot. Document this in `docs/operations/backups.md`.
- **Roadmap fit:** 14.11-14.15, 14.20, 16.12
- **Effort:** M

#### DATA-14: Backup API contract (list, update, delete, executions, downloads) and sensitivity rules
- **Upstream:** baseline backup API plus v4.2.0 (state-changing endpoints POST-only), v4.5.0 #12151 (execution `message` hidden without `read:sensitive`/`root`), v4.5.1 #12279 (backup endpoints scoped to the database type), v4.6.0 (`delete` ability for every delete), v4.7.0 (`save_s3` default fix; backup pages don't load all executions) · OpenAPI `/databases/{uuid}/backups`, `…/backups/{scheduled_backup_uuid}`, `…/executions`, `…/executions/{execution_uuid}`; `app/Traits/PreparesBackupDownloads.php`, `app/Support/StreamsDownloads.php`
- **Coolify behavior (now):** Full CRUD on schedules (create fields include `s3_storage_uuids[]`, `dump_all`, `timeout`, `missing_backup_notification_days`, `backup_now`). Executions can be listed (paginated) and deleted, with an option to delete the S3 copy. Backups can be downloaded by streaming from local disk or a presigned destination URL; SFTP downloads require a pinned host key.
- **Vessel today:** **Partial.** The only endpoints are `POST /api/v1/databases/{id}/backups`, `POST /api/v1/databases/backup-schedules` and `POST …/backups/{id}/restore/{target}` (`DatabasesController.cs:54-96`). There is no GET for schedules or executions, no PATCH or DELETE, and no download. `BackupExecutionSummary` always includes `FailureReason` (`ManagedServiceModels.cs:239-253`). Restore is not scoped so that source and target share a database or engine.
- **Gap type:** API contract
- **Priority:** P1: the UI and automation cannot see backup history, and failure messages need a sensitivity gate.
- **Recommended Vessel approach:** Add thin controller actions over Application queries and commands: `GET /databases/{id}/backup-schedules`, `PATCH/DELETE /backup-schedules/{id}`, `GET /backup-schedules/{id}/executions?page=`, `DELETE /executions/{id}?deleteRemote=`, `GET /executions/{id}/download` (stream from `IObjectStorage` or redirect to a presigned URL). Gate the failure message behind a `backups:read-sensitive` scope (coordinate with the API agent's ability model) and require delete-scope for deletes.
- **Roadmap fit:** 6.x API, 11.14
- **Effort:** M

#### DATA-15: Non-S3 backup storage types (SFTP, FTPS, WebDAV)
- **Upstream:** v4.7.0 (types, downloads, retention, restores; SFTP needs an admin-saved host key), v4.7.5 (SFTP over IPv6; network reason in errors) · `app/Enums/BackupStorageType.php` (`s3|sftp|ftps|webdav`; endpoint schemes; default ports 22/21), migrations `2026_10_31_161621` (`type`, nullable `bucket`, `private_key_id`) and `2026_11_01_170003` (`sftp_host_key`), `app/Support/{SftpFilesystemAdapter,FtpsFilesystemAdapter,WebdavFilesystemAdapter,PinnedSftpConnectionProvider}.php`, `app/Services/RcloneTransfer.php`, `app/Notifications/SftpHostKeyChanged.php`
- **Coolify behavior (now):** The same `s3_storages` table is reused with `type`. SFTP logs in with a password or a team private key, pins the host key on the first validated test, and refuses changed keys (with a notification). Server-side transfers go through rclone in a helper container. File-browser downloads and avatars still require S3 (presigned URLs).
- **Vessel today:** **Missing.** `BackupStorageKind` is only `Local | ObjectStorage` (`BackupStorageKind.cs`).
- **Gap type:** New feature / Schema
- **Priority:** P2: valuable for self-hosters without S3, but builds on DATA-06.
- **Recommended Vessel approach:** Make `BackupDestinationKind` an enum on the DATA-06 aggregate with kind-specific validated settings. Implement `IBackupDestinationClient` in Infrastructure with an S3 implementation (AWSSDK), an SFTP one (SSH.NET or the existing `Ssh` layer with host-key pinning reused from server host-key verification), and WebDAV/FTPS through `HttpClient`/FluentFTP. Alternatively, a single rclone helper behind `IProcessRunner`. Prefer native .NET clients so transfers stay cancellable and redacted.
- **Roadmap fit:** New: Phase 11b
- **Effort:** L

#### DATA-16: Additional engines: MongoDB, KeyDB, Dragonfly, ClickHouse (with scheduled backups) and SQLite
- **Upstream:** v4.2.0 (scheduled ClickHouse backups, `app/Support/ClickhouseBackupCommand.php`), v4.4.0 #11992 (StandaloneSqlite; one-click connect of its volume to an app or Compose app on the same server; backup and restore), v4.6.0 (SQLite image 3.54.1-v0.2.0, writable by any UID; bind-mount warning), v4.4.0 (ClickHouse file-mount data volume adoption) · `app/Actions/Database/Start{Mongodb,Keydb,Dragonfly,Clickhouse,Sqlite}.php`, `app/Models/StandaloneSqlite.php` (`DATA_DIRECTORY = /var/lib/sqlite`, `sqlite_databases` default `database.sqlite`, pattern `^[A-Za-z0-9][A-Za-z0-9._-]*(,…)*$`), migrations `2026_09_18_145710`, `2026_09_26_152908`, `2026_10_30_171359`
- **Coolify behavior (now):** All 9 engines are first-class with compose generation, health checks and (except Redis-family) scheduled backups. ClickHouse uses `BACKUP DATABASE … TO File()` plus `docker cp`. SQLite uses `sqlite3 -readonly … "VACUUM INTO"` and restores into a selected file.
- **Vessel today:** **Partial.** The enum lists 8 engines (`DatabaseEngine.cs`), but provisioning throws for anything beyond PG, MySQL, MariaDB and Redis (`ManagedDatabaseService.cs:421-422`). The create UI still offers MongoDB (`DatabaseCreate.razor:24`), so users can create a database that can never start. Backup and restore have the same 4-engine limit (`ProcessDatabaseBackupProvider.cs:83`). There is no SQLite.
- **Gap type:** New feature
- **Priority:** P2: parity breadth. The MongoDB UI inconsistency should be fixed immediately (hide it until supported).
- **Recommended Vessel approach:** Replace the `switch` with an `IDatabaseEngineStrategy` per engine in Infrastructure (compose fragment, health probe, env keys, backup and restore scripts). Add engines one by one with tests. Treat SQLite as a volume-centric engine with an explicit "connect to application" use case that mounts the volume and protects it from deletion.
- **Roadmap fit:** 11.01-11.04 (extend)
- **Effort:** L

#### DATA-17: File browser for applications, services and databases
- **Upstream:** v4.5.0 #11518 (fixes #6519), v4.5.1 (container picker in the header), v4.6.0 (helper cleanup no longer kills running file-browser helpers) · `app/Services/ContainerFilesystemService.php` (list/read/write/mkdir/chmod/rename/delete/upload/download; `MAX_INLINE_WRITE_SIZE = 64 KiB`, `MAX_DOWNLOAD_SIZE = 1 GiB`; helper container sharing the PID namespace through `/proc/1/root` for shell-less images, `--pids-limit 64`, capabilities mirrored), `app/Services/FileDownloadStorageService.php` (prefix `coolify-file-downloads/`, link TTL 15 min, object TTL 6 h, max 5 GiB, bucket lifecycle rule plus private check), `app/Jobs/{PrepareFileDownloadJob,DeleteFileDownloadJob}.php`, `app/Http/Controllers/FileBrowserDownloadController.php`, `instance_settings.file_download_s3_storage_id`
- **Coolify behavior (now):** Users browse and edit files inside a running container. Large downloads are staged to a configured S3 storage and served through a short-lived presigned URL; otherwise they are streamed directly.
- **Vessel today:** **Missing.** Vessel has a terminal (Phase 12) but no filesystem API.
- **Gap type:** New feature
- **Priority:** P2: high operator value, but security-sensitive. It needs the Security agent's review and the DATA-06 destinations.
- **Recommended Vessel approach:** Add an Application `ContainerFilesystemService` interface with structured operations, and implement it in Infrastructure through `IContainerRuntimeClient` exec (structured args, no shell concatenation) with a helper-container fallback. Add size limits as constants, and audit every mutation. Downloads should either stream through a Web controller or use a Hangfire `PrepareFileDownloadJob` that uploads to the instance download destination and returns a presigned URL, with a cleanup job. Authorize with the same permission as the terminal.
- **Roadmap fit:** 12.x; New: Phase 12b
- **Effort:** L

#### DATA-18: Clone and migrate databases with volume data
- **Upstream:** v4.4.0 (cloned volumes keep driver and options), v4.5.1 (confirmation when cloning with volume data; source stopped during copy), new API `POST /databases/{uuid}/clone` (`clone_volume_data` needs `deploy`) and `POST /databases/{uuid}/migrate` (optional volume transfer across Coolify-managed servers) · `app/Actions/Shared/CloneVolumeDataWithSourceStopped.php`, `app/Jobs/VolumeCloneJob.php`
- **Coolify behavior (now):** Cloning copies volume data in one queued chain. A running source is stopped before the copy and restarted afterwards, also on failure; a stopped source stays stopped. The team is notified on failure.
- **Vessel today:** **Missing.** No clone or migrate use case.
- **Gap type:** New feature / API contract
- **Priority:** P2: useful for staging copies and server moves, and naturally reuses DATA-09 primitives.
- **Recommended Vessel approach:** Add an Application `CloneDatabaseCommand` and `MigrateDatabaseCommand` as Hangfire chains. The volume copy uses a runtime helper (`run --rm -v src:/from:ro -v dst:/to`), and SSH streaming covers cross-server moves later. Make the source stop and start a compensating step under the DATA-03 operation lock.
- **Roadmap fit:** New: Phase 11b; 17.x
- **Effort:** M

#### DATA-19: Backup job reliability, housekeeping, indexes and table tuning
- **Upstream:** v4.3.22 (once per occurrence across schedulers), v4.4.0 (lost backup jobs re-queued after 60 min; stale running executions marked failed after 2× timeout (`markStaleExecutionsAsFailed`); backups use current server state; one scheduler process per type), v4.4.1 (missed scheduled start recorded as failed), v4.4.0 (daily cleanup deletes in batches), v4.7.0 (backup pages paginate), v4.7.4 (failed DB backup records older than 60 days deleted), v4.1.2 `tune_postgres_fillfactor_and_autovacuum`, index migrations (`scheduled_volume_executions_backup_created_index`, `scheduled_volume_executions_recovery_index`, replica `execution_id` indexes, `2026_10_28_*` dashboard and history indexes)
- **Coolify behavior (now):** Each scheduler dispatches every minute with occurrence UUIDs. A watchdog re-queues or fails lost and stale runs. Housekeeping prunes old failure rows. Hot-update tables run with fillfactor 70-90 and autovacuum scale factor 0.05.
- **Vessel today:** **Partial.** Hangfire recurring jobs give once-per-occurrence. A crashed worker leaves `BackupExecution` stuck in `Running`: there is no stale sweep, and `Fail` is only reached in the catch at `ManagedDatabaseService.cs:277-282`. There is no failed-row retention and no pagination (no list query exists). The only execution index is `(TeamId, DatabaseResourceId, CreatedAt)` (`VesselDbContext.cs:735`), with nothing on `(ScheduleId, CreatedAt)` or `(Status, StartedAt)`. Fillfactor and autovacuum are untuned on hot tables (`database_resources`, `servers`, `deployments`).
- **Gap type:** Behavior change / Schema
- **Priority:** P2: reliability under crashes and long-lived installs.
- **Recommended Vessel approach:** Add a Hangfire `BackupWatchdogJob` that fails `Running` executions older than 2× the timeout and records a missed occurrence when `LastRunAt` falls behind the cron schedule. Add a `BackupHousekeepingJob` that batch-deletes failed executions older than 60 days. Add indexes `(ScheduleId, CreatedAt)` and `(Status, StartedAt)` in the Phase 16 index migration. Put fillfactor and autovacuum tuning in a raw-SQL EF migration for `database_resources`, `servers`, `deployments` and `backup_executions`.
- **Roadmap fit:** 16.01, 16.04, 16.05, 16.06
- **Effort:** M

#### DATA-20: Managed-service workspace location and atomic, owner-only file writes
- **Upstream:** v4.4.4 `app/Support/AtomicFilesystem.php` (temp file plus rename), v4.4.0 (`EnsureContentFilesOnServer`; "Database starts … work when the user cannot write to `/data/coolify`"), v4.4.4 ("database startup so configuration files were written before containers started"), v4.5.0 (custom configurations under a strict umask)
- **Coolify behavior (now):** Generated config and content files are written atomically before containers start, under a predictable data root, and work with a strict umask and non-root SSH users.
- **Vessel today:** **Drifted.** Workspaces live under `AppContext.BaseDirectory/storage/managed-services` (`LocalManagedServiceWorkspace.cs:10`), which is the binary directory and conflicts with the planned `/var/lib/vessel` layout and atomic binary swap (14.07/14.12). `.env` with secrets is written with `File.WriteAllTextAsync` and only then chmod'ed (`:36-40`), which is non-atomic and leaves a world-readable window. `LocalObjectStorage.PutAsync` writes in place (`LocalObjectStorage.cs:7-13`), so a failed write leaves a partial artifact.
- **Gap type:** Behavior change / Security hardening (cross-ref Security)
- **Priority:** P2: will lose workspaces on self-update and briefly exposes secrets.
- **Recommended Vessel approach:** Take the root from configured `Vessel:Paths:Data` (Phase 14.07). Add an Infrastructure `AtomicFileWriter` that creates the temp file with owner-only mode (`UnixCreateMode`), writes, `fsync`s and renames, and use it for workspaces and local artifacts.
- **Roadmap fit:** 14.07, 15.10
- **Effort:** S

#### DATA-21: Cache flush for Redis-family databases
- **Upstream:** v4.5.0 #11513 · `app/Actions/Database/FlushDatabase.php`
- **Coolify behavior (now):** For Redis, KeyDB and Dragonfly, `docker exec <db> sh -c 'REDISCLI_AUTH="$REDIS_PASSWORD" redis-cli|keydb-cli FLUSHALL ASYNC'` runs, with TLS args for Dragonfly when SSL is on. It fails unless the output is `OK`.
- **Vessel today:** **Missing.**
- **Gap type:** New feature
- **Priority:** P3: a convenience action.
- **Recommended Vessel approach:** Add a `DatabaseLifecycleAction.Flush` (Redis-family only, Domain-guarded), queued under the DATA-03 lock and executed through runtime exec with the password in the environment, plus an audit record and a confirmation in the UI and API.
- **Roadmap fit:** 11.04
- **Effort:** S

#### DATA-22: Database TLS, public TCP proxy and restart-limit semantics (baseline gap; upstream semantics moved)
- **Upstream:** v4.3.3 #11286 (PG SSL key ownership), v4.3.9 #11315/#11352 (SSL DBs not left stopped during ownership repair), v4.7.0 ("Regenerate SSL Certificates" writes `server.crt`/`server.key` instead of `server.pem` for PG, MySQL, MariaDB and the Redis family; P-256 keys for Compass), v4.3.8 #11348 and v4.3.18 #11633 (persist public access), v4.4.4 #12133 (IPv6 TCP proxies; proxy follows DB IP changes), v4.5.2 (recreate outdated TCP proxies), v4.3.17/v4.3.20 (restart limits removed from databases; DBs follow Docker restart policy), v4.6.0 (PostgreSQL init-script filenames kept inside the init dir; Security cross-ref)
- **Coolify behavior (now):** Optional per-DB TLS with a CA-signed P-256 certificate (`server.crt`/`server.key`, correct ownership), a public TCP proxy (nginx stream) that is IPv6-aware and self-healing, and Docker restart policy preserved on stop. Databases are not subject to the application restart-limit.
- **Vessel today:** **Missing.** There is no TLS, no public exposure, no init scripts, and the restart policy is fixed at `unless-stopped` (`ManagedDatabaseService.cs:441`). There is no restart-limit on DBs, which matches upstream's final position.
- **Gap type:** New feature
- **Priority:** P3: niche for the first release. When built, start from the newer semantics (crt/key naming, P-256, no DB restart-limit).
- **Recommended Vessel approach:** Defer. When it is built, reuse the Phase 10 certificate infrastructure for a per-DB CA. Implement the TCP proxy as a proxy-module concern (cross-ref the Proxy agent).
- **Roadmap fit:** New: later phase
- **Effort:** L

### Reviewed and not applicable

- **`DatabaseOperationReservation` cache TTL coupling to `ResourceStartActivity`, and Livewire `DatabaseImportFinished` broadcast:** Laravel and Livewire internals. Vessel equivalents are covered by DATA-03 (Redis lock) and SignalR (DATA-10).
- **`remove_duplicate_redis_username_environment_variables` (v4.4.4):** a data-repair migration for Coolify's per-DB env-var rows. Vessel has no DB env-var rows, so there is nothing to repair.
- **Legacy ID `0` scheduled records (v4.3.5 #11249) and "Identified the instance database backup by ID":** Coolify seeding artifacts.
- **`drop_image_default_from_standalone_sqlites_table`, `add_redact_all_env_values_in_logs_to_standalone_databases`, `add_restart_limit_*` then `remove_restart_limits_from_databases`:** schema churn whose end state is covered in DATA-11, DATA-16 and DATA-22, or belongs to the logs and secrets domain.
- **Avatar and project-icon storage (`AvatarStorageService`, `ProjectIconStorageService`, `TeamAvatarStorageService`, `avatar_storage_settings`):** a profile/UI feature, not data protection. The only overlap is reuse of DATA-06 destinations (route to the UI/Teams agent).
- **Coolify Cloud user cleanup writing `cloud-deleted-users.csv` to the backups disk:** Coolify Cloud only.
- **`AtomicFilesystem` reason (PHP-FPM/OPcache empty cache files):** PHP-specific. The general atomic-write principle is captured in DATA-20.
- **Helper image SHA256 verification and Docker CLI/Compose/Buildx updates:** belongs to the Servers/Runtime agent.
- **Service-template backups (Odoo all-DB default, Bugsink binlog, Portabase template):** template catalog content (route to the Templates agent). The Vessel service model has no "service database" sub-resource yet. Service-DB backups need DATA-07 plus a service DB model.
- **Backup execution message redaction behind `read:sensitive` (#12151), `delete` ability (v4.6.0), backup endpoints scoped by DB type (#12279):** API token ability semantics are owned by the API agent. DATA-14 only records the dependency.
- **PostgreSQL restore program-execution scanning (#12333), init-script filename containment (v4.6.0), S3 endpoint DNS pinning/SSRF (`SafeWebhookUrl`), quoting of container names and paths in backup commands (#11970-#11989), encrypting queued remote commands:** owned by the Security agent. They are cross-referenced in DATA-02, DATA-06 and DATA-12.
- **MCP tools (`ListDatabaseBackups`, `ManageBackup`, `ManageS3Storage`, `GetDatabaseImport`, `ManageStorage`):** owned by the API/MCP agent. They depend on DATA-06, DATA-10 and DATA-14 contracts.
- **Traefik `acme.json` backups (`ListTraefikAcmeBackups`/`RestoreTraefikAcmeBackup`):** a proxy domain item (route to the Proxy agent).

#### Could not verify
- The exact `variable_fs_path` semantics (unreleased commit `171eae00a`, after v4.7.6) were read from the commit subject and schema only.
- The cascade-on-schedule-delete migration (`2026_11_15_120000`) is also unreleased on upstream main.
- Upstream volume-backup UI details (Livewire) were not read line by line. Behavior was taken from the jobs, models, migrations and release notes.

## 13. SRV / SCAN — Servers, Monitoring, Logs, Terminal and Scanning

### Summary

Between v4.1.0 and v4.7.6 Coolify moved a lot of server work into a mandatory on-host agent. Sentinel became mandatory on regular servers in v4.3.19. It pushes container state, holds metrics and traffic rollups, and moved its token into an env file in v4.7.0. Upstream also added a fleet Metrics page with CPU, memory, disk, network and load charts. New host management covers patching, automatic security updates, reboot, Sysbox, GPU detection, key rotation and Docker registry logins. Other additions:

- More cloud providers: DigitalOcean, Vultr and Hostinger, plus Hetzner extras.
- Background server transfers between instances, using a versioned JSON bundle.
- A CloudWatch log drain and validation of custom Fluent Bit configs.
- Runtime log improvements: replica merging, text view, and exports with secrets hidden.
- Short-lived terminal session tokens and a `docker attach` console.
- A large container and host vulnerability-scanning subsystem (Syft and Grype run on the server), with an optional deploy gate.

**Vessel today:** Vessel is modelled on v4.1.0, so its server and operations layer is still a scaffold. All runtime, health and terminal operations run on the Vessel host, whatever the server's address or connection type. There is no private-key model, no remote validation, and no host metrics: CPU, memory and disk are always `null`. Vessel has no runtime or container logs, no container terminal, no Docker cleanup, no log drains, no provisioning and no scanning. The server details page is a static placeholder.

Two existing behaviours are correctness or security defects:
- Remote servers are reported "Reachable" based on the *local* Docker daemon.
- A terminal opened for *any* server spawns `/bin/sh` on the control-plane host, and that shell inherits the daemon's environment.

**Counts:** P0: 1 · P1: 7 · P2: 9 · P3: 2 · N/A: listed below.

### Gap items

#### SRV-01: Server-targeted operations silently run on the Vessel host (health, connectivity, terminal)
- **Upstream:** Long-standing behaviour, hardened in v4.4.0 (#11942, terminal session tokens), v4.3.15 ("Restricted web terminal SSH commands to approved hosts, keys, ports, and options") and v4.7.0 ("Ended terminal sessions when access to the server is lost", fixed SSH options on every connection). Key files: `app/Actions/Server/ValidateServer.php`, `app/Services/TerminalSessionService.php`, `app/Http/Middleware/CanAccessTerminal.php`.
- **Coolify behavior (now):**
  - Every server operation goes over SSH to that server's `ip:port` with that server's private key. Localhost counts as a server: Coolify SSHes from its container to the host and never into its own runtime.
  - Validation really probes the server: OS, Docker Engine of at least `24.0` (`constants.docker.minimum_required_version`), and Compose.
  - A terminal issues a 60 s single-use token. The token is redeemed for a `docker exec`/`docker attach` or SSH command toward the target. The session is re-checked while it runs and is capped at 8 h.
  - Terminals are limited to team admins and owners, and each server has a terminal-access toggle.
- **Vessel today:** **Drifted (defect)**:
  - `ServerHealthPollingService.RuntimeTarget` builds `new ContainerRuntimeTarget(provider)` with no endpoint or host (`src/Vessel.Application/Monitoring/ServerHealthPollingService.cs:145-152`). Every SSH server is therefore probed against the local daemon (`:67-74`) and marked `Reachable` when local Docker answers.
  - `TestServerConnectivityAsync` marks the server `Reachable` without probing and writes a snapshot with fixed values, `proxyHealthy=true` and `certificatesHealthy=true` (`src/Vessel.Application/Resources/ResourceManagementService.cs:213-230`).
  - `TerminalSessionManager.BuildCommand` returns `TerminalSecurityPolicy.Default.DefaultShellCommand` (`/bin/sh`) for any server target (`src/Vessel.Application/Terminals/TerminalSessionManager.cs:196-202`, `src/Vessel.Application/Terminals/TerminalModels.cs:51-55`). The bridge then spawns it locally (`src/Vessel.Infrastructure/Processes/InteractiveTerminalProcessBridge.cs:33-44`). For an SSH server the operator gets a shell on the control plane, not the target.
  - `DotNetProcessRunner` only adds variables to the inherited environment (`src/Vessel.Infrastructure/Processes/DotNetProcessRunner.cs:280-281`). In host-native mode the shell can therefore read `SecretStorage:MasterKey` (`src/Vessel.Infrastructure/Security/AesSecretVault.cs:128`), DB connection strings, and `/etc/vessel` as the daemon user.
  - Deployments already fail closed for SSH (`src/Vessel.Application/Deployments/DeploymentRunner.cs:77-79`). Health and terminal do not.
- **Gap type:** Security hardening / Behavior change.
- **Priority:** P0. Wrong health state is persisted and broadcast, and the terminal hands out a control-plane shell with key-material exposure.
- **Recommended Vessel approach:**
  1. Fail closed right away, as `DeploymentRunner` does:
     - Health polling: for `ConnectionType.Ssh` servers, record `ServerStatus.Unknown` with reason "remote runtime execution not yet supported" instead of probing locally.
     - Connectivity test: do a real probe through `ISshClient.TestConnectionAsync` plus `GetInfoAsync`, or return "not supported". Never assume `Reachable`, and never fabricate proxy or certificate health.
     - Terminal: reject `Server` targets whose connection type is not `Local`.
  2. For local terminals, stop spawning a shell as the daemon user. Options: `ssh` to `localhost` with a dedicated operator key (Coolify's model); `systemd-run --uid=<ops-user> --pty`; or `machinectl shell`. All of them go through `IProcessRunner`, with a scrubbed environment (`ProcessCommand` gains `InheritEnvironment=false` plus an allowlist such as `PATH`, `TERM`, `LANG`).
  3. Add an architecture test that the terminal bridge never inherits secrets.
  4. Cross-reference the Security agent for SSH option and host validation (an un-validated `ServerAddress.Host` can carry a `-o...` option injection, `src/Vessel.Domain/ValueObjects/ServerAddress.cs:9-14`).
- **Roadmap fit:** 12.07, 12.11, 7.05 (re-open); 15.01/15.12.
- **Effort:** S (fail-closed and env scrub), then covered by SRV-02.

#### SRV-02: Remote server execution foundation: private keys, real validation and install, server lifecycle API
- **Upstream:**
  - v4.2.0: dedicated server, cloud-credential and private-key flows; POST-only `/servers/{uuid}/validate`.
  - v4.4.0: Alpine `apk` support (#6189); `SSH_COMMAND_TIMEOUT=0` no longer disables the time limit (default 3600 s).
  - v4.4.4: IPv6 addresses (`2026_10_09_092903_normalize_ipv6_server_ips`).
  - v4.5.0: OS derivatives accepted via `ID_LIKE`; validation works without a proxy (#11048); Docker Engine version failures explained (#11089).
  - v4.7.0: Ed25519 key generation and stricter key validation.
  - Docker and Compose versions persisted (`2026_08_13_085439_add_docker_version_to_server_settings_table`: `docker_version`, `docker_version_checked_at`, `compose_version`, `compose_version_checked_at`).
  - Key files: `app/Actions/Server/ValidateServer.php`, `app/Jobs/ValidateAndInstallServerJob.php`, `app/Models/Server.php::validateDockerEngineVersion`, `routes/api.php:169-226`.
- **Coolify behavior (now):**
  - A server references a team-owned `PrivateKey`, and only keys of the same team can be selected.
  - Validate-and-install runs as a job: SSH reachability; OS detection (`ID`/`ID_LIKE`, plus apt, dnf, zypper, apk and pacman package managers); Docker and Compose install when missing; minimum Docker 24; then Sentinel start.
  - Versions and validation logs are stored. Validation logs and the proxy config require `read:sensitive`.
  - API: `GET/PATCH/DELETE /servers/{uuid}`, `POST /servers/{uuid}/validate` (GET returns 405), `GET /servers/{uuid}/resources` and `/domains`, `POST /servers/{uuid}/host-key/reset`.
  - Keys still in use by servers cannot be deleted.
- **Vessel today:** **Missing**:
  - `Server` has no key reference, settings, versions or validation state (`src/Vessel.Domain/Servers/Server.cs:6-104`).
  - `SshConnectionOptions` only takes a `PrivateKeyPath` (`src/Vessel.Application/Ssh/SshModels.cs:22-29`). `ISshClient` is only registered, never consumed (`src/Vessel.Infrastructure/Extensions/InfrastructureServiceCollectionExtensions.cs`).
  - `/security/keys` is an empty-state page (`src/Vessel.Web/Components/Pages/SecurityKeys.razor:5-9`).
  - The Servers API has only list, create, `connectivity-test` and `snapshots`, with no PATCH or DELETE (`src/Vessel.Web/Controllers/Api/V1/ServersController.cs:22-44`).
  - `GetInfoAsync` returns versions, but they are never persisted (`src/Vessel.Infrastructure/Docker/DockerApiContainerRuntimeClient.cs:19-24`).
- **Gap type:** New feature / Schema / API contract.
- **Priority:** P1. Every remote feature in this report depends on it, and it is core Coolify parity.
- **Recommended Vessel approach:**
  - Domain: `SshKey` aggregate (team-owned, `SecretReferenceId` for the private material, public key, fingerprint, algorithm, `IsGitRelated`). `Server.SshKeyId`. A `ServerValidation` value object (os id/like/version, docker and compose versions with checked-at, last error, validated-at).
  - Application: `Servers/Validation/ValidateServerCommand`, run as a Hangfire job behind a per-server distributed lock. It needs `IRemoteHostExecutor`, which wraps `ISshClient` for SSH and `IProcessRunner` for local.
  - Infrastructure: material the SSH layer needs on disk goes to a validated `0600` file under `/var/lib/vessel/ssh/` and is deleted afterwards. `ContainerRuntimeTarget` gains `SshEndpoint` (`DOCKER_HOST=ssh://`, or the remote CLI via the SSH executor).
  - Install Docker only on explicit operator consent.
  - Add `PATCH`, `DELETE` and `POST /validate` server endpoints. Migration `Phase14xServerKeysValidation`.
  - Tests: OS detection parser (`ID_LIKE`), version gate, key-in-use delete guard.
- **Roadmap fit:** 7.04/7.05 (extend), new Phase 12b "Remote servers"; Security agent for key validation and host-key pinning.
- **Effort:** L.

#### SRV-03: Host and container metrics, fleet Metrics page, and the host-agent decision (Sentinel equivalent)
- **Upstream:**
  - v4.3.19: Sentinel mandatory (`2026_09_08_202212_enable_sentinel_for_existing_regular_servers`), toggle read-only.
  - v4.3.22: real-time `SentinelSynchronized`.
  - `2026_09_16_102243_add_sentinel_waiting_since_to_servers_table`.
  - v4.4.0: metrics settings moved to the Charts page; `--restart unless-stopped`; restart when not pushing; collision lock; Retry-After semantics.
  - v4.5.0: fleet Metrics page (#11867, #12162); disk, network and load charts.
  - v4.5.1 and v4.6.0: one SSH command per server, Sentinel-side bucketing (#12300, #12389), unreachable servers no longer block workers.
  - v4.7.0: token via env file; disk mount fallbacks (#12448).
  - Key files: `app/Http/Controllers/Api/SentinelController.php`, `ServerSentinelController.php`, `app/Actions/Server/StartSentinel.php`, `app/Services/SentinelMetricsClient.php`, `app/Services/FleetMetricsAggregator.php`, `app/Support/Metrics/PressureLevel.php`, `app/Jobs/ServerManagerJob.php`.
- **Coolify behavior (now):**
  - **Container and runtime:**
    - Sentinel is a container on every non-build, non-Swarm, non-transferred server (`Server::isSentinelEnabled()`).
    - It runs with `docker.sock` mounted, `--pid host --userns host`, a bind of `/data/coolify/sentinel` for its own database, and `--restart unless-stopped`.
    - Its config comes from an env file written `0600` and deleted after `docker run`.
  - **Push to Coolify:**
    - Every `PUSH_INTERVAL_SECONDS` (default 60) it pushes the full container list to `POST /api/v1/sentinel/push` with a Bearer token, which is an encrypted JSON blob containing `server_uuid`.
    - Coolify responds 401 for a bad token (Retry-After 3600), 410 when the server is unknown (Retry-After 86400), and 401 with Retry-After 300 when the server is not functional.
    - Each push updates the heartbeat (`sentinel_updated_at`) and clears `sentinel_waiting_since`. Coolify only dispatches `PushServerUpdateJob` when the container-state hash changes or a force window elapses.
    - A live heartbeat lets Coolify skip SSH connectivity checks.
  - **Metrics:**
    - Collection is optional (`is_metrics_enabled`, refresh rate, history days). Data is stored on the server and read via SSH `curl localhost:8888/api/...`.
    - The fleet page fetches all servers concurrently with one SSH command per server. It uses a per-server single-flight lock, a 60 s response cache, a 60 s cooldown after failure, and a 20 s remote timeout.
    - KPIs, per-bucket sums, and top-20 container ranking.
    - `PressureLevel` thresholds: warning at 70% and error at 90% for CPU and memory; 80% and 90% for disk.
  - **Settings API:** `GET/PATCH /servers/{uuid}/sentinel`. The token and custom URL require `read:sensitive`.
  - **Other:** Sentinel also ingests proxy access logs for traffic analytics (v4.4.0).
- **Vessel today:** **Missing**:
  - Snapshots always write `cpuLoadPercent: null, memoryUsedBytes: null, diskUsedBytes: null` (`src/Vessel.Application/Monitoring/ServerHealthPollingService.cs:100-109`).
  - Phase 12 explicitly defers this (`docs/deployment/phase-12-terminal-logs-monitoring.md`, Monitoring section; ROADMAP 12.11 notes).
  - `IContainerRuntimeClient` has no stats API (`src/Vessel.Application/Docker/IContainerRuntimeClient.cs:5-52`).
  - The server page shows hard-coded values (`src/Vessel.Web/Components/Pages/ServerDetails.razor:9-14,19`).
  - A separate `Vessel.Agent` deployable is forbidden unless the user explicitly asks for it (`PROJECT_REQUIREMENTS.md:83`, `:2389-2396`).
- **Gap type:** New feature / Behavior change.
- **Priority:** P1. Server metrics and status are baseline expectations, and upstream now builds new features on the agent.
- **Recommended Vessel approach:**
  - Write an ADR "Host telemetry: agentless first, optional host-native agent later". Do **not** adopt the Coolify Sentinel image: it is third-party, needs host-root (`docker.sock` plus `--pid host`), and has had a CVE history (Sentinel 1.1.2 fixed CVE-2026-111943).
  - Phase A (agentless):
    - `Monitoring/HostMetrics/IHostMetricsCollector` in Application.
    - In Infrastructure, one batched SSH or local command reads `/proc/stat`, `/proc/meminfo`, `/proc/loadavg`, `/proc/net/dev` and `df -P` for the root and Vessel data mounts, plus a fallback list like upstream's `ROOT_MOUNTS`, together with `docker stats --no-stream --format '{{json .}}'`.
    - Parse it with pure, unit-tested parsers.
    - Persist to a `server_metric_samples` table (time-bucketed rows, plus a daily rollup and retention job), or to a PostgreSQL partitioned table.
    - Collect in a staggered Hangfire job (per-server minute offset, as upstream does for its hourly and weekly checks), with per-server single-flight, a short timeout, and a failure cooldown.
    - Pages: Fleet Metrics (server and container tables plus trend charts) and per-server charts (CPU, memory, disk, network, load), with `PressureLevel` thresholds as a Domain value object.
  - Phase B (only with explicit approval):
    - `Vessel.Agent` as a host-native systemd binary rather than a docker.sock container. It pushes container state and metrics to `POST /api/v1/agent/push`.
    - The token is a per-server hashed secret (not reversible encryption), delivered by an env file at `0600`. Version is reported by header.
    - Heartbeat-driven `AgentLiveness` (waiting-since / live / out of sync, with the last push error stored) suppresses SSH polling.
    - Retry-After contract mirrors upstream.
- **Roadmap fit:** 12.11/12.12 (re-open), 16.08; new ADR; Phase B is a new phase item.
- **Effort:** L (Phase A); XL (Phase B).

#### SRV-04: Resource runtime status reconciliation, restart limits, and stop notifications
- **Upstream:**
  - v4.3.3 (stale running states).
  - v4.3.6 (a complete empty Sentinel snapshot marks resources stopped).
  - v4.3.15 (#11566: keep exited containers visible after the restart limit, stop auto restarts, add retry-deploy and remove-container actions).
  - v4.3.17 (restart policies kept on stop).
  - v4.3.19 (`max_restart_count`, "Restart Limit Reached" notification).
  - v4.5.1 (#10761).
  - v4.7.0 (`NotifyResourceStoppedJob`: notify only if still stopped after 120 s; `ContainerStopped` notification removed; "Not in Compose file" state, #9591).
  - `ApplicationStopFailed` event; `RemoveContainerJob` (3 tries with backoff); `ResourceStatusRefresher` (inspects the started resource right away, 8 × 2 s health wait).
  - Key files: `app/Jobs/PushServerUpdateJob.php`, `app/Jobs/NotifyResourceStoppedJob.php`, `app/Jobs/RemoveContainerJob.php`, `app/Services/ResourceStatusRefresher.php`, `app/Services/ContainerStatusAggregator.php`.
- **Coolify behavior (now):**
  - Container states from Sentinel pushes, or from the SSH `ServerCheckJob` fallback, are reconciled to resource statuses such as `running:healthy`, `exited`, `degraded`, `restarting` and "Not in Compose file". A *complete* empty snapshot marks missing resources stopped.
  - Restart counts are tracked. After `max_restart_count` Coolify stops the container, keeps it visible, and offers retry or remove.
  - An unexpected stop (not caused by Coolify within 300 s plus the stop timeout) waits 120 s, re-checks, and only then sends a "resource stopped" notification.
  - A stuck container removal is retried in the background instead of blocking a deploy.
- **Vessel today:** **Missing**:
  - Applications and services carry no runtime status. Only `DatabaseResource.HealthState` exists (`src/Vessel.Domain/Databases/DatabaseResource.cs:51,75`).
  - `IContainerRuntimeClient.StreamEventsAsync` exists (`src/Vessel.Application/Docker/IContainerRuntimeClient.cs:44`) but nothing in Application consumes it.
  - Health polling only counts running containers (`ServerHealthPollingService.cs:69-72`).
- **Gap type:** New feature / Behavior change.
- **Priority:** P1. "Is my app running?" is core UX, and notification semantics changed upstream.
- **Recommended Vessel approach:**
  - Domain: `ResourceRuntimeState` value object (state, health, restart count, `LastSeenAt`, `RestartLimitReached`) on Application, Service component and Database.
  - Application: `Resources/Status/ReconcileServerContainersCommand`. Input is a full container snapshot (`IsComplete` flag) from polling or a future agent, mapped by Vessel labels. Raises a `resource.stopped` notification through a delayed Hangfire job (120 s) that re-checks first.
  - `RefreshResourceStatusAfterStart` is called by the deployment and start flows.
  - `RemoveContainer` runs as a retryable job.
  - Cross-reference the Deployments agent for restart-limit settings and the stop-failed event, and the Notifications agent for event keys.
- **Roadmap fit:** 12.13 (extend); new item under Phase 12b.
- **Effort:** M.

#### SRV-05: Runtime (container) logs for applications, databases and services: API, UI, merging, safe export
- **Upstream:**
  - v4.2.0 (#6293: database, service and container log API with timestamps).
  - v4.3.20 (`lines=all`).
  - v4.3.23 (#11884: preview runtime logs).
  - v4.4.0 (Time/Type/Message columns, JSON expand, virtualised rows; logs only for users who can update the resource; API needs `read:sensitive`).
  - v4.4.3 (#12101).
  - v4.5.0: text view; Deployment and Service selectors with replicas merged into one stream (discussion #12176); log exports with secrets hidden.
  - v4.5.1: stopped-container logs via API; invalid UTF-8 (#10628).
  - Key files: `app/Support/RuntimeLogTargets.php`, `app/Services/Security/SensitiveDataRedactor.php`, `app/Traits/HasLogRedactionSetting.php`, `routes/api.php:295-296,372,442,461,475`.
- **Coolify behavior (now):**
  - `GET /applications|databases|services/{uuid}/logs` (plus service sub-resources and preview PR logs) take `lines` (number, `all`, or `-1`) and `timestamps`, and require `read:sensitive`.
  - `RuntimeLogTargets` groups containers by deployment (production first, then previews newest-first) and by service. Every container of a service, across servers and leftover rollout containers, becomes one merged stream.
  - Exports redact application secrets.
- **Vessel today:** **Missing.** Only deployment build logs exist (`src/Vessel.Web/Controllers/Api/V1/DeploymentsController.cs:46,63`). `IContainerRuntimeClient` has no logs method (`src/Vessel.Application/Docker/IContainerRuntimeClient.cs:5-52`).
- **Gap type:** New feature / API contract.
- **Priority:** P1. Runtime logs are among the most-used operator features.
- **Recommended Vessel approach:**
  - Add `IContainerRuntimeClient.StreamLogsAsync(target, containerId, since, tail, timestamps, follow)`, decoding invalid UTF-8 leniently.
  - Application: `Resources/RuntimeLogs/RuntimeLogQueryService`. It resolves containers by Vessel labels, merges replica streams by timestamp, groups like `RuntimeLogTargets`, and pipes every line through `ISecretRedactor` with the resource's secret values. Require a new `resources.readLogs` permission that is not granted to Member, matching upstream's "users who can update" rule.
  - Live tail flows over SignalR resource groups. Export is a redacted text download.
  - API: `GET /api/v1/{applications|databases|services}/{id}/logs?lines=&timestamps=`.
- **Roadmap fit:** 12.01/12.04 (extend to runtime logs).
- **Effort:** M.

#### SRV-06: Container terminal (PTY exec/attach), short-lived session tokens, and continuous re-authorization
- **Upstream:**
  - v4.3.1 (#11203).
  - v4.3.11 and v4.3.19 (same-named containers on different servers).
  - v4.3.15 (approved hosts, keys, ports and options only).
  - v4.3.18 (#11638: keep PATH).
  - v4.4.0 (#11942: tokens; error instead of an endless spinner).
  - v4.4.3 (#12110).
  - v4.5.0 (#11538: `docker attach` console; #12230: no CPU leak after close).
  - v4.5.1 (custom cookie and XSRF fixes, cloudflared token, #12312).
  - v4.6.0 (console with the last 200 log lines and a line-send box; `-i`/`-it` keeps stdin open; fallback when the user's shell is `nologin`/`false`).
  - v4.7.0 (end sessions when access is lost).
  - Key files: `app/Services/TerminalSessionService.php`, `routes/web.php:358-413,604-613`.
- **Coolify behavior (now):**
  - **Token flow:**
    - `issue()` creates a single-use token with a 60 s TTL, bound to user, server, container, mode (`shell` or `attach`), IP and user agent.
    - The terminal server redeems it and gets `{command, session_id}`. The command is built server-side.
    - The terminal server calls `/terminal/session/check` periodically. That check re-evaluates permission against the *server's* team, not the session's team. `/terminal/session/end` records the end, including on logout.
  - **Limits and access:**
    - Live sessions are capped at 8 h.
    - Access requires team admin or owner and the per-server `is_terminal_enabled` setting.
  - **Attach console:** works for containers with stdin open, shows the last 200 log lines, and sends whole lines.
- **Vessel today:** **Partial**:
  - Session model, authorization, audit, hub forwarding and a max lifetime exist (`src/Vessel.Application/Terminals/TerminalSessionManager.cs:25-60`; `docs/security/terminal.md`).
  - Container targets are rejected (`src/Vessel.Infrastructure/Processes/InteractiveTerminalProcessBridge.cs:19-20`).
  - There is no PTY, and resize is a no-op (`:77-83`).
  - The container command is built by string concatenation, `$"docker exec -it {containerName} sh"` (`TerminalSessionManager.cs:198-199`), which violates the structured-argument rule.
  - No re-authorization runs during a session beyond per-input permission checks, and there is no per-server terminal toggle.
- **Gap type:** New feature / Security hardening.
- **Priority:** P1. The container terminal is core parity, and the current construction must be fixed before it is enabled.
- **Recommended Vessel approach:**
  - Infrastructure `PtyTerminalBridge` using a real PTY (`forkpty` via a small native shim, or `script -qfc`, or `ssh -tt` through `IProcessRunner.StartInteractiveAsync` with a PTY option). Real resize via `TIOCSWINSZ` or `stty`.
  - Container mode: `docker exec -it <id> <shell>` with structured args and a shell fallback probe (`bash`, `sh`, `ash`). Attach mode: `docker attach --sig-proxy=false`, then replay `docker logs --tail 200`.
  - Application: a periodic `TerminalSessionRevalidator` (Hangfire or `PeriodicTimer` in the bridge host) re-checks `terminals.open` plus server access and closes the session on loss. Add a per-server `TerminalAccessEnabled` setting.
  - The SignalR hub already avoids sending commands from the browser. Keep it that way: the browser sends only target ids and the command is derived server-side, which is equivalent to upstream's token model.
  - Cross-reference the Security agent (session tokens, CSRF).
- **Roadmap fit:** 12.07–12.10 (re-open the container PTY).
- **Effort:** M.

#### SRV-07: Docker cleanup (scheduled, threshold-based, safe) and the server Images page
- **Upstream:**
  - v4.3.7 (#8831: never delete `coolify.managed=true` images).
  - v4.3.9 (stuck removal deferred).
  - v4.3.10 (#11408: failed or timed-out executions marked finished with an error).
  - v4.3.11 (#11463: retry-safe).
  - v4.3.17 (orphaned helper containers).
  - v4.3.22 (execute once per occurrence).
  - v4.4.0: Images page (#11689); cleanup on stop/delete only at or above the threshold and when none ran in the last hour, one per server; own scheduler; a manual run waits instead of being dropped.
  - v4.4.4 (preserve app, database, service and proxy containers).
  - v4.5.0 (Docker 29 image removal; executions orphaned by a killed worker fail).
  - v4.5.1 (#11069 keep new images; #12183 skip scheduled cleanups during deployments).
  - v4.6.0 (remove only helpers of finished deployments).
  - v4.7.0 (failure notifications: first failure, then daily, plus "works again"; never auto-disabled).
  - Indexes `2026_10_28_211105`, `2026_10_28_214953` (interrupted executions).
  - API `routes/api.php:178-181`. Key files: `app/Actions/Server/CleanupDocker.php`, `app/Http/Controllers/Api/ServerDockerCleanupController.php`.
- **Coolify behavior (now):**
  - Per-server settings: `docker_cleanup_frequency` (cron), `docker_cleanup_threshold` (1–99 %), `force_docker_cleanup`, `delete_unused_volumes`, `delete_unused_networks`, `disable_application_image_retention`.
  - The cleanup prunes stopped containers carrying the managed label (excluding proxy, app, database and service containers), unused images (keeping managed-label images and the retained app image tags), builder cache, and optionally volumes and networks.
  - Each execution is recorded with status and log.
  - API: `GET/PATCH /servers/{uuid}/docker-cleanup`, `POST .../run` (needs the deploy ability), `GET .../executions`.
  - The Images page lists image size, age, the containers using each image, and total vs reclaimable space. Deleting requires typing the reference.
- **Vessel today:** **Missing.** No cleanup, prune or image-management code exists: `rg prune|DockerCleanup` hits only backup and log retention (`src/Vessel.Application/ManagedServices/ManagedDatabaseService.cs:467`, `DeploymentLogRetentionService`). `ListImagesAsync` exists but is unused by any feature (`src/Vessel.Application/Docker/IContainerRuntimeClient.cs:21`).
- **Gap type:** New feature.
- **Priority:** P1. Disks filling with images and build cache is the most common self-hosting outage.
- **Recommended Vessel approach:**
  - Domain: `DockerCleanupPolicy` (owned by the server) and a `DockerCleanupExecution` entity (status, started/finished, reclaimed bytes, redacted log, error).
  - Application: `Servers/DockerCleanup/RunDockerCleanupCommand`. It takes a per-server distributed lock (a manual run waits), skips while a deployment is active on the server, and is idempotent.
  - Infrastructure: implement through `IContainerRuntimeClient` prune and remove methods. Docker.DotNet has `Images.PruneImagesAsync`/`Containers.PruneContainersAsync` with label filters. Protect `vessel.managed=true` and the current or rollback images.
  - Startup sweep marks interrupted executions failed.
  - Staggered recurring job per server. Images page with typed-reference delete.
  - API mirrors upstream (POST for run).
- **Roadmap fit:** New Phase 12b item; 16.04.
- **Effort:** M.

#### SRV-08: Disk-usage and reachability alerts (event producers for server notifications)
- **Upstream:**
  - v4.4.0: disk alerts repeat at most once per `server_disk_usage_notification_interval_hours` (default 24) per server, and re-arm early when usage drops at least 5 points below the threshold and rises again (`2026_09_29_082403_...`).
  - v4.3.10: server status indicators warn on pending proxy update and unavailable Sentinel.
  - v4.7.0: scheduled-failure notification throttling.
  - Server unreachable and reachable, force-enable and force-disable notifications.
  - Key files: `app/Jobs/ServerStorageCheckJob.php`, `app/Notifications/Server/*`.
- **Coolify behavior (now):**
  - Storage is checked on `server_disk_usage_check_frequency` (cron), or from Sentinel pushes when live.
  - When usage is at or above `server_disk_usage_notification_threshold`, Coolify sends one alert per interval.
  - Unreachable is notified after repeated failed checks (`unreachable_count`), and reachable again on recovery.
- **Vessel today:** **Missing:**
  - Notification routing for `server.*` exists (`src/Vessel.Application/Notifications/NotificationDispatchService.cs:139`), but nothing raises events. The only `RaiseAsync` caller is the manual API (`src/Vessel.Web/Controllers/Api/V1/NotificationsController.cs:68`).
  - Status changes emit only a domain event (`src/Vessel.Domain/Servers/Server.cs:93-103`) with no handler.
  - Disk is never measured (SRV-03).
- **Gap type:** New feature / Behavior change.
- **Priority:** P1. Phase 13 delivered channels without the server events operators rely on.
- **Recommended Vessel approach:**
  - Application `Servers/Alerts/ServerAlertEvaluator`, run after each health or metrics sample. It holds hysteresis state (`LastDiskAlertAt`, `DiskAlertArmed`) in a `ServerAlertState` table, uses a per-server interval (default 24 h) and the 5-point re-arm, and raises `server.disk_usage_high`, `server.unreachable` (after N consecutive failures) and `server.reachable`.
  - Handle `ServerStatusChangedEvent` through an Application domain-event dispatcher.
  - Cross-reference the Notifications agent for the event catalog.
- **Roadmap fit:** 13.x follow-up; 12.11.
- **Effort:** S–M.

#### SRV-09: Server roles (deployment / build / both), build fallback, and build server restrictions
- **Upstream:**
  - v4.2.0 (#10961: build-server hosting restrictions).
  - v4.4.0 "Server roles": `ServerRole` enum `deployment|build|both` with `canBuild()`/`canDeploy()`; `server_role` on `server_settings`, default `both`, with `is_build_server=true` migrated to `build` (`2026_09_19_121840_...`); team `is_build_server_fallback_enabled` default `true` (`2026_09_18_165916_...`); `is_build_server` deprecated but accepted.
  - v4.7.0 (#7649: the concurrent build limit counts on the build server; a deployment reserves a free slot, and freeing a slot starts waiting deployments of other servers).
  - Key files: `app/Enums/ServerRole.php`.
- **Coolify behavior (now):**
  - Deployment-only servers never build, so apps on them need an image name.
  - Build-only servers host no resources and get no Sentinel.
  - Builds fall back to the deployment server when no build server is usable, if the team allows it.
- **Vessel today:** **Missing.** The `Server` has no role (`src/Vessel.Domain/Servers/Server.cs:32-52`), and there is no build-server concept anywhere (`rg -i buildserver` returns nothing).
- **Gap type:** Schema / Behavior change.
- **Priority:** P2. The schema is cheap to add now and avoids a later data migration. The behaviour depends on remote execution.
- **Recommended Vessel approach:**
  - Domain enum `ServerRole { Both, Deployment, Build }` with `CanBuild` and `CanDeploy` invariants. `Server.ChangeRole` rejects `Build` while resources are hosted.
  - Team setting `BuildServerFallbackEnabled = true`.
  - Application placement checks in resource creation and deployment.
  - Build-slot reservation via a per-build-server semaphore in Redis or PostgreSQL. Cross-reference the Deployments agent.
- **Roadmap fit:** New, Phase 12b/Deployments.
- **Effort:** S (schema), M (scheduling).

#### SRV-10: SSH key lifecycle: rotation across servers, authorized_keys management, unused key cleanup
- **Upstream:**
  - v4.2.0 (keys in use cannot be deleted).
  - v4.4.0 (only same-team keys; keys kept out of the browser).
  - v4.5.1 (#12278: keep operator keys that mention "coolify").
  - v4.7.0: "Rotate key" on private keys (`RotatePrivateKeyJob`, `RemoveRotatedAuthorizedKeyJob`, `app/Actions/Server/AuthorizedKeys.php`); a server delete can remove the key from `authorized_keys`; `CleanupUnusedSshKeys` runs hourly (`app/Console/Kernel.php:53-55`); Ed25519 generation.
  - Index `2026_10_28_232000_add_index_for_active_servers_by_private_key`.
- **Coolify behavior (now):**
  - Rotation adds the new public key to `~/.ssh/authorized_keys` on every server using the old key. Each server moves only after the new key logs in, and failed servers stay on the old key so the rotation can re-run.
  - The old key is removed later, and the rotation can be undone.
  - Per-server results (`moved_old_pending`, `moved_old_not_removed`, `failed`, `old_key_not_found`, `unreachable`, `skipped`) are kept for 24 h. SSH errors are sanitised of host addresses for the UI.
  - Both `add` and `remove` are idempotent.
- **Vessel today:** **Missing.** There is no key entity (see SRV-02).
- **Gap type:** New feature / Security hardening.
- **Priority:** P2. This is an operational security feature that builds on SRV-02.
- **Recommended Vessel approach:**
  - Application `Security/SshKeys/RotateSshKeyCommand`, run as a Hangfire job. It loops over servers with per-server state persisted in `ssh_key_rotation_items`.
  - Infrastructure `AuthorizedKeysEditor`: an idempotent add or remove via an SSH script with structured arguments that matches the full key line, never a substring.
  - Delete guard for keys in use. Hourly cleanup of materialised key files under the owned SSH directory.
  - Cross-reference the Security agent for key validation and host-key pinning.
- **Roadmap fit:** New (Phase 15 adjacent).
- **Effort:** M.

#### SCAN-01: Container image and host package vulnerability scanning (scanner pipeline, SBOMs, rescans, results)
- **Upstream:**
  - v4.7.0 ("Container image vulnerability scanning, opt-in for each server and each application"; API `GET/PATCH /api/v1/servers/{uuid}/image-scanning`, `GET /api/v1/vulnerabilities`, `GET /api/v1/{applications|databases|services}/{uuid}/vulnerabilities`; SBOM download `routes/web.php:454,479,500`).
  - v4.7.1 (error log; scanner templates missing).
  - v4.7.2 (scanner memory `image_scan_memory_mb`, default 1024; disk usage `scanner_disk_used_bytes`).
  - v4.7.3 (Export as Markdown, CSV or JSON; `image_scan_syft_timeout`/`image_scan_grype_timeout` default 600 s with a maximum of 3600; `image_scan_cpus` default 1.0, capped to the server's CPUs, and the API refuses higher values).
  - Migrations `2026_10_29_100000_create_image_scanning_tables` (`image_scans`, `image_sboms`, `image_scan_policies`, `image_scan_ignores`, `image_scan_notification_states`, server, team and app flags), `2026_11_06_145819_create_server_host_scans_table`, `2026_11_08_120000_drop_image_scanning_columns_from_instance_settings`, `2026_11_09_*`.
  - About 40 files in `app/Services/ImageScan/*`. Jobs: `ScanResourceImagesJob`, `ImageRescanJob`, `ScanHostPackagesJob`, `PrepareImageScannerJob`, `RefreshImageScanSummariesJob`. Actions: `ScanImage`, `ScanHostPackages`, `RefreshCandidates`. Parsers: `GrypeReportParser`, `SyftPackageParser`. Enums: `ImageScanStatus`, `ImageScanErrorCode`, `SecurityStatusType`, `SecurityActionType`, `ImageScanFixState`.
- **Coolify behavior (now):**
  - **Triggers:**
    - Opt-in per server (`is_image_scan_enabled`, default false; team `scan_images_on_new_servers`). Per application, `is_image_scan_enabled` defaults to true once the server opts in.
    - `ScanResourceImagesJob` runs after an app deploy (unless the gate already scanned), and after a service or database start (30 s delay, retries while the image is still pulling).
    - "Scan now" pulls missing images. It scans at most 5 derived images per resource and retries on a busy lock (60/120/300 s, 10 attempts).
    - A daily staggered `ImageRescanJob` per server updates the Grype database and re-runs Grype on stored SBOMs of running images, without re-running Syft. It also triggers the host scan.
  - **Where scanners run:**
    - Everything runs *on the managed server*: Syft, Grype and regctl containers pinned by sha256 digest (`config/constants.php:93-94`, e.g. `anchore/grype` 0.121.0), with no third-party data upload.
    - Containers are hardened: `--cap-drop ALL --security-opt no-new-privileges --read-only --pids-limit 256 --user 65534:65534 --cpus N --memory M --memory-swap M --log-driver none`, and `--network none` except for registry and DB fetches.
    - Data lives under `/data/coolify/image-scans` (SBOMs named by image id) plus a `coolify-grype-db` volume. An exit-code protocol separates timeout (124) from OOM (137 plus a marker).
    - Registry auth reuses the server's `~/.docker/config.json` entry for a single registry, which is never stored.
  - **Results:**
    - Per resource and image id, with status `queued|running|completed|failed` and `error_code` (`disk_space`, `db_too_old`, `timeout`, `out_of_memory`, `lock_busy`, `registry_auth`, `truncated`, ...).
    - Findings include KEV/EPSS, fix state and package, plus SBOM packages (truncation flag) and a summary (incl. `eol_distro`).
    - Each resource gets a single `SecurityStatus`: `not_checked|check_needed|update_now|update_available|no_fix|ok`.
    - Actions are computed: `update_image|pull_latest|change_base|rebuild|upgrade_in_dockerfile|upgrade_package|rebuild_in_ci`.
    - `RefreshCandidates` uses regctl to find newer builds and tags of the same line, scans them (capped per image and per server per day), and checks the Dockerfile base image.
    - `ServerHostScan` scans host OS packages (dpkg, rpm, apk and alpm catalogers on a copied package database) into the server's Security > Vulnerabilities page.
- **Vessel today:** **Missing.** Nothing in `src/**` mentions scan, vulnerability or SBOM. ROADMAP 15.15 covers only CI scanning of Vessel's own artifacts.
- **Gap type:** New feature / Schema / API contract.
- **Priority:** P2. This is a differentiating, high-value feature, but it is not core parity, it needs SRV-02 and SRV-07, and it is a very large subsystem.
- **Recommended Vessel approach:**
  - Domain (`Domain/Security/ImageScanning`): `ImageScan` aggregate (status machine, error code, summary), `ImageSbom` (per server and image id), `HostPackageScan`, `Finding` value object (id, aliases, severity, CVSS, EPSS, KEV, package, type, fixed-in), `SecurityStatus` and `SecurityAction` enums, plus a pure `SecurityRules` (exploited when KEV; likely when EPSS ≥ 0.10 and CVSS ≥ 7.0 or high/critical).
  - Application (`Security/ImageScanning/*`): `ScanResourceImages` and `RescanServerImages` use cases, plus `IImageScanner` (Syft SBOM, Grype match, tag listing) and `IVulnerabilityFeed`.
  - Infrastructure: `DockerScannerRunner` builds the hardened `docker run` through `IProcessRunner` or the remote executor, using digest-pinned images from config with a configurable registry mirror. Grype and Syft JSON parsers are pure and golden-tested.
  - Store findings as `jsonb` behind EF owned JSON or a raw-SQL read model, and SBOMs compressed.
  - Hangfire queues: a `scans` queue below `deployments` priority (upstream moved scans off its high queue for this reason). Per-server scan lock. Daily staggered rescan job.
  - Server settings: memory, CPU and timeout limits, validated against the server's capacity.
  - API: `GET/PATCH /api/v1/servers/{id}/image-scanning`, `GET /api/v1/vulnerabilities`, `GET /api/v1/{resource}/{id}/vulnerabilities`, SBOM download (CycloneDX/SPDX via `syft convert`), and CSV/JSON export.
  - Phase it: (1) scan after deploy and results UI, (2) daily rescan and host scan, (3) candidate tags and base-image recommendations.
- **Roadmap fit:** New phase ("Supply-chain security"); relates to 15.15.
- **Effort:** XL.

#### SCAN-02: Scan policies, deploy gate (off/on/strict), ignores, and vulnerability notifications
- **Upstream:**
  - v4.7.0. Key files: `app/Services/ImageScan/SecurityGate.php`, `DeploymentImageScan.php`, `ImageScanPolicyResolver.php`, `IgnoreMatcher.php`, `ScanNotifications.php`; `app/Jobs/ImageScanDigestJob.php`, `ImageScanAlertCheckJob.php`; notifications `ImageScan/ExploitedProblemFound`, `VulnerabilityDigest`.
  - Enums `ImageScanGateMode` (`off|on|strict`) and `ImageScanOnError` (`deploy|block`).
  - Columns `application_deployment_queues.scan_gate_result` (jsonb) and `scan_gate_bypassed_by`.
  - The `image_scan_policies` seed row: instance scope `gate_mode=off`, `on_scan_error=deploy`, `count_medium=false`.
- **Coolify behavior (now):**
  - **Policy resolution:** policies resolve Instance → Team → Server → App, where null inherits. They resolve through the main destination server, and the team comes from the resource, never the session.
  - **Gate placement:** the gate runs inside the deployment before containers are replaced. For Compose it gates every image before stopping the running containers and pins checked images by repo digest so Compose cannot pull different ones. It waits for the per-server scan lock in steps with cancellation checks.
  - **Gate modes:**
    - **On** blocks only when the new image adds a relevant exploited or likely finding that the running image does not have. If no baseline scan exists, it only warns and this deploy becomes the baseline.
    - **Strict** also blocks any relevant finding with a package fix (medium findings count when the rule says so or EPSS ≥ 0.10) and end-of-life OS images.
    - A scan error or truncated report follows `on_scan_error`.
  - **Recording and bypass:** the result is stored on the deployment and written to the deployment log ("Image scan: passed" or "Deploy blocked: ..."). Bypass is recorded by user.
  - **Ignores and notifications:**
    - Ignores are per team and optionally per resource, by vulnerability id and/or package, with a reason and an optional expiry.
    - Notifications: an immediate alert for exploited problems (after the version check, or a timed fallback), plus a daily per-team digest (newly `update_available`/`check_needed`, ignores expiring within 7 days, servers whose rescans failed 2 days running). Per-channel toggles; a Telegram thread id.
- **Vessel today:** **Missing.**
- **Gap type:** New feature / Behavior change (deploy pipeline).
- **Priority:** P2. It depends on SCAN-01, but the gate hook point should be designed into the deployment runner now.
- **Recommended Vessel approach:**
  - Domain: `ImageScanPolicy` (scope, nullable fields, `Resolve()` with inheritance), `ImageScanIgnore` (expiry invariant), and a pure `SecurityGate.Evaluate(newScan, runningScan, policy, ignores)` returning a `GateResult` record, unit-tested against the upstream rules.
  - Application: a `IDeploymentGate` step in `DeploymentRunner`, after build or pull and before container switch. It persists `GateResult` on `Deployment`. Bypass needs a new permission and is audited.
  - Notifications: `security.exploited_found` (immediate) and `security.digest` (daily per team via a staggered Hangfire job), plus per-resource dedup state.
  - Cross-reference the Deployments agent (gate placement, Compose digest pinning) and the Notifications agent.
- **Roadmap fit:** New phase (with SCAN-01).
- **Effort:** L.

#### SRV-11: Server patching: patch checks, automatic security updates, reboot, and removal of unused packages
- **Upstream:**
  - v4.4.0 (weekly patch checks staggered per server).
  - v4.5.0 ("server patching (beta) with automatic security updates per server, reboot and risky package warnings, and patch status in the server list").
  - v4.6.0 (patches badge).
  - v4.7.0 (consolidated server Security page).
  - Migrations `2026_10_19_082209_add_patch_check_to_server_settings_table` (`patch_check_frequency`, `patch_check_result` json, `patch_check_error`, `patch_checked_at`) and `2026_10_19_120000_add_auto_security_updates_to_server_settings_table` (`is_auto_security_updates_enabled` default false, `auto_security_updates_time` default `03:00`).
  - Key files: `app/Jobs/ServerPatchCheckJob.php`, `app/Actions/Server/{CheckUpdates,ConfigureAutomaticSecurityUpdates,RebootServer,RemoveUnusedPackages}.php`; events `ServerPatchCheckFinished`, `ServerPackagesUpdateFinished`; notification `Server/ServerPatchCheck`.
- **Coolify behavior (now):**
  - **Patch checks:** run at a configurable frequency (default weekly, staggered on Sunday). They detect upgradable packages per package manager:
    - apt phased updates are found by diffing two simulations, and holds are respected;
    - dnf4 and dnf5 are handled;
    - apk world pins are respected;
    - Arch gets a full upgrade only.
  - Results are flagged with reboot-required and risky-package warnings, and an "updates available" notification is sent (not for manual checks).
  - **Automatic security updates:** apt uses an `unattended-upgrades` drop-in (`/etc/apt/apt.conf.d/52coolify-unattended-upgrades`) restricted to security origins, with the time set by an `apt-daily-upgrade.timer` drop-in. Other distros use their equivalents.
  - **Reboot:** runs via `systemd-run` a few seconds later, then a patch check after 5 min.
  - **Cleanup:** "Remove unused packages" also removes old kernels while keeping the running one.
  - After an update, a re-check and a host vulnerability scan run.
- **Vessel today:** **Missing.**
- **Gap type:** New feature / Schema.
- **Priority:** P2. Valuable for operators who run many servers. It mutates hosts, so it needs explicit consent UX.
- **Recommended Vessel approach:**
  - Application `Servers/Patching/{CheckServerPatches, ConfigureAutomaticSecurityUpdates, RebootServer, RemoveUnusedPackages}`, all behind the per-server mutation lock and audited, with the dangerous ones confirmed by typed confirmation.
  - Infrastructure `PackageManagerProbe` strategies (apt, dnf, zypper, apk, pacman) with pure output parsers and golden tests built from captured outputs.
  - Store results in a `server_patch_checks` table rather than a JSON column, so the server list loads without the result, which upstream optimised later.
  - Notification event `server.patches_available`.
- **Roadmap fit:** New (Phase 12b / host management).
- **Effort:** L.

#### SRV-12: Log drains (New Relic, Axiom with edge domain, AWS CloudWatch, custom Fluent Bit with validation)
- **Upstream:**
  - v4.4-rc.1 (toggle rollback).
  - v4.4.0 (#10543: settings restored when enable fails).
  - v4.5.0 (#11972: CloudWatch `awslogs`; #12197: custom config validation; #12232: fields required only when enabled; #12239: names with whitespace).
  - v4.5.1 (Highlight removed and its containers stopped; Axiom edge domain; Fluent Bit 2.0 → 5.1 with restart on file change).
  - v4.7.0 (#8415: `fluentd` driver sends Coolify labels as a fallback).
  - Migrations `2026_09_24_055934_add_cloudwatch_log_drain_to_server_settings_table` (`is_logdrain_cloudwatch_enabled`, `logdrain_cloudwatch_region`, `logdrain_cloudwatch_group` (512), `logdrain_cloudwatch_stream_prefix`), `2026_10_27_155023_...axiom_domain` (default `us-east-1.aws.edge.axiom.co`), `2026_10_27_160258_remove_highlight...`.
  - API `GET/PATCH /servers/{uuid}/log-drains` (PATCH needs write plus deploy). Key files: `app/Rules/ValidFluentBitConfig.php`, `app/Actions/Server/StartLogDrain.php`, `bootstrap/helpers/shared.php:4660-4680`.
- **Coolify behavior (now):**
  - One drain is enabled per server. Containers use the `fluentd` log driver toward a `coolify-log-drain` Fluent Bit container, except CloudWatch, which sets the Docker `awslogs` driver directly per container (`awslogs-create-group=true`, stream named by container tag) with no Fluent Bit.
  - Custom configs are restricted to sections `SERVICE/INPUT/FILTER/OUTPUT/PARSER/MULTILINE_PARSER`, must contain a `forward` input, and deny the plugins `lua`, `wasm`, `exec`, `exec_wasi` and `file` and the key `plugins_file`. A parsers-only mode also exists.
  - Log-drain keys are hidden from members without update permission.
- **Vessel today:** **Missing** (no log-drain code or settings).
- **Gap type:** New feature / Security hardening (config validation).
- **Priority:** P2.
- **Recommended Vessel approach:**
  - Domain: `LogDrain` (server-owned; a provider discriminator `NewRelic|Axiom|CloudWatch|CustomFluentBit`; secret references for keys) with an invariant of at most one enabled.
  - Application: `Servers/LogDrains/ConfigureLogDrainCommand`, which redeploys or recreates affected containers lazily. `ILoggingDriverConfigurator` is consumed by the compose generator to emit the `logging:` stanza.
  - Infrastructure: Fluent Bit container lifecycle via `IContainerRuntimeClient`, and a `FluentBitConfigValidator` that ports the allowlist and denylist rules with tests.
  - For the Vessel daemon itself, journald plus OpenTelemetry already covers control-plane logs; keep that separate.
- **Roadmap fit:** New (Phase 12b / Ops).
- **Effort:** M.

#### SRV-13: Docker registry logins per server vs. Vessel's stored registry credentials
- **Upstream:**
  - v4.4.0 (#12048). Registries page plus a per-server tab listing the registries each server is logged in to and the registries its resources need. Presets: Docker Hub, GHCR, GitLab, GAR, ACR, ECR.
  - API `GET /servers/{uuid}/registries` (`read:sensitive`), `POST /servers/{uuid}/registries`, `POST .../{registry}/check`, `DELETE .../{registry}`.
  - v4.5.1 (#12216: use SSH-user registry credentials for host pulls).
  - v4.6.0 (check and logout need the `deploy` ability).
  - Key file: `app/Services/DockerRegistryLogins.php` (`REGISTRY_PATTERN` incl. bracketed IPv6).
- **Coolify behavior (now):**
  - Coolify runs `docker login` on the server and does **not** store the token. It reads `~/.docker/config.json` to list registries and usernames only, and reports `helper` for credential helpers.
  - The image scanner extracts a single-registry auth config transiently.
- **Vessel today:** **Partial / divergent:**
  - `RegistryCredential` stores the registry, username and a secret reference (`src/Vessel.Domain/Registries/RegistryCredential.cs:6-50`).
  - It is only creatable via `POST /api/v1/registry-credentials` (`src/Vessel.Web/Controllers/Api/V1/RegistryCredentialsController.cs:14`), and no deployment or pull path consumes it (`rg RegistryCredential` hits only persistence and resource code).
- **Gap type:** New feature / Behavior change.
- **Priority:** P2.
- **Recommended Vessel approach:**
  - Keep Vessel's encrypted-credential model, which is better for multi-server, rotation and audit. Apply it per operation through a temporary `DOCKER_CONFIG` directory (owned path, `0700`, deleted after use) rather than a persistent `docker login` on the host. Optionally offer "log in on server" for parity.
  - Add `ServerRegistryStatus` queries: needed registries derived from resource images vs. available credentials, plus a `check` action (`docker manifest inspect`/registry ping). Add list, update and delete endpoints and a registry host validator like the upstream pattern.
  - Cross-reference the Deployments agent for consumption during pull and push.
- **Roadmap fit:** 7.17 (complete), 8.x.
- **Effort:** M.

#### SRV-14: Import from Coolify's server-transfer bundle (Phase 17 path)
- **Upstream:**
  - v4.3.0 (#11075, `f9caa5250`: cross-instance server migration).
  - v4.5.0 (#12180: background transfers; bundle export requires update access).
  - v4.5.1 (security holes closed in import, claim and archives).
  - v4.6.0 (IPv6 host resolution).
  - v4.7.0 (transferred servers stay disabled).
  - Key files: `app/Services/ServerTransfer/{ServerTransferBundle,Exporter,Importer,Migrator,Claimer,Coverage,TransferFinishChecklist}.php`, `app/Http/Controllers/Api/ServerTransferController.php`, `app/Jobs/{MigrateServerToInstanceJob,CheckTransferFinishStepsJob}.php`, `docs/server-transfer-full-coverage.md`, `scripts/transfer-test/*`, routes `routes/api.php:220-224`.
- **Coolify behavior (now):**
  - **Bundle format:** a JSON bundle, `schema_version: 1`, with:
    - header fields `coolify_version`, `exported_at`, `export_id`, and `source_instance {url, name}`;
    - `server` (`uuid`, `name`, `ip`, `port`, `user`, `proxy`, `server_role`, cloud ids, `settings`), `private_key` (**plaintext material**), `destinations`, and `projects` (nested environments, applications, databases, services, previews, environment variables incl. preview and locked ones, storages, scheduled tasks and backups);
    - team dependencies `private_keys`, `github_apps`, `gitlab_apps`, `gitea_apps`, `s3_storages`, `cloud_provider_tokens`, `integration_tokens`, `dns_zones`, `managed_dns_records`, `ssl_certificates`, `image_scan_policies` and `image_scan_ignores`;
    - `shared_environment_variables {server, team}`, `warnings` and `post_transfer_checklist`.
  - **Limits and validation:** per-key maximums (e.g. 5000 applications, 50000 environment variables). The id pattern is `^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$`. Import is rejected when the bundle's `coolify_version` is newer than the target.
  - **Transport:**
    - The source calls the target's `GET /api/v1/servers/import/check`, then `POST /api/v1/servers/import` with `{bundle, dry_run, preserve_uuids=true, adopt_mode=true}` (a 201 or 200 dry-run response).
    - It then probes `GET /api/v1/servers/{uuid}/transfer`. The target token needs write plus deploy.
    - The target URL must be HTTPS unless it is localhost.
  - **Claim:** the target claims the host by writing `/data/coolify/transfer/owner.json` last, then rebinds Sentinel. The source marks the server transferred away and disables automations.
  - **Follow-up:** `TransferFinishChecklist` guides re-pointing webhooks, DNS, Sentinel, backups and runners.
- **Vessel today:** **Missing.** Phase 17 is not started (ROADMAP 17.01–17.13), and there are no import endpoints.
- **Gap type:** New feature / API contract.
- **Priority:** P2. This is the most practical migration path, since Coolify itself can push into a compatible target. It should shape the 17.03 "import format" decision.
- **Recommended Vessel approach:**
  - Decide in 17.03 to accept Coolify bundle `schema_version: 1` as a first-class import format, and pin the supported `coolify_version` range.
  - Two entry points:
    - an operator-uploaded bundle file (CLI `vessel import coolify-bundle <file> --dry-run`, plus a UI);
    - optionally, Coolify-compatible `GET /api/v1/servers/import/check`, `POST /api/v1/servers/import` and `GET /api/v1/servers/{uuid}/transfer` endpoints behind a dedicated, scoped, short-lived import token, so Coolify's "Migrate server" can target Vessel directly. Verify this against `ServerTransferMigrator` (HTTPS check, response shapes) before promising it.
  - Application `Migration/Coolify/CoolifyBundleImporter`. It parses into `Vessel.Shared` DTOs, validates limits and ids, then maps to Vessel aggregates, routing every secret through `ISecretVault` (the bundle is plaintext, so never log or persist it raw, and zero its buffers). A dry run produces a coverage report of unsupported features (Swarm, runners, traffic analytics, ...).
  - After import, stop and remove `coolify-sentinel`, and write a Vessel owner marker. Then run a Vessel `TransferFinishChecklist` equivalent covering webhooks (regenerate secrets, per 17.09), DNS and backups.
- **Roadmap fit:** 17.02, 17.03, 17.05, 17.06, 17.07, 17.13.
- **Effort:** XL (full mapping); M (dry-run parser and coverage report).

#### SRV-15: Cloud server provisioning (Hetzner extras, DigitalOcean, Vultr, Hostinger) and cloud tokens
- **Upstream:**
  - Hetzner already existed at v4.1.0 (`cloud_provider_tokens`, `cloud_init_scripts`, `HetznerService`).
  - v4.2.0: Hetzner firewall, internal network and backups (#9646); Vultr provisioning and power controls (#10533); DigitalOcean Droplets (#10871); retaining instances awaiting a public IP; separate provider vs SSH checks.
  - `2026_07_08_*`: description on `cloud_provider_tokens`; uuid on `cloud_init_scripts`, which also gets an API.
  - v4.4.0 (#12026): `DELETE /servers/{uuid}?delete_from_provider=true`; server limit enforced on every creation path (#11968).
  - v4.5.0 (#11176): Hostinger, with post-install scripts.
  - v4.5.1: validate new cloud servers automatically (`ValidateNewCloudServerJob`, 40 × 30 s); show provider API errors; no duplicate Hetzner creates; hide out-of-stock types.
  - v4.5.3: validation continues over SSH when the Hetzner API fails.
  - v4.6.0: refresh provider status (`ServerCloudProviderStatusCheckJob`).
  - v4.7.0: cloud token API limited to team admins and owners (403 otherwise).
  - Routes `routes/api.php:141-152,228-253`.
- **Coolify behavior (now):**
  - Create a server at a provider with a token, region, size and image, a team private key (uploaded as a provider SSH key), an optional cloud-init script, and provider extras. For Hetzner these are `hetzner_firewall_ids`, `hetzner_network_ids`, `enable_backups` and `instant_validate`.
  - Provider ids and status are stored on the server (`vultr_instance_id`/`_status`, `digitalocean_droplet_id`/`_status`, `hostinger_virtual_machine_id`/`_status`). Power on and off are supported.
  - Deleting can also delete the VM at the provider.
- **Vessel today:** **Missing.** There is no provider abstraction or token model; `SecurityKeys.razor:5` mentions "cloud tokens" only as placeholder copy.
- **Gap type:** New feature / Schema / API contract.
- **Priority:** P2 for the abstraction plus Hetzner, P3 for the other providers. Convenient, but not needed for a host-native control plane managing existing hosts.
- **Recommended Vessel approach:**
  - Application `Servers/Provisioning/ICloudProvider` (list regions, sizes and images, upload key, create, status, power, delete) with a `CloudProviderToken` aggregate (team-owned, secret ref, description; admin and owner only) and a `CloudInitScript` aggregate.
  - Infrastructure: one typed `HttpClient` per provider with resilience (no retry on non-idempotent POST after send, as upstream fixed in v4.5.3).
  - The `ProvisionCloudServerJob` saga does create → wait for IP → `ValidateServer` (SRV-02). The job persists the provider id *before* waiting so that orphans can be cleaned up.
  - Enforce the team server limit in a single Application path.
- **Roadmap fit:** New phase; 17.01 inventory.
- **Effort:** L (Hetzner plus abstraction); +M per provider.

#### SRV-16: Health polling drift: per-poll audit rows, serial polling, no stagger or timeout
- **Upstream:**
  - v4.3.22 (scheduled work executes once per occurrence).
  - v4.4.0 (per-server staggering of hourly and weekly checks; dedicated schedulers).
  - v4.6.0 (short timeout, single-flight and a 60 s cooldown for unreachable servers).
  - `5c34a1b3d` ("reduce their audit rows" for rejected Sentinel pushes).
  - Key file: `app/Jobs/ServerManagerJob.php` (`patchCheckCron`, `imageRescanCron` with per-server offsets).
- **Coolify behavior (now):**
  - Checks are dispatched per server as separate jobs at staggered minutes.
  - A connectivity check is skipped when the agent heartbeat is live.
  - Unreachable servers cannot block workers or other servers.
  - Audit rows are not written for routine heartbeats.
- **Vessel today:** **Drifted:**
  - `PollAllAsync` loops over all non-disabled servers serially inside one job every 5 minutes (`src/Vessel.Application/Monitoring/ServerHealthPollingService.cs:22-30`; `ServerHealthPollingJob.cs:13-21`), with no per-server timeout or isolation.
  - It writes an `AuditActions.ServerHealthPolled` audit row on every poll for every server (`ServerHealthPollingService.cs:113-125`), which floods the audit log at 288 rows per server per day.
- **Gap type:** Behavior change.
- **Priority:** P3. Low risk today because Vessel only polls locally, but it matters once SRV-02 lands.
- **Recommended Vessel approach:**
  - A fan-out job enqueues one `PollServerHealthJob` per server, offset by a stable hash of the server id. Each job has a linked-CTS timeout of 20 s.
  - Audit only manual polls and status *transitions* (the domain event), not routine samples.
  - Keep snapshots pruned by a retention job.
- **Roadmap fit:** 12.11, 16.04.
- **Effort:** S.

#### SRV-17: Small host facts: GPU detection, Sysbox install, Docker and Compose versions in the UI
- **Upstream:**
  - v4.5.0 (server GPU detection: `nvidia-smi --query-gpu=index,uuid,memory.total,name` parsed in `Server::gatherServerMetadata`; GPU settings in Compose).
  - v4.4.0 Sysbox (`app/Actions/Server/InstallSysbox.php`, which writes `bip` and `default-address-pools` to `daemon.json` first so Docker only reloads), used by GitHub runners.
  - `docker_version`/`compose_version` with checked-at timestamps.
- **Coolify behavior (now):** The GPU list is offered in the per-resource GPU settings. Sysbox is installed on demand for runner Docker-in-Docker without privileged containers. Versions are shown and re-checked.
- **Vessel today:** **Missing.** `ServerCapability` has no GPU flag (`src/Vessel.Domain/Servers/ServerCapability.cs:3-11`), and versions are not persisted (see SRV-02).
- **Gap type:** New feature.
- **Priority:** P3. Niche. Fold version persistence into SRV-02.
- **Recommended Vessel approach:** Add a `ServerHostFacts` value object (GPU list, kernel, cgroup version, docker and compose versions) collected during validation. Add a `Gpu` capability flag. Add Sysbox only if runners are adopted.
- **Roadmap fit:** SRV-02 follow-up.
- **Effort:** S.

### Reviewed and not applicable

- **Traffic analytics via the Rust Sentinel** (v4.4.0 #11292, city-level v4.5.0 #12158, `SentinelTrafficClient`, `ConfigureTrafficAnalytics`, `traffic_*`/`geoip_*` settings): this is proxy-domain work. It is only noted here as a future agent responsibility in SRV-03 (Proxy agent).
- **GitHub Actions self-hosted runners on build servers** (v4.4.0 #12054, `GithubRunnerContainer`, `CleanupGithubRunnerJob`, `GithubRunnerDockerMode`): CI and runner feature for the Deployments/Git agent. Vessel would only need Sysbox (SRV-17) if adopted.
- **Coolify Cloud-only behaviour:** server validation failure rate limits (`2026_10_23_090005_add_server_validation_limits_to_instance_settings_table`, enforced only when `isCloud()`, `app/Models/Server.php:1489-1534`); daily removal of Sentinel on unpaid teams; `cleanup:unreachable-servers` resetting the IP (cloud branch); the subscription check in `TerminalSessionService`; and the dedicated cloud Sentinel queue. Billing and multi-tenant SaaS concerns are out of scope. Vessel could still rate-limit validation via its existing `api` policy.
- **Sentinel image versions, digest pinning in `upstream.json`/`versions.json`, and CVE-2026-111943 bumps:** these are Coolify distribution concerns. Vessel will not ship Sentinel (SRV-03), but the lesson about digest-pinning helper images applies to SCAN-01.
- **`docker_registry_url` instance setting** (v4.2.0 #9017; registry mirror for Coolify's own helper and Sentinel images): Vessel does not pull its own helper images. A mirror setting for scanner images is folded into SCAN-01.
- **Realtime on Laravel Reverb replacing Soketi; Node.js terminal server updates; terminal and realtime WebSocket proxying on the dashboard port (#11707):** Vessel uses SignalR in-process, so this is N/A.
- **Server Canvas (dashboard positions, `2026_10_23_200902_add_canvas_position_to_servers_table`) and the star or favorite button:** UI-only.
- **Docker Swarm restrictions ("only for teams that already use it"):** Vessel already removed Swarm (`20260531123000_RemoveDeprecatedSwarmCapability`), which is consistent with upstream de-emphasis.
- **MCP tools** (`get_server_metrics`, `manage_image_scanning`, `get_vulnerabilities`, `ManageServer`, `DockerCleanup`, `GetLogs`): these wrap the APIs above. They belong to the API/MCP agent if Vessel adopts MCP.
- **Cloudflare tunnel per server and `UpdateCloudflared`:** proxy and networking domain (Proxy agent).
- **SSH host-key pinning (`SSH_HOST_KEY_CHECKING` warn/enforce/off, `HostKeyChanged` notification, `/servers/{uuid}/host-key/reset`), fixed SSH options, and stricter key validation:** owned by the Security agent. Cross-referenced in SRV-01, SRV-02 and SRV-10.
- **`SCHEDULED_JOBS_DISPATCH_MODE` and per-type scheduler processes:** a Laravel scheduler implementation detail. Hangfire recurring jobs with per-server fan-out (SRV-16) cover the semantics.

## 14. NTF / OPS / INT / TPL / UX — Notifications, Operations, Integrations, Templates and Product Surface

### Summary

Between v4.1.0 (`49656aa`) and v4.7.6, upstream Coolify reworked notification reliability: channels turn themselves off after configuration failures, repeat alerts are throttled, failing jobs send one alert and then at most one reminder a day, and there are about 8 new notification types. Email gained named SMTP servers, presets, an EHLO domain setting, and separate sender fields (`resend_from_*` was a breaking change in v4.7.0). The scheduler was rebuilt on a durable record of each scheduled run. User-defined scheduled tasks gained failure tracking, auto-disable, and `use_shell`. Platform ops added update channels (stable/preview/nightly/custom), tracking of automatic update attempts, a database backup before each update, and in-container Reverb in place of Soketi. Coolify also added Vault/Infisical/Doppler secret managers, 36 new and 2 removed templates (334 modified), template pinning and disable/outdated flags, and UX features: stars, canvases, resource links, project icons, global/settings search, tag pages, and a `coolify.json` IaC branch that is not merged yet.

Vessel's position: Phase 13 built the right shape (event → in-app notification → delivery attempt), but **no workflow raises a notification** and the notification endpoints have **no permission checks**. User-defined scheduled tasks, update channels/self-update (Phase 14 has not started), secret managers, tags, and search are all missing. Service templates are a hard-coded C# catalog of 65 entries that is not compatible with the Coolify format (`:latest` images, fixed host ports, no magic variables).

Counts: **P0: 2 · P1: 9 · P2: 9 · P3: 7** (27 items).

Vessel notification channels today versus Coolify v4.7.6:

| Channel | Coolify v4.7.6 | Vessel |
|---|---|---|
| In-app | No (realtime UI events only) | Yes (`in_app_notifications`) |
| Email (SMTP) | Yes: named servers, presets, EHLO, system-wide option | Partial: one SMTP per target, `System.Net.Mail` |
| Email (Resend) | Yes (`resend_from_address/name`) | No |
| Discord | Yes | Yes |
| Slack | Yes | Yes |
| Telegram (topics/threads) | Yes, with a separate topic per event | Partial: one thread id |
| Pushover | Yes | Enum value only, no provider (`NotificationChannel.cs:11`) |
| Generic webhook | Yes (no signing; `Authorization` header added on `next` #12606) | Yes, with HMAC `X-Vessel-Signature` (Vessel is ahead) |

### Gap items

#### NTF-01: The notification pipeline is not connected to any producer
- **Upstream:** all versions. Every workflow notifies through `Team::notify()`: `app/Notifications/Application/DeploymentFailed.php`, `Database/BackupFailed.php`, `Server/HighDiskUsage.php`, `ScheduledTask/TaskFailed.php`, `Jobs/NotifyResourceStoppedJob.php`, and so on.
- **Coolify behavior (now):** Deployment success/failure, backup success/failure, missing backups, volume backups, scheduled tasks, Docker cleanup, server reachable/unreachable/disk/patch, Traefik outdated, restart limit, status changes, SSL expiry, API token expiry/failing, host key changes, and image scans all raise team notifications. The team's per-event toggles decide which channels get each one.
- **Vessel today:** **Missing (wiring).** The only caller of `NotificationEventService.RaiseAsync` (`src/Vessel.Application/Notifications/NotificationEventService.cs:16`) is the API endpoint `POST /api/v1/notifications/events` (`src/Vessel.Web/Controllers/Api/V1/NotificationsController.cs:65-70`). A grep for `RaiseAsync`/`NotificationEventService` across `src/` finds no caller in Deployments, ManagedServices (backups), Monitoring (server health), Proxy (certificates) or Terminals. `DeploymentRunner` and `ManagedDatabaseService` publish only SignalR/audit events (`DeploymentRunner.cs:473,525`; `ManagedDatabaseService.cs:268-273`). ROADMAP 13.01–13.12 are marked done, so operators who configure Discord or email will get no alerts.
- **Gap type:** Behavior change (correctness of a phase marked complete).
- **Priority:** P0. Vessel would ship with alerting that looks configured but never fires.
- **Recommended Vessel approach:** Add an Application-level `INotificationPublisher` with typed events (`DeploymentFinished`, `BackupFailed`, `ServerUnreachable`…), implemented by `NotificationEventService`. Raise events in `DeploymentRunner` (terminal states), `ManagedDatabaseService.RunBackupExecutionAsync` (success/fail), `ServerHealthPollingJob` (state transitions only, not every poll), and `CertificateRenewalJob`. Raise outside the main transaction, or through an outbox row in the same transaction, so a notification failure never fails the workflow (Coolify v4.4.0: "An error after the new container passed its health check, for example in a notification, no longer removes the new container"). Add integration tests per producer.
- **Roadmap fit:** 13.01/13.08 (reopen); prerequisite for NTF-03..05.
- **Effort:** M

#### NTF-02: Notification API and settings lack authorization, SSRF protection, and secret-safe URLs
- **Upstream:** v4.4.0 #11913/#11960 ("Enforce team ownership and permissions in … notification settings"); v4.7.0 ("Validated webhook URLs, limited DNS lookups"; "SMTP hosts are validated, and Coolify connects only to the checked address"); `app/Rules/SafeWebhookUrl.php`; `app/Traits/HandlesNotificationResponse.php` (#12232-era); instance settings `webhook_allowed_internal_hosts` and `webhook_allow_localhost` (migration `2026_07_02_142003`); notifications API `PATCH /notifications/{channel}` needs `api.ability:write`, and instance email `PATCH /settings/email` needs `write:sensitive`.
- **Coolify behavior (now):** Only team admins and owners (or tokens with write) can change channels. Webhook, Discord, Slack and SMTP targets are DNS-resolved and refused when they point to loopback, link-local/metadata or reserved ranges (private ranges only through an allow-list). The connection is pinned to the checked IP. HTTP client exceptions are rewritten so the full URL (bot token, webhook secret path) never reaches logs or the failed-jobs table, and provider response bodies are never stored.
- **Vessel today:** **Missing.** `NotificationsController` has only `[Authorize]` (`NotificationsController.cs:9-11`). `UpsertTargetAsync` performs no permission check (`NotificationTargetService.cs:36-99`), unlike other services that call `Require(..., VesselPermissions.X)` (for example `ManagedDatabaseService.cs:185`). Any authenticated team member can therefore repoint a webhook to an attacker URL and exfiltrate every later event, or call `POST /events` (`NotificationsController.cs:65`) to send any title, message and `ResourceUrl` to every external channel and to any `UserId` (phishing). `Notifications.razor:2` is `[Authorize]` only. `HttpWebhookNotificationProvider.cs:99` posts to any URL with no SSRF guard (no outbound URL guard exists in `src/`). The Telegram bot token is in the request path (`TelegramNotificationProvider.cs:19`), and Discord/Slack webhook secrets are URL paths, while `AddHttpClientInstrumentation()` (`src/Vessel.Web/Extensions/ServiceCollectionExtensions.cs:257`) exports `url.full` on spans to OTLP. `VesselPermissions` has no notifications permission (`src/Vessel.Application/Authorization/VesselPermissions.cs:7-20`).
- **Gap type:** Security hardening.
- **Priority:** P0. Read-only members can take over the alert channels and reach internal networks through SSRF.
- **Recommended Vessel approach:** Add `notifications.read`/`notifications.manage` permissions and enforce them in `NotificationTargetService` and `NotificationCenterService`, with controller policies on top. Remove `POST /events` from the public API, or restrict it to an admin/system test-only "send test notification" command with a fixed payload. Add an Infrastructure `IOutboundUrlGuard` (resolve, classify, pin through `SocketsHttpHandler.ConnectCallback`), with an instance allow-list setting, shared with Git/S3. Register a `DelegatingHandler`/OTel `EnrichWithHttpRequestMessage` filter that redacts URL paths for notification clients. Add tests for wrong-role, wrong-team and SSRF targets.
- **Roadmap fit:** 13.09 (reopen), 15.x security hardening.
- **Effort:** M

#### NTF-03: Channel delivery-failure tracking, auto-disable, and response classification
- **Upstream:** v4.5.0 ("Notification channels are disabled after repeated configuration failures"); `app/Traits/TracksDeliveryFailures.php`, `app/Traits/HandlesNotificationResponse.php`; migration `2026_10_12_121520_add_failure_count_to_notification_settings_tables`; v4.4.0 #10543 (settings restored when enabling fails); v4.7.0 notification sender used for the "channel disabled" email.
- **Coolify behavior (now):** Each channel's settings have `failure_count`. Only configuration errors (an invalid token or a deleted webhook) count. HTTP 429 and 5xx are retried by the queue. Other 4xx responses come from the payload and are only logged, never counted. After 3 consecutive configuration failures the channel is disabled. An audit event (`notifications.<channel>.auto_disabled`) is written, the team's other channels are told, and team admins get an email through the instance email settings. A success, or any settings change, resets the counter.
- **Vessel today:** **Missing.** `NotificationDispatchService` retries every failure up to 3 attempts with 2^n-minute backoff (`NotificationDispatchService.cs:17,118-125`), whether the cause is a 401 or a 503. `NotificationTarget` has `IsEnabled` but no failure counter (`src/Vessel.Domain/Notifications/NotificationTarget.cs:44,71-81`). Providers collapse every non-2xx into one failure string (`HttpWebhookNotificationProvider.cs:108-112`).
- **Gap type:** Behavior change / Schema.
- **Priority:** P1. Without it, a broken channel fails silently for ever and sends useless retries to dead endpoints.
- **Recommended Vessel approach:** Extend `NotificationDeliveryResult` with a `FailureKind` (`Transient`, `Configuration`, `Payload`). Providers map 401/403/404/410 to `Configuration` and 429/5xx/timeouts to `Transient`. Add `ConsecutiveConfigurationFailures` and `DisabledReason` to `NotificationTarget`, with domain method `RecordFailure(kind)` that auto-disables at a threshold of 3, and reset the counter on `Configure`. Retry only `Transient`. Raise a `notification.channel_disabled` event to other targets plus an in-app notification. Add an EF migration and tests.
- **Roadmap fit:** 13.08 (extend).
- **Effort:** S

#### NTF-04: Notification throttling and dedupe
- **Upstream:** v4.4.0 (disk-usage interval `server_disk_usage_notification_interval_hours`, default 24h; proxy restart loop at most 1/hour); v4.7.0 (scheduled-job failures: first failure, then at most once a day, plus one "works again" `ScheduledJobRecovered`; `Resource status changes` after a 2-minute confirmation, at most 1 per 15 minutes, never for Coolify-initiated stops; Traefik outdated once per patch version; API token failing 1/day); `app/Models/NotificationThrottle.php`, `app/Contracts/ThrottledNotification.php`, `app/Jobs/NotifyResourceStoppedJob.php`, migration `2026_09_29_083244_create_notification_throttles_table`.
- **Coolify behavior (now):** A `notification_throttles(notifiable_type, notifiable_id, notification, sent_at)` table with a unique key is claimed atomically (`claim`/`sendOnce`/`release` on exception). Notifications declare `throttleSubject()` and `throttleIntervalMinutes()`. Orphan rows are swept. `ContainerStopped` was removed in favour of a delayed recheck job.
- **Vessel today:** **Missing.** There is no dedupe beyond "skip targets that already succeeded for this event" (`NotificationDispatchService.cs:40-43`). Once NTF-01 is wired, health polling every 5 minutes (`ServerHealthPollingJob`) would alert on every poll.
- **Gap type:** New feature / Schema.
- **Priority:** P1. Required as soon as producers are connected.
- **Recommended Vessel approach:** Add a Domain `NotificationThrottle` (key, subject type/id, last sent) and an Application `INotificationThrottle.TryClaimAsync(subject, key, interval)`, implemented with a PostgreSQL `INSERT … ON CONFLICT DO UPDATE … WHERE sent_at < @cutoff RETURNING`. Give the typed events from NTF-01 an optional throttle policy. Add a "resource stopped unexpectedly" flow: on a status transition, schedule a Hangfire job delayed 120s that rechecks state and skips when Vessel itself initiated the stop (record an `ExpectedStopUntil` on the resource). Add a daily orphan sweep.
- **Roadmap fit:** New: 13.13–13.14 (or Phase 16 reliability).
- **Effort:** M

#### NTF-05: Granular event catalog and per-event routing
- **Upstream:** Event toggles per channel at HEAD (from `DiscordNotificationSettings`/`EmailNotificationSettings`): `deployment_success`, `deployment_failure`, `status_change`, `backup_success`, `backup_failure`, `scheduled_task_success`, `scheduled_task_failure`, `docker_cleanup_success`, `docker_cleanup_failure`, `server_disk_usage`, `server_reachable`, `server_unreachable`, `server_patch`, `traefik_outdated`, `restart_limit_reached` (v4.3.x, #11705), `image_vulnerabilities` (v4.7.0), plus email-only `deployment_commit_details` (v4.6.0/#12030, opt-in). New classes: `Application/RestartLimitReached`, `Database/BackupMissing`, `VolumeBackup/{BackupSuccess,BackupFailed,BackupMissing,RecoveryFailed}`, `Server/HostKeyChanged`, `ApiTokenFailingNotification`, `ImageScan/{ExploitedProblemFound,VulnerabilityDigest}`, `ScheduledJobRecovered`. Removed: `Container/ContainerStopped`. "Always-send" events that ignore toggles: SSL expiry, API token expiry, server force-enable/disable, Hetzner deletion failure (v4.3.18 #11528).
- **Coolify behavior (now):** Each channel has an independent boolean per event. Telegram additionally routes events to topic IDs. Links use the configured instance URL (#11623), not localhost.
- **Vessel today:** **Partial.** `NotificationDeliveryPolicy` has only `MinimumSeverity` plus 3 category flags (`src/Vessel.Domain/Notifications/NotificationDeliveryPolicy.cs:3-9`). Routing works by string prefix (`NotificationDispatchService.cs:135-143`), and unknown prefixes are always allowed. There is no success/failure split, no "always send" class, and no per-event Telegram topic.
- **Gap type:** New feature / Schema / API contract.
- **Priority:** P1. Operators expect to choose, for example, failures only.
- **Recommended Vessel approach:** Add a Domain `NotificationEventType` value object or enum with a catalog: key, category, default-on, `AlwaysDeliver`, and the throttle policy from NTF-04. Replace the policy struct with a set of enabled event keys stored as a JSON array on `NotificationTarget`, plus a per-event channel option map such as a Telegram thread id. Derive "success" vs "failure" from typed events. Document the catalog in `docs/deployment/phase-13…` and keep the 17.10 notification import mapping aligned with the Coolify keys.
- **Roadmap fit:** 13.09 (extend), 17.10.
- **Effort:** M

#### NTF-06: Email provider parity: instance transactional email, named SMTP servers, Resend, sender split, TLS modes, EHLO
- **Upstream:** v4.3.10 #11398 (`smtp_ehlo_domain`), #11393 (sender name/address on all mail), API `GET/PATCH /settings/email` (`InstanceEmailSettingsController`); v4.4.0 (mutually exclusive providers, Resend error handling, "an email that Resend did not accept is no longer counted as sent"); v4.7.0 (one provider list None/SMTP/Resend, **named SMTP servers each with its own sender** `smtp_server_name`/`smtp_servers` encrypted, `SmtpPresets` with 12 providers, separate `notification_from_address/name` on instance settings for team notifications, S3 alerts and channel-disabled alerts, **breaking: Resend sender moved to `resend_from_address`/`resend_from_name`**, "Use system-wide settings" replaces "Copy from instance settings"); `app/Traits/HasSmtpServers.php`, `app/Support/SmtpPresets.php`, `app/Support/SmtpTransportFactory.php`, `app/Notifications/Channels/EmailChannel.php`; deployment emails include commit SHA, branch and message (opt-in, v4.6.0).
- **Coolify behavior (now):** Instance-level email settings send transactional mail (password reset, invitations, verification) and can serve teams that choose "Use system-wide settings". SMTP supports `starttls`, `tls` (implicit, port 465) and none, with a configurable EHLO local domain and timeout. The SMTP host is validated and pinned. Email bodies are HTML templates.
- **Vessel today:** **Partial / Missing.** Each email target holds its own SMTP JSON (`src/Vessel.Infrastructure/Notifications/NotificationSecretModels.cs:3-10`). `EmailNotificationProvider` uses `System.Net.Mail.SmtpClient` (`EmailNotificationProvider.cs:20-37`). That API supports only STARTTLS through `EnableSsl`, so implicit TLS on 465 (Resend, Zoho presets) fails, and the EHLO domain cannot be set. Messages are plain text with no commit details. There are no instance-level email settings: `RequestPasswordResetAsync` creates and stores a reset token but never sends it (`src/Vessel.Application/Auth/VesselAuthenticationService.cs:136-153`). There is no Resend provider.
- **Gap type:** New feature / Breaking change (field naming to adopt) / API contract.
- **Priority:** P1. Account recovery depends on it, and email is the most common channel.
- **Recommended Vessel approach:** Add an Application `IEmailSender` (transactional) and `IEmailTransport` with Infrastructure adapters `MailKitSmtpTransport` (MailKit is an established dependency: `SecureSocketOptions.StartTls/SslOnConnect/None`, `LocalDomain` for EHLO, connect through the guarded IP from NTF-02) and `ResendTransport` (HTTP). Store a System-scope `EmailSettings` aggregate with named SMTP servers (encrypted through `ISecretVault`), active server, `ResendFromAddress/Name`, `NotificationFromAddress/Name`, and presets as static data. Email targets either reference "system" settings or carry their own. Use Razor-component or `.cshtml`-free templating for HTML bodies with the opt-in commit details. Expose `GET/PATCH /api/v1/settings/email` requiring `settings.manage` plus sensitive write. Tests should use an in-process SMTP fake.
- **Roadmap fit:** 13.03 (reopen); auth Phase 4 password reset.
- **Effort:** L

#### NTF-07: User-defined scheduled tasks (cron commands in application/service containers)
- **Upstream:** Pre-existing feature, reworked across releases: v4.3.4 #11239 (team-scoped authorization), v4.5.0 #12226 (run only in running containers, skip while the previous run is still active), v4.7.0 (`use_shell` "Run in a shell" #8244; failure tracking `consecutive_failures`/`failing_since`/`disabled_reason`; auto-disable after 10 consecutive failures once the first failure is more than 24h old; skip without counting when no container is running), v4.4.0 (encrypt remote commands stored for queued tasks and keep the command out of logs), v4.5.0 breaking (API tokens need `deploy` to run tasks), v4.6.0 (`delete` ability), v4.3.13 #11636 (duration as a JSON number); `app/Models/ScheduledTask.php` (`AUTO_DISABLE_AFTER_FAILURES = 10`, `AUTO_DISABLE_AFTER_HOURS = 24`, `containerCommand()`/`splitCommandArguments()`), `app/Jobs/ScheduledTaskJob.php`, migrations `2026_11_03_192735`, `2026_11_03_202635`, `2026_10_28_204953` (`worker_host`).
- **Coolify behavior (now):** Each task has name, command, cron `frequency` (server timezone), optional container name, `timeout`, `enabled` and `use_shell`. A run executes `docker exec <container> sh -c '<cmd>'`, or with argv splitting when `use_shell=false`. Executions record status, output (visible only to users who can update the resource, or `read:sensitive`), duration and worker host. The API returns the failure fields. Only "Run now" and scheduling need `deploy`.
- **Vessel today:** **Missing.** There is no scheduled-task domain, service, job, UI or API (a grep for `ScheduledTask` in `src/`, `docs/`, `ROADMAP.md` and `PROJECT_REQUIREMENTS.md` finds nothing). Only internal recurring jobs exist (deployment log retention, certificate renewal, health polling, backups).
- **Gap type:** New feature.
- **Priority:** P1. Core Coolify feature (cron jobs, migrations, queue workers) that users expect.
- **Recommended Vessel approach:** Domain `ScheduledTask` aggregate (strongly typed ID, owner Application or Service, `CronSchedule` value object, `ContainerSelector`, `Timeout`, `UseShell`, failure-tracking methods `RecordFailure(now)`/`RecordSuccess()` with auto-disable invariants) and `ScheduledTaskExecution`. Application `ScheduledTaskService` handles CRUD and run-now, with permissions `applications.write` for edit and `deployments.start` for run. Execution goes through `IContainerRuntimeClient.ExecAsync(container, argv)` → `IProcessRunner`, never a shell string. With `UseShell`, pass `["sh","-c",command]` as structured argv. Persist output incrementally with redaction and stream it through SignalR. Use the runtime semantics from NTF-08. Add a migration, API (`/api/v1/applications/{id}/scheduled-tasks`) and tests for auto-disable and skip-when-stopped.
- **Roadmap fit:** New (suggest Phase 11/12 follow-up, "11.16–11.20 Scheduled tasks"); the 17.x import should include tasks.
- **Effort:** L

#### NTF-08: Scheduled-job runtime semantics (durable runs, missed runs, retries, timezone)
- **Upstream:** v4.4.0 (schedules dispatched every minute by separate scheduler processes; lost backups requeued after 60 minutes; lost tasks and cleanups recorded as failed and notified; one failing schedule no longer blocks the others; DST repeated hour runs once; timeouts fail instead of staying open); v4.3.23 (execute once per occurrence across distributed schedulers); v4.4.1 (`SCHEDULED_JOBS_DISPATCH_MODE`; missed backups recorded as failed and notified); v4.6.0 (upgrade waits only for jobs on this host). Files: `app/Services/ScheduledJobDeliveryService.php` (`CATCH_UP_WINDOW_MINUTES=10`, `ENQUEUED_STALE_AFTER_MINUTES=60`, `RECOVERY_BATCH_SIZE=500`, `INTERRUPTED_CLAIM_MARGIN_MINUTES=15`), `app/Models/ScheduledJobState.php`, `ScheduledJobDelivery.php`, `app/Console/Commands/DispatchScheduledJobs.php`, `app/Jobs/ScheduledJobManager.php`, `app/Actions/UniqueUntilProcessingJobDecorator.php`; migrations `2026_09_18_111936` (tables `scheduled_job_states(schedule_key, last_scheduled_for)` and `scheduled_job_deliveries(schedule_key, scheduled_for UNIQUE, status, claim_token, enqueued_at, started_at)`), `2026_10_02_055938` (drop `next_run_at`), `2026_10_05_064841` (close legacy enqueued deliveries).
- **Coolify behavior (now):** Every occurrence becomes a unique row, claimed with a token, so it runs exactly once. Occurrences missed within 10 minutes are caught up; older ones are recorded as missed or failed and notified. Stale enqueued rows (over 60 minutes) are republished, and interrupted claims are failed. Backups and Docker cleanup are never auto-disabled.
- **Vessel today:** **Drifted / Partial.** Backup schedules are raw Hangfire recurring jobs (`ManagedDatabaseService.cs:192-195`) in UTC only (`src/Vessel.Infrastructure/Jobs/HangfireBackgroundJobDispatcher.cs:34`). `IRecurringJobScheduler` has no `Remove` and no timezone parameter (`src/Vessel.Application/Jobs/IBackgroundJobDispatcher.cs:12-18`), so disabled or deleted schedules cannot be unregistered. A disabled schedule throws (`ManagedDatabaseService.cs:228`), lock contention throws (`:249`), and failures rethrow (`:276-280`). With no `AutomaticRetry` configuration anywhere in `src/` (grep), Hangfire's default of 10 retries re-runs `RunScheduledBackupAsync`, and each retry creates a new `BackupExecution` (`:229-233`). Once NTF-01 is wired, that also means repeated failure notifications. There is no detection of missed or lost runs and no worker-host tracking.
- **Gap type:** Behavior change / Schema.
- **Priority:** P1. Duplicate executions and unremovable schedules are correctness bugs that also shape the scheduled-tasks design.
- **Recommended Vessel approach:** Add an Application `ScheduleOccurrenceLedger` with an Infrastructure table `schedule_occurrences(schedule_key, scheduled_for, status, claim_token, worker_host, started_at)` and a unique key. A single recurring Hangfire "dispatcher" job runs every minute, evaluates cron with timezone using Cronos/NCrontab (already Hangfire dependencies) against the instance or server timezone, inserts occurrences, and enqueues thin jobs carrying the occurrence ID. Jobs claim the occurrence and are idempotent. Mark them `[AutomaticRetry(Attempts = 0)]` and record failures in the domain instead. Add `IRecurringJobScheduler.Remove`. Run a sweeper for stale or interrupted claims and missed occurrences that raises `*.missed` notifications. Reuse this for backups, scheduled tasks, Docker cleanup and certificate renewals.
- **Roadmap fit:** 11.10 (reopen), 5.25 (jobs foundation), 16.x reliability.
- **Effort:** L

#### OPS-01: Update channels, version manifest, and automatic-update policy for Phase 14
- **Upstream:** v4.6.0 (channels stable / preview (betas and RCs) / nightly (with warning) / custom image tag such as `pr-1234`; `update_channel`, `update_custom_tag`, `previous_update_channel`, which keeps updates from the old channel after a switch to a more stable one until the installed version's stable release ships; `php artisan app:update-channel`; `install.sh --preview|--nightly`; `release.json` with `stable.coolify`/`preview.coolify`; CDN `versions.json` per channel); v4.7.0 (`AutoUpdateNotifier`: the root team is told when an auto-update starts, fails or finishes; `auto_update_target_version`/`auto_update_started_at` attempt tracking with a 30-minute result timeout); v4.5.0 ("automatic updates are spread over time"; migration `2026_10_15_213357_spread_default_auto_update_frequency` moves `0 0 * * *` to a random minute); v4.7.2 (nightly hint on the update page); v4.5.1 (manual update check failure reported); files `app/Console/Commands/UpdateChannel.php`, `app/Models/InstanceSettings.php::switchUpdateChannel`, `app/Services/AutoUpdateNotifier.php`, `app/Jobs/CheckForUpdatesJob.php`, migrations `2026_11_01_142702`, `2026_11_04_080000`.
- **Coolify behavior (now):** The instance follows one channel. Auto-update runs on a per-instance randomized cron. Each attempt is recorded and its outcome notified. Downgrading channel stability holds the previous channel until the stable release catches up.
- **Vessel today:** **Missing.** Phase 14 has not started (`ROADMAP.md:484-511`). `deploy/` is empty. There is no version-check code (grep for `CheckForUpdate|UpdateChannel|self-update` in `src` and `tools` finds nothing). PROJECT_REQUIREMENTS §2.6 defines only a manual `vessel self-update` (`PROJECT_REQUIREMENTS.md:203-222`) with no channels, schedule or notifications.
- **Gap type:** New feature (design input).
- **Priority:** P1. Channel and manifest design shapes 14.08/14.11 and the release pipeline (18.04-18.06) now.
- **Recommended Vessel approach:** Define a signed release manifest (`releases.json`: channel → version, per-arch artifact URL, SHA-256, signature) consumed by `vessel self-update --channel stable|preview|nightly|<version>`. Store a System `UpdatePolicy` setting (channel, previous channel, auto-update enabled, cron with a randomized default, target version, attempt start time). Add an Application `UpdateCheckService` (Hangfire recurring, jittered) that raises `platform.update_available|started|failed|succeeded` notifications to the instance-admin team (needs NTF-01). Actual swap and restart stay in the CLI and systemd (14.11-14.15), not a detached process.
- **Roadmap fit:** 14.11–14.15 (extend), new 14.23 "update channels & auto-update policy", 18.05.
- **Effort:** M

#### OPS-02: Pre-update control-plane database backup, upgrade status, job draining, and single-flight upgrades
- **Upstream:** v4.5.0 #12093 (`is_backup_before_update_enabled`, migration `2026_10_12_002632`; `app/Actions/Server/UpdateCoolify.php::backupDatabase()` runs the instance DB backup synchronously and aborts the update on failure with a clear message); v4.7.0 (pre-update backup always runs on the Coolify host, skips a disabled schedule, shows why it failed); v4.4.4/v4.4.5 (a 5 GB free-space check was added, then **removed** because it blocked updates); v4.4.4 (prevent duplicate upgrades); v4.3.x (`CoolifyUpgradeStatus` status file with steps 1–6 and `STALE_AFTER_MINUTES=10`, upgrade waits for the restarted instance and target version, #11401 persist target image before recreate); v4.6.0 (the upgrade waits for running scheduled jobs on this host); `scripts/upgrade.sh` (`write_status` steps), new `scripts/upgrade-postgres.sh` (explicit internal PostgreSQL major upgrade, never automatic) and `scripts/restore-backup-vm.sh` (offline VM to test migrations against a real backup).
- **Coolify behavior (now):** Before an update, Coolify backs up its own DB if the setting is on and fails closed. An exclusive lock blocks concurrent upgrades. Progress is written to a status file the UI polls. The upgrade waits for in-flight jobs and reports success only when the new version answers. The internal PostgreSQL major upgrade is a separate, explicit operator step.
- **Vessel today:** **Missing.** Phase 14.11–14.15 plan swap, restart, health check and rollback but have no pre-update DB backup, migration-safety gate, job drain or concurrency lock (`ROADMAP.md:500-504`). Phase 11 backs up managed databases but not Vessel's own control-plane PostgreSQL.
- **Gap type:** New feature (design input).
- **Priority:** P1. EF migrations on update are not reversible by a binary rollback alone.
- **Recommended Vessel approach:** In `vessel self-update`: take an advisory lock (`vessel:self-update`), run `pg_dump -Fc` of the Vessel DB through `IProcessRunner` into `/var/lib/vessel/backups/pre-update/`, and abort on failure unless `--skip-backup`. Write a status file under `/var/lib/vessel/` for UI and API polling. Ask the running instance to drain Hangfire (stop accepting new work, wait with a timeout for jobs owned by this host) before `systemctl restart`. Roll back the binary, and offer a restore of the pre-update dump when migrations failed. Document an explicit `vessel db upgrade-postgres` procedure. Do not add disk-space hard blocks; warn only (upstream reverted the block).
- **Roadmap fit:** 14.11–14.15, 14.20, 16.12.
- **Effort:** M

#### TPL-01: Service template format compatibility with the Coolify catalog
- **Upstream:** `templates/compose/*.yaml` (390 files at HEAD versus 356 at v4.1.0) with header comments `# documentation/slogan/category/tags/logo/port/minversion/env_file/amd_only/arm_only/ignore/outdated` and generated `templates/service-templates*.json` (base64 compose, `template_last_updated_at` added). Magic variables are parsed in `bootstrap/helpers/parsers.php` and `shared.php`: `SERVICE_URL_<NAME>_<PORT>`, `SERVICE_FQDN_<NAME>_<PORT>`, `SERVICE_PASSWORD_*`, `SERVICE_USER_*`, `SERVICE_BASE64_*`, and `COOLIFY_*`. v4.6.0 fixes: `SERVICE_URL_<NAME>_<PORT>` passed at runtime and build time, and Compose defaults stored for referenced variables. Labels moved from numeric IDs to `coolify.applicationUuid`/`serviceUuid`/`service.subUuid`/`databaseUuid` (v4.4.0). v4.5.0: Compose files with `scale`/`replicas` > 1 are rejected. v4.7.0: services removed from the Compose file show "Not in Compose file". v5 decision 0023 says 316 of 388 templates run unchanged as node stacks.
- **Coolify behavior (now):** Templates never publish host ports. Routing comes from `SERVICE_URL_*` magic variables that generate proxy labels and domains. Credentials are generated per magic variable and stored as env vars. Templates are data, refreshed from the CDN.
- **Vessel today:** **Drifted.** `ServiceTemplateCatalog` is 2,533 lines of hard-coded C# compose strings for 65 templates (`src/Vessel.Application/ManagedServices/ServiceTemplateCatalog.cs`). 52 image references use `:latest` (for example `dpage/pgadmin4:latest` at `:22`) and 74 publish fixed host ports (for example `"5050:80"` at `:27`), so two instances of a template on one server collide and bypass the proxy. There are no magic variables or proxy and domain integration, and `TemplateVersion` is a literal `"1"`. ROADMAP 17.08 (`ROADMAP.md:576`) assumes a mapping from Coolify templates that this model cannot express.
- **Gap type:** Behavior change / New feature.
- **Priority:** P1. One-click services are core parity, and the current model will not scale to the ~390 upstream templates or support migration.
- **Recommended Vessel approach:** Move templates to data: embedded resources in Coolify-compatible YAML plus a header-metadata parser in Application (`ServiceTemplateCatalog` loads `ServiceTemplateDefinition` from an `IServiceTemplateSource`). Implement a Domain `ServiceMagicVariable` value object and an Application `ServiceComposeParser` that resolves `SERVICE_URL/FQDN/PASSWORD/USER/BASE64_*` into env vars (secrets through `ISecretVault`) and proxy routes (Phase 10 `IProxyProvider`). Reject `ports:` host publishing by default and drop `scale`/`replicas` > 1. Record template version, last-updated time and image digests. Add golden tests against a sample of upstream templates.
- **Roadmap fit:** 11.05–11.07 (reopen), 17.08, 17.12.
- **Effort:** XL

#### INT-01: External secret managers (HashiCorp Vault, Infisical, Doppler) as `{{vault.KEY}}` references
- **Upstream:** v4.4.0 #11474 (Doppler, Infisical, Vault as secret sources for applications, services and standalone databases; values resolved during deployment and database start, never stored, redacted from logs; key browsing, bulk import, autocomplete; build reuse skipped when build-time references may have changed; DB starts moved to a queued job so secrets resolve); v4.4.4 #12128 (Infisical references expanded); v4.5.0 #12270 (configurable request timeout for Infisical/Vault); v4.5.1 #12267 (Infisical imported secrets); v4.6.0 (API and MCP: set, remove and import secret manager, key names only; `GET/PATCH/DELETE /{resource_type}/{uuid}/secret-manager`, `GET …/keys`, `POST …/references`, `POST …/import`); v4.7.0 (provider dashboard links). Files: `app/Services/VaultService.php` (KV v2, optional namespace, default 5s connect / 10s timeout), `InfisicalService`, `DopplerService`, `app/Services/SecretManagers/ResourceSecretManager.php`, `app/Models/SecretManagerLink.php`, `app/Support/RemoteSecretReferences.php` (`/{{\s*vault\.([A-Za-z0-9_]+)\s*}}/`), `RemoteSecretValueFormatter.php` (dotenv single-quote / `$$` escaping), `app/Traits/HasSecretManager(+Autocomplete).php`, `SecretManagerController.php`; migration `2026_08_23_000000_add_secret_manager_integrations` (`secret_manager_links(resourceable, integration_token_id, settings JSON)`, `integration_tokens.metadata`).
- **Coolify behavior (now):** A team integration token (provider + credentials + metadata such as base_url, namespace, client_id, timeout) links to one resource with provider settings (Vault mount/path; Infisical project/env/path; Doppler project/config). Env values may contain `{{vault.KEY}}`. At deploy time all keys are fetched in memory, missing keys fail the deployment with a clear error, and values are escaped for dotenv or compose. Linking needs write access on the whole team. Unlinking leaves references, which then fail the next deploy.
- **Vessel today:** **Missing.** `SecretProvider.External = 1` exists (`src/Vessel.Domain/Secrets/SecretProvider.cs:6`) but is never used; `AesSecretVault` always stores `SecretProvider.Vessel` (`src/Vessel.Infrastructure/Security/AesSecretVault.cs:32`). There is no provider adapter, link model or reference syntax.
- **Gap type:** New feature.
- **Priority:** P2. Valuable for teams that standardize on Vault or Doppler, and Vessel's own vault covers the base case.
- **Recommended Vessel approach:** Application `ISecretManagerProvider { ProviderKind; ValidateAsync; ListKeysAsync; FetchAsync(link, ct) → IReadOnlyDictionary<string,string> }` with Infrastructure adapters `HashiCorpVaultProvider` (KV v2), `InfisicalProvider` (universal auth) and `DopplerProvider`, each using a named `HttpClient` with a configurable timeout and the outbound guard from NTF-02. Domain `SecretManagerConnection` (team-owned, credentials in `ISecretVault`) and `ResourceSecretSource` (resource → connection + settings). An Application `RemoteSecretResolver` runs only inside deployment and DB-start jobs, adds fetched values to the `RedactionContext`, and fails on missing keys. Use `{{vault.KEY}}` syntax for Coolify import compatibility. Add a migration, API mirroring upstream routes, and tests (missing key, redaction, wrong-team connection).
- **Roadmap fit:** New (Phase 15 or a new "Integrations" phase); 17.07 import of `{{vault.*}}` references.
- **Effort:** L

#### TPL-02: Template security response: pinned tags, disable/outdated flags, and catalog churn
- **Upstream:** v4.5.0 #12268 (31 PHP-based templates disabled with `# ignore: true` for CVE-2026-109618/109619, then re-enabled with patched images in `0ebb1a1da`); v4.4.0 (osTicket hidden; Mattermost `amd_only`; `denoKV` renamed `deno-kv` with the old key still accepted); main after v4.7.6 (unreleased: `e9c24ae7d` "pin moving tags" replaces `latest` with digest-equivalent version tags across 238 files; `b051da4e9` "Outdated badge" via `# outdated: true`, 36 templates flagged at HEAD; `4223d20f7` major-version bumps); v4.7.0 template fixes (Bugsink MySQL binlog growth, Outline PG12→16, LiteLLM `sk-` key, Usesend pinned `v1.10.0` because `latest` was stale). Churn since v4.1.0: 36 added (for example 9router, bulwark, docling, healthchecks, hi-events, inngest, netbird, orbit, peertube, stalwart, vault, zero), 2 removed (booklore, osticket), 1 renamed, 334 modified (`notes/templates.txt`).
- **Coolify behavior (now):** Templates can be hidden (`ignore`), marked `outdated`, or restricted by architecture. Old keys keep working after renames. Upstream is moving away from moving tags so the same template deploys the same image.
- **Vessel today:** **Missing.** No template carries flags, architecture, digest, aliases or deprecation metadata (`ServiceTemplateDefinition` record, `ServiceTemplateCatalog.cs:2527-2533`). 52 `:latest` references. Disabling a vulnerable template would need a code release.
- **Gap type:** Security hardening / New feature.
- **Priority:** P2. A CVE response needs to be a data change, not a release.
- **Recommended Vessel approach:** Extend the template metadata from TPL-01 with `Hidden`, `Outdated`, `Architectures`, `Aliases`, `LastUpdatedAt` and pinned `image@digest` or version tags. Add a catalog lint test that fails on `:latest` and on host `ports:`. Existing `ServiceResource.TemplateKey` values resolve through aliases.
- **Roadmap fit:** 11.06 (extend), 17.08.
- **Effort:** M

#### TPL-03: Template catalog distribution and refresh
- **Upstream:** v4.3.0 #11094 (shared Redis cache of the CDN bundle across app nodes; `PullTemplatesFromCDN` timeout 10s → 60s, connect 10s, 3 retries, logs failures; `store_service_templates_bundle`, `get_service_templates_fetched_at`); v4.4.0 ("The service list no longer becomes empty when the template CDN returns an empty or invalid response"); v4.3.x (logos retried from a remote source before falling back to the default icon); `config/constants.php` `services.official = https://cdn.coollabs.io/coolify/service-templates-latest.json`.
- **Coolify behavior (now):** The catalog refreshes from the CDN, is cached for all nodes, keeps the last good bundle on failure, and shows a "last updated" hint.
- **Vessel today:** **Missing.** The catalog is compiled into the binary (`ServiceTemplateCatalog.cs`) and can only be updated by a Vessel release.
- **Gap type:** New feature.
- **Priority:** P2. The catalog moves faster than Vessel releases (334 template changes in six months).
- **Recommended Vessel approach:** Embed a baseline catalog, and optionally poll a signed catalog manifest (same trust chain as OPS-01) through a Hangfire job. Validate it with the TPL-01 parser and store the last good version in PostgreSQL (source of truth; Redis only as a cache). Never replace a good catalog with an empty or invalid one. Show the fetched time in the UI. Offline or air-gapped installs keep the embedded catalog.
- **Roadmap fit:** 11.06 (extend), 14.x.
- **Effort:** M

#### OPS-03: Realtime team scoping: stale team after a switch, team-channel payload exposure, scale-out
- **Upstream:** v4.4.0 (Reverb in-process replaces the `coolify-realtime` Soketi container; "Pages that stay open while you switch teams in another tab no longer fail when realtime events arrive"; "Team broadcast events share one trait and always name their team"); v4.5.1 #11707 (realtime and terminal WebSockets proxied on the dashboard port; orphaned realtime containers removed); v4.5.2 #12355 (finish events of background tasks apply only to servers and services of their own team); v4.6.0 (only `/app/` WebSocket path routed through the dashboard domain, Reverb HTTP API not exposed); v4.7.0 (WebSocket handshake failed with 413 for large cookies; Caddy `stream_close_delay`). Files: `app/Traits/BroadcastsToTeam.php` (team taken from the resource, never the session), `app/Traits/ListensToTeamChannel.php` (component bound to its mount team, stale events ignored, resource-specific payloads **encrypted** because the team channel reaches members without access to that resource), `routes/channels.php`, `config/reverb.php`, `docker-compose.prod.yml` (`soketi` placeholder with `replicas: 0`; `coolify` aliased `coolify-realtime`; health check also probes Reverb `/up`).
- **Coolify behavior (now):** One private team channel plus a user channel. The team is always derived from the resource. Resource-scoped data on the team channel is encrypted so members with scoped access grants (v4.6.0) cannot read other resources' events.
- **Vessel today:** **Partial.** SignalR is in-process (`ServiceCollectionExtensions.cs:55`; hubs at `EndpointRouteBuilderExtensions.cs:19-22`), which matches the upstream direction of in-process realtime. However, the tenant group is joined from the connection's team claim at connect (`src/Vessel.Web/Hubs/AuthorizedResourceHub.cs:21-27`), and a team switch only reissues the cookie (`src/Vessel.Web/Controllers/TeamsController.cs:35-48`), so open hub connections and Blazor circuits keep the old team. In-app notifications go to the whole team group with full title and message (`NotificationCenterService.cs:79`). `ListInApp` does not filter by resource access, which would leak once scoped grants exist. There is no Redis backplane for multi-node or separate-worker deployments.
- **Gap type:** Security hardening / Behavior change.
- **Priority:** P2. Low impact until scoped access lands; it becomes P1 together with access grants (auth domain).
- **Recommended Vessel approach:** Carry `TeamId` on every `RealtimeMessage` from the resource. Make hubs and components ignore messages whose team differs from their mount team, and on a team switch re-negotiate hub connections (client reconnect) and refresh the circuit's `AuthenticationState`. For team-group notifications about a specific resource, send a thin "refresh" event or filter recipients by `CanAccess*` (user groups) instead of the full payload. Add `AddStackExchangeRedis` as an optional backplane when Redis is enabled, and document that Hangfire workers in another process need it.
- **Roadmap fit:** 6.18/6.21 (extend), 13.11, Phase 16.
- **Effort:** M

#### OPS-04: Secret re-encryption command after master-key rotation
- **Upstream:** v4.5.0 (`php artisan app-key:reencrypt` re-encrypts stored secrets after an `APP_KEY` rotation; `app/Console/Commands/ReencryptWithAppKey.php`); v4.5.0 migration `2026_10_19_083521_encrypt_plaintext_secrets` (plaintext secrets encrypted at rest). Cross-ref security agent.
- **Coolify behavior (now):** The operator rotates the key and runs one command that decrypts with the old key and re-encrypts with the new one.
- **Vessel today:** **Partial.** `SecretValue.KeyVersion` is recorded (`src/Vessel.Domain/Secrets/SecretValue.cs:17-36`), but re-encryption is only planned (`docs/security/secrets.md:22`). There is no CLI host yet (14.04).
- **Gap type:** Security hardening / New feature.
- **Priority:** P2. Needed before stable, and it belongs with the Phase 14 CLI.
- **Recommended Vessel approach:** `vessel secrets reencrypt --from-key-version X` via an Application `SecretReencryptionService`: batched, resumable, with an advisory lock, audit record only, never logging plaintext. Infrastructure supports multiple key versions for decryption during the transition. Add tests.
- **Roadmap fit:** 14.05 (CLI), 15.x.
- **Effort:** S

#### OPS-05: Installer and upgrade-script lessons for the Phase 14 installer
- **Upstream:** v4.4.0 (re-running the installer no longer changes owner and permissions of `/data/coolify`, waits for Docker on OpenRC); v4.5.0 #12050 (no Docker restart with running containers, handles a stopped daemon, changes data directories only after checks and downloads); v4.5.1 #12276 (root credentials quoted in `.env`; keep operator SSH keys); v4.5.1 (helper downloads verified by SHA-256); v4.6.0 (image alone defines the version, `COOLIFY_VERSION` removed from `.env`; quote root values with spaces during upgrade; zstd layers need Docker ≥ 23; `install.sh` starts a stopped Docker daemon; Windows/Docker Desktop support removed); v4.7.0 (Docker install fallback for Ubuntu derivatives via `UBUNTU_CODENAME`, Debian repo on Raspbian 13); `scripts/install.sh` (+696/−) with `--preview|--nightly` channel flags; `other/nightly/install.sh` deleted; `docker-compose.prod.yml` (default registry ghcr.io → docker.io, `/data/coolify/images` mount, health check every 10s with retries 12 and `start_period: 1m` to cut CPU).
- **Coolify behavior (now):** The installer is idempotent, does all checks and downloads before making changes, never touches running workloads, verifies helper checksums, and picks the release channel.
- **Vessel today:** **Missing.** `deploy/` is empty, 14.08–14.10 and 14.21 are open (`ROADMAP.md:497-499,510`).
- **Gap type:** New feature (design input).
- **Priority:** P2. These are concrete acceptance criteria for 14.08 and 14.21.
- **Recommended Vessel approach:** Add to 14.08/14.21 acceptance: preflight-then-mutate ordering; never restart Docker/Podman when it is already running; preserve ownership and modes of `/var/lib/vessel`; write env files with safe quoting; verify artifact and helper checksums; idempotent re-run; channel flag; distro-derivative detection (`ID_LIKE`, `UBUNTU_CODENAME`). Use a systemd unit with `TimeoutStopSec` long enough for the Hangfire drain (OPS-02).
- **Roadmap fit:** 14.06, 14.08, 14.09, 14.21.
- **Effort:** S (spec) / M (implementation)

#### NTF-09: Channel feature parity and operator UX (Pushover, test send, typed forms, per-channel API)
- **Upstream:** Pushover channel (`app/Notifications/Channels/PushoverChannel.php`; v4.5.2 no payload in logs); v4.5.0 #12219 (Telegram test shows API errors), #12232 (fields required only when enabled); v4.5.1 #12323 (trim Telegram tokens and chat IDs); v4.7.0 (Telegram falls back to text links when the button URL is rejected); `next` (unreleased) #12606 optional `Authorization` header for webhooks; API `GET/PATCH /notifications/{email|discord|slack|telegram|pushover|webhook}`.
- **Coolify behavior (now):** Each channel has a typed settings form with a "Send test notification" action and validation. The API reads and updates per channel, with secrets write-only.
- **Vessel today:** **Partial.** `NotificationChannel.Pushover` exists but has no provider or UI option (`NotificationChannel.cs:11`; `Notifications.razor:90-95`; registrations at `InfrastructureServiceCollectionExtensions.cs:137-153`). Selecting it through the API records "No provider is registered" failures (`NotificationDispatchService.cs:53-59`). Secrets are typed as raw JSON in a textarea (`Notifications.razor:124`). There is no test-send command and no input trimming or validation, and the webhook has no custom auth header.
- **Gap type:** New feature / API contract.
- **Priority:** P2.
- **Recommended Vessel approach:** Add `PushoverNotificationProvider`. Use per-channel typed request DTOs (in Shared) validated in Application instead of free JSON. Add a `SendTestNotificationAsync(targetId)` command that bypasses throttles and never counts toward NTF-03. Add an optional `Authorization` header for webhooks, stored in the secret, and keep HMAC.
- **Roadmap fit:** 13.03–13.09 (extend).
- **Effort:** M

#### UX-01: Global search and command palette
- **Upstream:** v4.3.0 (improved global search); v4.3.2 (searchable breadcrumb switching); v4.4.2 #12111 (settings search in the command palette, Global and This-page scopes); v4.7.0 (global search accepts quick-create commands such as "new valkey"); v4.3.x `ClearsGlobalSearchCache` trait; v4.6.0 (search results limited to the scopes a member can access).
- **Coolify behavior (now):** A keyboard-invoked palette searches resources, settings and actions, scoped to the user's access.
- **Vessel today:** **Missing (UI stub).** "Command Center" and "Search resources" buttons with `/` hints have no handler (`src/Vessel.Web/Components/Layout/MainLayout.razor:12-16,61-65`).
- **Gap type:** New feature.
- **Priority:** P2.
- **Recommended Vessel approach:** Application `GlobalSearchQuery` (projects, environments, apps, databases, services, servers, settings pages; PostgreSQL `ILIKE`/trigram, filtered by `VesselAuthorizationService`). Blazor palette component with debounced calls; no `DbContext` in the component.
- **Roadmap fit:** New (Phase 6 follow-up).
- **Effort:** M

#### UX-02: Tags: management, assignment during creation, bulk tagging, tag pages, tag API
- **Upstream:** v4.2.0 #9275 (tag management and assignment during application, database and service creation); v4.5.0 (connect resources to a tag from the tag page); v4.5.1 (filtered tag count fix); v4.4.0 #11971 (quick-add tags with special characters); migration `2026_07_07_113248_add_team_name_unique_index_to_tags_table`; API `GET/POST/PATCH/DELETE /tags`, `GET/POST/DELETE /applications/{uuid}/tags`; v4.4.0 (tag deployments enforce permissions); v4.6.0 (`delete` ability needed to detach a tag).
- **Coolify behavior (now):** Team-unique tag names. Tags are attached to applications, databases and services. A tag page lists resources, supports bulk attach and can deploy everything tagged (permission-checked).
- **Vessel today:** **Missing (UI stub).** `/tags` renders only an empty state, "Tags will appear here when resource labeling is implemented" (`src/Vessel.Web/Components/Pages/Tags.razor:8`). There is no Domain tag entity (grep).
- **Gap type:** New feature / Schema / API contract.
- **Priority:** P2.
- **Recommended Vessel approach:** Domain `Tag` (team-owned, unique name) and a `ResourceTag` join (resource kind + ID). Application `TagService` with assign, bulk-assign and deploy-by-tag (delegating to `StartDeploymentService` per resource, with permission checks). API under `/api/v1/tags`. Migration. Coordinate with the deployments agent on tag deploy webhooks.
- **Roadmap fit:** New (Phase 7 follow-up).
- **Effort:** M

#### OPS-06: HTML no-transform header for proxies such as Cloudflare Rocket Loader
- **Upstream:** `app/Http/Middleware/PreventHtmlTransform.php` (adds `Cache-Control: no-transform` to `text/html` responses, because script-rewriting proxies break SPA navigation); `AddServerTimingHeaders.php` (dev-only Server-Timing plus HUD).
- **Coolify behavior (now):** Proxies are told not to rewrite dashboard HTML. Dev builds expose request and DB timing.
- **Vessel today:** **Missing.** Blazor's `blazor.web.js` and enhanced navigation are similarly sensitive to script rewriting. There is no such header (grep). OpenTelemetry covers timing.
- **Gap type:** Behavior change (robustness).
- **Priority:** P3.
- **Recommended Vessel approach:** Add a small Web middleware or `OnStarting` hook that appends `no-transform` for HTML. Optionally emit `Server-Timing` in Development from the existing `Activity`.
- **Roadmap fit:** Phase 2/16.
- **Effort:** S

#### OPS-07: Coolify v5 direction (strategic alignment only)
- **Upstream:** branches `v5-progress` (active, 2026-11-18), `v5.x`, `v4-ui-inertia-switchable`; `docs/v5/decisions/0001–0030`.
- **Coolify behavior (now):** Unreleased. v5 adds "Nodes" next to legacy Docker servers, kept permanently in parallel (0004). Nodes run a **host-native Sentinel under systemd** that absorbs the `coold` agent (0001, 0013). Workloads run on **Podman** with rootless BuildKit builds (0006), and a Flux control connection runs as a service inside the Coolify container (0021, 0026). Caddy ingress per node uses ACME certificates stored in Corrosion (0005). Volumes stay pinned to one node and are copied with rsync over WireGuard (0007). Scheduled tasks run through a typed `workload.exec.v1` with no host shell (0018). Templates run as stacks (0023: 316 of 388 templates). The UI moves to **Inertia + Svelte 5** (`origin/v5.x:package.json`), not React as the brief assumed.
- **Vessel today:** N/A (direction). Vessel's host-native, systemd-first, Podman-ready runtime abstraction (`AGENTS.md`, PROJECT_REQUIREMENTS §2) already matches v5's direction.
- **Gap type:** Design input.
- **Priority:** P3. Track it; do not chase it before upstream release.
- **Recommended Vessel approach:** Keep `IContainerRuntimeClient` Podman-capable. Treat "typed exec with no host shell" (0018) as the model for NTF-07 execution. Revisit the 17.x migration scope when v5 ships.
- **Roadmap fit:** 17.01.
- **Effort:** S (tracking)

#### UX-03: Stars and favourites
- **Upstream:** v4.7.0 ("A star button on servers and resources for quick access on the dashboard"); migration `2026_11_06_165709_create_stars_table` (`user_id`, morph `starrable`, `position`, unique per user and resource); `app/Traits/HasStars.php`, `app/Livewire/StarToggle.php`.
- **Coolify behavior (now):** Each user stars resources or servers. Starred items appear on the dashboard in a user-defined order.
- **Vessel today:** **Missing** (grep finds no star or favourite entity).
- **Gap type:** New feature / Schema.
- **Priority:** P3.
- **Recommended Vessel approach:** Domain `UserFavorite(UserId, ResourceKind, ResourceId, Position)`, an Application toggle and list filtered by access, and a dashboard section.
- **Roadmap fit:** New (UI backlog).
- **Effort:** S

#### UX-04: Canvas views and resource links
- **Upstream:** v4.5.0 #12160 (dashboard Canvas with interactive resource views, active deployments, team avatars); v4.5.1 (admins drag project and server zones; faster pan and zoom); v4.6.0 (SQLite on canvas); v4.7.0 (deploy canvas cards). Livewire `Dashboard/Canvas`, `Project/{ProjectCanvas,ProjectsCanvas,EnvironmentCanvas}`, `Server/Canvas`, `Tags/Canvas`, `SharedVariables/Canvas`, `DeployCanvas`, `Concerns/RemembersCanvasView`. Migrations: `environments.canvas_positions` JSON, `projects.canvas_position`, `servers.canvas_position`, `resource_links(environment_id, source morph, target morph, label ≤64, created_by, unique direction)`.
- **Coolify behavior (now):** A visual topology of projects, environments, resources and servers with persisted positions, plus user-drawn labelled links between resources in an environment.
- **Vessel today:** **Missing.**
- **Gap type:** New feature / Schema.
- **Priority:** P3. Visual nicety; resource links have some documentation value.
- **Recommended Vessel approach:** Defer. If adopted, store positions as JSON on Environment/Project/Server (UI state, not domain invariants) and add a `ResourceLink` entity scoped to an environment. Render with a JS interop graph library behind a Blazor component.
- **Roadmap fit:** New (UI backlog).
- **Effort:** L

#### UX-05: Project icons and image storage / CDN URL
- **Upstream:** v4.3.1 (custom project icons with local and S3 storage); v4.3.3 (icons on dashboard cards and shared-variable views); v4.3.6 (persistent private storage survives upgrades); v4.3.10 (served directly from S3 or an optional CDN); v4.3.13 (`image_cdn_url` instance setting); v4.5.0 #11865 (no unauthenticated public S3 URLs for avatars and icons); migrations `2026_08_13_140035` (`projects.icon_path`, `icon_storage_type`, `icon_s3_storage_id`), `2026_08_07_185536` (avatar storage settings), `2026_09_04_191011` (`image_cdn_url`); `docker-compose.prod.yml` mount `/data/coolify/images`.
- **Coolify behavior (now):** Projects and users have uploaded images stored locally or in S3 and served through authenticated routes or a configured CDN.
- **Vessel today:** **Missing.**
- **Gap type:** New feature.
- **Priority:** P3.
- **Recommended Vessel approach:** Store images through `IObjectStorage` under a validated owned path (`/var/lib/vessel/images`). Serve through an authorized controller with content-type sniffing and size limits; an optional CDN base URL setting comes later.
- **Roadmap fit:** New (UI backlog).
- **Effort:** S

#### UX-06: `coolify.json` config-as-code (unreleased upstream)
- **Upstream:** branch `coolify-json-iac-config` (last commit 2026-11-20, **not merged** into main or v4.7.6; 194 files, +20k lines), the older `coolify-json-detection` (2025-12, not merged). `app/Services/ProjectConfig/ConfigParser.php`: `FORMAT_VERSION = 1`, max 1 MiB, top-level keys `$schema, version, minimum_coolify_version, project, resources, environments, moved, unmanage`, resource types `application, postgresql, mysql, mariadb, mongodb, redis, keydb, dragonfly, clickhouse, sqlite, service`, resource keys `^[a-z][a-z0-9-]{0,62}$`, env forms `value | required | generate (password|base64|uuid) | ref`, where `ref` is `(team|project|environment|server|vault).KEY` or `resource.<key>.(internal_url|host|port)`, plus flags `buildtime/runtime/literal/multiline/preview/rotation`. Change note: a push to the linked branch applies safe changes; destructive changes wait for approval on a "Config as code" page; managed settings become read-only; there is a PR plan comment; export via `GET /api/v1/environments/{uuid}/config/export` (admin or `read:sensitive`).
- **Coolify behavior (now):** Not released. The design is a GitOps-style environment manifest with plan/apply, an approval gate and secret-free variables.
- **Vessel today:** **Missing.**
- **Gap type:** New feature (watch).
- **Priority:** P3. Track it until upstream merges; it could become the 17.03 import/export format.
- **Recommended Vessel approach:** Do not build yet. When designing 17.03, evaluate reading `coolify.json` v1 as an import format and a Vessel-native manifest with the same `value/required/generate/ref` semantics, planned and applied through Application commands with audit records.
- **Roadmap fit:** 17.02–17.03.
- **Effort:** XL (if adopted)

#### UX-07: New-resource flow (list view, logos, smart scan)
- **Upstream:** v4.3.0 #11140 (simplified new-resource choices with icons); v4.5.0 #8502 (Smart scan detects build pack, Dockerfiles and env vars); v4.7.0 (list view on the new-resource page; resource logo and name on the creation screen; Valkey step for Redis). Detection is covered by the deployments agent.
- **Coolify behavior (now):** A searchable grid or list of all resource types and templates, with scan-assisted Git setup.
- **Vessel today:** **Partial.** There are separate create pages (`ApplicationCreate.razor`, `ApplicationCreateGit.razor`, `DatabaseCreate.razor`, `ServiceTemplates.razor`) and no unified picker.
- **Gap type:** New feature (UX).
- **Priority:** P3.
- **Recommended Vessel approach:** A unified `/new` picker backed by an Application query that merges resource kinds and the TPL-01 catalog (logo, category, tags), reusing UX-01 search.
- **Roadmap fit:** New (UI backlog).
- **Effort:** S

### Reviewed and not applicable

- **Removed Telescope, Debugbar, Ray (v4.2.0 #10847), Sentry (v4.5.0), and Laravel Nightwatch (v4.4.0):** PHP-only tooling. Vessel uses OpenTelemetry and Serilog.
- **Reverb replacing the Soketi container (v4.4.0), the `soketi` replicas-0 placeholder, Pusher env vars, and Reverb `/app/` routing (v4.6.0):** Laravel-specific. Vessel's in-process SignalR is already the equivalent; only the semantics in OPS-03 carry over.
- **`UniqueUntilProcessingJobDecorator`, Horizon (`HORIZON_ALLOWED_EMAILS`, `horizon:drain` on `next`), `SCHEDULED_JOBS_DISPATCH_MODE` sequential versus concurrent:** Laravel queue internals. The intent (single delivery per occurrence) is in NTF-08.
- **Release-process and CI overhaul (v4.6.0: `release.json`/`upstream.json`, `scripts/release.sh`, `plan-version.sh`, `sync-cdn.sh`, `clean-manifests.sh`, `size.sh`, action pinning, SBOM attestations, OCI labels, zstd layers, PR images `ghcr.io/…:pr-N`, nightly cadence):** Coolify release engineering. Only the channel and manifest concept is taken into OPS-01. SBOM and pinning ideas belong to Phase 18 (18.04–18.06), which is out of this domain.
- **The `emails` artisan command removal and "Copy from instance settings" removal (v4.7.0):** UI/CLI detail superseded by NTF-06's system-wide option.
- **Coolify Cloud items (checkout reminder emails, 60-day retention, Stripe sync, Cloud registration email queueing, Cloud multi-node template cache):** Billing and SaaS-only. The multi-node cache intent is noted in TPL-03.
- **Windows/Docker Desktop support removal (v4.6.0):** Vessel never targeted it.
- **Split action buttons, UI scale, theme/brand refresh, breadcrumb linking, digit-box 2FA input, branded steppers, layout-shift fixes:** Cosmetic UI.
- **File browser (v4.5.0 #11518):** Data/storage domain (cross-ref data agent).
- **Image vulnerability scanning notifications (`ImageScan/*`), `HostKeyChanged`, `ApiTokenFailingNotification` producers:** Their producers belong to the security and infra domains. Only their notification-catalog entries are captured in NTF-05.
- **AddServerTimingHeaders HUD:** Dev-only Laravel profiling. Vessel uses OTel instead (optional note in OPS-06).
- **MCP tools for scheduled tasks, secret managers, and notifications (v4.5.0 #12163, v4.6.0):** Vessel has no MCP server. API parity is captured in the respective items; MCP scope belongs to the API agent.
- **Telegram inline-button URL fallback (v4.7.0):** Vessel already sends links as plain text (`TelegramNotificationProvider.cs:23`), so this is covered.

## 15. Methodology and Verification Limits

**Sources consulted (upstream):**
- Blobless clone of `coollabsio/coolify`, `main` at `094f9ada2`. Diff base `49656aa` (v4.1.0).
- GitHub release notes for all 52 tags from v4.1.1 to v4.7.6.
- `database/migrations` (201 added), `app/{Models,Jobs,Actions,Services,Mcp,Http,Notifications,Events,Support,Traits,Policies,Rules,Enums}`, `bootstrap/helpers`, `routes`, `openapi.yaml` (96 → 233 paths), `templates/compose`, and `scripts/`.

**Sources consulted (Vessel):**
- `src/**`, `tests/**`, the EF migrations, `ROADMAP.md`, `PROJECT_REQUIREMENTS.md`, and the per-phase documents under `docs/`.

**Process:**
- Seven parallel domain reviews compared upstream behavior at HEAD with Vessel's actual code and cited `path:line` evidence.
- Release-note claims were confirmed against upstream code wherever an item depends on exact semantics.
- Platform items were verified directly: NuGet and Microsoft release metadata, plus `dotnet restore`/`build`/`test` on SDK 11.0.100.

**Limits:**
- The domain reviews were static reads. Runtime claims about Vessel defects (for example, Traefik rejecting the old YAML escapes) are inferred from the code and from the YAML and Traefik specifications. Where Vessel code was changed, the new behavior is covered by tests.
- Items citing upstream commits after v4.7.6 (on `main`, unreleased) are marked as such in their **Upstream** line. Their semantics may still change.
- `coolify.json` IaC and `feat/proxy-config-history` exist only on unmerged upstream branches.
- Coolify's v5 (`next` branch, Inertia + Svelte) is summarized for direction only (see OPS items). It is not a parity target.
