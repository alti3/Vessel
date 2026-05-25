using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Services;

public sealed class ServiceResource : Entity<ServiceResourceId>
{
    private ServiceResource()
    {
    }

    private ServiceResource(
        ServiceResourceId id,
        TeamId teamId,
        EnvironmentId environmentId,
        ServerId serverId,
        ResourceName name,
        string templateKey,
        string templateVersion,
        string configurationJson,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        EnvironmentId = environmentId;
        ServerId = serverId;
        Name = name;
        TemplateKey = DomainValidation.Required(templateKey, nameof(templateKey), 120);
        TemplateVersion = DomainValidation.Required(templateVersion, nameof(templateVersion), 80);
        ConfigurationJson = DomainValidation.Required(configurationJson, nameof(configurationJson), 12000);
    }

    public TeamId TeamId { get; private set; }

    public EnvironmentId EnvironmentId { get; private set; }

    public ServerId ServerId { get; private set; }

    public ResourceName Name { get; private set; }

    public string TemplateKey { get; private set; } = string.Empty;

    public string TemplateVersion { get; private set; } = string.Empty;

    public string ConfigurationJson { get; private set; } = "{}";

    public DatabaseLifecycleState State { get; private set; } = DatabaseLifecycleState.NotProvisioned;

    public string? ComposeSnapshotReference { get; private set; }

    public static ServiceResource Create(
        TeamId teamId,
        EnvironmentId environmentId,
        ServerId serverId,
        ResourceName name,
        string templateKey,
        string templateVersion,
        string configurationJson,
        DateTimeOffset now)
    {
        return new ServiceResource(ServiceResourceId.New(), teamId, environmentId, serverId, name, templateKey,
            templateVersion, configurationJson, now);
    }

    public void MarkProvisioning(DateTimeOffset now)
    {
        State = DatabaseLifecycleState.Provisioning;
        Touch(now);
    }

    public void MarkRunning(string composeSnapshotReference, DateTimeOffset now)
    {
        ComposeSnapshotReference = DomainValidation.Required(composeSnapshotReference, nameof(composeSnapshotReference),
            512);
        State = DatabaseLifecycleState.Running;
        Touch(now);
    }

    public void MarkFailed(DateTimeOffset now)
    {
        State = DatabaseLifecycleState.Failed;
        Touch(now);
    }
}
