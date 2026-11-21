using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Applications;

public sealed class Application : Entity<ApplicationId>
{
    private readonly List<ApplicationDomain> _domains = [];

    private Application()
    {
    }

    private Application(
        ApplicationId id,
        EnvironmentId environmentId,
        ServerId serverId,
        ResourceName name,
        GitSource gitSource,
        BuildConfiguration buildConfiguration,
        RuntimeConfiguration runtimeConfiguration,
        DeploymentSettings deploymentSettings,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        EnvironmentId = environmentId;
        ServerId = serverId;
        Name = name;
        GitSource = gitSource;
        BuildConfiguration = buildConfiguration.Validated();
        RuntimeConfiguration = runtimeConfiguration;
        DeploymentSettings = deploymentSettings;
    }

    public EnvironmentId EnvironmentId { get; private set; }

    public ServerId ServerId { get; private set; }

    public ResourceName Name { get; private set; }

    public Description? Description { get; private set; }

    public GitSource GitSource { get; private set; }

    public BuildConfiguration BuildConfiguration { get; private set; }

    public RuntimeConfiguration RuntimeConfiguration { get; private set; }

    public DeploymentSettings DeploymentSettings { get; private set; }

    public IReadOnlyCollection<ApplicationDomain> Domains => _domains.AsReadOnly();

    /// <summary>
    ///     Private Git source (GitHub App, GitLab/Gitea app, deploy key or HTTPS credentials) used to clone
    ///     <see cref="GitSource" />, or <c>null</c> for a public repository (DEP-08).
    /// </summary>
    public GitSourceConnectionId? SourceId { get; private set; }

    /// <summary>Selects the Git source used for clones; <c>null</c> clones the repository anonymously.</summary>
    public void AssignSource(GitSourceConnectionId? sourceId, DateTimeOffset now)
    {
        if (SourceId == sourceId) return;
        SourceId = sourceId;
        Touch(now);
    }

    public static Application Create(
        EnvironmentId environmentId,
        ServerId serverId,
        ResourceName name,
        GitSource gitSource,
        BuildConfiguration buildConfiguration,
        DateTimeOffset now)
    {
        var application = new Application(
            ApplicationId.New(),
            environmentId,
            serverId,
            name,
            gitSource,
            buildConfiguration,
            RuntimeConfiguration.Default,
            DeploymentSettings.Default,
            now);
        application.AddDomainEvent(new ApplicationCreatedEvent(application.Id, environmentId, serverId, now));

        return application;
    }

    public void AddDomain(DomainName domainName, DateTimeOffset now)
    {
        AddDomain(RouteAddress.FromDomainName(domainName), now);
    }

    public void AddDomain(RouteAddress address, DateTimeOffset now)
    {
        if (_domains.Any(domain => domain.Is(address))) return;

        var domain = new ApplicationDomain(Id, address, now);
        domain.UpdateRouting(address.Port, address.Scheme != RouteScheme.Http &&
                                           address.HostKind is not (RouteHostKind.IPv4 or RouteHostKind.IPv6),
            false, false);
        _domains.Add(domain);
        Touch(now);
    }

    public void UpsertDomain(
        DomainName domainName,
        int? targetPort,
        bool tlsEnabled,
        bool canonical,
        bool redirectToCanonical,
        DateTimeOffset now)
    {
        UpsertDomain(RouteAddress.FromDomainName(domainName), targetPort, tlsEnabled, canonical, redirectToCanonical,
            null, now);
    }

    /// <summary>
    ///     Adds or updates the route for <paramref name="address" /> (identity: host + path). A <see langword="null" />
    ///     <paramref name="policy" /> keeps the current policy (or the default for a new route).
    /// </summary>
    public ApplicationDomain UpsertDomain(
        RouteAddress address,
        int? targetPort,
        bool tlsEnabled,
        bool canonical,
        bool redirectToCanonical,
        RoutePolicy? policy,
        DateTimeOffset now)
    {
        if (canonical && redirectToCanonical)
            throw new DomainException("Canonical domain cannot redirect to itself.");

        ApplicationDomain? existing = _domains.SingleOrDefault(domain => domain.Is(address));
        var created = existing is null;
        existing ??= new ApplicationDomain(Id, address, now);
        existing.UpdateRouting(targetPort, tlsEnabled, canonical, redirectToCanonical);
        if (policy is not null) existing.UpdatePolicy(policy);
        if (created) _domains.Add(existing);

        if (canonical)
            foreach (ApplicationDomain domain in _domains.Where(domain => !domain.Is(address)))
                domain.UpdateRouting(domain.TargetPort, domain.TlsEnabled, false, domain.RedirectToCanonical);

        Touch(now);
        return existing;
    }

    public void RemoveDomain(DomainName domainName, DateTimeOffset now)
    {
        RemoveDomain(RouteAddress.FromDomainName(domainName), now);
    }

    public bool RemoveDomain(RouteAddress address, DateTimeOffset now)
    {
        ApplicationDomain? existing = _domains.SingleOrDefault(domain => domain.Is(address));
        if (existing is null) return false;
        _domains.Remove(existing);
        Touch(now);
        return true;
    }

    /// <summary>Replaces the build configuration (validated: build pack, relative paths, registry image).</summary>
    public void UpdateBuildConfiguration(BuildConfiguration buildConfiguration, DateTimeOffset now)
    {
        BuildConfiguration = buildConfiguration.Validated();
        Touch(now);
    }

    public void UpdateDeploymentSettings(DeploymentSettings deploymentSettings, DateTimeOffset now)
    {
        DeploymentSettings = deploymentSettings.Validated();
        Touch(now);
    }

    public void UpdateSettings(
        ResourceName name,
        Description? description,
        GitSource gitSource,
        BuildConfiguration buildConfiguration,
        RuntimeConfiguration runtimeConfiguration,
        DeploymentSettings deploymentSettings,
        DateTimeOffset now)
    {
        Name = name;
        Description = description;
        GitSource = gitSource;
        BuildConfiguration = buildConfiguration.Validated();
        RuntimeConfiguration = runtimeConfiguration;
        DeploymentSettings = deploymentSettings;
        Touch(now);
    }
}
