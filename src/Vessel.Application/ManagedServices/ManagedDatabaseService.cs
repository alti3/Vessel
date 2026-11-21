using Vessel.Application.Deployments;
using Vessel.Application.Servers;
using Vessel.Application.Storage.Persistent;
using Vessel.Domain.Storage;
using System.Text;
using System.Text.Json;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Backups;
using Vessel.Application.Docker;
using Vessel.Application.Jobs;
using Vessel.Application.ManagedServices.Templates;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Processes;
using Vessel.Application.Proxy.Domains;
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
    IDistributedLockManager locks,
    ISecretVault secretVault,
    ServiceTemplateCatalog templates,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    ServiceRoutingCoordinator serviceRouting,
    DatabaseHealthProbe healthProbe,
    ResourceStorageProvisioner? storage = null,
    IComposeDocumentBuilder? composeDocuments = null)
{
    private ServiceTemplateRuntimeResolver? _templateRuntime;

    private ServiceTemplateRuntimeResolver TemplateRuntime =>
        _templateRuntime ??= new ServiceTemplateRuntimeResolver(dbContext, secretVault, timeProvider);

    public IReadOnlyList<ServiceTemplateSummary> ListTemplates()
    {
        return templates.List();
    }

    public ServiceTemplateSummary GetTemplate(string key)
    {
        return templates.Describe(key);
    }

    public async Task<DatabaseLifecycleResult> QueueLifecycleActionAsync(
        UserId actorUserId,
        TeamId teamId,
        DatabaseResourceId databaseId,
        DatabaseLifecycleAction action,
        CancellationToken cancellationToken = default)
    {
        // Start/stop/restart are deploy-level actions (IAM-04) and honour resource-scoped grants (IAM-05).
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(databaseId),
            VesselPermissions.ResourcesLifecycle);
        DatabaseResource database = await GetDatabaseForTeamAsync(teamId, databaseId, cancellationToken);
        if (action != DatabaseLifecycleAction.Inspect)
            await EnsureNoOperationInProgressAsync(database, cancellationToken);
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
        if (action == DatabaseLifecycleAction.Inspect)
        {
            // Read-only: observes the container health without taking the operation lock.
            database.RecordHealthProbe(await healthProbe.ProbeAsync(database, server, cancellationToken),
                timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            return new DatabaseLifecycleResult(database.Id.Value, database.LifecycleState, database.HealthState,
                "Database health inspected.");
        }

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            DatabaseOperationLocks.KeyFor(database.Id), TimeSpan.FromHours(1), TimeSpan.Zero, cancellationToken);
        if (handle is null)
        {
            // Another operation owns the database: skip without touching its state (it is not a failure).
            await auditWriter.RecordAsync(teamId, null, AuditActions.DatabaseLifecycleActionSkipped,
                new AuditTarget("database", database.Id.Value.ToString("D")), null,
                new Dictionary<string, object?> { ["action"] = action.ToString() }, cancellationToken);
            return new DatabaseLifecycleResult(database.Id.Value, database.LifecycleState, database.HealthState,
                $"Database {action} skipped: another database operation is running.");
        }

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
                    database.MarkRunning(DatabaseComposeBuilder.ContainerName(database), SnapshotReference(database),
                        timeProvider.GetUtcNow());
                    break;
                case DatabaseLifecycleAction.Delete:
                    await ComposeAsync(database, server, ["down", "--volumes", "--remove-orphans"], cancellationToken);
                    database.MarkDeleted(timeProvider.GetUtcNow());
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
        IReadOnlyDictionary<string, string> requestInputs =
            request.Inputs ?? new Dictionary<string, string>(StringComparer.Ordinal);
        // TPL-01: imported templates accept only their declared inputs; secret inputs go to the vault.
        templates.ValidateInputs(template.Key, requestInputs);
        (Dictionary<string, string> plainInputs, Dictionary<string, string> secretInputs) =
            templates.SplitInputs(template.Key, requestInputs);

        var service = ServiceResource.Create(teamId, environmentId, serverId, new ResourceName(request.Name),
            template.Key, template.Version, JsonSerializer.Serialize(plainInputs), timeProvider.GetUtcNow());
        await dbContext.ServiceResourceRepository.AddAsync(service, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (secretInputs.Count > 0)
            await TemplateRuntime.StoreSecretInputsAsync(service, secretInputs, cancellationToken);
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
        ServiceTemplateDefinition template = templates.Get(service.TemplateKey);
        // TPL-01: magic variables of imported templates are generated once per instance (vault) and routed through
        // the service's domains.
        ServiceTemplateRuntimeValues? runtimeValues = template.Coolify is null
            ? null
            : await TemplateRuntime.ResolveAsync(service, template.Coolify, cancellationToken);
        ServiceProvisioningPlan plan = templates.CreatePlan(service.TemplateKey, service.Id.Value, service.Name.Value,
            inputs, runtimeValues);
        service.MarkProvisioning(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);

        // Compose services with domains join the server network under per-service aliases (NET-06).
        ServiceRoutingPreparation routing = await serviceRouting.PrepareAsync(service, server, plan.ComposeYaml,
            (network, token) => runtime.EnsureNetworkAsync(ManagedRuntimeTargets.For(server), network,
                new Dictionary<string, string> { ["vessel.managed"] = "true" }, token),
            cancellationToken);
        var composeYaml = routing.ComposeYaml;
        // DATA-12: service storage (named volumes, resource-directory files) is mounted and prepared before up.
        if (storage is not null && composeDocuments is not null &&
            storage.HasStorage(StorageOwnerKind.Service, service.Id.Value))
        {
            ServerExecutionTarget storageTarget = ServerExecutionTarget.From(server);
            composeYaml = composeDocuments.AddStorageMounts(composeYaml,
                storage.BuildSpec(StorageOwnerKind.Service, service.Id.Value, storageTarget, null, null));
            await storage.EnsureOnServerAsync(StorageOwnerKind.Service, service.Id.Value, storageTarget, null,
                (_, _, _) => Task.CompletedTask, cancellationToken);
        }

        ManagedServiceWorkspace workspace = await workspaces.PrepareServiceAsync(service.Id, cancellationToken);
        await workspaces.WriteTextAsync(workspace.RootDirectory, "docker-compose.yml", composeYaml, false,
            cancellationToken);
        await workspaces.WriteTextAsync(workspace.RootDirectory, ".env",
            ComposeEnvironmentFile.Format(plan.SecretValues), true, cancellationToken);
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

    private async Task StartDatabaseAsync(DatabaseResource database, Server server, CancellationToken cancellationToken)
    {
        database.MarkProvisioning(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        var credentials = await secretVault.RevealForDeploymentAsync(ResolveTeam(database.EnvironmentId),
            database.CredentialsReferenceId, cancellationToken);
        DatabaseProvisioningPlan plan = DatabaseComposeBuilder.CreatePlan(database, credentials);
        ManagedServiceWorkspace workspace = await workspaces.PrepareDatabaseAsync(database.Id, cancellationToken);
        await workspaces.WriteTextAsync(workspace.RootDirectory, "docker-compose.yml", plan.ComposeYaml, false,
            cancellationToken);
        await workspaces.WriteTextAsync(workspace.RootDirectory, ".env",
            ComposeEnvironmentFile.Format(plan.SecretValues), true, cancellationToken);
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
        DatabaseProvisioningPlan plan = DatabaseComposeBuilder.CreatePlan(database, credentials);
        await workspaces.WriteTextAsync(workspace.RootDirectory, "docker-compose.yml", plan.ComposeYaml, false,
            cancellationToken);
        await workspaces.WriteTextAsync(workspace.RootDirectory, ".env",
            ComposeEnvironmentFile.Format(plan.SecretValues), true, cancellationToken);
        await RunComposeAsync(server, workspace, plan.ProjectName, args, cancellationToken);
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

    /// <summary>
    ///     Request-time exclusion (DATA-03): a lifecycle action is refused with a conflict while another lifecycle
    ///     action, backup or restore holds the database, so two parallel requests cannot both enqueue destructive work.
    /// </summary>
    private async Task EnsureNoOperationInProgressAsync(DatabaseResource database,
        CancellationToken cancellationToken)
    {
        if (dbContext.DatabaseRestores.Any(restore => restore.TargetDatabaseResourceId == database.Id &&
                                                      (restore.Status == DatabaseRestoreStatus.Queued ||
                                                       restore.Status == DatabaseRestoreStatus.Running)))
            throw new DatabaseOperationConflictException(
                "A restore is queued or running for this database; try again after it finishes.");
        if (dbContext.BackupExecutions.Any(execution => execution.DatabaseResourceId == database.Id &&
                                                        execution.Status == BackupExecutionStatus.Running))
            throw new DatabaseOperationConflictException(
                "A backup is running for this database; try again after it finishes.");
        await using DistributedLockHandle? probe = await locks.TryAcquireAsync(
            DatabaseOperationLocks.KeyFor(database.Id), TimeSpan.FromSeconds(30), TimeSpan.Zero, cancellationToken);
        if (probe is null)
            throw new DatabaseOperationConflictException(
                "Another lifecycle, backup or restore operation is running for this database.");
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

/// <summary>Hangfire entry point for a schedule's recurring backup (type name kept for queued jobs).</summary>
public sealed class RunBackupJob(DatabaseBackupService service)
{
    public Task RunAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        return service.RunScheduledBackupAsync(new BackupScheduleId(scheduleId), cancellationToken);
    }
}

/// <summary>Hangfire entry point for one queued backup execution (type name kept for queued jobs).</summary>
public sealed class RunBackupExecutionJob(DatabaseBackupService service)
{
    public Task RunAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        return service.RunBackupExecutionAsync(new BackupExecutionId(executionId), cancellationToken);
    }
}
