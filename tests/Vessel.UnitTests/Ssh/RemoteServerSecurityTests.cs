using System.Text.Json;
using Microsoft.Extensions.Options;
using Vessel.Application.Authorization;
using Vessel.Application.Notifications;
using Vessel.Application.Processes;
using Vessel.Application.Security;
using Vessel.Application.Servers;
using Vessel.Application.Ssh;
using Vessel.Application.Ssh.Keys;
using Vessel.Application.Terminals;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Common;
using Vessel.Domain.Projects;
using Vessel.Domain.Secrets;
using Vessel.Domain.Servers;
using Vessel.Domain.Ssh;
using Vessel.Domain.Teams;
using Vessel.Domain.Terminals;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;
using Vessel.UnitTests.Application;
using Vessel.UnitTests.Application.SecurityHardening;
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.UnitTests.Ssh;

/// <summary>SRV-02 / SEC-12 / SRV-06 security boundaries: keys, host key pinning, server lifecycle, terminals.</summary>
public sealed class RemoteServerSecurityTests
{
    private const string PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk=\n-----END OPENSSH PRIVATE KEY-----\n";
    private readonly SecurityTestDbContext _db = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly RecordingVault _vault = new();
    private readonly RecordingAuditWriter _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- SSH keys -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ImportedKey_IsStoredInVault_AndOnlyPublicDataIsReturned()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        SshKeyService keys = Keys(new FakeKeyTool(SshKeyFormatsTests.Ed25519Public));

        SshKeySummary summary = await keys.CreateAsync(owner.Id, team.Id,
            new CreateSshKeyRequest("deploy", null, PrivateKey), Ct);

        Assert.Equal(SshKeyFormatsTests.Ed25519Fingerprint, summary.Fingerprint);
        Assert.Equal("ssh-ed25519", summary.Algorithm);
        var json = JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("PRIVATE KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("b3BlbnNzaC1rZXk", json, StringComparison.Ordinal);
        SshKey stored = Assert.Single(_db.SshKeyItems);
        Assert.Equal(PrivateKey, _vault.Plaintext[stored.PrivateKeyReferenceId]);
        Assert.DoesNotContain("PRIVATE KEY", JsonSerializer.Serialize(keys.List(owner.Id, team.Id)),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SshKeyFormatsTests.Rsa1024Public, "2048")]
    [InlineData(null, "invalid")]
    public async Task Import_RejectsWeakOrUnloadableKeys_WithoutStoringAnything(string? derivedPublicKey,
        string expected)
    {
        (User owner, Team team) = AddUser("owner@example.com");

        DomainException error = await Assert.ThrowsAsync<DomainException>(() =>
            Keys(new FakeKeyTool(derivedPublicKey)).CreateAsync(owner.Id, team.Id,
                new CreateSshKeyRequest("weak", null, PrivateKey), Ct));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_db.SshKeyItems);
        Assert.Empty(_vault.Plaintext);
    }

    [Fact]
    public async Task Import_RejectsDsaArmorBeforeRunningKeygen()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        var tool = new FakeKeyTool(SshKeyFormatsTests.Ed25519Public);

        await Assert.ThrowsAsync<DomainException>(() => Keys(tool).CreateAsync(owner.Id, team.Id,
            new CreateSshKeyRequest("dsa", null,
                "-----BEGIN DSA PRIVATE KEY-----\nAAAA\n-----END DSA PRIVATE KEY-----"), Ct));
        Assert.Equal(0, tool.DeriveCalls);
    }

    [Fact]
    public async Task Generate_CreatesEd25519Key()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        var tool = new FakeKeyTool(SshKeyFormatsTests.Ed25519Public);

        SshKeySummary summary = await Keys(tool).GenerateAsync(owner.Id, team.Id,
            new GenerateSshKeyRequest("generated", null), Ct);

        Assert.Equal(1, tool.GenerateCalls);
        Assert.Equal("ssh-ed25519", summary.Algorithm);
    }

    [Fact]
    public async Task KeyInUse_CannotBeDeleted_AndOtherTeamsKeysLookMissing()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        (User other, Team otherTeam) = AddUser("other@example.com");
        SshKeyService keys = Keys(new FakeKeyTool(SshKeyFormatsTests.Ed25519Public));
        SshKeySummary key = await keys.CreateAsync(owner.Id, team.Id, new CreateSshKeyRequest("k", null, PrivateKey),
            Ct);
        Server server = AddServer(team, ServerConnectionType.Ssh);
        server.AssignSshKey(new SshKeyId(key.Id), _now);

        await Assert.ThrowsAsync<SshKeyInUseException>(() =>
            keys.DeleteAsync(owner.Id, team.Id, new SshKeyId(key.Id), Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            keys.DeleteAsync(other.Id, otherTeam.Id, new SshKeyId(key.Id), Ct));
        Assert.Throws<KeyNotFoundException>(() => keys.Get(other.Id, otherTeam.Id, new SshKeyId(key.Id)));
        Assert.Single(_db.SshKeyItems);

        server.AssignSshKey(null, _now);
        await keys.DeleteAsync(owner.Id, team.Id, new SshKeyId(key.Id), Ct);
        Assert.Empty(_db.SshKeyItems);
    }

    [Fact]
    public async Task Members_CannotManageKeys()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        (User member, _) = AddUser("member@example.com");
        team.AddMember(member.Id, TeamRole.Member, _now);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Keys(new FakeKeyTool(SshKeyFormatsTests.Ed25519Public)).CreateAsync(member.Id, team.Id,
                new CreateSshKeyRequest("k", null, PrivateKey), Ct));
        Assert.Empty(_db.SshKeyItems);
    }

    // ---- Server lifecycle -----------------------------------------------------------------------------------

    [Fact]
    public async Task ServerUpdate_RejectsAnotherTeamsKey_AndAddressChangeForgetsHostKey()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        (User other, Team otherTeam) = AddUser("other@example.com");
        SshKeySummary foreignKey = await Keys(new FakeKeyTool(SshKeyFormatsTests.Ed25519Public))
            .CreateAsync(other.Id, otherTeam.Id, new CreateSshKeyRequest("theirs", null, PrivateKey), Ct);
        Server server = AddServer(team, ServerConnectionType.Ssh);
        server.PinHostKey("ssh-ed25519 AAAA", _now);
        ServerManagementService management = Management();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => management.UpdateAsync(owner.Id, team.Id, server.Id,
            new PatchServerRequest(SshKeyId: foreignKey.Id), Ct));
        Assert.Null(server.SshKeyId);

        ServerDetails details = await management.UpdateAsync(owner.Id, team.Id, server.Id,
            new PatchServerRequest(Host: "203.0.113.99"), Ct);
        Assert.Null(server.PinnedHostKey);
        Assert.Empty(details.PinnedHostKeyFingerprints);
    }

    [Fact]
    public async Task ServerUpdate_LooserHostKeyCheckingRequiresInstanceAdmin()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        Server server = AddServer(team, ServerConnectionType.Ssh);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Management().UpdateAsync(owner.Id, team.Id,
            server.Id, new PatchServerRequest(HostKeyChecking: SshHostKeyCheckingMode.Off), Ct));
        Assert.Equal(SshHostKeyCheckingMode.Enforce, server.HostKeyChecking);

        (User admin, _) = AddUser("admin@example.com", instanceAdmin: true);
        team.AddMember(admin.Id, TeamRole.Admin, _now);
        await Management().UpdateAsync(admin.Id, team.Id, server.Id,
            new PatchServerRequest(HostKeyChecking: SshHostKeyCheckingMode.Warn), Ct);
        Assert.Equal(SshHostKeyCheckingMode.Warn, server.HostKeyChecking);
    }

    [Fact]
    public async Task ServerDelete_IsRefusedWhileApplicationsUseIt_AndCrossTeamIsForbidden()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        (User other, Team otherTeam) = AddUser("other@example.com");
        Server server = AddServer(team, ServerConnectionType.Ssh);
        AppEntity application = AddApplication(team, server);

        await Assert.ThrowsAsync<ServerInUseException>(() => Management().DeleteAsync(owner.Id, team.Id, server.Id,
            Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Management().DeleteAsync(other.Id, otherTeam.Id,
            server.Id, Ct));
        Assert.Single(_db.ServerItems);

        _db.ApplicationItems.Remove(application);
        await Management().DeleteAsync(owner.Id, team.Id, server.Id, Ct);
        Assert.Empty(_db.ServerItems);
    }

    [Fact]
    public async Task Validation_IsQueued_NeverRunInline()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        Server server = AddServer(team, ServerConnectionType.Ssh);
        var jobs = new RecordingJobDispatcher();

        ServerValidationAccepted accepted = await Management(jobs).RequestValidationAsync(owner.Id, team.Id, server.Id,
            new ValidateServerRequest(), Ct);

        Assert.Equal(1, jobs.Enqueued);
        Assert.False(accepted.InstallDocker);
    }

    // ---- Host key pinning -----------------------------------------------------------------------------------

    [Fact]
    public async Task FirstConnection_PinsHostKey_TrustOnFirstUse()
    {
        (_, Team team) = AddUser("owner@example.com");
        Server server = AddServer(team, ServerConnectionType.Ssh);
        var scanner = new FakeScanner("ssh-ed25519 AAAA");

        PreparedSshConnection prepared = await Connections(scanner).PrepareAsync(server.Id, cancellationToken: Ct);

        Assert.Equal("ssh-ed25519 AAAA", server.PinnedHostKey);
        Assert.Equal("ssh-ed25519 AAAA", prepared.PinnedHostKeys);
        Assert.Equal(SshHostKeyCheckingMode.Enforce, prepared.Mode);
        Assert.Contains(_audit.Records, record => record.Action == Vessel.Application.Auditing.AuditActions.ServerHostKeyPinned);

        await Connections(scanner).PrepareAsync(server.Id, cancellationToken: Ct);
        Assert.Equal(1, scanner.Calls); // pinned keys are not scanned again
    }

    [Fact]
    public async Task EnforceMode_RefusesToConnectWhenTheHostKeyCannotBeRead()
    {
        (_, Team team) = AddUser("owner@example.com");
        Server server = AddServer(team, ServerConnectionType.Ssh);

        await Assert.ThrowsAsync<SshHostKeyUnavailableException>(() =>
            Connections(new FakeScanner(null)).PrepareAsync(server.Id, cancellationToken: Ct));
        Assert.Null(server.PinnedHostKey);
    }

    [Fact]
    public async Task InstanceOff_SkipsHostKeyScanning()
    {
        (_, Team team) = AddUser("owner@example.com");
        Server server = AddServer(team, ServerConnectionType.Ssh);
        var scanner = new FakeScanner("ssh-ed25519 AAAA");

        PreparedSshConnection prepared = await Connections(scanner, "off").PrepareAsync(server.Id,
            cancellationToken: Ct);

        Assert.Equal(SshHostKeyCheckingMode.Off, prepared.Mode);
        Assert.Equal(0, scanner.Calls);
    }

    [Fact]
    public async Task ChangedHostKey_IsRecordedAsPending_AndTheTeamIsNotifiedOnce()
    {
        (_, Team team) = AddUser("owner@example.com");
        Server server = AddServer(team, ServerConnectionType.Ssh);
        server.PinHostKey("ssh-ed25519 " + SshKeyFormatsTests.Ed25519Public.Split(' ')[1], _now);
        var notifications = new RecordingNotificationEventPublisher();
        var newKey = "ssh-rsa " + SshKeyFormatsTests.Rsa2048Public.Split(' ')[1];
        ServerSshConnectionService service = Connections(new FakeScanner(newKey), notifications: notifications);
        var stderr = $"@@@ WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED! @@@\nHost key for {ServerSshConnectionService.HostKeyAlias(server.Id)} has changed";

        Assert.True(await service.RecordHostKeyMismatchAsync(server.Id, stderr, Ct));
        Assert.False(await service.RecordHostKeyMismatchAsync(server.Id, stderr, Ct));

        Assert.Equal(newKey, server.PendingHostKey);
        Assert.NotEqual(newKey, server.PinnedHostKey);
        TeamNotification notification = Assert.Single(notifications.Published);
        Assert.Equal(NotificationEventTypes.ServerSshHostKeyChanged, notification.Notification.EventType);
        Assert.Equal(team.Id, notification.TeamId);
        Assert.Contains(SshKeyFormatsTests.Rsa2048Fingerprint, notification.Notification.PayloadJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MismatchMessageOfAnotherHost_IsIgnored()
    {
        (_, Team team) = AddUser("owner@example.com");
        Server server = AddServer(team, ServerConnectionType.Ssh);
        server.PinHostKey("ssh-ed25519 AAAA", _now);
        var scanner = new FakeScanner("ssh-ed25519 BBBB");

        // For example git inside a build connecting to its own remote prints the same warning.
        Assert.False(await Connections(scanner).RecordHostKeyMismatchAsync(server.Id,
            "@@@ WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED! @@@ github.com", Ct));
        Assert.Equal(0, scanner.Calls);
        Assert.Null(server.PendingHostKey);
    }

    [Fact]
    public async Task ServerKeyOfAnotherTeam_IsNeverUsed()
    {
        (User other, Team otherTeam) = AddUser("other@example.com");
        (_, Team team) = AddUser("owner@example.com");
        SshKeySummary foreign = await Keys(new FakeKeyTool(SshKeyFormatsTests.Ed25519Public))
            .CreateAsync(other.Id, otherTeam.Id, new CreateSshKeyRequest("theirs", null, PrivateKey), Ct);
        Server server = AddServer(team, ServerConnectionType.Ssh);
        server.PinHostKey("ssh-ed25519 AAAA", _now);
        server.AssignSshKey(new SshKeyId(foreign.Id), _now); // e.g. a corrupted row

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Connections(new FakeScanner("ssh-ed25519 AAAA")).PrepareAsync(server.Id, cancellationToken: Ct));
    }

    // ---- Terminals ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AttachToken_IsSingleUse_AndBoundToTheUser()
    {
        (User admin, Team team) = AddUser("admin@example.com", instanceAdmin: true);
        (User member, _) = AddUser("member@example.com", instanceAdmin: true);
        team.AddMember(member.Id, TeamRole.Admin, _now);
        Server server = AddServer(team, ServerConnectionType.Ssh);
        var bridge = new PtyCapableBridge();
        TerminalSessionManager terminals = Terminals(bridge);

        TerminalSessionDetails opened = await terminals.OpenAsync(admin.Id, team.Id,
            new OpenTerminalSessionRequest(server.Id.Value), cancellationToken: Ct);
        var sessionId = new TerminalSessionId(opened.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            terminals.AttachAsync(member.Id, team.Id, sessionId, opened.AttachToken, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            terminals.AttachAsync(admin.Id, team.Id, sessionId, "forged-token", Ct));
        Assert.Empty(bridge.Opened);

        TerminalSessionDetails reissued = terminals.IssueAttachToken(admin.Id, team.Id, sessionId);
        await terminals.AttachAsync(admin.Id, team.Id, sessionId, reissued.AttachToken, Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            terminals.AttachAsync(admin.Id, team.Id, sessionId, reissued.AttachToken, Ct));

        TerminalBridgeOpenRequest open = Assert.Single(bridge.Opened);
        // SSH server shells run over the server's own connection, never on the control-plane host.
        Assert.Equal(ServerConnectionType.Ssh, open.TargetConnection);
        Assert.NotNull(open.Remote);
        Assert.Equal(server.Id, open.Remote.ServerId);
        Assert.Null(open.Arguments);
    }

    [Fact]
    public async Task ExpiredAttachToken_IsRejected()
    {
        (User admin, Team team) = AddUser("admin@example.com", instanceAdmin: true);
        Server server = AddServer(team, ServerConnectionType.Ssh);
        var clock = new MutableTimeProvider(_now);
        var bridge = new PtyCapableBridge();
        TerminalSessionManager terminals = Terminals(bridge, clock);
        TerminalSessionDetails opened = await terminals.OpenAsync(admin.Id, team.Id,
            new OpenTerminalSessionRequest(server.Id.Value), cancellationToken: Ct);

        clock.Advance(TerminalAttachTokens.Lifetime + TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => terminals.AttachAsync(admin.Id, team.Id,
            new TerminalSessionId(opened.Id), opened.AttachToken, Ct));
        Assert.Empty(bridge.Opened);
    }

    [Fact]
    public async Task Revalidation_EndsSessionsWhenAccessIsLost_OrTerminalsAreDisabled()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        (User admin, _) = AddUser("admin@example.com");
        team.AddMember(admin.Id, TeamRole.Admin, _now);
        Server server = AddServer(team, ServerConnectionType.Ssh);
        var bridge = new PtyCapableBridge();
        TerminalSessionManager terminals = Terminals(bridge);
        TerminalSessionId adminSession = await OpenAttachedAsync(terminals, admin, team, server);
        TerminalSessionId ownerSession = await OpenAttachedAsync(terminals, owner, team, server);

        Assert.Equal(0, await terminals.RevalidateAsync(Ct));

        team.RemoveMember(admin.Id, _now);
        Assert.Equal(1, await terminals.RevalidateAsync(Ct));
        Assert.Contains(adminSession, bridge.Closed);
        Assert.Equal(TerminalSessionStatus.Failed, Session(adminSession).Status);
        Assert.Equal(TerminalSessionManager.AccessLostReason, Session(adminSession).FailureReason);
        Assert.Contains(_audit.Records, record => record.Action == Vessel.Application.Auditing.AuditActions.TerminalSessionRevoked);

        server.SetTerminalAccess(false, _now);
        Assert.Equal(1, await terminals.RevalidateAsync(Ct));
        Assert.Equal(TerminalSessionStatus.Failed, Session(ownerSession).Status);
    }

    [Fact]
    public async Task Input_IsRefusedAndSessionEndedOnceTerminalsAreDisabled()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        Server server = AddServer(team, ServerConnectionType.Ssh);
        var bridge = new PtyCapableBridge();
        TerminalSessionManager terminals = Terminals(bridge);
        TerminalSessionId session = await OpenAttachedAsync(terminals, owner, team, server);

        server.SetTerminalAccess(false, _now);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            terminals.SendInputAsync(owner.Id, team.Id, session, "ls\n", Ct));
        Assert.Empty(bridge.Inputs);
        Assert.Equal(TerminalSessionStatus.Failed, Session(session).Status);
    }

    [Fact]
    public async Task DisabledTerminalAccess_RefusesNewSessions()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        Server server = AddServer(team, ServerConnectionType.Ssh);
        server.SetTerminalAccess(false, _now);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Terminals(new PtyCapableBridge()).OpenAsync(
            owner.Id, team.Id, new OpenTerminalSessionRequest(server.Id.Value), cancellationToken: Ct));
        Assert.Empty(_db.TerminalSessionItems);
    }

    [Fact]
    public async Task ContainerTerminal_DerivesStructuredExecCommand_FromTheResource()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        Server server = AddServer(team, ServerConnectionType.Ssh);
        AppEntity application = AddApplication(team, server);
        var runner = new FakeServerCommands("vessel-app-1\nvessel-app-2\n");
        var bridge = new PtyCapableBridge();
        TerminalSessionManager terminals = Terminals(bridge, commands: runner);

        TerminalSessionDetails opened = await terminals.OpenAsync(owner.Id, team.Id,
            new OpenTerminalSessionRequest(server.Id.Value, TerminalTargetType.Container, "vessel-app-2",
                ResourceKind: TerminalResourceKind.Application, ResourceId: application.Id.Value),
            cancellationToken: Ct);
        await terminals.AttachAsync(owner.Id, team.Id, new TerminalSessionId(opened.Id), opened.AttachToken, Ct);

        IReadOnlyList<string> lookup = Assert.Single(runner.Commands);
        Assert.Equal(["docker", "ps", "--filter", $"label=vessel.application={application.Id.Value:D}", "--format",
            "{{.Names}}"], lookup);
        TerminalBridgeOpenRequest open = Assert.Single(bridge.Opened);
        Assert.Equal("docker", open.Command);
        Assert.Equal(["exec", "-it", "vessel-app-2", "sh", "-c", TerminalTargetPolicy.ContainerShellScript],
            open.Arguments);
        Assert.NotNull(open.Remote);
    }

    [Fact]
    public async Task ContainerTerminal_RejectsContainersOutsideTheResource_AndForeignResources()
    {
        (User owner, Team team) = AddUser("owner@example.com");
        (_, Team otherTeam) = AddUser("other@example.com");
        Server server = AddServer(team, ServerConnectionType.Ssh);
        Server foreignServer = AddServer(otherTeam, ServerConnectionType.Ssh);
        AppEntity application = AddApplication(team, server);
        AppEntity foreignApplication = AddApplication(otherTeam, foreignServer);
        TerminalSessionManager terminals = Terminals(new PtyCapableBridge(),
            commands: new FakeServerCommands("vessel-app-1\n"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => terminals.OpenAsync(owner.Id, team.Id,
            new OpenTerminalSessionRequest(server.Id.Value, TerminalTargetType.Container, "postgres-of-someone-else",
                ResourceKind: TerminalResourceKind.Application, ResourceId: application.Id.Value),
            cancellationToken: Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => terminals.OpenAsync(owner.Id, team.Id,
            new OpenTerminalSessionRequest(server.Id.Value, TerminalTargetType.Container, null,
                ResourceKind: TerminalResourceKind.Application, ResourceId: foreignApplication.Id.Value),
            cancellationToken: Ct));
        Assert.Empty(_db.TerminalSessionItems);
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private async Task<TerminalSessionId> OpenAttachedAsync(TerminalSessionManager terminals, User user, Team team,
        Server server)
    {
        TerminalSessionDetails opened = await terminals.OpenAsync(user.Id, team.Id,
            new OpenTerminalSessionRequest(server.Id.Value), cancellationToken: Ct);
        var id = new TerminalSessionId(opened.Id);
        await terminals.AttachAsync(user.Id, team.Id, id, opened.AttachToken, Ct);
        return id;
    }

    private TerminalSession Session(TerminalSessionId id) => _db.TerminalSessionItems.Single(item => item.Id == id);

    private SshKeyService Keys(ISshKeyTool tool) =>
        new(_db, new VesselAuthorizationService(_db), _vault, tool, new RecordingJobDispatcher(), _audit,
            TimeProvider.System);

    private ServerManagementService Management(RecordingJobDispatcher? jobs = null) =>
        new(_db, new VesselAuthorizationService(_db), jobs ?? new RecordingJobDispatcher(), new SshHostKeyScanThrottle(),
            _audit, Options.Create(new SshOptions()), TimeProvider.System);

    private ServerSshConnectionService Connections(FakeScanner scanner, string mode = "enforce",
        RecordingNotificationEventPublisher? notifications = null) =>
        new(_db, _vault, scanner, new SshHostKeyScanThrottle(), notifications ?? new RecordingNotificationEventPublisher(),
            _audit, Options.Create(new SshOptions { HostKeyChecking = mode }), TimeProvider.System);

    private TerminalSessionManager Terminals(ITerminalProcessBridge bridge, TimeProvider? clock = null,
        IServerCommandRunner? commands = null)
    {
        TimeProvider time = clock ?? TimeProvider.System;
        return new TerminalSessionManager(_db, new VesselAuthorizationService(_db), bridge, new NullRealtimeNotifier(),
            _audit, new PassthroughRedactor(), time, new TerminalAttachTokens(time), commands);
    }

    private (User User, Team Team) AddUser(string email, bool instanceAdmin = false)
    {
        User user = User.Create(new DisplayName(email.Split('@')[0]), new EmailAddress(email), _now);
        if (instanceAdmin) user.GrantInstanceAdmin(_now);
        Team team = Team.Create(new DisplayName(email + " team"), user.Id, true, _now);
        _db.UserItems.Add(user);
        _db.TeamItems.Add(team);
        return (user, team);
    }

    private Server AddServer(Team team, ServerConnectionType connectionType)
    {
        Server server = Server.Create(team.Id, new ResourceName("server-" + _db.ServerItems.Count),
            new ServerAddress("203.0.113.10", new PortNumber(22), "deploy"), connectionType,
            ContainerRuntimeKind.Docker, _now);
        _db.ServerItems.Add(server);
        return server;
    }

    private AppEntity AddApplication(Team team, Server server)
    {
        Project project = Project.Create(team.Id, new ResourceName("project-" + _db.ProjectItems.Count), _now);
        EnvironmentEntity environment = EnvironmentEntity.CreateProduction(project.Id, _now);
        AppEntity application = AppEntity.Create(environment.Id, server.Id, new ResourceName("app"),
            new GitSource(new RepositoryUrl("https://example.com/repo.git"), "main"),
            BuildConfiguration.Default(ApplicationBuildPack.Dockerfile), _now);
        _db.ProjectItems.Add(project);
        _db.EnvironmentItems.Add(environment);
        _db.ApplicationItems.Add(application);
        return application;
    }

    private sealed class FakeKeyTool(string? derivedPublicKey) : ISshKeyTool
    {
        public int DeriveCalls { get; private set; }
        public int GenerateCalls { get; private set; }

        public Task<GeneratedSshKey> GenerateEd25519Async(string comment, CancellationToken cancellationToken = default)
        {
            GenerateCalls++;
            return Task.FromResult(new GeneratedSshKey(PrivateKey, SshKeyFormatsTests.Ed25519Public));
        }

        public Task<string?> DerivePublicKeyAsync(string privateKey, CancellationToken cancellationToken = default)
        {
            DeriveCalls++;
            return Task.FromResult(derivedPublicKey);
        }
    }

    private sealed class FakeScanner(string? hostKeys) : ISshHostKeyScanner
    {
        public int Calls { get; private set; }

        public Task<SshHostKeyScanResult> ScanAsync(string host, int port, string user, string hostKeyAlias,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new SshHostKeyScanResult(hostKeys, hostKeys is null ? "connection refused" : null));
        }
    }

    private sealed class RecordingVault : ISecretVault
    {
        public Dictionary<SecretReferenceId, string> Plaintext { get; } = [];

        public Task<SecretReference> StoreAsync(TeamId teamId, SecretScope scope, string key, string plaintext,
            SecretPolicy policy, SecretTarget target, CancellationToken cancellationToken = default)
        {
            var reference = SecretReference.Create(teamId, scope, key, SecretProvider.Vessel,
                $"vessel://secrets/{Guid.NewGuid():D}", policy, DateTimeOffset.UtcNow);
            Plaintext[reference.Id] = plaintext;
            return Task.FromResult(reference);
        }

        public Task ReplaceAsync(SecretReferenceId secretReferenceId, string plaintext,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> RevealAsync(UserId actorUserId, TeamId teamId, SecretReferenceId secretReferenceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> RevealForDeploymentAsync(TeamId teamId, SecretReferenceId secretReferenceId,
            CancellationToken cancellationToken = default) => Task.FromResult(Plaintext[secretReferenceId]);
    }

    private sealed class PtyCapableBridge : ITerminalProcessBridge
    {
        private readonly HashSet<TerminalSessionId> _open = [];
        public List<TerminalBridgeOpenRequest> Opened { get; } = [];
        public List<TerminalSessionId> Closed { get; } = [];
        public List<string> Inputs { get; } = [];

        public bool SupportsRemoteAndContainerSessions => true;

        public bool IsOpen(TerminalSessionId sessionId) => _open.Contains(sessionId);

        public Task OpenAsync(TerminalBridgeOpenRequest request,
            Func<TerminalOutputChunk, CancellationToken, Task> onOutput,
            Func<TerminalSessionId, string?, CancellationToken, Task> onExit,
            CancellationToken cancellationToken = default)
        {
            Opened.Add(request);
            _open.Add(request.SessionId);
            return Task.CompletedTask;
        }

        public Task SendInputAsync(TerminalSessionId sessionId, string data,
            CancellationToken cancellationToken = default)
        {
            Inputs.Add(data);
            return Task.CompletedTask;
        }

        public Task ResizeAsync(TerminalBridgeResizeRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CloseAsync(TerminalSessionId sessionId, CancellationToken cancellationToken = default)
        {
            Closed.Add(sessionId);
            _open.Remove(sessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeServerCommands(string output) : IServerCommandRunner
    {
        public List<IReadOnlyList<string>> Commands { get; } = [];

        public Task<ServerCommandResult> RunAsync(ServerExecutionTarget target, IReadOnlyList<string> arguments,
            TimeSpan timeout, string? standardInput = null, CancellationToken cancellationToken = default)
        {
            Commands.Add(arguments);
            return Task.FromResult(new ServerCommandResult(0, output, string.Empty));
        }

        public Task<ServerCommandResult> RunWithKeyAsync(ServerExecutionTarget target, SshKeyId keyId,
            IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ProcessOutputLine> StreamAsync(ServerExecutionTarget target,
            IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout,
            IReadOnlyList<string>? secretValues = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
