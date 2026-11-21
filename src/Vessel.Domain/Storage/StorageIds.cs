using Vessel.Domain.Common;

// Kept beside the storage entities (instead of Ids.cs) so parallel feature work does not collide in one file.
// ReSharper disable once CheckNamespace
namespace Vessel.Domain;

public readonly record struct PersistentVolumeId(Guid Value) : IStronglyTypedId
{
    public static PersistentVolumeId New()
    {
        return new PersistentVolumeId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct FileMountId(Guid Value) : IStronglyTypedId
{
    public static FileMountId New()
    {
        return new FileMountId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct ScheduledVolumeBackupId(Guid Value) : IStronglyTypedId
{
    public static ScheduledVolumeBackupId New()
    {
        return new ScheduledVolumeBackupId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct VolumeBackupExecutionId(Guid Value) : IStronglyTypedId
{
    public static VolumeBackupExecutionId New()
    {
        return new VolumeBackupExecutionId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct VolumeBackupRestoreId(Guid Value) : IStronglyTypedId
{
    public static VolumeBackupRestoreId New()
    {
        return new VolumeBackupRestoreId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct DatabaseImportUploadId(Guid Value) : IStronglyTypedId
{
    public static DatabaseImportUploadId New()
    {
        return new DatabaseImportUploadId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
