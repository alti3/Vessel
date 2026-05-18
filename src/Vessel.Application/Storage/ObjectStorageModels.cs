namespace Vessel.Application.Storage;

public sealed record ObjectStorageKey(string Bucket, string Key);

public sealed record ObjectStoragePutRequest(
    ObjectStorageKey Location,
    Stream Content,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ObjectStorageObject(
    ObjectStorageKey Location,
    long? Length,
    DateTimeOffset? LastModified,
    string? ETag);
