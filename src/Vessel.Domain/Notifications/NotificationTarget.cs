using System.Text.Json;
using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Notifications;

public sealed class NotificationTarget : Entity<NotificationTargetId>
{
    /// <summary>Consecutive configuration failures after which a target is disabled (upstream threshold).</summary>
    public const int AutoDisableThreshold = 3;

    public const int MaxRouteLength = 64;

    private NotificationTarget()
    {
    }

    private NotificationTarget(
        NotificationTargetId id,
        TeamId teamId,
        ResourceName name,
        NotificationChannel channel,
        SecretReferenceId? credentialsReferenceId,
        string configurationJson,
        NotificationDeliveryPolicy policy,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        Name = name;
        Channel = channel;
        CredentialsReferenceId = credentialsReferenceId;
        ConfigurationJson = configurationJson;
        Policy = policy;
        IsEnabled = true;
    }

    public TeamId TeamId { get; private set; }

    public ResourceName Name { get; private set; }

    public NotificationChannel Channel { get; private set; }

    public SecretReferenceId? CredentialsReferenceId { get; private set; }

    public string ConfigurationJson { get; private set; } = "{}";

    public NotificationDeliveryPolicy Policy { get; private set; }

    public bool IsEnabled { get; private set; }

    /// <summary>
    ///     Explicit event subscriptions (catalog keys). <c>null</c> means "catalog defaults", which is also how targets
    ///     created before NTF-05 behave.
    /// </summary>
    public string[]? SubscribedEvents { get; private set; }

    /// <summary>
    ///     Per-event channel routes as a JSON object of event key to route (for example a Telegram topic id). Events
    ///     without a route use the target's default destination.
    /// </summary>
    public string EventRoutesJson { get; private set; } = "{}";

    /// <summary>Consecutive configuration failures (NTF-03). Reset by a success or by any settings change.</summary>
    public int ConsecutiveFailureCount { get; private set; }

    /// <summary>Why the target was disabled automatically. Safe for display (no provider response bodies).</summary>
    public string? DisabledReason { get; private set; }

    public DateTimeOffset? AutoDisabledAt { get; private set; }

    public IReadOnlyCollection<string> EffectiveSubscriptions =>
        SubscribedEvents ?? (IReadOnlyCollection<string>)NotificationEventCatalog.DefaultSubscriptions;

    public static NotificationTarget Create(
        TeamId teamId,
        ResourceName name,
        NotificationChannel channel,
        SecretReferenceId? credentialsReferenceId,
        DateTimeOffset now)
    {
        return new NotificationTarget(NotificationTargetId.New(), teamId, name, channel, credentialsReferenceId,
            "{}", NotificationDeliveryPolicy.Default, now);
    }

    public void Configure(ResourceName name, SecretReferenceId? credentialsReferenceId, string configurationJson,
        NotificationDeliveryPolicy policy, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(configurationJson))
            configurationJson = "{}";

        Name = name;
        CredentialsReferenceId = credentialsReferenceId;
        ConfigurationJson = configurationJson;
        Policy = policy;
        IsEnabled = true;
        ResetDeliveryFailures();
        Touch(now);
    }

    /// <summary>
    ///     Replaces the event subscriptions. <c>null</c> restores the catalog defaults. Only known, subscribable
    ///     catalog keys are accepted; always-delivered events cannot be unsubscribed and are ignored.
    /// </summary>
    public void SetSubscriptions(IEnumerable<string>? eventKeys, DateTimeOffset now)
    {
        if (eventKeys is null)
        {
            SubscribedEvents = null;
            Touch(now);
            return;
        }

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in eventKeys)
        {
            var key = raw?.Trim();
            var definition = NotificationEventCatalog.Find(key);
            if (definition is null)
                throw new DomainException($"Unknown notification event '{Truncate(key ?? string.Empty, 80)}'.");
            if (!definition.AlwaysDeliver)
                keys.Add(definition.Key);
        }

        SubscribedEvents = keys.ToArray();
        ResetDeliveryFailures();
        Touch(now);
    }

    /// <summary>Sets per-event routes (for example Telegram topic ids). Keys must be catalog events.</summary>
    public void SetEventRoutes(IReadOnlyDictionary<string, string>? routes, DateTimeOffset now)
    {
        var normalized = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawValue) in routes ?? new Dictionary<string, string>())
        {
            var key = rawKey?.Trim() ?? string.Empty;
            if (!NotificationEventCatalog.IsKnown(key))
                throw new DomainException($"Unknown notification event '{Truncate(key, 80)}'.");

            var value = rawValue?.Trim();
            if (string.IsNullOrEmpty(value))
                continue;
            if (value.Length > MaxRouteLength || value.Any(char.IsControl))
                throw new DomainException($"Route for notification event '{key}' is not valid.");
            normalized[key] = value;
        }

        EventRoutesJson = JsonSerializer.Serialize(normalized);
        ResetDeliveryFailures();
        Touch(now);
    }

    public IReadOnlyDictionary<string, string> EventRoutes()
    {
        if (string.IsNullOrWhiteSpace(EventRoutesJson))
            return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(EventRoutesJson)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public string? RouteFor(string eventType)
    {
        return EventRoutes().TryGetValue(eventType, out var route) ? route : null;
    }

    /// <summary>
    ///     Whether this target receives <paramref name="eventType" />: always-delivered events always, other catalog events
    ///     when subscribed, and unknown event types never.
    /// </summary>
    public bool IsSubscribedTo(string eventType)
    {
        var definition = NotificationEventCatalog.Find(eventType);
        if (definition is null)
            return false;
        if (!definition.DeliverToEmail && Channel == NotificationChannel.Email)
            return false;

        return definition.AlwaysDeliver || EffectiveSubscriptions.Contains(definition.Key);
    }

    /// <summary>
    ///     Records a failed delivery. Only <see cref="NotificationFailureKind.Configuration" /> failures count; after
    ///     <see cref="AutoDisableThreshold" /> in a row the target is disabled. Returns <c>true</c> exactly when this call
    ///     disabled the target, so the caller can tell the team once.
    /// </summary>
    public bool RecordDeliveryFailure(NotificationFailureKind kind, string reason, DateTimeOffset now)
    {
        if (kind != NotificationFailureKind.Configuration)
            return false;

        ConsecutiveFailureCount++;
        var disabledNow = false;
        if (ConsecutiveFailureCount >= AutoDisableThreshold && IsEnabled)
        {
            IsEnabled = false;
            DisabledReason = Truncate(string.IsNullOrWhiteSpace(reason) ? "Delivery failed." : reason.Trim(), 512);
            AutoDisabledAt = now;
            disabledNow = true;
        }

        Touch(now);
        return disabledNow;
    }

    public void RecordDeliverySuccess(DateTimeOffset now)
    {
        if (ConsecutiveFailureCount == 0)
            return;

        ConsecutiveFailureCount = 0;
        Touch(now);
    }

    public void Disable(DateTimeOffset now)
    {
        IsEnabled = false;
        Touch(now);
    }

    public void Enable(DateTimeOffset now)
    {
        if (!IsEnabled)
            ResetDeliveryFailures();
        IsEnabled = true;
        Touch(now);
    }

    private void ResetDeliveryFailures()
    {
        ConsecutiveFailureCount = 0;
        DisabledReason = null;
        AutoDisabledAt = null;
    }

    private static string Truncate(string value, int length)
    {
        return value.Length <= length ? value : value[..length];
    }
}
