using Vessel.Domain.Notifications;

namespace Vessel.Application.Notifications;

public sealed record NotificationTargetListItem(
    Guid Id,
    string Name,
    NotificationChannel Channel,
    bool IsEnabled,
    NotificationSeverity MinimumSeverity,
    bool DeploymentEvents,
    bool BackupEvents,
    bool ServerEvents);

public sealed record InAppNotificationListItem(
    Guid Id,
    Guid EventId,
    string EventType,
    NotificationSeverity Severity,
    NotificationTargetType TargetType,
    string? TargetId,
    string Title,
    string Message,
    string? ResourceUrl,
    InAppNotificationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? ArchivedAt);

public sealed record NotificationDeliveryAttemptListItem(
    Guid Id,
    Guid EventId,
    Guid TargetId,
    NotificationChannel Channel,
    int AttemptNumber,
    NotificationDeliveryStatus Status,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? RetryAfter);

public sealed record UpsertNotificationTargetRequest(
    string Name,
    NotificationChannel Channel,
    bool IsEnabled,
    NotificationSeverity MinimumSeverity,
    bool DeploymentEvents,
    bool BackupEvents,
    bool ServerEvents,
    string? ConfigurationJson,
    string? SecretJson);

public sealed record RaiseNotificationRequest(
    string EventType,
    NotificationSeverity Severity,
    NotificationTargetType TargetType,
    string? TargetId,
    string Title,
    string Message,
    string? PayloadJson,
    string? ResourceUrl,
    Guid? UserId);

public sealed record NotificationEventDispatchMessage(
    Guid EventId,
    string EventType,
    NotificationSeverity Severity,
    NotificationTargetType TargetType,
    string? TargetId,
    string Title,
    string Message,
    string PayloadJson,
    string? ResourceUrl,
    DateTimeOffset CreatedAt);

public sealed record NotificationTargetDispatchContext(
    Guid TargetId,
    string Name,
    NotificationChannel Channel,
    string ConfigurationJson,
    string? SecretJson);

public sealed record NotificationDeliveryResult(bool Success, string? ProviderMessageId, string? FailureReason)
{
    public static NotificationDeliveryResult Succeeded(string? providerMessageId = null)
    {
        return new NotificationDeliveryResult(true, providerMessageId, null);
    }

    public static NotificationDeliveryResult Failed(string? failureReason)
    {
        var normalized = string.IsNullOrWhiteSpace(failureReason)
            ? "Notification delivery failed."
            : failureReason.Trim();
        return new NotificationDeliveryResult(false, null, normalized);
    }
}
