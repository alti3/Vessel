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

## Coolify Reference

Upstream Coolify default branch was inspected for:

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
