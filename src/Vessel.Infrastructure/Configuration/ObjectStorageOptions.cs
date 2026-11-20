namespace Vessel.Infrastructure.Configuration;

public sealed class ObjectStorageOptions
{
    public const string SectionName = "Vessel:ObjectStorage";

    public bool Enabled { get; init; }

    public string Provider { get; init; } = "S3";

    public string? Endpoint { get; init; }

    public string? BucketName { get; init; }

    public string? LocalRootDirectory { get; init; }

    public string? Region { get; init; }

    public bool ForcePathStyle { get; init; } = true;

    /// <summary>Per-request timeout for control calls (metadata, delete, existence checks).</summary>
    public int TimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Per-request timeout for data transfers (object uploads, multipart parts and downloads). The overall
    /// duration of a backup or restore is bounded by its own deadline, not by this value.
    /// </summary>
    public int TransferTimeoutSeconds { get; init; } = 900;
}
