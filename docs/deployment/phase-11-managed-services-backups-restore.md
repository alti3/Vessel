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
- Lifecycle actions, backups and restores take the same lock key (`database-op:{databaseId}`, `DatabaseOperationLocks`), so they cannot overlap on the same database. A backup that finds the lock taken is recorded as failed (skipped) instead of waiting.
- Backup and restore commands talk to the local Docker/Podman socket, which is host-root-equivalent.
- Object storage remains abstracted behind `IObjectStorage`; local object storage is suitable for development only.
- Current runtime execution supports local Docker/Podman targets. SSH target execution remains behind the same runtime abstractions for a later hardening pass.

## Verification

Phase gate verification for this phase should include:

```powershell
dotnet build Vessel.slnx --artifacts-path artifacts\phase11-build
dotnet test Vessel.slnx --no-restore --artifacts-path artifacts\phase11-build --verbosity minimal
tools\validate-project-references.ps1
```
