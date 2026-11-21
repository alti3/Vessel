using System.Text;
using System.Text.Json;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Docker;
using Vessel.Application.Jobs;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Proxy.Domains;
using Vessel.Application.Processes;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Storage;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.Domain.Servers;
using Vessel.Domain.Services;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.ManagedServices;

public sealed class ManagedDatabaseService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IContainerRuntimeClient runtime,
    IManagedServiceWorkspace workspaces,
    IBackgroundJobDispatcher jobs,
    IRecurringJobScheduler recurringJobs,
    IDistributedLockManager locks,
    ISecretVault secretVault,
    ISecretRedactor redactor,
    IDatabaseBackupProvider backupProvider,
    IObjectStorage objectStorage,
    ServiceTemplateCatalog templates,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    INotificationEventPublisher notifications,
    ServiceRoutingCoordinator serviceRouting)
{
    private static readonly TimeSpan LockLeaseMargin = TimeSpan.FromMinutes(10);

    public IReadOnlyList<ServiceTemplateSummary> ListTemplates()
    {
        return templates.List();
    }

    public async Task<DatabaseLifecycleResult> QueueLifecycleActionAsync(
        UserId actorUserId,
        TeamId teamId,
        DatabaseResourceId databaseId,
        DatabaseLifecycleAction action,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.ProjectsWrite);
        DatabaseResource database = await GetDatabaseForTeamAsync(teamId, databaseId, cancellationToken);
        var jobId = jobs.Enqueue<RunDatabaseLifecycleJob>(job =>
            job.RunAsync(database.Id.Value, action, CancellationToken.None));
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.DatabaseLifecycleActionQueued,
            new AuditTarget("database", database.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["action"] = action.ToString(), ["jobId"] = jobId },
            cancellationToken);
        return new DatabaseLifecycleResult(database.Id.Value, database.LifecycleState, database.HealthState,
            $"Database {action} queued.");
    }

    public async Task<DatabaseLifecycleResult> RunLifecycleActionAsync(
        DatabaseResourceId databaseId,
        DatabaseLifecycleAction action,
        CancellationToken cancellationToken = default)
    {
        DatabaseResource database =
            await dbContext.DatabaseResourceRepository.GetByIdAsync(databaseId, cancellationToken)
            ?? throw new InvalidOperationException("Database was not found.");
        Server server = dbContext.Servers.Single(server => server.Id == database.ServerId);
        TeamId teamId = ResolveTeam(database.EnvironmentId);
        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            DatabaseOperationLocks.KeyFor(database.Id), TimeSpan.FromHours(1), TimeSpan.Zero, cancellationToken);
        if (handle is null) throw new DomainException("Another database operation is already running.");

        try
        {
            switch (action)
            {
                case DatabaseLifecycleAction.Start:
                    await StartDatabaseAsync(database, server, cancellationToken);
                    break;
                case DatabaseLifecycleAction.Stop:
                    await ComposeAsync(database, server, ["stop"], cancellationToken);
                    database.MarkStopped(timeProvider.GetUtcNow());
                    break;
                case DatabaseLifecycleAction.Restart:
                    database.MarkRestarting(timeProvider.GetUtcNow());
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await ComposeAsync(database, server, ["restart"], cancellationToken);
                    database.MarkRunning(ContainerName(database), SnapshotReference(database),
                        timeProvider.GetUtcNow());
                    break;
                case DatabaseLifecycleAction.Delete:
                    await ComposeAsync(database, server, ["down", "--volumes", "--remove-orphans"], cancellationToken);
                    database.MarkDeleted(timeProvider.GetUtcNow());
                    break;
                case DatabaseLifecycleAction.Inspect:
                    database.ChangeHealth(DatabaseHealthState.Healthy, timeProvider.GetUtcNow());
                    break;
                default:
                    throw new DomainException("Unsupported database lifecycle action.");
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await auditWriter.RecordAsync(teamId, null, AuditActions.DatabaseLifecycleActionCompleted,
                new AuditTarget("database", database.Id.Value.ToString("D")), null,
                new Dictionary<string, object?>
                { ["action"] = action.ToString(), ["state"] = database.LifecycleState.ToString() },
                cancellationToken);
            return new DatabaseLifecycleResult(database.Id.Value, database.LifecycleState, database.HealthState,
                $"Database {action} completed.");
        }
        catch
        {
            database.MarkFailed(timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ServiceResourceSummary> CreateServiceFromTemplateAsync(
        UserId actorUserId,
        TeamId teamId,
        CreateServiceFromTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        var projectId = new ProjectId(request.ProjectId);
        var environmentId = new EnvironmentId(request.EnvironmentId);
        var serverId = new ServerId(request.ServerId);
        RequireProject(actorUserId, teamId, projectId, VesselPermissions.ProjectsWrite);
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ProjectsWrite);
        EnsureEnvironmentBelongsToProject(environmentId, projectId);
        ServiceTemplateDefinition template = templates.Get(request.TemplateKey);

        var service = ServiceResource.Create(teamId, environmentId, serverId, new ResourceName(request.Name),
            template.Key, template.Version, JsonSerializer.Serialize(request.Inputs), timeProvider.GetUtcNow());
        await dbContext.ServiceResourceRepository.AddAsync(service, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        var jobId = jobs.Enqueue<ProvisionServiceTemplateJob>(job =>
            job.RunAsync(service.Id.Value, CancellationToken.None));
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ServiceCreated,
            new AuditTarget("service", service.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["template"] = template.Key, ["jobId"] = jobId }, cancellationToken);
        return ToSummary(service);
    }

    public async Task<ServiceResourceSummary> ProvisionServiceAsync(
        ServiceResourceId serviceId,
        CancellationToken cancellationToken = default)
    {
        ServiceResource service = await dbContext.ServiceResourceRepository.GetByIdAsync(serviceId, cancellationToken)
                                  ?? throw new InvalidOperationException("Service was not found.");
        Server server = dbContext.Servers.Single(item => item.Id == service.ServerId);
        Dictionary<string, string> inputs =
            JsonSerializer.Deserialize<Dictionary<string, string>>(service.ConfigurationJson) ?? [];
        ServiceProvisioningPlan plan = templates.CreatePlan(service.TemplateKey, service.Id.Value, service.Name.Value,
            inputs);
        service.MarkProvisioning(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);

        // Compose services with domains join the server network under per-service aliases (NET-06).
        ServiceRoutingPreparation routing = await serviceRouting.PrepareAsync(service, server, plan.ComposeYaml,
            (network, token) => runtime.EnsureNetworkAsync(ManagedRuntimeTargets.For(server), network,
                new Dictionary<string, string> { ["vessel.managed"] = "true" }, token),
            cancellationToken);
        ManagedServiceWorkspace workspace = await workspaces.PrepareServiceAsync(service.Id, cancellationToken);
        await workspaces.WriteTextAsync(workspace.RootDirectory, "docker-compose.yml", routing.ComposeYaml, false,
            cancellationToken);
        await workspaces.WriteTextAsync(workspace.RootDirectory, ".env", BuildEnvironmentFile(plan.SecretValues),
            true, cancellationToken);
        await RunComposeAsync(server, workspace, plan.ProjectName, ["up", "--detach", "--remove-orphans"],
            cancellationToken);
        service.MarkRunning("docker-compose.yml", timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        if (routing.Routed)
            await serviceRouting.PublishRoutesAsync(service, cancellationToken);
        await auditWriter.RecordAsync(service.TeamId, null, AuditActions.ServiceProvisioned,
            new AuditTarget("service", service.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["template"] = service.TemplateKey }, cancellationToken);
        return ToSummary(service);
    }

    public async Task<BackupScheduleSummary> CreateBackupScheduleAsync(
        UserId actorUserId,
        TeamId teamId,
        CreateBackupScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.ProjectsWrite);
        DatabaseResource database =
            await GetDatabaseForTeamAsync(teamId, new DatabaseResourceId(request.DatabaseId), cancellationToken);
        var schedule = BackupSchedule.Create(teamId, database.Id, new ResourceName(request.Name),
            request.CronExpression, request.RetentionCount, request.StorageKind, timeProvider.GetUtcNow(),
            request.TimeoutSeconds ?? BackupSchedule.DefaultTimeoutSeconds);
        await dbContext.BackupScheduleRepository.AddAsync(schedule, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        recurringJobs.AddOrUpdate<RunBackupJob>(
            $"backup:{schedule.Id.Value:D}",
            job => job.RunAsync(schedule.Id.Value, CancellationToken.None),
            schedule.CronExpression);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.BackupScheduleCreated,
            new AuditTarget("backup-schedule", schedule.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["databaseId"] = database.Id.Value.ToString("D") }, cancellationToken);
        return ToSummary(schedule);
    }

    public async Task<BackupExecutionSummary> QueueBackupAsync(
        UserId actorUserId,
        TeamId teamId,
        DatabaseResourceId databaseId,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.ProjectsWrite);
        DatabaseResource database = await GetDatabaseForTeamAsync(teamId, databaseId, cancellationToken);
        var execution = BackupExecution.Queue(teamId, database.Id, null, BackupStorageKind.ObjectStorage,
            timeProvider.GetUtcNow());
        await dbContext.BackupExecutionRepository.AddAsync(execution, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        var jobId = jobs.Enqueue<RunBackupExecutionJob>(job =>
            job.RunAsync(execution.Id.Value, CancellationToken.None));
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.BackupQueued,
            new AuditTarget("backup", execution.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["jobId"] = jobId }, cancellationToken);
        return ToSummary(execution);
    }

    public async Task<BackupExecutionSummary> RunScheduledBackupAsync(
        BackupScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        BackupSchedule schedule = await dbContext.BackupScheduleRepository.GetByIdAsync(scheduleId, cancellationToken)
                                  ?? throw new InvalidOperationException("Backup schedule was not found.");
        if (!schedule.Enabled) throw new DomainException("Backup schedule is disabled.");
        var execution = BackupExecution.Queue(schedule.TeamId, schedule.DatabaseResourceId, schedule.Id,
            schedule.StorageKind, timeProvider.GetUtcNow());
        schedule.RecordRun(timeProvider.GetUtcNow());
        await dbContext.BackupExecutionRepository.AddAsync(execution, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await RunBackupExecutionAsync(execution.Id, cancellationToken);
    }

    public async Task<BackupExecutionSummary> RunBackupExecutionAsync(
        BackupExecutionId executionId,
        CancellationToken cancellationToken = default)
    {
        BackupExecution execution =
            await dbContext.BackupExecutionRepository.GetByIdAsync(executionId, cancellationToken)
            ?? throw new InvalidOperationException("Backup execution was not found.");
        // Retry-safe: a Hangfire retry of an execution that already ran must not dump again.
        if (execution.Status != BackupExecutionStatus.Queued) return ToSummary(execution);
        DatabaseResource database =
            await dbContext.DatabaseResourceRepository.GetByIdAsync(execution.DatabaseResourceId, cancellationToken)
            ?? throw new InvalidOperationException("Database was not found.");
        TimeSpan timeout = ResolveBackupTimeout(execution);
        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            DatabaseOperationLocks.KeyFor(database.Id), timeout + LockLeaseMargin, TimeSpan.Zero, cancellationToken);
        if (handle is null)
        {
            execution.Fail("Backup skipped: another lifecycle, backup or restore operation is running for this " +
                           "database.", timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return ToSummary(execution);
        }

        execution.Start(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            Server server = dbContext.Servers.Single(item => item.Id == database.ServerId);
            var credentials = await secretVault.RevealForDeploymentAsync(execution.TeamId,
                database.CredentialsReferenceId, deadline.Token);
            await using BackupArtifactFile artifact = await backupProvider.BackupAsync(
                new DatabaseBackupRequest(database, ManagedRuntimeTargets.For(server), credentials, execution.Id),
                deadline.Token);
            await using (Stream content = artifact.OpenRead())
            {
                await objectStorage.PutAsync(new ObjectStoragePutRequest(
                    new ObjectStorageKey(artifact.Bucket, artifact.Key),
                    content,
                    artifact.ContentType,
                    new Dictionary<string, string>
                    {
                        ["vessel.databaseId"] = database.Id.Value.ToString("D"),
                        ["vessel.backupExecutionId"] = execution.Id.Value.ToString("D"),
                        ["vessel.format"] = artifact.Format,
                        ["vessel.sha256"] = artifact.Sha256
                    },
                    artifact.SizeBytes), deadline.Token);
            }

            execution.Succeed(artifact.Bucket, artifact.Key, artifact.SizeBytes, artifact.Sha256,
                timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            await PruneRetentionAsync(execution.TeamId, database.Id, cancellationToken);
            await auditWriter.RecordAsync(execution.TeamId, null, AuditActions.BackupCompleted,
                new AuditTarget("backup", execution.Id.Value.ToString("D")), null,
                new Dictionary<string, object?>
                {
                    ["databaseId"] = database.Id.Value.ToString("D"),
                    ["sizeBytes"] = artifact.SizeBytes,
                    ["format"] = artifact.Format
                },
                cancellationToken);
            await notifications.PublishAsync(execution.TeamId,
                OperationalNotifications.BackupSucceeded(execution.Id.Value, database.Id.Value, database.Name.Value),
                CancellationToken.None);
            return ToSummary(execution);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            // A timed-out run is a recorded failure, not an exception: retrying it immediately would only time
            // out again while holding the database lock.
            execution.Fail($"Backup exceeded its timeout of {timeout.TotalSeconds:0} seconds and was stopped. " +
                           "Increase the schedule timeout for large databases.", timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await notifications.PublishAsync(execution.TeamId,
                OperationalNotifications.BackupFailed(execution.Id.Value, database.Id.Value, database.Name.Value),
                CancellationToken.None);
            return ToSummary(execution);
        }
        catch (Exception ex)
        {
            execution.Fail(redactor.Redact(ex.Message), timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await notifications.PublishAsync(execution.TeamId,
                OperationalNotifications.BackupFailed(execution.Id.Value, database.Id.Value, database.Name.Value),
                CancellationToken.None);
            throw;
        }
    }

    private TimeSpan ResolveBackupTimeout(BackupExecution execution)
    {
        var seconds = execution.ScheduleId is { } scheduleId
            ? dbContext.BackupSchedules.Where(schedule => schedule.Id == scheduleId)
                .Select(schedule => (int?)schedule.TimeoutSeconds).SingleOrDefault()
            : null;
        return TimeSpan.FromSeconds(seconds ?? BackupSchedule.DefaultTimeoutSeconds);
    }

    private async Task StartDatabaseAsync(DatabaseResource database, Server server, CancellationToken cancellationToken)
    {
        database.MarkProvisioning(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        var credentials = await secretVault.RevealForDeploymentAsync(ResolveTeam(database.EnvironmentId),
            database.CredentialsReferenceId, cancellationToken);
        DatabaseProvisioningPlan plan = CreateDatabasePlan(database, credentials);
        ManagedServiceWorkspace workspace = await workspaces.PrepareDatabaseAsync(database.Id, cancellationToken);
        await workspaces.WriteTextAsync(workspace.RootDirectory, "docker-compose.yml", plan.ComposeYaml, false,
            cancellationToken);
        await workspaces.WriteTextAsync(workspace.RootDirectory, ".env", BuildEnvironmentFile(plan.SecretValues),
            true, cancellationToken);
        await RunComposeAsync(server, workspace, plan.ProjectName, ["up", "--detach", "--remove-orphans"],
            cancellationToken);
        database.MarkRunning(plan.ContainerName, "docker-compose.yml", timeProvider.GetUtcNow());
    }

    private async Task ComposeAsync(DatabaseResource database, Server server, IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        ManagedServiceWorkspace workspace = await workspaces.PrepareDatabaseAsync(database.Id, cancellationToken);
        var credentials = await secretVault.RevealForDeploymentAsync(ResolveTeam(database.EnvironmentId),
            database.CredentialsReferenceId, cancellationToken);
        DatabaseProvisioningPlan plan = CreateDatabasePlan(database, credentials);
        await workspaces.WriteTextAsync(workspace.RootDirectory, "docker-compose.yml", plan.ComposeYaml, false,
            cancellationToken);
        await workspaces.WriteTextAsync(workspace.RootDirectory, ".env", BuildEnvironmentFile(plan.SecretValues),
            true, cancellationToken);
        await RunComposeAsync(server, workspace, $"vessel-db-{database.Id.Value:N}"[..39], args, cancellationToken);
    }

    private async Task RunComposeAsync(Server server, ManagedServiceWorkspace workspace, string projectName,
        IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ContainerRuntimeTarget target = ManagedRuntimeTargets.For(server);
        var commandArgs = new List<string>
            { "--project-name", projectName, "--project-directory", workspace.RootDirectory };
        commandArgs.AddRange(args);
        await foreach (ProcessOutputLine _ in runtime.RunComposeAsync(target,
                           new ComposeCommand(workspace.RootDirectory, [workspace.ComposeFilePath], commandArgs,
                               workspace.EnvironmentFilePath, TimeSpan.FromMinutes(20)), cancellationToken))
        {
        }
    }

    private DatabaseProvisioningPlan CreateDatabasePlan(DatabaseResource database, string credentials)
    {
        var service = Slug(database.Name.Value);
        var containerName = ContainerName(database);
        var image = database.Engine switch
        {
            DatabaseEngine.PostgreSql => $"postgres:{database.Version.Value}",
            DatabaseEngine.MySql => $"mysql:{database.Version.Value}",
            DatabaseEngine.MariaDb => $"mariadb:{database.Version.Value}",
            DatabaseEngine.Redis => $"redis:{database.Version.Value}",
            _ => throw new DomainException(
                "Managed database provisioning currently supports PostgreSQL, MySQL, MariaDB, and Redis.")
        };
        var passwordKey = database.Engine == DatabaseEngine.Redis ? "REDIS_PASSWORD" :
            database.Engine == DatabaseEngine.PostgreSql ? "POSTGRES_PASSWORD" : "MYSQL_ROOT_PASSWORD";
        var mountPath = database.Storage.MountPath;
        var builder = new StringBuilder();
        builder.AppendLine("services:");
        builder.AppendLine($"  {service}:");
        builder.AppendLine($"    image: {image}");
        builder.AppendLine($"    container_name: {containerName}");
        builder.AppendLine("    environment:");
        builder.AppendLine($"      {passwordKey}: \"${{{passwordKey}}}\"");
        if (database.Engine == DatabaseEngine.Redis)
            builder.AppendLine("    command: [\"redis-server\", \"--requirepass\", \"${REDIS_PASSWORD}\"]");
        builder.AppendLine("    volumes:");
        builder.AppendLine($"      - {database.Storage.VolumeName}:{mountPath}");
        builder.AppendLine("    labels:");
        builder.AppendLine("      vessel.managed: \"true\"");
        builder.AppendLine($"      vessel.database: \"{database.Id.Value:D}\"");
        builder.AppendLine("    restart: unless-stopped");
        builder.AppendLine("volumes:");
        builder.AppendLine($"  {database.Storage.VolumeName}:");
        return new DatabaseProvisioningPlan($"vessel-db-{database.Id.Value:N}"[..39], service, containerName,
            database.Storage.VolumeName, builder.ToString(),
            new Dictionary<string, string> { [passwordKey] = credentials });
    }

    private static string BuildEnvironmentFile(IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(ServiceTemplateCatalog.EnvironmentKey(key)).Append('=').AppendLine(QuoteEnv(value));
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

    private async Task PruneRetentionAsync(TeamId teamId, DatabaseResourceId databaseId,
        CancellationToken cancellationToken)
    {
        BackupSchedule? schedule = dbContext.BackupSchedules
            .Where(item => item.TeamId == teamId && item.DatabaseResourceId == databaseId && item.Enabled)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
        var retention = schedule?.RetentionCount ?? 10;
        BackupExecution[] stale = dbContext.BackupExecutions
            .Where(item => item.TeamId == teamId
                           && item.DatabaseResourceId == databaseId
                           && item.Status == BackupExecutionStatus.Succeeded
                           && !item.Protected)
            .OrderByDescending(item => item.CreatedAt)
            .Skip(retention)
            .ToArray();
        foreach (BackupExecution execution in stale)
        {
            if (execution.ArtifactBucket is not null && execution.ArtifactKey is not null)
                await objectStorage.DeleteAsync(new ObjectStorageKey(execution.ArtifactBucket, execution.ArtifactKey),
                    cancellationToken);
            execution.MarkPruned(timeProvider.GetUtcNow());
            await auditWriter.RecordAsync(teamId, null, AuditActions.BackupPruned,
                new AuditTarget("backup", execution.Id.Value.ToString("D")), null,
                new Dictionary<string, object?> { ["databaseId"] = databaseId.Value.ToString("D") }, cancellationToken);
        }

        if (stale.Length > 0) await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<DatabaseResource> GetDatabaseForTeamAsync(TeamId teamId, DatabaseResourceId databaseId,
        CancellationToken cancellationToken)
    {
        DatabaseResource database =
            await dbContext.DatabaseResourceRepository.GetByIdAsync(databaseId, cancellationToken)
            ?? throw new InvalidOperationException("Database was not found.");
        if (ResolveTeam(database.EnvironmentId) != teamId)
            throw new UnauthorizedAccessException("Database is outside the active team.");
        return database;
    }

    private TeamId ResolveTeam(EnvironmentId environmentId)
    {
        ProjectId projectId = dbContext.Environments.Where(environment => environment.Id == environmentId)
            .Select(environment => environment.ProjectId).Single();
        return dbContext.Projects.Where(project => project.Id == projectId).Select(project => project.TeamId).Single();
    }

    private void Require(UserId actorUserId, TeamId teamId, string permission)
    {
        if (!authorization.HasPermission(actorUserId, teamId, permission))
            throw new UnauthorizedAccessException($"Missing required permission '{permission}'.");
    }

    private void RequireProject(UserId actorUserId, TeamId teamId, ProjectId projectId, string permission)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Project(projectId), permission);
    }

    private void RequireServer(UserId actorUserId, TeamId teamId, ServerId serverId, string permission)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), permission);
        ServerConnectionType? connectionType = dbContext.Servers
            .Where(server => server.Id == serverId)
            .Select(server => (ServerConnectionType?)server.ConnectionType)
            .SingleOrDefault();
        if (connectionType.HasValue)
            HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, connectionType.Value);
    }

    private void EnsureEnvironmentBelongsToProject(EnvironmentId environmentId, ProjectId projectId)
    {
        if (!dbContext.Environments.Any(environment =>
                environment.Id == environmentId && environment.ProjectId == projectId))
            throw new DomainException("Environment does not belong to the selected project.");
    }

    private static string SnapshotReference(DatabaseResource database)
    {
        return database.ComposeSnapshotReference ?? "docker-compose.yml";
    }

    private static string ContainerName(DatabaseResource database)
    {
        return $"vessel-db-{database.Id.Value:N}"[..50];
    }

    private static string Slug(string value)
    {
        string normalized = new(value.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        normalized = normalized.Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "database" : normalized[..Math.Min(40, normalized.Length)];
    }

    private static BackupScheduleSummary ToSummary(BackupSchedule schedule)
    {
        return new BackupScheduleSummary(schedule.Id.Value, schedule.DatabaseResourceId.Value, schedule.Name.Value,
            schedule.CronExpression, schedule.RetentionCount, schedule.StorageKind, schedule.Enabled,
            schedule.LastRunAt, schedule.TimeoutSeconds);
    }

    internal static BackupExecutionSummary ToSummary(BackupExecution execution)
    {
        return new BackupExecutionSummary(execution.Id.Value, execution.DatabaseResourceId.Value,
            execution.ScheduleId?.Value, execution.Status, execution.StorageKind, execution.ArtifactBucket,
            execution.ArtifactKey, execution.SizeBytes, execution.ChecksumSha256, execution.Protected, execution.CreatedAt,
            execution.FinishedAt, execution.FailureReason, execution.LastRestoreFailedAt,
            execution.LastRestoreFailureReason);
    }

    private static ServiceResourceSummary ToSummary(ServiceResource service)
    {
        return new ServiceResourceSummary(service.Id.Value, service.Name.Value, service.EnvironmentId.Value,
            service.ServerId.Value, service.TemplateKey, service.TemplateVersion, service.State);
    }
}

public sealed class RunDatabaseLifecycleJob(ManagedDatabaseService service)
{
    public Task RunAsync(Guid databaseId, DatabaseLifecycleAction action, CancellationToken cancellationToken = default)
    {
        return service.RunLifecycleActionAsync(new DatabaseResourceId(databaseId), action, cancellationToken);
    }
}

public sealed class ProvisionServiceTemplateJob(ManagedDatabaseService service)
{
    public Task RunAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        return service.ProvisionServiceAsync(new ServiceResourceId(serviceId), cancellationToken);
    }
}

public sealed class RunBackupJob(ManagedDatabaseService service)
{
    public Task RunAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        return service.RunScheduledBackupAsync(new BackupScheduleId(scheduleId), cancellationToken);
    }
}

public sealed class RunBackupExecutionJob(ManagedDatabaseService service)
{
    public Task RunAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        return service.RunBackupExecutionAsync(new BackupExecutionId(executionId), cancellationToken);
    }
}
