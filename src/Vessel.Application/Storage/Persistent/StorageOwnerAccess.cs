using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Storage;
using AppEntity = Vessel.Domain.Applications.Application;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Storage.Persistent;

/// <summary>An application or service that owns persistent storage, with the facts storage rules need.</summary>
public sealed record StorageOwnerContext(
    StorageOwnerKind Kind,
    Guid Id,
    TeamId TeamId,
    ServerId ServerId,
    string Name,
    EnvironmentId EnvironmentId,
    bool IsCompose);

/// <summary>Audit action names of persistent storage, volume backups and database imports.</summary>
public static class StorageAuditActions
{
    public const string StorageCreated = "storage.created";
    public const string StorageUpdated = "storage.updated";
    public const string StorageDeleted = "storage.deleted";
    public const string StorageContentRevealed = "storage.content_revealed";
    public const string VolumeBackupScheduleCreated = "volume_backup.schedule_created";
    public const string VolumeBackupScheduleUpdated = "volume_backup.schedule_updated";
    public const string VolumeBackupScheduleDeleted = "volume_backup.schedule_deleted";
    public const string VolumeBackupQueued = "volume_backup.queued";
    public const string VolumeBackupCompleted = "volume_backup.completed";
    public const string VolumeBackupFailed = "volume_backup.failed";
    public const string VolumeBackupDeleted = "volume_backup.deleted";
    public const string VolumeBackupPruned = "volume_backup.pruned";
    public const string VolumeBackupRecovered = "volume_backup.recovered";
    public const string VolumeBackupRecoveryFailed = "volume_backup.recovery_failed";
    public const string VolumeRestoreQueued = "volume_backup.restore_queued";
    public const string VolumeRestoreCompleted = "volume_backup.restore_completed";
    public const string VolumeRestoreFailed = "volume_backup.restore_failed";
    public const string DatabaseImportUploadStarted = "database_import.upload_started";
    public const string DatabaseImportQueued = "database_import.queued";
}

/// <summary>
///     Resolves and authorizes storage owners. Applications use application permissions on the application; services
///     use project permissions on their environment (as service domains do). Missing owners and owners of another team
///     fail the same way, so IDs cannot be probed.
/// </summary>
public sealed class StorageOwnerAccess(IVesselDbContext dbContext, VesselAuthorizationService authorization)
{
    public StorageOwnerContext Authorize(UserId actorUserId, TeamId teamId, StorageOwnerKind kind, Guid ownerId,
        bool write)
    {
        StorageOwnerContext? owner = Load(kind, ownerId);
        if (owner is null || owner.TeamId != teamId)
            throw new UnauthorizedAccessException($"{kind} is outside the active team.");
        if (kind == StorageOwnerKind.Application)
            authorization.Demand(actorUserId, teamId, ResourceRef.Application(new ApplicationId(ownerId)),
                write ? VesselPermissions.ApplicationsWrite : VesselPermissions.ApplicationsRead);
        else
            authorization.Demand(actorUserId, teamId, ResourceRef.Environment(owner.EnvironmentId),
                write ? VesselPermissions.ProjectsWrite : VesselPermissions.ProjectsRead);
        return owner;
    }

    /// <summary>Loads an owner without authorization (background jobs).</summary>
    public StorageOwnerContext? Load(StorageOwnerKind kind, Guid ownerId)
    {
        if (kind == StorageOwnerKind.Application)
        {
            var applicationId = new ApplicationId(ownerId);
            AppEntity? application = dbContext.Applications.SingleOrDefault(item => item.Id == applicationId);
            if (application is null) return null;
            ProjectId? projectId = dbContext.Environments
                .Where(environment => environment.Id == application.EnvironmentId)
                .Select(environment => (ProjectId?)environment.ProjectId)
                .SingleOrDefault();
            TeamId? team = projectId is null
                ? null
                : dbContext.Projects.Where(project => project.Id == projectId.Value)
                    .Select(project => (TeamId?)project.TeamId).SingleOrDefault();
            if (team is null) return null;
            return new StorageOwnerContext(kind, ownerId, team.Value, application.ServerId, application.Name.Value,
                application.EnvironmentId,
                application.BuildConfiguration.BuildPack == ApplicationBuildPack.DockerCompose);
        }

        var serviceId = new ServiceResourceId(ownerId);
        return dbContext.ServiceResources.Where(service => service.Id == serviceId).AsEnumerable()
            .Select(service => new StorageOwnerContext(kind, ownerId, service.TeamId, service.ServerId,
                service.Name.Value, service.EnvironmentId, true))
            .SingleOrDefault();
    }
}
