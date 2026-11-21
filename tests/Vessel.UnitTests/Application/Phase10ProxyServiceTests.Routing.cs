using System.Text;
using Vessel.Application.Auditing;
using Vessel.Application.Notifications;
using Vessel.Application.Proxy;
using Vessel.Application.Proxy.Certificates;
using Vessel.Application.Proxy.Domains;
using Vessel.Application.Security;
using Vessel.Application.Servers;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Certificates;
using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.Secrets;
using Vessel.Domain.Servers;
using Vessel.Domain.Services;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;

namespace Vessel.UnitTests.Application;

/// <summary>NET-03..NET-07: route identity and conflicts, policies, endpoints, certificate truth, history.</summary>
public sealed partial class Phase10ProxyServiceTests
{
    // ---- NET-03: identity, validation, conflicts -------------------------------------------------------------------

    [Fact]
    public async Task ConfigureDomain_AcceptsPathWildcardIpAndWebSocketForms()
    {
        var fixture = ServiceFixture.Create();

        DomainRouteSummary path = await Configure(fixture, "https://app.example.com/Api/");
        DomainRouteSummary wildcard = await Configure(fixture, "*.preview.example.com");
        DomainRouteSummary ip = await Configure(fixture, "http://10.0.0.5:3000", tls: false);
        DomainRouteSummary socket = await Configure(fixture, "wss://ws.example.com");

        Assert.Equal("/Api", path.PathPrefix);
        Assert.Equal("https://app.example.com/Api", path.Url);
        Assert.Equal(RouteHostKind.Wildcard, wildcard.HostKind);
        Assert.Equal(RouteHostKind.IPv4, ip.HostKind);
        Assert.Equal(3000, ip.TargetPort);
        Assert.False(ip.TlsEnabled);
        Assert.True(socket.TlsEnabled);
        // TLS hosts get an expected certificate; the IP host does not.
        Assert.DoesNotContain(fixture.Db.CertificateItems, certificate => certificate.Host == "10.0.0.5");
        Assert.All(fixture.Db.CertificateItems, certificate => Assert.Equal(fixture.Server.Id, certificate.ServerId));
    }

    [Fact]
    public async Task ConfigureDomain_RejectsConflictAcrossApplicationsAndSchemes()
    {
        var fixture = ServiceFixture.Create();
        AppEntity other = fixture.AddApplication("Other App", fixture.Server.Id);
        other.UpsertDomain(RouteAddress.Parse("shop.example.com/store"), null, true, false, false, null, fixture.Now);

        DomainException conflict = await Assert.ThrowsAsync<DomainException>(() =>
            Configure(fixture, "http://SHOP.example.com/store/", tls: false));

        Assert.Contains("Other App", conflict.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Application.Domains, domain => domain.Host == "shop.example.com");
        // A different path on the same host is a different route identity.
        await Configure(fixture, "https://shop.example.com/admin");
    }

    [Fact]
    public async Task ConfigureDomain_ConflictWithAnotherTeamDoesNotRevealItsName()
    {
        var fixture = ServiceFixture.Create();
        var otherUser = User.Create(new DisplayName("Other"), new EmailAddress("other@example.com"), fixture.Now);
        var otherTeam = Team.Create(new DisplayName("Other Team"), otherUser.Id, false, fixture.Now);
        fixture.Db.UserItems.Add(otherUser);
        fixture.Db.TeamItems.Add(otherTeam);
        AppEntity secret = fixture.AddApplication("Secret Project", fixture.Server.Id, otherTeam.Id);
        secret.UpsertDomain(RouteAddress.Parse("taken.example.com"), null, true, false, false, null, fixture.Now);

        DomainException conflict = await Assert.ThrowsAsync<DomainException>(() =>
            Configure(fixture, "taken.example.com"));

        Assert.DoesNotContain("Secret Project", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("another team", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigureDomain_ForceOverrideSavesAndIsAudited()
    {
        var fixture = ServiceFixture.Create();
        AppEntity other = fixture.AddApplication("Other App", fixture.Server.Id);
        other.UpsertDomain(RouteAddress.Parse("shared.example.com"), null, true, false, false, null, fixture.Now);

        await fixture.Domains.ConfigureAsync(fixture.UserId, fixture.TeamId, fixture.Application.Id,
            new ConfigureDomainRouteRequest("shared.example.com", null, true, false, false,
                ForceDomainOverride: true), TestContext.Current.CancellationToken);

        Assert.Contains(fixture.Application.Domains, domain => domain.Host == "shared.example.com");
        AuditRecord audit = Assert.Single(fixture.Audit.Records,
            record => record.Action == AuditActions.DomainConflictOverridden);
        Assert.Equal("shared.example.com", audit.Metadata["identity"]);
    }

    [Fact]
    public async Task ConfigureDomain_ConflictsWithServiceDomains()
    {
        var fixture = ServiceFixture.Create();
        ServiceResource service = fixture.AddService("pgadmin");
        fixture.Db.ServiceDomainItems.Add(ServiceDomain.Create(service.Id, RouteAddress.Parse("db.example.com"),
            "pgadmin", 80, true, null, fixture.Now));

        await Assert.ThrowsAsync<DomainException>(() => Configure(fixture, "https://db.example.com"));
    }

    [Fact]
    public async Task RemoveDomain_DropsPendingCertificateForUnroutedHost()
    {
        var fixture = ServiceFixture.Create();
        await Configure(fixture, "https://gone.example.com");
        Assert.Single(fixture.Db.CertificateItems);

        await fixture.Domains.RemoveAsync(fixture.UserId, fixture.TeamId, fixture.Application.Id,
            "https://gone.example.com", TestContext.Current.CancellationToken);

        Assert.Empty(fixture.Application.Domains);
        Assert.Empty(fixture.Db.CertificateItems);
    }

    // ---- NET-05: policies --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConfigureDomain_BasicAuthPasswordIsWriteOnly()
    {
        var fixture = ServiceFixture.Create();

        DomainRouteSummary summary = await fixture.Domains.ConfigureAsync(fixture.UserId, fixture.TeamId,
            fixture.Application.Id,
            new ConfigureDomainRouteRequest("https://admin.example.com", null, true, false, false,
                new RoutePolicyRequest(BasicAuthUsername: "ops", BasicAuthPassword: "correct-horse-battery",
                    NoIndex: true, PermanentRedirect: false)),
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary.Policy);
        Assert.True(summary.Policy!.BasicAuthEnabled);
        Assert.Equal("ops", summary.Policy.BasicAuthUsername);
        Assert.True(summary.Policy.NoIndex);
        var stored = Assert.Single(fixture.Vault.Values);
        Assert.Equal("correct-horse-battery", stored.Value);
        RouteBasicAuth basicAuth = fixture.Application.Domains.Single().Policy.BasicAuth!;
        Assert.Equal(stored.Key, basicAuth.PasswordSecretReferenceId);
        Assert.DoesNotContain(fixture.Audit.Records, record => record.Metadata.Values
            .Any(value => value?.ToString()?.Contains("correct-horse", StringComparison.Ordinal) == true));

        // Updating without a password keeps the stored secret; a new password replaces it in place.
        await fixture.Domains.ConfigureAsync(fixture.UserId, fixture.TeamId, fixture.Application.Id,
            new ConfigureDomainRouteRequest("https://admin.example.com", null, true, false, false,
                new RoutePolicyRequest(Compression: false)), TestContext.Current.CancellationToken);
        Assert.Equal(basicAuth, fixture.Application.Domains.Single().Policy.BasicAuth);
        await fixture.Domains.ConfigureAsync(fixture.UserId, fixture.TeamId, fixture.Application.Id,
            new ConfigureDomainRouteRequest("https://admin.example.com", null, true, false, false,
                new RoutePolicyRequest(BasicAuthPassword: "another-long-password")),
            TestContext.Current.CancellationToken);
        Assert.Equal("another-long-password", Assert.Single(fixture.Vault.Values).Value);
    }

    [Theory]
    [InlineData("ops", null)]
    [InlineData(null, "long-enough-password")]
    [InlineData("ops", "short")]
    public async Task ConfigureDomain_BasicAuthRequiresUsernameAndStrongPassword(string? username, string? password)
    {
        var fixture = ServiceFixture.Create();

        await Assert.ThrowsAsync<DomainException>(() => fixture.Domains.ConfigureAsync(fixture.UserId,
            fixture.TeamId, fixture.Application.Id,
            new ConfigureDomainRouteRequest("https://admin.example.com", null, true, false, false,
                new RoutePolicyRequest(BasicAuthEnabled: true, BasicAuthUsername: username,
                    BasicAuthPassword: password)),
            TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Vault.Values);
    }

    [Fact]
    public async Task ApplyProxyConfiguration_ResolvesBasicAuthCredentialsAndQuarantinesUnreadableSecrets()
    {
        var fixture = ServiceFixture.Create();
        await fixture.Domains.ConfigureAsync(fixture.UserId, fixture.TeamId, fixture.Application.Id,
            new ConfigureDomainRouteRequest("https://admin.example.com", null, true, false, false,
                new RoutePolicyRequest(BasicAuthUsername: "ops", BasicAuthPassword: "correct-horse-battery")),
            TestContext.Current.CancellationToken);
        await Configure(fixture, "https://open.example.com");

        await fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId,
            fixture.Server.Id, TestContext.Current.CancellationToken);
        ProxyRoute protectedRoute = fixture.ProxyProvider.GeneratedRoutes.Single(route =>
            route.Host == "admin.example.com");
        Assert.Equal("correct-horse-battery", protectedRoute.BasicAuth!.Password);
        Assert.DoesNotContain("correct-horse", protectedRoute.BasicAuth.ToString(), StringComparison.Ordinal);

        fixture.Vault.FailReveal = true;
        fixture.ProxyProvider.GeneratedHash = new string('c', 64);
        await fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId,
            fixture.Server.Id, TestContext.Current.CancellationToken);
        // The protected route is never published without its credentials; the rest of the server keeps routing.
        Assert.Equal(["open.example.com"], fixture.ProxyProvider.GeneratedRoutes.Select(route => route.Host));
    }

    // ---- NET-06: routable endpoints -----------------------------------------------------------------------------------

    [Fact]
    public async Task RoutesForServer_ProjectsApplicationsPreviewsAndServiceComponents()
    {
        var fixture = ServiceFixture.Create();
        await Configure(fixture, "https://app.example.com/docs");
        var preview = ApplicationPreview.Open(fixture.Application.Id, WebhookProvider.GitHub, 7, "feature", "main",
            new string('a', 40), null, null, fixture.Now);
        preview.SetPreviewUrl("https://7.app.example.com/docs", fixture.Now);
        fixture.Db.PreviewItems.Add(preview);
        ServiceResource service = fixture.AddService("pgadmin");
        ServiceDomainSummary serviceDomain = await fixture.ServiceDomains.ConfigureAsync(fixture.UserId,
            fixture.TeamId, service.Id, new ConfigureServiceDomainRequest("https://pg.example.com", null, null),
            TestContext.Current.CancellationToken);

        IReadOnlyList<ProxyRoute> routes = fixture.Projector.RoutesForServer(fixture.Server.Id);

        Assert.Equal("pgadmin", serviceDomain.ComposeService);
        Assert.Equal(80, serviceDomain.TargetPort);
        ProxyRoute app = routes.Single(route => route.Endpoint.OwnerKind == RouteOwnerKind.Application);
        Assert.Equal("/docs", app.PathPrefix);
        Assert.Equal(3000, app.TargetPort);
        Assert.Equal(ProxyNaming.UpstreamAlias(fixture.Application.Id), app.UpstreamHost);
        ProxyRoute previewRoute = routes.Single(route => route.Endpoint.OwnerKind == RouteOwnerKind.Preview);
        Assert.Equal("7.app.example.com", previewRoute.Host);
        Assert.Equal("/docs", previewRoute.PathPrefix);
        Assert.Equal(ProxyNaming.PreviewUpstreamAlias(fixture.Application.Id, 7), previewRoute.UpstreamHost);
        ProxyRoute serviceRoute = routes.Single(route => route.Endpoint.OwnerKind == RouteOwnerKind.Service);
        Assert.Equal(80, serviceRoute.TargetPort);
        Assert.Equal("pgadmin", serviceRoute.Endpoint.Component);
        Assert.Equal(ProxyNaming.ServiceUpstreamAlias(service.Id, "pgadmin"), serviceRoute.UpstreamHost);
    }

    [Fact]
    public async Task ServiceDomain_RequiresPortForUnknownTemplatesAndRejectsCrossTeamAccess()
    {
        var fixture = ServiceFixture.Create();
        ServiceResource service = fixture.AddService("custom-template");

        await Assert.ThrowsAsync<DomainException>(() => fixture.ServiceDomains.ConfigureAsync(fixture.UserId,
            fixture.TeamId, service.Id, new ConfigureServiceDomainRequest("svc.example.com", "web", null),
            TestContext.Current.CancellationToken));

        var otherUser = User.Create(new DisplayName("Other"), new EmailAddress("other@example.com"), fixture.Now);
        var otherTeam = Team.Create(new DisplayName("Other Team"), otherUser.Id, false, fixture.Now);
        fixture.Db.UserItems.Add(otherUser);
        fixture.Db.TeamItems.Add(otherTeam);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ServiceDomains.ConfigureAsync(
            otherUser.Id, otherTeam.Id, service.Id, new ConfigureServiceDomainRequest("svc.example.com", "web", 8080),
            TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Db.ServiceDomainItems);
    }

    // ---- NET-07: history, encryption, restore, user dynamic configs -------------------------------------------------

    [Fact]
    public async Task ApplyProxyConfiguration_StoresConfigurationEncryptedAndSkipsUnchangedVersions()
    {
        var fixture = ServiceFixture.Create();

        await fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId,
            fixture.Server.Id, TestContext.Current.CancellationToken);
        await fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId,
            fixture.Server.Id, TestContext.Current.CancellationToken);

        ProxyConfigurationVersion version = Assert.Single(fixture.Db.ProxyConfigurationVersionItems);
        Assert.StartsWith(TestSecretCipher.Prefix, version.Configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("routers", version.Configuration, StringComparison.Ordinal);
        ProxyConfigurationContent content = await fixture.ProxyConfigurations.GetVersionContentAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, version.Id, TestContext.Current.CancellationToken);
        Assert.Contains("routers", content.Configuration, StringComparison.Ordinal);
        Assert.Contains(fixture.Audit.Records, record => record.Action == AuditActions.ProxyConfigurationRevealed);
    }

    [Fact]
    public async Task RestoreProxyConfiguration_CreatesANewVersionAndKeepsHistory()
    {
        var fixture = ServiceFixture.Create();
        await fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId,
            fixture.Server.Id, TestContext.Current.CancellationToken);
        ProxyConfigurationVersion first = Assert.Single(fixture.Db.ProxyConfigurationVersionItems);
        fixture.ProxyProvider.GeneratedContents = "http:\n  routers: {second: {}}\n  services: {}\n";
        fixture.ProxyProvider.GeneratedHash = new string('d', 64);
        await fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId,
            fixture.Server.Id, TestContext.Current.CancellationToken);

        ProxyConfigurationSummary restored = await fixture.ProxyConfigurations.RestoreAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, first.Id, TestContext.Current.CancellationToken);

        Assert.Equal(3, fixture.Db.ProxyConfigurationVersionItems.Count);
        Assert.Equal(ProxyConfigurationSource.Restore, restored.Source);
        Assert.Equal(first.Id.Value, restored.RestoredFromId);
        Assert.Equal(ProxyConfigurationStatus.Applied, restored.Status);
        Assert.Equal(ProxyConfigurationStatus.Applied, first.Status);
        Assert.Equal("http:\n  routers: {}\n  services: {}\n",
            fixture.Files.Contents[ProxyLayout.DynamicConfigurationFile]);
        Assert.Contains(fixture.Audit.Records, record => record.Action == AuditActions.ProxyConfigurationRestored);
    }

    [Fact]
    public async Task PruneHistory_KeepsNewestVersionsAndTheAppliedOnes()
    {
        var fixture = ServiceFixture.Create();
        ProxyConfigurationVersion applied = AddVersion(fixture, fixture.Now.AddDays(-30), applied: true);
        ProxyConfigurationVersion userFile = AddVersion(fixture, fixture.Now.AddDays(-29), applied: true,
            fileName: "custom.yml");
        for (var index = 0; index < 25; index++)
            AddVersion(fixture, fixture.Now.AddMinutes(-index), applied: false);

        var removed = await fixture.ProxyConfigurations.PruneHistoryAsync(20, TestContext.Current.CancellationToken);

        Assert.Equal(5, removed);
        Assert.Equal(22, fixture.Db.ProxyConfigurationVersionItems.Count);
        Assert.Contains(applied, fixture.Db.ProxyConfigurationVersionItems);
        Assert.Contains(userFile, fixture.Db.ProxyConfigurationVersionItems);
    }

    [Fact]
    public async Task UserDynamicConfiguration_ValidatesDeliversEncryptsAndRemoves()
    {
        var fixture = ServiceFixture.Create();
        const string content = "http:\n  middlewares:\n    headers:\n      headers: {}\n";

        ProxyDynamicConfigurationSummary saved = await fixture.ProxyConfigurations.SaveDynamicConfigurationAsync(
            fixture.UserId, fixture.TeamId, fixture.Server.Id, "custom.yml",
            new SaveProxyDynamicConfigurationRequest(content), TestContext.Current.CancellationToken);

        Assert.Equal("custom.yml", saved.FileName);
        Assert.Equal(content, fixture.Files.Contents["proxy/dynamic/custom.yml"]);
        ProxyDynamicConfiguration stored = Assert.Single(fixture.Db.DynamicConfigurationItems);
        Assert.StartsWith(TestSecretCipher.Prefix, stored.Content, StringComparison.Ordinal);
        ProxyConfigurationVersion version = Assert.Single(fixture.Db.ProxyConfigurationVersionItems);
        Assert.Equal(ProxyConfigurationSource.UserDynamic, version.Source);
        Assert.Equal("custom.yml", version.FileName);

        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyConfigurations.SaveDynamicConfigurationAsync(
            fixture.UserId, fixture.TeamId, fixture.Server.Id, "vessel-routes.yml",
            new SaveProxyDynamicConfigurationRequest(content), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyConfigurations.SaveDynamicConfigurationAsync(
            fixture.UserId, fixture.TeamId, fixture.Server.Id, "../escape.yml",
            new SaveProxyDynamicConfigurationRequest(content), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyConfigurations.SaveDynamicConfigurationAsync(
            fixture.UserId, fixture.TeamId, fixture.Server.Id, "broken.yml",
            new SaveProxyDynamicConfigurationRequest("invalid: ["), TestContext.Current.CancellationToken));

        await fixture.ProxyConfigurations.DeleteDynamicConfigurationAsync(fixture.UserId, fixture.TeamId,
            fixture.Server.Id, "custom.yml", TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Db.DynamicConfigurationItems);
        Assert.False(fixture.Files.Contents.ContainsKey("proxy/dynamic/custom.yml"));
    }

    [Fact]
    public async Task UserDynamicConfiguration_OnHostLocalServerRequiresInstanceAdmin()
    {
        var fixture = ServiceFixture.Create();
        var member = User.Create(new DisplayName("Member"), new EmailAddress("member@example.com"), fixture.Now);
        fixture.Db.UserItems.Add(member);
        fixture.Db.TeamItems.Single().AddMember(member.Id, TeamRole.Admin, fixture.Now);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.ProxyConfigurations.SaveDynamicConfigurationAsync(member.Id, fixture.TeamId, fixture.Server.Id,
                "custom.yml", new SaveProxyDynamicConfigurationRequest("http: {}\n"),
                TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Files.Contents);
    }

    // ---- NET-04: certificate truth from acme.json -------------------------------------------------------------------

    [Fact]
    public async Task CertificateSync_MirrorsAcmeEntriesTracksPendingAndInUse()
    {
        var fixture = ServiceFixture.Create();
        await Configure(fixture, "https://app.example.com");
        await Configure(fixture, "https://new.example.com");
        fixture.Acme.Entries.Add(new AcmeCertificateEntry(new string('1', 64), "letsencrypt", "app.example.com",
            ["www.example.com"], fixture.Now.AddDays(-10), fixture.Now.AddDays(80)));
        fixture.Acme.Entries.Add(new AcmeCertificateEntry(new string('2', 64), "letsencrypt", "old.example.com", [],
            fixture.Now.AddDays(-10), fixture.Now.AddDays(80)));

        CertificateSyncResult result = await fixture.Inventory.SyncServerAsync(fixture.UserId, fixture.TeamId,
            fixture.Server.Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Certificates);
        Certificate app = fixture.Db.CertificateItems.Single(certificate => certificate.Host == "app.example.com");
        Assert.Equal(CertificateStatus.Issued, app.Status);
        Assert.True(app.InUse);
        Assert.Equal(fixture.Application.Id, app.ApplicationId);
        Assert.Equal(["www.example.com"], app.SubjectAlternativeNameList);
        Certificate old = fixture.Db.CertificateItems.Single(certificate => certificate.Host == "old.example.com");
        Assert.False(old.InUse);
        Certificate pending = fixture.Db.CertificateItems.Single(certificate => certificate.Host == "new.example.com");
        Assert.Equal(CertificateStatus.Pending, pending.Status);
        Assert.Empty(fixture.Notifications.Published);
    }

    [Fact]
    public async Task CertificateSync_AlertsOnceWhenAnInUseCertificateIsNotRenewed()
    {
        var fixture = ServiceFixture.Create();
        await Configure(fixture, "https://app.example.com");
        fixture.Acme.Entries.Add(new AcmeCertificateEntry(new string('1', 64), "letsencrypt", "app.example.com", [],
            fixture.Now.AddDays(-85), fixture.Now.AddDays(5)));

        await fixture.Inventory.SyncAllAsync(TestContext.Current.CancellationToken);
        await fixture.Inventory.SyncAllAsync(TestContext.Current.CancellationToken);

        TeamNotification notification = Assert.Single(fixture.Notifications.Published);
        Assert.Equal(NotificationEventTypes.CertificateExpiringSoon, notification.Notification.EventType);
        Assert.Contains("app.example.com", notification.Notification.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificateDeleteUnused_RemovesOnlyUnusedEntriesAndAudits()
    {
        var fixture = ServiceFixture.Create();
        await Configure(fixture, "https://app.example.com");
        fixture.Acme.Entries.Add(new AcmeCertificateEntry(new string('1', 64), "letsencrypt", "*.example.com", [],
            null, fixture.Now.AddDays(80)));
        fixture.Acme.Entries.Add(new AcmeCertificateEntry(new string('2', 64), "letsencrypt", "old.other.com", [],
            null, fixture.Now.AddDays(80)));

        CertificateDeleteResult result = await fixture.Inventory.DeleteUnusedAsync(fixture.UserId, fixture.TeamId,
            fixture.Server.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Deleted);
        Assert.True(result.RestartRequired);
        Assert.Equal([new string('2', 64)], fixture.Acme.Deleted);
        Assert.Contains(fixture.Audit.Records, record => record.Action == AuditActions.CertificateDeleted);
        Assert.DoesNotContain(fixture.Db.CertificateItems, certificate => certificate.Host == "old.other.com");
    }

    [Fact]
    public async Task CertificateDelete_RejectsCertificatesOfAnotherServerAndNonAdminsOnHostLocalServers()
    {
        var fixture = ServiceFixture.Create();
        var otherServer = Server.Create(fixture.TeamId, new ResourceName("other"),
            new ServerAddress("10.0.0.9", new PortNumber(22)), ServerConnectionType.Ssh, ContainerRuntimeKind.Docker,
            fixture.Now);
        fixture.Db.ServerItems.Add(otherServer);
        var foreign = Certificate.CreatePending(fixture.TeamId, otherServer.Id, null, "x.example.com", fixture.Now);
        foreign.SyncFromAcme(new string('9', 64), "letsencrypt", "x.example.com", [], null, fixture.Now.AddDays(9),
            false, null, fixture.Now);
        fixture.Db.CertificateItems.Add(foreign);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Inventory.DeleteAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, foreign.Id, TestContext.Current.CancellationToken));

        var member = User.Create(new DisplayName("Member"), new EmailAddress("member@example.com"), fixture.Now);
        fixture.Db.UserItems.Add(member);
        fixture.Db.TeamItems.Single(team => team.Id == fixture.TeamId).AddMember(member.Id, TeamRole.Admin,
            fixture.Now);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Inventory.DeleteUnusedAsync(member.Id,
            fixture.TeamId, fixture.Server.Id, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Acme.Deleted);
    }

    private static Task<DomainRouteSummary> Configure(ServiceFixture fixture, string host, bool tls = true)
    {
        return fixture.Domains.ConfigureAsync(fixture.UserId, fixture.TeamId, fixture.Application.Id,
            new ConfigureDomainRouteRequest(host, null, tls, false, false), TestContext.Current.CancellationToken);
    }

    private static ProxyConfigurationVersion AddVersion(ServiceFixture fixture, DateTimeOffset createdAt,
        bool applied, string? fileName = null)
    {
        var version = ProxyConfigurationVersion.Create(fixture.Server.Id, ProxyProviderKind.Traefik, "v",
            Guid.NewGuid().ToString("N"), "content", null, createdAt,
            fileName is null ? ProxyConfigurationSource.Generated : ProxyConfigurationSource.UserDynamic, fileName);
        version.MarkValidated(createdAt);
        if (applied) version.MarkApplied(createdAt);
        fixture.Db.ProxyConfigurationVersionItems.Add(version);
        return version;
    }

    /// <summary>Reversible stand-in for the key ring: values are visibly "encrypted" so tests can assert at-rest form.</summary>
    private sealed class TestSecretCipher : ISecretCipher
    {
        public const string Prefix = "enc:v1:";

        public string ActiveKeyVersion => "v1";

        public SecretCipherText Encrypt(string plaintext, string associatedData)
        {
            throw new NotSupportedException();
        }

        public string Decrypt(SecretCipherText cipherText, string associatedData)
        {
            throw new NotSupportedException();
        }

        public string Protect(string plaintext, string purpose)
        {
            return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(purpose + "\n" + plaintext));
        }

        public string Unprotect(string protectedValue, string purpose)
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[Prefix.Length..]));
            if (!decoded.StartsWith(purpose + "\n", StringComparison.Ordinal))
                throw new SecretDecryptionException();
            return decoded[(purpose.Length + 1)..];
        }

        public bool IsProtected(string value)
        {
            return value.StartsWith(Prefix, StringComparison.Ordinal);
        }

        public string? GetProtectedKeyVersion(string value)
        {
            return IsProtected(value) ? ActiveKeyVersion : null;
        }
    }

    private sealed class TestSecretVault : ISecretVault
    {
        public Dictionary<SecretReferenceId, string> Values { get; } = [];

        public bool FailReveal { get; set; }

        public Task<SecretReference> StoreAsync(TeamId teamId, SecretScope scope, string key, string plaintext,
            SecretPolicy policy, SecretTarget target, CancellationToken cancellationToken = default)
        {
            var reference = SecretReference.Create(teamId, scope, key, SecretProvider.Vessel, "vault", policy,
                DateTimeOffset.UtcNow);
            Values[reference.Id] = plaintext;
            return Task.FromResult(reference);
        }

        public Task ReplaceAsync(SecretReferenceId secretReferenceId, string plaintext,
            CancellationToken cancellationToken = default)
        {
            Values[secretReferenceId] = plaintext;
            return Task.CompletedTask;
        }

        public Task<string> RevealAsync(UserId actorUserId, TeamId teamId, SecretReferenceId secretReferenceId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> RevealForDeploymentAsync(TeamId teamId, SecretReferenceId secretReferenceId,
            CancellationToken cancellationToken = default)
        {
            if (FailReveal) throw new InvalidOperationException("vault unavailable");
            return Task.FromResult(Values[secretReferenceId]);
        }
    }

    private sealed class TestAcmeStore : ITraefikAcmeStore
    {
        public List<AcmeCertificateEntry> Entries { get; } = [];

        public List<string> Deleted { get; } = [];

        public Task<IReadOnlyList<AcmeCertificateEntry>> ListCertificatesAsync(ServerExecutionTarget target,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AcmeCertificateEntry>>(Entries.ToArray());
        }

        public Task<AcmeWriteResult> DeleteCertificatesAsync(ServerExecutionTarget target,
            IReadOnlyCollection<string> certificateIds, CancellationToken cancellationToken = default)
        {
            Deleted.AddRange(certificateIds);
            var removed = Entries.RemoveAll(entry => certificateIds.Contains(entry.Id));
            return Task.FromResult(new AcmeWriteResult(removed, "acme-20261120T120000000Z.json", true));
        }

        public Task<IReadOnlyList<AcmeBackup>> ListBackupsAsync(ServerExecutionTarget target,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AcmeBackup>>([]);
        }

        public Task<AcmeWriteResult> RestoreBackupAsync(ServerExecutionTarget target, string backupName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AcmeWriteResult(0, backupName, true));
        }

        public Task DeleteBackupAsync(ServerExecutionTarget target, string backupName,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
