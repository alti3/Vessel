# Scheduled tasks and the scheduled job runtime (NTF-07, NTF-08)

Upstream reference: Coolify `app/Models/ScheduledTask.php`, `app/Jobs/ScheduledTaskJob.php`,
`app/Services/ScheduledJobDeliveryService.php` (v4.3–v4.7: `use_shell`, failure tracking and auto-disable, run only in
running containers, one execution per occurrence, missed runs, worker host).

## Scheduled job ledger (all Vessel schedules)

Every schedule (database backups, scheduled tasks, scheduled control-plane dumps, the update check; volume backups
plug in through the same `IScheduledJobSource` interface with kind `VolumeBackup`) is dispatched by one recurring
Hangfire job, `scheduled-jobs:dispatch`, every minute under the distributed lock `scheduled-jobs:dispatch`.

* Cron expressions have five fields (or a macro such as `@daily`, or Coolify's `every_minute`/`hourly`/…; a six-field
  expression is accepted only with a `0` seconds field). They are evaluated in the schedule's IANA time zone, or the
  instance default (`api/v1/instance/scheduling/settings`, default `UTC`). A local time skipped by a DST jump runs at the
  end of the gap; a repeated local hour runs once.
* Each occurrence becomes one row in `scheduled_job_runs` (unique per kind, schedule and instant for scheduled runs)
  with status `Due → Started → Succeeded | Failed | Skipped`, or `Missed`. The queued Hangfire job carries only the run
  ID; the worker claims the run (optimistic concurrency) and records its host (`WorkerHost`).
* Hangfire's global ten-retry policy is replaced by `VesselRetryPolicyFilter`: jobs marked `[JobRetryPolicy(0)]` (the
  dispatcher, the run job, the legacy backup job, control-plane backup jobs) are never retried, so a retry can no longer
  create a duplicate backup execution or run a command twice. Other jobs keep ten attempts.
* Catch-up: occurrences up to 10 minutes late run (only the newest when several are pending). Older ones (control plane
  down) are recorded as one `Missed` run and notified (`scheduled_job.missed`). With the catch-up policy `RunOnce`
  (per schedule or instance default) the newest missed occurrence runs once; with `Skip` (default) it does not.
* Sweeper: a `Due` run not started within 60 minutes is re-enqueued once for backups/update checks and marked missed for
  tasks; a `Started` run older than its timeout plus 15 minutes is failed ("the worker stopped") and never re-run.
* Existing per-schedule Hangfire recurring jobs (`backup:{id}`) remove themselves when they fire and never run a backup.
* Backup schedules gained optional `timeZone` and `missedRunPolicy` fields.
* Admin view of the ledger: `GET api/v1/instance/scheduling/runs?kind=&status=`.

## Scheduled tasks

A task runs a command inside a running container of an application, database or service:

| Field | Notes |
| --- | --- |
| `name`, `command` | The command is never audited or sent in notifications (it may contain credentials). |
| `cronExpression`, `timeZone`, `missedRunPolicy` | See the ledger. |
| `containerName` | Required when the resource runs containers of several compose services; matches the container name or the compose service name, and must belong to the resource. |
| `timeoutSeconds` | 10–36000, default 300. |
| `useShell` | `true` (default): `<runtime> exec <container> sh -c <command>`. `false`: the command is split into arguments like a POSIX shell (quotes, backslashes) without any expansion and executed directly, for images without a shell. |
| `enabled` | Ten consecutive failures with the first failure at least 24 hours old disable the task with a `disabledReason`; enabling it again starts a new streak. |

Execution goes through `IServerCommandRunner` as a structured argument vector (local runtime or SSH, never a host shell
string). Output is streamed to the owning team's realtime group (`scheduled_task.output`, `scheduled_task.status`) and
stored redacted (pattern redaction plus the resource's secret environment values and database credentials), capped at
256 KiB. Output and failure messages are returned only to callers who can change the resource or read sensitive data.

* A stopped resource (no running container) is skipped: no notification, no failure count.
* A run is skipped while the previous run of the task is still running (lock `scheduled-task:{id}`).
* Failures raise `scheduled_task.failed` (daily throttle); the first success after failures raises
  `scheduled_task.recovered`.
* Tasks on host-local servers (the control-plane host) can be created, changed and run only by instance administrators;
  unattended runs need an instance administrator as Owner/Admin of the team.
* Known limit: when the timeout stops `docker exec`, the process inside the container may keep running (same as
  upstream); keep commands bounded with their own timeouts where possible.

### API

| Method and path | Permission |
| --- | --- |
| `GET /api/v1/{applications\|databases\|services}/{id}/scheduled-tasks` | `applications.read` |
| `POST /api/v1/{applications\|databases\|services}/{id}/scheduled-tasks` | `applications.write` + `deployments.start` |
| `GET /api/v1/scheduled-tasks/{taskId}` | `applications.read` |
| `PATCH /api/v1/scheduled-tasks/{taskId}` | `applications.write` (+ `deployments.start` when the command, container, shell mode, schedule or enabling changes) |
| `DELETE /api/v1/scheduled-tasks/{taskId}` | `applications.delete` |
| `POST /api/v1/scheduled-tasks/{taskId}/run` | `deployments.start` (token ability `deploy`), returns 202 |
| `GET /api/v1/scheduled-tasks/{taskId}/executions` | `applications.read` (output hidden without write or sensitive read) |
| `GET /api/v1/scheduled-tasks/{taskId}/runs` | `applications.read` |

Durations are JSON numbers (seconds).
