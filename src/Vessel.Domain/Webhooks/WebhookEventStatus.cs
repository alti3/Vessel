namespace Vessel.Domain.Webhooks;

public enum WebhookEventStatus
{
    Received = 0,
    Duplicate = 1,
    Rejected = 2,
    Queued = 3,
    Processing = 4,
    Processed = 5,
    Ignored = 6,
    Failed = 7
}
