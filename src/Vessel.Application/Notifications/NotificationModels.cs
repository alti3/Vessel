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
    bool ServerEvents,
    bool HasCredentials,
    IReadOnlyList<string>? Events = null,
    bool UsesDefaultEvents = true,
    IReadOnlyDictionary<string, string>? EventRoutes = null,
    int ConsecutiveFailureCount = 0,
    string? DisabledReason = null,
    DateTimeOffset? AutoDisabledAt = null);

/// <summary>One entry of the notification event catalog as exposed by the API.</summary>
public sealed record NotificationEventCatalogItem(
    string Key,
    string? CoolifyKey,
    NotificationEventCategory Category,
    bool DefaultEnabled,
    bool AlwaysDeliver,
    string Throttle);

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
    string? SecretJson,
    IReadOnlyList<string>? Events = null,
    IReadOnlyDictionary<string, string>? EventRoutes = null);

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
    string? SecretJson,
    string? EventRoute = null);

/// <summary>
///     Outcome of one provider send. Failures carry a <see cref="NotificationFailureKind" />: only transient failures
///     are retried, and only configuration failures count towards auto-disabling the target (NTF-03). Failure reasons
///     never contain provider response bodies, URLs, or credentials.
/// </summary>
public sealed record NotificationDeliveryResult(
    bool Success,
    string? ProviderMessageId,
    string? FailureReason,
    NotificationFailureKind FailureKind = NotificationFailureKind.Transient)
{
    public static NotificationDeliveryResult Succeeded(string? providerMessageId = null)
    {
        return new NotificationDeliveryResult(true, providerMessageId, null);
    }

    public static NotificationDeliveryResult Failed(string? failureReason,
        NotificationFailureKind kind = NotificationFailureKind.Transient)
    {
        var normalized = string.IsNullOrWhiteSpace(failureReason)
            ? "Notification delivery failed."
            : failureReason.Trim();
        return new NotificationDeliveryResult(false, null, normalized, kind);
    }

    public static NotificationDeliveryResult ConfigurationError(string failureReason)
    {
        return Failed(failureReason, NotificationFailureKind.Configuration);
    }

    /// <summary>
    ///     Classifies a non-success HTTP status like upstream <c>HandlesNotificationResponse</c>: 429 and 5xx are transient,
    ///     the statuses in <paramref name="configurationStatuses" /> are configuration errors, and other 4xx are payload
    ///     rejections. The reason contains only the channel and status code.
    /// </summary>
    public static NotificationDeliveryResult FromHttpStatus(string channel, int statusCode,
        IReadOnlyCollection<int> configurationStatuses)
    {
        var kind = statusCode == 429 || statusCode >= 500
            ? NotificationFailureKind.Transient
            : configurationStatuses.Contains(statusCode)
                ? NotificationFailureKind.Configuration
                : statusCode is >= 400 and < 500
                    ? NotificationFailureKind.Payload
                    : NotificationFailureKind.Transient;
        return Failed($"{channel} notification delivery failed with HTTP {statusCode}.", kind);
    }
}
