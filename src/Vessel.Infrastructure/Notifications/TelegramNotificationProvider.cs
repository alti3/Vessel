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
            // The token is part of the request path; a strict format check prevents path injection.
            if (!NotificationCredentialFormats.IsValidTelegramBotToken(secret.BotToken))
                return NotificationDeliveryResult.ConfigurationError("Telegram bot token has an invalid format.");
            using var response = await httpClient.PostAsJsonAsync($"/bot{secret.BotToken}/sendMessage", new
            {
                chat_id = secret.ChatId,
                // NTF-05: a per-event topic route overrides the target's default thread.
                message_thread_id = string.IsNullOrWhiteSpace(target.EventRoute) ? secret.ThreadId : target.EventRoute,
                text = $"{notification.Title}\n{notification.Message}\n{notification.ResourceUrl}"
            }, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
                return NotificationDeliveryResult.Succeeded();

            // 400 "chat not found" means a wrong chat id (upstream). The body itself is never stored.
            if ((int)response.StatusCode == 400 && body.Contains("chat not found", StringComparison.OrdinalIgnoreCase))
                return NotificationDeliveryResult.ConfigurationError(
                    "Telegram notification delivery failed with HTTP 400 (chat not found).");

            return NotificationDeliveryResult.FromHttpStatus("Telegram", (int)response.StatusCode,
                NotificationHttpFailures.TelegramConfigurationStatuses);
        }
        catch (Exception ex) when (NotificationHttpFailures.IsHandled(ex, cancellationToken))
        {
            return NotificationHttpFailures.FromException("Telegram", ex, cancellationToken);
        }
    }
}
