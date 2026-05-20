using Vessel.Domain.Common;

namespace Vessel.Domain.Proxy;

public sealed class ProxyConfigurationVersion : Entity<ProxyConfigurationVersionId>
{
    private ProxyConfigurationVersion()
    {
    }

    private ProxyConfigurationVersion(
        ProxyConfigurationVersionId id,
        ServerId serverId,
        ProxyProviderKind provider,
        string version,
        string configurationHash,
        string configuration,
        ProxyConfigurationVersionId? previousVersionId,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        ServerId = serverId;
        Provider = provider;
        Version = DomainValidation.Required(version, nameof(Version), 80);
        ConfigurationHash = DomainValidation.Required(configurationHash, nameof(ConfigurationHash), 128);
        Configuration = DomainValidation.Required(configuration, nameof(Configuration), 200_000);
        PreviousVersionId = previousVersionId;
        Status = ProxyConfigurationStatus.Generated;
    }

    public ServerId ServerId { get; private set; }

    public ProxyProviderKind Provider { get; private set; }

    public string Version { get; private set; } = string.Empty;

    public string ConfigurationHash { get; private set; } = string.Empty;

    public string Configuration { get; private set; } = string.Empty;

    public ProxyConfigurationVersionId? PreviousVersionId { get; private set; }

    public ProxyConfigurationStatus Status { get; private set; }

    public string? ValidationError { get; private set; }

    public string? ApplyError { get; private set; }

    public DateTimeOffset? ValidatedAt { get; private set; }

    public DateTimeOffset? AppliedAt { get; private set; }

    public DateTimeOffset? RolledBackAt { get; private set; }

    public static ProxyConfigurationVersion Create(
        ServerId serverId,
        ProxyProviderKind provider,
        string version,
        string configurationHash,
        string configuration,
        ProxyConfigurationVersionId? previousVersionId,
        DateTimeOffset now)
    {
        return new ProxyConfigurationVersion(ProxyConfigurationVersionId.New(), serverId, provider, version,
            configurationHash, configuration, previousVersionId, now);
    }

    public void MarkValidated(DateTimeOffset now)
    {
        Status = ProxyConfigurationStatus.Validated;
        ValidationError = null;
        ValidatedAt = now;
        Touch(now);
    }

    public void MarkValidationFailed(string error, DateTimeOffset now)
    {
        Status = ProxyConfigurationStatus.Failed;
        ValidationError = DomainValidation.Required(error, nameof(error), 2000);
        Touch(now);
    }

    public void MarkApplied(DateTimeOffset now)
    {
        Status = ProxyConfigurationStatus.Applied;
        ApplyError = null;
        AppliedAt = now;
        Touch(now);
    }

    public void MarkApplyFailed(string error, DateTimeOffset now)
    {
        Status = ProxyConfigurationStatus.Failed;
        ApplyError = DomainValidation.Required(error, nameof(error), 2000);
        Touch(now);
    }

    public void MarkRolledBack(DateTimeOffset now)
    {
        Status = ProxyConfigurationStatus.RolledBack;
        RolledBackAt = now;
        Touch(now);
    }
}
