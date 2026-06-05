using System.Net.Http.Json;
using Vessel.Application.Notifications;
using Vessel.Domain.Notifications;

namespace Vessel.Infrastructure.Notifications;

public sealed class TelegramNotificationProvider(HttpClient httpClient) : INotificationProvider
{
    public NotificationChannel Channel => NotificationChannel.Telegram;

    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationEventDispatchMessage notification,
        NotificationTargetDispatchContext target,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var secret = NotificationJson.ParseSecret<TelegramNotificationSecret>(target.SecretJson);
            using var response = await httpClient.PostAsJsonAsync($"/bot{secret.BotToken}/sendMessage", new
            {
                chat_id = secret.ChatId,
                message_thread_id = secret.ThreadId,
                text = $"{notification.Title}\n{notification.Message}\n{notification.ResourceUrl}"
            }, cancellationToken);
            await response.Content.ReadAsByteArrayAsync(cancellationToken);

            return response.IsSuccessStatusCode
                ? NotificationDeliveryResult.Succeeded()
                : NotificationDeliveryResult.Failed($"Telegram notification delivery failed with HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or UriFormatException)
        {
            return NotificationDeliveryResult.Failed($"Telegram notification delivery failed: {ex.GetType().Name}.");
        }
    }
}
