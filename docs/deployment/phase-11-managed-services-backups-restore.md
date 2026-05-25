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
- Backup executions persist artifact location, size, checksum, protected/pruned state, status, and safe failure reason.
- Restore requires `RESTORE` confirmation for destructive restores, uses a per-database lock, validates artifact readability, and records audit metadata.

## Safety Notes

- Secrets are stored through existing secret references and are redacted before compose snapshots, failure reasons, audit metadata, and process output persistence.
- Backups and restores use locks named by database ID so backup, restore, and lifecycle actions cannot overlap on the same database.
- Object storage remains abstracted behind `IObjectStorage`; local object storage is suitable for development only.
- Current runtime execution supports local Docker/Podman targets. SSH target execution remains behind the same runtime abstractions for a later hardening pass.

## Verification

Phase gate verification for this phase should include:

```powershell
dotnet build Vessel.slnx --artifacts-path artifacts\phase11-build
dotnet test Vessel.slnx --no-restore --artifacts-path artifacts\phase11-build --verbosity minimal
tools\validate-project-references.ps1
```
