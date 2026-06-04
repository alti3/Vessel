using Vessel.Application.Persistence;
using Vessel.Application.Realtime;
using Vessel.Domain;
using Vessel.Domain.Notifications;

namespace Vessel.Application.Notifications;

public sealed class NotificationCenterService(
    IVesselDbContext dbContext,
    IRealtimeNotifier realtimeNotifier,
    TimeProvider timeProvider)
{
    public IReadOnlyList<InAppNotificationListItem> ListInApp(TeamId teamId, UserId userId, bool includeArchived = false)
    {
        var allowedStatuses = includeArchived
            ? new[] { InAppNotificationStatus.Unread, InAppNotificationStatus.Read, InAppNotificationStatus.Archived }
            : [InAppNotificationStatus.Unread, InAppNotificationStatus.Read];

        return (from inApp in dbContext.InAppNotifications
                join notificationEvent in dbContext.NotificationEvents on inApp.EventId equals notificationEvent.Id
                where inApp.TeamId == teamId
                      && (inApp.UserId == null || inApp.UserId == userId)
                      && allowedStatuses.Contains(inApp.Status)
                orderby inApp.CreatedAt descending
                select new InAppNotificationListItem(
                    inApp.Id.Value,
                    notificationEvent.Id.Value,
                    notificationEvent.EventType,
                    notificationEvent.Severity,
                    notificationEvent.TargetType,
                    notificationEvent.TargetId,
                    notificationEvent.Title,
                    notificationEvent.Message,
                    notificationEvent.ResourceUrl,
                    inApp.Status,
                    inApp.CreatedAt,
                    inApp.ReadAt,
                    inApp.ArchivedAt))
            .Take(100)
            .ToArray();
    }

    public async Task MarkReadAsync(TeamId teamId, UserId userId, InAppNotificationId notificationId,
        CancellationToken cancellationToken = default)
    {
        var notification = GetAuthorizedNotification(teamId, userId, notificationId);
        notification.MarkRead(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ArchiveAsync(TeamId teamId, UserId userId, InAppNotificationId notificationId,
        CancellationToken cancellationToken = default)
    {
        var notification = GetAuthorizedNotification(teamId, userId, notificationId);
        notification.Archive(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task PublishInAppAsync(NotificationEvent notificationEvent, CancellationToken cancellationToken)
    {
        var inApp = InAppNotification.Create(notificationEvent, timeProvider.GetUtcNow());
        await dbContext.InAppNotificationRepository.AddAsync(inApp, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var payload = new InAppNotificationListItem(inApp.Id.Value, notificationEvent.Id.Value,
            notificationEvent.EventType, notificationEvent.Severity, notificationEvent.TargetType,
            notificationEvent.TargetId, notificationEvent.Title, notificationEvent.Message,
            notificationEvent.ResourceUrl, inApp.Status, inApp.CreatedAt, inApp.ReadAt, inApp.ArchivedAt);

        await realtimeNotifier.PublishAsync(
            new RealtimeGroup(RealtimeGroupKind.Team, notificationEvent.TeamId.Value.ToString("D")),
            new RealtimeMessage("notification.received", payload), cancellationToken);

        if (notificationEvent.UserId.HasValue)
        {
            await realtimeNotifier.PublishAsync(
                new RealtimeGroup(RealtimeGroupKind.User, notificationEvent.UserId.Value.Value.ToString("D")),
                new RealtimeMessage("notification.received", payload), cancellationToken);
        }
    }

    private InAppNotification GetAuthorizedNotification(TeamId teamId, UserId userId, InAppNotificationId notificationId)
    {
        return dbContext.InAppNotifications.FirstOrDefault(notification =>
                   notification.Id == notificationId
                   && notification.TeamId == teamId
                   && (notification.UserId == null || notification.UserId == userId))
               ?? throw new InvalidOperationException("Notification was not found.");
    }
}
