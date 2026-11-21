# Phase 11b: persistent storage, volume backups and database imports

Covers gap-report items DATA-12, DATA-09 and DATA-10.

## Persistent storage (DATA-12)

Applications and services have storage entries:

| Type | Fields | On the server |
| --- | --- | --- |
| `Volume` | `name`, `mountPath`, `serviceName?` | Named runtime volume `vessel-{ownerId:N}-{name}` (previews: `…-pr-{n}`) |
| `File` | `path`, `mountPath`, `content?` or `isHostFile` | File in the resource directory |
| `Directory` | `path`, `mountPath` | Directory in the resource directory |

- **No host paths.** Upstream Coolify v4.3.22 removed `host_path`. Vessel rejects any request that carries
  `host_path`/`hostPath` (`400 host_path_not_supported`). `path` must be relative to the resource directory. Absolute
  paths, `~`, `$VAR` and `..` are rejected.
- The resource directory is `resources/{applications|services}/{id:N}` under the server file channel root
  (`Vessel:ServerFiles`, default `/var/lib/vessel` on SSH servers). Previews use `…/{id:N}-pr-{n}`, so they never share
  volumes or files with production.
- File content is at most 5 MiB of text. It is encrypted at rest with the secret key ring, and only callers with
  `secrets.read` can read it (`GET …/storages/{id}?includeContent=true`). The API never writes to the server. The next
  deployment writes content files before `compose up`. A file is overwritten only when its content changed in Vessel;
  otherwise an existing file is kept. Host files are only created (empty) when they are missing. Bind mounts set
  `create_host_path: false`.
- Renaming a volume needs `confirmRename: true`, because the new name mounts a new, empty volume. Entries with a backup
  schedule cannot be deleted or renamed. Deleting an entry keeps its data on the server.
- Dockerfile apps mount entries into the single service. Compose apps and services mount them into `serviceName`, or
  into the primary service (or the only service) when `serviceName` is not set. A target path that the compose file
  already mounts is rejected.

API: `GET|POST /api/v1/{applications|services}/{id}/storages`, `GET|PATCH|DELETE …/storages/{storageId}`.

## Volume backups (DATA-09)

- There is one schedule per volume or directory mount: `POST …/storages/{storageId}/backups`. Manage schedules with
  `PATCH|DELETE /api/v1/volume-backups/{scheduleId}` (deleting also deletes archives unless `deleteArtifacts=false`),
  start a manual run with `POST …/{scheduleId}/run`, and list runs with `GET …/{scheduleId}/executions`.
- A run starts a helper container (`ComponentVersions.VolumeBackupHelperImage`, `busybox:1.37.0`) on the resource's
  server. It uses `--network none` and `no-new-privileges`, and mounts the target read-only at `/volume`. It runs
  `tar -czf -`, and the archive streams over the server command channel (local process or SSH) into an owned scratch
  file. It is hashed while it is written. tar exit code 1 (files changed while they were read) is reported as a
  warning.
- `stopDuringBackup` stops the containers that mount the target and starts them again. Their IDs are saved before they
  are stopped. If the worker dies, the recovery job (every 5 minutes) starts them again. It makes at most 5 attempts,
  5 minutes apart, within 7 days. After that the execution is marked "needs attention" and the team is notified. Retry
  with `POST /api/v1/volume-backups/executions/{id}/recover`.
- Archives are stored as a local copy and/or on team backup destinations (DATA-06), with separate local and remote
  retention. Default retention keeps 7 copies. The default timeout is 10 hours.
- Notifications reuse the backup events: success, failure (throttled daily), recovered, missing backup (hourly check,
  `missingAlertDays`) and restore failure.
- To restore, call `POST /api/v1/volume-backups/executions/{id}/restore` with `{ "confirmation": "RESTORE",
  "replaceExisting": true }`. The restore runs as a job. It verifies the checksum and gzip integrity, stops the mounting
  containers, extracts into the target and starts the containers again. Check progress with
  `GET /api/v1/volume-backups/restores/{id}`.
- Backups and restores of one target never overlap: they share the lock `volume-backup:{kind}:{targetId}`.
- On host-local servers, creating or changing schedules, manual runs and restores require an instance administrator.
  Unattended runs require an instance administrator in the owning team.

## Database imports (DATA-10)

- Upload in chunks:
  1. `POST /api/v1/databases/{id}/imports/uploads` with `{ fileName, sizeBytes }`.
  2. `PUT …/uploads/{uploadId}/chunks?offset=N` with the raw body. Chunks are sequential and at most 64 MiB each.
  3. `POST …/uploads/{uploadId}/complete`.

  Uploads are staged owner-only in `Vessel:DatabaseImports:StagingDirectory`. The total size limit is
  `MaxImportBytes` (default 10 GiB). Uploads expire after 24 hours, and an hourly job deletes them.
- Start an import with `POST /api/v1/databases/{id}/imports` and `source` set to `upload`, `server` or `destination`:
  - `upload`: imports a completed upload.
  - `server`: imports a file on the control-plane host. Instance administrators only; the permission is checked before
    the path is inspected. The path must be absolute and a regular file, and it must be inside
    `Vessel:DatabaseImports:ServerPathRoots` when that is set. Set it on host-native installs.
  - `destination`: imports an object (`path`) from a team backup destination.
- An import is a `DatabaseRestore` without a backup execution. It runs through the same pipeline as a restore: format
  detection, engine compatibility, safety scanning and a single-transaction restore. Check its status and log with
  `GET …/imports/{importId}` (realtime event `database.restore`).
