using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vessel.Domain.Webhooks;

namespace Vessel.Infrastructure.Persistence;

/// <summary>JSON (jsonb) mapping of <see cref="WebhookEvent.ApplicationOutcomes" />.</summary>
internal static class WebhookOutcomeConversions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly ValueConverter<WebhookApplicationOutcome[], string> Converter = new(
        value => JsonSerializer.Serialize(value, Options),
        value => Deserialize(value));

    public static readonly ValueComparer<WebhookApplicationOutcome[]> Comparer = new(
        (left, right) => left == null ? right == null : right != null && left.SequenceEqual(right),
        value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
        value => value.ToArray());

    private static WebhookApplicationOutcome[] Deserialize(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : JsonSerializer.Deserialize<WebhookApplicationOutcome[]>(value, Options) ?? [];
    }
}
