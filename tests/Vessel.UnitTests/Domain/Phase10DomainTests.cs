using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Certificates;
using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.ValueObjects;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.UnitTests.Domain;

public sealed class Phase10DomainTests
{
    [Fact]
    public void Application_RejectsCanonicalSelfRedirect()
    {
        var application = AppEntity.Create(
            EnvironmentId.New(),
            ServerId.New(),
            new ResourceName("web"),
            new GitSource(new RepositoryUrl("https://example.com/repo.git"), "main"),
            BuildConfiguration.Default(ApplicationBuildPack.Dockerfile),
            DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => application.UpsertDomain(
            new DomainName("app.example.com"),
            8080,
            true,
            true,
            true,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Certificate_RejectsRenewalQueueBeforeIssue()
    {
        var certificate = Certificate.Create(
            TeamId.New(),
            AppId.New(),
            "app.example.com",
            CertificateProvider.TraefikAcme,
            DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => certificate.QueueRenewal(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ProxyConfigurationVersion_RejectsInvalidStateTransitions()
    {
        var version = ProxyConfigurationVersion.Create(
            ServerId.New(),
            ProxyProviderKind.Traefik,
            "v1",
            new string('a', 64),
            "http:\n  routers: {}\n  services: {}\n",
            null,
            DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => version.MarkApplied(DateTimeOffset.UtcNow));
        Assert.Throws<DomainException>(() => version.MarkRolledBack(DateTimeOffset.UtcNow));
    }
}
