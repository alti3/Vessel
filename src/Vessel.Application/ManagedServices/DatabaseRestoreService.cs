using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Realtime;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Storage;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.Domain.Servers;

namespace Vessel.Application.ManagedServices;

/// <summary>
/// Queues and runs database restores. A request only validates and records the restore; the destructive work runs
/// in <see cref="RunDatabaseRestoreJob" /> under the shared database operation lock, with state transitions and an
/// incrementally persisted log on <see cref="DatabaseRestore" />.
/// </summary>
public sealed class DatabaseRestoreService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IBackgroundJobDispatcher jobs,
    IDistributedLockManager locks,
    ISecretVault secretVault,
    ISecretRedactor redactor,
    IDatabaseBackupProvider backupProvider,
    IObjectStorage objectStorage,
    IAuditWriter auditWriter,
    IRealtimeNotifier realtime,
    TimeProvider timeProvider)
{
    public const string ConfirmationPhrase = "RESTORE";

    /// <summary>Upper bound for one restore run; there is no short cut-off for large artifacts.</summary>
    public static readonly TimeSpan RestoreTimeout = TimeSpan.FromHours(6);

    private static readonly TimeSpan LockLeaseMargin = TimeSpan.FromMinutes(10);

    public async Task<DatabaseRestoreSummary> QueueRestoreAsync(
        UserId actorUserId,
        TeamId teamId,
        BackupExecutionId backupExecutionId,
        DatabaseResourceId targetDatabaseId,
        QueueDatabaseRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.ProjectsWrite);
        if (!request.DryRun && !string.Equals(request.Confirmation, ConfirmationPhrase, StringComparison.Ordinal))
            throw new DomainException($"Restore confirmation must be {ConfirmationPhrase}.");

        BackupExecution execution = await GetBackupForTeamAsync(teamId, backupExecutionId, cancellationToken);
        DatabaseResource target = await GetDatabaseForTeamAsync(teamId, targetDatabaseId, cancellationToken);
        DatabaseEngine sourceEngine = ResolveSourceEngine(execution);
        EnsureRestorable(execution, target, sourceEngine, request);

        if (dbContext.DatabaseRestores.Any(restore => restore.TargetDatabaseResourceId == target.Id &&
                                                      (restore.Status == DatabaseRestoreStatus.Queued ||
                                                       restore.Status == DatabaseRestoreStatus.Running)))
            throw new DatabaseOperationConflictException(
                "Another restore is already queued or running for this database.");

        if (!request.DryRun) execution.MarkRestoreValidated(timeProvider.GetUtcNow());
        var restore = DatabaseRestore.Queue(teamId, execution.Id, target.Id, actorUserId, request.DryRun,
            request.ReplaceExisting, request.KeepOwners, timeProvider.GetUtcNow());
        restore.AppendLog(request.DryRun
            ? "Dry run queued: the artifact will be verified and inspected without changing the target."
            : $"Restore queued: data in database '{target.Name.Value}' will be overwritten.", timeProvider.GetUtcNow());
        await dbContext.DatabaseRestoreRepository.AddAsync(restore, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var jobId = jobs.Enqueue<RunDatabaseRestoreJob>(job => job.RunAsync(restore.Id.Value, CancellationToken.None));
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.RestoreQueued,
            new AuditTarget("backup", execution.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["restoreId"] = restore.Id.Value.ToString("D"),
                ["targetDatabaseId"] = target.Id.Value.ToString("D"),
                ["dryRun"] = request.DryRun,
                ["replaceExisting"] = request.ReplaceExisting,
                ["keepOwners"] = request.KeepOwners,
                ["jobId"] = jobId
            },
            cancellationToken);
        return ToSummary(restore);
    }

    public async Task<DatabaseRestoreSummary> GetRestoreAsync(
        UserId actorUserId,
        TeamId teamId,
        DatabaseRestoreId restoreId,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.ProjectsRead);
        DatabaseRestore restore = await dbContext.DatabaseRestoreRepository.GetByIdAsync(restoreId, cancellationToken)
                                  ?? throw new InvalidOperationException("Restore was not found.");
        if (restore.TeamId != teamId) throw new UnauthorizedAccessException("Restore is outside the active team.");
        return ToSummary(restore);
    }

    public async Task<DatabaseRestoreSummary> RunRestoreAsync(
        DatabaseRestoreId restoreId,
        CancellationToken cancellationToken = default)
    {
        DatabaseRestore restore = await dbContext.DatabaseRestoreRepository.GetByIdAsync(restoreId, cancellationToken)
                                  ?? throw new InvalidOperationException("Restore was not found.");
        // Retry-safe: a destructive restore never runs twice for the same request.
        if (restore.Status != DatabaseRestoreStatus.Queued) return ToSummary(restore);

        BackupExecution execution =
            await dbContext.BackupExecutionRepository.GetByIdAsync(restore.BackupExecutionId, cancellationToken)
            ?? throw new InvalidOperationException("Backup execution was not found.");
        DatabaseResource target =
            await dbContext.DatabaseResourceRepository.GetByIdAsync(restore.TargetDatabaseResourceId,
                cancellationToken)
            ?? throw new InvalidOperationException("Database was not found.");

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            DatabaseOperationLocks.KeyFor(target.Id), RestoreTimeout + LockLeaseMargin, TimeSpan.Zero,
            cancellationToken);
        if (handle is null)
        {
            await FailAsync(restore, execution,
                "Another lifecycle, backup or restore operation is running for this database. Nothing was changed.",
                cancellationToken);
            return ToSummary(restore);
        }

        restore.Start(timeProvider.GetUtcNow());
        restore.AppendLog(restore.DryRun ? "Dry run started." : "Restore started.", timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await PublishAsync(restore, cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RestoreTimeout);
        string? credentials = null;
        try
        {
            DatabaseEngine sourceEngine = ResolveSourceEngine(execution);
            EnsureRestorable(execution, target, sourceEngine,
                new QueueDatabaseRestoreRequest(restore.DryRun, ConfirmationPhrase, restore.ReplaceExisting,
                    restore.KeepOwners));
            Server server = dbContext.Servers.Single(item => item.Id == target.ServerId);
            credentials = await secretVault.RevealForDeploymentAsync(restore.TeamId, target.CredentialsReferenceId,
                deadline.Token);
            var secrets = new RedactionContext([credentials]);
            await using Stream artifact = await objectStorage.OpenReadAsync(
                new ObjectStorageKey(execution.ArtifactBucket!, execution.ArtifactKey!), deadline.Token);
            DatabaseRestoreOutcome outcome = await backupProvider.RestoreAsync(new DatabaseRestoreRequest(
                target,
                ManagedRuntimeTargets.For(server),
                credentials,
                artifact,
                execution.ChecksumSha256,
                sourceEngine,
                new DatabaseRestoreOptions(restore.DryRun, restore.ReplaceExisting, restore.KeepOwners),
                async (line, token) =>
                {
                    restore.AppendLog(redactor.Redact(line, secrets), timeProvider.GetUtcNow());
                    await dbContext.SaveChangesAsync(token);
                    await PublishAsync(restore, token);
                }), deadline.Token);

            restore.RecordDetectedFormat(outcome.Format, timeProvider.GetUtcNow());
            restore.AppendLog(redactor.Redact(outcome.Summary, secrets), timeProvider.GetUtcNow());
            restore.Succeed(timeProvider.GetUtcNow());
            if (!restore.DryRun)
            {
                execution.MarkRestoreValidated(timeProvider.GetUtcNow());
                execution.MarkRestoreSucceeded(timeProvider.GetUtcNow());
            }

            await dbContext.SaveChangesAsync(CancellationToken.None);
            await auditWriter.RecordAsync(restore.TeamId, restore.RequestedByUserId, AuditActions.RestoreCompleted,
                new AuditTarget("backup", execution.Id.Value.ToString("D")), null,
                new Dictionary<string, object?>
                {
                    ["restoreId"] = restore.Id.Value.ToString("D"),
                    ["targetDatabaseId"] = target.Id.Value.ToString("D"),
                    ["dryRun"] = restore.DryRun,
                    ["format"] = outcome.Format
                },
                CancellationToken.None);
            await PublishAsync(restore, CancellationToken.None);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            await FailAsync(restore, execution,
                $"Restore exceeded its timeout of {RestoreTimeout.TotalHours:0} hours and was stopped.",
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = credentials is null
                ? redactor.Redact(ex.Message)
                : redactor.Redact(ex.Message, new RedactionContext([credentials]));
            await FailAsync(restore, execution, reason, CancellationToken.None);
        }

        return ToSummary(restore);
    }

    private void EnsureRestorable(BackupExecution execution, DatabaseResource target, DatabaseEngine sourceEngine,
        QueueDatabaseRestoreRequest request)
    {
        if (!execution.HasRestorableArtifact)
            throw new DomainException("Only successful backup artifacts can be restored.");
        if (target.LifecycleState is DatabaseLifecycleState.Deleting or DatabaseLifecycleState.Deleted)
            throw new DomainException("Deleted databases cannot be restore targets.");
        if (target.LifecycleState != DatabaseLifecycleState.Running || string.IsNullOrWhiteSpace(target.ContainerName))
            throw new DomainException("The target database must be running before it can be restored.");
        if (sourceEngine != target.Engine)
            throw new DomainException(
                $"A {sourceEngine} backup cannot be restored into a {target.Engine} database.");
        if ((request.ReplaceExisting || request.KeepOwners) && target.Engine != DatabaseEngine.PostgreSql)
            throw new DomainException("Replace-existing and keep-owners options apply to PostgreSQL restores only.");
    }

    private DatabaseEngine ResolveSourceEngine(BackupExecution execution)
    {
        return dbContext.DatabaseResources.Where(database => database.Id == execution.DatabaseResourceId)
                   .Select(database => (DatabaseEngine?)database.Engine).SingleOrDefault()
               ?? throw new DomainException("The source database of this backup no longer exists.");
    }

    private async Task FailAsync(DatabaseRestore restore, BackupExecution execution, string reason,
        CancellationToken cancellationToken)
    {
        restore.AppendLog($"Restore failed: {reason}", timeProvider.GetUtcNow());
        restore.Fail(reason, timeProvider.GetUtcNow());
        if (!restore.DryRun && execution.HasRestorableArtifact)
            execution.MarkRestoreFailed(reason.Length > 1000 ? reason[..1000] : reason, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(restore.TeamId, restore.RequestedByUserId, AuditActions.RestoreFailed,
            new AuditTarget("backup", execution.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["restoreId"] = restore.Id.Value.ToString("D"),
                ["targetDatabaseId"] = restore.TargetDatabaseResourceId.Value.ToString("D"),
                ["dryRun"] = restore.DryRun
            },
            cancellationToken);
        await PublishAsync(restore, cancellationToken);
    }

    private async Task PublishAsync(DatabaseRestore restore, CancellationToken cancellationToken)
    {
        try
        {
            await realtime.PublishAsync(
                new RealtimeGroup(RealtimeGroupKind.Team, restore.TeamId.Value.ToString("D")),
                new RealtimeMessage("database.restore", new
                {
                    RestoreId = restore.Id.Value,
                    TargetDatabaseId = restore.TargetDatabaseResourceId.Value,
                    Status = restore.Status.ToString(),
                    restore.DryRun,
                    restore.FailureReason
                }),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Realtime delivery is best effort; the restore record is the source of truth.
        }
    }

    private async Task<DatabaseResource> GetDatabaseForTeamAsync(TeamId teamId, DatabaseResourceId databaseId,
        CancellationToken cancellationToken)
    {
        DatabaseResource database =
            await dbContext.DatabaseResourceRepository.GetByIdAsync(databaseId, cancellationToken)
            ?? throw new InvalidOperationException("Database was not found.");
        ProjectId projectId = dbContext.Environments.Where(environment => environment.Id == database.EnvironmentId)
            .Select(environment => environment.ProjectId).Single();
        TeamId owner = dbContext.Projects.Where(project => project.Id == projectId)
            .Select(project => project.TeamId).Single();
        if (owner != teamId) throw new UnauthorizedAccessException("Database is outside the active team.");
        return database;
    }

    private async Task<BackupExecution> GetBackupForTeamAsync(TeamId teamId, BackupExecutionId executionId,
        CancellationToken cancellationToken)
    {
        BackupExecution execution =
            await dbContext.BackupExecutionRepository.GetByIdAsync(executionId, cancellationToken)
            ?? throw new InvalidOperationException("Backup execution was not found.");
        if (execution.TeamId != teamId)
            throw new UnauthorizedAccessException("Backup is outside the active team.");
        return execution;
    }

    private void Require(UserId actorUserId, TeamId teamId, string permission)
    {
        if (!authorization.HasPermission(actorUserId, teamId, permission))
            throw new UnauthorizedAccessException($"Missing required permission '{permission}'.");
    }

    internal static DatabaseRestoreSummary ToSummary(DatabaseRestore restore)
    {
        return new DatabaseRestoreSummary(restore.Id.Value, restore.BackupExecutionId.Value,
            restore.TargetDatabaseResourceId.Value, restore.Status, restore.DryRun, restore.ReplaceExisting,
            restore.KeepOwners, restore.DetectedFormat, restore.FailureReason, restore.CreatedAt, restore.StartedAt,
            restore.FinishedAt, restore.Log);
    }
}

public sealed class RunDatabaseRestoreJob(DatabaseRestoreService service)
{
    public Task RunAsync(Guid restoreId, CancellationToken cancellationToken = default)
    {
        return service.RunRestoreAsync(new DatabaseRestoreId(restoreId), cancellationToken);
    }
}
