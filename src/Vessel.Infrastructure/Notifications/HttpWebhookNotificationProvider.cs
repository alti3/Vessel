using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Notifications;
using Vessel.Domain.Notifications;

namespace Vessel.Infrastructure.Notifications;

public sealed class HttpWebhookNotificationProvider(HttpClient httpClient) : INotificationProvider
{
    public NotificationChannel Channel => NotificationChannel.Webhook;

    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationEventDispatchMessage notification,
        NotificationTargetDispatchContext target,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var secret = NotificationJson.ParseSecret<WebhookNotificationSecret>(target.SecretJson);
            var payload = JsonSerializer.Serialize(new
            {
                id = notification.EventId,
                type = notification.EventType,
                severity = notification.Severity.ToString(),
                title = notification.Title,
                message = notification.Message,
                target = new { type = notification.TargetType.ToString(), id = notification.TargetId },
                resourceUrl = notification.ResourceUrl,
                createdAt = notification.CreatedAt
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            using var request = new HttpRequestMessage(HttpMethod.Post, secret.Url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(secret.Secret))
                request.Headers.Add("X-Vessel-Signature", Sign(payload, secret.Secret));

            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? NotificationDeliveryResult.Succeeded(response.Headers.TryGetValues("X-Request-Id", out var values)
                    ? values.FirstOrDefault()
                    : null)
                : NotificationDeliveryResult.Failed($"Webhook notification delivery failed with HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or UriFormatException)
        {
            return NotificationDeliveryResult.Failed($"Webhook notification delivery failed: {ex.GetType().Name}.");
        }
    }

    private static string Sign(string payload, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var signature = HMACSHA256.HashData(key, bytes);
        return $"sha256={Convert.ToHexString(signature).ToLowerInvariant()}";
    }
}
