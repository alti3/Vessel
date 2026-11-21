# Phase 11: Managed Services, Backups, and Restore

Phase 11 adds the first managed-service surface for databases, common service templates, and explicit backup/restore workflows.

## Upstream Coolify Areas Consulted

- `app/Actions/Database/StartDatabase.php`, `StartPostgresql.php`, `StartMysql.php`, `StartMariadb.php`, `StartRedis.php`, `StopDatabase.php`, and `RestartDatabase.php`
- `app/Jobs/DatabaseBackupJob.php`
- `app/Models/ScheduledDatabaseBackup.php` and `ScheduledDatabaseBackupExecution.php`
- `templates/service-templates.json` and `templates/compose/*.yaml`
- backup/S3-related migrations including `scheduled_database_backups`, `scheduled_database_backup_executions`, and `s3_storages`

Vessel preserves the product semantics: standalone databases are managed resources, lifecycle actions are queued and locked, backup schedules retain execution history, artifacts can live in object storage, and restore is explicit with confirmation and audit records. The implementation is not a PHP/Laravel port.

## Vessel Behavior

- PostgreSQL, MySQL, MariaDB, and Redis database resources can be queued for start, stop, restart, delete, and inspect lifecycle actions.
- Lifecycle jobs delegate to `ManagedDatabaseService`; external runtime operations remain behind `IContainerRuntimeClient`.
- Service templates are curated through `ServiceTemplateCatalog`; initial templates include pgAdmin, Redis Insight, and MinIO.
- Backup schedules persist cron, retention count, storage target, last run time, and execution history.
- Backup executions persist artifact location, size, checksum, protected/pruned state, status, safe backup failure reason, and last restore failure metadata.
- Restore requires `RESTORE` confirmation for destructive restores and is queued as a Hangfire job (see "Backup and restore pipeline" below).

## Backup and Restore Pipeline (Wave 1 DATA-01/DATA-02)

Upstream areas consulted: `app/Jobs/DatabaseBackupJob.php`, `app/Support/BackupCompression.php`, `app/Actions/Database/StartDatabaseImport.php`, `app/Support/DatabaseImport/DatabaseImportCommandBuilder.php`, `app/Support/DatabaseBackupFileValidator.php`.

Backups:

- Dumps run inside the database container through the server's container runtime CLI (`docker` or `podman`, resolved from `ContainerRuntimeTarget`), via `IProcessRunner.RunStreamingAsync`. Output streams straight to an owned temporary file under `storage/backup-scratch/` (owner-only permissions, path-validated, deleted after upload); nothing is buffered in memory and there is no 10 MiB limit.
- PostgreSQL uses `pg_dump --format=custom` (compressed by pg_dump). MySQL/MariaDB dump the user databases only (`--databases`, system schemas excluded, `mariadb-dump` for MariaDB) and Redis uses `redis-cli --rdb -`; both are gzip-compressed while streaming. Size and SHA-256 are computed while streaming and recorded on the execution.
- Each schedule has a `TimeoutSeconds` deadline (default 3600, 60–86400) covering dump and upload; manual backups use the default. A timed-out run is recorded as failed and its temp file removed.
- S3 uploads use multipart transfers with a part size that keeps uploads at or under 1,000 parts. `Vessel:ObjectStorage:TransferTimeoutSeconds` (default 900) is the per-request timeout for transfers; `TimeoutSeconds` (default 5) still applies to control calls.

Restores:

- `POST /api/v1/databases/backups/{backupId}/restore/{targetId}` validates and queues a `DatabaseRestore` record and returns `202 Accepted`; `GET /api/v1/databases/restores/{restoreId}` returns its status and incrementally persisted log. A second restore for the same target while one is queued or running returns `409 Conflict`. Options: `dryRun`, `confirmation` (`RESTORE`), `replaceExisting` and `keepOwners` (PostgreSQL archives only).
- Before anything is changed the job verifies the artifact's SHA-256 against the recorded checksum, decompresses and integrity-checks gzip, detects the format from content (PostgreSQL custom/tar archive, PostgreSQL SQL, MySQL SQL, Redis RDB), and rejects artifacts whose engine does not match the source and target databases.
- PostgreSQL archives restore with `pg_restore --exit-on-error --single-transaction` (`--no-owner --no-privileges` unless `keepOwners`, `--clean --if-exists` only with `replaceExisting`); SQL dumps restore with `psql --variable=ON_ERROR_STOP=1 --single-transaction --no-psqlrc`. Both are scanned first (archives through `pg_restore` without a database) and blocked when they contain psql meta-commands that run a shell or use files (`\!`, `\o |`, `\copy ... program`, `\i`, backquotes), `COPY ... TO/FROM PROGRAM`, `lo_import`/`lo_export`, or `ALTER SYSTEM`. Static scanning cannot see through every form of dynamic SQL, which is why the checksum check is mandatory for Vessel-produced artifacts.
- MySQL/MariaDB restores drop the system databases from the dump (users, passwords and privileges are never restored), reject client commands (`system`, `tee`, `pager`, `edit`, `source` and their short forms) and run the client with `--binary-mode`. MySQL has no transactional DDL: statements before a failure stay applied.
- Redis restores no longer replay the RDB through `--pipe` (an RDB snapshot is not a command stream). The job reads `appendonly`, `dir` and `dbfilename`, refuses when AOF is enabled, stops the container, copies the RDB over the dump file, and always starts the container again.
- Restores have a 6 hour deadline and a job retry never re-runs a restore that already started.

## Safety Notes

- Secrets are stored through existing secret references and are redacted before compose snapshots, failure reasons, audit metadata, and process output persistence.
- Lifecycle actions, backups and restores take the same lock key (`database-op:{databaseId}`, `DatabaseOperationLocks`), so they cannot overlap on the same database. A backup that finds the lock taken (or a database that is not running) is recorded with status `Skipped` instead of waiting; skipped runs are neither failures nor successes and send no notification. Lifecycle requests are refused with `409 Conflict` while the lock is held, a backup runs, or a restore is queued/running; a lifecycle job that still finds the lock taken is skipped without marking the database failed.
- Backup and restore commands talk to the local Docker/Podman socket, which is host-root-equivalent.
- Backup copies no longer go to a hard-coded instance bucket. Each run keeps a local copy under an owned directory on the control-plane host (`Vessel:Backups:LocalRootDirectory`, default `{app}/storage/backups`, bucket directory `database-backups`) unless the schedule is remote-only, and uploads one replica per selected team destination (see below).
- Current runtime execution supports local Docker/Podman targets. SSH target execution remains behind the same runtime abstractions for a later hardening pass.

## Wave 2 (DATA-03/04/05/06/07/08/11/14, PLT-06)

Upstream areas consulted: `app/Traits/HasDatabaseHealthCheck.php`, `app/Actions/Database/Start*.php`, migrations `2026_11_06_083240_update_standalone_database_default_images` and `2026_11_06_113843_set_valkey_as_standalone_redis_default_image`, `app/Rules/ValidS3BucketName.php` / `ValidationPatterns::isValidS3BucketName`, `templates/compose/*.yaml`, CHANGELOG v4.7.x.

- **Health (DATA-04).** Every generated database compose file carries an engine health check (defaults: enabled, 15 s interval, 5 s timeout, 5 retries, 5 s start period; stored per database). Probes read passwords from the container environment at probe time (`$$POSTGRES_PASSWORD`), so no password appears in the health-check command line. A started database is `Starting`, never assumed `Healthy`; the `Inspect` action and the `databases:health-poll` recurring job (every minute) map `docker/podman inspect` state to `Starting`/`Healthy`/`Unhealthy`/`Unknown`.
- **Credentials (DATA-05).** The `.env` passed with `--env-file` is written by `ComposeEnvironmentFile`: every value double-quoted, `$` escaped as `$$`, `\` and `"` escaped, line breaks as `\n`. Values ending in a backslash are refused (Compose would read the closing quote as escaped). Redis-family servers get the password through exec-form `command` arrays, so no shell quoting is involved. No legacy-quoting flag is needed: Redis re-applies the password on every start, and PostgreSQL/MySQL databases initialised with a mangled password were already failing backups.
- **Images (DATA-11, PLT-06).** New databases run a pinned image from `Application/Common/ComponentVersions` (`postgres:18-alpine`, `valkey/valkey:9-alpine` for Redis with `redis:8` selectable, `mongo:9`, `mysql:9.7`, `mariadb:12.3`, `clickhouse/clickhouse-server:26.8`, KeyDB and Dragonfly release tags); `latest` and untagged references are rejected, and a custom image (for example Supabase or PostGIS) can be supplied. Existing databases keep their `{repository}:{Version}` image. Database, tunnel and vector-store service templates (cloudflared 2026.11.1, valkey, keydb, dragonfly, timescale, qdrant, chroma) read their pins from the same manifest. KeyDB only publishes per-architecture tags; arm64 hosts should use a custom image.
- **Destinations (DATA-06).** `/api/v1/backup-destinations` (GET list/item with `projects.read`; POST, PATCH, POST `{id}/validate`, DELETE with `secrets.write`) manages team-owned S3-compatible storages: endpoint (absolute http/https, no user info or query), region, bucket (upstream `ValidS3BucketName` rules, upper case accepted), path-style (default on), skip-TLS-verification (explicit opt-in, audited on every change). Access keys are stored as one secret in the vault and never returned. A connection test (list one object) runs on save; failures mark the destination unusable and notify the team once (`backup.destination_unusable`); `backup-destinations:revalidate` re-tests unusable destinations hourly. Endpoints are checked against the outbound request policy and every connection goes through the guarded handler (address pinning, no proxy, no redirects), so private endpoints must be allow-listed. Deleting a destination detaches it from schedules (a remote-only schedule gets its local copy back) and keeps replica history without the reference.
- **Schedules (DATA-07).** Schedules select all databases (default; PostgreSQL expands into one execution per connectable database, MySQL/MariaDB dump every user database), named databases (one execution each) or the default database; a dump format (PostgreSQL custom archive or plain SQL); local and remote retention by count, days and size; a timeout; remote destinations; remote-only mode; missing-backup alert days. Editing and disabling sync the Hangfire recurring job (disabled schedules never fire; a stale job removes itself); deleting a schedule deletes its executions (cascade) and optionally their files. PostgreSQL restores target the database the backup was taken from. The redundant `BackupPolicy` model is removed.
- **Notifications (DATA-08).** Success and failure events are published for every run; scheduled failures notify on the first failure and then at most once a day, and the first success after failures sends `backup.recovered`. A failed remote upload counts as a failure for throttling. `backups:missing-check` (hourly, lock `backups:missing-check`) sends `backup.missing` once per idle period when a schedule has no successful backup for its configured days.
- **API (DATA-14).** `GET /api/v1/databases/{id}/backups?page=`, `GET /api/v1/databases/{id}/backup-schedules`, `PATCH`/`DELETE /api/v1/databases/backup-schedules/{id}?deleteArtifacts=`, `GET /api/v1/databases/backup-schedules/{id}/executions?page=`, `DELETE /api/v1/databases/backups/{id}?deleteRemote=`, `GET /api/v1/databases/backups/{id}/download` (streamed, requires `secrets.read`, audited). Execution failure/skip reasons and replica messages are returned only to callers with `secrets.read` (`messageHidden` tells others a message exists).

## Verification

Phase gate verification for this phase should include:

```powershell
dotnet build Vessel.slnx --artifacts-path artifacts\phase11-build
dotnet test Vessel.slnx --no-restore --artifacts-path artifacts\phase11-build --verbosity minimal
tools\validate-project-references.ps1
```
