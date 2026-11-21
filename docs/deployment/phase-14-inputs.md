# Phase 14 inputs: update channels, pre-update backup, upgrade status and draining

Status: Application services and admin API exist (OPS-01, OPS-02, DATA-13). The binary swap, restart, migration run,
health check and rollback remain Phase 14 work in the `vessel self-update` CLI and systemd units. Nothing in the
control plane starts a detached process or replaces its own binary.

Upstream reference: Coolify v4.5–v4.7 `app/Actions/Server/UpdateCoolify.php` (`backupDatabase()`, fail closed),
`app/Models/InstanceSettings.php::switchUpdateChannel`, `app/Services/AutoUpdateNotifier.php`,
`app/Jobs/CheckForUpdatesJob.php`, `scripts/upgrade.sh` (status file, single-flight), migration
`2026_10_15_213357_spread_default_auto_update_frequency`.

## Update channels and policy (OPS-01)

The instance follows one channel: `stable`, `preview` (betas and release candidates), `nightly`, or `custom` (a pinned
version or tag). The policy is a System setting (`operations.update_policy`), managed by instance administrators:

| Field | Default | Notes |
| --- | --- | --- |
| `channel` | `stable` | Switching to a more stable channel records `previousChannel`; the CLI should keep offering fixes from the previous channel until the stable channel ships a version at least as new as the installed one (upstream `previous_update_channel`). |
| `customVersion` | none | Used only with `custom`. |
| `manifestUrl` | none | No manifest URL means **no outbound update traffic** and no scheduled check. |
| `checksumsUrl` | `SHA256SUMS` next to the manifest | |
| `autoUpdateEnabled` | `false` | When on, an available version is written to `autoUpdateTargetVersion` for the CLI. |
| `checkCron` | random daily minute and hour, generated once and persisted | Spreads checks so instances never hit the release CDN at the same minute. |

The check (`POST /api/v1/instance/operations/update-check`, or the daily ledger schedule `UpdateCheck`) fetches the
manifest through the SSRF-guarded outbound client (bounded size, 20 s timeout), verifies it, compares versions
(semantic versioning, pre-releases sort before their release, a leading `v` is ignored), records the result in the
policy and raises `platform.update_available` once per new version to every team that has an instance administrator
as Owner/Admin.

### Release manifest (`releases.json`)

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-11-20T00:00:00Z",
  "channels": {
    "stable":  { "version": "1.4.2", "publishedAt": "…", "notesUrl": "…",
                 "artifacts": [ { "os": "linux", "arch": "x64", "url": "…/vessel-linux-x64.tar.gz", "sha256": "…" } ] },
    "preview": { "version": "1.5.0-rc.1", "artifacts": [ … ] },
    "nightly": { "version": "1.5.0-nightly.20261120", "artifacts": [ … ] }
  },
  "signature": { "scheme": "minisign-ed25519", "keyId": "…", "signatureUrl": "…/SHA256SUMS.minisig" }
}
```

### Integrity

* Implemented: the `SHA256SUMS` list (`sha256sum` format, `<64 hex>  <file name>`, optional `*` binary marker) is parsed
  strictly (malformed lines, duplicates, path separators and control characters reject the whole list); the manifest's
  own file name must be listed and match the fetched bytes (constant-time comparison). Artifact `sha256` values come from
  the verified manifest, and the CLI must verify each downloaded artifact against them before swapping.
* TODO (Phase 14 / 18.05): signature verification. Chosen scheme: a detached **minisign (Ed25519)** signature over
  `SHA256SUMS`, with the release public key pinned in the binary (and rotatable through a signed key-rotation entry).
  The seam is `IManifestSignatureVerifier`; the current implementation reports `NotConfigured` and never `Valid`, so a
  check can be trusted only as far as TLS plus checksums allow until this lands.

## Pre-update control-plane database backup (DATA-13, OPS-02)

`ControlPlaneBackupService` dumps Vessel's own PostgreSQL with `pg_dump --format=custom` through `IProcessRunner`:

* Host, port, user and database are separate arguments; the password is passed only as `PGPASSWORD` in a child
  environment that inherits nothing from the daemon, and it is redacted from captured output.
* Files go to the owned directory `Vessel:Operations:ControlPlaneBackups:Directory` (default
  `/var/lib/vessel/backups/control-plane`) under `manual/`, `pre-update/` or `scheduled/`, validated with
  `IPathSafetyService` (no traversal, no symlinks), written as `*.partial` and renamed atomically, mode `0600`
  (directories `0700`), with a `<file>.sha256` checksum file.
* Retention keeps the newest N dumps per trigger (default 10); a dump referenced by an unfinished upgrade is never pruned.
* A low-disk condition only logs a warning (upstream removed its 5 GB hard block because it blocked updates).
* `pg_dump` must be installed on the host and its major version must be at least the server's major version.
* Instance settings (`operations.instance_backup`): `enabled`, `beforeUpdate` (default **true**), `schedule` (optional
  cron, UTC, dispatched by the scheduled job ledger as `ControlPlaneBackup` with catch-up policy run-once),
  `retentionCount`.
* Failures raise `platform.control_plane_backup_failed`.

Restore (operator procedure, Vessel stopped):

```bash
sudo systemctl stop vessel
sha256sum -c /var/lib/vessel/backups/control-plane/pre-update/vessel-<timestamp>.dump.sha256
pg_restore --clean --if-exists --no-owner --dbname "postgres://vessel@db/vessel" \
  /var/lib/vessel/backups/control-plane/pre-update/vessel-<timestamp>.dump
# reinstall the previous binary, then
sudo systemctl start vessel
```

PostgreSQL major upgrades of Vessel's own database are never automatic: Phase 14 should ship an explicit
`vessel db upgrade-postgres` procedure (dump with the old major, restore into the new one, verify), mirroring upstream's
separate `upgrade-postgres.sh`.

## Upgrade status and single-flight (OPS-02)

`UpgradeCoordinator` owns the `upgrade_runs` record that the UI/API polls (`GET /api/v1/instance/operations/upgrade`):

1. Single-flight: distributed lock `vessel:self-update` plus "no unfinished run". A run without heartbeat for
   10 minutes (`UpgradeRun.StaleAfter`) is failed as abandoned.
2. `BackingUp`: pre-update dump when `beforeUpdate` is on and `skipBackup` was not requested. **Fails closed** with
   "The database backup failed, so the update was not started."
3. `Draining`: see below; a drain that does not finish in time (default 10 minutes) fails the run and releases the drain.
4. `ReadyForSwap`: hand-off to the CLI, which reports `Swapping`, `Migrating`, `Verifying` and then `Succeeded`, `Failed`
   or `RolledBack` (`POST …/upgrade/{id}/report`, or `ReportFromHostAsync` in-process). Terminal reports release the
   drain. `POST …/upgrade/{id}/abort` works until the swap started.

Notifications: `platform.update_started`, `platform.update_failed`, `platform.update_succeeded`.

## Job draining (OPS-02)

* `operations.drain` (System setting) is consulted by deployment admission (`DeploymentQueueService.AdmitAsync`), so no
  trigger (manual, API, webhook, preview, rollback) starts a deployment while draining.
* `DrainService.WaitForIdleAsync` polls every `IInFlightWorkProbe` for **this worker host** (upstream v4.6.0 "wait only
  for jobs on this host"): running deployments and started scheduled-job ledger runs.
* Instance administrators can also start/release a drain manually (`POST …/drain/start`, `POST …/drain/release`).

## What the Phase 14 CLI must do

1. `BeginFromHostAsync(target)` → stop if it throws (another upgrade, backup failure, drain timeout).
2. Download the artifact from the verified manifest, verify its SHA-256 (and, once implemented, the signature).
3. Report `Swapping`; atomically swap the binary (keep the previous one); `systemctl restart vessel`.
4. Report `Migrating` / `Verifying`; wait until the new process answers its health endpoint with the target version.
5. Report `Succeeded`, or roll back the binary, report `RolledBack`, and offer the pre-update dump for restore when
   migrations had run.

## API summary

All under `api/v1/instance/operations`, interactive sessions of instance administrators only: `update-policy`
(GET/PUT), `update-check` (POST), `backups` (GET/POST, `{id}/download`, DELETE `{id}`), `backup-settings` (GET/PUT),
`upgrade` (GET, POST begin, `{id}/report`, `{id}/abort`), `drain` (GET, `start`, `release`).
