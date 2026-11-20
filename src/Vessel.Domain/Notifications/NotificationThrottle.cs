using Vessel.Domain.Common;

namespace Vessel.Domain.Notifications;

/// <summary>
///     Records when a notification event was last delivered for one subject (team + event type + resource), so
///     repeated checks across workers do not alert again within the event's throttle window (NTF-04). Equivalent to
///     upstream Coolify's <c>notification_throttles</c> table. Rows are claimed atomically by the Infrastructure
///     implementation of the Application throttle interface; this entity documents the shape and invariants.
/// </summary>
public sealed class NotificationThrottle : Entity<NotificationThrottleId>
{
    public const int MaxEventTypeLength = 120;
    public const int MaxSubjectKeyLength = 200;

    private NotificationThrottle()
    {
    }

    private NotificationThrottle(NotificationThrottleId id, TeamId teamId, string eventType, string subjectKey,
        DateTimeOffset sentAt)
        : base(id, sentAt)
    {
        TeamId = teamId;
        EventType = eventType;
        SubjectKey = subjectKey;
        SentAt = sentAt;
    }

    public TeamId TeamId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    /// <summary>Resource the throttle belongs to, for example <c>Server:7f0c…</c>.</summary>
    public string SubjectKey { get; private set; } = string.Empty;

    public DateTimeOffset SentAt { get; private set; }

    public static NotificationThrottle Create(TeamId teamId, string eventType, string subjectKey, DateTimeOffset now)
    {
        return new NotificationThrottle(NotificationThrottleId.New(), teamId,
            DomainValidation.Required(eventType, nameof(EventType), MaxEventTypeLength),
            DomainValidation.Required(subjectKey, nameof(SubjectKey), MaxSubjectKeyLength), now);
    }

    /// <summary>Builds the subject key for a notification's target resource, or null when it has none.</summary>
    public static string? SubjectKeyFor(NotificationTargetType targetType, string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return null;

        var key = $"{targetType}:{targetId.Trim()}";
        return key.Length <= MaxSubjectKeyLength ? key : key[..MaxSubjectKeyLength];
    }

    public bool TryRenew(DateTimeOffset cutoff, DateTimeOffset now)
    {
        if (SentAt >= cutoff)
            return false;

        SentAt = now;
        Touch(now);
        return true;
    }
}
