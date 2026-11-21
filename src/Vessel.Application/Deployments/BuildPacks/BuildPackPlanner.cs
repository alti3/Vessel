using System.Globalization;
using System.Text;
using Vessel.Application.Common;
using Vessel.Domain.Applications;
using Vessel.Domain.Common;

namespace Vessel.Application.Deployments.BuildPacks;

/// <summary>A file Vessel generates in the deployment workspace (never inside the cloned repository).</summary>
public sealed record GeneratedBuildFile(string WorkspacePath, string Contents);

/// <summary>One step of an image build.</summary>
public abstract record BuildStep;

/// <summary>
///     <c>docker build</c> of <see cref="DockerfilePath" /> with the plan's context. The Dockerfile is relative to the
///     context, or relative to the workspace when <see cref="InWorkspace" />; <see cref="Generated" /> marks files a
///     previous step writes on the runtime host (not checked against the local checkout).
/// </summary>
public sealed record DockerfileBuildStep(string DockerfilePath, bool InWorkspace = false, bool Generated = false)
    : BuildStep;

/// <summary><c>railpack prepare</c> writing <see cref="PlanFile" /> (workspace relative).</summary>
public sealed record RailpackPrepareStep(
    string PlanDirectory,
    string PlanFileName,
    IReadOnlyList<KeyValuePair<string, string>> ControlVariables,
    string? BuildCommand,
    string? StartCommand) : BuildStep
{
    public string PlanFile => PlanDirectory + "/" + PlanFileName;
}

/// <summary><c>docker buildx build</c> of the Railpack plan with the pinned frontend.</summary>
public sealed record RailpackBuildStep(string PlanFile) : BuildStep;

/// <summary><c>nixpacks build --out</c> generating <c>.nixpacks/Dockerfile</c> in the context.</summary>
public sealed record NixpacksGenerateStep(string? InstallCommand, string? BuildCommand, string? StartCommand)
    : BuildStep;

/// <summary>
///     How an application image is built: the build context (relative to the repository, empty for the root), the
///     workspace files to generate, the steps, and notes for the deployment log. The Docker Compose pack has no steps:
///     its services are built by <c>docker compose up --build</c>.
/// </summary>
public sealed record BuildPlan(
    ApplicationBuildPack BuildPack,
    string ContextPath,
    IReadOnlyList<GeneratedBuildFile> Files,
    IReadOnlyList<BuildStep> Steps,
    IReadOnlyList<string> Notes)
{
    public bool BuildsImage => Steps.Count > 0;
}

/// <summary>
///     Plans the build of each build pack (DEP-09). Pure, so every pack is unit tested:
///     <list type="bullet">
///         <item>Dockerfile: the repository Dockerfile (relative to the base directory).</item>
///         <item>
///             Railpack (recommended): <c>railpack prepare</c> in the pinned CLI image, then BuildKit with the pinned
///             Railpack frontend. Install/build/start commands map to <c>RAILPACK_INSTALL_CMD</c>,
///             <c>--build-cmd</c>, <c>--start-cmd</c>; <c>curl</c> and <c>wget</c> are added to the deploy packages so health
///             checks work (upstream <c>merge_railpack_deploy_apt_packages</c>).
///         </item>
///         <item>
///             Static: a generated nginx image serving the publish directory (relative to the base directory) on the
///             application port, with an optional SPA fallback; dot files (except <c>.well-known</c>) are never served.
///         </item>
///         <item>Nixpacks (deprecated): <c>nixpacks build --out</c>, then the generated Dockerfile.</item>
///         <item>Docker Compose: no image build step.</item>
///     </list>
/// </summary>
public static class BuildPackPlanner
{
    public const string WorkspaceBuildDirectory = "build";
    public const string StaticDockerfile = WorkspaceBuildDirectory + "/static/Dockerfile";
    public const string RailpackPlanDirectory = WorkspaceBuildDirectory + "/railpack";
    public const string RailpackPlanFileName = "railpack-plan.json";

    /// <summary>The shell file that carries build values to the runtime CLI on SSH servers (workspace relative).</summary>
    public const string BuildEnvironmentFile = WorkspaceBuildDirectory + "/build.env";

    public static BuildPlan Plan(BuildConfiguration configuration, int port,
        IReadOnlyList<KeyValuePair<string, string>> buildValues)
    {
        ArgumentNullException.ThrowIfNull(buildValues);
        BuildConfiguration config = configuration.Validated();
        var context = ContextPath(config.BaseDirectory);
        var notes = new List<string>();
        switch (config.BuildPack)
        {
            case ApplicationBuildPack.Dockerfile:
                return new BuildPlan(config.BuildPack, context, [],
                    [new DockerfileBuildStep(config.DockerfilePath ?? "Dockerfile")], notes);
            case ApplicationBuildPack.Static:
                {
                    var publish = string.IsNullOrEmpty(config.PublishDirectory)
                        ? context
                        : Combine(context, config.PublishDirectory);
                    notes.Add(
                        $"Static site: serving {(publish.Length == 0 ? "the repository root" : publish)} with nginx on port {port.ToString(CultureInfo.InvariantCulture)}{(config.IsSpa ? " (single-page application fallback)" : string.Empty)}.");
                    return new BuildPlan(config.BuildPack, publish,
                    [
                        new GeneratedBuildFile(StaticDockerfile,
                            StaticSiteImage.Dockerfile(ComponentVersions.StaticNginxImage, port, config.IsSpa)),
                        new GeneratedBuildFile(StaticDockerfile + ".dockerignore", StaticSiteImage.DockerIgnore)
                    ], [new DockerfileBuildStep(StaticDockerfile, true)], notes);
                }
            case ApplicationBuildPack.Railpack:
                return new BuildPlan(config.BuildPack, context, [],
                [
                    new RailpackPrepareStep(RailpackPlanDirectory, RailpackPlanFileName,
                        RailpackControlVariables(config, buildValues), config.BuildCommand, config.StartCommand),
                    new RailpackBuildStep(RailpackPlanDirectory + "/" + RailpackPlanFileName)
                ], notes);
            case ApplicationBuildPack.Nixpacks:
                notes.Add(ApplicationBuildPacks.NixpacksDeprecationMessage);
                return new BuildPlan(config.BuildPack, context, [],
                [
                    new NixpacksGenerateStep(config.InstallCommand, config.BuildCommand, config.StartCommand),
                    new DockerfileBuildStep(".nixpacks/Dockerfile", Generated: true)
                ], notes);
            case ApplicationBuildPack.DockerCompose:
                return new BuildPlan(config.BuildPack, context, [], [], notes);
            default:
                throw new DomainException("Build pack is not supported.");
        }
    }

    /// <summary>
    ///     <c>RAILPACK_*</c> configuration: user build variables with that prefix, the install command, and the deploy
    ///     packages with <c>...</c> (keep Railpack's own packages) plus <c>curl</c> and <c>wget</c>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> RailpackControlVariables(BuildConfiguration config,
        IReadOnlyList<KeyValuePair<string, string>> buildValues)
    {
        var control = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in buildValues)
            if (key.StartsWith("RAILPACK_", StringComparison.Ordinal))
                control[key] = value;
        if (!string.IsNullOrWhiteSpace(config.InstallCommand)) control["RAILPACK_INSTALL_CMD"] = config.InstallCommand;

        var packages = (control.GetValueOrDefault("RAILPACK_DEPLOY_APT_PACKAGES") ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!packages.Contains("...")) packages.Insert(0, "...");
        foreach (var package in new[] { "curl", "wget" })
            if (!packages.Contains(package))
                packages.Add(package);
        control["RAILPACK_DEPLOY_APT_PACKAGES"] = string.Join(' ', packages);
        return control.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Repository-relative context of a base directory (<c>/</c> is the repository root, an empty path).</summary>
    public static string ContextPath(string? baseDirectory)
    {
        var trimmed = (baseDirectory ?? "/").Trim().Trim('/');
        return trimmed == "." ? string.Empty : trimmed;
    }

    public static string Combine(string left, string right)
    {
        if (left.Length == 0) return right.Trim('/');
        return right.Trim('/').Length == 0 ? left : $"{left}/{right.Trim('/')}";
    }
}

/// <summary>The generated nginx image of the Static build pack (upstream <c>defaultNginxConfiguration</c>).</summary>
public static class StaticSiteImage
{
    public const string DockerIgnore = ".git\n**/.git\n.env\n.env.*\n**/.env\n";

    public static string NginxConfiguration(int port, bool spa)
    {
        if (port is < 1 or > 65535) throw new DomainException("Port must be between 1 and 65535.");
        var portText = port.ToString(CultureInfo.InvariantCulture);
        var tryFiles = spa
            ? "try_files $uri $uri/ /index.html;"
            : "try_files $uri $uri.html $uri/index.html $uri/index.htm $uri/ =404;";
        return
            "server {\n" +
            $"    listen {portText};\n" +
            "    root /usr/share/nginx/html;\n" +
            "\n" +
            "    # Never serve dot files (.git, .env, ...); .well-known stays reachable.\n" +
            "    location ~ /\\.(?!well-known) {\n" +
            "        deny all;\n" +
            "    }\n" +
            "\n" +
            "    location / {\n" +
            "        index index.html index.htm;\n" +
            $"        {tryFiles}\n" +
            "    }\n" +
            "\n" +
            "    error_page 404 /404.html;\n" +
            "    location = /404.html {\n" +
            "        internal;\n" +
            "    }\n" +
            "\n" +
            "    error_page 500 502 503 504 /50x.html;\n" +
            "    location = /50x.html {\n" +
            "        internal;\n" +
            "    }\n" +
            "}\n";
    }

    /// <summary>
    ///     The Dockerfile. The nginx configuration is embedded base64-encoded (it is generated by Vessel, so the build
    ///     context needs no extra file and no Dockerfile heredoc support).
    /// </summary>
    public static string Dockerfile(string image, int port, bool spa)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        var config = Convert.ToBase64String(Encoding.UTF8.GetBytes(NginxConfiguration(port, spa)));
        return
            $"FROM {image}\n" +
            $"RUN printf '%s' '{config}' | base64 -d > /etc/nginx/conf.d/default.conf\n" +
            "WORKDIR /usr/share/nginx/html\n" +
            "COPY . .\n" +
            $"EXPOSE {port.ToString(CultureInfo.InvariantCulture)}\n";
    }
}
