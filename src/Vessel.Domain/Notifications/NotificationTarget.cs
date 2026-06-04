using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Notifications;

public sealed class NotificationTarget : Entity<NotificationTargetId>
{
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
        Touch(now);
    }

    public void Disable(DateTimeOffset now)
    {
        IsEnabled = false;
        Touch(now);
    }
}
