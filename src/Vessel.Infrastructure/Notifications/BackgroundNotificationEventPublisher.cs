using Microsoft.Extensions.Logging;
using Vessel.Application.Jobs;
using Vessel.Application.Notifications;
using Vessel.Application.Security;
using Vessel.Domain;

namespace Vessel.Infrastructure.Notifications;

/// <summary>
///     Hands producer notifications to Hangfire (<see cref="RaiseNotificationJob" />). The producer's DbContext is
///     never touched, so a notification problem cannot leave pending entities behind or fail the workflow's own
///     transaction. Every failure is logged and swallowed.
/// </summary>
public sealed partial class BackgroundNotificationEventPublisher(
    IBackgroundJobDispatcher backgroundJobs,
    ISecretRedactor redactor,
    ILogger<BackgroundNotificationEventPublisher> logger)
    : INotificationEventPublisher
{
    public Task PublishAsync(TeamId teamId, RaiseNotificationRequest notification,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Enqueue(teamId, notification);
        }
        catch (Exception exception)
        {
            LogPublishFailed(logger, notification.EventType, exception.GetType().Name);
        }

        return Task.CompletedTask;
    }

    public Task PublishAsync(Func<TeamNotification?> build, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        TeamNotification? built;
        try
        {
            built = build();
        }
        catch (Exception exception)
        {
            LogBuildFailed(logger, exception.GetType().Name);
            return Task.CompletedTask;
        }

        return built is null
            ? Task.CompletedTask
            : PublishAsync(built.TeamId, built.Notification, cancellationToken);
    }

    private void Enqueue(TeamId teamId, RaiseNotificationRequest notification)
    {
        // Hangfire persists job arguments, so redact before they leave the process.
        var safe = notification with
        {
            Title = redactor.Redact(notification.Title),
            Message = redactor.Redact(notification.Message),
            PayloadJson = string.IsNullOrWhiteSpace(notification.PayloadJson)
                ? "{}"
                : redactor.Redact(notification.PayloadJson)
        };
        var teamGuid = teamId.Value;
        backgroundJobs.Enqueue<RaiseNotificationJob>(job => job.RunAsync(teamGuid, safe, CancellationToken.None));
    }

    [LoggerMessage(EventId = 1301, Level = LogLevel.Warning,
        Message = "Failed to queue notification event {EventType}: {ExceptionType}.")]
    private static partial void LogPublishFailed(ILogger logger, string eventType, string exceptionType);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Warning,
        Message = "Failed to build notification event: {ExceptionType}.")]
    private static partial void LogBuildFailed(ILogger logger, string exceptionType);
}
