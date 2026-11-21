using System.Diagnostics;
using System.Globalization;
using System.Text;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Deployments.Queue;
using Vessel.Application.Diagnostics;
using Vessel.Application.Docker;
using Vessel.Application.Git;
using Vessel.Application.Jobs;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Processes;
using Vessel.Application.Proxy;
using Vessel.Application.Realtime;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Servers;
using Vessel.Application.Servers.Files;
using Vessel.Application.Sources;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Servers;
using Vessel.Domain.ValueObjects;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;
using ProcessOutputLine = Vessel.Application.Processes.ProcessOutputLine;

namespace Vessel.Application.Deployments;

public sealed partial class DeploymentRunner(
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
    ProxyLifecycleService proxyLifecycle,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    INotificationEventPublisher notifications,
    VesselAuthorizationService authorization,
    DeploymentQueueService queue,
    IDeploymentCancellationRegistry cancellations,
    IBackgroundJobDispatcher backgroundJobs,
    IServerDirectoryTransfer? directoryTransfer = null,
    IServerCommandRunner? serverCommands = null,
    IGitCredentialProvider? gitCredentials = null)
    : IDeploymentRunner
{
    /// <summary>
    ///     HTTP probe run on an SSH server for its deployment health check (prints the status code, or <c>nohttp</c> when
    ///     neither curl nor wget exists). The URL is passed as <c>$1</c>, never interpolated.
    /// </summary>
    public const string RemoteHealthCheckScript =
        "url=$1; if command -v curl >/dev/null 2>&1; then curl -s -o /dev/null -w '%{http_code}' --max-time 5 \"$url\" || true; " +
        "elif command -v wget >/dev/null 2>&1; then wget -q -S -O /dev/null -T 5 \"$url\" 2>&1 | awk '/^  HTTP\\//{c=$2} END{print c}'; " +
        "else echo nohttp; fi";

    /// <summary>How long a deployment waits for the run lock of its application before it is retried.</summary>
    public static readonly TimeSpan ContentionRetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>Retries for a contended run lock (one hour at <see cref="ContentionRetryDelay" />).</summary>
    public const int MaxContentionAttempts = 240;

    private DeploymentLogRedactor? _logRedactor;

    private DeploymentLogRedactor LogRedactor => _logRedactor ??= new DeploymentLogRedactor(redactor);

    public Task RunAsync(DeploymentId deploymentId, CancellationToken cancellationToken = default)
    {
        return RunAsync(deploymentId, 0, cancellationToken);
    }

    public async Task RunAsync(DeploymentId deploymentId, int attempt, CancellationToken cancellationToken = default)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        await dbContext.RefreshAsync(deployment, cancellationToken);
        if (Deployment.IsTerminal(deployment.Status)) return;

        ApplicationPreview? preview = LoadPreview(deployment);
        // Production and each preview are separate stacks, so they lock independently.
        var lockKey = preview is null
            ? $"deployment:{deployment.ApplicationId.Value:D}"
            : $"deployment:{deployment.ApplicationId.Value:D}:pr:{preview.PullRequestNumber}";
        DeploymentRunClaim claim;
        DistributedLockHandle? handle = await locks.TryAcquireAsync(lockKey, TimeSpan.FromHours(2), TimeSpan.Zero,
            cancellationToken);
        if (handle is null)
        {
            // DEP-04: lock contention never fails the deployment (Queued -> Failed is not a valid transition and the
            // deployment did nothing wrong). It keeps its state and the job is dispatched again.
            await HandleContentionAsync(deployment, attempt, cancellationToken);
            return;
        }

        await using (handle)
        {
            claim = await queue.ClaimRunAsync(deployment, cancellationToken);
            switch (claim)
            {
                case DeploymentRunClaim.Terminal or DeploymentRunClaim.Waiting:
                    break;
                case DeploymentRunClaim.Orphaned:
                    // The run lock was free while the deployment was running: the worker that ran it stopped.
                    await FailAsync(deploymentId,
                        "Deployment failed: the worker that ran it stopped before it finished. Deploy again.",
                        CancellationToken.None);
                    await NotifyFinishedAsync(deploymentId, DeploymentStatus.Failed);
                    break;
                case DeploymentRunClaim.Started:
                    await ExecuteAsync(deployment, preview, cancellationToken);
                    break;
            }
        }

        // Every terminal transition advances the queue, after the run lock is released so the next deployment of
        // this application can take it.
        if (claim is DeploymentRunClaim.Started or DeploymentRunClaim.Orphaned)
            try
            {
                await queue.AdvanceAsync(deployment.ServerId, CancellationToken.None);
            }
            catch (Exception ex) when (ex is DomainException or InvalidOperationException)
            {
                // The recurring queue maintenance job advances the queue later.
            }
    }

    private async Task HandleContentionAsync(Deployment deployment, int attempt, CancellationToken cancellationToken)
    {
        if (attempt == 0)
            await AppendAsync(deployment.Id, "system",
                "Another deployment of this application still holds its run lock; waiting for it to finish.",
                cancellationToken);
        if (attempt >= MaxContentionAttempts) return; // The queue maintenance job dispatches or fails it later.
        try
        {
            backgroundJobs.Schedule<RunDeploymentJob>(
                job => job.RunAttemptAsync(deployment.Id.Value, attempt + 1, CancellationToken.None),
                ContentionRetryDelay);
        }
        catch (BackgroundJobsUnavailableException)
        {
            // Without job storage the deployment stays queued for the queue maintenance job.
        }
    }

    private async Task<DeploymentStatus> ExecuteAsync(Deployment deployment, ApplicationPreview? preview,
        CancellationToken cancellationToken)
    {
        DeploymentId deploymentId = deployment.Id;
        var startedAt = Stopwatch.GetTimestamp();
        DeploymentStatus finalStatus = DeploymentStatus.Failed;
        using Activity? activity = VesselDiagnostics.ActivitySource.StartActivity("RunDeployment");
        activity?.SetTag("vessel.deployment_id", deploymentId.Value);
        activity?.SetTag("vessel.application_id", deployment.ApplicationId.Value);
        activity?.SetTag("vessel.server_id", deployment.ServerId.Value);
        activity?.SetTag("vessel.deployment_trigger", deployment.Trigger.ToString());
        VesselDiagnostics.ActiveDeployments.Add(1);
        await PublishStatusAsync(deployment, cancellationToken);

        // Build steps use a token that a user cancel or a superseding deployment can trigger; once the deploy phase
        // starts only the job token applies, so containers are never left half replaced.
        DeploymentCancellationScope run = cancellations.Register(deploymentId, cancellationToken);
        CancellationToken buildToken = run.Token;
        DeploymentWorkspace? workspace = null;
        ServerExecutionTarget? remoteTarget = null;
        string? remoteWorkspace = null;
        try
        {
            await ThrowIfCanceledAsync(deploymentId, buildToken);

            (AppEntity application, Server server, EnvironmentEntity environment, TeamId teamId) =
                LoadDeploymentContext(deploymentId);
            if (server.Status == ServerStatus.Unreachable)
                throw new DomainException("Server is unreachable.");
            if (server.ConnectionType == ServerConnectionType.Ssh)
            {
                // SSH servers: the workspace is prepared here, uploaded to the server, and the runtime CLI runs there.
                if (directoryTransfer is null || serverCommands is null)
                    throw new DomainException("Remote (SSH) deployments are not configured on this Vessel instance.");
                remoteTarget = ServerExecutionTarget.From(server);
            }
            // Defense in depth: host-local deployments run on the control-plane host and must be authorized by an
            // instance administrator (the triggering user, or for webhooks an instance-admin Owner/Admin of the team).
            if (!HostLocalExecutionGuard.IsAuthorized(authorization, server, deployment.ActorUserId))
                throw new DomainException(HostLocalExecutionGuard.UnattendedNotAuthorizedMessage);

            ContainerRuntimeTarget target = ContainerRuntimeTarget.For(server);
            if (preview is not null)
            {
                if (preview.Status == ApplicationPreviewStatus.Archived)
                    throw new DomainException("Preview is closed; its pull request is no longer deployed.");
                if (application.BuildConfiguration.BuildPack == ApplicationBuildPack.DockerCompose &&
                    !preview.IsSameRepository)
                    throw new DomainException(
                        "Docker Compose previews only deploy pull requests from the application repository; a fork's compose file is not trusted.");
            }

            workspace = await workspaces.PrepareAsync(deploymentId, buildToken);
            // DEP-06: a rollback to a listed local image skips clone and build.
            var reuseImage = await RollbackImageAvailableAsync(target, application, deployment, preview, buildToken);
            if (reuseImage)
            {
                await AppendAsync(deploymentId, "system",
                    $"Rolling back to image {deployment.RollbackImage}; clone and build are skipped.", buildToken);
            }
            else
            {
                // Refs are validated again at start (queue-time validation may predate a settings change).
                GitCheckoutRequest checkout =
                    DeploymentPlanBuilder.Checkout(application, deployment, preview, workspace.RepositoryDirectory);
                // DEP-08: private sources get short-lived credentials (installation token, OAuth token, deploy key)
                // and pinned SSH host keys, injected by the git client; they never appear in the URL or the log.
                (checkout, var sourceName) =
                    await AuthenticateCheckoutAsync(checkout, application, teamId, buildToken);
                if (sourceName is not null)
                    await AppendAsync(deploymentId, "system", $"Using Git source '{sourceName}'.", buildToken);
                await AppendAsync(deploymentId, "system", CheckoutMessage(checkout), buildToken);
                await git.CheckoutAsync(checkout, buildToken);
                GitCommitInfo commit = await git.GetCommitAsync(workspace.RepositoryDirectory, "HEAD", buildToken);
                await RecordSourceAsync(deploymentId, application, preview, commit, buildToken);
            }

            await ThrowIfCanceledAsync(deploymentId, buildToken);

            // Paths the runtime sees: the local workspace, or its mirror under the owned remote root of an SSH server.
            DeploymentWorkspace runtimeWorkspace = workspace;
            if (remoteTarget is not null)
            {
                var remoteRelative = RemoteWorkspacePath(deploymentId);
                runtimeWorkspace = MapWorkspace(workspace,
                    directoryTransfer!.ResolveHostPath(remoteTarget, remoteRelative));
            }

            DeploymentRuntimePlan plan = await CreatePlanAsync(application, environment, server, teamId, deployment,
                preview, workspace, runtimeWorkspace, buildToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, ".env", plan.EnvironmentFile, true,
                buildToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, "docker-compose.yml", plan.ComposeYaml, false,
                buildToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, "snapshots/docker-compose.redacted.yml",
                plan.RedactedComposeYaml, false, buildToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, "snapshots/env.redacted",
                plan.RedactedEnvironmentFile, false, buildToken);
            await RecordSnapshotAsync(deploymentId, "snapshots/docker-compose.redacted.yml", buildToken);

            if (remoteTarget is not null)
            {
                await AppendAsync(deploymentId, "system", "Uploading deployment files to the server over SSH.",
                    buildToken);
                remoteWorkspace = RemoteWorkspacePath(deploymentId);
                await directoryTransfer!.UploadAsync(remoteTarget, workspace.RootDirectory, remoteWorkspace,
                    buildToken);
                await ThrowIfCanceledAsync(deploymentId, buildToken);
            }

            await AppendAsync(deploymentId, "system", "Ensuring deployment network exists.", buildToken);
            await runtime.EnsureNetworkAsync(target, plan.NetworkName, Labels(deploymentId, application),
                buildToken);
            // Attach the proxy before application containers start (upstream v4.4.0); proxy type None skips this.
            ProxyEnsureResult proxy =
                await proxyLifecycle.EnsureForDeploymentAsync(server.Id, plan.NetworkName, buildToken);
            await AppendAsync(deploymentId, proxy.Succeeded ? "system" : "stderr", proxy.Message, buildToken);
            await ThrowIfCanceledAsync(deploymentId, buildToken);

            if (application.BuildConfiguration.BuildPack == ApplicationBuildPack.Dockerfile && !reuseImage)
            {
                var dockerfile = NormalizeRelativePath(application.BuildConfiguration.DockerfilePath, "Dockerfile");
                // The Dockerfile is read by the runtime CLI on this host: reject symlinks/escapes from the repository.
                workspaces.EnsureRegularFile(workspace.RepositoryDirectory, dockerfile);
                await StreamBuildAsync(target, plan, runtimeWorkspace, dockerfile, deployment, application, buildToken);
                await ThrowIfCanceledAsync(deploymentId, buildToken);
            }

            await StreamComposeAsync(target, plan, runtimeWorkspace, deploymentId, ["config", "--quiet"], buildToken);
            await ThrowIfCanceledAsync(deploymentId, buildToken);

            // DEP-05: from here on containers are replaced; the deployment is never auto-cancelled any more.
            if (!await queue.TryStartDeployPhaseAsync(deployment, buildToken))
                throw new DeploymentCanceledException();
            run.Dispose();
            await AppendAsync(deploymentId, "system", "Deploy phase started; replacing containers.", cancellationToken);

            await StreamComposeAsync(target, plan, runtimeWorkspace, deploymentId,
                reuseImage
                    ? ["up", "--detach", "--remove-orphans", "--no-build"]
                    : ["up", "--detach", "--remove-orphans", "--build"], cancellationToken);

            var healthCheckUrl = preview is null
                ? plan.HealthCheckUrl
                : await ResolvePreviewHealthCheckUrlAsync(target, plan, runtimeWorkspace, cancellationToken);
            if (remoteTarget is null)
                await RunHealthCheckAsync(healthCheckUrl, deploymentId, cancellationToken);
            else
                await RunRemoteHealthCheckAsync(remoteTarget, healthCheckUrl, deploymentId, cancellationToken);
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
            if (preview is null)
                await PruneImagesAsync(target, application, plan.ImageName, CancellationToken.None);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested || buildToken.IsCancellationRequested)
        {
            await CancelAsync(deploymentId, CancellationToken.None);
            finalStatus = DeploymentStatus.CanceledByUser;
        }
        catch (DeploymentCanceledException)
        {
            await CancelAsync(deploymentId, CancellationToken.None);
            finalStatus = DeploymentStatus.CanceledByUser;
        }
        catch (Exception ex)
        {
            await FailAsync(deploymentId, $"Deployment failed: {ex.Message}", CancellationToken.None);
            finalStatus = DeploymentStatus.Failed;
        }
        finally
        {
            run.Dispose();
            if (remoteTarget is not null && remoteWorkspace is not null)
                await CleanupRemoteWorkspaceAsync(remoteTarget, remoteWorkspace, deploymentId);
            if (workspace is not null)
                await workspaces.CleanupAsync(deploymentId, CancellationToken.None);
            activity?.SetTag("vessel.deployment_status", finalStatus.ToString());
            VesselDiagnostics.DeploymentRuns.Add(1,
                new TagList { { "status", finalStatus.ToString() } });
            VesselDiagnostics.DeploymentDurationSeconds.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                new TagList { { "status", finalStatus.ToString() } });
            VesselDiagnostics.ActiveDeployments.Add(-1);
            await NotifyFinishedAsync(deploymentId, finalStatus);
        }

        return finalStatus;
    }

    private Task NotifyFinishedAsync(DeploymentId deploymentId, DeploymentStatus finalStatus)
    {
        // Raised after the terminal state is persisted; the publisher never throws into the runner.
        return notifications.PublishAsync(() =>
        {
            Deployment deployment = dbContext.Deployments.Single(item => item.Id == deploymentId);
            (AppEntity application, _, _, TeamId teamId) = LoadDeploymentContext(deploymentId);
            DeploymentNotificationOutcome outcome = finalStatus switch
            {
                DeploymentStatus.Succeeded => DeploymentNotificationOutcome.Succeeded,
                DeploymentStatus.CanceledByUser => DeploymentNotificationOutcome.Cancelled,
                _ => DeploymentNotificationOutcome.Failed
            };
            return new TeamNotification(teamId, OperationalNotifications.DeploymentFinished(
                deploymentId.Value, application.Id.Value, application.Name.Value, deployment.CommitSha, outcome));
        });
    }

    private async Task StreamBuildAsync(
        ContainerRuntimeTarget target,
        DeploymentRuntimePlan plan,
        DeploymentWorkspace workspace,
        string dockerfile,
        Deployment deployment,
        AppEntity application,
        CancellationToken cancellationToken)
    {
        await AppendAsync(deployment.Id, "system", "Building Docker image.", cancellationToken);
        Dictionary<string, string> labels = Labels(deployment.Id, application);
        if (GitRef.IsCommitShaValue(deployment.CommitSha)) labels["vessel.commit"] = deployment.CommitSha!;
        await foreach (ProcessOutputLine line in runtime.BuildImageAsync(
                           target,
                           new DockerBuildCommand(
                               workspace.RepositoryDirectory,
                               dockerfile,
                               plan.ImageName,
                               labels,
                               new Dictionary<string, string>(),
                               TimeSpan.FromMinutes(30),
                               LogRedactor.ProcessProfile),
                           cancellationToken))
            await AppendAsync(deployment.Id, StreamName(line.Stream), line.Content, cancellationToken);
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
                               TimeSpan.FromMinutes(30),
                               LogRedactor.ProcessProfile),
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
                               TimeSpan.FromMinutes(1),
                               LogRedactor.ProcessProfile),
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

            // The deploy phase already started: a cancel request no longer interrupts the deployment.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new DomainException($"Health check failed before timeout. {lastError?.Message}");
    }

    private async Task<DeploymentRuntimePlan> CreatePlanAsync(
        AppEntity application,
        EnvironmentEntity environment,
        Server server,
        TeamId teamId,
        Deployment deployment,
        ApplicationPreview? preview,
        DeploymentWorkspace workspace,
        DeploymentWorkspace runtimeWorkspace,
        CancellationToken cancellationToken)
    {
        DeploymentId deploymentId = deployment.Id;
        ResolvedEnvironment resolved =
            await ResolveEnvironmentAsync(application, environment, server, teamId, deploymentId, preview,
                cancellationToken);
        IReadOnlyDictionary<string, string> env = resolved.Values;
        // DEP-03/SEC-09: from here on every log line and command output is redacted with the deployment's values.
        _logRedactor = new DeploymentLogRedactor(redactor, DeploymentLogRedactor.CreateContext(env,
            resolved.SecretKeys, application.DeploymentSettings.RedactAllEnvValuesInLogs));
        var envFile = BuildEnvironmentFile(env);
        var redactedEnv = redactor.Redact(envFile, new RedactionContext(env.Values.ToArray()));
        DeploymentNaming naming = DeploymentPlanBuilder.Naming(application.Id, server.Id, deploymentId, preview);
        // DEP-06: production images are tagged by commit (rollback targets); a rollback reuses its listed image.
        if (preview is null)
            naming = naming with
            {
                ImageName = deployment.RollbackImage ??
                            DeploymentPlanBuilder.ProductionImage(application.Id, deployment.CommitSha, deploymentId)
            };
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
                ? await CreateComposePassthroughAsync(workspace, runtimeWorkspace, application, naming, port, labels,
                    cancellationToken)
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
        DeploymentWorkspace runtimeWorkspace,
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
            naming.PreviewSuffix,
            string.Equals(runtimeWorkspace.RepositoryDirectory, workspace.RepositoryDirectory, StringComparison.Ordinal)
                ? null
                : runtimeWorkspace.RepositoryDirectory));
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

    private sealed record ResolvedEnvironment(
        IReadOnlyDictionary<string, string> Values,
        IReadOnlySet<string> SecretKeys);

    private async Task<ResolvedEnvironment> ResolveEnvironmentAsync(
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

        var secretKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (EnvironmentVariable variable in selection.Variables)
        {
            var isSecret = variable.ValueKind == EnvironmentVariableValueKind.Secret;
            values[variable.Key.Value] = isSecret && variable.SecretReferenceId.HasValue
                ? await secretVault.RevealForDeploymentAsync(teamId, variable.SecretReferenceId.Value,
                    cancellationToken)
                : variable.PlainValue ?? string.Empty;
            if (isSecret) secretKeys.Add(variable.Key.Value);
            else secretKeys.Remove(variable.Key.Value);
        }

        return new ResolvedEnvironment(values, secretKeys);
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

    private async Task RecordSourceAsync(
        DeploymentId deploymentId,
        AppEntity application,
        ApplicationPreview? preview,
        GitCommitInfo commit,
        CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        await queue.TransitionAsync(deployment, (item, now) => item.RecordSource(application.GitSource.RepositoryUrl.Value,
            preview?.SourceBranch ?? application.GitSource.Branch, commit.Sha, commit.Subject, now), cancellationToken);
        await AppendAsync(deploymentId, "system",
            $"Checked out {commit.Sha[..Math.Min(12, commit.Sha.Length)]} {commit.Subject}", cancellationToken);
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

    private async Task<(GitCheckoutRequest Checkout, string? SourceName)> AuthenticateCheckoutAsync(
        GitCheckoutRequest checkout, AppEntity application, TeamId teamId, CancellationToken cancellationToken)
    {
        if (gitCredentials is null)
        {
            if (application.SourceId is not null)
                throw new DomainException("Private Git sources are not configured on this Vessel instance.");
            return (checkout, null);
        }

        GitCloneAccess access = await gitCredentials.ForApplicationAsync(application, teamId, cancellationToken);
        return (checkout with { RepositoryUrl = access.RepositoryUri, Authentication = access.Authentication },
            access.SourceName);
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
        await queue.TransitionAsync(deployment, (item, now) => item.RecordConfigurationSnapshot(reference, now),
            cancellationToken);
    }

    private async Task SucceedAsync(DeploymentId deploymentId, string artifactReference,
        CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        await queue.TransitionAsync(deployment, (item, now) =>
        {
            var lateCancel = item.Status == DeploymentStatus.CancelRequested;
            item.MarkSucceeded(artifactReference, now);
            DeploymentLog.Append(dbContext, item, "system",
                lateCancel
                    ? "Deployment completed successfully. Cancellation was requested after containers started being replaced, so it was not applied."
                    : "Deployment completed successfully.", now);
        }, cancellationToken);
    }

    private async Task FailAsync(DeploymentId deploymentId, string reason, CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        var message = LogRedactor.Redact(reason);
        await queue.TransitionAsync(deployment, (item, now) =>
        {
            DeploymentLog.Append(dbContext, item, "stderr", message, now);
            if (!Deployment.IsTerminal(item.Status)) item.MarkFailed(now);
        }, cancellationToken);
    }

    private async Task CancelAsync(DeploymentId deploymentId, CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        await queue.TransitionAsync(deployment, (item, now) =>
        {
            if (!Deployment.IsTerminal(item.Status)) item.CancelByUser(now);
            DeploymentLog.Append(dbContext, item, "system",
                string.IsNullOrWhiteSpace(item.CancellationReason)
                    ? "Deployment canceled."
                    : $"Deployment canceled. {item.CancellationReason}", now);
        }, cancellationToken);
    }

    private async Task AppendAsync(DeploymentId deploymentId, string stream, string message,
        CancellationToken cancellationToken)
    {
        Deployment deployment = await GetDeploymentAsync(deploymentId, cancellationToken);
        var redacted = LogRedactor.Redact(message);
        DeploymentLogLine line = DeploymentLog.Append(dbContext, deployment, stream, redacted,
            timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
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
        // Another scope (user cancel, superseding deployment) may have changed the status.
        await dbContext.RefreshAsync(deployment, cancellationToken);
        if (deployment.Status == DeploymentStatus.CancelRequested ||
            deployment.Status == DeploymentStatus.CanceledByUser)
            throw new DeploymentCanceledException();
    }

    /// <summary>
    ///     A rollback reuses its image when it is a Dockerfile production rollback and the image is still present
    ///     locally; otherwise it rebuilds the target commit (only successful production commits are allowed).
    /// </summary>
    private async Task<bool> RollbackImageAvailableAsync(ContainerRuntimeTarget target, AppEntity application,
        Deployment deployment, ApplicationPreview? preview, CancellationToken cancellationToken)
    {
        if (!deployment.IsRollback || preview is not null || string.IsNullOrWhiteSpace(deployment.RollbackImage) ||
            application.BuildConfiguration.BuildPack != ApplicationBuildPack.Dockerfile)
            return false;
        IReadOnlyList<ImageSummary> images = await runtime.ListImagesAsync(target, cancellationToken);
        var available = images.Any(image => image.RepoTags.Contains(deployment.RollbackImage, StringComparer.Ordinal));
        if (!available)
            await AppendAsync(deployment.Id, "system",
                $"Image {deployment.RollbackImage} is no longer present locally; rebuilding commit {deployment.CommitSha}.",
                cancellationToken);
        return available;
    }

    /// <summary>
    ///     Keeps the images of the most recent successful production deployments (<c>ImagesToKeep</c>, Coolify
    ///     <c>docker_images_to_keep</c>) as rollback targets and removes older images of the application. Images still
    ///     used by a container are never forced out.
    /// </summary>
    private async Task PruneImagesAsync(ContainerRuntimeTarget target, AppEntity application, string currentImage,
        CancellationToken cancellationToken)
    {
        try
        {
            var repository = DeploymentPlanBuilder.ImageRepository(application.Id) + ":";
            var keep = dbContext.Deployments
                .Where(item => item.ApplicationId == application.Id && item.PreviewId == null &&
                               item.Status == DeploymentStatus.Succeeded && item.ArtifactReference != null)
                .OrderByDescending(item => item.FinishedAt)
                .Select(item => item.ArtifactReference!)
                .AsEnumerable()
                .Distinct(StringComparer.Ordinal)
                .Take(application.DeploymentSettings.ImagesToKeep + 1)
                .Append(currentImage)
                .ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<ImageSummary> images = await runtime.ListImagesAsync(target, cancellationToken);
            string[] remove = images.SelectMany(image => image.RepoTags)
                .Where(tag => tag.StartsWith(repository, StringComparison.Ordinal) &&
                              !tag[repository.Length..].StartsWith("pr-", StringComparison.Ordinal) &&
                              !keep.Contains(tag))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var image in remove)
                await runtime.RemoveImageAsync(target, image, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Image retention is best effort; a failure never changes the deployment result.
        }
    }

    private async Task<Deployment> GetDeploymentAsync(DeploymentId deploymentId, CancellationToken cancellationToken)
    {
        return await dbContext.DeploymentRepository.GetByIdAsync(deploymentId, cancellationToken)
               ?? throw new InvalidOperationException("Deployment was not found.");
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

    private static string RemoteWorkspacePath(DeploymentId deploymentId)
    {
        return $"deployments/{deploymentId.Value:N}";
    }

    /// <summary>The same workspace layout rooted at <paramref name="remoteRoot" /> (POSIX paths on the server).</summary>
    private static DeploymentWorkspace MapWorkspace(DeploymentWorkspace local, string remoteRoot)
    {
        string Map(string path)
        {
            var relative = Path.GetRelativePath(local.RootDirectory, path).Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new DomainException("Deployment workspace layout is invalid.");
            return relative == "." ? remoteRoot : $"{remoteRoot}/{relative}";
        }

        return new DeploymentWorkspace(remoteRoot, Map(local.RepositoryDirectory), Map(local.ComposeFilePath),
            Map(local.EnvironmentFilePath));
    }

    private async Task CleanupRemoteWorkspaceAsync(ServerExecutionTarget target, string remoteWorkspace,
        DeploymentId deploymentId)
    {
        try
        {
            await directoryTransfer!.DeleteAsync(target, remoteWorkspace, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AppendAsync(deploymentId, "stderr",
                $"Could not remove the deployment files from the server: {redactor.Redact(ex.Message)}",
                CancellationToken.None);
        }
    }

    /// <summary>The deployment health check of an SSH server runs on that server (curl or wget), like the containers.</summary>
    private async Task RunRemoteHealthCheckAsync(
        ServerExecutionTarget target,
        string healthCheckUrl,
        DeploymentId deploymentId,
        CancellationToken cancellationToken)
    {
        await AppendAsync(deploymentId, "system", "Running deployment health check on the server.", cancellationToken);
        DateTimeOffset deadline = timeProvider.GetUtcNow().Add(TimeSpan.FromSeconds(30));
        string? last = null;
        while (timeProvider.GetUtcNow() < deadline)
        {
            ServerCommandResult result = await serverCommands!.RunAsync(target,
                ["sh", "-c", RemoteHealthCheckScript, "vessel", healthCheckUrl], TimeSpan.FromSeconds(20), null,
                cancellationToken);
            var output = result.StandardOutput.Trim();
            if (output == "nohttp")
                throw new DomainException(
                    "Health check failed: the server has neither curl nor wget. Install one of them on the server.");
            if (int.TryParse(output, NumberStyles.None, CultureInfo.InvariantCulture, out var status) &&
                status is > 0 and < 500)
            {
                await AppendAsync(deploymentId, "system", $"Health check passed: {status}.", cancellationToken);
                return;
            }

            last = output.Length == 0 ? "no response" : $"status {output}";
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await ThrowIfCanceledAsync(deploymentId, cancellationToken);
        }

        throw new DomainException($"Health check failed before timeout ({last ?? "no response"}).");
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
