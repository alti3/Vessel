using System.Net.Http.Json;
using Vessel.Application.Notifications;
using Vessel.Domain.Notifications;

namespace Vessel.Infrastructure.Notifications;

public sealed class DiscordNotificationProvider(HttpClient httpClient) : INotificationProvider
{
    public NotificationChannel Channel => NotificationChannel.Discord;

    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationEventDispatchMessage notification,
        NotificationTargetDispatchContext target,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var secret = NotificationJson.ParseSecret<WebhookUrlSecret>(target.SecretJson);
            using var response = await httpClient.PostAsJsonAsync(secret.WebhookUrl, new
            {
                content = $"**{notification.Title}**\n{notification.Message}\n{notification.ResourceUrl}"
            }, cancellationToken);
            await response.Content.ReadAsByteArrayAsync(cancellationToken);

            return response.IsSuccessStatusCode
                ? NotificationDeliveryResult.Succeeded()
                : NotificationDeliveryResult.FromHttpStatus("Discord", (int)response.StatusCode,
                    NotificationHttpFailures.DiscordConfigurationStatuses);
        }
        catch (Exception ex) when (NotificationHttpFailures.IsHandled(ex, cancellationToken))
        {
            return NotificationHttpFailures.FromException("Discord", ex, cancellationToken);
        }
    }
}
