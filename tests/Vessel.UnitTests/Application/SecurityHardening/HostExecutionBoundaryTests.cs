using Microsoft.Extensions.Options;
using Vessel.Application.Auth;
using Vessel.Application.Authorization;
using Vessel.Application.Deployments;
using Vessel.Application.Monitoring;
using Vessel.Application.Resources;
using Vessel.Application.Security;
using Vessel.Application.Terminals;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Projects;
using Vessel.Domain.Servers;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.UnitTests.Application.SecurityHardening;

/// <summary>SEC-03 / SRV-01: host-local execution is instance-admin only and remote targets never run locally.</summary>
public sealed class HostExecutionBoundaryTests
{
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly SecurityTestDbContext _db = new();

    [Theory]
    [InlineData(ServerConnectionType.Local)]
    [InlineData(ServerConnectionType.DockerSocket)]
    public async Task NonAdminOwner_CannotCreateHostLocalServer(ServerConnectionType connectionType)
    {
        (User owner, Team team) = AddUser("owner@example.com", instanceAdmin: false);
        ResourceManagementService resources = Resources();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => resources.CreateServerAsync(owner.Id, team.Id,
            new CreateServerRequest("host", null, "localhost", 22, null, connectionType, ContainerRuntimeKind.Docker, null),
            TestContext.Current.CancellationToken));
        Assert.Empty(_db.ServerItems);
    }

    [Fact]
    public async Task NonAdminOwner_CanCreateSshServer_AndInstanceAdminCanCreateLocal()
    {
        (User owner, Team team) = AddUser("owner@example.com", instanceAdmin: false);
        (User admin, Team adminTeam) = AddUser("admin@example.com", instanceAdmin: true);
        ResourceManagementService resources = Resources();

        await resources.CreateServerAsync(owner.Id, team.Id,
            new CreateServerRequest("remote", null, "203.0.113.10", 22, "deploy", ServerConnectionType.Ssh,
                ContainerRuntimeKind.Docker, null), TestContext.Current.CancellationToken);
        await resources.CreateServerAsync(admin.Id, adminTeam.Id,
            new CreateServerRequest("host", null, "localhost", 22, null, ServerConnectionType.Local,
                ContainerRuntimeKind.Docker, null), TestContext.Current.CancellationToken);

        Assert.Equal(2, _db.ServerItems.Count);
    }

    [Fact]
    public async Task NonAdminOwner_CannotOpenHostTerminal()
    {
        (User owner, Team team) = AddUser("owner@example.com", instanceAdmin: false);
        Server server = AddServer(team, ServerConnectionType.Local);
        var bridge = new RecordingTerminalBridge();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Terminals(bridge).OpenAsync(owner.Id, team.Id,
            new OpenTerminalSessionRequest(server.Id.Value), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(bridge.Opened);
        Assert.Empty(_db.TerminalSessionItems);
    }

    [Fact]
    public async Task SshServerTerminal_IsRejectedEvenForInstanceAdmin()
    {
        (User admin, Team team) = AddUser("admin@example.com", instanceAdmin: true);
        Server server = AddServer(team, ServerConnectionType.Ssh, "203.0.113.10");
        var bridge = new RecordingTerminalBridge();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Terminals(bridge).OpenAsync(admin.Id, team.Id, new OpenTerminalSessionRequest(server.Id.Value),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TerminalTargetPolicy.RemoteNotSupportedMessage, error.Message);
        Assert.Empty(bridge.Opened);
    }

    [Fact]
    public async Task InstanceAdmin_OpensHostTerminalWithLocalTargetConnection()
    {
        (User admin, Team team) = AddUser("admin@example.com", instanceAdmin: true);
        Server server = AddServer(team, ServerConnectionType.Local);
        var bridge = new RecordingTerminalBridge();

        await Terminals(bridge).OpenAsync(admin.Id, team.Id, new OpenTerminalSessionRequest(server.Id.Value),
            cancellationToken: TestContext.Current.CancellationToken);

        TerminalBridgeOpenRequest request = Assert.Single(bridge.Opened);
        Assert.Equal(ServerConnectionType.Local, request.TargetConnection);
    }

    [Theory]
    [InlineData(true, ServerStatus.Reachable)]
    [InlineData(false, ServerStatus.Unreachable)]
    public async Task HealthPolling_SshServer_ProbesOverSshAndNeverUsesLocalRuntime(bool sshReachable,
        ServerStatus expected)
    {
        (_, Team team) = AddUser("owner@example.com", instanceAdmin: false);
        Server server = AddServer(team, ServerConnectionType.Ssh, "203.0.113.10", user: "deploy", port: 2222);
        var runtime = new CountingContainerRuntime();
        var ssh = new RecordingSshClient(sshReachable);

        ServerHealthPollingResult result = await Polling(runtime, ssh)
            .PollAsync(server.Id, TestContext.Current.CancellationToken);

        Assert.Equal(0, runtime.Calls);
        SshProbe(ssh, "203.0.113.10", 2222, "deploy");
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected, server.Status);
        Assert.False(result.RuntimeReachable);
    }

    [Fact]
    public async Task HealthPolling_RejectsOptionInjectionHostWithoutRunningSsh()
    {
        (_, Team team) = AddUser("owner@example.com", instanceAdmin: false);
        Server server = AddServer(team, ServerConnectionType.Ssh, "-oproxycommand=touch-pwned");
        var ssh = new RecordingSshClient(true);

        ServerHealthPollingResult result = await Polling(new CountingContainerRuntime(), ssh)
            .PollAsync(server.Id, TestContext.Current.CancellationToken);

        Assert.Empty(ssh.Probes);
        Assert.Equal(ServerStatus.Unreachable, result.Status);
    }

    [Fact]
    public async Task HealthPolling_HostLocalServerWithoutInstanceAdminTeam_IsNotProbed()
    {
        (_, Team team) = AddUser("owner@example.com", instanceAdmin: false);
        Server server = AddServer(team, ServerConnectionType.Local);
        var runtime = new CountingContainerRuntime();

        ServerHealthPollingResult result = await Polling(runtime, new RecordingSshClient(true))
            .PollAsync(server.Id, TestContext.Current.CancellationToken);

        Assert.Equal(0, runtime.Calls);
        Assert.Equal(ServerStatus.Unknown, result.Status);
        Assert.Equal(HostLocalExecutionGuard.UnattendedNotAuthorizedMessage, result.Reason);
    }

    [Fact]
    public async Task ConnectivityTest_ActuallyProbesAndDoesNotFabricateReachable()
    {
        (User owner, Team team) = AddUser("owner@example.com", instanceAdmin: false);
        Server server = AddServer(team, ServerConnectionType.Ssh, "203.0.113.10");
        var runtime = new CountingContainerRuntime();
        var ssh = new RecordingSshClient(false);

        ServerConnectivityResult result = await Resources(runtime, ssh).TestServerConnectivityAsync(owner.Id, team.Id,
            server.Id, TestContext.Current.CancellationToken);

        Assert.False(result.Reachable);
        Assert.Single(ssh.Probes);
        Assert.Equal(0, runtime.Calls);
        ServerStatusSnapshot snapshot = Assert.Single(_db.ServerStatusSnapshotItems);
        Assert.Equal(ServerStatus.Unreachable, snapshot.Status);
        Assert.False(snapshot.ProxyHealthy);
    }

    [Fact]
    public async Task ConnectivityTest_HostLocalServerRequiresInstanceAdmin()
    {
        (User admin, Team team) = AddUser("admin@example.com", instanceAdmin: true);
        Server server = AddServer(team, ServerConnectionType.Local);
        (User member, _) = AddUser("member@example.com", instanceAdmin: false);
        team.AddMember(member.Id, TeamRole.Admin, _now);
        var runtime = new CountingContainerRuntime();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Resources(runtime).TestServerConnectivityAsync(
            member.Id, team.Id, server.Id, TestContext.Current.CancellationToken));
        Assert.Equal(0, runtime.Calls);

        ServerConnectivityResult result = await Resources(runtime).TestServerConnectivityAsync(admin.Id, team.Id,
            server.Id, TestContext.Current.CancellationToken);
        Assert.True(result.Reachable);
        Assert.True(runtime.Calls > 0);
    }

    [Fact]
    public async Task NonAdmin_CannotStartDeploymentOnHostLocalServer()
    {
        (User admin, Team team) = AddUser("admin@example.com", instanceAdmin: true);
        (User member, _) = AddUser("member@example.com", instanceAdmin: false);
        team.AddMember(member.Id, TeamRole.Admin, _now);
        Server server = AddServer(team, ServerConnectionType.Local);
        AppEntity application = AddApplication(team, server);
        var jobs = new RecordingJobDispatcher();
        var queue = new Vessel.Application.Deployments.Queue.DeploymentQueueService(_db,
            new InMemoryDistributedLockManager(TimeProvider.System), jobs,
            new Vessel.Application.Deployments.Queue.DeploymentCancellationRegistry(), new NullRealtimeNotifier(),
            TimeProvider.System);
        var starter = new StartDeploymentService(_db, new VesselAuthorizationService(_db), queue,
            new RecordingAuditWriter(), TimeProvider.System);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => starter.StartAsync(member.Id, team.Id,
            new StartDeploymentRequest(application.Id.Value), TestContext.Current.CancellationToken));
        Assert.Equal(0, jobs.Enqueued);

        await starter.StartAsync(admin.Id, team.Id, new StartDeploymentRequest(application.Id.Value),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, jobs.Enqueued);
    }

    [Fact]
    public async Task Registration_FirstUserIsInstanceAdmin_LaterSelfRegistrationIsClosedByDefault()
    {
        var auth = new VesselAuthenticationService(_db, new PlainPasswordHasher(), new FixedTokenGenerator(),
            new TotpService(new FixedTokenGenerator()), new RecordingAuditWriter(), Options.Create(new AuthOptions()),
            new FakeSecretCipher("v1"));

        await auth.RegisterAsync("Root", "root@example.com", "Correct-Horse-Battery-9", null,
            TestContext.Current.CancellationToken);
        // IAM-06: once the bootstrap account exists, self-registration is closed unless an instance admin opens it.
        await Assert.ThrowsAsync<Vessel.Application.Instance.RegistrationClosedException>(() => auth.RegisterAsync(
            "Other", "other@example.com", "Correct-Horse-Battery-9", null, TestContext.Current.CancellationToken));

        Assert.True(_db.UserItems.Single(user => user.Email.Value == "root@example.com").IsInstanceAdmin);
        Assert.DoesNotContain(_db.UserItems, user => user.Email.Value == "other@example.com");
    }

    private static void SshProbe(RecordingSshClient ssh, string host, int port, string user)
    {
        var probe = Assert.Single(ssh.Probes);
        Assert.Equal(host, probe.Host);
        Assert.Equal(port, probe.Port);
        Assert.Equal(user, probe.UserName);
        Assert.Equal(Vessel.Application.Ssh.SshHostIdentityPolicy.RequireKnownHost, probe.HostIdentityPolicy);
    }

    private (User User, Team Team) AddUser(string email, bool instanceAdmin)
    {
        User user = User.Create(new DisplayName(email.Split('@')[0]), new EmailAddress(email), _now);
        if (instanceAdmin) user.GrantInstanceAdmin(_now);
        Team team = Team.Create(new DisplayName(email + " team"), user.Id, true, _now);
        _db.UserItems.Add(user);
        _db.TeamItems.Add(team);
        return (user, team);
    }

    private Server AddServer(Team team, ServerConnectionType connectionType, string host = "localhost",
        string? user = null, int port = 22)
    {
        Server server = Server.Create(team.Id, new ResourceName("server-" + _db.ServerItems.Count),
            new ServerAddress(host, new PortNumber(port), user), connectionType, ContainerRuntimeKind.Docker, _now);
        _db.ServerItems.Add(server);
        return server;
    }

    private AppEntity AddApplication(Team team, Server server)
    {
        Project project = Project.Create(team.Id, new ResourceName("project"), _now);
        EnvironmentEntity environment = EnvironmentEntity.CreateProduction(project.Id, _now);
        AppEntity application = AppEntity.Create(environment.Id, server.Id, new ResourceName("app"),
            new GitSource(new RepositoryUrl("https://example.com/repo.git"), "main"),
            BuildConfiguration.Default(ApplicationBuildPack.Dockerfile), _now);
        _db.ProjectItems.Add(project);
        _db.EnvironmentItems.Add(environment);
        _db.ApplicationItems.Add(application);
        return application;
    }

    private ServerHealthPollingService Polling(CountingContainerRuntime runtime, RecordingSshClient ssh) =>
        new(_db, new VesselAuthorizationService(_db), runtime, new FixedProxyStatusProbe(true), new NullRealtimeNotifier(),
            new RecordingAuditWriter(), TimeProvider.System, new RecordingNotificationEventPublisher(), ssh);

    private ResourceManagementService Resources(CountingContainerRuntime? runtime = null, RecordingSshClient? ssh = null) =>
        new(_db, new VesselAuthorizationService(_db), new UnavailableSecretVault(), new RecordingAuditWriter(),
            TimeProvider.System, Polling(runtime ?? new CountingContainerRuntime(), ssh ?? new RecordingSshClient(false)));

    private TerminalSessionManager Terminals(RecordingTerminalBridge bridge) =>
        new(_db, new VesselAuthorizationService(_db), bridge, new NullRealtimeNotifier(), new RecordingAuditWriter(),
            new PassthroughRedactor(), TimeProvider.System);

    private sealed class PlainPasswordHasher : IPasswordHasher
    {
        public string HashPassword(string password) => "hash:" + password;
        public bool VerifyPassword(string passwordHash, string password) => passwordHash == "hash:" + password;
    }

    private sealed class FixedTokenGenerator : ITokenGenerator
    {
        private int _counter;
        public string GenerateUrlSafeToken(int byteCount = 32) => "token" + Interlocked.Increment(ref _counter);
    }
}
