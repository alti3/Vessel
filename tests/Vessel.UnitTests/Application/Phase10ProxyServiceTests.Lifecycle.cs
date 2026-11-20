using Vessel.Application.Auditing;
using Vessel.Application.Common;
using Vessel.Application.Proxy;
using Vessel.Application.Servers;
using Vessel.Application.Servers.Files;
using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.Servers;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Application;

public sealed partial class Phase10ProxyServiceTests
{
    [Fact]
    public async Task ProxyStart_DeliversStaticFilesStartsContainerAndRecordsPinnedState()
    {
        var fixture = ServiceFixture.Create();

        ServerProxySummary summary = await fixture.ProxyLifecycle.StartAsync(fixture.UserId, fixture.TeamId,
            fixture.Server.Id, TestContext.Current.CancellationToken);

        Assert.Equal("running", summary.ContainerState);
        Assert.True(summary.Healthy);
        Assert.False(summary.RestartRequired);
        Assert.Equal(ComponentVersions.TraefikImage, summary.Image);
        Assert.Contains(ProxyLayout.StaticConfigurationFile, fixture.Files.Contents.Keys);
        Assert.Contains(ProxyLayout.ComposeFile, fixture.Files.Contents.Keys);
        Assert.Equal(ServerFileModes.OwnerReadWrite, fixture.Files.Modes[ProxyLayout.AcmeStorageFile]);
        Assert.Equal(ServerFileModes.OwnerOnlyDirectory, fixture.Files.Directories[ProxyLayout.AcmeDirectory]);
        var network = ProxyNaming.ServerNetwork(fixture.Server.Id);
        Assert.Equal(
            [$"network:{network}", $"up:/srv/vessel/{ProxyLayout.ComposeFile}:False"],
            fixture.Runtime.Calls);
        ServerProxySettings settings = Assert.Single(fixture.Db.ServerProxySettingsItems);
        Assert.Equal(ProxyDesiredState.Running, settings.DesiredState);
        Assert.Equal(fixture.Renderer.LastHash, settings.LastAppliedStaticHash);
        Assert.Contains(fixture.Audit.Records, record => record.Action == AuditActions.ServerProxyStarted);
    }

    [Fact]
    public async Task ProxyStart_NeverOverwritesExistingAcmeStorage()
    {
        var fixture = ServiceFixture.Create();
        fixture.Files.Contents[ProxyLayout.AcmeStorageFile] = "{\"letsencrypt\":{}}";

        await fixture.ProxyLifecycle.StartAsync(fixture.UserId, fixture.TeamId, fixture.Server.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal("{\"letsencrypt\":{}}", fixture.Files.Contents[ProxyLayout.AcmeStorageFile]);
    }

    [Fact]
    public async Task ProxyStart_WhenStaticFileVerificationFails_DoesNotStartContainer()
    {
        var fixture = ServiceFixture.Create();
        fixture.Files.CorruptReads = true;

        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyLifecycle.StartAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(fixture.Runtime.Calls, call => call.StartsWith("up:", StringComparison.Ordinal));
        Assert.NotNull(Assert.Single(fixture.Db.ServerProxySettingsItems).LastError);
    }

    [Fact]
    public async Task ProxyStart_WhenContainerDoesNotRun_RecordsRedactedFailure()
    {
        var fixture = ServiceFixture.Create();
        fixture.Runtime.FailUpMessage = "port 80 in use password=hunter2";

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
            fixture.ProxyLifecycle.StartAsync(fixture.UserId, fixture.TeamId, fixture.Server.Id,
                TestContext.Current.CancellationToken));

        Assert.Contains("[redacted]", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("password", exception.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", Assert.Single(fixture.Db.ServerProxySettingsItems).LastError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyManagement_OnHostLocalServer_RequiresInstanceAdmin()
    {
        var fixture = ServiceFixture.Create();
        User owner = Assert.Single(fixture.Db.UserItems, user => user.Id == fixture.UserId);
        owner.RevokeInstanceAdmin(fixture.Now);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ProxyLifecycle.StartAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ProxyLifecycle.UpdateAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, new UpdateServerProxySettingsRequest("None", null, null),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(fixture.UserId, fixture.TeamId,
                fixture.Server.Id, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Db.ProxyConfigurationVersionItems);
    }

    [Fact]
    public async Task ProxyStart_RejectsUnauthorizedUnreachableNoneAndLockedServers()
    {
        var fixture = ServiceFixture.Create();
        var outsider = User.Create(new DisplayName("Outsider"), new EmailAddress("outsider@example.com"),
            fixture.Now);
        var otherTeam = Team.Create(new DisplayName("Other"), outsider.Id, false, fixture.Now);
        fixture.Db.UserItems.Add(outsider);
        fixture.Db.TeamItems.Add(otherTeam);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ProxyLifecycle.StartAsync(outsider.Id,
            otherTeam.Id, fixture.Server.Id, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ProxyLifecycle.UpdateAsync(outsider.Id,
            otherTeam.Id, fixture.Server.Id, new UpdateServerProxySettingsRequest("None", null, null),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ProxyLifecycle.GetAsync(outsider.Id,
            fixture.TeamId, fixture.Server.Id, TestContext.Current.CancellationToken));

        fixture.Locks.ShouldAcquire = false;
        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyLifecycle.RestartAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, TestContext.Current.CancellationToken));
        fixture.Locks.ShouldAcquire = true;

        fixture.Server.ChangeStatus(ServerStatus.Unreachable, fixture.Now);
        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyLifecycle.StartAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, TestContext.Current.CancellationToken));
        fixture.Server.ChangeStatus(ServerStatus.Reachable, fixture.Now);

        await fixture.ProxyLifecycle.UpdateAsync(fixture.UserId, fixture.TeamId, fixture.Server.Id,
            new UpdateServerProxySettingsRequest("none", null, null), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyLifecycle.StartAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Runtime.Calls);
    }

    [Fact]
    public async Task ProxyStop_RemovesContainerAndDeploymentsDoNotRestartIt()
    {
        var fixture = ServiceFixture.Create();
        fixture.Runtime.State = ProxyContainerState.Running;

        ServerProxySummary summary = await fixture.ProxyLifecycle.StopAsync(fixture.UserId, fixture.TeamId,
            fixture.Server.Id, TestContext.Current.CancellationToken);
        ProxyEnsureResult ensure = await fixture.ProxyLifecycle.EnsureForDeploymentAsync(fixture.Server.Id,
            ProxyNaming.ServerNetwork(fixture.Server.Id), TestContext.Current.CancellationToken);

        Assert.Equal("Stopped", summary.DesiredState);
        Assert.Equal("missing", summary.ContainerState);
        Assert.False(summary.Healthy);
        Assert.True(ensure.Skipped);
        Assert.Equal(["stop"], fixture.Runtime.Calls);
        Assert.Contains(fixture.Audit.Records, record => record.Action == AuditActions.ServerProxyStopped);
    }

    [Fact]
    public async Task EnsureForDeployment_StartsMissingProxyThenAttachesDeploymentNetwork()
    {
        var fixture = ServiceFixture.Create();
        var network = ProxyNaming.ServerNetwork(fixture.Server.Id);

        ProxyEnsureResult result = await fixture.ProxyLifecycle.EnsureForDeploymentAsync(fixture.Server.Id,
            network, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(result.Skipped);
        Assert.Equal(
            [$"network:{network}", $"up:/srv/vessel/{ProxyLayout.ComposeFile}:False", $"connect:{network}"],
            fixture.Runtime.Calls);
    }

    [Fact]
    public async Task EnsureForDeployment_WhenProxyRuns_OnlyConnectsNetwork()
    {
        var fixture = ServiceFixture.Create();
        fixture.Runtime.State = ProxyContainerState.Running;

        ProxyEnsureResult result = await fixture.ProxyLifecycle.EnsureForDeploymentAsync(fixture.Server.Id,
            "vessel-network", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(["connect:vessel-network"], fixture.Runtime.Calls);
    }

    [Fact]
    public async Task EnsureForDeployment_WhenProxyCannotStart_ReturnsRedactedFailureWithoutThrowing()
    {
        var fixture = ServiceFixture.Create();
        fixture.Runtime.FailUpMessage = "bind failed password=hunter2";

        ProxyEnsureResult result = await fixture.ProxyLifecycle.EnsureForDeploymentAsync(fixture.Server.Id,
            "vessel-network", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("[redacted]", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Runtime.Calls, call => call.StartsWith("connect:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProxyTypeNone_SkipsAttachmentAndRoutingEntirely()
    {
        var fixture = ServiceFixture.Create();
        fixture.Application.UpsertDomain(new DomainName("app.example.com"), null, true, true, false, fixture.Now);
        await fixture.ProxyLifecycle.UpdateAsync(fixture.UserId, fixture.TeamId, fixture.Server.Id,
            new UpdateServerProxySettingsRequest("None", null, null), TestContext.Current.CancellationToken);

        ProxyEnsureResult ensure = await fixture.ProxyLifecycle.EnsureForDeploymentAsync(fixture.Server.Id,
            "vessel-network", TestContext.Current.CancellationToken);
        ProxyConfigurationSummary? applied = await fixture.ProxyConfigurations.ApplyForDeploymentAsync(null,
            fixture.TeamId, fixture.Server.Id, TestContext.Current.CancellationToken);

        Assert.True(ensure.Skipped);
        Assert.Null(applied);
        Assert.Empty(fixture.Db.ProxyConfigurationVersionItems);
        Assert.Empty(fixture.Runtime.Calls);
        Assert.Empty(fixture.Files.Contents);
        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(
            fixture.UserId, fixture.TeamId, fixture.Server.Id, TestContext.Current.CancellationToken));
        Assert.True(await fixture.ProxyLifecycle.IsHealthyAsync(fixture.Server,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProxyUpdate_ValidatesTypeAndPortsAndRequiresStopBeforeTypeChange()
    {
        var fixture = ServiceFixture.Create();

        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyLifecycle.UpdateAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, new UpdateServerProxySettingsRequest("Caddy", null, null),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyLifecycle.UpdateAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, new UpdateServerProxySettingsRequest("1", null, null),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyLifecycle.UpdateAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, new UpdateServerProxySettingsRequest(null, 8443, 8443),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyLifecycle.UpdateAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, new UpdateServerProxySettingsRequest(null, 0, null),
            TestContext.Current.CancellationToken));

        fixture.Runtime.State = ProxyContainerState.Running;
        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyLifecycle.UpdateAsync(fixture.UserId,
            fixture.TeamId, fixture.Server.Id, new UpdateServerProxySettingsRequest("None", null, null),
            TestContext.Current.CancellationToken));

        ServerProxySummary summary = await fixture.ProxyLifecycle.UpdateAsync(fixture.UserId, fixture.TeamId,
            fixture.Server.Id, new UpdateServerProxySettingsRequest(null, 8080, 8443),
            TestContext.Current.CancellationToken);
        Assert.Equal(8080, summary.HttpPort);
        Assert.Equal(8443, summary.HttpsPort);
        Assert.Equal("Traefik", summary.ProxyType);
        Assert.Contains(fixture.Audit.Records, record => record.Action == AuditActions.ServerProxyUpdated);
    }

    [Fact]
    public async Task ProxyGet_ReportsRestartRequiredAfterStaticSettingsChange()
    {
        var fixture = ServiceFixture.Create();
        await fixture.ProxyLifecycle.StartAsync(fixture.UserId, fixture.TeamId, fixture.Server.Id,
            TestContext.Current.CancellationToken);

        await fixture.ProxyLifecycle.UpdateAsync(fixture.UserId, fixture.TeamId, fixture.Server.Id,
            new UpdateServerProxySettingsRequest(null, 8080, null), TestContext.Current.CancellationToken);
        ServerProxySummary summary = await fixture.ProxyLifecycle.GetAsync(fixture.UserId, fixture.TeamId,
            fixture.Server.Id, TestContext.Current.CancellationToken);

        Assert.True(summary.RestartRequired);
    }

    [Theory]
    [InlineData(ProxyContainerState.Missing, null, false)]
    [InlineData(ProxyContainerState.Exited, null, false)]
    [InlineData(ProxyContainerState.Running, "unhealthy", false)]
    [InlineData(ProxyContainerState.Running, "starting", false)]
    [InlineData(ProxyContainerState.Running, "healthy", true)]
    [InlineData(ProxyContainerState.Running, null, true)]
    public async Task ProxyHealth_ReflectsRealContainerState(ProxyContainerState state, string? health, bool expected)
    {
        var fixture = ServiceFixture.Create();
        fixture.Runtime.State = state;
        fixture.Runtime.Health = health;

        Assert.Equal(expected, await fixture.ProxyLifecycle.IsHealthyAsync(fixture.Server,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProxyHealth_WhenRuntimeCannotBeInspected_IsNotHealthy()
    {
        var fixture = ServiceFixture.Create();
        fixture.Runtime.InspectFailure = "ssh: connect to host refused";

        Assert.False(await fixture.ProxyLifecycle.IsHealthyAsync(fixture.Server,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplyProxyConfiguration_WhenDeliveredFileDoesNotVerify_MarksVersionFailed()
    {
        var fixture = ServiceFixture.Create();
        fixture.Files.CorruptReads = true;

        await Assert.ThrowsAsync<DomainException>(() => fixture.ProxyConfigurations.GenerateValidateAndApplyAsync(
            fixture.UserId, fixture.TeamId, fixture.Server.Id, TestContext.Current.CancellationToken));

        ProxyConfigurationVersion version = Assert.Single(fixture.Db.ProxyConfigurationVersionItems);
        Assert.Equal(ProxyConfigurationStatus.Failed, version.Status);
        Assert.Contains("Verification", version.ApplyError, StringComparison.Ordinal);
    }

    private sealed class TestServerFileChannel : IServerFileChannel
    {
        public Dictionary<string, string> Contents { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, UnixFileMode> Modes { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, UnixFileMode> Directories { get; } = new(StringComparer.Ordinal);

        public int FailNextWrites { get; set; }

        public string FailureMessage { get; set; } = "write failed";

        public bool CorruptReads { get; set; }

        public bool Supports(ServerConnectionType connectionType)
        {
            return true;
        }

        public string ResolveHostPath(ServerExecutionTarget target, string relativePath)
        {
            return "/srv/vessel/" + ServerFilePaths.ValidateRelative(relativePath);
        }

        public Task EnsureDirectoryAsync(
            ServerExecutionTarget target,
            string relativePath,
            UnixFileMode mode,
            CancellationToken cancellationToken = default)
        {
            Directories[ServerFilePaths.ValidateRelative(relativePath)] = mode;
            return Task.CompletedTask;
        }

        public Task<ServerFileWriteResult> WriteAsync(
            ServerExecutionTarget target,
            ServerFileWrite file,
            CancellationToken cancellationToken = default)
        {
            if (FailNextWrites > 0)
            {
                FailNextWrites--;
                throw new IOException(FailureMessage);
            }

            var path = ServerFilePaths.ValidateRelative(file.RelativePath);
            if (file.CreateOnly && Contents.ContainsKey(path))
                return Task.FromResult(ServerFileWriteResult.AlreadyExists);
            Contents[path] = file.Contents;
            Modes[path] = file.Mode;
            return Task.FromResult(ServerFileWriteResult.Written);
        }

        public Task<string?> ReadTextAsync(
            ServerExecutionTarget target,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            if (CorruptReads)
                return Task.FromResult<string?>("tampered");
            return Task.FromResult(Contents.GetValueOrDefault(ServerFilePaths.ValidateRelative(relativePath)));
        }
    }

    private sealed class TestProxyContainerRuntime : IProxyContainerRuntime
    {
        public ProxyContainerState State { get; set; } = ProxyContainerState.Missing;

        public string? Health { get; set; }

        public string? FailUpMessage { get; set; }

        public string? InspectFailure { get; set; }

        public List<string> Calls { get; } = [];

        public Task<ProxyContainerStatus> InspectAsync(
            ServerExecutionTarget target,
            CancellationToken cancellationToken = default)
        {
            if (InspectFailure is not null)
                throw new InvalidOperationException(InspectFailure);
            return Task.FromResult(new ProxyContainerStatus(State, Health, ComponentVersions.TraefikImage, []));
        }

        public Task EnsureNetworkAsync(
            ServerExecutionTarget target,
            string network,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"network:{network}");
            return Task.CompletedTask;
        }

        public Task UpAsync(
            ServerExecutionTarget target,
            string composeHostPath,
            bool forceRecreate,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"up:{composeHostPath}:{forceRecreate}");
            if (FailUpMessage is not null)
                throw new InvalidOperationException(FailUpMessage);
            State = ProxyContainerState.Running;
            return Task.CompletedTask;
        }

        public Task StopAsync(ServerExecutionTarget target, CancellationToken cancellationToken = default)
        {
            Calls.Add("stop");
            State = ProxyContainerState.Missing;
            return Task.CompletedTask;
        }

        public Task ConnectNetworkAsync(
            ServerExecutionTarget target,
            string network,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"connect:{network}");
            return Task.CompletedTask;
        }
    }

    private sealed class TestProxyRuntimeRenderer : IProxyRuntimeRenderer
    {
        public string? LastHash { get; private set; }

        public ServerProxyType ProxyType => ServerProxyType.Traefik;

        public ProxyRuntimeBundle Render(ProxyRuntimeRenderRequest request)
        {
            var hash = $"h-{request.Image}-{request.HttpPort}-{request.HttpsPort}-{request.Network}";
            LastHash = hash;
            return new ProxyRuntimeBundle(
                [
                    new ProxyRuntimeDirectory(ProxyLayout.Directory, ServerFileModes.WorldReadableDirectory),
                    new ProxyRuntimeDirectory(ProxyLayout.DynamicDirectory, ServerFileModes.WorldReadableDirectory),
                    new ProxyRuntimeDirectory(ProxyLayout.AcmeDirectory, ServerFileModes.OwnerOnlyDirectory)
                ],
                [
                    new ProxyRuntimeFile(ProxyLayout.StaticConfigurationFile, "static", ServerFileModes.WorldReadable),
                    new ProxyRuntimeFile(ProxyLayout.ComposeFile, $"compose {request.Image}",
                        ServerFileModes.WorldReadable),
                    new ProxyRuntimeFile(ProxyLayout.AcmeStorageFile, string.Empty, ServerFileModes.OwnerReadWrite,
                        true)
                ],
                ProxyLayout.ComposeFile,
                hash);
        }
    }
}
