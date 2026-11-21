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

    /// <summary>
    ///     Vault reference holding the values generated for the template's magic variables (TPL-01:
    ///     <c>SERVICE_PASSWORD_*</c>, <c>SERVICE_USER_*</c>, ...) as a JSON object. Generated once per service instance and
    ///     never regenerated on re-provision; never exposed through APIs.
    /// </summary>
    public SecretReferenceId? GeneratedSecretsReferenceId { get; private set; }

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

    public void AttachGeneratedSecrets(SecretReferenceId secretReferenceId, DateTimeOffset now)
    {
        if (GeneratedSecretsReferenceId.HasValue && GeneratedSecretsReferenceId.Value != secretReferenceId)
            throw new DomainException("Generated template secrets are already attached to this service.");
        GeneratedSecretsReferenceId = secretReferenceId;
        Touch(now);
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
