using System.Net.Http.Json;
using Vessel.Application.Notifications;
using Vessel.Domain.Notifications;

namespace Vessel.Infrastructure.Notifications;

public sealed class SlackNotificationProvider(HttpClient httpClient) : INotificationProvider
{
    public NotificationChannel Channel => NotificationChannel.Slack;

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
                text = $"*{notification.Title}*\n{notification.Message}\n{notification.ResourceUrl}"
            }, cancellationToken);

            return response.IsSuccessStatusCode
                ? NotificationDeliveryResult.Succeeded()
                : NotificationDeliveryResult.Failed($"Slack notification delivery failed with HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or UriFormatException)
        {
            return NotificationDeliveryResult.Failed($"Slack notification delivery failed: {ex.GetType().Name}.");
        }
    }
}
