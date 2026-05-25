using System.Linq.Expressions;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Proxy;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Backups;
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
using Vessel.Domain.Services;
using Vessel.Domain.Settings;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using ApplicationId = Vessel.Domain.ApplicationId;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.UnitTests.Application;

public sealed class Phase10ProxyServiceTests
{
    [Fact]
    public async Task ConfigureDomain_NormalizesHostCreatesCertificateAndUpdatesCanonical()
    {
        var fixture = ServiceFixture.Create();

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
        Assert.False(fixture.Application.Domains.Single(domain => domain.DomainName.Value == "app.example.com")
            .Canonical);
        Assert.Equal(2, fixture.Audit.Records.Count(record => record.Action == AuditActions.DomainRouteConfigured));
    }

    [Theory]
    [InlineData("app.example.com:8080")]
    [InlineData("app.example.com/path")]
    [InlineData("bad_host.example.com")]
    [InlineData("")]
    public async Task ConfigureDomain_RejectsMalformedHosts(string host)
    {
        var fixture = ServiceFixture.Create();

        await Assert.ThrowsAsync<DomainException>(() => fixture.Domains.ConfigureAsync(
            fixture.UserId,
            fixture.TeamId,
            fixture.Application.Id,
            new ConfigureDomainRouteRequest(host, 8080, true, false, false)));
    }

    [Fact]
    public async Task ConfigureDomain_RejectsCrossTeamApplicationAccess()
    {
        var fixture = ServiceFixture.Create();
        var otherUser = User.Create(new DisplayName("Other User"), new EmailAddress("other@example.com"), fixture.Now);
        var otherTeam = Team.Create(new DisplayName("Other Team"), otherUser.Id, false, fixture.Now);
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
        var fixture = ServiceFixture.Create();

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
        Assert.Equal(2, fixture.BackgroundJobs.Enqueued.Count);
        Assert.All(fixture.BackgroundJobs.Enqueued,
            job => Assert.StartsWith(nameof(CertificateIssuanceJob), job, StringComparison.Ordinal));
        AuditRecord queuedAudit = Assert.Single(fixture.Audit.Records, record =>
            record.Action == AuditActions.CertificateIssuanceQueued && Equals(record.Metadata["jobId"], "job-1"));
        Assert.Equal("TraefikAcme", queuedAudit.Metadata["provider"]);
        Assert.Equal("app.example.com", queuedAudit.Metadata["host"]);
        await Assert.ThrowsAsync<DomainException>(() => fixture.Certificates.QueueIssuanceAsync(
            fixture.UserId,
            fixture.TeamId,
            fixture.Application.Id,
            "app.example.com/path"));
    }

    [Fact]
    public async Task QueueCertificate_WhenLockUnavailable_DoesNotPersistOrEnqueue()
    {
        var fixture = ServiceFixture.Create();
        fixture.Locks.ShouldAcquire = false;

        await Assert.ThrowsAsync<DomainException>(() => fixture.Certificates.QueueIssuanceAsync(
            fixture.UserId,
            fixture.TeamId,
            fixture.Application.Id,
            "app.example.com"));

        Assert.Empty(fixture.Db.CertificateItems);
        Assert.Empty(fixture.BackgroundJobs.Enqueued);
    }

    [Fact]
    public async Task RequestIssuance_AppliesProxyRoutesForCertificateApplication()
    {
        var fixture = ServiceFixture.Create();
        fixture.Application.UpsertDomain(new DomainName("app.example.com"), 8080, true, true, false, fixture.Now);
        var certificate = Certificate.Create(
            fixture.TeamId,
            fixture.Application.Id,
            "app.example.com",
            CertificateProvider.TraefikAcme,
            fixture.Now);
        fixture.Db.CertificateItems.Add(certificate);

        CertificateSummary summary = await fixture.Certificates.RequestIssuanceAsync(certificate.Id);

        Assert.Equal(certificate.Id.Value, summary.Id);
        Assert.Single(fixture.ProxyProvider.AppliedDocuments);
        Assert.Equal("app.example.com", fixture.ProxyProvider.GeneratedRoutes.Single().Host);
    }

    [Fact]
    public async Task RenewDue_QueuesIssuedDueCertificatesAndSkipsLockedRows()
    {
        var fixture = ServiceFixture.Create();
        var due = Certificate.Create(
            fixture.TeamId,
            fixture.Application.Id,
            "due.example.com",
            CertificateProvider.TraefikAcme,
            fixture.Now.AddDays(-90));
        due.MarkIssued(fixture.Now.AddDays(-90), fixture.Now.AddDays(7), null, null);
        var notDue = Certificate.Create(
            fixture.TeamId,
            fixture.Application.Id,
            "later.example.com",
            CertificateProvider.TraefikAcme,
            fixture.Now.AddDays(-10));
        notDue.MarkIssued(fixture.Now.AddDays(-10), fixture.Now.AddDays(40), null, null);
        fixture.Db.CertificateItems.AddRange([due, notDue]);

        var renewed = await fixture.Certificates.RenewDueAsync();

        Assert.Equal(1, renewed);
        Assert.Equal(CertificateStatus.RenewalQueued, due.Status);
        Assert.Equal(CertificateStatus.Issued, notDue.Status);

        fixture.Locks.ShouldAcquire = false;
        due.MarkFailed("reset", fixture.Now);
        var skipped = await fixture.Certificates.RenewDueAsync();

        Assert.Equal(0, skipped);
        Assert.Equal(CertificateStatus.Failed, due.Status);
    }

    [Fact]
    public void CertificateRecurringJobs_RegisterSchedulesRenewalJob()
    {
        var scheduler = new TestRecurringJobScheduler();

        CertificateRecurringJobs.Register(scheduler);

        RecurringRegistration registration = Assert.Single(scheduler.Registrations);
        Assert.Equal(CertificateRecurringJobs.RenewDueJobId, registration.RecurringJobId);
        Assert.Equal(typeof(CertificateRenewalJob), registration.JobType);
        Assert.Equal(CertificateRecurringJobs.RenewDueCronExpression, registration.CronExpression);
    }

    [Fact]
    public async Task ApplyProxyConfiguration_PersistsAppliedVersionWithGeneratedRoutes()
    {
        var fixture = ServiceFixture.Create();
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
        var fixture = ServiceFixture.Create();
        fixture.ProxyProvider.ValidationResult = new ProxyValidationResult(false, ["password secret-value"]);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
            fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId,
                fixture.Server.Id));

        ProxyConfigurationVersion version = Assert.Single(fixture.Db.ProxyConfigurationVersionItems);
        Assert.Equal(ProxyConfigurationStatus.Failed, version.Status);
        Assert.Contains("[redacted]", version.ValidationError, StringComparison.Ordinal);
        Assert.Empty(fixture.ProxyProvider.AppliedDocuments);
        Assert.Contains("[redacted]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyProxyConfiguration_WhenApplyFails_RollsBackPreviousAppliedVersion()
    {
        var fixture = ServiceFixture.Create();
        var previous = ProxyConfigurationVersion.Create(
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
            fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId,
                fixture.Server.Id));

        ProxyConfigurationVersion current =
            fixture.Db.ProxyConfigurationVersionItems.Single(version => version.Id != previous.Id);
        Assert.Equal(previous.Id, current.PreviousVersionId);
        Assert.Equal(ProxyConfigurationStatus.Failed, current.Status);
        Assert.Contains("[redacted]", current.ApplyError, StringComparison.Ordinal);
        Assert.Single(fixture.ProxyProvider.RolledBackDocuments);
        Assert.Contains("[redacted]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyProxyConfiguration_RejectsUnreachableServerAndLockContention()
    {
        var fixture = ServiceFixture.Create();
        fixture.Server.ChangeStatus(ServerStatus.Unreachable, fixture.Now);

        await Assert.ThrowsAsync<DomainException>(() =>
            fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId,
                fixture.Server.Id));
        Assert.Empty(fixture.Db.ProxyConfigurationVersionItems);

        fixture.Server.ChangeStatus(ServerStatus.Reachable, fixture.Now);
        fixture.Locks.ShouldAcquire = false;
        await Assert.ThrowsAsync<DomainException>(() =>
            fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId,
                fixture.Server.Id));
        Assert.Empty(fixture.Db.ProxyConfigurationVersionItems);
    }

    [Fact]
    public async Task Rollback_RejectsVersionFromDifferentServer()
    {
        var fixture = ServiceFixture.Create();
        var otherServer = Server.Create(
            fixture.TeamId,
            new ResourceName("other"),
            new ServerAddress("127.0.0.2", new PortNumber(22)),
            ServerConnectionType.Local,
            ContainerRuntimeKind.Docker,
            fixture.Now);
        fixture.Db.ServerItems.Add(otherServer);
        var version = ProxyConfigurationVersion.Create(
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
            ProxyConfigurations =
                new ProxyConfigurationService(Db, Authorization, ProxyProvider, Locks, Audit, Redactor, Time);
            Domains = new DomainRoutingService(Db, Authorization, Audit, Redactor, Time);
            Certificates = new CertificateManagementService(
                Db,
                Authorization,
                Locks,
                BackgroundJobs,
                ProxyConfigurations,
                Audit,
                Redactor,
                Time);
        }

        public DateTimeOffset Now { get; } = new(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);

        public TestDbContext Db { get; } = new();

        public TestAuditWriter Audit { get; } = new();

        public TestLockManager Locks { get; } = new();

        public TestBackgroundJobDispatcher BackgroundJobs { get; } = new();

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
            var user = User.Create(new DisplayName("Test User"), new EmailAddress("owner@example.com"), now);
            var team = Team.Create(new DisplayName("Team"), user.Id, false, now);
            var project = Project.Create(team.Id, new ResourceName("Project"), now);
            var environment = EnvironmentEntity.CreateProduction(project.Id, now);
            var server = Server.Create(
                team.Id,
                new ResourceName("server"),
                new ServerAddress("127.0.0.1", new PortNumber(22)),
                ServerConnectionType.Local,
                ContainerRuntimeKind.Docker,
                now);
            server.ChangeStatus(ServerStatus.Reachable, now);
            var application = AppEntity.Create(
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

        public int SaveCount { get; private set; }

        public IQueryable<User> Users => UserItems.AsQueryable();
        public IQueryable<Team> Teams => TeamItems.AsQueryable();

        public IQueryable<TeamMembership> TeamMemberships =>
            TeamItems.SelectMany(team => team.Memberships).AsQueryable();

        public IQueryable<TeamInvitation> TeamInvitations => Array.Empty<TeamInvitation>().AsQueryable();
        public IQueryable<Project> Projects => ProjectItems.AsQueryable();
        public IQueryable<EnvironmentEntity> Environments => EnvironmentItems.AsQueryable();
        public IQueryable<Server> Servers => ServerItems.AsQueryable();
        public IQueryable<AppEntity> Applications => ApplicationItems.AsQueryable();

        public IQueryable<ApplicationDomain> ApplicationDomains =>
            ApplicationItems.SelectMany(application => application.Domains).AsQueryable();

        public IQueryable<DatabaseResource> DatabaseResources => Array.Empty<DatabaseResource>().AsQueryable();
        public IQueryable<ServiceResource> ServiceResources => Array.Empty<ServiceResource>().AsQueryable();
        public IQueryable<BackupSchedule> BackupSchedules => Array.Empty<BackupSchedule>().AsQueryable();
        public IQueryable<BackupExecution> BackupExecutions => Array.Empty<BackupExecution>().AsQueryable();
        public IQueryable<Deployment> Deployments => Array.Empty<Deployment>().AsQueryable();
        public IQueryable<SecretReference> SecretReferences => Array.Empty<SecretReference>().AsQueryable();
        public IQueryable<SecretValue> SecretValues => Array.Empty<SecretValue>().AsQueryable();
        public IQueryable<EnvironmentVariable> EnvironmentVariables => Array.Empty<EnvironmentVariable>().AsQueryable();
        public IQueryable<RegistryCredential> RegistryCredentials => Array.Empty<RegistryCredential>().AsQueryable();

        public IQueryable<ServerStatusSnapshot> ServerStatusSnapshots =>
            Array.Empty<ServerStatusSnapshot>().AsQueryable();

        public IQueryable<NotificationTarget> NotificationTargets => Array.Empty<NotificationTarget>().AsQueryable();
        public IQueryable<AuditLog> AuditLogs => Array.Empty<AuditLog>().AsQueryable();
        public IQueryable<SettingEntry> Settings => Array.Empty<SettingEntry>().AsQueryable();
        public IQueryable<PersonalAccessToken> PersonalAccessTokens => Array.Empty<PersonalAccessToken>().AsQueryable();
        public IQueryable<WebhookEvent> WebhookEvents => Array.Empty<WebhookEvent>().AsQueryable();

        public IQueryable<ApplicationWebhookConfiguration> ApplicationWebhookConfigurations =>
            Array.Empty<ApplicationWebhookConfiguration>().AsQueryable();

        public IQueryable<ApplicationPreview> ApplicationPreviews => Array.Empty<ApplicationPreview>().AsQueryable();

        public IQueryable<ProxyConfigurationVersion> ProxyConfigurationVersions =>
            ProxyConfigurationVersionItems.AsQueryable();

        public IQueryable<Certificate> Certificates => CertificateItems.AsQueryable();

        public IRepository<User, UserId> UserRepository => new ListRepository<User, UserId>(UserItems);
        public IRepository<Team, TeamId> TeamRepository => new ListRepository<Team, TeamId>(TeamItems);

        public IRepository<TeamInvitation, TeamInvitationId> TeamInvitationRepository =>
            new EmptyRepository<TeamInvitation, TeamInvitationId>();

        public IRepository<PersonalAccessToken, PersonalAccessTokenId> PersonalAccessTokenRepository =>
            new EmptyRepository<PersonalAccessToken, PersonalAccessTokenId>();

        public IRepository<Project, ProjectId> ProjectRepository =>
            new ListRepository<Project, ProjectId>(ProjectItems);

        public IRepository<EnvironmentEntity, EnvironmentId> EnvironmentRepository =>
            new ListRepository<EnvironmentEntity, EnvironmentId>(EnvironmentItems);

        public IRepository<Server, ServerId> ServerRepository => new ListRepository<Server, ServerId>(ServerItems);

        public IRepository<AppEntity, ApplicationId> ApplicationRepository =>
            new ListRepository<AppEntity, ApplicationId>(ApplicationItems);

        public IRepository<DatabaseResource, DatabaseResourceId> DatabaseResourceRepository =>
            new EmptyRepository<DatabaseResource, DatabaseResourceId>();

        public IRepository<ServiceResource, ServiceResourceId> ServiceResourceRepository =>
            new EmptyRepository<ServiceResource, ServiceResourceId>();

        public IRepository<BackupSchedule, BackupScheduleId> BackupScheduleRepository =>
            new EmptyRepository<BackupSchedule, BackupScheduleId>();

        public IRepository<BackupExecution, BackupExecutionId> BackupExecutionRepository =>
            new EmptyRepository<BackupExecution, BackupExecutionId>();

        public IRepository<Deployment, DeploymentId> DeploymentRepository =>
            new EmptyRepository<Deployment, DeploymentId>();

        public IRepository<SecretReference, SecretReferenceId> SecretReferenceRepository =>
            new EmptyRepository<SecretReference, SecretReferenceId>();

        public IRepository<SecretValue, SecretValueId> SecretValueRepository =>
            new EmptyRepository<SecretValue, SecretValueId>();

        public IRepository<EnvironmentVariable, EnvironmentVariableId> EnvironmentVariableRepository =>
            new EmptyRepository<EnvironmentVariable, EnvironmentVariableId>();

        public IRepository<RegistryCredential, RegistryCredentialId> RegistryCredentialRepository =>
            new EmptyRepository<RegistryCredential, RegistryCredentialId>();

        public IRepository<ServerStatusSnapshot, ServerStatusSnapshotId> ServerStatusSnapshotRepository =>
            new EmptyRepository<ServerStatusSnapshot, ServerStatusSnapshotId>();

        public IRepository<WebhookEvent, WebhookEventId> WebhookEventRepository =>
            new EmptyRepository<WebhookEvent, WebhookEventId>();

        public IRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId>
            ApplicationWebhookConfigurationRepository =>
            new EmptyRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId>();

        public IRepository<ApplicationPreview, ApplicationPreviewId> ApplicationPreviewRepository =>
            new EmptyRepository<ApplicationPreview, ApplicationPreviewId>();

        public IRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId>
            ProxyConfigurationVersionRepository =>
            new ListRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId>(ProxyConfigurationVersionItems);

        public IRepository<Certificate, CertificateId> CertificateRepository =>
            new ListRepository<Certificate, CertificateId>(CertificateItems);

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

    private sealed record RecurringRegistration(
        string RecurringJobId,
        Type JobType,
        string CronExpression);

    private sealed class TestRecurringJobScheduler : IRecurringJobScheduler
    {
        public List<RecurringRegistration> Registrations { get; } = [];

        public void AddOrUpdate<TJob>(
            string recurringJobId,
            Expression<Func<TJob, Task>> methodCall,
            string cronExpression)
        {
            Registrations.Add(new RecurringRegistration(recurringJobId, typeof(TJob), cronExpression));
        }
    }

    private sealed class TestBackgroundJobDispatcher : IBackgroundJobDispatcher
    {
        public List<string> Enqueued { get; } = [];

        public List<string> Scheduled { get; } = [];

        public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
        {
            var id = $"job-{Enqueued.Count + 1}";
            Enqueued.Add($"{typeof(TJob).Name}:{id}");
            return id;
        }

        public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay)
        {
            var id = $"scheduled-{Scheduled.Count + 1}";
            Scheduled.Add($"{typeof(TJob).Name}:{id}:{delay}");
            return id;
        }
    }

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
