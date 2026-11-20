# Phase 9: Webhooks, Git Providers, and Preview Deployments

Phase 9 adds durable inbound deployment triggers for GitHub, GitLab, Gitea, Bitbucket, and generic webhooks.

## Behavior

- Provider endpoints live under `/webhooks/{provider}` and are protected by the named `webhooks` rate-limit policy.
- Application webhook secrets are configured per application and provider from the application details UI or `PUT /api/v1/applications/{applicationId}/webhooks`.
- Webhook secret values are stored through `ISecretVault`; plaintext secrets are not returned by APIs and are not persisted in webhook receipts.
- Receipt handling persists a `WebhookEvent`, verifies signatures or tokens, applies replay dedupe through a unique dedupe key, and enqueues `ProcessWebhookEventJob`.
- The processing job is thin and delegates to `WebhookProcessingService`.
- Push events match applications by repository and configured branch, respect `AutoDeployEnabled`, honor watch paths, skip commits containing `[skip cd]` or `[skip ci]`, and queue a deployment for the incoming commit.
- Manual redeploy by commit is supported through `StartDeploymentRequest.CommitSha`.
- Pull request and merge request events create or refresh `ApplicationPreview` records and queue preview deployments when `PreviewDeploymentsEnabled` is true.
- Closed, merged, rejected, or fulfilled PR/MR events archive preview records. Container removal remains a later runtime cleanup concern.
- Git branch and tag discovery is exposed by `GET /api/v1/applications/{applicationId}/git/refs` through `IGitClient.ListRefsAsync`.

## Security Notes

- GitHub, Gitea, and Bitbucket use HMAC SHA-256 signatures.
- GitLab uses `X-Gitlab-Token`.
- Generic webhooks use the configured generic provider secret in the JSON `secret` field.
- GitLab tokens and generic secrets are verified at receipt time and redacted before event payload persistence.
- Controllers only read HTTP payloads and call Application services; deployment orchestration remains in Application.

## Wave 1 corrections (DEP-01, DEP-02, SEC-01, NET-01)

### Per-application webhook verification (SEC-01)

- A delivery is verified **per application**: each candidate application's own enabled webhook secret is checked
  (HMAC SHA-256 or constant-time token compare). `WebhookEvent.VerifiedApplicationIds` records exactly which
  applications verified; processing acts only on those. A valid signature for one application never authorizes another,
  even when two teams register the same repository and branch. The old event-level "verified once, trusted for all"
  short-circuit is gone.
- Repository matching is an exact match on a normalized identity (`RepositoryIdentity`: lower-case `owner/repo` path
  without `.git`, plus the host when the payload carries one). `org/app` never matches `org/app-private`.
- Every verified matching application is evaluated independently (auto-deploy, watch paths, active-deployment checks),
  so one application's skip does not block another.
- Branch-deletion pushes (all-zero SHA) are ignored. Events queued before this change have no verified applications and
  are rejected at processing time (safe default).

### Commit-pinned, webhook and pull request checkouts (DEP-01)

- `IGitClient.CheckoutAsync(GitCheckoutRequest)` replaces `git clone --branch <sha>`: `git init`, then
  `git fetch --no-tags --depth 1 origin <ref>`, then `git checkout --detach`. Full SHAs are fetched directly; if the
  server refuses fetch-by-SHA, or the SHA is abbreviated, the branch is fetched and the commit resolved locally.
- Previews fetch the provider ref so fork commits are reachable: GitHub/Gitea `refs/pull/<n>/head`, GitLab
  `refs/merge-requests/<n>/head`, Bitbucket the source branch (Bitbucket fork previews are not supported). The PR head
  commit from the webhook is checked out exactly.
- Git runs non-interactively (`GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`, `ssh -o BatchMode=yes`) with
  bounded timeouts, and every positional URL follows `--`.
- Refs are validated with the Domain `GitRef` value object (Coolify `validateGitRef` allowlist, no leading `-`, no
  `..`, `//`, `.lock`) when queued (UI/API/webhook) and again when the deployment starts.

### Preview isolation and fork gating (DEP-02)

- Pull requests deploy only when the target repository is the webhook repository and the source repository equals the
  target, unless the application sets `DeploymentSettings.PublicPreviewsEnabled` (default **false**). Untrusted PRs are
  recorded as ignored with a reason. `ApplicationPreview.IsSameRepository`/`SourceRepository` record the decision.
- Docker Compose previews only deploy same-repository PRs; a fork's compose file is never trusted.
- Each preview gets its own compose project (`vessel-{app}-pr-{n}`), service name and network alias
  (`ProxyNaming.PreviewUpstreamAlias`), image tag (`pr-{n}-{deployment}`), namespaced `container_name`/named volumes, and
  an ephemeral loopback host port (`127.0.0.1::{port}`) so it never collides with production. Production and preview
  deployments of one application lock independently.
- Production never receives preview variables (`IsPreview`). A preview receives its preview copies; it falls back to
  production runtime values for keys without a preview copy only when public previews are off and at least one preview
  copy exists (Coolify `previewGetsProductionRuntimeFallback`). Public previews never receive production values. Keys
  that are not passed are logged by name only.
- After a healthy preview deployment, the preview gets its own routing record: `{pr}.{canonical domain}`
  (Coolify's default preview template), stored in `ApplicationPreview.PreviewUrl` and routed by the proxy to the preview
  alias.

### Docker Compose applications (NET-01 remainder)

- Repository compose files are parsed with YamlDotNet (BOM-safe; aliases and `<<` merge keys are expanded under a node
  budget so anchors cannot smuggle mounts past validation). The primary service (the one exposing the application port,
  else the first built/published service) joins the server network with alias `ProxyNaming.UpstreamAlias(applicationId)`
  while keeping its other networks.
- Build contexts, Dockerfiles, bind-mount sources, `env_file`, `extends.file` and top-level `secrets`/`configs` files are
  resolved relative to the compose file and must stay inside the repository. Absolute host paths (including
  `/var/run/docker.sock`), `~` and variable-interpolated paths are rejected; `include:` and `network_mode` on the primary
  service are rejected. The previous raw `..` substring check (which rejected values such as `Loading...`) is gone.
- Remaining compose policy (`privileged`, host namespaces, `cap_add`, `devices`, an elevated permission for host
  mounts) is tracked by SEC-11.

## Coolify Reference

Upstream Coolify default branch was inspected for:

- `app/Http/Controllers/Webhook/Concerns/ValidatesPreviewDeploymentRepository.php`
- `app/Jobs/ApplicationDeploymentJob.php` (`previewUsesProductionValues`, `previewGetsProductionRuntimeFallback`)
- `bootstrap/helpers/shared.php` (`validateGitRef`)

- `routes/webhooks.php`
- `app/Http/Controllers/Webhook/Github.php`
- `app/Http/Controllers/Webhook/Gitlab.php`
- `app/Http/Controllers/Webhook/Bitbucket.php`
- `app/Http/Controllers/Webhook/Gitea.php`
- `bootstrap/helpers/applications.php`
- `app/Models/ApplicationPreview.php`
- migrations for manual webhook secrets and application previews

The Vessel implementation preserves the important product semantics: provider-specific signature/token checks, repository and branch matching, skip markers, duplicate deployment avoidance, queued background processing, and PR/MR preview lifecycle metadata. It does not copy Laravel structure or direct remote-process cleanup behavior.

## Verification

- `dotnet restore Vessel.slnx --artifacts-path artifacts\phase9-build`
- `dotnet build Vessel.slnx --no-restore --artifacts-path artifacts\phase9-build`
- `dotnet test Vessel.slnx --no-restore --artifacts-path artifacts\phase9-build --verbosity minimal`
- `tools/validate-project-references.ps1`
