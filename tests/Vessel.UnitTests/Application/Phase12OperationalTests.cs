using System.Linq.Expressions;
using System.Text;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Deployments;
using Vessel.Application.Docker;
using Vessel.Application.Jobs;
using Vessel.Application.Monitoring;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Proxy;
using Vessel.Application.Realtime;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Terminals;
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
using Vessel.Domain.Terminals;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.UnitTests.Application;

public sealed class Phase12OperationalTests
{
    [Fact]
    public void TerminalSession_TracksLifecycleResizeAndTerminalState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TerminalSession session = TerminalSession.Open(
            TeamId.New(),
            UserId.New(),
            ServerId.New(),
            TerminalTargetType.Server,
            null,
            "pwsh",
            120,
            32,
            now);

        session.MarkConnected(now.AddSeconds(1));
        session.Resize(100, 24, now.AddSeconds(2));
        session.RecordInput(now.AddSeconds(3));
        session.Close(now.AddSeconds(4));

        Assert.Equal(TerminalSessionStatus.Closed, session.Status);
        Assert.Equal(100, session.Columns);
        Assert.Equal(24, session.Rows);
        Assert.NotNull(session.EndedAt);
    }

    [Fact]
    public void TerminalSession_ClosingDoesNotAcceptInputResizeOrReconnect()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TerminalSession session = TerminalSession.Open(
            TeamId.New(),
            UserId.New(),
            ServerId.New(),
            TerminalTargetType.Server,
            null,
            "pwsh",
            120,
            32,
            now);

        session.MarkConnected(now.AddSeconds(1));
        session.BeginClose(now.AddSeconds(2));
        session.MarkConnected(now.AddSeconds(3));

        Assert.Equal(TerminalSessionStatus.Closing, session.Status);
        Assert.Throws<DomainException>(() => session.Resize(80, 24, now.AddSeconds(4)));
        Assert.Throws<DomainException>(() => session.RecordInput(now.AddSeconds(5)));
    }

    [Fact]
    public void DeploymentLogQuery_PaginatesFiltersAndRedacts()
    {
        Scenario scenario = Scenario.Create();
        Deployment deployment = Deployment.Queue(scenario.Application.Id, scenario.Server.Id, scenario.User.Id, null,
            scenario.Now);
        deployment.AddLogLine("stdout", "first", scenario.Now);
        deployment.AddLogLine("stderr", "password=sensitive", scenario.Now.AddSeconds(1));
        deployment.AddLogLine("stdout", "third", scenario.Now.AddSeconds(2));
        scenario.Db.DeploymentItems.Add(deployment);

        var service = new DeploymentQueryService(
            scenario.Db,
            new VesselAuthorizationService(scenario.Db),
            new TestRedactor());

        DeploymentLogPage page = service.GetLogs(
            scenario.User.Id,
            scenario.Team.Id,
            deployment.Id,
            new DeploymentLogQuery(AfterSequence: 1, Search: "password", Stream: "stderr", PageSize: 1));

        Assert.Single(page.Entries);
        Assert.True(page.HasMore == false);
        Assert.Equal(2, page.Entries[0].Sequence);
        Assert.DoesNotContain("sensitive", page.Entries[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminalSessionManager_AuthorizesAuditsAndDelegatesBridge()
    {
        Scenario scenario = Scenario.Create();
        var bridge = new TestTerminalBridge();
        var audit = new TestAuditWriter();
        var realtime = new TestRealtimeNotifier();
        var manager = new TerminalSessionManager(
            scenario.Db,
            new VesselAuthorizationService(scenario.Db),
            bridge,
            realtime,
            audit,
            new TestRedactor(),
            TimeProvider.System);

        TerminalSessionDetails details = await manager.OpenAsync(
            scenario.User.Id,
            scenario.Team.Id,
            new OpenTerminalSessionRequest(scenario.Server.Id.Value),
            cancellationToken: TestContext.Current.CancellationToken);
        await manager.SendInputAsync(
            scenario.User.Id,
            scenario.Team.Id,
            new TerminalSessionId(details.Id),
            "é\n",
            TestContext.Current.CancellationToken);
        await manager.ResizeAsync(
            scenario.User.Id,
            scenario.Team.Id,
            new TerminalSessionId(details.Id),
            80,
            24,
            TestContext.Current.CancellationToken);
        await manager.CloseAsync(
            scenario.User.Id,
            scenario.Team.Id,
            new TerminalSessionId(details.Id),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, bridge.OpenCount);
        Assert.Equal(["é\n"], bridge.Inputs);
        AuditRecord inputAudit = Assert.Single(audit.Records, record => record.Action == AuditActions.TerminalSessionInput);
        Assert.Equal(Encoding.UTF8.GetByteCount("é\n"), inputAudit.Metadata["bytes"]);
        Assert.Contains(audit.Records, record => record.Action == AuditActions.TerminalSessionOpened);
        Assert.Contains(audit.Records, record => record.Action == AuditActions.TerminalSessionClosed);
        Assert.Contains(realtime.Messages, message => message.Message.Type == "terminal.status");
    }

    [Fact]
    public async Task ServerHealthPolling_RecordsSnapshotAndPublishesStatus()
    {
        Scenario scenario = Scenario.Create();
        var runtime = new TestContainerRuntime();
        var realtime = new TestRealtimeNotifier();
        var proxyStatus = new TestProxyStatusProbe { Healthy = false };
        var service = new ServerHealthPollingService(
            scenario.Db,
            new VesselAuthorizationService(scenario.Db),
            runtime,
            proxyStatus,
            realtime,
            new TestAuditWriter(),
            TimeProvider.System,
            new RecordingNotificationEventPublisher(),
            new TestSshClient());

        ServerHealthPollingResult result =
            await service.PollAsync(scenario.Server.Id, TestContext.Current.CancellationToken);

        Assert.True(result.RuntimeReachable);
        Assert.Equal(ServerStatus.Reachable, scenario.Server.Status);
        ServerStatusSnapshot snapshot = Assert.Single(scenario.Db.ServerStatusSnapshotItems);
        // Proxy health comes from the real container probe, not from proxy configuration history.
        Assert.False(snapshot.ProxyHealthy);
        Assert.Equal(1, proxyStatus.Calls);
        Assert.Contains(realtime.Messages, message => message.Message.Type == "server.health");
    }

    private sealed class TestProxyStatusProbe : IProxyStatusProbe
    {
        public bool Healthy { get; init; }

        public int Calls { get; private set; }

        public Task<bool> IsHealthyAsync(Server server, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Healthy);
        }
    }

    [Fact]
    public async Task ServerHealthPolling_NotifiesOnlyOnReachabilityTransitions()
    {
        Scenario scenario = Scenario.Create();
        var runtime = new TestContainerRuntime();
        var notifications = new RecordingNotificationEventPublisher();
        var service = new ServerHealthPollingService(
            scenario.Db,
            new VesselAuthorizationService(scenario.Db),
            runtime,
            new TestProxyStatusProbe(),
            new TestRealtimeNotifier(),
            new TestAuditWriter(),
            TimeProvider.System,
            notifications,
            new TestSshClient());
        CancellationToken token = TestContext.Current.CancellationToken;

        await service.PollAsync(scenario.Server.Id, token); // Unknown -> Reachable: no alert.
        Assert.Empty(notifications.Published);

        runtime.Fail = true;
        await service.PollAsync(scenario.Server.Id, token); // Reachable -> Unreachable: alert.
        await service.PollAsync(scenario.Server.Id, token); // still Unreachable: de-duplicated.
        TeamNotification down = Assert.Single(notifications.Published);
        Assert.Equal(NotificationEventTypes.ServerUnreachable, down.Notification.EventType);
        Assert.Equal(scenario.Team.Id, down.TeamId);
        Assert.Equal(NotificationSeverity.Critical, down.Notification.Severity);

        runtime.Fail = false;
        await service.PollAsync(scenario.Server.Id, token); // Unreachable -> Reachable: recovery alert.
        await service.PollAsync(scenario.Server.Id, token);
        Assert.Equal(2, notifications.Published.Count);
        Assert.Equal(NotificationEventTypes.ServerReachable, notifications.Published[1].Notification.EventType);
    }

    [Fact]
    public async Task DeploymentRunnerNotifiesFailedDeploymentAfterTerminalState()
    {
        Scenario scenario = Scenario.Create();
        scenario.Server.ChangeStatus(ServerStatus.Unreachable, scenario.Now);
        Deployment deployment = Deployment.Queue(scenario.Application.Id, scenario.Server.Id, scenario.User.Id,
            "0123456789abcdef0123", scenario.Now);
        scenario.Db.DeploymentItems.Add(deployment);
        var notifications = new RecordingNotificationEventPublisher();
        DeploymentRunner runner = CreateRunner(scenario, new InMemoryDistributedLockManager(TimeProvider.System),
            notifications);

        await runner.RunAsync(deployment.Id, TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentStatus.Failed, deployment.Status);
        TeamNotification notification = Assert.Single(notifications.Published);
        Assert.Equal(scenario.Team.Id, notification.TeamId);
        Assert.Equal(NotificationEventTypes.DeploymentFailed, notification.Notification.EventType);
        Assert.Equal(deployment.Id.Value.ToString("D"), notification.Notification.TargetId);
        Assert.Contains("app", notification.Notification.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("unreachable", notification.Notification.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeploymentRunnerSurvivesNotificationBuildFailure()
    {
        Scenario scenario = Scenario.Create();
        scenario.Server.ChangeStatus(ServerStatus.Unreachable, scenario.Now);
        Deployment deployment = Deployment.Queue(scenario.Application.Id, scenario.Server.Id, scenario.User.Id,
            null, scenario.Now);
        scenario.Db.DeploymentItems.Add(deployment);

        // A publisher that swallows builder failures (the production contract) must not affect the runner.
        await CreateRunner(scenario, new InMemoryDistributedLockManager(TimeProvider.System),
                new SwallowingPublisher())
            .RunAsync(deployment.Id, TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentStatus.Failed, deployment.Status);
    }

    private static DeploymentRunner CreateRunner(Scenario scenario, IDistributedLockManager locks,
        INotificationEventPublisher notifications)
    {
        var authorization = new VesselAuthorizationService(scenario.Db);
        var proxyLifecycle = new ProxyLifecycleService(scenario.Db, authorization, locks, [],
            ThrowingProxy.Create<IProxyContainerRuntime>(), ThrowingProxy.Create<IProxyRuntimeRenderer>(),
            new TestAuditWriter(), new TestRedactor(), TimeProvider.System);
        var proxy = new Vessel.Application.Proxy.ProxyConfigurationService(scenario.Db, authorization,
            ThrowingProxy.Create<Vessel.Application.Proxy.IProxyProvider>(), proxyLifecycle, locks,
            new TestAuditWriter(), new TestRedactor(), TimeProvider.System, notifications);
        return new DeploymentRunner(scenario.Db, ThrowingProxy.Create<Vessel.Application.Git.IGitClient>(),
            new TestContainerRuntime(), ThrowingProxy.Create<IDeploymentWorkspaceManager>(), locks,
            ThrowingProxy.Create<ISecretVault>(), new TestRedactor(), new TestRealtimeNotifier(), proxy,
            proxyLifecycle, new TestAuditWriter(), TimeProvider.System, notifications, authorization);
    }

    private sealed class SwallowingPublisher : INotificationEventPublisher
    {
        public Task PublishAsync(TeamId teamId, RaiseNotificationRequest notification,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task PublishAsync(Func<TeamNotification?> build, CancellationToken cancellationToken = default)
        {
            try
            {
                _ = build();
            }
            catch (InvalidOperationException)
            {
                // Mirrors BackgroundNotificationEventPublisher: logged and swallowed.
            }

            return Task.CompletedTask;
        }
    }

    public class ThrowingProxy : System.Reflection.DispatchProxy
    {
        public static T Create<T>() where T : class
        {
            return Create<T, ThrowingProxy>();
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            throw new NotSupportedException($"{targetMethod?.Name} is not expected in this test.");
        }
    }

    [Fact]
    public void DeploymentLogRecurringJobs_RegisterSchedulesRetentionPrune()
    {
        var scheduler = new TestRecurringJobScheduler();

        DeploymentLogRecurringJobs.Register(scheduler);

        RecurringRegistration registration = Assert.Single(scheduler.Registrations);
        Assert.Equal(DeploymentLogRecurringJobs.PruneJobId, registration.RecurringJobId);
        Assert.Equal(typeof(DeploymentLogRetentionJob), registration.JobType);
        Assert.Equal(DeploymentLogRecurringJobs.PruneCronExpression, registration.CronExpression);
    }

    private sealed class Scenario
    {
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public TestDbContext Db { get; } = new();
        public User User { get; private set; } = null!;
        public Team Team { get; private set; } = null!;
        public Project Project { get; private set; } = null!;
        public EnvironmentEntity Environment { get; private set; } = null!;
        public Server Server { get; private set; } = null!;
        public AppEntity Application { get; private set; } = null!;

        public static Scenario Create()
        {
            var scenario = new Scenario();
            User user = User.Create(new DisplayName("Alice"), new EmailAddress("alice@example.com"), scenario.Now);
            // The scenario server is host-local (control-plane host), which requires an instance administrator.
            user.GrantInstanceAdmin(scenario.Now);
            Team team = Team.Create(new DisplayName("Team"), user.Id, false, scenario.Now);
            Project project = Project.Create(team.Id, new ResourceName("project"), scenario.Now);
            EnvironmentEntity environment = EnvironmentEntity.CreateProduction(project.Id, scenario.Now);
            Server server = Server.Create(team.Id, new ResourceName("local"),
                new ServerAddress("localhost", new PortNumber(22), null),
                ServerConnectionType.Local, ContainerRuntimeKind.Docker, scenario.Now);
            AppEntity application = AppEntity.Create(
                environment.Id,
                server.Id,
                new ResourceName("app"),
                new GitSource(new RepositoryUrl("https://example.com/repo.git"), "main"),
                BuildConfiguration.Default(ApplicationBuildPack.Dockerfile),
                scenario.Now);

            scenario.Db.UserItems.Add(user);
            scenario.Db.TeamItems.Add(team);
            scenario.Db.ProjectItems.Add(project);
            scenario.Db.EnvironmentItems.Add(environment);
            scenario.Db.ServerItems.Add(server);
            scenario.Db.ApplicationItems.Add(application);

            scenario.User = user;
            scenario.Team = team;
            scenario.Project = project;
            scenario.Environment = environment;
            scenario.Server = server;
            scenario.Application = application;
            return scenario;
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
        public List<Deployment> DeploymentItems { get; } = [];
        public List<TerminalSession> TerminalSessionItems { get; } = [];
        public List<ServerStatusSnapshot> ServerStatusSnapshotItems { get; } = [];

        public IQueryable<User> Users => UserItems.AsQueryable();
        public IQueryable<Team> Teams => TeamItems.AsQueryable();
        public IQueryable<TeamMembership> TeamMemberships => TeamItems.SelectMany(team => team.Memberships).AsQueryable();
        public IQueryable<TeamInvitation> TeamInvitations => Empty<TeamInvitation>();
        public IQueryable<Project> Projects => ProjectItems.AsQueryable();
        public IQueryable<EnvironmentEntity> Environments => EnvironmentItems.AsQueryable();
        public IQueryable<Server> Servers => ServerItems.AsQueryable();
        public IQueryable<AppEntity> Applications => ApplicationItems.AsQueryable();
        public IQueryable<ApplicationDomain> ApplicationDomains => ApplicationItems.SelectMany(application => application.Domains).AsQueryable();
        public IQueryable<DatabaseResource> DatabaseResources => Empty<DatabaseResource>();
        public IQueryable<ServiceResource> ServiceResources => Empty<ServiceResource>();
        public IQueryable<BackupSchedule> BackupSchedules => Empty<BackupSchedule>();
        public IQueryable<BackupExecution> BackupExecutions => Empty<BackupExecution>();
        public IQueryable<Deployment> Deployments => DeploymentItems.AsQueryable();
        public IQueryable<SecretReference> SecretReferences => Empty<SecretReference>();
        public IQueryable<SecretValue> SecretValues => Empty<SecretValue>();
        public IQueryable<EnvironmentVariable> EnvironmentVariables => Empty<EnvironmentVariable>();
        public IQueryable<RegistryCredential> RegistryCredentials => Empty<RegistryCredential>();
        public IQueryable<ServerStatusSnapshot> ServerStatusSnapshots => ServerStatusSnapshotItems.AsQueryable();
        public IQueryable<NotificationTarget> NotificationTargets => Empty<NotificationTarget>();
        public IQueryable<NotificationEvent> NotificationEvents => Empty<NotificationEvent>();
        public IQueryable<InAppNotification> InAppNotifications => Empty<InAppNotification>();
        public IQueryable<NotificationDeliveryAttempt> NotificationDeliveryAttempts => Empty<NotificationDeliveryAttempt>();
        public IQueryable<AuditLog> AuditLogs => Empty<AuditLog>();
        public IQueryable<SettingEntry> Settings => Empty<SettingEntry>();
        public IQueryable<PersonalAccessToken> PersonalAccessTokens => Empty<PersonalAccessToken>();
        public IQueryable<WebhookEvent> WebhookEvents => Empty<WebhookEvent>();
        public IQueryable<ApplicationWebhookConfiguration> ApplicationWebhookConfigurations => Empty<ApplicationWebhookConfiguration>();
        public IQueryable<ApplicationPreview> ApplicationPreviews => Empty<ApplicationPreview>();
        public IQueryable<ProxyConfigurationVersion> ProxyConfigurationVersions => Empty<ProxyConfigurationVersion>();
        public IQueryable<Certificate> Certificates => Empty<Certificate>();
        public IQueryable<ServerProxySettings> ServerProxySettings => Empty<ServerProxySettings>();
        public IQueryable<TerminalSession> TerminalSessions => TerminalSessionItems.AsQueryable();

        public IRepository<User, UserId> UserRepository => new ListRepository<User, UserId>(UserItems);
        public IRepository<Team, TeamId> TeamRepository => new ListRepository<Team, TeamId>(TeamItems);
        public IRepository<TeamInvitation, TeamInvitationId> TeamInvitationRepository => EmptyRepo<TeamInvitation, TeamInvitationId>();
        public IRepository<PersonalAccessToken, PersonalAccessTokenId> PersonalAccessTokenRepository => EmptyRepo<PersonalAccessToken, PersonalAccessTokenId>();
        public IRepository<Project, ProjectId> ProjectRepository => new ListRepository<Project, ProjectId>(ProjectItems);
        public IRepository<EnvironmentEntity, EnvironmentId> EnvironmentRepository => new ListRepository<EnvironmentEntity, EnvironmentId>(EnvironmentItems);
        public IRepository<Server, ServerId> ServerRepository => new ListRepository<Server, ServerId>(ServerItems);
        public IRepository<AppEntity, Vessel.Domain.ApplicationId> ApplicationRepository => new ListRepository<AppEntity, Vessel.Domain.ApplicationId>(ApplicationItems);
        public IRepository<DatabaseResource, DatabaseResourceId> DatabaseResourceRepository => EmptyRepo<DatabaseResource, DatabaseResourceId>();
        public IRepository<ServiceResource, ServiceResourceId> ServiceResourceRepository => EmptyRepo<ServiceResource, ServiceResourceId>();
        public IRepository<BackupSchedule, BackupScheduleId> BackupScheduleRepository => EmptyRepo<BackupSchedule, BackupScheduleId>();
        public IRepository<BackupExecution, BackupExecutionId> BackupExecutionRepository => EmptyRepo<BackupExecution, BackupExecutionId>();
        public IRepository<Deployment, DeploymentId> DeploymentRepository => new ListRepository<Deployment, DeploymentId>(DeploymentItems);
        public IRepository<SecretReference, SecretReferenceId> SecretReferenceRepository => EmptyRepo<SecretReference, SecretReferenceId>();
        public IRepository<SecretValue, SecretValueId> SecretValueRepository => EmptyRepo<SecretValue, SecretValueId>();
        public IRepository<EnvironmentVariable, EnvironmentVariableId> EnvironmentVariableRepository => EmptyRepo<EnvironmentVariable, EnvironmentVariableId>();
        public IRepository<RegistryCredential, RegistryCredentialId> RegistryCredentialRepository => EmptyRepo<RegistryCredential, RegistryCredentialId>();
        public IRepository<ServerStatusSnapshot, ServerStatusSnapshotId> ServerStatusSnapshotRepository => new ListRepository<ServerStatusSnapshot, ServerStatusSnapshotId>(ServerStatusSnapshotItems);
        public IRepository<WebhookEvent, WebhookEventId> WebhookEventRepository => EmptyRepo<WebhookEvent, WebhookEventId>();
        public IRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId> ApplicationWebhookConfigurationRepository => EmptyRepo<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId>();
        public IRepository<ApplicationPreview, ApplicationPreviewId> ApplicationPreviewRepository => EmptyRepo<ApplicationPreview, ApplicationPreviewId>();
        public IRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId> ProxyConfigurationVersionRepository => EmptyRepo<ProxyConfigurationVersion, ProxyConfigurationVersionId>();
        public IRepository<Certificate, CertificateId> CertificateRepository => EmptyRepo<Certificate, CertificateId>();
        public IRepository<ServerProxySettings, ServerId> ServerProxySettingsRepository => EmptyRepo<ServerProxySettings, ServerId>();
        public IRepository<TerminalSession, TerminalSessionId> TerminalSessionRepository => new ListRepository<TerminalSession, TerminalSessionId>(TerminalSessionItems);

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);

        private static IQueryable<T> Empty<T>() => Array.Empty<T>().AsQueryable();
        private static IRepository<TEntity, TId> EmptyRepo<TEntity, TId>()
            where TEntity : Entity<TId>
            where TId : notnull => new EmptyRepository<TEntity, TId>();
    }

    private sealed class ListRepository<TEntity, TId>(List<TEntity> items) : IRepository<TEntity, TId>
        where TEntity : Entity<TId>
        where TId : notnull
    {
        public Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(items.SingleOrDefault(item => EqualityComparer<TId>.Default.Equals(item.Id, id)));
        public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            items.Add(entity);
            return Task.CompletedTask;
        }
        public void Remove(TEntity entity) => items.Remove(entity);
    }

    private sealed class EmptyRepository<TEntity, TId> : IRepository<TEntity, TId>
        where TEntity : Entity<TId>
        where TId : notnull
    {
        public Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default) => Task.FromResult<TEntity?>(null);
        public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Remove(TEntity entity) => throw new NotSupportedException();
    }

    private sealed class TestRedactor : ISecretRedactor
    {
        public string Redact(string value, RedactionContext? context = null) =>
            value.Replace("sensitive", "<REDACTED>", StringComparison.Ordinal)
                .Replace("password=<REDACTED>", "password=<REDACTED>", StringComparison.Ordinal);
        public byte[] RedactUtf8(byte[] value, RedactionContext? context = null) => value;
    }

    private sealed class TestAuditWriter : IAuditWriter
    {
        public List<AuditRecord> Records { get; } = [];
        public Task RecordAsync(TeamId? teamId, UserId? actorUserId, string action, AuditTarget target,
            string? correlationId, IReadOnlyDictionary<string, object?> metadata,
            CancellationToken cancellationToken = default)
        {
            Records.Add(new AuditRecord(action, metadata));
            return Task.CompletedTask;
        }
    }

    private sealed record AuditRecord(string Action, IReadOnlyDictionary<string, object?> Metadata);

    private sealed class TestRealtimeNotifier : IRealtimeNotifier
    {
        public List<(RealtimeGroup Group, RealtimeMessage Message)> Messages { get; } = [];
        public Task PublishAsync(RealtimeGroup group, RealtimeMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add((group, message));
            return Task.CompletedTask;
        }
    }

    private sealed class TestTerminalBridge : ITerminalProcessBridge
    {
        public int OpenCount { get; private set; }
        public List<string> Inputs { get; } = [];
        public Task OpenAsync(TerminalBridgeOpenRequest request, Func<TerminalOutputChunk, CancellationToken, Task> onOutput,
            Func<TerminalSessionId, string?, CancellationToken, Task> onExit,
            CancellationToken cancellationToken = default)
        {
            OpenCount++;
            return onOutput(new TerminalOutputChunk(request.SessionId.Value, "stdout", "ready", DateTimeOffset.UtcNow),
                cancellationToken);
        }
        public Task SendInputAsync(TerminalSessionId sessionId, string data, CancellationToken cancellationToken = default)
        {
            Inputs.Add(data);
            return Task.CompletedTask;
        }
        public Task ResizeAsync(TerminalBridgeResizeRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync(TerminalSessionId sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestContainerRuntime : IContainerRuntimeClient
    {
        public bool Fail { get; set; }

        public Task<ContainerRuntimeInfo> GetInfoAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default) =>
            Fail
                ? throw new InvalidOperationException("runtime unavailable")
                : Task.FromResult(new ContainerRuntimeInfo(ContainerRuntimeProvider.Docker, "1", "1", "linux", "x64"));
        public Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(ContainerRuntimeTarget target, bool all, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContainerSummary>>([new ContainerSummary("1", ["app"], "image", "running", "Up", new Dictionary<string, string>())]);
        public Task<string> InspectContainerAsync(ContainerRuntimeTarget target, string containerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ImageSummary>> ListImagesAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnsureNetworkAsync(ContainerRuntimeTarget target, string name, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<Vessel.Application.Processes.ProcessOutputLine> BuildImageAsync(ContainerRuntimeTarget target, DockerBuildCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ContainerEvent> StreamEventsAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<Vessel.Application.Processes.ProcessOutputLine> RunComposeAsync(ContainerRuntimeTarget target, ComposeCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestSshClient : Vessel.Application.Ssh.ISshClient
    {
        public Task<bool> TestConnectionAsync(Vessel.Application.Ssh.SshConnectionOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Host-local servers must not be probed over SSH.");
        public Task<Vessel.Application.Ssh.SshCommandResult> RunCommandAsync(Vessel.Application.Ssh.SshCommandRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Vessel.Application.Ssh.SshCommandResult> RunCommandWithInputAsync(Vessel.Application.Ssh.SshCommandRequest request, Stream standardInput, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UploadAsync(Vessel.Application.Ssh.SshFileTransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadAsync(Vessel.Application.Ssh.SshFileTransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed record RecurringRegistration(
        string RecurringJobId,
        Type JobType,
        string MethodName,
        string CronExpression);

    private sealed class TestRecurringJobScheduler : IRecurringJobScheduler
    {
        public List<RecurringRegistration> Registrations { get; } = [];

        public void AddOrUpdate<TJob>(
            string recurringJobId,
            Expression<Func<TJob, Task>> methodCall,
            string cronExpression)
        {
            MethodCallExpression method = (MethodCallExpression)methodCall.Body;
            Registrations.Add(new RecurringRegistration(
                recurringJobId,
                typeof(TJob),
                method.Method.Name,
                cronExpression));
        }
    }
}
