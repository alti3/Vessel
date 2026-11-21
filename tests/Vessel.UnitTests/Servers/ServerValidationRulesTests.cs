using Vessel.Application.Servers.Validation;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Servers;
using Vessel.Domain.Terminals;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Servers;

/// <summary>SRV-02: OS detection, version gate and install guidance; SEC-12 host key domain rules.</summary>
public sealed class ServerValidationRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseOsRelease_ReadsQuotedValuesAndIdLike()
    {
        OsRelease release = ServerValidationRules.ParseOsRelease("""
                                                                 NAME="Pop!_OS"
                                                                 VERSION_ID="22.04"
                                                                 ID=pop
                                                                 ID_LIKE="ubuntu debian"
                                                                 PRETTY_NAME='Pop!_OS 22.04 LTS'
                                                                 # comment
                                                                 """);

        Assert.Equal("pop", release.Id);
        Assert.Equal("ubuntu debian", release.IdLike);
        Assert.Equal("22.04", release.VersionId);
        Assert.Equal("Pop!_OS 22.04 LTS", release.PrettyName);
        // Derivatives are accepted through ID_LIKE (upstream v4.5.0).
        Assert.True(ServerValidationRules.IsSupported(release));
    }

    [Theory]
    [InlineData("ID=alpine", true)]
    [InlineData("ID=rocky\nID_LIKE=\"rhel centos fedora\"", true)]
    [InlineData("ID=freebsd", false)]
    [InlineData("", false)]
    public void IsSupported_UsesIdAndIdLike(string content, bool supported)
    {
        Assert.Equal(supported, ServerValidationRules.IsSupported(ServerValidationRules.ParseOsRelease(content)));
    }

    [Theory]
    [InlineData(ContainerRuntimeKind.Docker, "23.0.6", false)]
    [InlineData(ContainerRuntimeKind.Docker, "24.0.0", true)]
    [InlineData(ContainerRuntimeKind.Docker, "27.3.1", true)]
    [InlineData(ContainerRuntimeKind.Docker, "v28.0.0-rc.1", true)]
    [InlineData(ContainerRuntimeKind.Docker, "garbage", false)]
    [InlineData(ContainerRuntimeKind.Docker, null, false)]
    [InlineData(ContainerRuntimeKind.Podman, "3.4.4", false)]
    [InlineData(ContainerRuntimeKind.Podman, "4.9.3", true)]
    public void CheckRuntimeVersion_RequiresDocker24OrPodman4(ContainerRuntimeKind runtime, string? version,
        bool accepted)
    {
        Assert.Equal(accepted, ServerValidationRules.CheckRuntimeVersion(runtime, version) is null);
    }

    [Theory]
    [InlineData("apt-get", true)]
    [InlineData("dnf", true)]
    [InlineData("apk", true)]
    [InlineData("pacman", true)]
    [InlineData("emerge", false)]
    [InlineData(null, false)]
    public void DockerInstallScript_OnlyForKnownPackageManagers(string? packageManager, bool available)
    {
        Assert.Equal(available, ServerValidationRules.DockerInstallScript(packageManager) is not null);
        Assert.False(string.IsNullOrWhiteSpace(
            ServerValidationRules.InstallGuidance(packageManager, ContainerRuntimeKind.Docker)));
    }

    [Fact]
    public void HostKey_PinsOnFirstUse_RecordsChangeOnce_AndTrustOrResetResolvesIt()
    {
        Server server = SshServer();

        Assert.True(server.PinHostKey("ssh-ed25519 AAAA", Now));
        Assert.False(server.PinHostKey("ssh-ed25519 BBBB", Now)); // already pinned: never silently replaced
        Assert.Equal("ssh-ed25519 AAAA", server.PinnedHostKey);

        Assert.False(server.RecordHostKeyChange("ssh-ed25519 AAAA", Now)); // same key
        server.ClearDomainEvents();
        Assert.True(server.RecordHostKeyChange("ssh-ed25519 BBBB", Now));
        Assert.False(server.RecordHostKeyChange("ssh-ed25519 BBBB", Now)); // reported once
        Assert.Single(server.DomainEvents.OfType<ServerHostKeyChangedEvent>());
        Assert.Equal("ssh-ed25519 AAAA", server.PinnedHostKey);
        Assert.Equal("ssh-ed25519 BBBB", server.PendingHostKey);

        server.TrustPendingHostKey(Now);
        Assert.Equal("ssh-ed25519 BBBB", server.PinnedHostKey);
        Assert.Null(server.PendingHostKey);
        Assert.Throws<DomainException>(() => server.TrustPendingHostKey(Now));

        server.ResetHostKey(Now);
        Assert.Null(server.PinnedHostKey);
        Assert.True(server.PinHostKey("ssh-rsa CCCC", Now));
    }

    [Fact]
    public void HostKey_PresentedSubsetOfPinnedKeysIsNotAChange()
    {
        Server server = SshServer();
        server.PinHostKey("ssh-ed25519 AAAA\nssh-rsa BBBB", Now);

        Assert.False(server.RecordHostKeyChange("ssh-rsa BBBB", Now));
        Assert.Null(server.PendingHostKey);
    }

    [Fact]
    public void NewServers_EnforceHostKeysAndAllowTerminalsByDefault()
    {
        Server server = SshServer();

        Assert.Equal(SshHostKeyCheckingMode.Enforce, server.HostKeyChecking);
        Assert.True(server.TerminalAccessEnabled);
        Assert.Null(server.SshKeyId);
    }

    [Fact]
    public void ContainerTerminalSession_RequiresOwningResource_AndServerSessionsOnlyShell()
    {
        Assert.Throws<DomainException>(() => TerminalSession.Open(TeamId.New(), UserId.New(), ServerId.New(),
            TerminalTargetType.Container, "app-1", "docker exec", 80, 24, Now));
        Assert.Throws<DomainException>(() => TerminalSession.Open(TeamId.New(), UserId.New(), ServerId.New(),
            TerminalTargetType.Server, null, "sh", 80, 24, Now, TerminalMode.Attach));

        TerminalSession session = TerminalSession.Open(TeamId.New(), UserId.New(), ServerId.New(),
            TerminalTargetType.Container, "app-1", "docker attach app-1", 80, 24, Now, TerminalMode.Attach,
            TerminalResourceKind.Application, Guid.NewGuid());
        Assert.Equal(TerminalMode.Attach, session.Mode);
        Assert.Equal(TerminalResourceKind.Application, session.ResourceKind);
    }

    private static Server SshServer()
    {
        return Server.Create(TeamId.New(), new ResourceName("remote"),
            new ServerAddress("203.0.113.10", new PortNumber(22), "deploy"), ServerConnectionType.Ssh,
            ContainerRuntimeKind.Docker, Now);
    }
}
