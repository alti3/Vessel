using Vessel.Domain.Common;

namespace Vessel.Domain.Proxy;

/// <summary>
///     One version of a proxy configuration file on a server. <see cref="Configuration" /> holds the full file text
///     encrypted with the secret key ring (it can contain basic-auth hashes and backend topology); legacy rows may hold
///     plaintext. History is bounded by the cleanup job (newest versions plus the applied ones are kept), and restoring an
///     older version creates a new version linked through <see cref="RestoredFromId" /> instead of rewriting history.
/// </summary>
public sealed class ProxyConfigurationVersion : Entity<ProxyConfigurationVersionId>
{
    public const int MaxConfigurationLength = 400_000;
    public const int MaxFileNameLength = 128;

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
        ProxyConfigurationSource source,
        string? fileName,
        ProxyConfigurationVersionId? restoredFromId,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        ServerId = serverId;
        Provider = provider;
        Version = DomainValidation.Required(version, nameof(Version), 80);
        ConfigurationHash = DomainValidation.Required(configurationHash, nameof(ConfigurationHash), 128);
        Configuration = DomainValidation.Required(configuration, nameof(Configuration), MaxConfigurationLength);
        PreviousVersionId = previousVersionId;
        Source = source;
        FileName = DomainValidation.Optional(fileName, nameof(FileName), MaxFileNameLength);
        if (source == ProxyConfigurationSource.UserDynamic && FileName is null)
            throw new DomainException("User dynamic configuration versions require a file name.");
        if (source == ProxyConfigurationSource.Restore && restoredFromId is null)
            throw new DomainException("Restored versions must reference the version they restore.");
        RestoredFromId = restoredFromId;
        Status = ProxyConfigurationStatus.Generated;
    }

    public ServerId ServerId { get; private set; }

    public ProxyProviderKind Provider { get; private set; }

    public string Version { get; private set; } = string.Empty;

    public string ConfigurationHash { get; private set; } = string.Empty;

    /// <summary>Protected (encrypted) configuration text; see the Application-layer protector.</summary>
    public string Configuration { get; private set; } = string.Empty;

    public ProxyConfigurationVersionId? PreviousVersionId { get; private set; }

    public ProxyConfigurationStatus Status { get; private set; }

    public ProxyConfigurationSource Source { get; private set; }

    /// <summary>File name for user dynamic configurations; <see langword="null" /> for the generated routes file.</summary>
    public string? FileName { get; private set; }

    /// <summary>The version this one restores (only for <see cref="ProxyConfigurationSource.Restore" />).</summary>
    public ProxyConfigurationVersionId? RestoredFromId { get; private set; }

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
        DateTimeOffset now,
        ProxyConfigurationSource source = ProxyConfigurationSource.Generated,
        string? fileName = null,
        ProxyConfigurationVersionId? restoredFromId = null)
    {
        return new ProxyConfigurationVersion(ProxyConfigurationVersionId.New(), serverId, provider, version,
            configurationHash, configuration, previousVersionId, source, fileName, restoredFromId, now);
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
        if (Status != ProxyConfigurationStatus.Validated)
            throw new DomainException("Only validated proxy configurations can be applied.");

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
}
