namespace Vessel.Infrastructure.Configuration;

public sealed class ObjectStorageOptions
{
    public const string SectionName = "Vessel:ObjectStorage";

    public bool Enabled { get; init; }

    public string Provider { get; init; } = "S3";

    public string? Endpoint { get; init; }

    public string? BucketName { get; init; }

    public string? Region { get; init; }

    public bool ForcePathStyle { get; init; } = true;

    public int TimeoutSeconds { get; init; } = 5;
}
