using Vessel.Domain.Common;

namespace Vessel.Domain.Storage;

/// <summary>
///     A named persistent volume of an application or service (upstream <c>local_persistent_volumes</c>). The runtime
///     volume name is derived from the owner ID and <see cref="Name" />, so renaming a volume means a new, empty volume.
///     There is no host path: upstream v4.3.22 removed it and Vessel never accepts one.
/// </summary>
public sealed class PersistentVolume : Entity<PersistentVolumeId>
{
    private PersistentVolume()
    {
    }

    private PersistentVolume(PersistentVolumeId id, TeamId teamId, StorageOwnerKind ownerKind, Guid ownerId,
        string name, string mountPath, string? serviceName, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        OwnerKind = ownerKind;
        OwnerId = ownerId;
        Name = StoragePaths.ValidateVolumeName(name);
        MountPath = StoragePaths.ValidateMountPath(mountPath);
        ServiceName = StoragePaths.ValidateServiceName(serviceName);
    }

    public TeamId TeamId { get; private set; }

    public StorageOwnerKind OwnerKind { get; private set; }

    public Guid OwnerId { get; private set; }

    /// <summary>User-facing name (unique per owner).</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Absolute path inside the container.</summary>
    public string MountPath { get; private set; } = string.Empty;

    /// <summary>Compose service the volume is mounted into; <see langword="null" /> for the primary service.</summary>
    public string? ServiceName { get; private set; }

    /// <summary>Runtime name used for production.</summary>
    public string RuntimeName => StoragePaths.RuntimeVolumeName(OwnerId, Name);

    public static PersistentVolume Create(TeamId teamId, StorageOwnerKind ownerKind, Guid ownerId, string name,
        string mountPath, string? serviceName, DateTimeOffset now)
    {
        if (ownerId == Guid.Empty) throw new DomainException("Storage owner is required.");
        return new PersistentVolume(PersistentVolumeId.New(), teamId, ownerKind, ownerId, name, mountPath,
            serviceName, now);
    }

    public string RuntimeNameFor(string? previewSuffix)
    {
        return StoragePaths.RuntimeVolumeName(OwnerId, Name, previewSuffix);
    }

    /// <summary>
    ///     Changes the volume. A new name is a new runtime volume (the old one keeps its data but is no longer mounted), so
    ///     a rename must be confirmed explicitly.
    /// </summary>
    public void Update(string name, string mountPath, string? serviceName, bool confirmRename, DateTimeOffset now)
    {
        var validated = StoragePaths.ValidateVolumeName(name);
        if (!string.Equals(validated, Name, StringComparison.Ordinal) && !confirmRename)
            throw new DomainException(
                "Renaming a volume mounts a new, empty volume; the data stays in the old volume. Confirm the rename to continue.");
        Name = validated;
        MountPath = StoragePaths.ValidateMountPath(mountPath);
        ServiceName = StoragePaths.ValidateServiceName(serviceName);
        Touch(now);
    }
}
