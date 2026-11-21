using System.Text;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Storage;

namespace Vessel.Application.Storage.Persistent;

/// <summary>
///     Persistent storage of applications and services (DATA-12): named volumes, content files, host files and
///     directories, all inside the resource's owned directory. Arbitrary host paths do not exist (upstream v4.3.22).
///     Content is encrypted at rest and only revealed to callers with <c>secrets.read</c>; it is written to the server
///     by the next deployment, never by the API request itself.
/// </summary>
public sealed class ResourceStorageService(
    IVesselDbContext dbContext,
    StorageOwnerAccess owners,
    VesselAuthorizationService authorization,
    ISecretCipher cipher,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public const int MaxStoragesPerOwner = 50;

    private static readonly Dictionary<Guid, Guid> NoSchedules = [];

    public static string ContentPurpose(FileMountId id)
    {
        return $"file-mount:{id.Value:D}";
    }

    public IReadOnlyList<ResourceStorageSummary> List(UserId actorUserId, TeamId teamId, StorageOwnerKind kind,
        Guid ownerId)
    {
        StorageOwnerContext owner = owners.Authorize(actorUserId, teamId, kind, ownerId, false);
        Dictionary<Guid, Guid> schedules = SchedulesByTarget(owner);
        IEnumerable<ResourceStorageSummary> volumes = Volumes(owner).AsEnumerable()
            .Select(volume => ToSummary(volume, schedules));
        IEnumerable<ResourceStorageSummary> mounts = Mounts(owner).AsEnumerable()
            .Select(mount => ToSummary(mount, schedules));
        return volumes.Concat(mounts).OrderBy(item => item.CreatedAt).ToArray();
    }

    /// <summary>Returns one entry; <paramref name="includeContent" /> additionally requires <c>secrets.read</c>.</summary>
    public async Task<ResourceStorageDetail> GetAsync(UserId actorUserId, TeamId teamId, StorageOwnerKind kind,
        Guid ownerId, Guid storageId, bool includeContent, CancellationToken cancellationToken = default)
    {
        StorageOwnerContext owner = owners.Authorize(actorUserId, teamId, kind, ownerId, false);
        Dictionary<Guid, Guid> schedules = SchedulesByTarget(owner);
        if (FindVolume(owner, storageId) is { } volume)
            return new ResourceStorageDetail(ToSummary(volume, schedules), null);
        FileMount mount = FindMount(owner, storageId) ?? throw new InvalidOperationException("Storage was not found.");
        string? content = null;
        if (includeContent && mount.ProtectedContent is not null)
        {
            if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.SecretsRead))
                throw new UnauthorizedAccessException(
                    $"Reading file content requires the '{VesselPermissions.SecretsRead}' permission.");
            content = cipher.Unprotect(mount.ProtectedContent, ContentPurpose(mount.Id));
            await auditWriter.RecordAsync(teamId, actorUserId, StorageAuditActions.StorageContentRevealed,
                Target(mount.Id.Value), null, OwnerMetadata(owner), cancellationToken);
        }

        return new ResourceStorageDetail(ToSummary(mount, schedules), content);
    }

    public async Task<ResourceStorageSummary> CreateAsync(UserId actorUserId, TeamId teamId, StorageOwnerKind kind,
        Guid ownerId, CreateResourceStorageRequest request, CancellationToken cancellationToken = default)
    {
        StorageOwnerContext owner = owners.Authorize(actorUserId, teamId, kind, ownerId, true);
        if (Volumes(owner).Count() + Mounts(owner).Count() >= MaxStoragesPerOwner)
            throw new DomainException($"A resource can have at most {MaxStoragesPerOwner} storage entries.");
        var serviceName = ValidateService(owner, request.ServiceName);
        DateTimeOffset now = timeProvider.GetUtcNow();
        ResourceStorageSummary summary;
        switch (request.Type)
        {
            case ResourceStorageType.Volume:
                {
                    if (request.Path is not null || request.Content is not null || request.IsHostFile)
                        throw new DomainException("Volumes take a name and a mount path only.");
                    if (string.IsNullOrWhiteSpace(request.Name)) throw new DomainException("Volume name is required.");
                    var volume = PersistentVolume.Create(owner.TeamId, owner.Kind, owner.Id, request.Name,
                        request.MountPath, serviceName, now);
                    EnsureUniqueVolumeName(owner, volume.Name, null);
                    EnsureMountPathFree(owner, volume.ServiceName, volume.MountPath, null);
                    await dbContext.PersistentVolumeRepository.AddAsync(volume, cancellationToken);
                    summary = ToSummary(volume, NoSchedules);
                    break;
                }
            case ResourceStorageType.File or ResourceStorageType.Directory:
                {
                    if (request.Name is not null)
                        throw new DomainException("Files and directories take a path, not a name.");
                    if (string.IsNullOrWhiteSpace(request.Path)) throw new DomainException("Path is required.");
                    FileMount mount;
                    if (request.Type == ResourceStorageType.Directory)
                    {
                        if (request.Content is not null || request.IsHostFile)
                            throw new DomainException("Directory mounts have no content.");
                        mount = FileMount.CreateDirectory(owner.TeamId, owner.Kind, owner.Id, request.Path,
                            request.MountPath, serviceName, now);
                    }
                    else if (request.IsHostFile)
                    {
                        if (request.Content is not null)
                            throw new DomainException(
                                "Host files are managed on the server; Vessel does not store their content.");
                        mount = FileMount.CreateHostFile(owner.TeamId, owner.Kind, owner.Id, request.Path,
                            request.MountPath, serviceName, now);
                    }
                    else
                    {
                        mount = FileMount.CreateFile(owner.TeamId, owner.Kind, owner.Id, request.Path,
                            request.MountPath, serviceName, null, 0, now);
                        SetContent(mount, request.Content, now);
                    }

                    EnsurePathFree(owner, mount.Path, null);
                    EnsureMountPathFree(owner, mount.ServiceName, mount.MountPath, null);
                    await dbContext.FileMountRepository.AddAsync(mount, cancellationToken);
                    summary = ToSummary(mount, NoSchedules);
                    break;
                }
            default:
                throw new DomainException("Unknown storage type.");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(owner.TeamId, actorUserId, StorageAuditActions.StorageCreated,
            Target(summary.Id), null, OwnerMetadata(owner, summary.Type), cancellationToken);
        return summary;
    }

    public async Task<ResourceStorageSummary> UpdateAsync(UserId actorUserId, TeamId teamId, StorageOwnerKind kind,
        Guid ownerId, Guid storageId, UpdateResourceStorageRequest request,
        CancellationToken cancellationToken = default)
    {
        StorageOwnerContext owner = owners.Authorize(actorUserId, teamId, kind, ownerId, true);
        DateTimeOffset now = timeProvider.GetUtcNow();
        Dictionary<Guid, Guid> schedules = SchedulesByTarget(owner);
        ResourceStorageSummary summary;
        if (FindVolume(owner, storageId) is { } volume)
        {
            if (request.Path is not null || request.Content is not null || request.ClearContent)
                throw new DomainException("Volumes take a name and a mount path only.");
            var serviceName = request.ServiceName is null ? volume.ServiceName : ValidateService(owner, request.ServiceName);
            var name = request.Name ?? volume.Name;
            var mountPath = request.MountPath ?? volume.MountPath;
            if (!string.Equals(StoragePaths.ValidateVolumeName(name), volume.Name, StringComparison.Ordinal))
            {
                EnsureUniqueVolumeName(owner, StoragePaths.ValidateVolumeName(name), volume.Id.Value);
                if (schedules.ContainsKey(volume.Id.Value))
                    throw new DomainException(
                        "This volume has a backup schedule; delete the schedule before renaming the volume.");
            }

            EnsureMountPathFree(owner, StoragePaths.ValidateServiceName(serviceName),
                StoragePaths.ValidateMountPath(mountPath), volume.Id.Value);
            volume.Update(name, mountPath, serviceName, request.ConfirmRename, now);
            summary = ToSummary(volume, schedules);
        }
        else
        {
            FileMount mount = FindMount(owner, storageId) ??
                              throw new InvalidOperationException("Storage was not found.");
            if (request.Name is not null) throw new DomainException("Files and directories take a path, not a name.");
            var serviceName = request.ServiceName is null ? mount.ServiceName : ValidateService(owner, request.ServiceName);
            var path = request.Path ?? mount.Path;
            var mountPath = request.MountPath ?? mount.MountPath;
            var validatedPath = StoragePaths.ValidateRelativePath(path);
            if (!string.Equals(validatedPath, mount.Path, StringComparison.Ordinal))
            {
                EnsurePathFree(owner, validatedPath, mount.Id.Value);
                if (schedules.ContainsKey(mount.Id.Value))
                    throw new DomainException(
                        "This directory has a backup schedule; delete the schedule before moving the directory.");
            }

            EnsureMountPathFree(owner, StoragePaths.ValidateServiceName(serviceName),
                StoragePaths.ValidateMountPath(mountPath), mount.Id.Value);
            mount.Relocate(path, mountPath, serviceName, now);
            if (request.ClearContent) mount.ReplaceContent(null, 0, now);
            else if (request.Content is not null) SetContent(mount, request.Content, now);
            summary = ToSummary(mount, schedules);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(owner.TeamId, actorUserId, StorageAuditActions.StorageUpdated,
            Target(summary.Id), null, OwnerMetadata(owner, summary.Type), cancellationToken);
        return summary;
    }

    /// <summary>
    ///     Deletes a storage entry. The data on the server (volume or files) is kept: removing it is a separate,
    ///     deliberate action. Entries with a backup schedule cannot be deleted (upstream behavior).
    /// </summary>
    public async Task DeleteAsync(UserId actorUserId, TeamId teamId, StorageOwnerKind kind, Guid ownerId,
        Guid storageId, CancellationToken cancellationToken = default)
    {
        StorageOwnerContext owner = owners.Authorize(actorUserId, teamId, kind, ownerId, true);
        if (dbContext.ScheduledVolumeBackups.Any(schedule => schedule.TargetId == storageId))
            throw new DomainException("Delete this storage's backup schedule and its archives before deleting it.");
        ResourceStorageType type;
        if (FindVolume(owner, storageId) is { } volume)
        {
            dbContext.PersistentVolumeRepository.Remove(volume);
            type = ResourceStorageType.Volume;
        }
        else
        {
            FileMount mount = FindMount(owner, storageId) ??
                              throw new InvalidOperationException("Storage was not found.");
            dbContext.FileMountRepository.Remove(mount);
            type = mount.IsDirectory ? ResourceStorageType.Directory : ResourceStorageType.File;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(owner.TeamId, actorUserId, StorageAuditActions.StorageDeleted,
            Target(storageId), null, OwnerMetadata(owner, type), cancellationToken);
    }

    private void SetContent(FileMount mount, string? content, DateTimeOffset now)
    {
        if (content is null)
        {
            mount.ReplaceContent(null, 0, now);
            return;
        }

        var length = Encoding.UTF8.GetByteCount(content);
        if (length > FileMount.MaxContentBytes)
            throw new DomainException($"File content must be at most {FileMount.MaxContentBytes / (1024 * 1024)} MiB.");
        if (content.Contains('\0'))
            throw new DomainException("File content must be text; binary files are not supported.");
        mount.ReplaceContent(cipher.Protect(content, ContentPurpose(mount.Id)), length, now);
    }

    private static string? ValidateService(StorageOwnerContext owner, string? serviceName)
    {
        var validated = StoragePaths.ValidateServiceName(serviceName);
        if (validated is not null && !owner.IsCompose)
            throw new DomainException(
                "Dockerfile applications have a single service; omit the service name.");
        return validated;
    }

    private void EnsureUniqueVolumeName(StorageOwnerContext owner, string name, Guid? exceptId)
    {
        if (Volumes(owner).AsEnumerable().Any(volume => volume.Id.Value != exceptId &&
                                                        string.Equals(volume.Name, name, StringComparison.Ordinal)))
            throw new DomainException($"A volume named '{name}' already exists.");
    }

    private void EnsureMountPathFree(StorageOwnerContext owner, string? serviceName, string mountPath, Guid? exceptId)
    {
        var used = Volumes(owner).AsEnumerable()
            .Where(volume => volume.Id.Value != exceptId)
            .Select(volume => (volume.ServiceName, volume.MountPath))
            .Concat(Mounts(owner).AsEnumerable().Where(mount => mount.Id.Value != exceptId)
                .Select(mount => (mount.ServiceName, mount.MountPath)));
        if (used.Any(item => string.Equals(item.ServiceName, serviceName, StringComparison.Ordinal) &&
                             string.Equals(item.MountPath, mountPath, StringComparison.Ordinal)))
            throw new DomainException($"Another storage entry is already mounted at {mountPath}.");
    }

    /// <summary>Paths in the resource directory must not be equal or nested (a file inside a mounted directory).</summary>
    private void EnsurePathFree(StorageOwnerContext owner, string path, Guid? exceptId)
    {
        foreach (FileMount other in Mounts(owner).AsEnumerable().Where(mount => mount.Id.Value != exceptId))
            if (string.Equals(other.Path, path, StringComparison.Ordinal) ||
                other.Path.StartsWith(path + "/", StringComparison.Ordinal) ||
                path.StartsWith(other.Path + "/", StringComparison.Ordinal))
                throw new DomainException($"The path '{path}' overlaps the storage path '{other.Path}'.");
    }

    private IQueryable<PersistentVolume> Volumes(StorageOwnerContext owner)
    {
        return dbContext.PersistentVolumes.Where(volume =>
            volume.OwnerKind == owner.Kind && volume.OwnerId == owner.Id && volume.TeamId == owner.TeamId);
    }

    private IQueryable<FileMount> Mounts(StorageOwnerContext owner)
    {
        return dbContext.FileMounts.Where(mount =>
            mount.OwnerKind == owner.Kind && mount.OwnerId == owner.Id && mount.TeamId == owner.TeamId);
    }

    private PersistentVolume? FindVolume(StorageOwnerContext owner, Guid id)
    {
        var volumeId = new PersistentVolumeId(id);
        return Volumes(owner).SingleOrDefault(volume => volume.Id == volumeId);
    }

    private FileMount? FindMount(StorageOwnerContext owner, Guid id)
    {
        var mountId = new FileMountId(id);
        return Mounts(owner).SingleOrDefault(mount => mount.Id == mountId);
    }

    private Dictionary<Guid, Guid> SchedulesByTarget(StorageOwnerContext owner)
    {
        return dbContext.ScheduledVolumeBackups
            .Where(schedule => schedule.OwnerKind == owner.Kind && schedule.OwnerId == owner.Id)
            .AsEnumerable()
            .ToDictionary(schedule => schedule.TargetId, schedule => schedule.Id.Value);
    }

    private static AuditTarget Target(Guid id)
    {
        return new AuditTarget("storage", id.ToString("D"));
    }

    private static Dictionary<string, object?> OwnerMetadata(StorageOwnerContext owner,
        ResourceStorageType? type = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["ownerKind"] = owner.Kind.ToString(),
            ["ownerId"] = owner.Id.ToString("D")
        };
        if (type is not null) metadata["type"] = type.Value.ToString();
        return metadata;
    }

    internal static ResourceStorageSummary ToSummary(PersistentVolume volume, IReadOnlyDictionary<Guid, Guid> schedules)
    {
        return new ResourceStorageSummary(volume.Id.Value, ResourceStorageType.Volume, volume.MountPath,
            volume.ServiceName, volume.Name, volume.RuntimeName, null, false, false, 0, false,
            schedules.TryGetValue(volume.Id.Value, out Guid schedule) ? schedule : null, volume.CreatedAt,
            volume.UpdatedAt);
    }

    internal static ResourceStorageSummary ToSummary(FileMount mount, IReadOnlyDictionary<Guid, Guid> schedules)
    {
        return new ResourceStorageSummary(mount.Id.Value,
            mount.IsDirectory ? ResourceStorageType.Directory : ResourceStorageType.File, mount.MountPath,
            mount.ServiceName, null, null, mount.Path, mount.IsHostFile, mount.HasContent, mount.ContentLength,
            mount.ContentPendingWrite, schedules.TryGetValue(mount.Id.Value, out Guid schedule) ? schedule : null,
            mount.CreatedAt, mount.UpdatedAt);
    }
}
