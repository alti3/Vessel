using System.Formats.Tar;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;
using Vessel.Application.Deployments;
using Vessel.Application.Docker;
using Vessel.Application.Processes;
using Vessel.Application.Servers;
using Vessel.Application.Ssh;
using Vessel.Application.Terminals;
using Vessel.Domain;
using Vessel.Domain.Servers;
using Vessel.Domain.Terminals;
using Vessel.Infrastructure.Deployments;
using Vessel.Infrastructure.Docker;
using Vessel.Infrastructure.Files;
using Vessel.Infrastructure.Processes;
using Vessel.Infrastructure.Security;
using Vessel.Infrastructure.Servers;
using Vessel.Infrastructure.Ssh;
using YamlDotNet.RepresentationModel;

namespace Vessel.IntegrationTests.Ssh;

/// <summary>SRV-02 / SEC-12 / SRV-06: hardened SSH arguments, remote runtime routing, transfers, keys and the PTY.</summary>
public sealed class RemoteExecutionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- SEC-12 fixed options --------------------------------------------------------------------------------

    [Fact]
    public void SshArguments_AlwaysCarryTheFixedHardenedOptions_AndEndOptionsBeforeTheDestination()
    {
        var options = new SshConnectionOptions("203.0.113.10", 2222, "deploy", "/var/lib/vessel/ssh/keys/key_1",
            HostIdentityPolicy: SshHostIdentityPolicy.RequireKnownHost,
            KnownHostsPath: "/var/lib/vessel/ssh/known_hosts/known_hosts_x_1", HostKeyAlias: "vessel-server-x");

        List<string> args = SshArguments.ForSsh(options, "'docker' 'ps'");

        foreach (var expected in new[]
                 {
                     "BatchMode=yes", "IdentitiesOnly=yes", "PasswordAuthentication=no",
                     "KbdInteractiveAuthentication=no", "ClearAllForwardings=yes", "ForwardAgent=no", "ForwardX11=no",
                     "ServerAliveInterval=15", "ConnectTimeout=15", "StrictHostKeyChecking=yes",
                     "UserKnownHostsFile=/var/lib/vessel/ssh/known_hosts/known_hosts_x_1",
                     "GlobalKnownHostsFile=/dev/null", "HostKeyAlias=vessel-server-x", "RequestTTY=no"
                 })
            Assert.Contains(expected, args);
        Assert.Equal("/dev/null", args[args.IndexOf("-F") + 1]);
        Assert.Equal("/var/lib/vessel/ssh/keys/key_1", args[args.IndexOf("-i") + 1]);
        Assert.Equal("2222", args[args.IndexOf("-p") + 1]);
        var separator = args.IndexOf("--");
        Assert.Equal("deploy@203.0.113.10", args[separator + 1]);
        Assert.Equal("'docker' 'ps'", args[^1]);
        Assert.True(args.IndexOf("-i") < separator);
    }

    [Theory]
    [InlineData(SshHostIdentityPolicy.WarnOnMismatch, "StrictHostKeyChecking=no", true)]
    [InlineData(SshHostIdentityPolicy.InsecureSkipValidation, "StrictHostKeyChecking=no", false)]
    [InlineData(SshHostIdentityPolicy.RequireKnownHost, "StrictHostKeyChecking=yes", true)]
    public void SshArguments_MapHostKeyModes(SshHostIdentityPolicy policy, string strictness, bool pinnedFile)
    {
        var options = new SshConnectionOptions("server.example.com", 22, "root", HostIdentityPolicy: policy,
            KnownHostsPath: "/tmp/known", HostKeyAlias: "alias");

        List<string> args = SshArguments.ForSsh(options, null);

        Assert.Contains(strictness, args);
        Assert.Equal(pinnedFile, args.Contains("UserKnownHostsFile=/tmp/known"));
        if (!pinnedFile) Assert.Contains("UserKnownHostsFile=/dev/null", args);
    }

    [Fact]
    public void ScpArguments_UseUpperCasePortFlag()
    {
        var options = new SshConnectionOptions("203.0.113.10", 2200, "deploy");

        List<string> args = SshArguments.ForScp(options, "/local/file",
            SshArguments.RemotePath(options, "/remote/file"));

        Assert.Equal("2200", args[args.IndexOf("-P") + 1]);
        Assert.DoesNotContain("-p", args);
        Assert.Equal(["--", "/local/file", "deploy@203.0.113.10:/remote/file"], args[^3..]);
    }

    [Fact]
    public async Task SshProcessClient_UploadPassesThePortWithUpperCaseFlag()
    {
        var runner = new RecordingRunner();
        var client = new SshProcessClient(runner);

        await client.UploadAsync(new SshFileTransferRequest(new SshConnectionOptions("203.0.113.10", 2201, "deploy"),
            "/tmp/a", "/srv/a"), Ct);

        ProcessCommand command = Assert.Single(runner.Commands);
        Assert.Equal("scp", command.FileName);
        Assert.Equal("2201", command.Arguments[command.Arguments.ToList().IndexOf("-P") + 1]);
    }

    [Theory]
    [InlineData("-oProxyCommand=touch /tmp/pwned", "root")]
    [InlineData("203.0.113.10", "-oProxyCommand=x")]
    [InlineData("host name", "root")]
    public void SshArguments_RejectOptionInjection(string host, string user)
    {
        Assert.Throws<InvalidOperationException>(() =>
            SshArguments.ForSsh(new SshConnectionOptions(host, 22, user), "true"));
    }

    [Fact]
    public void HostKeyScan_LogsInWithNothing_AndPinsUnderTheAlias()
    {
        List<string> args = SshHostKeyScanner.BuildArguments("203.0.113.10", 22, "deploy", "vessel-server-1", "/s/scan");

        Assert.Contains("StrictHostKeyChecking=accept-new", args);
        Assert.Contains("PubkeyAuthentication=no", args);
        Assert.Contains("PasswordAuthentication=no", args);
        Assert.Contains("HostKeyAlias=vessel-server-1", args);
        Assert.Contains("UserKnownHostsFile=/s/scan", args);
        Assert.Equal("deploy@203.0.113.10", args[args.IndexOf("--") + 1]);
    }

    // ---- SRV-02 single remote execution path ----------------------------------------------------------------

    [Fact]
    public async Task ServerCommandExecutor_UsesTheConnectionFactory_AndReportsHostKeyChanges()
    {
        var factory = new RecordingFactory();
        var ssh = new ScriptedSsh(new SshCommandResult(0, "ok", "@@@ WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED"));
        var executor = new ServerCommandExecutor(new RecordingRunner(), ssh, factory);
        ServerExecutionTarget target = SshTarget();

        ServerCommandResult result = await executor.RunAsync(target, ["docker", "ps", "--format", "{{.Names}}"],
            TimeSpan.FromSeconds(5), null, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(1, factory.Acquired);
        Assert.Equal(1, factory.Released);
        Assert.Equal("'docker' 'ps' '--format' '{{.Names}}'", Assert.Single(ssh.Requests).Command);
        Assert.Same(factory.Options, ssh.Requests[0].Connection);
        Assert.Contains("REMOTE HOST IDENTIFICATION", Assert.Single(factory.Observed));
    }

    [Fact]
    public void WorkingDirectoryWrapper_PassesTheDirectoryAsAnArgument()
    {
        IReadOnlyList<string> wrapped = ServerCommandExecutor.WithWorkingDirectory(["docker", "build", "."],
            "/var/lib/vessel/deployments/x; rm -rf /");

        Assert.Equal(["sh", "-c", ServerCommandExecutor.WorkingDirectoryScript, "vessel",
            "/var/lib/vessel/deployments/x; rm -rf /", "docker", "build", "."], wrapped);
        Assert.Throws<ArgumentException>(() => ServerCommandExecutor.WithWorkingDirectory(["true"], "relative"));
    }

    [Fact]
    public async Task DockerCli_RoutesRemoteTargetsOverTheServerRunner_NeverTheLocalRuntime()
    {
        var local = new RecordingRunner();
        var remote = new RecordingServerRunner("""{"Client":{},"Server":{"Version":"27.3.1","ApiVersion":"1.47","Os":"linux","Arch":"amd64"}}""");
        var client = new DockerCliContainerRuntimeClient(local, remote);
        var target = new ContainerRuntimeTarget(ContainerRuntimeProvider.Docker, Remote: SshTarget());

        ContainerRuntimeInfo info = await client.GetInfoAsync(target, Ct);

        Assert.Empty(local.Commands);
        Assert.Equal(["docker", "version", "--format", "{{json .}}"], Assert.Single(remote.Commands));
        Assert.Equal("27.3.1", info.Version);
        Assert.Equal("1.47", info.ApiVersion);
    }

    [Fact]
    public void DockerCli_ParsesPodmanVersionAndContainerLabels()
    {
        ContainerRuntimeInfo podman = DockerCliContainerRuntimeClient.ParseVersion(ContainerRuntimeProvider.Podman,
            """{"Client":{},"Server":{"APIVersion":"4.9.3","Version":"4.9.3","OsArch":"linux/arm64"}}""");

        Assert.Equal("4.9.3", podman.Version);
        Assert.Equal("linux", podman.OperatingSystem);
        Assert.Equal("arm64", podman.Architecture);
        Assert.Throws<InvalidOperationException>(() =>
            DockerCliContainerRuntimeClient.ParseVersion(ContainerRuntimeProvider.Docker, """{"Client":{}}"""));
        IReadOnlyDictionary<string, string> labels =
            DockerCliContainerRuntimeClient.ParseLabels("vessel.application=abc,com.docker.compose.project=p");
        Assert.Equal("abc", labels["vessel.application"]);
        Assert.Equal("p", labels["com.docker.compose.project"]);
    }

    [Fact]
    public void ComposeNormalization_WritesPathsUnderTheUploadedRepositoryOfAnSshServer()
    {
        var repository = Path.Combine(Path.GetTempPath(), "vessel-remote-compose-repo");
        var builder = new YamlComposeDocumentBuilder(new PathSafetyService());
        const string yaml = """
                            services:
                              web:
                                build: ./app
                                volumes:
                                  - ./data:/data
                            """;

        ComposeNormalizationResult result = builder.NormalizeRepositoryCompose(yaml, new RepositoryComposeSpec(
            repository, "docker-compose.yml", "vessel-net", "alias", 3000, new Dictionary<string, string>(), null,
            "/var/lib/vessel/deployments/abc/repository"));

        Assert.Contains("/var/lib/vessel/deployments/abc/repository/app", result.Yaml, StringComparison.Ordinal);
        Assert.Contains("/var/lib/vessel/deployments/abc/repository/data:/data", result.Yaml, StringComparison.Ordinal);
        Assert.DoesNotContain(repository, result.Yaml, StringComparison.Ordinal);
        _ = new YamlStream();
    }

    [Fact]
    public async Task DirectoryArchive_KeepsInsideLinksOwnerOnlyModes_AndDropsEscapingLinks()
    {
        using var root = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "src", "app.txt"), "hello", Ct);
        await File.WriteAllTextAsync(Path.Combine(root.Path, ".env"), "SECRET=1", Ct);
        File.CreateSymbolicLink(Path.Combine(root.Path, "inside"), "src/app.txt");
        File.CreateSymbolicLink(Path.Combine(root.Path, "escape"), "../../etc/passwd");
        File.CreateSymbolicLink(Path.Combine(root.Path, "absolute"), "/etc/passwd");

        await using var archive = new MemoryStream();
        await SshServerDirectoryTransfer.WriteArchiveAsync(root.Path, archive, Ct);
        archive.Position = 0;
        var entries = new Dictionary<string, TarEntry>(StringComparer.Ordinal);
        await using (var reader = new TarReader(archive))
        {
            while (await reader.GetNextEntryAsync(cancellationToken: Ct) is { } entry)
                entries[entry.Name] = entry;
        }

        Assert.Contains("src/app.txt", entries.Keys);
        Assert.Contains(".env", entries.Keys);
        Assert.Equal(TarEntryType.SymbolicLink, entries["inside"].EntryType);
        Assert.DoesNotContain("escape", entries.Keys);
        Assert.DoesNotContain("absolute", entries.Keys);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, entries[".env"].Mode);
    }

    // ---- SEC-12 runtime files and keys ----------------------------------------------------------------------

    [Fact]
    public void RuntimeFiles_AreOwnerOnly_AndKnownHostsFilesReplaceStaleOnes()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TemporaryDirectory();
        var files = new SshRuntimeFiles(Options.Create(new SshOptions { RuntimeDirectory = root.Path }));
        var serverId = ServerId.New();

        var key = files.WritePrivateKey("-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----");
        var first = files.WriteKnownHosts(serverId, "alias ssh-ed25519 AAAA\n");
        var second = files.WriteKnownHosts(serverId, "alias ssh-ed25519 BBBB\n");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(second));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(files.KeysDirectory));
        Assert.False(File.Exists(first));
        files.Delete(key);
        Assert.False(File.Exists(key));
        files.Delete("/etc/passwd"); // outside the runtime root: ignored
        Assert.True(File.Exists("/etc/passwd"));
    }

    [Fact]
    public async Task SshKeygen_GeneratesEd25519_DerivesPublicKeys_AndRejectsEncryptedKeys()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/ssh-keygen")) return;
        using var root = new TemporaryDirectory();
        var files = new SshRuntimeFiles(Options.Create(new SshOptions { RuntimeDirectory = root.Path }));
        var tool = new SshKeygenTool(new DotNetProcessRunner(new SecretRedactor(), TimeProvider.System), files);

        GeneratedSshKey generated = await tool.GenerateEd25519Async("vessel test", Ct);
        Assert.StartsWith("ssh-ed25519 ", generated.PublicKey, StringComparison.Ordinal);
        Assert.Null(SshKeyFormats.ValidatePrivateKeyText(generated.PrivateKey));
        var derived = await tool.DerivePublicKeyAsync(generated.PrivateKey, Ct);
        Assert.Equal(SshKeyFormats.ParsePublicKey(generated.PublicKey).Fingerprint,
            SshKeyFormats.ParsePublicKey(derived!).Fingerprint);
        Assert.Null(await tool.DerivePublicKeyAsync(
            "-----BEGIN OPENSSH PRIVATE KEY-----\nbm90IGEga2V5\n-----END OPENSSH PRIVATE KEY-----", Ct));
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.KeysDirectory));
    }

    // ---- SRV-06 PTY ------------------------------------------------------------------------------------------

    [Fact]
    public async Task PtyBridge_RunsAShellWithARealTerminal_AndResizes()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/setsid")) return;
        var bridge = new PtyTerminalBridge(new DotNetProcessRunner(new SecretRedactor(), TimeProvider.System));
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionId = TerminalSessionId.New();
        await bridge.OpenAsync(
            new TerminalBridgeOpenRequest(sessionId, ServerId.New(), TerminalTargetType.Server, null, "/bin/sh", 100,
                30, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), ServerConnectionType.Local, []),
            (chunk, _) =>
            {
                lock (output) output.Append(chunk.Data);
                return Task.CompletedTask;
            },
            (_, reason, _) =>
            {
                exited.TrySetResult(reason);
                return Task.CompletedTask;
            },
            Ct);

        try
        {
            Assert.True(bridge.IsOpen(sessionId));
            await bridge.SendInputAsync(sessionId, "tty; stty size\n", Ct);
            await WaitForAsync(output, "30 100");
            Assert.Contains("/dev/pts/", Text(output), StringComparison.Ordinal);

            await bridge.ResizeAsync(new TerminalBridgeResizeRequest(sessionId, 132, 40), Ct);
            await bridge.SendInputAsync(sessionId, "stty size\n", Ct);
            await WaitForAsync(output, "40 132");

            // The terminal environment is the minimal allowlist, never the daemon's.
            await bridge.SendInputAsync(sessionId, "echo TERM=$TERM; exit\n", Ct);
            await WaitForAsync(output, "TERM=xterm-256color");
            Assert.Null(await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            Assert.False(bridge.IsOpen(sessionId));
        }
        finally
        {
            await bridge.CloseAsync(sessionId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task PtyBridge_RefusesSshSessionsWithoutTheConnectionFactory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var bridge = new PtyTerminalBridge(new RecordingRunner());

        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.OpenAsync(
            new TerminalBridgeOpenRequest(TerminalSessionId.New(), ServerId.New(), TerminalTargetType.Server, null,
                "ssh", 80, 24, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), ServerConnectionType.Ssh, null,
                SshTarget()),
            (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, Ct));
    }

    private static async Task WaitForAsync(StringBuilder output, string expected)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Text(output).Contains(expected, StringComparison.Ordinal)) return;
            await Task.Delay(50, Ct);
        }

        Assert.Fail($"Terminal output did not contain '{expected}': {Text(output)}");
    }

    private static string Text(StringBuilder output)
    {
        lock (output) return output.ToString();
    }

    private static ServerExecutionTarget SshTarget()
    {
        return new ServerExecutionTarget(ServerId.New(), ServerConnectionType.Ssh, "203.0.113.10", 22, "deploy",
            ContainerRuntimeKind.Docker);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vessel-ssh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class RecordingFactory : IServerSshConnectionFactory
    {
        public SshConnectionOptions Options { get; } = new("203.0.113.10", 22, "deploy");
        public int Acquired { get; private set; }
        public int Released { get; private set; }
        public List<string?> Observed { get; } = [];

        public Task<SshConnectionLease> AcquireAsync(ServerExecutionTarget target, SshKeyId? keyOverride = null,
            CancellationToken cancellationToken = default)
        {
            Acquired++;
            return Task.FromResult(new SshConnectionLease(Options, () =>
            {
                Released++;
                return ValueTask.CompletedTask;
            }));
        }

        public Task ObserveAsync(ServerExecutionTarget target, string? standardError,
            CancellationToken cancellationToken = default)
        {
            Observed.Add(standardError);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedSsh(SshCommandResult result) : ISshClient
    {
        public List<SshCommandRequest> Requests { get; } = [];

        public Task<bool> TestConnectionAsync(SshConnectionOptions options,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<SshCommandResult> RunCommandAsync(SshCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }

        public Task<SshCommandResult> RunCommandWithInputAsync(SshCommandRequest request, Stream standardInput,
            CancellationToken cancellationToken = default) => RunCommandAsync(request, cancellationToken);

        public Task UploadAsync(SshFileTransferRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DownloadAsync(SshFileTransferRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingServerRunner(string output) : IServerCommandRunner
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

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessCommand> Commands { get; } = [];

        public Task<ProcessResult> RunTextAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(command, new ProcessExitInfo(0, false, false, null), 1, now, now,
                string.Empty, string.Empty));
        }

        public Task<ProcessExitInfo> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProcessBinaryResult> RunBinaryAsync(ProcessCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProcessResult> RunTextWithInputAsync(ProcessCommand command, Stream standardInput,
            CancellationToken cancellationToken = default) => RunTextAsync(command, cancellationToken);

        public async IAsyncEnumerable<ProcessOutputLine> StreamLinesAsync(ProcessCommand command,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            await Task.CompletedTask;
            yield break;
        }

        public Task<IInteractiveProcess> StartInteractiveAsync(ProcessCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
