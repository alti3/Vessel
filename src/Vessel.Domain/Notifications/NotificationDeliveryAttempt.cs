using Vessel.Domain.Common;

namespace Vessel.Domain.Notifications;

public sealed class NotificationDeliveryAttempt : Entity<NotificationDeliveryAttemptId>
{
    private NotificationDeliveryAttempt()
    {
    }

    private NotificationDeliveryAttempt(
        NotificationDeliveryAttemptId id,
        NotificationEventId eventId,
        NotificationTargetId targetId,
        NotificationChannel channel,
        int attemptNumber,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        EventId = eventId;
        TargetId = targetId;
        Channel = channel;
        AttemptNumber = attemptNumber;
        Status = NotificationDeliveryStatus.Pending;
    }

    public NotificationEventId EventId { get; private set; }

    public NotificationTargetId TargetId { get; private set; }

    public NotificationChannel Channel { get; private set; }

    public int AttemptNumber { get; private set; }

    public NotificationDeliveryStatus Status { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? RetryAfter { get; private set; }

    public string? FailureReason { get; private set; }

    public string? ProviderMessageId { get; private set; }

    public static NotificationDeliveryAttempt Create(
        NotificationEventId eventId,
        NotificationTargetId targetId,
        NotificationChannel channel,
        int attemptNumber,
        DateTimeOffset now)
    {
        if (attemptNumber <= 0)
            throw new DomainException("Notification attempt number must be positive.");

        return new NotificationDeliveryAttempt(NotificationDeliveryAttemptId.New(), eventId, targetId, channel,
            attemptNumber, now);
    }

    public void MarkSucceeded(string? providerMessageId, DateTimeOffset now)
    {
        Status = NotificationDeliveryStatus.Succeeded;
        ProviderMessageId = string.IsNullOrWhiteSpace(providerMessageId) ? null : providerMessageId.Trim();
        FailureReason = null;
        RetryAfter = null;
        CompletedAt = now;
        Touch(now);
    }

    public void MarkFailed(string failureReason, DateTimeOffset now)
    {
        Status = NotificationDeliveryStatus.Failed;
        FailureReason = string.IsNullOrWhiteSpace(failureReason) ? "Notification delivery failed." : failureReason.Trim();
        RetryAfter = null;
        CompletedAt = now;
        Touch(now);
    }

    public void ScheduleRetry(string failureReason, DateTimeOffset retryAfter, DateTimeOffset now)
    {
        if (retryAfter <= now)
            throw new DomainException("Notification retry time must be in the future.");

        Status = NotificationDeliveryStatus.RetryScheduled;
        FailureReason = string.IsNullOrWhiteSpace(failureReason) ? "Notification delivery failed." : failureReason.Trim();
        RetryAfter = retryAfter;
        CompletedAt = now;
        Touch(now);
    }
}
