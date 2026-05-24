using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Proxy;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Certificates;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.Domain.Deployments;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Notifications;
using Vessel.Domain.Projects;
using Vessel.Domain.Proxy;
using Vessel.Domain.Registries;
using Vessel.Domain.Secrets;
using Vessel.Domain.Servers;
using Vessel.Domain.Settings;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.UnitTests.Application;

public sealed class Phase10ProxyServiceTests
{
    [Fact]
    public async Task ConfigureDomain_NormalizesHostCreatesCertificateAndUpdatesCanonical()
    {
        ServiceFixture fixture = ServiceFixture.Create();

        DomainRouteSummary first = await fixture.Domains.ConfigureAsync(
            fixture.UserId,
            fixture.TeamId,
            fixture.Application.Id,
            new ConfigureDomainRouteRequest("HTTPS://App.Example.COM/", 8080, true, true, false));
        DomainRouteSummary second = await fixture.Domains.ConfigureAsync(
            fixture.UserId,
            fixture.TeamId,
            fixture.Application.Id,
            new ConfigureDomainRouteRequest("www.example.com", 8081, false, true, false));

        Assert.Equal("app.example.com", first.Host);
        Assert.True(first.TlsEnabled);
        Assert.Equal(CertificateStatus.Pending, first.CertificateStatus);
        Assert.Single(fixture.Db.CertificateItems);
        Assert.Equal("app.example.com", fixture.Db.CertificateItems.Single().Host);
        Assert.True(second.Canonical);
        Assert.False(fixture.Application.Domains.Single(domain => domain.DomainName.Value == "app.example.com").Canonical);
        Assert.Equal(2, fixture.Audit.Records.Count(record => record.Action == AuditActions.DomainRouteConfigured));
    }

    [Theory]
    [InlineData("app.example.com:8080")]
    [InlineData("app.example.com/path")]
    [InlineData("bad_host.example.com")]
    [InlineData("")]
    public async Task ConfigureDomain_RejectsMalformedHosts(string host)
    {
        ServiceFixture fixture = ServiceFixture.Create();

        await Assert.ThrowsAsync<DomainException>(() => fixture.Domains.ConfigureAsync(
            fixture.UserId,
            fixture.TeamId,
            fixture.Application.Id,
            new ConfigureDomainRouteRequest(host, 8080, true, false, false)));
    }

    [Fact]
    public async Task ConfigureDomain_RejectsCrossTeamApplicationAccess()
    {
        ServiceFixture fixture = ServiceFixture.Create();
        User otherUser = User.Create(new DisplayName("Other User"), new EmailAddress("other@example.com"), fixture.Now);
        Team otherTeam = Team.Create(new DisplayName("Other Team"), otherUser.Id, isPersonal: false, fixture.Now);
        fixture.Db.UserItems.Add(otherUser);
        fixture.Db.TeamItems.Add(otherTeam);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Domains.ConfigureAsync(
            otherUser.Id,
            otherTeam.Id,
            fixture.Application.Id,
            new ConfigureDomainRouteRequest("app.example.com", 8080, true, false, false)));
    }

    [Fact]
    public async Task QueueCertificate_IsIdempotentAndValidatesHosts()
    {
        ServiceFixture fixture = ServiceFixture.Create();

        CertificateSummary first = await fixture.Certificates.QueueIssuanceAsync(
            fixture.UserId,
            fixture.TeamId,
            fixture.Application.Id,
            "https://APP.example.com/");
        CertificateSummary second = await fixture.Certificates.QueueIssuanceAsync(
            fixture.UserId,
            fixture.TeamId,
            fixture.Application.Id,
            "app.example.com");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(fixture.Db.CertificateItems);
        Assert.Equal("app.example.com", second.Host);
        await Assert.ThrowsAsync<DomainException>(() => fixture.Certificates.QueueIssuanceAsync(
            fixture.UserId,
            fixture.TeamId,
            fixture.Application.Id,
            "app.example.com/path"));
    }

    [Fact]
    public async Task RenewDue_QueuesIssuedDueCertificatesAndSkipsLockedRows()
    {
        ServiceFixture fixture = ServiceFixture.Create();
        Certificate due = Certificate.Create(
            fixture.TeamId,
            fixture.Application.Id,
            "due.example.com",
            CertificateProvider.TraefikAcme,
            fixture.Now.AddDays(-90));
        due.MarkIssued(fixture.Now.AddDays(-90), fixture.Now.AddDays(7), null, null);
        Certificate notDue = Certificate.Create(
            fixture.TeamId,
            fixture.Application.Id,
            "later.example.com",
            CertificateProvider.TraefikAcme,
            fixture.Now.AddDays(-10));
        notDue.MarkIssued(fixture.Now.AddDays(-10), fixture.Now.AddDays(40), null, null);
        fixture.Db.CertificateItems.AddRange([due, notDue]);

        int renewed = await fixture.Certificates.RenewDueAsync();

        Assert.Equal(1, renewed);
        Assert.Equal(CertificateStatus.RenewalQueued, due.Status);
        Assert.Equal(CertificateStatus.Issued, notDue.Status);

        fixture.Locks.ShouldAcquire = false;
        due.MarkFailed("reset", fixture.Now);
        int skipped = await fixture.Certificates.RenewDueAsync();

        Assert.Equal(0, skipped);
        Assert.Equal(CertificateStatus.Failed, due.Status);
    }

    [Fact]
    public async Task ApplyProxyConfiguration_PersistsAppliedVersionWithGeneratedRoutes()
    {
        ServiceFixture fixture = ServiceFixture.Create();
        fixture.Application.UpsertDomain(new DomainName("app.example.com"), null, true, true, false, fixture.Now);
        fixture.Application.UpsertDomain(new DomainName("admin.example.com"), 9090, false, false, false, fixture.Now);

        ProxyConfigurationSummary summary = await fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(
            fixture.UserId,
            fixture.TeamId,
            fixture.Server.Id);

        ProxyConfigurationVersion version = Assert.Single(fixture.Db.ProxyConfigurationVersionItems);
        Assert.Equal(ProxyConfigurationStatus.Applied, version.Status);
        Assert.Equal(version.Id.Value, summary.Id);
        Assert.Single(fixture.ProxyProvider.AppliedDocuments);
        Assert.Collection(
            fixture.ProxyProvider.GeneratedRoutes.OrderBy(route => route.Host, StringComparer.OrdinalIgnoreCase),
            route =>
            {
                Assert.Equal("admin.example.com", route.Host);
                Assert.Equal(9090, route.TargetPort);
                Assert.False(route.TlsEnabled);
            },
            route =>
            {
                Assert.Equal("app.example.com", route.Host);
                Assert.Equal(3000, route.TargetPort);
                Assert.True(route.TlsEnabled);
            });
        Assert.Contains(fixture.Audit.Records, record => record.Action == AuditActions.ProxyConfigurationApplied);
    }

    [Fact]
    public async Task ApplyProxyConfiguration_WhenValidationFails_PersistsFailedVersionAndDoesNotApply()
    {
        ServiceFixture fixture = ServiceFixture.Create();
        fixture.ProxyProvider.ValidationResult = new ProxyValidationResult(false, ["password secret-value"]);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
            fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId, fixture.Server.Id));

        ProxyConfigurationVersion version = Assert.Single(fixture.Db.ProxyConfigurationVersionItems);
        Assert.Equal(ProxyConfigurationStatus.Failed, version.Status);
        Assert.Contains("[redacted]", version.ValidationError, StringComparison.Ordinal);
        Assert.Empty(fixture.ProxyProvider.AppliedDocuments);
        Assert.Contains("[redacted]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyProxyConfiguration_WhenApplyFails_RollsBackPreviousAppliedVersion()
    {
        ServiceFixture fixture = ServiceFixture.Create();
        ProxyConfigurationVersion previous = ProxyConfigurationVersion.Create(
            fixture.Server.Id,
            ProxyProviderKind.Traefik,
            "previous",
            new string('a', 64),
            "http:\n  routers: {}\n  services: {}\n",
            null,
            fixture.Now.AddMinutes(-10));
        previous.MarkValidated(fixture.Now.AddMinutes(-9));
        previous.MarkApplied(fixture.Now.AddMinutes(-8));
        fixture.Db.ProxyConfigurationVersionItems.Add(previous);
        fixture.ProxyProvider.ApplyResult = new ProxyApplyResult(false, "password leaked");

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
            fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId, fixture.Server.Id));

        ProxyConfigurationVersion current = fixture.Db.ProxyConfigurationVersionItems.Single(version => version.Id != previous.Id);
        Assert.Equal(previous.Id, current.PreviousVersionId);
        Assert.Equal(ProxyConfigurationStatus.Failed, current.Status);
        Assert.Contains("[redacted]", current.ApplyError, StringComparison.Ordinal);
        Assert.Single(fixture.ProxyProvider.RolledBackDocuments);
        Assert.Contains("[redacted]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyProxyConfiguration_RejectsUnreachableServerAndLockContention()
    {
        ServiceFixture fixture = ServiceFixture.Create();
        fixture.Server.ChangeStatus(ServerStatus.Unreachable, fixture.Now);

        await Assert.ThrowsAsync<DomainException>(() =>
            fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId, fixture.Server.Id));
        Assert.Empty(fixture.Db.ProxyConfigurationVersionItems);

        fixture.Server.ChangeStatus(ServerStatus.Reachable, fixture.Now);
        fixture.Locks.ShouldAcquire = false;
        await Assert.ThrowsAsync<DomainException>(() =>
            fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId, fixture.Server.Id));
        Assert.Empty(fixture.Db.ProxyConfigurationVersionItems);
    }

    [Fact]
    public async Task Rollback_RejectsVersionFromDifferentServer()
    {
        ServiceFixture fixture = ServiceFixture.Create();
        Server otherServer = Server.Create(
            fixture.TeamId,
            new ResourceName("other"),
            new ServerAddress("127.0.0.2", new PortNumber(22)),
            ServerConnectionType.Local,
            ContainerRuntimeKind.Docker,
            fixture.Now);
        fixture.Db.ServerItems.Add(otherServer);
        ProxyConfigurationVersion version = ProxyConfigurationVersion.Create(
            otherServer.Id,
            ProxyProviderKind.Traefik,
            "v1",
            new string('a', 64),
            "http:\n  routers: {}\n  services: {}\n",
            null,
            fixture.Now);
        version.MarkValidated(fixture.Now);
        version.MarkApplied(fixture.Now);
        fixture.Db.ProxyConfigurationVersionItems.Add(version);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.ProxyConfigurations.RollbackAsync(fixture.UserId, fixture.TeamId, fixture.Server.Id, version.Id));
    }

    private sealed class ServiceFixture
    {
        private ServiceFixture()
        {
            Domains = new DomainRoutingService(Db, Authorization, Audit, Redactor, Time);
            Certificates = new CertificateManagementService(Db, Authorization, Locks, Audit, Redactor, Time);
            ProxyConfigurations = new ProxyConfigurationService(Db, Authorization, ProxyProvider, Locks, Audit, Redactor, Time);
        }

        public DateTimeOffset Now { get; } = new(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);

        public TestDbContext Db { get; } = new();

        public TestAuditWriter Audit { get; } = new();

        public TestLockManager Locks { get; } = new();

        public TestRedactor Redactor { get; } = new();

        public TestProxyProvider ProxyProvider { get; } = new();

        public TimeProvider Time => new FixedTimeProvider(Now);

        public VesselAuthorizationService Authorization => new(Db);

        public DomainRoutingService Domains { get; }

        public CertificateManagementService Certificates { get; }

        public ProxyConfigurationService ProxyConfigurations { get; }

        public UserId UserId { get; private set; }

        public TeamId TeamId { get; private set; }

        public Server Server { get; private set; } = null!;

        public AppEntity Application { get; private set; } = null!;

        public static ServiceFixture Create()
        {
            var fixture = new ServiceFixture();
            DateTimeOffset now = fixture.Now;
            User user = User.Create(new DisplayName("Test User"), new EmailAddress("owner@example.com"), now);
            Team team = Team.Create(new DisplayName("Team"), user.Id, isPersonal: false, now);
            Project project = Project.Create(team.Id, new ResourceName("Project"), now);
            EnvironmentEntity environment = EnvironmentEntity.CreateProduction(project.Id, now);
            Server server = Server.Create(
                team.Id,
                new ResourceName("server"),
                new ServerAddress("127.0.0.1", new PortNumber(22)),
                ServerConnectionType.Local,
                ContainerRuntimeKind.Docker,
                now);
            server.ChangeStatus(ServerStatus.Reachable, now);
            AppEntity application = AppEntity.Create(
                environment.Id,
                server.Id,
                new ResourceName("Web App"),
                new GitSource(new RepositoryUrl("https://example.com/repo.git"), "main"),
                BuildConfiguration.Default(ApplicationBuildPack.Dockerfile),
                now);
            application.UpdateSettings(
                application.Name,
                null,
                application.GitSource,
                application.BuildConfiguration,
                RuntimeConfiguration.Default with { ExposedPort = new PortNumber(3000) },
                application.DeploymentSettings,
                now);

            fixture.Db.UserItems.Add(user);
            fixture.Db.TeamItems.Add(team);
            fixture.Db.ProjectItems.Add(project);
            fixture.Db.EnvironmentItems.Add(environment);
            fixture.Db.ServerItems.Add(server);
            fixture.Db.ApplicationItems.Add(application);

            fixture.UserId = user.Id;
            fixture.TeamId = team.Id;
            fixture.Server = server;
            fixture.Application = application;
            return fixture;
        }
    }

    private sealed class TestDbContext : IVesselDbContext
    {
        public List<User> UserItems { get; } = [];
        public List<Team> TeamItems { get; } = [];
        public List<Project> ProjectItems { get; } = [];
        public List<EnvironmentEntity> EnvironmentItems { get; } = [];
        public List<Server> ServerItems { get; } = [];
        public List<AppEntity> ApplicationItems { get; } = [];
        public List<Certificate> CertificateItems { get; } = [];
        public List<ProxyConfigurationVersion> ProxyConfigurationVersionItems { get; } = [];

        public IQueryable<User> Users => UserItems.AsQueryable();
        public IQueryable<Team> Teams => TeamItems.AsQueryable();
        public IQueryable<TeamMembership> TeamMemberships => TeamItems.SelectMany(team => team.Memberships).AsQueryable();
        public IQueryable<TeamInvitation> TeamInvitations => Array.Empty<TeamInvitation>().AsQueryable();
        public IQueryable<Project> Projects => ProjectItems.AsQueryable();
        public IQueryable<EnvironmentEntity> Environments => EnvironmentItems.AsQueryable();
        public IQueryable<Server> Servers => ServerItems.AsQueryable();
        public IQueryable<AppEntity> Applications => ApplicationItems.AsQueryable();
        public IQueryable<ApplicationDomain> ApplicationDomains => ApplicationItems.SelectMany(application => application.Domains).AsQueryable();
        public IQueryable<DatabaseResource> DatabaseResources => Array.Empty<DatabaseResource>().AsQueryable();
        public IQueryable<Deployment> Deployments => Array.Empty<Deployment>().AsQueryable();
        public IQueryable<SecretReference> SecretReferences => Array.Empty<SecretReference>().AsQueryable();
        public IQueryable<SecretValue> SecretValues => Array.Empty<SecretValue>().AsQueryable();
        public IQueryable<EnvironmentVariable> EnvironmentVariables => Array.Empty<EnvironmentVariable>().AsQueryable();
        public IQueryable<RegistryCredential> RegistryCredentials => Array.Empty<RegistryCredential>().AsQueryable();
        public IQueryable<ServerStatusSnapshot> ServerStatusSnapshots => Array.Empty<ServerStatusSnapshot>().AsQueryable();
        public IQueryable<NotificationTarget> NotificationTargets => Array.Empty<NotificationTarget>().AsQueryable();
        public IQueryable<AuditLog> AuditLogs => Array.Empty<AuditLog>().AsQueryable();
        public IQueryable<SettingEntry> Settings => Array.Empty<SettingEntry>().AsQueryable();
        public IQueryable<PersonalAccessToken> PersonalAccessTokens => Array.Empty<PersonalAccessToken>().AsQueryable();
        public IQueryable<WebhookEvent> WebhookEvents => Array.Empty<WebhookEvent>().AsQueryable();
        public IQueryable<ApplicationWebhookConfiguration> ApplicationWebhookConfigurations => Array.Empty<ApplicationWebhookConfiguration>().AsQueryable();
        public IQueryable<ApplicationPreview> ApplicationPreviews => Array.Empty<ApplicationPreview>().AsQueryable();
        public IQueryable<ProxyConfigurationVersion> ProxyConfigurationVersions => ProxyConfigurationVersionItems.AsQueryable();
        public IQueryable<Certificate> Certificates => CertificateItems.AsQueryable();

        public IRepository<User, UserId> UserRepository => new ListRepository<User, UserId>(UserItems);
        public IRepository<Team, TeamId> TeamRepository => new ListRepository<Team, TeamId>(TeamItems);
        public IRepository<TeamInvitation, TeamInvitationId> TeamInvitationRepository => new EmptyRepository<TeamInvitation, TeamInvitationId>();
        public IRepository<PersonalAccessToken, PersonalAccessTokenId> PersonalAccessTokenRepository => new EmptyRepository<PersonalAccessToken, PersonalAccessTokenId>();
        public IRepository<Project, ProjectId> ProjectRepository => new ListRepository<Project, ProjectId>(ProjectItems);
        public IRepository<EnvironmentEntity, EnvironmentId> EnvironmentRepository => new ListRepository<EnvironmentEntity, EnvironmentId>(EnvironmentItems);
        public IRepository<Server, ServerId> ServerRepository => new ListRepository<Server, ServerId>(ServerItems);
        public IRepository<AppEntity, Vessel.Domain.ApplicationId> ApplicationRepository => new ListRepository<AppEntity, Vessel.Domain.ApplicationId>(ApplicationItems);
        public IRepository<DatabaseResource, DatabaseResourceId> DatabaseResourceRepository => new EmptyRepository<DatabaseResource, DatabaseResourceId>();
        public IRepository<Deployment, DeploymentId> DeploymentRepository => new EmptyRepository<Deployment, DeploymentId>();
        public IRepository<SecretReference, SecretReferenceId> SecretReferenceRepository => new EmptyRepository<SecretReference, SecretReferenceId>();
        public IRepository<SecretValue, SecretValueId> SecretValueRepository => new EmptyRepository<SecretValue, SecretValueId>();
        public IRepository<EnvironmentVariable, EnvironmentVariableId> EnvironmentVariableRepository => new EmptyRepository<EnvironmentVariable, EnvironmentVariableId>();
        public IRepository<RegistryCredential, RegistryCredentialId> RegistryCredentialRepository => new EmptyRepository<RegistryCredential, RegistryCredentialId>();
        public IRepository<ServerStatusSnapshot, ServerStatusSnapshotId> ServerStatusSnapshotRepository => new EmptyRepository<ServerStatusSnapshot, ServerStatusSnapshotId>();
        public IRepository<WebhookEvent, WebhookEventId> WebhookEventRepository => new EmptyRepository<WebhookEvent, WebhookEventId>();
        public IRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId> ApplicationWebhookConfigurationRepository => new EmptyRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId>();
        public IRepository<ApplicationPreview, ApplicationPreviewId> ApplicationPreviewRepository => new EmptyRepository<ApplicationPreview, ApplicationPreviewId>();
        public IRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId> ProxyConfigurationVersionRepository => new ListRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId>(ProxyConfigurationVersionItems);
        public IRepository<Certificate, CertificateId> CertificateRepository => new ListRepository<Certificate, CertificateId>(CertificateItems);

        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }
    }

    private sealed class ListRepository<TEntity, TId>(List<TEntity> items) : IRepository<TEntity, TId>
        where TEntity : Entity<TId>
        where TId : notnull
    {
        public Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(items.SingleOrDefault(item => EqualityComparer<TId>.Default.Equals(item.Id, id)));
        }

        public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            items.Add(entity);
            return Task.CompletedTask;
        }

        public void Remove(TEntity entity)
        {
            items.Remove(entity);
        }
    }

    private sealed class EmptyRepository<TEntity, TId> : IRepository<TEntity, TId>
        where TEntity : Entity<TId>
        where TId : notnull
    {
        public Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TEntity?>(null);
        }

        public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void Remove(TEntity entity)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestAuditWriter : IAuditWriter
    {
        public List<AuditRecord> Records { get; } = [];

        public Task RecordAsync(
            TeamId? teamId,
            UserId? actorUserId,
            string action,
            AuditTarget target,
            string? correlationId,
            IReadOnlyDictionary<string, object?> metadata,
            CancellationToken cancellationToken = default)
        {
            Records.Add(new AuditRecord(teamId, actorUserId, action, target, metadata));
            return Task.CompletedTask;
        }
    }

    private sealed record AuditRecord(
        TeamId? TeamId,
        UserId? ActorUserId,
        string Action,
        AuditTarget Target,
        IReadOnlyDictionary<string, object?> Metadata);

    private sealed class TestLockManager : IDistributedLockManager
    {
        public bool ShouldAcquire { get; set; } = true;

        public Task<DistributedLockHandle?> TryAcquireAsync(
            string key,
            TimeSpan leaseDuration,
            TimeSpan waitTimeout,
            CancellationToken cancellationToken = default)
        {
            DistributedLockHandle? handle = ShouldAcquire
                ? new DistributedLockHandle(key, "owner", DateTimeOffset.UtcNow, leaseDuration)
                : null;
            return Task.FromResult(handle);
        }
    }

    private sealed class TestRedactor : ISecretRedactor
    {
        public string Redact(string value, RedactionContext? context = null)
        {
            return value.Replace("password", "[redacted]", StringComparison.OrdinalIgnoreCase)
                .Replace("secret-value", "[redacted]", StringComparison.OrdinalIgnoreCase)
                .Replace("leaked", "[redacted]", StringComparison.OrdinalIgnoreCase);
        }

        public byte[] RedactUtf8(byte[] value, RedactionContext? context = null)
        {
            return value;
        }
    }

    private sealed class TestProxyProvider : IProxyProvider
    {
        public ProxyValidationResult ValidationResult { get; set; } = ProxyValidationResult.Success;

        public ProxyApplyResult ApplyResult { get; set; } = new(true, "applied");

        public List<ProxyRoute> GeneratedRoutes { get; private set; } = [];

        public List<ProxyConfigurationDocument> AppliedDocuments { get; } = [];

        public List<ProxyConfigurationDocument> RolledBackDocuments { get; } = [];

        public ProxyConfigurationDocument Generate(ServerId serverId, IReadOnlyList<ProxyRoute> routes)
        {
            GeneratedRoutes = routes.ToList();
            return new ProxyConfigurationDocument(
                serverId,
                ProxyProviderKind.Traefik,
                "test-version",
                "http:\n  routers: {}\n  services: {}\n",
                new string('a', 64),
                routes);
        }

        public ProxyValidationResult Validate(ProxyConfigurationDocument document)
        {
            return ValidationResult;
        }

        public Task<ProxyApplyResult> ApplyAsync(
            ProxyConfigurationDocument document,
            ProxyConfigurationDocument? previous,
            CancellationToken cancellationToken = default)
        {
            AppliedDocuments.Add(document);
            return Task.FromResult(ApplyResult);
        }

        public Task<ProxyApplyResult> RollbackAsync(
            ProxyConfigurationDocument previous,
            CancellationToken cancellationToken = default)
        {
            RolledBackDocuments.Add(previous);
            return Task.FromResult(new ProxyApplyResult(true, "rolled back"));
        }

        public Task<ProxyApplyResult> ReloadAsync(ServerId serverId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProxyApplyResult(true, "reloaded"));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}
