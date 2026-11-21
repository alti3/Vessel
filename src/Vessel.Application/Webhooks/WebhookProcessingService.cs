using System.Text.Json;
using Vessel.Application.Auditing;
using Vessel.Application.Deployments;
using Vessel.Application.Deployments.Previews;
using Vessel.Application.Deployments.Queue;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.Servers;
using Vessel.Domain.ValueObjects;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Webhooks;

public sealed class WebhookProcessingService(
    IVesselDbContext dbContext,
    IBackgroundJobDispatcher backgroundJobs,
    DeploymentQueueService queue,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public async Task<WebhookProcessingResult> ProcessAsync(
        WebhookEventId webhookEventId,
        CancellationToken cancellationToken = default)
    {
        WebhookEvent webhookEvent =
            await dbContext.WebhookEventRepository.GetByIdAsync(webhookEventId, cancellationToken)
            ?? throw new InvalidOperationException("Webhook event was not found.");
        if (webhookEvent.Status is WebhookEventStatus.Processed or WebhookEventStatus.Ignored
            or WebhookEventStatus.Failed or WebhookEventStatus.Rejected)
            return Result(webhookEvent, webhookEvent.FailureReason ?? "Webhook was already handled.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        webhookEvent.StartProcessing(now);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(webhookEvent.PayloadJson);
            IReadOnlyDictionary<string, string> headers =
                WebhookPayloadReader.EnvelopeHeaders(new Dictionary<string, string>(), document.RootElement);
            ParsedWebhook parsed = WebhookPayloadReader.Parse(webhookEvent.Provider, headers, document.RootElement)
                                   ?? throw new DomainException(
                                       "Webhook payload is unsupported or missing required repository data.");

            if (parsed.Kind == "generic")
                return await ProcessGenericAsync(webhookEvent, parsed, cancellationToken);

            if (parsed.Kind == "push")
                return await ProcessPushAsync(webhookEvent, parsed, cancellationToken);

            if (parsed.Kind == "pull_request")
                return await ProcessPullRequestAsync(webhookEvent, parsed, cancellationToken);

            webhookEvent.Ignore("Webhook event type is not actionable.", now);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Result(webhookEvent, "Webhook ignored.");
        }
        catch (Exception ex)
        {
            webhookEvent.Fail(ex.Message, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return Result(webhookEvent, "Webhook processing failed.");
        }
    }

    private async Task<WebhookProcessingResult> ProcessPushAsync(
        WebhookEvent webhookEvent,
        ParsedWebhook parsed,
        CancellationToken cancellationToken)
    {
        if (GitRef.IsZeroSha(parsed.CommitSha))
            return await IgnoreAsync(webhookEvent, "Branch deletion pushes are not deployed.", cancellationToken);

        IReadOnlyList<AppEntity> candidates = WebhookApplicationMatching.Candidates(dbContext, parsed);
        if (candidates.Count == 0)
            return await IgnoreAsync(webhookEvent, "No application matched the repository and branch.",
                cancellationToken);

        // SEC-01: act only on applications whose own secret verified this delivery.
        AppEntity[] verified = candidates.Where(application => webhookEvent.IsVerifiedFor(application.Id)).ToArray();
        if (verified.Length == 0)
            return await RejectAsync(webhookEvent, "Webhook signature verification failed for matched applications.",
                cancellationToken);
        var outcomes = new WebhookOutcomes();
        // DEP-12: every verified matching application is evaluated on its own; one application's skip, watch-path
        // miss or full queue never stops another application's deployment.
        foreach (AppEntity application in verified)
        {
            if (ShouldSkip(parsed.CommitMessages))
            {
                outcomes.Skip(application.Id, "Commit message requested deployment skip.");
                continue;
            }

            if (!application.DeploymentSettings.AutoDeployEnabled)
            {
                outcomes.Skip(application.Id, "Automatic deployment is disabled for the matched application.");
                continue;
            }

            if (!WatchPathsTriggered(application.DeploymentSettings.WatchPaths, parsed.ChangedFiles))
            {
                outcomes.Skip(application.Id, "Changed files did not match watch paths.");
                continue;
            }

            TeamId teamId = TeamForApplication(application.Id);
            try
            {
                DeploymentAdmission admission = await QueueDeploymentAsync(application, parsed.CommitSha, null,
                    webhookEvent.Id, cancellationToken);
                outcomes.Queued(application.Id, teamId, admission, null);
            }
            catch (DomainException ex)
            {
                outcomes.Fail(application.Id, ex.Message);
            }
        }

        return await CompleteAsync(webhookEvent, outcomes, "Deployment queued.", cancellationToken);
    }

    private async Task<WebhookProcessingResult> ProcessPullRequestAsync(
        WebhookEvent webhookEvent,
        ParsedWebhook parsed,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AppEntity> candidates = WebhookApplicationMatching.Candidates(dbContext, parsed);
        if (candidates.Count == 0)
            return await IgnoreAsync(webhookEvent, "No application matched the repository and target branch.",
                cancellationToken);

        AppEntity[] verified = candidates.Where(application => webhookEvent.IsVerifiedFor(application.Id)).ToArray();
        if (verified.Length == 0)
            return await RejectAsync(webhookEvent, "Webhook signature verification failed for matched applications.",
                cancellationToken);

        var closing = IsClosingAction(parsed.Provider, parsed.Action);
        var outcomes = new WebhookOutcomes();
        foreach (AppEntity application in verified)
        {
            TeamId teamId = TeamForApplication(application.Id);
            ApplicationPreview? preview = dbContext.ApplicationPreviews.SingleOrDefault(item =>
                item.ApplicationId == application.Id &&
                item.Provider == parsed.Provider &&
                item.PullRequestNumber == parsed.PullRequestNumber!.Value);

            if (closing)
            {
                if (preview is null)
                {
                    outcomes.Skip(application.Id, "No preview deployment existed for the closed pull request.");
                    continue;
                }

                // DEP-07: archive first (its route drops on the next proxy apply), then a retry-safe job removes the
                // preview's containers, networks, volumes, images and route.
                preview.Archive(timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(cancellationToken);
                await auditWriter.RecordAsync(teamId, null, AuditActions.PreviewArchived,
                    new AuditTarget("preview", preview.Id.Value.ToString("D")), null,
                    new Dictionary<string, object?>
                    { ["applicationId"] = application.Id.Value, ["pullRequest"] = preview.PullRequestNumber },
                    cancellationToken);
                Guid archivedId = preview.Id.Value;
                try
                {
                    backgroundJobs.Enqueue<CleanupPreviewJob>(job => job.RunAsync(archivedId, CancellationToken.None));
                }
                catch (BackgroundJobsUnavailableException)
                {
                    // The preview stays archived; its runtime cleanup can be re-run through the preview API.
                }

                outcomes.Archived(application.Id, preview.Id);
                continue;
            }

            if (!application.DeploymentSettings.PreviewDeploymentsEnabled)
            {
                outcomes.Skip(application.Id, "Preview deployments are disabled for this application.");
                continue;
            }

            // DEP-02: fork pull requests deploy only when the application explicitly allows public previews.
            if (!parsed.IsPreviewRepositoryTrusted(application.DeploymentSettings.PublicPreviewsEnabled))
            {
                outcomes.Skip(application.Id,
                    "Pull request was not deployed: its source repository is not the application repository and public previews are disabled.");
                continue;
            }

            if (ShouldSkip(parsed.CommitMessages))
            {
                outcomes.Skip(application.Id, "Pull request requested deployment skip.");
                continue;
            }

            if (!GitRef.TryParse(parsed.SourceBranch, out _) ||
                (parsed.CommitSha is not null && parsed.CommitSha != "HEAD" &&
                 !GitRef.IsCommitShaValue(parsed.CommitSha)))
            {
                outcomes.Skip(application.Id, "Pull request branch or commit is not a valid git reference.");
                continue;
            }

            var commitSha = GitRef.IsCommitShaValue(parsed.CommitSha) ? parsed.CommitSha! : "HEAD";
            var sameRepository = parsed.IsSameRepositoryPullRequest;
            if (preview is null)
            {
                preview = ApplicationPreview.Open(application.Id, parsed.Provider, parsed.PullRequestNumber!.Value,
                    parsed.SourceBranch!, parsed.TargetBranch!, commitSha, parsed.PullRequestUrl,
                    parsed.PullRequestTitle, timeProvider.GetUtcNow(), parsed.SourceRepository, sameRepository);
                await dbContext.ApplicationPreviewRepository.AddAsync(preview, cancellationToken);
            }
            else
            {
                preview.Refresh(parsed.SourceBranch!, parsed.TargetBranch!, commitSha, parsed.PullRequestUrl,
                    parsed.PullRequestTitle, timeProvider.GetUtcNow(), parsed.SourceRepository, sameRepository);
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                DeploymentAdmission admission = await QueueDeploymentAsync(application, commitSha, preview.Id,
                    webhookEvent.Id, cancellationToken);
                await auditWriter.RecordAsync(teamId, null, AuditActions.PreviewOpened,
                    new AuditTarget("preview", preview.Id.Value.ToString("D")), null,
                    new Dictionary<string, object?>
                    {
                        ["applicationId"] = application.Id.Value,
                        ["pullRequest"] = preview.PullRequestNumber,
                        ["sameRepository"] = sameRepository
                    },
                    cancellationToken);
                outcomes.Queued(application.Id, teamId, admission, preview.Id);
            }
            catch (DomainException ex)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                outcomes.Fail(application.Id, ex.Message);
            }
        }

        return await CompleteAsync(webhookEvent, outcomes,
            closing ? "Preview archived." : "Preview deployment queued.", cancellationToken);
    }

    private async Task<WebhookProcessingResult> ProcessGenericAsync(
        WebhookEvent webhookEvent,
        ParsedWebhook parsed,
        CancellationToken cancellationToken)
    {
        AppId applicationId = new(parsed.ApplicationId!.Value);
        AppEntity application = dbContext.Applications.SingleOrDefault(application => application.Id == applicationId)
                                ?? throw new InvalidOperationException("Application was not found.");
        if (!webhookEvent.IsVerifiedFor(application.Id))
            return await RejectAsync(webhookEvent, "Generic webhook secret verification failed.", cancellationToken);

        TeamId teamId = TeamForApplication(application.Id);
        var outcomes = new WebhookOutcomes();
        try
        {
            DeploymentAdmission admission = await QueueDeploymentAsync(application, parsed.CommitSha, null,
                webhookEvent.Id, cancellationToken);
            outcomes.Queued(application.Id, teamId, admission, null);
        }
        catch (DomainException ex)
        {
            outcomes.Fail(application.Id, ex.Message);
        }

        return await CompleteAsync(webhookEvent, outcomes, "Deployment queued.", cancellationToken);
    }

    /// <summary>
    ///     Records the outcome of a fan-out: every verified matching application is evaluated independently, so one
    ///     application's skip never prevents another application's deployment.
    /// </summary>
    private async Task<WebhookProcessingResult> CompleteAsync(
        WebhookEvent webhookEvent,
        WebhookOutcomes outcomes,
        string successMessage,
        CancellationToken cancellationToken)
    {
        webhookEvent.RecordApplicationOutcomes(outcomes.All, timeProvider.GetUtcNow());
        if (outcomes.QueuedDeployments.Count == 0 && outcomes.ArchivedPreviews.Count == 0)
            return await IgnoreAsync(webhookEvent, outcomes.SkipReason(), cancellationToken);

        if (outcomes.QueuedDeployments.Count > 0)
        {
            QueuedOutcome first = outcomes.QueuedDeployments[0];
            webhookEvent.MarkProcessed(first.ApplicationId, first.DeploymentId, first.PreviewId,
                timeProvider.GetUtcNow());
        }
        else
        {
            ArchivedOutcome first = outcomes.ArchivedPreviews[0];
            webhookEvent.MarkProcessed(first.ApplicationId, null, first.PreviewId, timeProvider.GetUtcNow());
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        foreach (QueuedOutcome queued in outcomes.QueuedDeployments.Where(item => !item.Deduplicated))
            await AuditProcessedAsync(queued.TeamId, queued.ApplicationId, webhookEvent, queued.DeploymentId,
                queued.PreviewId, cancellationToken);

        var count = outcomes.QueuedDeployments.Count + outcomes.ArchivedPreviews.Count;
        return Result(webhookEvent, count == 1 ? successMessage : $"{successMessage} ({count} applications)");
    }

    /// <summary>
    ///     Queues a webhook deployment through queue admission (DEP-04/DEP-05): a deployment behind a running one is
    ///     queued instead of rejected, an identical active one is reused, and older queued (or, opt-in, still building)
    ///     webhook deployments of the same application, pull request and server are cancelled.
    /// </summary>
    private async Task<DeploymentAdmission> QueueDeploymentAsync(
        AppEntity application,
        string? commitSha,
        ApplicationPreviewId? previewId,
        WebhookEventId webhookEventId,
        CancellationToken cancellationToken)
    {
        Server server = dbContext.Servers.Single(server => server.Id == application.ServerId);
        if (server.Status == ServerStatus.Unreachable)
            throw new DomainException("Deployment server is unreachable.");

        // Refs are validated before queueing (and again when the deployment starts).
        var resolvedCommit = DeploymentPlanBuilder.NormalizeRequestedRef(commitSha) ??
                             DeploymentPlanBuilder.NormalizeRequestedRef(application.GitSource.CommitSha) ?? "HEAD";
        DeploymentAdmission admission = await queue.AdmitAsync(new DeploymentAdmissionRequest(
            application,
            null,
            DeploymentTrigger.Webhook,
            resolvedCommit,
            previewId,
            webhookEventId,
            QueuedMessage: previewId.HasValue
                ? "Preview deployment queued by webhook."
                : "Deployment queued by webhook."), cancellationToken);
        if (admission.Outcome == DeploymentAdmissionOutcome.Deduplicated) return admission;

        TeamId teamId = TeamForApplication(application.Id);
        await auditWriter.RecordAsync(teamId, null, AuditActions.DeploymentStarted,
            new AuditTarget("deployment", admission.Deployment.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["applicationId"] = application.Id.Value,
                ["serverId"] = application.ServerId.Value,
                ["webhookEventId"] = webhookEventId.Value,
                ["previewId"] = previewId?.Value,
                ["commitSha"] = resolvedCommit,
                ["trigger"] = DeploymentTrigger.Webhook.ToString(),
                ["supersededDeployments"] = admission.Superseded.Select(id => id.Value).ToArray()
            }, cancellationToken);
        return admission;
    }

    private TeamId TeamForApplication(AppId applicationId)
    {
        AppEntity application = dbContext.Applications.Single(application => application.Id == applicationId);
        return WebhookApplicationMatching.TeamForApplication(dbContext, application);
    }

    private async Task<WebhookProcessingResult> IgnoreAsync(WebhookEvent webhookEvent, string reason,
        CancellationToken cancellationToken)
    {
        webhookEvent.Ignore(reason, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result(webhookEvent, reason);
    }

    private async Task<WebhookProcessingResult> RejectAsync(WebhookEvent webhookEvent, string reason,
        CancellationToken cancellationToken)
    {
        webhookEvent.Reject(reason, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(null, null, AuditActions.WebhookRejected,
            new AuditTarget("webhook-event", webhookEvent.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["provider"] = webhookEvent.Provider.ToString(), ["reason"] = reason },
            cancellationToken);
        return Result(webhookEvent, reason);
    }

    private async Task AuditProcessedAsync(
        TeamId teamId,
        AppId applicationId,
        WebhookEvent webhookEvent,
        DeploymentId deploymentId,
        ApplicationPreviewId? previewId,
        CancellationToken cancellationToken)
    {
        await auditWriter.RecordAsync(teamId, null, AuditActions.WebhookProcessed,
            new AuditTarget("webhook-event", webhookEvent.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["provider"] = webhookEvent.Provider.ToString(),
                ["applicationId"] = applicationId.Value,
                ["deploymentId"] = deploymentId.Value,
                ["previewId"] = previewId?.Value
            }, cancellationToken);
    }

    private static WebhookProcessingResult Result(WebhookEvent webhookEvent, string message)
    {
        return new WebhookProcessingResult(webhookEvent.Id.Value, webhookEvent.Status,
            webhookEvent.ApplicationId?.Value,
            webhookEvent.DeploymentId?.Value, webhookEvent.PreviewId?.Value, message,
            webhookEvent.ApplicationOutcomes);
    }

    private static bool ShouldSkip(IReadOnlyList<string> messages)
    {
        return messages.Count > 0 && messages.All(message =>
            message.Contains("[skip cd]", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("[skip ci]", StringComparison.OrdinalIgnoreCase));
    }

    private static bool WatchPathsTriggered(string? watchPaths, IReadOnlyList<string> changedFiles)
    {
        if (string.IsNullOrWhiteSpace(watchPaths)) return true;
        if (changedFiles.Count == 0) return false;
        var patterns = watchPaths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return changedFiles.Any(file => patterns.Any(pattern => GlobMatch(pattern.TrimStart('/'), file)));
    }

    private static bool GlobMatch(string pattern, string path)
    {
        if (pattern == "**" || pattern == "*") return true;
        if (pattern.EndsWith("/**", StringComparison.Ordinal))
            return path.StartsWith(pattern[..^3], StringComparison.Ordinal);
        if (pattern.Contains('*', StringComparison.Ordinal))
        {
            var parts = pattern.Split('*');
            var position = 0;
            foreach (var part in parts)
            {
                if (part.Length == 0) continue;
                var found = path.IndexOf(part, position, StringComparison.Ordinal);
                if (found < 0) return false;
                position = found + part.Length;
            }

            return true;
        }

        return string.Equals(pattern, path, StringComparison.Ordinal) ||
               path.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    private static bool IsClosingAction(WebhookProvider provider, string? action)
    {
        return provider switch
        {
            WebhookProvider.GitHub or WebhookProvider.Gitea => string.Equals(action, "closed",
                StringComparison.OrdinalIgnoreCase),
            WebhookProvider.GitLab => action is "closed" or "close" or "merge",
            WebhookProvider.Bitbucket => action is "pullrequest:rejected" or "pullrequest:fulfilled",
            _ => false
        };
    }

    private sealed class WebhookOutcomes
    {
        private readonly List<string> _skipReasons = [];

        public List<QueuedOutcome> QueuedDeployments { get; } = [];

        public List<ArchivedOutcome> ArchivedPreviews { get; } = [];

        public List<WebhookApplicationOutcome> All { get; } = [];

        public void Queued(AppId applicationId, TeamId teamId, DeploymentAdmission admission,
            ApplicationPreviewId? previewId)
        {
            var deduplicated = admission.Outcome == DeploymentAdmissionOutcome.Deduplicated;
            All.Add(new WebhookApplicationOutcome(applicationId.Value,
                deduplicated ? WebhookApplicationOutcomeKind.Deduplicated : WebhookApplicationOutcomeKind.Queued,
                admission.Deployment.Id.Value, previewId?.Value,
                deduplicated ? "Deployment already queued for this commit." : null));
            QueuedDeployments.Add(new QueuedOutcome(applicationId, teamId, admission.Deployment.Id, previewId,
                deduplicated));
        }

        public void Archived(AppId applicationId, ApplicationPreviewId previewId)
        {
            All.Add(new WebhookApplicationOutcome(applicationId.Value, WebhookApplicationOutcomeKind.PreviewClosed,
                null, previewId.Value, null));
            ArchivedPreviews.Add(new ArchivedOutcome(applicationId, previewId));
        }

        public void Skip(AppId applicationId, string reason)
        {
            All.Add(new WebhookApplicationOutcome(applicationId.Value, WebhookApplicationOutcomeKind.Skipped, null,
                null, reason));
            if (!_skipReasons.Contains(reason, StringComparer.Ordinal)) _skipReasons.Add(reason);
        }

        public void Fail(AppId applicationId, string reason)
        {
            All.Add(new WebhookApplicationOutcome(applicationId.Value, WebhookApplicationOutcomeKind.Failed, null,
                null, reason));
            if (!_skipReasons.Contains(reason, StringComparer.Ordinal)) _skipReasons.Add(reason);
        }

        public string SkipReason()
        {
            var reason = _skipReasons.Count == 0
                ? "No matched application was deployed."
                : string.Join(" ", _skipReasons);
            return reason.Length <= 512 ? reason : reason[..509] + "...";
        }
    }

    private sealed record QueuedOutcome(
        AppId ApplicationId,
        TeamId TeamId,
        DeploymentId DeploymentId,
        ApplicationPreviewId? PreviewId,
        bool Deduplicated);

    private sealed record ArchivedOutcome(AppId ApplicationId, ApplicationPreviewId PreviewId);
}
