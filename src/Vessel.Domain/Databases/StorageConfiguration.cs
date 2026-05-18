using Vessel.Domain.Common;

namespace Vessel.Domain.Databases;

public readonly record struct StorageConfiguration
{
    public StorageConfiguration(string volumeName, string mountPath)
        : this(volumeName, mountPath, null)
    {
    }

    public StorageConfiguration(string volumeName, string mountPath, long? sizeBytes)
    {
        if (sizeBytes is <= 0) throw new DomainException("Storage size must be greater than zero.");

        VolumeName = DomainValidation.Required(volumeName, nameof(VolumeName), 160);
        MountPath = DomainValidation.Required(mountPath, nameof(MountPath), 260);
        SizeBytes = sizeBytes;
    }

    public string VolumeName { get; }

    public string MountPath { get; }

    public long? SizeBytes { get; }
}
