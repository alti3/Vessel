using System.Diagnostics;
using System.Globalization;
using System.Text;
using Vessel.Application.Auditing;
using Vessel.Application.Diagnostics;
using Vessel.Application.Docker;
using Vessel.Application.Git;
using Vessel.Application.Persistence;
using Vessel.Application.Processes;
using Vessel.Application.Proxy;
using Vessel.Application.Realtime;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Servers;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;
using ProcessOutputLine = Vessel.Application.Processes.ProcessOutputLine;

namespace Vessel.Application.Deployments;

public sealed class DeploymentRunner(
    IVesselDbContext dbContext,
    IGitClient git,
    IContainerRuntimeClient runtime,
    IDeploymentWorkspaceManager workspaces,
    IDistributedLockManager locks,
    ISecretVault secretVault,
    ISecretRedactor redactor,
    IRealtimeNotifier realtime,
    IComposeDocumentBuilder composeDocuments,
    ProxyConfigurationService proxyConfiguration,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
    : IDeploymentRunner
{
    public async Task RunAsync(DeploymentId deploymentId, CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        DeploymentStatus finalStatus = DeploymentStatus.Failed;
        using Activity? activity = VesselDiagnostics.ActivitySource.StartActivity("RunDeployment");
        activity?.SetTag("vessel.deployment_id", deploymentId.Value);
        VesselDiagnostics.ActiveDeployments.Add(1);

        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        activity?.SetTag("vessel.application_id", deployment.ApplicationId.Value);
        activity?.SetTag("vessel.server_id", deployment.ServerId.Value);
        ApplicationPreview? preview = LoadPreview(deployment);
        // Production and each preview are separate stacks, so they lock independently.
        var lockKey = preview is null
            ? $"deployment:{deployment.ApplicationId.Value:D}"
            : $"deployment:{deployment.ApplicationId.Value:D}:pr:{preview.PullRequestNumber}";
        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            lockKey,
            TimeSpan.FromHours(2),
            TimeSpan.Zero,
            cancellationToken);

        if (handle is null)
        {
            await AppendAsync(deploymentId, "stderr", "Another deployment is already running for this application.",
                cancellationToken);
            await FailAsync(deploymentId, cancellationToken);
            finalStatus = DeploymentStatus.Failed;
            return;
        }

        DeploymentWorkspace? workspace = null;
        try
        {
            await StartAsync(deploymentId, cancellationToken);
            await ThrowIfCanceledAsync(deploymentId, cancellationToken);

            (AppEntity application, Server server, EnvironmentEntity environment, TeamId teamId) =
                LoadDeploymentContext(deploymentId);
            if (server.Status == ServerStatus.Unreachable)
                throw new DomainException("Server is unreachable.");
            if (server.ConnectionType == ServerConnectionType.Ssh)
                throw new DomainException(
                    "Phase 8 deployment runner currently supports local Docker/Podman targets; SSH runtime execution remains behind the runtime abstraction.");

            ContainerRuntimeTarget target = RuntimeTarget(server);
            if (preview is not null)
            {
                if (preview.Status != ApplicationPreviewStatus.Open)
                    throw new DomainException("Preview is closed; its pull request is no longer deployed.");
                if (application.BuildConfiguration.BuildPack == ApplicationBuildPack.DockerCompose &&
                    !preview.IsSameRepository)
                    throw new DomainException(
                        "Docker Compose previews only deploy pull requests from the application repository; a fork's compose file is not trusted.");
            }

            // Refs are validated again at start (queue-time validation may predate a settings change).
            workspace = await workspaces.PrepareAsync(deploymentId, cancellationToken);
            GitCheckoutRequest checkout =
                DeploymentPlanBuilder.Checkout(application, deployment, preview, workspace.RepositoryDirectory);

            await AppendAsync(deploymentId, "system", CheckoutMessage(checkout), cancellationToken);
            await git.CheckoutAsync(checkout, cancellationToken);
            GitCommitInfo commit = await git.GetCommitAsync(workspace.RepositoryDirectory, "HEAD", cancellationToken);
            await RecordSourceAsync(deploymentId, application, preview, commit, cancellationToken);
            await ThrowIfCanceledAsync(deploymentId, cancellationToken);

            DeploymentRuntimePlan plan = await CreatePlanAsync(application, environment, server, teamId, deploymentId,
                preview, workspace, cancellationToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, ".env", plan.EnvironmentFile, true,
                cancellationToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, "docker-compose.yml", plan.ComposeYaml, false,
                cancellationToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, "snapshots/docker-compose.redacted.yml",
                plan.RedactedComposeYaml, false, cancellationToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, "snapshots/env.redacted",
                plan.RedactedEnvironmentFile, false, cancellationToken);
            await RecordSnapshotAsync(deploymentId, "snapshots/docker-compose.redacted.yml", cancellationToken);

            await AppendAsync(deploymentId, "system", "Ensuring deployment network exists.", cancellationToken);
            await runtime.EnsureNetworkAsync(target, plan.NetworkName, Labels(deploymentId, application),
                cancellationToken);
            await ThrowIfCanceledAsync(deploymentId, cancellationToken);

            if (application.BuildConfiguration.BuildPack == ApplicationBuildPack.Dockerfile)
            {
                var dockerfile = NormalizeRelativePath(application.BuildConfiguration.DockerfilePath, "Dockerfile");
                await StreamBuildAsync(target, plan, workspace, dockerfile, deploymentId, cancellationToken);
                await ThrowIfCanceledAsync(deploymentId, cancellationToken);
            }

            await StreamComposeAsync(target, plan, workspace, deploymentId, ["config", "--quiet"], cancellationToken);
            await ThrowIfCanceledAsync(deploymentId, cancellationToken);
            await StreamComposeAsync(target, plan, workspace, deploymentId,
                ["up", "--detach", "--remove-orphans", "--build"], cancellationToken);
            await ThrowIfCanceledAsync(deploymentId, cancellationToken);

            var healthCheckUrl = preview is null
                ? plan.HealthCheckUrl
                : await ResolvePreviewHealthCheckUrlAsync(target, plan, workspace, cancellationToken);
            await RunHealthCheckAsync(healthCheckUrl, deploymentId, cancellationToken);
            if (preview is not null)
                await RecordPreviewRouteAsync(deploymentId, application, preview, cancellationToken);
            await AppendAsync(deploymentId, "system", "Applying reverse proxy routes.", cancellationToken);
            await proxyConfiguration.ApplyForDeploymentAsync(deployment.ActorUserId, teamId, server.Id,
                cancellationToken);
            await SucceedAsync(deploymentId, plan.ImageName, cancellationToken);
            finalStatus = DeploymentStatus.Succeeded;
            await auditWriter.RecordAsync(teamId, deployment.ActorUserId, AuditActions.DeploymentFinished,
                new AuditTarget("deployment", deploymentId.Value.ToString("D")), null,
                new Dictionary<string, object?> { ["status"] = DeploymentStatus.Succeeded.ToString() },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelAsync(deploymentId, cancellationToken);
            finalStatus = DeploymentStatus.CanceledByUser;
        }
        catch (DeploymentCanceledException)
        {
            await CancelAsync(deploymentId, cancellationToken);
            finalStatus = DeploymentStatus.CanceledByUser;
        }
        catch (Exception ex)
        {
            await AppendAsync(deploymentId, "stderr", $"Deployment failed: {redactor.Redact(ex.Message)}",
                CancellationToken.None);
            await FailAsync(deploymentId, CancellationToken.None);
            finalStatus = DeploymentStatus.Failed;
        }
        finally
        {
            if (workspace is not null)
                await workspaces.CleanupAsync(deploymentId, CancellationToken.None);
            activity?.SetTag("vessel.deployment_status", finalStatus.ToString());
            VesselDiagnostics.DeploymentRuns.Add(1,
                new TagList { { "status", finalStatus.ToString() } });
            VesselDiagnostics.DeploymentDurationSeconds.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                new TagList { { "status", finalStatus.ToString() } });
            VesselDiagnostics.ActiveDeployments.Add(-1);
        }
    }

    private async Task StreamBuildAsync(
        ContainerRuntimeTarget target,
        DeploymentRuntimePlan plan,
        DeploymentWorkspace workspace,
        string dockerfile,
        DeploymentId deploymentId,
        CancellationToken cancellationToken)
    {
        await AppendAsync(deploymentId, "system", "Building Docker image.", cancellationToken);
        await foreach (ProcessOutputLine line in runtime.BuildImageAsync(
                           target,
                           new DockerBuildCommand(
                               workspace.RepositoryDirectory,
                               dockerfile,
                               plan.ImageName,
                               Labels(deploymentId, LoadApplication(deploymentId)),
                               new Dictionary<string, string>(),
                               TimeSpan.FromMinutes(30)),
                           cancellationToken))
            await AppendAsync(deploymentId, StreamName(line.Stream), line.Content, cancellationToken);
    }

    private async Task StreamComposeAsync(
        ContainerRuntimeTarget target,
        DeploymentRuntimePlan plan,
        DeploymentWorkspace workspace,
        DeploymentId deploymentId,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        await AppendAsync(deploymentId, "system", $"Running docker compose {string.Join(' ', args)}.",
            cancellationToken);
        var commandArgs = new List<string>
            { "--project-name", plan.ProjectName, "--project-directory", workspace.RootDirectory };
        commandArgs.AddRange(args);
        await foreach (ProcessOutputLine line in runtime.RunComposeAsync(
                           target,
                           new ComposeCommand(
                               workspace.RootDirectory,
                               [workspace.ComposeFilePath],
                               commandArgs,
                               workspace.EnvironmentFilePath,
                               TimeSpan.FromMinutes(30)),
                           cancellationToken))
            await AppendAsync(deploymentId, StreamName(line.Stream), line.Content, cancellationToken);
    }

    /// <summary>
    ///     Previews publish their primary port on an ephemeral loopback port (no collisions with production or other
    ///     previews); ask compose which host port was assigned.
    /// </summary>
    private async Task<string> ResolvePreviewHealthCheckUrlAsync(
        ContainerRuntimeTarget target,
        DeploymentRuntimePlan plan,
        DeploymentWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var port = plan.Port.ToString(CultureInfo.InvariantCulture);
        string? published = null;
        await foreach (ProcessOutputLine line in runtime.RunComposeAsync(
                           target,
                           new ComposeCommand(
                               workspace.RootDirectory,
                               [workspace.ComposeFilePath],
                               ["--project-name", plan.ProjectName, "--project-directory", workspace.RootDirectory,
                                   "port", plan.ServiceName, port],
                               workspace.EnvironmentFilePath,
                               TimeSpan.FromMinutes(1)),
                           cancellationToken))
            if (line.Stream == ProcessStreamKind.StandardOutput && !string.IsNullOrWhiteSpace(line.Content))
                published = line.Content.Trim();

        var hostPort = published?[(published.LastIndexOf(':') + 1)..];
        if (!int.TryParse(hostPort, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed is < 1 or > 65535)
            throw new DomainException("Could not determine the published port of the preview deployment.");
        return $"http://127.0.0.1:{parsed.ToString(CultureInfo.InvariantCulture)}{plan.HealthCheckPath}";
    }

    private async Task RunHealthCheckAsync(
        string healthCheckUrl,
        DeploymentId deploymentId,
        CancellationToken cancellationToken)
    {
        await AppendAsync(deploymentId, "system", "Running deployment health check.", cancellationToken);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        DateTimeOffset deadline = timeProvider.GetUtcNow().Add(TimeSpan.FromSeconds(30));
        Exception? lastError = null;

        while (timeProvider.GetUtcNow() < deadline)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(healthCheckUrl, cancellationToken);
                if ((int)response.StatusCode < 500)
                {
                    await AppendAsync(deploymentId, "system", $"Health check passed: {(int)response.StatusCode}.",
                        cancellationToken);
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await ThrowIfCanceledAsync(deploymentId, cancellationToken);
        }

        throw new DomainException($"Health check failed before timeout. {lastError?.Message}");
    }

    private async Task<DeploymentRuntimePlan> CreatePlanAsync(
        AppEntity application,
        EnvironmentEntity environment,
        Server server,
        TeamId teamId,
        DeploymentId deploymentId,
        ApplicationPreview? preview,
        DeploymentWorkspace workspace,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> env =
            await ResolveEnvironmentAsync(application, environment, server, teamId, deploymentId, preview,
                cancellationToken);
        var envFile = BuildEnvironmentFile(env);
        var redactedEnv = redactor.Redact(envFile, new RedactionContext(env.Values.ToArray()));
        DeploymentNaming naming = DeploymentPlanBuilder.Naming(application.Id, server.Id, deploymentId, preview);
        var port = application.RuntimeConfiguration.ExposedPort?.Value ?? DeploymentPlanBuilder.DefaultPort;
        var healthPath = application.RuntimeConfiguration.HealthCheckPath.StartsWith('/')
            ? application.RuntimeConfiguration.HealthCheckPath
            : "/" + application.RuntimeConfiguration.HealthCheckPath;
        var healthUrl = $"http://localhost:{port}{healthPath}";
        IReadOnlyDictionary<string, string> labels = Labels(deploymentId, application, preview);
        var serviceName = naming.ServiceName;
        string compose;
        ComposeNormalizationResult? normalized =
            application.BuildConfiguration.BuildPack == ApplicationBuildPack.DockerCompose
                ? await CreateComposePassthroughAsync(workspace, application, naming, port, labels, cancellationToken)
                : null;
        if (normalized is null)
        {
            compose = CreateDockerfileCompose(application, naming, port, labels);
        }
        else
        {
            compose = normalized.Yaml;
            serviceName = normalized.PrimaryService;
            foreach (var warning in normalized.Warnings)
                await AppendAsync(deploymentId, "system", warning, cancellationToken);
        }

        return new DeploymentRuntimePlan(
            naming.ProjectName,
            serviceName,
            naming.ImageName,
            naming.NetworkName,
            compose,
            redactor.Redact(compose, new RedactionContext(env.Values.ToArray())),
            envFile,
            redactedEnv,
            healthUrl,
            naming,
            port,
            healthPath);
    }

    private async Task<ComposeNormalizationResult?> CreateComposePassthroughAsync(
        DeploymentWorkspace workspace,
        AppEntity application,
        DeploymentNaming naming,
        int port,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken)
    {
        var composePath = NormalizeRelativePath(application.BuildConfiguration.DockerfilePath, "docker-compose.yml");
        string raw;
        try
        {
            raw = await workspaces.ReadTextAsync(workspace.RepositoryDirectory, composePath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        // Parsed and normalized: build contexts, bind mounts and env files must resolve inside the repository, and
        // the primary service joins the server network under the per-application (or per-preview) upstream alias.
        return composeDocuments.NormalizeRepositoryCompose(raw, new RepositoryComposeSpec(
            workspace.RepositoryDirectory,
            composePath,
            naming.NetworkName,
            naming.UpstreamAlias,
            port,
            labels,
            naming.PreviewSuffix));
    }

    private string CreateDockerfileCompose(
        AppEntity application,
        DeploymentNaming naming,
        int port,
        IReadOnlyDictionary<string, string> labels)
    {
        var dockerfile = NormalizeRelativePath(application.BuildConfiguration.DockerfilePath, "Dockerfile");
        var portText = port.ToString(CultureInfo.InvariantCulture);
        // Production keeps its fixed host port; previews bind an ephemeral loopback port so they never collide.
        var binding = naming.IsPreview ? $"127.0.0.1::{portText}" : $"{portText}:{portText}";
        return composeDocuments.CreateDockerfileStack(new DockerfileStackSpec(
            naming.ServiceName,
            naming.ImageName,
            "./repository",
            dockerfile,
            naming.NetworkName,
            naming.UpstreamAlias,
            binding,
            labels));
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(
        AppEntity application,
        EnvironmentEntity environment,
        Server server,
        TeamId teamId,
        DeploymentId deploymentId,
        ApplicationPreview? preview,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VESSEL_APPLICATION_ID"] = application.Id.Value.ToString("D"),
            ["VESSEL_ENVIRONMENT_ID"] = environment.Id.Value.ToString("D"),
            ["VESSEL_SERVER_ID"] = server.Id.Value.ToString("D")
        };
        if (preview is not null)
            values["VESSEL_PULL_REQUEST_ID"] = preview.PullRequestNumber.ToString(CultureInfo.InvariantCulture);

        EnvironmentVariable[] applicable = dbContext.EnvironmentVariables
            .Where(variable => variable.TeamId == teamId && variable.IsRuntime)
            .AsEnumerable()
            .Where(variable => AppliesTo(variable, application, environment, server))
            .ToArray();
        // Production never receives preview copies; previews follow Coolify's preview-variable rules.
        DeploymentVariableSelection selection = DeploymentPlanBuilder.SelectVariables(applicable, preview is not null,
            application.DeploymentSettings.PublicPreviewsEnabled);
        if (selection.WithheldKeys.Count > 0)
            await AppendAsync(deploymentId, "system",
                (application.DeploymentSettings.PublicPreviewsEnabled
                    ? "Public previews never receive production values; variables not passed: "
                    : "Preview variables are missing for these production variables, which are not passed: ") +
                string.Join(", ", selection.WithheldKeys),
                cancellationToken);

        foreach (EnvironmentVariable variable in selection.Variables)
            values[variable.Key.Value] = variable.ValueKind == EnvironmentVariableValueKind.Secret &&
                                         variable.SecretReferenceId.HasValue
                ? await secretVault.RevealForDeploymentAsync(teamId, variable.SecretReferenceId.Value,
                    cancellationToken)
                : variable.PlainValue ?? string.Empty;

        return values;
    }

    private static bool AppliesTo(EnvironmentVariable variable, AppEntity application, EnvironmentEntity environment,
        Server server)
    {
        return variable.TargetType switch
        {
            EnvironmentVariableTargetType.Team => true,
            EnvironmentVariableTargetType.Project => variable.ProjectId == environment.ProjectId,
            EnvironmentVariableTargetType.Environment => variable.EnvironmentId == application.EnvironmentId,
            EnvironmentVariableTargetType.Server => variable.ServerId == server.Id,
            EnvironmentVariableTargetType.Application => variable.ApplicationId == application.Id,
            _ => false
        };
    }

    private static string BuildEnvironmentFile(IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(key).Append('=').AppendLine(QuoteEnv(value));
        return builder.ToString();
    }

    private static string QuoteEnv(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (value.Any(char.IsWhiteSpace) || value.Contains('"', StringComparison.Ordinal) ||
            value.Contains('#', StringComparison.Ordinal))
            return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return value;
    }

    private async Task StartAsync(DeploymentId deploymentId, CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        deployment.Start(timeProvider.GetUtcNow());
        deployment.AddLogLine("system", "Deployment started.", timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(deployment, cancellationToken);
    }

    private async Task RecordSourceAsync(
        DeploymentId deploymentId,
        AppEntity application,
        ApplicationPreview? preview,
        GitCommitInfo commit,
        CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        deployment.RecordSource(application.GitSource.RepositoryUrl.Value,
            preview?.SourceBranch ?? application.GitSource.Branch, commit.Sha,
            commit.Subject, timeProvider.GetUtcNow());
        deployment.AddLogLine("system", $"Checked out {commit.Sha[..Math.Min(12, commit.Sha.Length)]} {commit.Subject}",
            timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(deployment, cancellationToken);
    }

    /// <summary>Gives the preview its own routing record (<c>{pr}.{domain}</c>), routed to the preview alias.</summary>
    private async Task RecordPreviewRouteAsync(
        DeploymentId deploymentId,
        AppEntity application,
        ApplicationPreview preview,
        CancellationToken cancellationToken)
    {
        ApplicationDomain[] domains = dbContext.ApplicationDomains
            .Where(domain => domain.ApplicationId == application.Id)
            .ToArray();
        PreviewRoute? route = DeploymentPlanBuilder.PreviewRoute(domains, preview.PullRequestNumber,
            application.RuntimeConfiguration.ExposedPort?.Value ?? DeploymentPlanBuilder.DefaultPort);
        preview.SetPreviewUrl(route?.Url, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await AppendAsync(deploymentId, "system",
            route is null
                ? "Preview has no public route because the application has no domain."
                : $"Preview available at {route.Url}.", cancellationToken);
    }

    private ApplicationPreview? LoadPreview(Deployment deployment)
    {
        if (!deployment.PreviewId.HasValue) return null;
        ApplicationPreviewId previewId = deployment.PreviewId.Value;
        ApplicationPreview preview = dbContext.ApplicationPreviews.SingleOrDefault(item => item.Id == previewId)
                                     ?? throw new InvalidOperationException("Preview was not found.");
        if (preview.ApplicationId != deployment.ApplicationId)
            throw new InvalidOperationException("Preview does not belong to the deployed application.");
        return preview;
    }

    private static string CheckoutMessage(GitCheckoutRequest checkout)
    {
        return checkout.Kind switch
        {
            GitCheckoutKind.Commit =>
                $"Fetching commit {checkout.Reference[..Math.Min(12, checkout.Reference.Length)]}.",
            GitCheckoutKind.PullRequest => $"Fetching pull request ref {checkout.Reference}.",
            _ => $"Fetching {checkout.Reference}."
        };
    }

    private async Task RecordSnapshotAsync(DeploymentId deploymentId, string reference,
        CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        deployment.RecordConfigurationSnapshot(reference, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SucceedAsync(DeploymentId deploymentId, string artifactReference,
        CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        deployment.MarkSucceeded(artifactReference, timeProvider.GetUtcNow());
        deployment.AddLogLine("system", "Deployment completed successfully.", timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(deployment, cancellationToken);
    }

    private async Task FailAsync(DeploymentId deploymentId, CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        if (!Deployment.IsTerminal(deployment.Status))
            deployment.MarkFailed(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(deployment, cancellationToken);
    }

    private async Task CancelAsync(DeploymentId deploymentId, CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        if (!Deployment.IsTerminal(deployment.Status))
            deployment.CancelByUser(timeProvider.GetUtcNow());
        deployment.AddLogLine("system", "Deployment canceled.", timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(deployment, cancellationToken);
    }

    private async Task AppendAsync(DeploymentId deploymentId, string stream, string message,
        CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        var redacted = redactor.Redact(message);
        deployment.AddLogLine(stream, redacted, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        DeploymentLogLine line = deployment.LogLines.OrderBy(item => item.Sequence).Last();
        await realtime.PublishAsync(
            new RealtimeGroup(RealtimeGroupKind.Deployment, deployment.Id.Value.ToString("D")),
            new RealtimeMessage("deployment.log",
                new DeploymentLogEntry(line.Sequence, line.Stream, line.Message, line.CreatedAt)),
            cancellationToken);
    }

    private async Task ThrowIfCanceledAsync(DeploymentId deploymentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        if (deployment.Status == DeploymentStatus.CancelRequested ||
            deployment.Status == DeploymentStatus.CanceledByUser)
            throw new DeploymentCanceledException();
    }

    private async Task<Deployment> GetDeploymentAsync(DeploymentId deploymentId, CancellationToken cancellationToken)
    {
        return await dbContext.DeploymentRepository.GetByIdAsync(deploymentId, cancellationToken)
               ?? throw new InvalidOperationException("Deployment was not found.");
    }

    private AppEntity LoadApplication(DeploymentId deploymentId)
    {
        Deployment deployment = dbContext.Deployments.Single(item => item.Id == deploymentId);
        return dbContext.Applications.Single(application => application.Id == deployment.ApplicationId);
    }

    private (AppEntity Application, Server Server, EnvironmentEntity Environment, TeamId TeamId) LoadDeploymentContext(
        DeploymentId deploymentId)
    {
        Deployment deployment = dbContext.Deployments.Single(item => item.Id == deploymentId);
        AppEntity application =
            dbContext.Applications.Single(application => application.Id == deployment.ApplicationId);
        Server server = dbContext.Servers.Single(server => server.Id == deployment.ServerId);
        EnvironmentEntity environment =
            dbContext.Environments.Single(environment => environment.Id == application.EnvironmentId);
        TeamId teamId = dbContext.Projects.Where(project => project.Id == environment.ProjectId)
            .Select(project => project.TeamId).Single();
        return (application, server, environment, teamId);
    }

    private async Task PublishStatusAsync(Deployment deployment, CancellationToken cancellationToken)
    {
        var payload = new
        {
            deploymentId = deployment.Id.Value,
            applicationId = deployment.ApplicationId.Value,
            status = deployment.Status.ToString(),
            commitSha = deployment.CommitSha,
            finishedAt = deployment.FinishedAt
        };
        await realtime.PublishAsync(new RealtimeGroup(RealtimeGroupKind.Deployment, deployment.Id.Value.ToString("D")),
            new RealtimeMessage("deployment.status", payload), cancellationToken);
        await realtime.PublishAsync(
            new RealtimeGroup(RealtimeGroupKind.Application, deployment.ApplicationId.Value.ToString("D")),
            new RealtimeMessage("deployment.status", payload), cancellationToken);
    }

    private static ContainerRuntimeTarget RuntimeTarget(Server server)
    {
        return new ContainerRuntimeTarget(server.Runtime == ContainerRuntimeKind.Podman
            ? ContainerRuntimeProvider.Podman
            : ContainerRuntimeProvider.Docker);
    }

    private static Dictionary<string, string> Labels(DeploymentId deploymentId, AppEntity application,
        ApplicationPreview? preview = null)
    {
        var labels = new Dictionary<string, string>
        {
            ["vessel.managed"] = "true",
            ["vessel.deployment"] = deploymentId.Value.ToString("D"),
            ["vessel.application"] = application.Id.Value.ToString("D")
        };
        if (preview is not null)
            labels["vessel.preview"] = preview.Id.Value.ToString("D");
        return labels;
    }

    /// <summary>
    ///     Validates a repository-relative file path segment by segment (no <c>..</c> segment, no absolute or drive paths,
    ///     no control characters) instead of rejecting any value that merely contains two dots.
    /// </summary>
    public static string NormalizeRelativePath(string? value, string defaultPath)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? defaultPath : value.Trim();
        if (trimmed.Any(char.IsControl))
            throw new DomainException("Deployment file paths must not contain control characters.");
        var normalized = trimmed.Replace('\\', '/').TrimStart('/');
        if (normalized.Length > 1 && normalized[1] == ':')
            throw new DomainException("Deployment file paths must be relative to the repository.");
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();
        if (segments.Length == 0 || segments.Any(segment => segment == ".."))
            throw new DomainException("Path traversal is not allowed in deployment file paths.");
        return string.Join('/', segments);
    }

    private static string StreamName(ProcessStreamKind stream)
    {
        return stream == ProcessStreamKind.StandardError ? "stderr" : "stdout";
    }

    private sealed class DeploymentCanceledException : Exception;
}
