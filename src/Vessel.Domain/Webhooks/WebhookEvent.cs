using Vessel.Domain.Common;

namespace Vessel.Domain.Webhooks;

public sealed class WebhookEvent : Entity<WebhookEventId>
{
    private WebhookEvent()
    {
    }

    private WebhookEvent(
        WebhookEventId id,
        WebhookProvider provider,
        string eventType,
        string? providerEventId,
        string dedupeKey,
        string payloadReference,
        string payloadJson,
        WebhookSignatureStatus signatureStatus,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        Provider = provider;
        EventType = eventType;
        ProviderEventId = providerEventId;
        DedupeKey = dedupeKey;
        PayloadReference = payloadReference;
        PayloadJson = payloadJson;
        SignatureStatus = signatureStatus;
        Status = WebhookEventStatus.Received;
    }

    public WebhookProvider Provider { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public string? ProviderEventId { get; private set; }

    public string DedupeKey { get; private set; } = string.Empty;

    public string PayloadReference { get; private set; } = string.Empty;

    public string PayloadJson { get; private set; } = string.Empty;

    public WebhookSignatureStatus SignatureStatus { get; private set; }

    public WebhookEventStatus Status { get; private set; }

    public string? FailureReason { get; private set; }

    public ApplicationId? ApplicationId { get; private set; }

    public DeploymentId? DeploymentId { get; private set; }

    public ApplicationPreviewId? PreviewId { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public static WebhookEvent Receive(
        WebhookProvider provider,
        string eventType,
        string? providerEventId,
        string dedupeKey,
        string payloadJson,
        WebhookSignatureStatus signatureStatus,
        DateTimeOffset now)
    {
        var normalizedPayload = DomainValidation.Required(payloadJson, nameof(payloadJson), 262_144);
        var id = WebhookEventId.New();
        return new WebhookEvent(
            id,
            provider,
            DomainValidation.Required(eventType, nameof(eventType), 120),
            DomainValidation.Optional(providerEventId, nameof(providerEventId), 255),
            DomainValidation.Required(dedupeKey, nameof(dedupeKey), 512),
            $"db://webhook-events/{id.Value:D}/payload",
            normalizedPayload,
            signatureStatus,
            now);
    }

    public void MarkDuplicate(DateTimeOffset now)
    {
        Status = WebhookEventStatus.Duplicate;
        ProcessedAt = now;
        Touch(now);
    }

    public void Reject(string reason, DateTimeOffset now)
    {
        Status = WebhookEventStatus.Rejected;
        SignatureStatus = WebhookSignatureStatus.Failed;
        FailureReason = DomainValidation.Required(reason, nameof(reason), 512);
        ProcessedAt = now;
        Touch(now);
    }

    public void MarkQueued(DateTimeOffset now)
    {
        Status = WebhookEventStatus.Queued;
        Touch(now);
    }

    public void MarkSignatureVerified(DateTimeOffset now)
    {
        SignatureStatus = WebhookSignatureStatus.Verified;
        Touch(now);
    }

    public void StartProcessing(DateTimeOffset now)
    {
        Status = WebhookEventStatus.Processing;
        Touch(now);
    }

    public void MarkProcessed(ApplicationId? applicationId, DeploymentId? deploymentId, ApplicationPreviewId? previewId,
        DateTimeOffset now)
    {
        ApplicationId = applicationId;
        DeploymentId = deploymentId;
        PreviewId = previewId;
        Status = WebhookEventStatus.Processed;
        ProcessedAt = now;
        Touch(now);
    }

    public void Ignore(string reason, DateTimeOffset now)
    {
        FailureReason = DomainValidation.Required(reason, nameof(reason), 512);
        Status = WebhookEventStatus.Ignored;
        ProcessedAt = now;
        Touch(now);
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        FailureReason = DomainValidation.Required(reason, nameof(reason), 512);
        Status = WebhookEventStatus.Failed;
        ProcessedAt = now;
        Touch(now);
    }
}
