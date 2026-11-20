# Deployment queue, auto-cancel, rollback, preview lifecycle and log redaction

Wave 2 items DEP-03, DEP-04, DEP-05, DEP-06, DEP-07, DEP-12 (remainder), SEC-06 (settings setter) and SEC-09.

## Queue admission (DEP-04)

All deployments (UI, REST API, webhook, rollback, preview redeploy) are admitted by
`Application/Deployments/Queue/DeploymentQueueService`. Every decision runs under one lock per server
(`deployments:queue:{serverId}`), so concurrent requests cannot both start, exceed limits, or race a cancel.

- A request while another deployment runs is **queued**, not rejected.
- An active (queued or running) deployment with the same application, preview and commit is returned instead of a
  duplicate (`Deduplicated`, message "Deployment already queued for this commit."), unless the request forces a rebuild
  or is a rollback. Branch-head requests (`HEAD`/no commit) dedupe against each other.
- A server queue holds at most `Server.DeploymentQueueLimit` waiting deployments (default 25); more are rejected.
- A deployment starts only when its application **and preview** have no active deployment (production and each preview
  run independently, like Coolify's per application + pull request rule) and the server runs fewer than
  `Server.ConcurrentBuilds` deployments (default 2, Coolify `concurrent_builds`). Limits are set with
  `GET/PUT /api/v1/servers/{serverId}/deployment-limits` (`servers.write`).
- Each deployment records its `Trigger` (`Manual`, `Api`, `Webhook`, `Restart`, `Rollback`, `Preview`) and optional
  `ParentDeploymentId`. The trigger is decided by the endpoint, never by the request body.
- An admitted deployment is marked `DispatchedAt` and handed to Hangfire. Every terminal transition advances the queue.
  A deployment that cannot be dispatched is failed (Queued → Failed is now a valid transition).
- **Run-lock contention no longer fails a deployment.** Previously a lock miss tried Queued → Failed (forbidden by the
  domain), threw, and leaked the active-deployment gauge. Now the deployment keeps its state and the job is scheduled
  again (`RunDeploymentJob.RunAttemptAsync`, every 15 s for up to an hour).
- A worker that obtains the run lock for a deployment that is already running fails it as orphaned (its previous
  worker stopped) instead of running it twice.

### Stuck-deployment recovery

- Recurring Hangfire job `deployments.queue.recover` (every minute): fails running deployments without any activity
  (log output or state change) for 70 minutes (Coolify: deployment timeout plus grace), re-dispatches dispatched
  deployments whose job never started within 10 minutes, and advances every server with waiting deployments.
- At startup a one-off `DeploymentQueueJob.RecoverAfterStartupAsync` fails deployments that were running when the
  previous control-plane process stopped and restarts the queues.

## Auto-cancel superseded deployments (DEP-05, Coolify v4.7.0)

Scope: application + pull request (preview) + server.

| New deployment | Cancels |
| --- | --- |
| Webhook, `AutoCancelQueuedDeployments` (default **on**) | older queued webhook deployments |
| Webhook, `AutoCancelBuildingDeployments` (opt-in) | also older webhook deployments still building |
| API, `cancel_previous` = `none` (default) / `queued` / `building` | older webhook and API deployments as asked |
| Manual UI, restart, rollback, preview redeploy | nothing |

Manual, restart, rollback and preview-redeploy deployments are never cancelled. A deployment is never auto-cancelled
once its deploy phase started (`DeployPhaseStartedAt`, set under the queue lock right before `compose up`), so a cancel
never leaves a half-replaced stack. A cancelled build is signalled immediately through the in-process
`IDeploymentCancellationRegistry`; its log names the replacing deployment. User cancels follow the same rule: a request
that arrives after the deploy phase started lets the deployment finish (CancelRequested → Succeeded).

`POST /api/v1/deployments` accepts `cancelPrevious` (`none|queued|building`) and `parentDeploymentId`.

## Rollback (DEP-06)

- Production images are tagged by commit: `vessel/{applicationId:N}:{commitSha}`.
- `GET /api/v1/applications/{id}/rollback-targets` (read) lists the images of the last 10 successful production,
  non-rollback deployments plus other local images of the application's own repository. Listing local images probes
  the runtime, so on host-local servers it needs an instance administrator.
- `POST /api/v1/applications/{id}/rollback` (`deployments.start`) with `deploymentId`, `image` or `commitSha` accepts
  only a listed image, a listed deployment or the commit of a successful production deployment. Any other commit is
  refused, because a rollback rebuilds from source when its image is gone.
- A Dockerfile rollback whose image is still local skips clone and build (`compose up --no-build`).
- `ImagesToKeep` (default 2, Coolify `docker_images_to_keep`) keeps the current image plus that many previous successful
  production images; older images of the application are removed after a successful deployment (never forced).
- Registry images are not listed: Vessel builds and keeps images locally and does not push to a registry yet.

## Preview lifecycle (DEP-07) and preview settings (SEC-06)

- PR close/merge (or `DELETE /api/v1/applications/{id}/previews/{pr}`, `applications.write`) archives the preview and
  runs the retry-safe `CleanupPreviewJob`: `compose down --volumes --remove-orphans` for the preview project only,
  removal of images tagged `pr-{n}-…` (exact prefix: PR 1 never matches PR 12), clearing of the preview URL and a proxy
  re-apply that drops its route. Cleanup is skipped when the pull request was reopened.
- `POST …/previews/{pr}/stop` (`deployments.start`) removes the preview's containers but keeps its volumes; status
  `Stopped`. `POST …/previews/{pr}/deploy` (`deployments.start`) redeploys and reopens a stopped preview.
- `GET …/previews` and `GET …/previews/{pr}` (read).
- `GET/PATCH /api/v1/applications/{id}/deployment-settings` exposes `previewDeploymentsEnabled`,
  `publicPreviewsEnabled` (fork pull requests; needs `applications.write`, audited with before/after values),
  `autoDeployEnabled`, `watchPaths`, `autoCancelQueuedDeployments`, `autoCancelBuildingDeployments`,
  `redactAllEnvValuesInLogs` and `imagesToKeep`.
- Stop and close on host-local servers require an instance administrator.

## Webhook fan-out (DEP-12 remainder)

One delivery evaluates every verified matching application independently; skip markers, auto-deploy, watch paths and
queue failures apply per application. Per-application results are stored on `WebhookEvent.ApplicationOutcomes`
(`Queued`, `Deduplicated`, `Skipped`, `PreviewClosed`, `Failed` with reason) and returned by processing. Watch paths use
the push's net file change (a file added and removed within the same push is not a change). GitLab system hooks are not
implemented.

## Log redaction (DEP-03, SEC-09)

- Every deployment log line, build output and compose output is redacted with the deployment's own context: all secret
  values always; every other resolved environment value when `RedactAllEnvValuesInLogs` is on (opt-in; Vessel's
  default keeps plain values visible, Coolify defaults to hiding all values). `VESSEL_*` identifiers stay visible.
- The context also reaches the process layer (`ComposeCommand.Redaction`, `DockerBuildCommand.Redaction`).
- Multiline secrets are matched line by line, and a PEM private key streamed over several lines is hidden from its
  BEGIN line through its END line.
- `SecretRedactor` follows Coolify's `SensitiveDataRedactor`: known values (≥ 8 characters) and locked values (any
  length, short ones only as whole tokens) with URL-, JSON- and base64-encoded variants, Authorization headers,
  `x-access-token:`/`oauth2:` URLs, URL credentials scoped to one line (the user part is kept), GitHub (`gh*_`, dotted,
  `github_pat_`), GitLab (`glpat-`, `glcbt-`, `glrt-`) and AWS keys, PEM blocks (also unterminated), and key-based
  assignments in env, shell, JSON and YAML form with Coolify's prose exception. Log output additionally strips ANSI and
  control characters and hides email addresses; interactive terminal output keeps them.

## Coolify reference

`bootstrap/helpers/applications.php` (`queue_application_deployment`, `cancel_superseded_deployments`,
`next_queuable`, `start_queued_deployment`, `fail_unattended_deployment`, `fail_deployments_of_stopped_workers`),
`app/Enums/CancelPreviousDeployments.php`, `app/Jobs/CancelSupersededDeploymentJob.php`,
`app/Actions/Application/StopApplicationPreview.php`, `app/Services/Security/SensitiveDataRedactor.php`,
`tests/Unit/SensitiveDataRedactorTest.php`, `app/Traits/HasLogRedactionSetting.php`, `routes/api.php` (previews).
