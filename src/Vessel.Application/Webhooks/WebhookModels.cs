using Vessel.Domain.Webhooks;

namespace Vessel.Application.Webhooks;

public sealed record WebhookReceiptRequest(
    WebhookProvider Provider,
    IReadOnlyDictionary<string, string> Headers,
    string PayloadJson);

public sealed record WebhookReceiptResult(
    Guid EventId,
    WebhookProvider Provider,
    string EventType,
    WebhookEventStatus Status,
    WebhookSignatureStatus SignatureStatus,
    string Message);

public sealed record WebhookProcessingResult(
    Guid EventId,
    WebhookEventStatus Status,
    Guid? ApplicationId,
    Guid? DeploymentId,
    Guid? PreviewId,
    string Message);

public sealed record ApplicationWebhookConfigurationSummary(
    Guid Id,
    Guid ApplicationId,
    WebhookProvider Provider,
    bool IsEnabled,
    Guid SecretReferenceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastRotatedAt);

public sealed record ConfigureApplicationWebhookRequest(WebhookProvider Provider, string Secret, bool Enabled = true);

public sealed record GitRepositoryRefSummary(string Name, string Sha, bool IsTag);

public sealed record ApplicationPreviewSummary(
    Guid Id,
    Guid ApplicationId,
    WebhookProvider Provider,
    int PullRequestNumber,
    string SourceBranch,
    string TargetBranch,
    string CommitSha,
    string? PullRequestUrl,
    string? Title,
    string? PreviewUrl,
    string Status);
