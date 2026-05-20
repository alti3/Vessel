using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Vessel.Application.Auditing;
using Vessel.Application.Diagnostics;
using Vessel.Application.Docker;
using Vessel.Application.Git;
using Vessel.Application.Persistence;
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
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

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
    ProxyConfigurationService proxyConfiguration,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
    : IDeploymentRunner
{
    public async Task RunAsync(DeploymentId deploymentId, CancellationToken cancellationToken = default)
    {
        long startedAt = Stopwatch.GetTimestamp();
        DeploymentStatus finalStatus = DeploymentStatus.Failed;
        using Activity? activity = VesselDiagnostics.ActivitySource.StartActivity("RunDeployment");
        activity?.SetTag("vessel.deployment_id", deploymentId.Value);
        VesselDiagnostics.ActiveDeployments.Add(1);

        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        activity?.SetTag("vessel.application_id", deployment.ApplicationId.Value);
        activity?.SetTag("vessel.server_id", deployment.ServerId.Value);
        string lockKey = $"deployment:{deployment.ApplicationId.Value:D}";
        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            lockKey,
            TimeSpan.FromHours(2),
            TimeSpan.Zero,
            cancellationToken);

        if (handle is null)
        {
            await AppendAsync(deploymentId, "stderr", "Another deployment is already running for this application.", cancellationToken);
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
                throw new DomainException("Phase 8 deployment runner currently supports local Docker/Podman targets; SSH runtime execution remains behind the runtime abstraction.");

            ContainerRuntimeTarget target = RuntimeTarget(server);
            workspace = await workspaces.PrepareAsync(deploymentId, cancellationToken);

            await AppendAsync(deploymentId, "system", "Cloning application source.", cancellationToken);
            string? checkoutRef = string.IsNullOrWhiteSpace(deployment.CommitSha)
                || string.Equals(deployment.CommitSha, "pending", StringComparison.OrdinalIgnoreCase)
                    ? application.GitSource.CommitSha ?? application.GitSource.Branch
                    : deployment.CommitSha;
            await git.CloneAsync(new GitCloneRequest(
                new Uri(application.GitSource.RepositoryUrl.Value),
                workspace.RepositoryDirectory,
                checkoutRef,
                Depth: 1), cancellationToken);
            GitCommitInfo commit = await git.GetCommitAsync(workspace.RepositoryDirectory, "HEAD", cancellationToken);
            await RecordSourceAsync(deploymentId, application, commit, cancellationToken);
            await ThrowIfCanceledAsync(deploymentId, cancellationToken);

            DeploymentRuntimePlan plan = await CreatePlanAsync(application, environment, server, teamId, deploymentId, workspace, cancellationToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, ".env", plan.EnvironmentFile, restrictToOwner: true, cancellationToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, "docker-compose.yml", plan.ComposeYaml, restrictToOwner: false, cancellationToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, "snapshots/docker-compose.redacted.yml", plan.RedactedComposeYaml, restrictToOwner: false, cancellationToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, "snapshots/env.redacted", plan.RedactedEnvironmentFile, restrictToOwner: false, cancellationToken);
            await RecordSnapshotAsync(deploymentId, "snapshots/docker-compose.redacted.yml", cancellationToken);

            await AppendAsync(deploymentId, "system", "Ensuring deployment network exists.", cancellationToken);
            await runtime.EnsureNetworkAsync(target, plan.NetworkName, Labels(deploymentId, application), cancellationToken);
            await ThrowIfCanceledAsync(deploymentId, cancellationToken);

            if (application.BuildConfiguration.BuildPack == ApplicationBuildPack.Dockerfile)
            {
                string dockerfile = NormalizeRelativePath(application.BuildConfiguration.DockerfilePath ?? "Dockerfile");
                await StreamBuildAsync(target, plan, workspace, dockerfile, deploymentId, cancellationToken);
                await ThrowIfCanceledAsync(deploymentId, cancellationToken);
            }

            await StreamComposeAsync(target, plan, workspace, deploymentId, ["config", "--quiet"], cancellationToken);
            await ThrowIfCanceledAsync(deploymentId, cancellationToken);
            await StreamComposeAsync(target, plan, workspace, deploymentId, ["up", "--detach", "--remove-orphans", "--build"], cancellationToken);
            await ThrowIfCanceledAsync(deploymentId, cancellationToken);

            await RunHealthCheckAsync(plan, deploymentId, cancellationToken);
            await AppendAsync(deploymentId, "system", "Applying reverse proxy routes.", cancellationToken);
            await proxyConfiguration.ApplyForDeploymentAsync(deployment.ActorUserId, teamId, server.Id, cancellationToken);
            await SucceedAsync(deploymentId, plan.ImageName, cancellationToken);
            finalStatus = DeploymentStatus.Succeeded;
            await auditWriter.RecordAsync(teamId, deployment.ActorUserId, AuditActions.DeploymentFinished,
                new AuditTarget("deployment", deploymentId.Value.ToString("D")), null,
                new Dictionary<string, object?> { ["status"] = DeploymentStatus.Succeeded.ToString() }, cancellationToken);
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
            await AppendAsync(deploymentId, "stderr", $"Deployment failed: {redactor.Redact(ex.Message)}", CancellationToken.None);
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
        await foreach (Processes.ProcessOutputLine line in runtime.BuildImageAsync(
                           target,
                           new DockerBuildCommand(
                               workspace.RepositoryDirectory,
                               dockerfile,
                               plan.ImageName,
                               Labels(deploymentId, LoadApplication(deploymentId)),
                               new Dictionary<string, string>(),
                               TimeSpan.FromMinutes(30)),
                           cancellationToken))
        {
            await AppendAsync(deploymentId, StreamName(line.Stream), line.Content, cancellationToken);
        }
    }

    private async Task StreamComposeAsync(
        ContainerRuntimeTarget target,
        DeploymentRuntimePlan plan,
        DeploymentWorkspace workspace,
        DeploymentId deploymentId,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        await AppendAsync(deploymentId, "system", $"Running docker compose {string.Join(' ', args)}.", cancellationToken);
        var commandArgs = new List<string> { "--project-name", plan.ProjectName, "--project-directory", workspace.RootDirectory };
        commandArgs.AddRange(args);
        await foreach (Processes.ProcessOutputLine line in runtime.RunComposeAsync(
                           target,
                           new ComposeCommand(
                               workspace.RootDirectory,
                               [workspace.ComposeFilePath],
                               commandArgs,
                               workspace.EnvironmentFilePath,
                               TimeSpan.FromMinutes(30)),
                           cancellationToken))
        {
            await AppendAsync(deploymentId, StreamName(line.Stream), line.Content, cancellationToken);
        }
    }

    private async Task RunHealthCheckAsync(
        DeploymentRuntimePlan plan,
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
                using HttpResponseMessage response = await client.GetAsync(plan.HealthCheckUrl, cancellationToken);
                if ((int)response.StatusCode < 500)
                {
                    await AppendAsync(deploymentId, "system", $"Health check passed: {(int)response.StatusCode}.", cancellationToken);
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
        DeploymentWorkspace workspace,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> env = await ResolveEnvironmentAsync(application, environment, server, teamId, cancellationToken);
        string envFile = BuildEnvironmentFile(env);
        string redactedEnv = redactor.Redact(envFile, new RedactionContext(env.Values.ToArray(), null));
        string projectName = $"vessel-{application.Id.Value:N}"[..39];
        string serviceName = SanitizeName(application.Name.Value);
        string imageName = $"vessel/{application.Id.Value:N}:{deploymentId.Value:N}";
        string networkName = $"vessel-{server.Id.Value:N}"[..39];
        int port = application.RuntimeConfiguration.ExposedPort?.Value ?? 8080;
        string healthPath = application.RuntimeConfiguration.HealthCheckPath.StartsWith("/", StringComparison.Ordinal)
            ? application.RuntimeConfiguration.HealthCheckPath
            : "/" + application.RuntimeConfiguration.HealthCheckPath;
        string healthUrl = $"http://localhost:{port}{healthPath}";
        string compose = application.BuildConfiguration.BuildPack == ApplicationBuildPack.DockerCompose
            ? await CreateComposePassthroughAsync(workspace, application, serviceName, imageName, networkName, port, cancellationToken)
            : CreateDockerfileCompose(application, serviceName, imageName, networkName, port);

        return new DeploymentRuntimePlan(
            projectName,
            serviceName,
            imageName,
            networkName,
            compose,
            redactor.Redact(compose, new RedactionContext(env.Values.ToArray(), null)),
            envFile,
            redactedEnv,
            healthUrl);
    }

    private async Task<string> CreateComposePassthroughAsync(
        DeploymentWorkspace workspace,
        AppEntity application,
        string serviceName,
        string imageName,
        string networkName,
        int port,
        CancellationToken cancellationToken)
    {
        string composePath = NormalizeRelativePath(application.BuildConfiguration.DockerfilePath ?? "docker-compose.yml");
        try
        {
            string raw = await workspaces.ReadTextAsync(workspace.RepositoryDirectory, composePath, cancellationToken);
            if (raw.Contains("..", StringComparison.Ordinal))
                throw new DomainException("Compose file contains unsupported traversal markers.");
            return raw;
        }
        catch (FileNotFoundException)
        {
            return CreateDockerfileCompose(application, serviceName, imageName, networkName, port);
        }
    }

    private static string CreateDockerfileCompose(
        AppEntity application,
        string serviceName,
        string imageName,
        string networkName,
        int port)
    {
        string dockerfile = NormalizeRelativePath(application.BuildConfiguration.DockerfilePath ?? "Dockerfile");
        var builder = new StringBuilder();
        builder.AppendLine("services:");
        builder.AppendLine($"  {serviceName}:");
        builder.AppendLine($"    image: {imageName}");
        builder.AppendLine("    build:");
        builder.AppendLine("      context: ./repository");
        builder.AppendLine($"      dockerfile: {dockerfile.Replace("\\", "/", StringComparison.Ordinal)}");
        builder.AppendLine("    env_file:");
        builder.AppendLine("      - .env");
        builder.AppendLine("    labels:");
        builder.AppendLine("      vessel.managed: \"true\"");
        builder.AppendLine($"      vessel.application: \"{application.Id.Value:D}\"");
        builder.AppendLine("    networks:");
        builder.AppendLine($"      - {networkName}");
        builder.AppendLine("    ports:");
        builder.AppendLine($"      - \"{port}:{port}\"");
        builder.AppendLine("    restart: unless-stopped");
        builder.AppendLine("networks:");
        builder.AppendLine($"  {networkName}:");
        builder.AppendLine("    external: true");
        return builder.ToString();
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(
        AppEntity application,
        EnvironmentEntity environment,
        Server server,
        TeamId teamId,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VESSEL_APPLICATION_ID"] = application.Id.Value.ToString("D"),
            ["VESSEL_ENVIRONMENT_ID"] = environment.Id.Value.ToString("D"),
            ["VESSEL_SERVER_ID"] = server.Id.Value.ToString("D")
        };

        EnvironmentVariable[] variables = dbContext.EnvironmentVariables
            .Where(variable => variable.TeamId == teamId && variable.IsRuntime)
            .ToArray();

        foreach (EnvironmentVariable variable in variables.Where(variable => AppliesTo(variable, application, environment, server)))
        {
            values[variable.Key.Value] = variable.ValueKind == EnvironmentVariableValueKind.Secret && variable.SecretReferenceId.HasValue
                ? await secretVault.RevealForDeploymentAsync(teamId, variable.SecretReferenceId.Value, cancellationToken)
                : variable.PlainValue ?? string.Empty;
        }

        return values;
    }

    private static bool AppliesTo(EnvironmentVariable variable, AppEntity application, EnvironmentEntity environment, Server server)
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
        foreach ((string key, string value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(key).Append('=').AppendLine(QuoteEnv(value));
        return builder.ToString();
    }

    private static string QuoteEnv(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (value.Any(char.IsWhiteSpace) || value.Contains('"', StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal))
            return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
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
        GitCommitInfo commit,
        CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        deployment.RecordSource(application.GitSource.RepositoryUrl.Value, application.GitSource.Branch, commit.Sha, commit.Subject, timeProvider.GetUtcNow());
        deployment.AddLogLine("system", $"Checked out {commit.Sha[..Math.Min(12, commit.Sha.Length)]} {commit.Subject}", timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(deployment, cancellationToken);
    }

    private async Task RecordSnapshotAsync(DeploymentId deploymentId, string reference, CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        deployment.RecordConfigurationSnapshot(reference, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SucceedAsync(DeploymentId deploymentId, string artifactReference, CancellationToken cancellationToken)
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

    private async Task AppendAsync(DeploymentId deploymentId, string stream, string message, CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        string redacted = redactor.Redact(message);
        deployment.AddLogLine(stream, redacted, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        DeploymentLogLine line = deployment.LogLines.OrderBy(item => item.Sequence).Last();
        await realtime.PublishAsync(
            new RealtimeGroup(RealtimeGroupKind.Deployment, deployment.Id.Value.ToString("D")),
            new RealtimeMessage("deployment.log", new DeploymentLogEntry(line.Sequence, line.Stream, line.Message, line.CreatedAt)),
            cancellationToken);
    }

    private async Task ThrowIfCanceledAsync(DeploymentId deploymentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        if (deployment.Status == DeploymentStatus.CancelRequested || deployment.Status == DeploymentStatus.CanceledByUser)
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

    private (AppEntity Application, Server Server, EnvironmentEntity Environment, TeamId TeamId) LoadDeploymentContext(DeploymentId deploymentId)
    {
        Deployment deployment = dbContext.Deployments.Single(item => item.Id == deploymentId);
        AppEntity application = dbContext.Applications.Single(application => application.Id == deployment.ApplicationId);
        Server server = dbContext.Servers.Single(server => server.Id == deployment.ServerId);
        EnvironmentEntity environment = dbContext.Environments.Single(environment => environment.Id == application.EnvironmentId);
        TeamId teamId = dbContext.Projects.Where(project => project.Id == environment.ProjectId).Select(project => project.TeamId).Single();
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
        await realtime.PublishAsync(new RealtimeGroup(RealtimeGroupKind.Application, deployment.ApplicationId.Value.ToString("D")),
            new RealtimeMessage("deployment.status", payload), cancellationToken);
    }

    private static ContainerRuntimeTarget RuntimeTarget(Server server)
    {
        return new ContainerRuntimeTarget(server.Runtime == ContainerRuntimeKind.Podman
            ? ContainerRuntimeProvider.Podman
            : ContainerRuntimeProvider.Docker);
    }

    private static IReadOnlyDictionary<string, string> Labels(DeploymentId deploymentId, AppEntity application)
    {
        return new Dictionary<string, string>
        {
            ["vessel.managed"] = "true",
            ["vessel.deployment"] = deploymentId.Value.ToString("D"),
            ["vessel.application"] = application.Id.Value.ToString("D")
        };
    }

    private static string NormalizeRelativePath(string value)
    {
        string trimmed = string.IsNullOrWhiteSpace(value) ? "Dockerfile" : value.Trim().TrimStart('/', '\\');
        if (trimmed.Contains("..", StringComparison.Ordinal))
            throw new DomainException("Path traversal is not allowed in deployment file paths.");
        return trimmed;
    }

    private static string SanitizeName(string value)
    {
        string normalized = new(value.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        normalized = normalized.Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "app" : normalized[..Math.Min(40, normalized.Length)];
    }

    private static string StreamName(Processes.ProcessStreamKind stream)
    {
        return stream == Processes.ProcessStreamKind.StandardError ? "stderr" : "stdout";
    }

    private sealed class DeploymentCanceledException : Exception;
}
