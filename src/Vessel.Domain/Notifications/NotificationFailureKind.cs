namespace Vessel.Domain.Notifications;

/// <summary>
///     Classification of a failed notification delivery (NTF-03), mirroring upstream Coolify's
///     <c>HandlesNotificationResponse</c>.
/// </summary>
public enum NotificationFailureKind
{
    /// <summary>Rate limits, 5xx responses, timeouts, and network errors. Retried; never counted.</summary>
    Transient = 0,

    /// <summary>
    ///     The channel configuration is broken (invalid token, deleted webhook, refused SMTP login, blocked host).
    ///     Not retried; counted towards auto-disable.
    /// </summary>
    Configuration = 1,

    /// <summary>The provider rejected this message (other 4xx). Logged only: never retried and never counted.</summary>
    Payload = 2
}
