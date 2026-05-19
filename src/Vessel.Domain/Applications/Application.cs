using Vessel.Domain.Common;
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
        BuildConfiguration = buildConfiguration;
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
        if (_domains.Any(domain => domain.DomainName == domainName)) return;

        _domains.Add(new ApplicationDomain(Id, domainName, now));
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
        BuildConfiguration = buildConfiguration;
        RuntimeConfiguration = runtimeConfiguration;
        DeploymentSettings = deploymentSettings;
        Touch(now);
    }
}
