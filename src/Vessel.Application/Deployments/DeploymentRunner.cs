using System.Diagnostics;
using System.Globalization;
using System.Text;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Common;
using Vessel.Application.Deployments.BuildPacks;
using Vessel.Application.Deployments.Queue;
using Vessel.Application.Diagnostics;
using Vessel.Application.Docker;
using Vessel.Application.EnvironmentVariables;
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
using Vessel.Domain.Registries;
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
        RegistryDirectory? registryDirectory = null;
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
            registryDirectory = await PrepareRegistryDirectoryAsync(workspace, remoteTarget, deploymentId,
                buildToken);
            // DEP-06: a rollback to a listed image (local, or pulled again from the configured registry) skips clone
            // and build.
            var reuseImage = await RollbackImageAvailableAsync(target, application, teamId, deployment, preview,
                registryDirectory, buildToken);
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
                deployment = await GetDeploymentAsync(deploymentId, buildToken);
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

            (DeploymentRuntimePlan plan, PreparedBuild build) = await CreatePlanAsync(application, environment,
                server, teamId, deployment, preview, workspace, runtimeWorkspace, buildToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, ".env", plan.EnvironmentFile, true,
                buildToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, "docker-compose.yml", plan.ComposeYaml, false,
                buildToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, "snapshots/docker-compose.redacted.yml",
                plan.RedactedComposeYaml, false, buildToken);
            await workspaces.WriteTextAsync(workspace.RootDirectory, "snapshots/env.redacted",
                plan.RedactedEnvironmentFile, false, buildToken);
            await WriteBuildFilesAsync(workspace, build, remoteTarget is not null, buildToken);
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

            if (!reuseImage && build.Plan.BuildsImage)
            {
                await RunBuildAsync(target, server, plan, build, workspace, runtimeWorkspace, deployment, application,
                    buildToken);
                await ThrowIfCanceledAsync(deploymentId, buildToken);
                if (preview is null && build.RegistryImage is not null)
                {
                    await PushImageAsync(target, application, teamId, plan.ImageName, registryDirectory, deploymentId,
                        buildToken);
                    await ThrowIfCanceledAsync(deploymentId, buildToken);
                }
            }

            await StreamComposeAsync(target, plan, runtimeWorkspace, deploymentId, ["config", "--quiet"], buildToken);
            await ThrowIfCanceledAsync(deploymentId, buildToken);

            // DEP-05: from here on containers are replaced; the deployment is never auto-cancelled any more.
            if (!await queue.TryStartDeployPhaseAsync(deployment, buildToken))
                throw new DeploymentCanceledException();
            run.Dispose();
            await AppendAsync(deploymentId, "system", "Deploy phase started; replacing containers.", cancellationToken);

            // Images Vessel built (or reused) are never rebuilt by compose; only Docker Compose services build here.
            var composeBuilds = !reuseImage && application.BuildConfiguration.BuildPack == ApplicationBuildPack.DockerCompose;
            await StreamComposeAsync(target, plan, runtimeWorkspace, deploymentId,
                composeBuilds
                    ? ["up", "--detach", "--remove-orphans", "--build"]
                    : ["up", "--detach", "--remove-orphans", "--no-build"], cancellationToken);


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
            if (remoteTarget is not null && registryDirectory?.RemoteRelativePath is { } registryRemote)
                await CleanupRemoteWorkspaceAsync(remoteTarget, registryRemote, deploymentId);
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

    /// <summary>
    ///     Build inputs of one deployment: the build pack plan, the build values (build-time variables only) and the
    ///     registry repository production images are pushed to (null when not configured or not applicable).
    /// </summary>
    private sealed record PreparedBuild(
        BuildPlan Plan,
        BuildVariableSelection Values,
        string? RegistryImage);

    /// <summary>Isolated registry client configuration: the path the runtime sees and, on SSH servers, its remote path.</summary>
    private sealed record RegistryDirectory(string RuntimePath, string? RemoteRelativePath);

    private async Task<RegistryDirectory> PrepareRegistryDirectoryAsync(DeploymentWorkspace workspace,
        ServerExecutionTarget? remoteTarget, DeploymentId deploymentId, CancellationToken cancellationToken)
    {
        if (remoteTarget is null)
        {
            await workspaces.WriteTextAsync(workspace.RootDirectory, "registry/.vessel", string.Empty, true,
                cancellationToken);
            return new RegistryDirectory(Path.Combine(workspace.RootDirectory, "registry"), null);
        }

        // Outside the uploaded workspace (a rollback pull happens before the upload); removed after the deployment.
        var relative = RemoteWorkspacePath(deploymentId) + "-registry";
        return new RegistryDirectory(directoryTransfer!.ResolveHostPath(remoteTarget, relative), relative);
    }

    private async Task WriteBuildFilesAsync(DeploymentWorkspace workspace, PreparedBuild build, bool remote,
        CancellationToken cancellationToken)
    {
        foreach (GeneratedBuildFile file in build.Plan.Files)
            await workspaces.WriteTextAsync(workspace.RootDirectory, file.WorkspacePath, file.Contents, false,
                cancellationToken);
        if (build.Plan.Steps.OfType<RailpackPrepareStep>().FirstOrDefault() is { } prepare)
            await workspaces.WriteTextAsync(workspace.RootDirectory, prepare.PlanDirectory + "/.vessel", string.Empty,
                false, cancellationToken);
        // SSH servers read build values from this file (inside the uploaded workspace, removed afterwards); locally
        // they are set in the CLI's environment and never written.
        if (remote && build.Values.Values.Count > 0)
            await workspaces.WriteTextAsync(workspace.RootDirectory, BuildPackPlanner.BuildEnvironmentFile,
                ShellEnvFileWriter.Write(build.Values.Values), true, cancellationToken);
    }

    private async Task RunBuildAsync(
        ContainerRuntimeTarget target,
        Server server,
        DeploymentRuntimePlan plan,
        PreparedBuild build,
        DeploymentWorkspace workspace,
        DeploymentWorkspace runtimeWorkspace,
        Deployment deployment,
        AppEntity application,
        CancellationToken cancellationToken)
    {
        var remote = target.IsRemote;
        var context = RuntimePath(runtimeWorkspace.RepositoryDirectory, build.Plan.ContextPath, remote);
        // The context is read by the runtime CLI: it must be a directory inside the repository without symbolic links.
        if (build.Plan.ContextPath.Length > 0)
            workspaces.EnsureDirectory(workspace.RepositoryDirectory, build.Plan.ContextPath);
        var environment = new BuildEnvironment(build.Values.Values,
            remote && build.Values.Values.Count > 0
                ? RuntimePath(runtimeWorkspace.RootDirectory, BuildPackPlanner.BuildEnvironmentFile, true)
                : null);
        Dictionary<string, string> labels = Labels(deployment.Id, application);
        if (GitRef.IsCommitShaValue(deployment.CommitSha)) labels["vessel.commit"] = deployment.CommitSha!;
        var noCache = application.DeploymentSettings.ForceRebuild;

        foreach (BuildStep step in build.Plan.Steps)
            switch (step)
            {
                case DockerfileBuildStep dockerfile:
                    {
                        string dockerfilePath;
                        if (dockerfile.InWorkspace)
                        {
                            dockerfilePath = RuntimePath(runtimeWorkspace.RootDirectory, dockerfile.DockerfilePath, remote);
                        }
                        else
                        {
                            if (!dockerfile.Generated)
                                workspaces.EnsureRegularFile(workspace.RepositoryDirectory,
                                    BuildPackPlanner.Combine(build.Plan.ContextPath, dockerfile.DockerfilePath));
                            dockerfilePath = dockerfile.DockerfilePath;
                        }

                        await AppendAsync(deployment.Id, "system", "Building Docker image.", cancellationToken);
                        await foreach (ProcessOutputLine line in runtime.BuildImageAsync(target,
                                           new DockerBuildCommand(context, dockerfilePath, plan.ImageName, labels,
                                               build.Values.AsDictionary(), TimeSpan.FromMinutes(30),
                                               LogRedactor.ProcessProfile, environment.ShellFilePath, noCache),
                                           cancellationToken))
                            await AppendAsync(deployment.Id, StreamName(line.Stream), line.Content, cancellationToken);
                        break;
                    }
                case RailpackPrepareStep prepare:
                    await AppendAsync(deployment.Id, "system",
                        $"Generating the Railpack build plan (Railpack {ComponentVersions.Railpack}).", cancellationToken);
                    await foreach (ProcessOutputLine line in runtime.PrepareRailpackPlanAsync(target,
                                       new RailpackPrepareCommand(context,
                                           RuntimePath(runtimeWorkspace.RootDirectory, prepare.PlanDirectory, remote),
                                           prepare.PlanFileName, ComponentVersions.RailpackCliImage,
                                           prepare.ControlVariables, environment, prepare.BuildCommand,
                                           prepare.StartCommand, TimeSpan.FromMinutes(10), LogRedactor.ProcessProfile),
                                       cancellationToken))
                        await AppendAsync(deployment.Id, StreamName(line.Stream), line.Content, cancellationToken);
                    break;
                case RailpackBuildStep railpack:
                    {
                        // One Railpack build per server at a time: the shared BuildKit builder is stopped after each
                        // build (upstream v4.5.0), which must not interrupt another build.
                        DistributedLockHandle? builderLock = await locks.TryAcquireAsync(
                            $"railpack-builder:{server.Id.Value:D}", TimeSpan.FromHours(1), TimeSpan.FromMinutes(30),
                            cancellationToken) ?? throw new DomainException(
                            "Another Railpack build on this server did not finish within 30 minutes.");
                        await using (builderLock)
                        {
                            await AppendAsync(deployment.Id, "system", "Building Docker image with Railpack.",
                                cancellationToken);
                            await foreach (ProcessOutputLine line in runtime.BuildRailpackImageAsync(target,
                                               new RailpackBuildCommand(context,
                                                   RuntimePath(runtimeWorkspace.RootDirectory, railpack.PlanFile, remote),
                                                   plan.ImageName, ComponentVersions.RailpackFrontendImage,
                                                   ComponentVersions.BuildKitImage, RailpackBuilderName, labels,
                                                   environment, application.Id.Value.ToString("N"), noCache,
                                                   TimeSpan.FromMinutes(45), LogRedactor.ProcessProfile),
                                               cancellationToken))
                                await AppendAsync(deployment.Id, StreamName(line.Stream), line.Content,
                                    cancellationToken);
                        }

                        break;
                    }
                case NixpacksGenerateStep nixpacks:
                    await AppendAsync(deployment.Id, "stderr", ApplicationBuildPacks.NixpacksDeprecationMessage,
                        cancellationToken);
                    await AppendAsync(deployment.Id, "system", "Generating the Nixpacks Dockerfile.", cancellationToken);
                    await foreach (ProcessOutputLine line in runtime.GenerateNixpacksBuildAsync(target,
                                       new NixpacksGenerateCommand(context, plan.ImageName, environment,
                                           nixpacks.InstallCommand, nixpacks.BuildCommand, nixpacks.StartCommand,
                                           TimeSpan.FromMinutes(10), LogRedactor.ProcessProfile), cancellationToken))
                        await AppendAsync(deployment.Id, StreamName(line.Stream), line.Content, cancellationToken);
                    break;
            }
    }

    /// <summary>Name of the per-server buildx builder of Railpack builds.</summary>
    public const string RailpackBuilderName = "vessel-railpack";

    private static string RuntimePath(string root, string relative, bool remote)
    {
        if (relative.Length == 0) return root;
        return remote ? $"{root.TrimEnd('/')}/{relative}" : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    ///     Pushes a production image to the application's registry (DEP-06): pushed images stay rollback targets after
    ///     the local image is pruned. Credentials come from the team's registry credential and are sent on standard input.
    /// </summary>
    private async Task PushImageAsync(ContainerRuntimeTarget target, AppEntity application, TeamId teamId,
        string image, RegistryDirectory? registryDirectory, DeploymentId deploymentId,
        CancellationToken cancellationToken)
    {
        RegistryAuth? auth = await RegistryAuthAsync(application, teamId, cancellationToken);
        await AppendAsync(deploymentId, "system", $"Pushing image {image} to the registry.", cancellationToken);
        await foreach (ProcessOutputLine line in runtime.PushImageAsync(target,
                           new RegistryImageCommand(image, auth, registryDirectory!.RuntimePath,
                               TimeSpan.FromMinutes(30), RegistryRedaction(auth)), cancellationToken))
            await AppendAsync(deploymentId, StreamName(line.Stream), line.Content, cancellationToken);
        await auditWriter.RecordAsync(teamId, null, AuditActions.DeploymentImagePushed,
            new AuditTarget("deployment", deploymentId.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["image"] = image }, cancellationToken);
    }

    private ProcessRedactionProfile RegistryRedaction(RegistryAuth? auth)
    {
        ProcessRedactionProfile profile = LogRedactor.ProcessProfile;
        return auth is null
            ? profile
            : profile with { SecretValues = [.. profile.SecretValues, auth.Password] };
    }

    /// <summary>The team registry credential of the application, or null to push/pull with the server's own login.</summary>
    private async Task<RegistryAuth?> RegistryAuthAsync(AppEntity application, TeamId teamId,
        CancellationToken cancellationToken)
    {
        if (application.BuildConfiguration.RegistryCredentialId is not { } credentialId) return null;
        RegistryCredential credential = dbContext.RegistryCredentials
                                            .SingleOrDefault(item => item.Id == credentialId && item.TeamId == teamId)
                                        ?? throw new DomainException(
                                            "The registry credential of this application no longer exists.");
        var password = await secretVault.RevealForDeploymentAsync(teamId, credential.PasswordReferenceId,
            cancellationToken);
        return new RegistryAuth(credential.Registry, credential.Username, password);
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

    private async Task<(DeploymentRuntimePlan Plan, PreparedBuild Build)> CreatePlanAsync(
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
        BuildConfiguration config = application.BuildConfiguration;
        var isCompose = config.BuildPack == ApplicationBuildPack.DockerCompose;
        var port = application.RuntimeConfiguration.ExposedPort?.Value ?? DeploymentPlanBuilder.DefaultPort;
        ResolvedDeploymentEnvironment resolved =
            await ResolveEnvironmentAsync(application, environment, server, teamId, deployment, preview, port,
                cancellationToken);
        IReadOnlyDictionary<string, string> env = resolved.AllValues;
        // DEP-03/SEC-09: from here on every log line and command output is redacted with the deployment's values.
        _logRedactor = new DeploymentLogRedactor(redactor, DeploymentLogRedactor.CreateContext(env,
            resolved.SecretKeys, application.DeploymentSettings.RedactAllEnvValuesInLogs));
        if (resolved.UnresolvedReferences.Count > 0)
            await AppendAsync(deploymentId, "stderr",
                "Environment values reference variables that are not defined (empty strings are used): " +
                string.Join(", ", resolved.UnresolvedReferences), cancellationToken);

        BuildVariableSelection buildValues = BuildVariablePolicy.Select(resolved.Build,
            ApplicationBuildPacks.DisplayName(config.BuildPack));
        foreach (var warning in buildValues.Warnings)
            await AppendAsync(deploymentId, "stderr", warning, cancellationToken);
        BuildPlan buildPlan = BuildPackPlanner.Plan(config, port, buildValues.Values);
        foreach (var note in buildPlan.Notes)
            await AppendAsync(deploymentId, "system", note, cancellationToken);

        // Docker Compose interpolates the compose file from the env file, so build values (as build.args) need to be in
        // it too; every other stack reads the env file as container environment, which gets runtime values only.
        IEnumerable<ResolvedVariable> envFileVariables = isCompose
            ? resolved.Runtime.Concat(resolved.Build.Where(build =>
                resolved.Runtime.All(runtimeVariable => runtimeVariable.Key != build.Key)))
            : resolved.Runtime;
        var envFile = ComposeEnvFileWriter.Write(envFileVariables);
        var redactedEnv = redactor.Redact(envFile, new RedactionContext(env.Values.ToArray()));
        DeploymentNaming naming = DeploymentPlanBuilder.Naming(application.Id, server.Id, deploymentId, preview);
        // DEP-06: production images are tagged by commit (rollback targets); a rollback reuses its listed image.
        // With a registry configured, production images are named in the registry repository and pushed.
        var registryImage = preview is null && !isCompose ? config.RegistryImage : null;
        if (preview is null)
            naming = naming with
            {
                ImageName = deployment.RollbackImage ??
                            DeploymentPlanBuilder.ProductionImage(application.Id, deployment.CommitSha, deploymentId,
                                registryImage)
            };
        var healthPath = application.RuntimeConfiguration.HealthCheckPath.StartsWith('/')
            ? application.RuntimeConfiguration.HealthCheckPath
            : "/" + application.RuntimeConfiguration.HealthCheckPath;
        var healthUrl = $"http://localhost:{port}{healthPath}";
        IReadOnlyDictionary<string, string> labels = Labels(deploymentId, application, preview);
        var serviceName = naming.ServiceName;
        string compose;
        if (!isCompose)
        {
            compose = CreateImageStack(naming, port, labels);
        }
        else
        {
            ComposeNormalizationResult normalized = await CreateRepositoryComposeAsync(workspace, runtimeWorkspace,
                application, deployment, naming, port, labels,
                buildValues.Values.Select(pair => pair.Key).ToArray(), cancellationToken);
            compose = normalized.Yaml;
            serviceName = normalized.PrimaryService;
            foreach (var warning in normalized.Warnings)
                await AppendAsync(deploymentId, "system", warning, cancellationToken);
            await RecordElevatedComposeAsync(teamId, deployment, normalized.ElevatedSettings, cancellationToken);
        }

        var plan = new DeploymentRuntimePlan(
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
        return (plan, new PreparedBuild(buildPlan, buildValues, registryImage));
    }

    /// <summary>Elevated compose settings (SEC-11) are logged in the deployment log and audited.</summary>
    private async Task RecordElevatedComposeAsync(TeamId teamId, Deployment deployment,
        IReadOnlyList<string> elevated, CancellationToken cancellationToken)
    {
        if (elevated.Count == 0) return;
        await AppendAsync(deployment.Id, "stderr",
            "Host-level compose settings allowed because an instance administrator authorized this deployment: " +
            string.Join("; ", elevated) + ".", cancellationToken);
        await auditWriter.RecordAsync(teamId, deployment.ActorUserId, AuditActions.DeploymentComposeElevated,
            new AuditTarget("deployment", deployment.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["settings"] = elevated.ToArray() }, cancellationToken);
    }

    /// <summary>
    ///     Reads and normalizes the repository compose file (DEP-11/SEC-11): the path is relative to the base directory;
    ///     a missing file fails the deployment (no silent fallback); included files are read from the repository (each
    ///     inside it, at most five levels); host-level settings need an instance administrator as the deployment actor.
    /// </summary>
    private async Task<ComposeNormalizationResult> CreateRepositoryComposeAsync(
        DeploymentWorkspace workspace,
        DeploymentWorkspace runtimeWorkspace,
        AppEntity application,
        Deployment deployment,
        DeploymentNaming naming,
        int port,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyList<string> buildArgumentKeys,
        CancellationToken cancellationToken)
    {
        BuildConfiguration config = application.BuildConfiguration;
        var composePath = NormalizeRelativePath(
            BuildPackPlanner.Combine(BuildPackPlanner.ContextPath(config.BaseDirectory),
                config.DockerfilePath ?? "docker-compose.yml"), "docker-compose.yml");
        string raw;
        try
        {
            raw = await workspaces.ReadTextAsync(workspace.RepositoryDirectory, composePath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            throw new DomainException($"Compose file {composePath} was not found in the repository.");
        }

        if (string.IsNullOrWhiteSpace(raw.TrimStart('﻿')))
            throw new DomainException($"Compose file {composePath} is empty.");

        var included = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Queue<(string Path, string Content, int Depth)>();
        pending.Enqueue((composePath, raw, 0));
        while (pending.Count > 0)
        {
            (var path, var content, var depth) = pending.Dequeue();
            foreach (var include in composeDocuments.ListIncludes(content, workspace.RepositoryDirectory, path))
            {
                if (included.ContainsKey(include) || include == composePath) continue;
                if (depth >= 5) throw new DomainException("Compose 'include' is nested too deeply.");
                string text;
                try
                {
                    text = await workspaces.ReadTextAsync(workspace.RepositoryDirectory, include, cancellationToken);
                }
                catch (FileNotFoundException)
                {
                    throw new DomainException($"Included compose file {include} was not found in the repository.");
                }

                included[include] = text;
                pending.Enqueue((include, text, depth + 1));
            }
        }

        var allowElevated = deployment.ActorUserId is { } actor && authorization.IsInstanceAdmin(actor);
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
                : runtimeWorkspace.RepositoryDirectory)
        {
            AllowElevated = allowElevated,
            IncludedFiles = included,
            PrimaryService = config.ComposeService,
            BuildArgumentKeys = buildArgumentKeys
        });
    }

    /// <summary>The single-service stack of an image Vessel built (or reuses): no build section.</summary>
    private string CreateImageStack(DeploymentNaming naming, int port, IReadOnlyDictionary<string, string> labels)
    {
        var portText = port.ToString(CultureInfo.InvariantCulture);
        // Production keeps its fixed host port; previews bind an ephemeral loopback port so they never collide.
        var binding = naming.IsPreview ? $"127.0.0.1::{portText}" : $"{portText}:{portText}";
        return composeDocuments.CreateDockerfileStack(new DockerfileStackSpec(
            naming.ServiceName,
            naming.ImageName,
            null,
            null,
            naming.NetworkName,
            naming.UpstreamAlias,
            binding,
            labels));
    }

    /// <summary>
    ///     Deployment variables in deterministic order (DEP-10): defaults, then team, project, environment, server and
    ///     application variables (page order, then key), then preview copies. Later definitions win. Runtime values and
    ///     build values are resolved separately, so runtime-only values never reach a build.
    /// </summary>
    private async Task<ResolvedDeploymentEnvironment> ResolveEnvironmentAsync(
        AppEntity application,
        EnvironmentEntity environment,
        Server server,
        TeamId teamId,
        Deployment deployment,
        ApplicationPreview? preview,
        int port,
        CancellationToken cancellationToken)
    {
        DeploymentId deploymentId = deployment.Id;
        var variables = new List<DeploymentVariable>();
        if (application.DeploymentSettings.IncludeDefaultVariables)
        {
            variables.Add(Default("VESSEL_APPLICATION_ID", application.Id.Value.ToString("D"), true));
            variables.Add(Default("VESSEL_ENVIRONMENT_ID", environment.Id.Value.ToString("D"), false));
            variables.Add(Default("VESSEL_SERVER_ID", server.Id.Value.ToString("D"), false));
            variables.Add(Default("VESSEL_BRANCH", preview?.SourceBranch ?? application.GitSource.Branch, true));
            if (preview is not null)
                variables.Add(Default("VESSEL_PULL_REQUEST_ID",
                    preview.PullRequestNumber.ToString(CultureInfo.InvariantCulture), true));
            // Runtime only: a per-commit build value would defeat the build cache (upstream SOURCE_COMMIT).
            if (GitRef.IsCommitShaValue(deployment.CommitSha))
                variables.Add(Default("SOURCE_COMMIT", deployment.CommitSha!, false));
        }

        // Upstream adds PORT (first exposed port, not for Compose) and HOST unless the application defines them.
        if (application.BuildConfiguration.BuildPack != ApplicationBuildPack.DockerCompose)
            variables.Add(Default("PORT", port.ToString(CultureInfo.InvariantCulture), false));
        variables.Add(Default("HOST", "0.0.0.0", false));

        EnvironmentVariable[] applicable = dbContext.EnvironmentVariables
            .Where(variable => variable.TeamId == teamId && (variable.IsRuntime || variable.IsBuildTime))
            .AsEnumerable()
            .Where(variable => AppliesTo(variable, application, environment, server))
            .OrderBy(variable => ScopeRank(variable.TargetType))
            .ThenBy(variable => variable.SortOrder)
            .ThenBy(variable => variable.Key.Value, StringComparer.Ordinal)
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
        {
            var isSecret = variable.ValueKind == EnvironmentVariableValueKind.Secret;
            var value = isSecret && variable.SecretReferenceId.HasValue
                ? await secretVault.RevealForDeploymentAsync(teamId, variable.SecretReferenceId.Value,
                    cancellationToken)
                : variable.PreserveComposeNull ? null : variable.PlainValue ?? string.Empty;
            variables.Add(DeploymentVariable.From(variable, value));
        }

        return DeploymentEnvironmentResolver.Resolve(variables);
    }

    private static DeploymentVariable Default(string key, string value, bool buildTime)
    {
        return new DeploymentVariable(key, value, IsBuildTime: buildTime, IsRuntime: true, IsDefault: true);
    }

    private static int ScopeRank(EnvironmentVariableTargetType targetType)
    {
        return targetType switch
        {
            EnvironmentVariableTargetType.Team => 0,
            EnvironmentVariableTargetType.Project => 1,
            EnvironmentVariableTargetType.Environment => 2,
            EnvironmentVariableTargetType.Server => 3,
            EnvironmentVariableTargetType.Application => 4,
            _ => 5
        };
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
    ///     A production rollback (every build pack except Docker Compose) reuses its image when it is still present
    ///     locally, or when it can be pulled again from the application's registry (DEP-06, upstream v4.5.0 rollback to
    ///     registry images); otherwise it rebuilds the target commit (only successful production commits are allowed).
    /// </summary>
    private async Task<bool> RollbackImageAvailableAsync(ContainerRuntimeTarget target, AppEntity application,
        TeamId teamId, Deployment deployment, ApplicationPreview? preview, RegistryDirectory? registryDirectory,
        CancellationToken cancellationToken)
    {
        if (!deployment.IsRollback || preview is not null || string.IsNullOrWhiteSpace(deployment.RollbackImage) ||
            application.BuildConfiguration.BuildPack == ApplicationBuildPack.DockerCompose)
            return false;
        IReadOnlyList<ImageSummary> images = await runtime.ListImagesAsync(target, cancellationToken);
        if (images.Any(image => image.RepoTags.Contains(deployment.RollbackImage, StringComparer.Ordinal)))
            return true;

        var registry = application.BuildConfiguration.RegistryImage;
        if (registry is not null && registryDirectory is not null &&
            deployment.RollbackImage.StartsWith(registry + ":", StringComparison.Ordinal))
            try
            {
                RegistryAuth? auth = await RegistryAuthAsync(application, teamId, cancellationToken);
                await AppendAsync(deployment.Id, "system",
                    $"Image {deployment.RollbackImage} is not present locally; pulling it from the registry.",
                    cancellationToken);
                await foreach (ProcessOutputLine line in runtime.PullImageAsync(target,
                                   new RegistryImageCommand(deployment.RollbackImage, auth,
                                       registryDirectory.RuntimePath, TimeSpan.FromMinutes(30),
                                       RegistryRedaction(auth)), cancellationToken))
                    await AppendAsync(deployment.Id, StreamName(line.Stream), line.Content, cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await AppendAsync(deployment.Id, "stderr",
                    $"Pulling {deployment.RollbackImage} failed: {redactor.Redact(ex.Message)}", cancellationToken);
            }

        await AppendAsync(deployment.Id, "system",
            $"Image {deployment.RollbackImage} is no longer available; rebuilding commit {deployment.CommitSha}.",
            cancellationToken);
        return false;
    }

    /// <summary>
    ///     Keeps the images of the most recent successful production deployments (<c>ImagesToKeep</c>, Coolify
    ///     <c>docker_images_to_keep</c>) as rollback targets and removes older local images of the application (its own
    ///     repository and its registry repository; pushed images stay in the registry). Images still used by a
    ///     container are never forced out.
    /// </summary>
    private async Task PruneImagesAsync(ContainerRuntimeTarget target, AppEntity application, string currentImage,
        CancellationToken cancellationToken)
    {
        try
        {
            var repositories = DeploymentPlanBuilder.ImageRepositories(application);
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
                .Where(tag => repositories.Any(repository => tag.StartsWith(repository + ":", StringComparison.Ordinal) &&
                                                             !tag[(repository.Length + 1)..].StartsWith("pr-",
                                                                 StringComparison.Ordinal)) &&
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
