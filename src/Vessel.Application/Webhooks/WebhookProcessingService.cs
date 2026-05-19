using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Auditing;
using Vessel.Application.Deployments;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.Servers;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.Application.Webhooks;

public sealed class WebhookProcessingService(
    IVesselDbContext dbContext,
    ISecretVault secretVault,
    IBackgroundJobDispatcher backgroundJobs,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public async Task<WebhookProcessingResult> ProcessAsync(
        WebhookEventId webhookEventId,
        CancellationToken cancellationToken = default)
    {
        WebhookEvent webhookEvent = await dbContext.WebhookEventRepository.GetByIdAsync(webhookEventId, cancellationToken)
            ?? throw new InvalidOperationException("Webhook event was not found.");
        if (webhookEvent.Status is WebhookEventStatus.Processed or WebhookEventStatus.Ignored or WebhookEventStatus.Failed or WebhookEventStatus.Rejected)
            return Result(webhookEvent, webhookEvent.FailureReason ?? "Webhook was already handled.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        webhookEvent.StartProcessing(now);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            using JsonDocument document = JsonDocument.Parse(webhookEvent.PayloadJson);
            IReadOnlyDictionary<string, string> headers = WebhookPayloadReader.EnvelopeHeaders(new Dictionary<string, string>(), document.RootElement);
            ParsedWebhook parsed = WebhookPayloadReader.Parse(webhookEvent.Provider, headers, document.RootElement)
                                   ?? throw new DomainException("Webhook payload is unsupported or missing required repository data.");

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
        var applications = MatchingApplications(parsed.Repository!, parsed.Branch!, forPreview: false).ToArray();
        if (applications.Length == 0)
            return await IgnoreAsync(webhookEvent, "No application matched the repository and branch.", cancellationToken);

        foreach (AppEntity application in applications)
        {
            TeamId teamId = TeamForApplication(application.Id);
            if (!await VerifyApplicationSecretAsync(webhookEvent, parsed.Provider, application.Id, teamId, cancellationToken))
                continue;
            if (!application.DeploymentSettings.AutoDeployEnabled)
                continue;
            if (ShouldSkip(parsed.CommitMessages))
                return await IgnoreAsync(webhookEvent, "Commit message requested deployment skip.", cancellationToken);
            if (!WatchPathsTriggered(application.DeploymentSettings.WatchPaths, parsed.ChangedFiles))
                return await IgnoreAsync(webhookEvent, "Changed files did not match watch paths.", cancellationToken);

            Deployment deployment = await QueueDeploymentAsync(application, teamId, parsed.CommitSha, null, webhookEvent.Id, cancellationToken);
            webhookEvent.MarkProcessed(application.Id, deployment.Id, null, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            await AuditProcessedAsync(teamId, application.Id, webhookEvent, deployment.Id, null, cancellationToken);
            return Result(webhookEvent, "Deployment queued.");
        }

        return await RejectAsync(webhookEvent, "Webhook signature verification failed for matched applications.", cancellationToken);
    }

    private async Task<WebhookProcessingResult> ProcessPullRequestAsync(
        WebhookEvent webhookEvent,
        ParsedWebhook parsed,
        CancellationToken cancellationToken)
    {
        var applications = MatchingApplications(parsed.Repository!, parsed.TargetBranch!, forPreview: true).ToArray();
        if (applications.Length == 0)
            return await IgnoreAsync(webhookEvent, "No application matched the repository and target branch.", cancellationToken);

        foreach (AppEntity application in applications)
        {
            TeamId teamId = TeamForApplication(application.Id);
            if (!await VerifyApplicationSecretAsync(webhookEvent, parsed.Provider, application.Id, teamId, cancellationToken))
                continue;

            bool closing = IsClosingAction(parsed.Provider, parsed.Action);
            ApplicationPreview? preview = dbContext.ApplicationPreviews.SingleOrDefault(item =>
                item.ApplicationId == application.Id &&
                item.Provider == parsed.Provider &&
                item.PullRequestNumber == parsed.PullRequestNumber!.Value);

            if (closing)
            {
                if (preview is null)
                    return await IgnoreAsync(webhookEvent, "No preview deployment existed for the closed pull request.", cancellationToken);

                preview.Archive(timeProvider.GetUtcNow());
                webhookEvent.MarkProcessed(application.Id, null, preview.Id, timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(cancellationToken);
                await auditWriter.RecordAsync(teamId, null, AuditActions.PreviewArchived,
                    new AuditTarget("preview", preview.Id.Value.ToString("D")), null,
                    new Dictionary<string, object?> { ["applicationId"] = application.Id.Value, ["pullRequest"] = preview.PullRequestNumber },
                    cancellationToken);
                return Result(webhookEvent, "Preview archived.");
            }

            if (!application.DeploymentSettings.PreviewDeploymentsEnabled)
                return await IgnoreAsync(webhookEvent, "Preview deployments are disabled for this application.", cancellationToken);
            if (ShouldSkip(parsed.CommitMessages))
                return await IgnoreAsync(webhookEvent, "Pull request requested deployment skip.", cancellationToken);

            if (preview is null)
            {
                preview = ApplicationPreview.Open(application.Id, parsed.Provider, parsed.PullRequestNumber!.Value,
                    parsed.SourceBranch!, parsed.TargetBranch!, parsed.CommitSha ?? "HEAD", parsed.PullRequestUrl,
                    parsed.PullRequestTitle, timeProvider.GetUtcNow());
                await dbContext.ApplicationPreviewRepository.AddAsync(preview, cancellationToken);
            }
            else
            {
                preview.Refresh(parsed.SourceBranch!, parsed.TargetBranch!, parsed.CommitSha ?? "HEAD",
                    parsed.PullRequestUrl, parsed.PullRequestTitle, timeProvider.GetUtcNow());
            }

            Deployment deployment = await QueueDeploymentAsync(application, teamId, parsed.CommitSha, preview.Id, webhookEvent.Id, cancellationToken);
            webhookEvent.MarkProcessed(application.Id, deployment.Id, preview.Id, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditWriter.RecordAsync(teamId, null, AuditActions.PreviewOpened,
                new AuditTarget("preview", preview.Id.Value.ToString("D")), null,
                new Dictionary<string, object?> { ["applicationId"] = application.Id.Value, ["pullRequest"] = preview.PullRequestNumber },
                cancellationToken);
            await AuditProcessedAsync(teamId, application.Id, webhookEvent, deployment.Id, preview.Id, cancellationToken);
            return Result(webhookEvent, "Preview deployment queued.");
        }

        return await RejectAsync(webhookEvent, "Webhook signature verification failed for matched applications.", cancellationToken);
    }

    private async Task<WebhookProcessingResult> ProcessGenericAsync(
        WebhookEvent webhookEvent,
        ParsedWebhook parsed,
        CancellationToken cancellationToken)
    {
        AppId applicationId = new(parsed.ApplicationId!.Value);
        AppEntity application = dbContext.Applications.SingleOrDefault(application => application.Id == applicationId)
            ?? throw new InvalidOperationException("Application was not found.");
        TeamId teamId = TeamForApplication(application.Id);
        if (!await VerifyApplicationSecretAsync(webhookEvent, WebhookProvider.Generic, application.Id, teamId, cancellationToken))
            return await RejectAsync(webhookEvent, "Generic webhook secret verification failed.", cancellationToken);

        Deployment deployment = await QueueDeploymentAsync(application, teamId, parsed.CommitSha, null, webhookEvent.Id, cancellationToken);
        webhookEvent.MarkProcessed(application.Id, deployment.Id, null, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditProcessedAsync(teamId, application.Id, webhookEvent, deployment.Id, null, cancellationToken);
        return Result(webhookEvent, "Deployment queued.");
    }

    private async Task<Deployment> QueueDeploymentAsync(
        AppEntity application,
        TeamId teamId,
        string? commitSha,
        ApplicationPreviewId? previewId,
        WebhookEventId webhookEventId,
        CancellationToken cancellationToken)
    {
        Server server = dbContext.Servers.Single(server => server.Id == application.ServerId);
        if (server.Status == ServerStatus.Unreachable)
            throw new DomainException("Deployment server is unreachable.");

        bool alreadyActive = dbContext.Deployments.Any(deployment =>
            deployment.ApplicationId == application.Id &&
            deployment.PreviewId == previewId &&
            (deployment.Status == DeploymentStatus.Queued ||
             deployment.Status == DeploymentStatus.InProgress ||
             deployment.Status == DeploymentStatus.CancelRequested));
        if (alreadyActive)
            throw new DomainException("A deployment is already queued or running for this application.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        string resolvedCommit = string.IsNullOrWhiteSpace(commitSha) ? application.GitSource.CommitSha ?? "HEAD" : commitSha.Trim();
        Deployment deployment = Deployment.Queue(application.Id, application.ServerId, null, resolvedCommit, previewId, webhookEventId, now);
        deployment.RecordSource(application.GitSource.RepositoryUrl.Value, application.GitSource.Branch, resolvedCommit, null, now);
        deployment.AddLogLine("system", previewId.HasValue ? "Preview deployment queued by webhook." : "Deployment queued by webhook.", now);

        await dbContext.DeploymentRepository.AddAsync(deployment, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        backgroundJobs.Enqueue<RunDeploymentJob>(job => job.RunAsync(deployment.Id.Value, CancellationToken.None));
        await auditWriter.RecordAsync(teamId, null, AuditActions.DeploymentStarted,
            new AuditTarget("deployment", deployment.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["applicationId"] = application.Id.Value,
                ["serverId"] = application.ServerId.Value,
                ["webhookEventId"] = webhookEventId.Value,
                ["previewId"] = previewId?.Value,
                ["commitSha"] = resolvedCommit
            }, cancellationToken);
        return deployment;
    }

    private IEnumerable<AppEntity> MatchingApplications(string repository, string branch, bool forPreview)
    {
        return dbContext.Applications.AsEnumerable()
            .Where(application => application.GitSource.Branch == branch)
            .Where(application => RepositoryMatches(application.GitSource.RepositoryUrl.Value, repository))
            .Where(application => forPreview || application.DeploymentSettings.AutoDeployEnabled);
    }

    private async Task<bool> VerifyApplicationSecretAsync(
        WebhookEvent webhookEvent,
        WebhookProvider provider,
        AppId applicationId,
        TeamId teamId,
        CancellationToken cancellationToken)
    {
        if (webhookEvent.SignatureStatus == WebhookSignatureStatus.Verified)
            return true;

        ApplicationWebhookConfiguration? configuration = dbContext.ApplicationWebhookConfigurations.SingleOrDefault(configuration =>
            configuration.ApplicationId == applicationId &&
            configuration.Provider == provider &&
            configuration.IsEnabled);
        if (configuration is null) return false;

        string secret = await secretVault.RevealForDeploymentAsync(teamId, configuration.SecretReferenceId, cancellationToken);
        using JsonDocument payload = JsonDocument.Parse(webhookEvent.PayloadJson);
        IReadOnlyDictionary<string, string> headers = WebhookPayloadReader.EnvelopeHeaders(new Dictionary<string, string>(), payload.RootElement);
        string? supplied = provider switch
        {
            WebhookProvider.GitHub => WebhookPayloadReader.Header(headers, "X-Hub-Signature-256"),
            WebhookProvider.Gitea => WebhookPayloadReader.Header(headers, "X-Hub-Signature-256"),
            WebhookProvider.GitLab => WebhookPayloadReader.Header(headers, "X-Gitlab-Token"),
            WebhookProvider.Bitbucket => WebhookPayloadReader.Header(headers, "X-Hub-Signature"),
            WebhookProvider.Generic => WebhookPayloadReader.Text(WebhookPayloadReader.PayloadRoot(payload.RootElement), "secret"),
            _ => null
        };
        string rawBody = WebhookPayloadReader.RawBody(payload.RootElement);

        bool verified = provider switch
        {
            WebhookProvider.GitHub or WebhookProvider.Gitea => VerifySha256Signature(supplied, rawBody, secret),
            WebhookProvider.Bitbucket => VerifySha256Signature(supplied, rawBody, secret),
            WebhookProvider.GitLab or WebhookProvider.Generic => FixedEquals(secret, supplied),
            _ => false
        };
        if (verified)
            webhookEvent.MarkSignatureVerified(timeProvider.GetUtcNow());
        return verified;
    }

    private TeamId TeamForApplication(AppId applicationId)
    {
        AppEntity application = dbContext.Applications.Single(application => application.Id == applicationId);
        EnvironmentEntity environment = dbContext.Environments.Single(environment => environment.Id == application.EnvironmentId);
        return dbContext.Projects.Single(project => project.Id == environment.ProjectId).TeamId;
    }

    private async Task<WebhookProcessingResult> IgnoreAsync(WebhookEvent webhookEvent, string reason, CancellationToken cancellationToken)
    {
        webhookEvent.Ignore(reason, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result(webhookEvent, reason);
    }

    private async Task<WebhookProcessingResult> RejectAsync(WebhookEvent webhookEvent, string reason, CancellationToken cancellationToken)
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
        return new WebhookProcessingResult(webhookEvent.Id.Value, webhookEvent.Status, webhookEvent.ApplicationId?.Value,
            webhookEvent.DeploymentId?.Value, webhookEvent.PreviewId?.Value, message);
    }

    private static bool RepositoryMatches(string repositoryUrl, string repositoryFullName)
    {
        return repositoryUrl.Contains(repositoryFullName, StringComparison.OrdinalIgnoreCase);
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
        string[] patterns = watchPaths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return changedFiles.Any(file => patterns.Any(pattern => GlobMatch(pattern.TrimStart('/'), file)));
    }

    private static bool GlobMatch(string pattern, string path)
    {
        if (pattern == "**" || pattern == "*") return true;
        if (pattern.EndsWith("/**", StringComparison.Ordinal))
            return path.StartsWith(pattern[..^3], StringComparison.Ordinal);
        if (pattern.Contains('*', StringComparison.Ordinal))
        {
            string[] parts = pattern.Split('*');
            int position = 0;
            foreach (string part in parts)
            {
                if (part.Length == 0) continue;
                int found = path.IndexOf(part, position, StringComparison.Ordinal);
                if (found < 0) return false;
                position = found + part.Length;
            }

            return true;
        }

        return string.Equals(pattern, path, StringComparison.Ordinal) || path.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    private static bool IsClosingAction(WebhookProvider provider, string? action)
    {
        return provider switch
        {
            WebhookProvider.GitHub or WebhookProvider.Gitea => string.Equals(action, "closed", StringComparison.OrdinalIgnoreCase),
            WebhookProvider.GitLab => action is "closed" or "close" or "merge",
            WebhookProvider.Bitbucket => action is "pullrequest:rejected" or "pullrequest:fulfilled",
            _ => false
        };
    }

    private static bool VerifySha256Signature(string? header, string payloadJson, string secret)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        string signature = header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase) ? header["sha256=".Length..] : header;
        byte[] hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payloadJson));
        return FixedEquals(Convert.ToHexString(hash).ToLowerInvariant(), signature);
    }

    private static bool FixedEquals(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
