using Microsoft.Extensions.Logging;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Monitoring;

namespace Vessel.Application.Resources.Status;

/// <summary>
///     Marks a resource as about to stop on purpose (a deployment replacing containers, an explicit stop/restart, a
///     volume backup that pauses its source) so runtime reconciliation does not send a "stopped unexpectedly"
///     notification for it (SRV-04, upstream <c>NotifyResourceStoppedJob</c> semantics). Best effort: it never throws
///     into the caller, because a missed suppression only costs a spurious notification.
/// </summary>
public sealed partial class ExpectedStopRecorder(
    IVesselDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<ExpectedStopRecorder> logger)
{
    public async Task ExpectStopAsync(
        ResourceRuntimeKind kind,
        Guid resourceId,
        TeamId teamId,
        ServerId serverId,
        TimeSpan? window = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            ResourceRuntimeStatus? status = dbContext.ResourceRuntimeStatuses
                .SingleOrDefault(item => item.Kind == kind && item.ResourceId == resourceId);
            if (status is null)
            {
                status = ResourceRuntimeStatus.Create(kind, resourceId, teamId, serverId, now);
                await dbContext.ResourceRuntimeStatusRepository.AddAsync(status, cancellationToken);
            }

            status.ExpectStop(now, window);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRecordFailed(logger, exception, kind, resourceId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Could not record an expected stop for {Kind} {ResourceId}; a stop notification may follow.")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, ResourceRuntimeKind kind,
        Guid resourceId);
}
