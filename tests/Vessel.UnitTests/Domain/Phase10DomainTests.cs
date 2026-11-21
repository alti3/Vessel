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
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Application_RejectsCanonicalSelfRedirect()
    {
        AppEntity application = CreateApplication();

        Assert.Throws<DomainException>(() => application.UpsertDomain(
            new DomainName("app.example.com"),
            8080,
            true,
            true,
            true,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Application_RoutesWithSameHostButDifferentPathsAreDistinct()
    {
        AppEntity application = CreateApplication();

        application.UpsertDomain(RouteAddress.Parse("https://app.example.com"), 8080, true, false, false, null, Now);
        application.UpsertDomain(RouteAddress.Parse("https://app.example.com/Api"), 9090, true, false, false, null,
            Now);
        application.UpsertDomain(RouteAddress.Parse("http://app.example.com/Api/"), 9091, false, false, false, null,
            Now);

        Assert.Equal(2, application.Domains.Count);
        ApplicationDomain api = application.Domains.Single(domain => domain.PathPrefix == "/Api");
        Assert.Equal(9091, api.TargetPort);
        Assert.False(api.TlsEnabled);
        Assert.Equal(RoutePolicy.Default, api.Policy);
    }

    [Fact]
    public void Application_RejectsTlsForIpAddressHosts()
    {
        AppEntity application = CreateApplication();

        Assert.Throws<DomainException>(() =>
            application.UpsertDomain(RouteAddress.Parse("10.0.0.5"), 8080, true, false, false, null, Now));
        Assert.Empty(application.Domains);
        application.UpsertDomain(RouteAddress.Parse("10.0.0.5"), 8080, false, false, false, null, Now);
        Assert.Equal(RouteHostKind.IPv4, Assert.Single(application.Domains).HostKind);
    }

    [Fact]
    public void Certificate_SyncFromAcmeTracksExpiryAndExpiringSoonAlertOncePerExpiry()
    {
        var certificate = Certificate.CreatePending(TeamId.New(), ServerId.New(), AppId.New(), "App.Example.com",
            Now);
        Assert.Equal("app.example.com", certificate.Host);
        Assert.Equal(CertificateStatus.Pending, certificate.Status);

        certificate.SyncFromAcme(new string('a', 64), "letsencrypt", "app.example.com",
            ["www.example.com", "app.example.com"], Now.AddDays(-80), Now.AddDays(5), true, null, Now);

        Assert.Equal(CertificateStatus.Issued, certificate.Status);
        Assert.Equal(["www.example.com"], certificate.SubjectAlternativeNameList);
        Assert.True(certificate.NeedsExpiryAlert(Now, TimeSpan.FromDays(14)));
        certificate.MarkExpiryAlertSent(Now);
        Assert.False(certificate.NeedsExpiryAlert(Now, TimeSpan.FromDays(14)));

        // Renewed by Traefik: a new expiry re-arms the alert, but it is not due yet.
        certificate.SyncFromAcme(new string('b', 64), "letsencrypt", "app.example.com", [], Now, Now.AddDays(90),
            true, null, Now);
        Assert.False(certificate.NeedsExpiryAlert(Now, TimeSpan.FromDays(14)));

        certificate.SyncFromAcme(new string('b', 64), "letsencrypt", "app.example.com", [], Now.AddDays(-91),
            Now.AddDays(-1), true, null, Now);
        Assert.Equal(CertificateStatus.Expired, certificate.Status);
    }

    [Fact]
    public void Certificate_UnusedCertificateNeverAlerts()
    {
        var certificate = Certificate.CreatePending(TeamId.New(), ServerId.New(), null, "old.example.com", Now);
        certificate.SyncFromAcme(new string('c', 64), "letsencrypt", "old.example.com", [], Now.AddDays(-85),
            Now.AddDays(3), false, null, Now);

        Assert.False(certificate.NeedsExpiryAlert(Now, TimeSpan.FromDays(14)));
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
        Assert.Equal(ProxyConfigurationSource.Generated, version.Source);
    }

    [Fact]
    public void ProxyConfigurationVersion_RestoreAndUserDynamicRequireTheirLinks()
    {
        Assert.Throws<DomainException>(() => ProxyConfigurationVersion.Create(ServerId.New(),
            ProxyProviderKind.Traefik, "v1", "hash", "content", null, Now, ProxyConfigurationSource.Restore));
        Assert.Throws<DomainException>(() => ProxyConfigurationVersion.Create(ServerId.New(),
            ProxyProviderKind.Traefik, "v1", "hash", "content", null, Now, ProxyConfigurationSource.UserDynamic));

        var source = ProxyConfigurationVersionId.New();
        var restored = ProxyConfigurationVersion.Create(ServerId.New(), ProxyProviderKind.Traefik, "v1", "hash",
            "content", null, Now, ProxyConfigurationSource.Restore, restoredFromId: source);
        Assert.Equal(source, restored.RestoredFromId);
    }

    [Theory]
    [InlineData("middlewares.yml")]
    [InlineData("my_routes-2.yaml")]
    public void ProxyDynamicConfiguration_AcceptsPlainYamlFileNames(string fileName)
    {
        Assert.Equal(fileName, ProxyDynamicConfiguration.ValidateFileName(fileName));
    }

    [Theory]
    [InlineData("../escape.yml")]
    [InlineData("dir/file.yml")]
    [InlineData("dir\\file.yml")]
    [InlineData(".hidden.yml")]
    [InlineData("vessel-routes.yml")]
    [InlineData("Vessel.yaml")]
    [InlineData("routes.json")]
    [InlineData("rou tes.yml")]
    [InlineData("")]
    public void ProxyDynamicConfiguration_RejectsUnsafeOrReservedFileNames(string fileName)
    {
        Assert.Throws<DomainException>(() => ProxyDynamicConfiguration.ValidateFileName(fileName));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("ops.team@example")]
    public void RouteBasicAuth_AcceptsSafeUsernames(string username)
    {
        Assert.Equal(username, new RouteBasicAuth(username, SecretReferenceId.New()).Username);
    }

    [Theory]
    [InlineData("user:name")]
    [InlineData("user name")]
    [InlineData("")]
    public void RouteBasicAuth_RejectsUsernamesThatWouldCorruptHtpasswd(string username)
    {
        Assert.Throws<DomainException>(() => new RouteBasicAuth(username, SecretReferenceId.New()));
    }

    private static AppEntity CreateApplication()
    {
        return AppEntity.Create(
            EnvironmentId.New(),
            ServerId.New(),
            new ResourceName("web"),
            new GitSource(new RepositoryUrl("https://example.com/repo.git"), "main"),
            BuildConfiguration.Default(ApplicationBuildPack.Dockerfile),
            DateTimeOffset.UtcNow);
    }
}
