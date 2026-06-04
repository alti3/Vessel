using Vessel.Domain;
using Vessel.Domain.Notifications;

namespace Vessel.UnitTests.Domain;

public sealed class Phase13NotificationDomainTests
{
    [Fact]
    public void InAppNotification_tracks_read_and_archive_state()
    {
        var now = DateTimeOffset.Parse("2026-06-04T12:00:00Z");
        var notificationEvent = NotificationEvent.Create(TeamId.New(), UserId.New(), "deployment.failed",
            NotificationSeverity.Critical, NotificationTargetType.Deployment, Guid.NewGuid().ToString("D"),
            "Deployment failed", "The deployment failed.", "{}", "/deployments/1", now);

        var inApp = InAppNotification.Create(notificationEvent, now);
        Assert.Equal(InAppNotificationStatus.Unread, inApp.Status);

        inApp.MarkRead(now.AddMinutes(1));
        inApp.Archive(now.AddMinutes(2));
        inApp.Archive(now.AddMinutes(3));

        Assert.Equal(InAppNotificationStatus.Archived, inApp.Status);
        Assert.Equal(now.AddMinutes(1), inApp.ReadAt);
        Assert.Equal(now.AddMinutes(2), inApp.ArchivedAt);
    }

    [Fact]
    public void DeliveryAttempt_records_retry_and_failure_without_provider_secret()
    {
        var now = DateTimeOffset.Parse("2026-06-04T12:00:00Z");
        var attempt = NotificationDeliveryAttempt.Create(NotificationEventId.New(), NotificationTargetId.New(),
            NotificationChannel.Webhook, 1, now);

        attempt.ScheduleRetry("Webhook notification delivery failed with HTTP 500.", now.AddMinutes(2), now);
        Assert.Equal(NotificationDeliveryStatus.RetryScheduled, attempt.Status);

        attempt.MarkFailed("Webhook notification delivery failed: HttpRequestException.", now.AddMinutes(3));
        Assert.Equal(NotificationDeliveryStatus.Failed, attempt.Status);
        Assert.Equal(now.AddMinutes(3), attempt.CompletedAt);
    }

    [Fact]
    public void DeliveryAttempt_rejects_retry_times_that_are_not_in_the_future()
    {
        var now = DateTimeOffset.Parse("2026-06-04T12:00:00Z");
        var attempt = NotificationDeliveryAttempt.Create(NotificationEventId.New(), NotificationTargetId.New(),
            NotificationChannel.Webhook, 1, now);

        Assert.Throws<Vessel.Domain.Common.DomainException>(() =>
            attempt.ScheduleRetry("Webhook notification delivery failed.", now, now));
    }
}
