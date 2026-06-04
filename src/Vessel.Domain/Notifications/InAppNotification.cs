using Vessel.Domain.Common;

namespace Vessel.Domain.Notifications;

public sealed class InAppNotification : Entity<InAppNotificationId>
{
    private InAppNotification()
    {
    }

    private InAppNotification(
        InAppNotificationId id,
        NotificationEventId eventId,
        TeamId teamId,
        UserId? userId,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        EventId = eventId;
        TeamId = teamId;
        UserId = userId;
        Status = InAppNotificationStatus.Unread;
    }

    public NotificationEventId EventId { get; private set; }

    public TeamId TeamId { get; private set; }

    public UserId? UserId { get; private set; }

    public InAppNotificationStatus Status { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public static InAppNotification Create(NotificationEvent notificationEvent, DateTimeOffset now)
    {
        return new InAppNotification(InAppNotificationId.New(), notificationEvent.Id, notificationEvent.TeamId,
            notificationEvent.UserId, now);
    }

    public void MarkRead(DateTimeOffset now)
    {
        if (Status == InAppNotificationStatus.Archived)
            return;

        Status = InAppNotificationStatus.Read;
        ReadAt ??= now;
        Touch(now);
    }

    public void Archive(DateTimeOffset now)
    {
        if (Status == InAppNotificationStatus.Archived)
            return;

        Status = InAppNotificationStatus.Archived;
        ReadAt ??= now;
        ArchivedAt ??= now;
        Touch(now);
    }
}
