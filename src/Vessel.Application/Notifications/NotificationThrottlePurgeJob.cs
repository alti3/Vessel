using Vessel.Application.Jobs;

namespace Vessel.Application.Notifications;

/// <summary>
///     Daily housekeeping for notification throttles (NTF-04): removes claims older than <see cref="Retention" />.
///     Throttle rows of deleted teams cascade with the team; this sweep also bounds rows of deleted resources. An
///     incident that is still open after the retention period alerts once more, which doubles as a reminder.
/// </summary>
public sealed class NotificationThrottlePurgeJob(INotificationThrottle throttle, TimeProvider timeProvider)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        return throttle.PurgeAsync(timeProvider.GetUtcNow() - Retention, cancellationToken);
    }
}

public static class NotificationRecurringJobs
{
    public const string ThrottlePurgeJobId = "notifications.throttles.purge";
    public const string ThrottlePurgeCronExpression = "41 3 * * *";

    public static void Register(IRecurringJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        scheduler.AddOrUpdate<NotificationThrottlePurgeJob>(
            ThrottlePurgeJobId,
            job => job.RunAsync(CancellationToken.None),
            ThrottlePurgeCronExpression);
    }
}
