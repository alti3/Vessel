using System.Runtime.CompilerServices;
using System.Text;
using Vessel.Application.Deployments;
using Vessel.Application.Docker;
using Vessel.Application.Processes;
using Vessel.Application.Servers;
using Vessel.Domain;
using Vessel.Domain.Servers;
using Vessel.Infrastructure.Deployments;
using Vessel.Infrastructure.Docker;
using Vessel.Infrastructure.Files;
using YamlDotNet.RepresentationModel;

namespace Vessel.IntegrationTests.Deployments;

/// <summary>
///     DEP-09 (build values never on a command line, isolated registry login), DEP-11/SEC-11 (compose policy, include,
///     scale) and SEC-10 (runtime argument validation).
/// </summary>
public sealed class BuildPackRuntimeTests
{
    private const string Network = "vessel-server";
    private const string Alias = "vessel-app-0123";
    private static readonly string Repository = Path.Combine(Path.GetTempPath(), "vessel-compose-policy-repo");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly YamlComposeDocumentBuilder _builder = new(new PathSafetyService());

    [Fact]
    public async Task Local_build_passes_build_argument_names_only_and_values_through_an_allowlisted_environment()
    {
        var local = new RecordingRunner();
        var client = new DockerCliContainerRuntimeClient(local);
        var target = new ContainerRuntimeTarget(ContainerRuntimeProvider.Docker);

        await foreach (ProcessOutputLine _ in client.BuildImageAsync(target, new DockerBuildCommand("/work",
                           "Dockerfile", "vessel/app:abc1234", new Dictionary<string, string>(),
                           new Dictionary<string, string> { ["API_TOKEN"] = "s3cr3t value" }), Ct))
        {
        }

        ProcessCommand command = Assert.Single(local.Commands);
        Assert.Contains("--build-arg", command.Arguments);
        Assert.Contains("API_TOKEN", command.Arguments);
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains("s3cr3t", StringComparison.Ordinal));
        Assert.False(command.InheritEnvironment);
        Assert.Equal("s3cr3t value", command.Environment!["API_TOKEN"]);
        Assert.Contains("s3cr3t value", command.Redaction!.SecretValues);
    }

    [Fact]
    public async Task Remote_build_sources_the_uploaded_values_file_instead_of_passing_values()
    {
        var remote = new RecordingServerRunner();
        var client = new DockerCliContainerRuntimeClient(new RecordingRunner(), remote);
        var target = new ContainerRuntimeTarget(ContainerRuntimeProvider.Docker, Remote: SshTarget());

        await foreach (ProcessOutputLine _ in client.BuildImageAsync(target, new DockerBuildCommand("/srv/x/repo",
                           "Dockerfile", "vessel/app:abc1234", new Dictionary<string, string>(),
                           new Dictionary<string, string> { ["API_TOKEN"] = "s3cr3t" }, BuildArgumentsFile:
                           "/srv/x/build/build.env"), Ct))
        {
        }

        IReadOnlyList<string> command = Assert.Single(remote.Streamed);
        Assert.Equal(["sh", "-c", DockerCliContainerRuntimeClient.SourceBuildEnvironmentScript, "vessel-build-env",
            "/srv/x/build/build.env", "docker", "build"], command.Take(7));
        Assert.DoesNotContain(command, argument => argument.Contains("s3cr3t", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => DockerCliContainerRuntimeClient.RemoteArguments("docker", [],
            new BuildEnvironment([new("A", "b")])));
    }

    [Fact]
    public async Task Registry_push_logs_in_with_an_isolated_config_and_the_password_on_standard_input()
    {
        var local = new RecordingRunner();
        var client = new DockerCliContainerRuntimeClient(local);
        var target = new ContainerRuntimeTarget(ContainerRuntimeProvider.Docker);

        await foreach (ProcessOutputLine _ in client.PushImageAsync(target, new RegistryImageCommand(
                           "ghcr.io/acme/web:abc1234", new RegistryAuth("ghcr.io", "bot", "pa55word"), "/work/registry"),
                           Ct))
        {
        }

        Assert.Equal(["--config", "/work/registry", "login", "--username", "bot", "--password-stdin", "ghcr.io"],
            local.Commands[0].Arguments);
        Assert.Equal("pa55word", local.Inputs[0]);
        Assert.Equal(["--config", "/work/registry", "push", "ghcr.io/acme/web:abc1234"], local.Commands[1].Arguments);
        Assert.Equal(["--config", "/work/registry", "logout", "ghcr.io"], local.Commands[2].Arguments);
        Assert.DoesNotContain(local.Commands.SelectMany(command => command.Arguments),
            argument => argument.Contains("pa55word", StringComparison.Ordinal));
        Assert.Equal(["push", "--authfile", "/r/auth.json", "x:1"],
            DockerCliContainerRuntimeClient.RegistryArguments(ContainerRuntimeProvider.Podman, "/r", "push", "x:1"));
    }

    [Theory]
    [InlineData("--privileged")]
    [InlineData("Upper:tag")]
    [InlineData("image:tag with space")]
    [InlineData("")]
    public async Task Runtime_client_rejects_unsafe_image_references(string image)
    {
        var client = new DockerCliContainerRuntimeClient(new RecordingRunner());
        var target = new ContainerRuntimeTarget(ContainerRuntimeProvider.Docker);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (ProcessOutputLine _ in client.BuildImageAsync(target, new DockerBuildCommand("/w",
                               "Dockerfile", image, new Dictionary<string, string>(),
                               new Dictionary<string, string>()), Ct))
            {
            }
        });
        await Assert.ThrowsAnyAsync<Exception>(() => client.RemoveImageAsync(target, "-f", Ct));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.EnsureNetworkAsync(target, "--driver=host", new Dictionary<string, string>(), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => client.InspectContainerAsync(target, "-x", Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => client.RemoveComposeProjectAsync(target, "Bad Name", false, Ct));
    }

    [Fact]
    public async Task Railpack_requires_docker_and_passes_build_values_as_named_secrets()
    {
        var local = new RecordingRunner { FailFirst = true };
        var client = new DockerCliContainerRuntimeClient(local);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (ProcessOutputLine _ in client.BuildRailpackImageAsync(
                               new ContainerRuntimeTarget(ContainerRuntimeProvider.Podman), Railpack(), Ct))
            {
            }
        });

        await foreach (ProcessOutputLine _ in client.BuildRailpackImageAsync(
                           new ContainerRuntimeTarget(ContainerRuntimeProvider.Docker), Railpack(), Ct))
        {
        }

        Assert.Equal(["buildx", "inspect", "vessel-railpack"], local.Commands[0].Arguments);
        Assert.Contains("image=moby/buildkit:v1", local.Commands[1].Arguments);
        IReadOnlyList<string> build = local.Commands[2].Arguments;
        Assert.Contains("BUILDKIT_SYNTAX=ghcr.io/railwayapp/railpack-frontend:v1", build);
        Assert.Contains("id=TOKEN,env=TOKEN", build);
        Assert.DoesNotContain(build, argument => argument.Contains("hidden", StringComparison.Ordinal));
        Assert.Equal("hidden", local.Commands[2].Environment!["TOKEN"]);
        Assert.Equal(["buildx", "stop", "vessel-railpack"], local.Commands[3].Arguments);
    }

    private static RailpackBuildCommand Railpack()
    {
        return new RailpackBuildCommand("/w/repo", "/w/build/railpack/plan.json", "vessel/app:abc1234",
            "ghcr.io/railwayapp/railpack-frontend:v1", "moby/buildkit:v1", "vessel-railpack",
            new Dictionary<string, string>(), new BuildEnvironment([new("TOKEN", "hidden")]), "app");
    }

    [Theory]
    [InlineData("services:\n  web:\n    image: x\n    privileged: true\n")]
    [InlineData("services:\n  web:\n    image: x\n  w:\n    image: y\n    pid: host\n")]
    [InlineData("services:\n  web:\n    image: x\n    cap_add: [NET_ADMIN]\n")]
    [InlineData("services:\n  web:\n    image: x\n    devices: [\"/dev/kvm:/dev/kvm\"]\n")]
    [InlineData("services:\n  web:\n    image: x\n    security_opt: [\"seccomp:unconfined\"]\n")]
    [InlineData("services:\n  web:\n    image: x\n    volumes: [\"/var/run/docker.sock:/var/run/docker.sock\"]\n")]
    [InlineData("services:\n  web:\n    image: x\n    volumes: [data:/data]\nvolumes:\n  data:\n    driver_opts:\n      type: none\n      o: bind\n      device: /etc\n")]
    public void Host_level_settings_need_an_instance_administrator(string yaml)
    {
        var error = Assert.Throws<ComposeValidationException>(() => _builder.NormalizeRepositoryCompose(yaml, Spec()));
        Assert.Contains("instance administrator", error.Message, StringComparison.Ordinal);

        ComposeNormalizationResult allowed = _builder.NormalizeRepositoryCompose(yaml, Spec() with { AllowElevated = true });
        Assert.NotEmpty(allowed.ElevatedSettings);
    }

    [Fact]
    public void Runtime_socket_mount_is_reported_as_host_root_equivalent()
    {
        const string yaml = "services:\n  web:\n    image: x\n    volumes: [\"/var/run/docker.sock:/var/run/docker.sock\"]\n";

        ComposeNormalizationResult result =
            _builder.NormalizeRepositoryCompose(yaml, Spec() with { AllowElevated = true });

        Assert.Contains(result.ElevatedSettings, setting => setting.Contains("host-root", StringComparison.Ordinal));
        Assert.Contains("/var/run/docker.sock:/var/run/docker.sock", result.Yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("services:\n  web:\n    image: x\n    scale: 2\n")]
    [InlineData("services:\n  web:\n    image: x\n    deploy:\n      replicas: 3\n")]
    [InlineData("services:\n  web:\n    image: x\n    container_name: \"-bad\"\n")]
    [InlineData("services:\n  web:\n    image: x\nnetworks:\n  \"bad name\": {}\n")]
    public void Replicas_and_invalid_names_are_rejected_even_for_administrators(string yaml)
    {
        Assert.Throws<ComposeValidationException>(() =>
            _builder.NormalizeRepositoryCompose(yaml, Spec() with { AllowElevated = true }));
    }

    [Fact]
    public void Included_files_are_flattened_with_paths_relative_to_the_included_file()
    {
        const string root = "include: [services/db.yml]\nservices:\n  web:\n    build: .\n";
        const string db = "services:\n  db:\n    image: postgres:18\n    volumes: [\"./init:/docker-entrypoint-initdb.d\"]\n";

        Assert.Equal(["services/db.yml"], _builder.ListIncludes(root, Repository, "docker-compose.yml"));
        ComposeNormalizationResult result = _builder.NormalizeRepositoryCompose(root, Spec() with
        {
            IncludedFiles = new Dictionary<string, string> { ["services/db.yml"] = db }
        });
        YamlMappingNode services = (YamlMappingNode)Parse(result.Yaml)["services"];

        Assert.True(services.Children.ContainsKey(new YamlScalarNode("db")));
        Assert.Contains(Path.Combine(Repository, "services", "init") + ":/docker-entrypoint-initdb.d", result.Yaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("include", result.Yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("include: [../outside.yml]\nservices:\n  web:\n    image: x\n")]
    [InlineData("include: [/etc/compose.yml]\nservices:\n  web:\n    image: x\n")]
    public void Includes_outside_the_repository_are_rejected(string yaml)
    {
        Assert.Throws<ComposeValidationException>(() => _builder.ListIncludes(yaml, Repository, "docker-compose.yml"));
    }

    [Fact]
    public void Included_service_that_redefines_a_root_service_is_rejected()
    {
        Assert.Throws<ComposeValidationException>(() => _builder.NormalizeRepositoryCompose(
            "include: [a.yml]\nservices:\n  web:\n    image: x\n",
            Spec() with { IncludedFiles = new Dictionary<string, string> { ["a.yml"] = "services:\n  web:\n    image: y\n" } }));
    }

    [Fact]
    public void Configured_public_service_and_build_arguments_are_applied()
    {
        const string yaml = "services:\n  web:\n    build: .\n    ports: [\"80:80\"]\n  admin:\n    build:\n      context: .\n      args:\n        API_URL: fixed\n";

        ComposeNormalizationResult result = _builder.NormalizeRepositoryCompose(yaml, Spec() with
        {
            PrimaryService = "admin",
            BuildArgumentKeys = ["API_URL", "TOKEN"]
        });
        YamlMappingNode services = (YamlMappingNode)Parse(result.Yaml)["services"];
        var adminArgs = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)services["admin"])["build"])["args"];
        var webArgs = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)services["web"])["build"])["args"];

        Assert.Equal("admin", result.PrimaryService);
        Assert.Equal("fixed", ((YamlScalarNode)adminArgs["API_URL"]).Value);
        Assert.Equal("${TOKEN}", ((YamlScalarNode)adminArgs["TOKEN"]).Value);
        Assert.Equal("${API_URL}", ((YamlScalarNode)webArgs["API_URL"]).Value);
        Assert.Throws<ComposeValidationException>(() =>
            _builder.NormalizeRepositoryCompose(yaml, Spec() with { PrimaryService = "missing" }));
    }

    [Fact]
    public void Repository_bind_mounts_and_external_volumes_produce_warnings()
    {
        const string yaml = "services:\n  web:\n    image: x\n    volumes: [\"./data:/data\", \"/tmp/cache\", \"shared:/s\"]\nvolumes:\n  shared:\n    external: true\n";

        ComposeNormalizationResult result = _builder.NormalizeRepositoryCompose(yaml, Spec());

        Assert.Contains(result.Warnings, warning => warning.Contains("removed after the deployment", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("anonymous volume", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("external", StringComparison.Ordinal));
    }

    [Fact]
    public void Built_images_get_a_stack_without_a_build_section()
    {
        var yaml = _builder.CreateDockerfileStack(new DockerfileStackSpec("vessel-app-0123", "vessel/app:abc1234",
            null, null, Network, Alias, "8080:8080", new Dictionary<string, string>()));
        var service = (YamlMappingNode)((YamlMappingNode)Parse(yaml)["services"])["vessel-app-0123"];

        Assert.False(service.Children.ContainsKey(new YamlScalarNode("build")));
        Assert.Equal("vessel/app:abc1234", ((YamlScalarNode)service["image"]).Value);
    }

    private static RepositoryComposeSpec Spec()
    {
        return new RepositoryComposeSpec(Repository, "docker-compose.yml", Network, Alias, 80,
            new Dictionary<string, string> { ["vessel.managed"] = "true" }, null);
    }

    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static ServerExecutionTarget SshTarget()
    {
        return new ServerExecutionTarget(ServerId.New(), ServerConnectionType.Ssh, "203.0.113.10", 22, "deploy",
            ContainerRuntimeKind.Docker);
    }

    private sealed class RecordingServerRunner : IServerCommandRunner
    {
        public List<IReadOnlyList<string>> Streamed { get; } = [];

        public Task<ServerCommandResult> RunAsync(ServerExecutionTarget target, IReadOnlyList<string> arguments,
            TimeSpan timeout, string? standardInput = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServerCommandResult(0, string.Empty, string.Empty));

        public Task<ServerCommandResult> RunWithKeyAsync(ServerExecutionTarget target, SshKeyId keyId,
            IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ProcessOutputLine> StreamAsync(ServerExecutionTarget target,
            IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout,
            IReadOnlyList<string>? secretValues = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Streamed.Add(arguments);
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessCommand> Commands { get; } = [];

        public List<string> Inputs { get; } = [];

        public bool FailFirst { get; set; }

        public Task<ProcessResult> RunTextAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            var exitCode = FailFirst ? 1 : 0;
            FailFirst = false;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(command, new ProcessExitInfo(exitCode, false, false, null), 1, now,
                now, string.Empty, string.Empty));
        }

        public Task<ProcessExitInfo> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProcessBinaryResult> RunBinaryAsync(ProcessCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProcessResult> RunTextWithInputAsync(ProcessCommand command, Stream standardInput,
            CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(standardInput, Encoding.UTF8);
            Inputs.Add(reader.ReadToEnd());
            return RunTextAsync(command, cancellationToken);
        }

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
