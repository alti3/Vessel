using System.Diagnostics;
using Vessel.Application.Auditing;
using Vessel.Application.Common;
using Vessel.Application.Diagnostics;
using Vessel.Application.Email;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Notifications;
using Vessel.Domain.Teams;

namespace Vessel.Application.Notifications;

/// <summary>
///     Delivers a persisted notification event to the team's external targets. Routing uses the per-target event
///     subscriptions of the <see cref="NotificationEventCatalog" /> (NTF-05). Only transient failures are retried;
///     configuration failures count towards auto-disabling the target, which then notifies the team's other channels,
///     the in-app center, and the team admins by email (NTF-03).
/// </summary>
public sealed class NotificationDispatchService(
    IVesselDbContext dbContext,
    IEnumerable<INotificationProvider> providers,
    ISecretVault secretVault,
    ISecretRedactor redactor,
    IBackgroundJobDispatcher backgroundJobs,
    NotificationEventService notificationEvents,
    InstanceEmailOutbox emailOutbox,
    InstanceEmailSender emailSender,
    InstanceLinkBuilder links,
    IAuditWriter auditWriter,
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

        var isTest = string.Equals(notificationEvent.EventType, NotificationEventTypes.Test, StringComparison.Ordinal);
        var message = new NotificationEventDispatchMessage(notificationEvent.Id.Value, notificationEvent.EventType,
            notificationEvent.Severity, notificationEvent.TargetType, notificationEvent.TargetId,
            notificationEvent.Title, notificationEvent.Message, notificationEvent.PayloadJson,
            links.Absolutize(notificationEvent.ResourceUrl), notificationEvent.CreatedAt);

        // Skip targets that already reached a terminal state for this event: delivered, or failed permanently.
        var targets = SelectTargets(notificationEvent)
            .Where(target => !dbContext.NotificationDeliveryAttempts.Any(attempt =>
                attempt.EventId == notificationEvent.Id
                && attempt.TargetId == target.Id
                && (attempt.Status == NotificationDeliveryStatus.Succeeded
                    || attempt.Status == NotificationDeliveryStatus.Failed)))
            .ToArray();

        var hasFailures = false;
        var hasSuccesses = false;
        var failureMessages = new List<string>();
        var disabledTargets = new List<NotificationTarget>();

        foreach (var target in targets)
        {
            var provider = providers.FirstOrDefault(candidate => candidate.Channel == target.Channel);
            if (provider is null)
            {
                hasFailures = true;
                await RecordAttemptAsync(notificationEvent, target, null,
                    NotificationDeliveryResult.ConfigurationError($"No provider is registered for {target.Channel}."),
                    cancellationToken);
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
                    target.ConfigurationJson, secretJson, target.RouteFor(notificationEvent.EventType));

                // Channel secrets are often part of the URL path (Discord/Slack webhooks, Telegram bot token).
                // The marker tag tells the telemetry pipeline to strip the path from child HTTP client spans.
                using Activity? activity = VesselDiagnostics.ActivitySource.StartActivity("SendNotification");
                activity?.SetTag(VesselDiagnostics.RedactChildHttpUrlTag, true);
                activity?.SetTag("vessel.notification.channel", target.Channel.ToString());
                activity?.SetTag("vessel.notification.target_id", target.Id.Value);
                result = await provider.SendAsync(message, context, cancellationToken);
                if (!result.Success && result.FailureReason is not null)
                {
                    result = result with
                    {
                        FailureReason = redactor.Redact(result.FailureReason, CreateRedactionContext(secretJson))
                    };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = NotificationDeliveryResult.Failed(redactor.Redact(
                    $"Notification provider {target.Channel} failed: {ex.GetType().Name}.",
                    CreateRedactionContext(secretJson)));
            }

            await RecordAttemptAsync(notificationEvent, target, provider, result, cancellationToken);

            // Test sends never change the failure counter: the operator is watching the result.
            if (!isTest)
            {
                if (result.Success)
                {
                    target.RecordDeliverySuccess(timeProvider.GetUtcNow());
                }
                else if (target.RecordDeliveryFailure(result.FailureKind,
                             result.FailureReason ?? "Delivery failed.", timeProvider.GetUtcNow()))
                {
                    disabledTargets.Add(target);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            hasSuccesses |= result.Success;
            hasFailures |= !result.Success;
            if (!result.Success && !string.IsNullOrWhiteSpace(result.FailureReason))
                failureMessages.Add(result.FailureReason);
        }

        notificationEvent.Complete(hasFailures, hasSuccesses, string.Join("; ", failureMessages.Distinct()), timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var target in disabledTargets)
            await AnnounceAutoDisabledAsync(target, cancellationToken);
    }

    private NotificationTarget[] SelectTargets(NotificationEvent notificationEvent)
    {
        if (string.Equals(notificationEvent.EventType, NotificationEventTypes.Test, StringComparison.Ordinal))
        {
            // A test send goes only to the target under test, regardless of its event toggles or enabled flag.
            if (!Guid.TryParse(notificationEvent.TargetId, out var targetGuid))
                return [];

            var testTargetId = new NotificationTargetId(targetGuid);
            return dbContext.NotificationTargets
                .Where(target => target.TeamId == notificationEvent.TeamId
                                 && target.Id == testTargetId
                                 && target.Channel != NotificationChannel.InApp)
                .ToArray();
        }

        var definition = NotificationEventCatalog.Find(notificationEvent.EventType);
        if (definition is null)
            return []; // Unknown event types are never routed to external channels.

        return dbContext.NotificationTargets
            .Where(target => target.TeamId == notificationEvent.TeamId
                             && target.IsEnabled
                             && target.Channel != NotificationChannel.InApp)
            .ToArray()
            .Where(target => target.IsSubscribedTo(definition.Key)
                             && (definition.AlwaysDeliver
                                 || (target.Policy.MinimumSeverity <= notificationEvent.Severity
                                     && IsCategoryEnabled(target.Policy, definition.Category))))
            .ToArray();
    }

    private async Task AnnounceAutoDisabledAsync(NotificationTarget target, CancellationToken cancellationToken)
    {
        var reason = target.DisabledReason ?? "Delivery failed.";
        await auditWriter.RecordAsync(target.TeamId, null, AuditActions.NotificationTargetAutoDisabled,
            new AuditTarget("notification_target", target.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["channel"] = target.Channel.ToString(),
                ["failures"] = target.ConsecutiveFailureCount,
                ["reason"] = reason
            }, cancellationToken);

        // In-app notice plus the team's other enabled channels (the disabled target is no longer selected).
        await notificationEvents.RaiseAsync(target.TeamId,
            OperationalNotifications.ChannelDisabled(target.Id.Value, target.Name.Value, target.Channel, reason),
            cancellationToken);

        // The broken channel cannot tell anyone, so the team admins get an email through the instance settings.
        if (!emailSender.IsConfigured())
            return;

        var adminIds = dbContext.TeamMemberships
            .Where(membership => membership.TeamId == target.TeamId
                                 && (membership.Role == TeamRole.Owner || membership.Role == TeamRole.Admin))
            .Select(membership => membership.UserId)
            .ToArray();
        var recipients = dbContext.Users
            .Where(user => adminIds.Contains(user.Id))
            .Select(user => user.Email)
            .ToArray()
            .Select(email => email.Value)
            .ToArray();
        if (recipients.Length == 0)
            return;

        var link = links.Build("/notifications")?.AbsoluteUri;
        var body = $"{target.Channel} notification target '{target.Name.Value}' was disabled after " +
                   $"{NotificationTarget.AutoDisableThreshold} failed deliveries in a row.\n\nReason: {reason}\n\n" +
                   "Fix the settings and enable the target again" + (link is null ? "." : $": {link}");
        foreach (var recipient in recipients)
            emailOutbox.Enqueue(new EmailMessage([recipient], $"Vessel: {target.Channel} notifications were disabled",
                body), EmailPurpose.Notification);
    }

    private static bool IsCategoryEnabled(NotificationDeliveryPolicy policy, NotificationEventCategory category)
    {
        // Legacy per-category switches still apply on top of the per-event subscriptions.
        return category switch
        {
            NotificationEventCategory.Deployment => policy.DeploymentEventsEnabled,
            NotificationEventCategory.Backup => policy.BackupEventsEnabled,
            NotificationEventCategory.Server => policy.ServerEventsEnabled,
            _ => true
        };
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
        else if (result.FailureKind == NotificationFailureKind.Transient && attemptNumber < MaxAttempts &&
                 provider is not null)
        {
            // Only rate limits, server errors, and network problems are retried; a 401 or a deleted webhook is not.
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
}
