using Vessel.Domain;
using Vessel.Domain.Deployments;
using Vessel.Domain.Webhooks;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.UnitTests.Webhooks;

public sealed class WebhookDomainTests
{
    [Fact]
    public void WebhookEvent_TracksReceiptDedupeAndProcessingState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var webhookEvent = WebhookEvent.Receive(
            WebhookProvider.GitHub,
            "push",
            "delivery-1",
            "GitHub:push:delivery-1",
            """{"ref":"refs/heads/main"}""",
            WebhookSignatureStatus.Missing,
            now);

        webhookEvent.MarkSignatureVerified(now.AddSeconds(1));
        webhookEvent.MarkQueued(now.AddSeconds(2));
        webhookEvent.StartProcessing(now.AddSeconds(3));
        webhookEvent.MarkProcessed(AppId.New(), DeploymentId.New(), null, now.AddSeconds(4));

        Assert.Equal(WebhookSignatureStatus.Verified, webhookEvent.SignatureStatus);
        Assert.Equal(WebhookEventStatus.Processed, webhookEvent.Status);
        Assert.NotNull(webhookEvent.PayloadReference);
        Assert.NotNull(webhookEvent.ProcessedAt);
    }

    [Fact]
    public void ApplicationPreview_RefreshAndArchivePreservePullRequestIdentity()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var preview = ApplicationPreview.Open(
            AppId.New(),
            WebhookProvider.GitLab,
            42,
            "feature",
            "main",
            "abc123",
            "https://gitlab.example/project/-/merge_requests/42",
            "Add feature",
            now);

        preview.Refresh("feature-v2", "main", "def456", preview.PullRequestUrl, "Add feature v2", now.AddMinutes(1));
        preview.Archive(now.AddMinutes(2));

        Assert.Equal(42, preview.PullRequestNumber);
        Assert.Equal("feature-v2", preview.SourceBranch);
        Assert.Equal("def456", preview.CommitSha);
        Assert.Equal(ApplicationPreviewStatus.Archived, preview.Status);
        Assert.NotNull(preview.ClosedAt);
    }

    [Fact]
    public void Deployment_RecordsWebhookAndPreviewMetadata()
    {
        var previewId = ApplicationPreviewId.New();
        var webhookEventId = WebhookEventId.New();

        var deployment = Deployment.Queue(
            AppId.New(),
            ServerId.New(),
            null,
            "abc123",
            previewId,
            webhookEventId,
            DateTimeOffset.UtcNow);

        Assert.True(deployment.IsPreview);
        Assert.True(deployment.IsWebhookTriggered);
        Assert.Equal(previewId, deployment.PreviewId);
        Assert.Equal(webhookEventId, deployment.WebhookEventId);
    }
}
