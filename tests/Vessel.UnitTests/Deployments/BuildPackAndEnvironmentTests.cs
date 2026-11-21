using Vessel.Application.Common;
using Vessel.Application.Deployments;
using Vessel.Application.Deployments.BuildPacks;
using Vessel.Application.EnvironmentVariables;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Common;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Deployments;

/// <summary>DEP-09 build packs and build values, DEP-10 environment value semantics, SEC-10 runtime names.</summary>
public sealed class BuildPackAndEnvironmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("")]
    [InlineData("  padded  ")]
    [InlineData("line1\nline2\n")]
    [InlineData("p@$$w0rd\"'\\")]
    public void Plain_values_are_stored_exactly(string value)
    {
        EnvironmentVariable variable = Plain("KEY", value);

        Assert.Equal(value, variable.PlainValue);
        Assert.False(variable.UsesLegacyEscaping);
    }

    [Fact]
    public void Nul_characters_and_missing_plain_values_are_rejected()
    {
        Assert.Throws<DomainException>(() => Plain("KEY", "a\0b"));
        Assert.Throws<DomainException>(() => Plain("KEY", null));
        EnvironmentVariable unset = EnvironmentVariable.Create(TeamId.New(), EnvironmentVariableTargetType.Team,
            new EnvironmentVariableKey("KEY"), EnvironmentVariableValueKind.Plain, null, null, false, true, false,
            false, false, null, Now, preserveComposeNull: true);
        Assert.True(unset.PreserveComposeNull);

        unset.Update(EnvironmentVariableValueKind.Plain, "now set", null, false, true, false, false, false, null, Now);
        Assert.False(unset.PreserveComposeNull);
    }

    [Theory]
    [InlineData("spring.datasource.url", false)]
    [InlineData("DATABASE_URL", true)]
    public void Dotted_keys_are_valid_but_not_shell_safe(string key, bool shellSafe)
    {
        Assert.Equal(shellSafe, new EnvironmentVariableKey(key).IsShellSafe);
    }

    [Fact]
    public void Env_file_writes_exact_double_quoted_values_and_leaves_preserved_nulls_unset()
    {
        var file = ComposeEnvFileWriter.Write([
            new ResolvedVariable("A", "x$y \"q\" \\", false, false, null),
            new ResolvedVariable("B", "cert\nline\n", true, false, null),
            new ResolvedVariable("C", "", false, false, null),
            new ResolvedVariable("UNSET", null, false, false, null),
            new ResolvedVariable("LEGACY", "two words", false, true, "two words")
        ]);

        Assert.Equal("A=\"x\\$y \\\"q\\\" \\\\\"\nB=\"cert\nline\n\"\nC=\"\"\nLEGACY=\"two words\"\n", file);
    }

    [Fact]
    public void Interpolation_is_done_by_vessel_against_deployment_variables_only()
    {
        ResolvedDeploymentEnvironment resolved = DeploymentEnvironmentResolver.Resolve([
            new DeploymentVariable("BASE", "https://example.com", IsDefault: true),
            new DeploymentVariable("URL", "${BASE}/api"),
            new DeploymentVariable("PRICE", "$$5 and $BASE"),
            new DeploymentVariable("LITERAL", "${BASE}", IsLiteral: true),
            new DeploymentVariable("SECRET", "$BASE", IsSecret: true),
            new DeploymentVariable("MISSING", "${NOPE:-fallback}|${PATH}"),
            new DeploymentVariable("URL", "${URL}/v2")
        ]);
        var values = resolved.Runtime.ToDictionary(variable => variable.Key, variable => variable.Value);

        Assert.Equal("https://example.com/api/v2", values["URL"]);
        Assert.Equal("$5 and https://example.com", values["PRICE"]);
        Assert.Equal("${BASE}", values["LITERAL"]);
        Assert.Equal("$BASE", values["SECRET"]);
        // The control plane's own PATH is never consulted.
        Assert.Equal("fallback|", values["MISSING"]);
        Assert.Contains("PATH", resolved.UnresolvedReferences);
        Assert.Equal(["BASE", "URL", "PRICE", "LITERAL", "SECRET", "MISSING"], resolved.Runtime.Select(item => item.Key));
        Assert.Throws<DomainException>(() =>
            EnvironmentValueInterpolator.Interpolate("${REQUIRED:?set it}", _ => null));
    }

    [Fact]
    public void Runtime_only_values_never_reach_the_build_and_build_only_values_never_reach_the_container()
    {
        ResolvedDeploymentEnvironment resolved = DeploymentEnvironmentResolver.Resolve([
            new DeploymentVariable("RUNTIME_ONLY", "r", IsBuildTime: false, IsRuntime: true),
            new DeploymentVariable("BUILD_ONLY", "b", IsBuildTime: true, IsRuntime: false),
            new DeploymentVariable("BOTH", "x", IsBuildTime: true, IsRuntime: true)
        ]);

        Assert.Equal(["RUNTIME_ONLY", "BOTH"], resolved.Runtime.Select(item => item.Key));
        Assert.Equal(["BUILD_ONLY", "BOTH"], resolved.Build.Select(item => item.Key));
    }

    [Fact]
    public void Build_values_skip_dotted_and_runtime_client_reserved_names()
    {
        BuildVariableSelection selection = BuildVariablePolicy.Select([
            new ResolvedVariable("NODE_ENV", "production", false, false, null),
            new ResolvedVariable("spring.profile", "x", false, false, null),
            new ResolvedVariable("DOCKER_HOST", "tcp://evil:2375", false, false, null),
            new ResolvedVariable("LD_PRELOAD", "/tmp/x.so", false, false, null),
            new ResolvedVariable("HOME", "/tmp", false, false, null),
            new ResolvedVariable("UNSET", null, false, false, null)
        ], "Railpack");

        Assert.Equal(["NODE_ENV"], selection.Values.Select(pair => pair.Key));
        Assert.Equal(4, selection.Warnings.Count);
        Assert.DoesNotContain(selection.Warnings, warning => warning.Contains("evil", StringComparison.Ordinal));
    }

    [Fact]
    public void Shell_env_file_single_quotes_values_exactly()
    {
        Assert.Equal("A='it'\\''s $HOME `x`'\nB=''\n",
            ShellEnvFileWriter.Write([new("A", "it's $HOME `x`"), new("B", "")]));
        Assert.Throws<ArgumentException>(() => ShellEnvFileWriter.Write([new("a.b", "x")]));
    }

    [Fact]
    public void Static_pack_serves_the_publish_directory_with_nginx_on_the_application_port()
    {
        BuildPlan plan = BuildPackPlanner.Plan(
            BuildConfiguration.Default(ApplicationBuildPack.Static) with
            {
                BaseDirectory = "/web", PublishDirectory = "dist", IsSpa = true
            }, 3000, []);

        Assert.Equal("web/dist", plan.ContextPath);
        DockerfileBuildStep step = Assert.IsType<DockerfileBuildStep>(Assert.Single(plan.Steps));
        Assert.True(step.InWorkspace);
        GeneratedBuildFile dockerfile = plan.Files.Single(file => file.WorkspacePath == BuildPackPlanner.StaticDockerfile);
        Assert.StartsWith($"FROM {ComponentVersions.StaticNginxImage}\n", dockerfile.Contents, StringComparison.Ordinal);
        Assert.Contains("EXPOSE 3000", dockerfile.Contents, StringComparison.Ordinal);
        var nginx = StaticSiteImage.NginxConfiguration(3000, true);
        Assert.Contains("listen 3000;", nginx, StringComparison.Ordinal);
        Assert.Contains("try_files $uri $uri/ /index.html;", nginx, StringComparison.Ordinal);
        Assert.Contains("location ~ /\\.(?!well-known)", nginx, StringComparison.Ordinal);
        Assert.Contains(plan.Files, file => file.WorkspacePath.EndsWith(".dockerignore", StringComparison.Ordinal) &&
                                            file.Contents.Contains(".git", StringComparison.Ordinal));
    }

    [Fact]
    public void Railpack_pack_prepares_then_builds_with_control_variables_and_health_check_packages()
    {
        BuildPlan plan = BuildPackPlanner.Plan(
            BuildConfiguration.Default(ApplicationBuildPack.Railpack) with
            {
                InstallCommand = "pnpm i", StartCommand = "node server.js"
            }, 8080, [new("RAILPACK_DEPLOY_APT_PACKAGES", "ffmpeg"), new("TOKEN", "x")]);

        RailpackPrepareStep prepare = Assert.IsType<RailpackPrepareStep>(plan.Steps[0]);
        Assert.IsType<RailpackBuildStep>(plan.Steps[1]);
        var control = prepare.ControlVariables.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("pnpm i", control["RAILPACK_INSTALL_CMD"]);
        Assert.Equal("... ffmpeg curl wget", control["RAILPACK_DEPLOY_APT_PACKAGES"]);
        Assert.False(control.ContainsKey("TOKEN"));
        Assert.Equal("node server.js", prepare.StartCommand);
    }

    [Fact]
    public void Nixpacks_still_builds_but_is_deprecated_and_compose_has_no_image_build()
    {
        BuildPlan nixpacks = BuildPackPlanner.Plan(BuildConfiguration.Default(ApplicationBuildPack.Nixpacks), 80, []);
        Assert.True(ApplicationBuildPacks.IsDeprecated(ApplicationBuildPack.Nixpacks));
        Assert.Contains(ApplicationBuildPacks.NixpacksDeprecationMessage, nixpacks.Notes);
        Assert.IsType<NixpacksGenerateStep>(nixpacks.Steps[0]);
        Assert.True(Assert.IsType<DockerfileBuildStep>(nixpacks.Steps[1]).Generated);

        Assert.False(BuildPackPlanner.Plan(BuildConfiguration.Default(ApplicationBuildPack.DockerCompose), 80, [])
            .BuildsImage);
        DockerfileBuildStep dockerfile = Assert.IsType<DockerfileBuildStep>(Assert.Single(BuildPackPlanner.Plan(
            BuildConfiguration.Default(ApplicationBuildPack.Dockerfile) with
            {
                BaseDirectory = "/api", DockerfilePath = "docker/Dockerfile"
            }, 80, []).Steps));
        Assert.Equal("docker/Dockerfile", dockerfile.DockerfilePath);
    }

    [Theory]
    [InlineData("../outside", null, null, null)]
    [InlineData(null, "ghcr.io/acme/web:latest", null, null)]
    [InlineData(null, "ghcr.io/Acme/web", null, null)]
    [InlineData(null, null, "npm run build\nrm -rf /", null)]
    [InlineData(null, null, null, "-service")]
    public void Build_configuration_rejects_unsafe_settings(string? publish, string? registry, string? command,
        string? service)
    {
        Assert.Throws<DomainException>(() => (BuildConfiguration.Default(ApplicationBuildPack.Static) with
        {
            PublishDirectory = publish, RegistryImage = registry, BuildCommand = command, ComposeService = service
        }).Validated());
    }

    [Fact]
    public void Registry_repository_names_production_images()
    {
        var applicationId = Vessel.Domain.ApplicationId.New();
        var deploymentId = DeploymentId.New();
        const string sha = "0123456789abcdef0123456789abcdef01234567";

        Assert.Equal($"ghcr.io/acme/web:{sha}",
            DeploymentPlanBuilder.ProductionImage(applicationId, sha, deploymentId, "ghcr.io/acme/web"));
        Assert.Equal($"vessel/{applicationId.Value:N}:{sha}",
            DeploymentPlanBuilder.ProductionImage(applicationId, sha, deploymentId));
    }

    [Theory]
    [InlineData("nginx", true)]
    [InlineData("ghcr.io/acme/web:abc", true)]
    [InlineData("localhost:5000/team/app:1.0", true)]
    [InlineData("busybox@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("-v", false)]
    [InlineData("Nginx", false)]
    [InlineData("nginx:bad tag", false)]
    [InlineData("nginx;rm", false)]
    public void Image_references_follow_the_distribution_grammar(string value, bool valid)
    {
        Assert.Equal(valid, ImageReference.TryParse(value, out _));
    }

    [Theory]
    [InlineData("vessel-db-1", true)]
    [InlineData("-rm", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    public void Container_names_cannot_be_read_as_options(string value, bool valid)
    {
        Assert.Equal(valid, ContainerName.IsValid(value));
        Assert.Equal(valid, ContainerNetworkName.IsValid(value));
    }

    private static EnvironmentVariable Plain(string key, string? value)
    {
        return EnvironmentVariable.Create(TeamId.New(), EnvironmentVariableTargetType.Team,
            new EnvironmentVariableKey(key), EnvironmentVariableValueKind.Plain, value, null, false, true, false,
            false, false, null, Now);
    }
}
