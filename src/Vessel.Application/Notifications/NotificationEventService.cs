using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Notifications;

namespace Vessel.Application.Notifications;

public sealed class NotificationEventService(
    IVesselDbContext dbContext,
    NotificationCenterService notificationCenter,
    IBackgroundJobDispatcher backgroundJobs,
    ISecretRedactor redactor,
    TimeProvider timeProvider)
{
    /// <summary>
    ///     Internal producer path (called from <see cref="RaiseNotificationJob" />). It is intentionally not exposed
    ///     through the public API: arbitrary callers must not be able to push content to every team channel.
    /// </summary>
    public async Task<Guid> RaiseAsync(TeamId teamId, RaiseNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var notificationEvent = CreateEvent(teamId, request);

        await dbContext.NotificationEventRepository.AddAsync(notificationEvent, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        backgroundJobs.Enqueue<DispatchNotificationJob>(job => job.RunAsync(notificationEvent.Id.Value, CancellationToken.None));
        await notificationCenter.PublishInAppAsync(notificationEvent, cancellationToken);

        return notificationEvent.Id.Value;
    }

    /// <summary>
    ///     Persists a fixed-content test event that the dispatcher routes only to <paramref name="targetId" />.
    ///     Callers must authorize the actor first (see <see cref="NotificationTargetService.SendTestAsync" />).
    /// </summary>
    internal async Task<Guid> RaiseTestAsync(TeamId teamId, NotificationTargetId targetId, string targetName,
        CancellationToken cancellationToken = default)
    {
        var notificationEvent = CreateEvent(teamId, OperationalNotifications.Test(targetId.Value, targetName));

        await dbContext.NotificationEventRepository.AddAsync(notificationEvent, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        backgroundJobs.Enqueue<DispatchNotificationJob>(job => job.RunAsync(notificationEvent.Id.Value, CancellationToken.None));

        return notificationEvent.Id.Value;
    }

    private NotificationEvent CreateEvent(TeamId teamId, RaiseNotificationRequest request)
    {
        return NotificationEvent.Create(teamId,
            request.UserId.HasValue ? new UserId(request.UserId.Value) : null,
            request.EventType,
            request.Severity,
            request.TargetType,
            request.TargetId,
            redactor.Redact(request.Title),
            redactor.Redact(request.Message),
            redactor.Redact(string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson),
            request.ResourceUrl,
            timeProvider.GetUtcNow());
    }
}
