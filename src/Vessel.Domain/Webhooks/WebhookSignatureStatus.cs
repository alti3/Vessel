namespace Vessel.Domain.Webhooks;

public enum WebhookSignatureStatus
{
    NotApplicable = 0,
    Missing = 1,
    Verified = 2,
    Failed = 3
}
