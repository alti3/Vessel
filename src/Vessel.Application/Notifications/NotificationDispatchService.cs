using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Notifications;

namespace Vessel.Application.Notifications;

public sealed class NotificationDispatchService(
    IVesselDbContext dbContext,
    IEnumerable<INotificationProvider> providers,
    ISecretVault secretVault,
    ISecretRedactor redactor,
    IBackgroundJobDispatcher backgroundJobs,
    TimeProvider timeProvider)
{
    private const int MaxAttempts = 3;

    public async Task DispatchAsync(Guid notificationEventId, CancellationToken cancellationToken = default)
    {
        var id = new NotificationEventId(notificationEventId);
        var notificationEvent = dbContext.NotificationEvents.FirstOrDefault(notification => notification.Id == id)
                                ?? throw new InvalidOperationException("Notification event was not found.");

        var now = timeProvider.GetUtcNow();
        notificationEvent.StartDispatch(now);
        await dbContext.SaveChangesAsync(cancellationToken);

        var message = new NotificationEventDispatchMessage(notificationEvent.Id.Value, notificationEvent.EventType,
            notificationEvent.Severity, notificationEvent.TargetType, notificationEvent.TargetId,
            notificationEvent.Title, notificationEvent.Message, notificationEvent.PayloadJson,
            notificationEvent.ResourceUrl, notificationEvent.CreatedAt);

        var targets = dbContext.NotificationTargets
            .Where(target => target.TeamId == notificationEvent.TeamId
                             && target.IsEnabled
                             && target.Channel != NotificationChannel.InApp
                             && target.Policy.MinimumSeverity <= notificationEvent.Severity)
            .ToArray()
            .Where(target => !dbContext.NotificationDeliveryAttempts.Any(attempt =>
                attempt.EventId == notificationEvent.Id
                && attempt.TargetId == target.Id
                && attempt.Status == NotificationDeliveryStatus.Succeeded))
            .Where(target => IsEventAllowed(target.Policy, notificationEvent.EventType))
            .ToArray();

        var hasFailures = false;
        var hasSuccesses = false;
        var failureMessages = new List<string>();

        foreach (var target in targets)
        {
            var provider = providers.FirstOrDefault(candidate => candidate.Channel == target.Channel);
            if (provider is null)
            {
                hasFailures = true;
                await RecordAttemptAsync(notificationEvent, target, null,
                    NotificationDeliveryResult.Failed($"No provider is registered for {target.Channel}."), cancellationToken);
                continue;
            }

            NotificationDeliveryResult result;
            string? secretJson = null;
            try
            {
                if (target.CredentialsReferenceId.HasValue)
                {
                    secretJson = await secretVault.RevealForDeploymentAsync(notificationEvent.TeamId,
                        target.CredentialsReferenceId.Value, cancellationToken);
                }

                var context = new NotificationTargetDispatchContext(target.Id.Value, target.Name.Value, target.Channel,
                    target.ConfigurationJson, secretJson);
                result = await provider.SendAsync(message, context, cancellationToken);
                if (!result.Success && result.FailureReason is not null)
                {
                    result = NotificationDeliveryResult.Failed(redactor.Redact(result.FailureReason,
                        CreateRedactionContext(secretJson)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = NotificationDeliveryResult.Failed(redactor.Redact(
                    $"Notification provider {target.Channel} failed: {ex.GetType().Name}.",
                    CreateRedactionContext(secretJson)));
            }

            await RecordAttemptAsync(notificationEvent, target, provider, result, cancellationToken);

            hasSuccesses |= result.Success;
            hasFailures |= !result.Success;
            if (!result.Success && !string.IsNullOrWhiteSpace(result.FailureReason))
                failureMessages.Add(result.FailureReason);
        }

        notificationEvent.Complete(hasFailures, hasSuccesses, string.Join("; ", failureMessages.Distinct()), timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static RedactionContext? CreateRedactionContext(string? secretJson)
    {
        return string.IsNullOrWhiteSpace(secretJson) ? null : new RedactionContext([secretJson]);
    }

    private async Task RecordAttemptAsync(NotificationEvent notificationEvent, NotificationTarget target,
        INotificationProvider? provider, NotificationDeliveryResult result, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var attemptNumber = dbContext.NotificationDeliveryAttempts.Count(attempt =>
            attempt.EventId == notificationEvent.Id && attempt.TargetId == target.Id) + 1;
        var attempt = NotificationDeliveryAttempt.Create(notificationEvent.Id, target.Id, target.Channel,
            attemptNumber, now);

        if (result.Success)
        {
            attempt.MarkSucceeded(result.ProviderMessageId, now);
        }
        else if (attemptNumber < MaxAttempts && provider is not null)
        {
            var retryAfter = now.Add(TimeSpan.FromMinutes(Math.Pow(2, attemptNumber)));
            attempt.ScheduleRetry(result.FailureReason ?? "Notification delivery failed.", retryAfter, now);
            backgroundJobs.Schedule<DispatchNotificationJob>(
                job => job.RunAsync(notificationEvent.Id.Value, CancellationToken.None),
                retryAfter - now);
        }
        else
        {
            attempt.MarkFailed(result.FailureReason ?? "Notification delivery failed.", now);
        }

        await dbContext.NotificationDeliveryAttemptRepository.AddAsync(attempt, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool IsEventAllowed(NotificationDeliveryPolicy policy, string eventType)
    {
        return eventType.StartsWith("deployment.", StringComparison.OrdinalIgnoreCase) && policy.DeploymentEventsEnabled
               || eventType.StartsWith("backup.", StringComparison.OrdinalIgnoreCase) && policy.BackupEventsEnabled
               || eventType.StartsWith("server.", StringComparison.OrdinalIgnoreCase) && policy.ServerEventsEnabled
               || !eventType.StartsWith("deployment.", StringComparison.OrdinalIgnoreCase)
               && !eventType.StartsWith("backup.", StringComparison.OrdinalIgnoreCase)
               && !eventType.StartsWith("server.", StringComparison.OrdinalIgnoreCase);
    }
}
