using Vessel.Domain.Common;

namespace Vessel.Domain.Notifications;

public sealed class NotificationEvent : Entity<NotificationEventId>
{
    private NotificationEvent()
    {
    }

    private NotificationEvent(
        NotificationEventId id,
        TeamId teamId,
        UserId? userId,
        string eventType,
        NotificationSeverity severity,
        NotificationTargetType targetType,
        string? targetId,
        string title,
        string message,
        string payloadJson,
        string? resourceUrl,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        UserId = userId;
        EventType = eventType;
        Severity = severity;
        TargetType = targetType;
        TargetId = targetId;
        Title = title;
        Message = message;
        PayloadJson = payloadJson;
        ResourceUrl = resourceUrl;
        Status = NotificationEventStatus.Pending;
    }

    public TeamId TeamId { get; private set; }

    public UserId? UserId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public NotificationSeverity Severity { get; private set; }

    public NotificationTargetType TargetType { get; private set; }

    public string? TargetId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    public string PayloadJson { get; private set; } = "{}";

    public string? ResourceUrl { get; private set; }

    public NotificationEventStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? LastAttemptedAt { get; private set; }

    public string? FailureReason { get; private set; }

    public static NotificationEvent Create(
        TeamId teamId,
        UserId? userId,
        string eventType,
        NotificationSeverity severity,
        NotificationTargetType targetType,
        string? targetId,
        string title,
        string message,
        string payloadJson,
        string? resourceUrl,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            throw new DomainException("Notification event type is required.");
        if (string.IsNullOrWhiteSpace(title))
            throw new DomainException("Notification title is required.");
        if (string.IsNullOrWhiteSpace(message))
            throw new DomainException("Notification message is required.");

        return new NotificationEvent(NotificationEventId.New(), teamId, userId, eventType.Trim(), severity,
            targetType, string.IsNullOrWhiteSpace(targetId) ? null : targetId.Trim(), title.Trim(), message.Trim(),
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson, resourceUrl, now);
    }

    public void StartDispatch(DateTimeOffset now)
    {
        Status = NotificationEventStatus.Dispatching;
        AttemptCount++;
        LastAttemptedAt = now;
        FailureReason = null;
        Touch(now);
    }

    public void Complete(bool hasFailures, bool hasSuccesses, string? failureReason, DateTimeOffset now)
    {
        Status = hasFailures switch
        {
            true when hasSuccesses => NotificationEventStatus.PartiallyFailed,
            true => NotificationEventStatus.Failed,
            _ => NotificationEventStatus.Delivered
        };
        FailureReason = string.IsNullOrWhiteSpace(failureReason) ? null : failureReason.Trim();
        Touch(now);
    }
}
