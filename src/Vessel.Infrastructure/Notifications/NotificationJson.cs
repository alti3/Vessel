using System.Text.Json;

namespace Vessel.Infrastructure.Notifications;

internal static class NotificationJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static T ParseSecret<T>(string? secretJson)
    {
        if (string.IsNullOrWhiteSpace(secretJson))
            throw new InvalidOperationException("Notification target credentials are not configured.");

        return JsonSerializer.Deserialize<T>(secretJson, Options)
               ?? throw new InvalidOperationException("Notification target credentials are invalid.");
    }
}
