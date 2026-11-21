# Server monitoring, runtime status, runtime logs and Docker cleanup

This page covers the SRV-03, SRV-04, SRV-05, SRV-07 and SRV-08 work (Wave 2). For why Vessel collects agentlessly and
does not run Coolify's Sentinel container, see [ADR-0009](../decisions/ADR-0009-host-metrics-collection.md).

## Upstream Coolify areas consulted

- `app/Jobs/ServerStorageCheckJob.php`, `app/Notifications/Server/HighDiskUsage.php` (recovery margin of 5 points)
- `app/Jobs/ServerConnectionCheckJob.php` (`UNREACHABLE_THRESHOLD = 2`)
- `app/Services/ContainerStatusAggregator.php` (status priority rules), `app/Traits/HasRestartLimit.php`,
  `app/Services/RestartCountTracker.php`, `app/Actions/Docker/GetContainersStatus.php`
- `app/Jobs/NotifyResourceStoppedJob.php` (120 s confirmation, expected-stop window)
- `app/Actions/Server/CleanupDocker.php` (managed-image protection, build cache kept during deployments, opt-in
  volumes and networks)
- `app/Support/RuntimeLogTargets.php`, `app/Services/Security/SensitiveDataRedactor.php` (merged replica streams,
  redacted exports)

## Metrics (SRV-03)

**Recurring jobs**

| Job | Schedule | What it does |
| --- | --- | --- |
| `servers.metrics.dispatch` | every minute | Schedules `CollectServerMetricsJob` per server with a stable 0–44 s offset. |
| `servers.metrics.retention` | hourly, at :05 | Creates the hourly rollups and deletes expired rows. |

Retention: raw samples and container samples are kept for 24 hours, and hourly rollups for 30 days.

**Per-server settings**: `GET`/`PATCH /api/v1/servers/{id}/monitoring` (`servers.read` / `servers.write`)

| Setting | Default | Range |
| --- | --- | --- |
| `metricsEnabled` | `true` | |
| `metricsIntervalSeconds` | 60 | 30–3600 |
| `diskAlertThresholdPercent` | 80 | 1–99 |
| `diskAlertIntervalHours` | 24 | 1–168 |

**Endpoints**

- `GET /api/v1/servers/{id}/metrics?from=&to=&resolution=auto|raw|hourly`. Ranges are capped at 30 days. The response
  includes the latest point, the pressure levels and the latest container samples.
- `GET /api/v1/metrics/fleet` returns the active team's readable servers with KPIs and the top 20 containers by CPU
  and by memory.
- `POST /api/v1/servers/{id}/metrics/collect` runs a collection now. It needs `servers.write`, and an instance
  administrator for host-local servers.

**Realtime**: each collection publishes `server.metrics` to the server group of `/hubs/server-status`.

## Runtime status and restart limits (SRV-04)

**How status is reconciled**

- Each collection reconciles application, database and service status from the real containers. Containers are
  matched by labels: `vessel.application`, `vessel.database`, or the `vessel-svc-…` compose project. Preview
  containers are excluded.
- Status is stored in `resource_runtime_statuses`, a table separate from each resource's own configuration row. The
  frequent status writes therefore never conflict with configuration edits.
- A resource with no containers is marked exited only when the container inventory command succeeded.

**Stopped-unexpectedly notification** (`application.status_changed`, NTF-04)

When an up resource turns exited and Vessel did not expect the stop, `NotifyResourceStoppedJob` is scheduled
120 seconds later. It sends the notification only if every one of these is still true:

- The resource is still exited and has not changed state since.
- It has not reached its restart limit.
- No expected-stop window is open.
- No deployment is queued or running for it.
- A database or service is still in its `Running` lifecycle state. A stop Vessel ran is expected.

The catalog throttle allows at most one of these notifications per resource every 15 minutes.

**Restart limit** (opt-in, upstream `max_restart_count`)

- `PUT /api/v1/{applications|databases|services}/{id}/restart-limit` with `{ "maxRestartCount": n }` sets the limit.
  `0` (the default) disables it.
- When the containers' `RestartCount` reaches the limit, Vessel does the following once:
  - claims the limit
  - stops the containers and keeps them for inspection
  - opens an expected-stop window
  - sends `application.restart_limit_reached`

  While a container still runs, Vessel stops it again.
- A new container generation running below the limit clears the flag. `POST …/restart-limit/reset` clears it by hand.
  Both endpoints need `resources.lifecycle`.
- `GET …/runtime-status` returns the current state, health, container counts and restart counters.

## Runtime logs (SRV-05)

**Endpoints**

- `GET /api/v1/{applications|databases|services}/{id}/logs?lines=100|all|-1&since=&timestamps=true`
- `GET …/logs/export` downloads the logs as plain text.

**What is included**

- Every container of the resource is merged into one stream ordered by timestamp. That includes replicas, compose
  services, and stopped or leftover containers.
- Up to 10,000 lines are returned.

**Live follow**

- Live follow uses the `/hubs/runtime-logs` SignalR stream method `Follow(kind, id, tail, timestamps)`.
- The browser sends only the resource kind and id. The containers are resolved on the server.
- Access is re-checked every 30 seconds, and a stream lasts at most 2 hours.

**Security**

- Logs require the new `resources.readLogs` permission. It is at the read-sensitive level, so read-only members do
  not hold it. Upstream likewise shows logs only to users who can update the resource and needs `read:sensitive` on
  the API.
- Host-local servers also require an instance administrator.
- Every line is redacted, including in exports, with the resource's secret context and the redactor's patterns:
  - applications: their applicable secret environment values
  - databases: their credentials
  - services: secret-like template inputs

## Docker cleanup and the Images page (SRV-07)

**Policy**: `GET`/`PATCH /api/v1/servers/{id}/docker-cleanup`

| Setting | Default |
| --- | --- |
| `scheduleEnabled` | `true` |
| `cronExpression` | `0 0 * * *` |
| `thresholdPercent` | 80 |
| `forceScheduledCleanup` | `true` |
| `deleteUnusedVolumes` | `false` |
| `deleteUnusedNetworks` | `false` |

`servers.docker-cleanup.sync` runs hourly. It keeps each server's `servers.docker-cleanup.{id}` recurring job in line
with its policy and marks runs older than 30 minutes as interrupted (failed).

**Runs**

- `POST …/docker-cleanup/run` queues a manual run (`servers.write`). A manual run waits for a cleanup that is already
  running.
- `GET …/docker-cleanup/executions` lists the history: status, reclaimed bytes, the containers and images removed, and
  a step log.
- Disk-threshold runs start after a metrics sample at or above the threshold, at most once an hour.
- Scheduled and threshold runs skip themselves while a deployment is active on the server.

**What a run removes**

- Stopped Vessel containers that belong to no existing resource. The proxy and every container of an existing
  resource are kept.
- Application images beyond the retained rollback targets (`ImagesToKeep` plus the current one), and images of
  deleted applications.
- Unused images that are not managed and are older than one hour. An image is unused when no container references
  it, running or stopped.
- Dangling images.
- The build cache, except while a deployment is active.
- Unused networks, only when the policy opts in.
- Volumes: never by default. Even with `deleteUnusedVolumes` set, only anonymous volumes are pruned (`volume prune`
  without `--all`). On Podman, volume pruning is refused because it would also remove named volumes.

Nothing is ever forced.

**Images page**

- `GET …/images?page=&pageSize=` lists each image's size, age, the containers using it and whether it is reclaimable,
  with total and reclaimable bytes.
- `POST …/images/delete` with `{ reference, confirmReference }` deletes one unused image. The reference must be typed
  twice.

**Notifications**: `server.docker_cleanup_succeeded` and `server.docker_cleanup_failed`. Failures repeat at most daily
until a success.

## Server alerts (SRV-08)

**Disk usage** (`server.disk_usage_high`)

- The alert is checked after every metrics sample, against the most used probed filesystem.
- It fires at or above the server's threshold, at most once per the server's interval.
- It re-arms early when usage drops at least 5 points below the threshold.

**Reachability** (`server.unreachable`, `server.reachable`)

- The unreachable alert fires after two consecutive failed health polls (`Server.ConsecutiveFailedHealthChecks`).
- The reachable alert follows only an announced outage.
