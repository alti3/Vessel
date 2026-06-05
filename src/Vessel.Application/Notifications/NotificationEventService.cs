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
    public async Task<Guid> RaiseAsync(TeamId teamId, RaiseNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var notificationEvent = NotificationEvent.Create(teamId,
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

        await dbContext.NotificationEventRepository.AddAsync(notificationEvent, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        backgroundJobs.Enqueue<DispatchNotificationJob>(job => job.RunAsync(notificationEvent.Id.Value, CancellationToken.None));
        await notificationCenter.PublishInAppAsync(notificationEvent, cancellationToken);

        return notificationEvent.Id.Value;
    }
}
