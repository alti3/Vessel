using System.Text.Json;
using Vessel.Application.Notifications;
using Vessel.Infrastructure.OutboundRequests;

namespace Vessel.Infrastructure.Notifications;

/// <summary>
///     Maps HTTP provider exceptions to classified delivery results (NTF-03). Reasons name only the channel and the
///     exception type: exception messages can contain the full URL with a webhook secret or bot token.
/// </summary>
internal static class NotificationHttpFailures
{
    public static readonly int[] WebhookConfigurationStatuses = [401, 403, 404, 410];
    public static readonly int[] DiscordConfigurationStatuses = [401, 403, 404];
    public static readonly int[] SlackConfigurationStatuses = [403, 404, 410];
    public static readonly int[] TelegramConfigurationStatuses = [401, 403, 404];

    public static NotificationDeliveryResult FromException(string channel, Exception exception,
        CancellationToken cancellationToken)
    {
        // SocketsHttpHandler wraps exceptions thrown by the ConnectCallback, so look one level down as well.
        if (exception.InnerException is OutboundRequestBlockedException)
            exception = exception.InnerException;

        return exception switch
        {
            // The target now resolves to a blocked address or has an invalid URL: the configuration is broken.
            OutboundRequestBlockedException => NotificationDeliveryResult.ConfigurationError(
                $"{channel} notification target is not allowed by the outbound request policy."),
            InvalidOperationException or UriFormatException or JsonException =>
                NotificationDeliveryResult.ConfigurationError(
                    $"{channel} notification credentials are not valid: {exception.GetType().Name}."),
            TaskCanceledException when !cancellationToken.IsCancellationRequested =>
                NotificationDeliveryResult.Failed($"{channel} notification delivery timed out."),
            _ => NotificationDeliveryResult.Failed(
                $"{channel} notification delivery failed: {exception.GetType().Name}.")
        };
    }

    public static bool IsHandled(Exception exception, CancellationToken cancellationToken)
    {
        return exception is HttpRequestException or InvalidOperationException or UriFormatException
                   or JsonException
               || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);
    }
}
