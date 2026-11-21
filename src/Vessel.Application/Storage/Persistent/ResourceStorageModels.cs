using System.Text.Json.Serialization;

namespace Vessel.Application.Storage.Persistent;

[JsonConverter(typeof(JsonStringEnumConverter<ResourceStorageType>))]
public enum ResourceStorageType
{
    /// <summary>A named persistent volume.</summary>
    Volume,

    /// <summary>A file in the resource directory: content managed by Vessel, or a host file managed on the server.</summary>
    File,

    /// <summary>A directory in the resource directory.</summary>
    Directory
}

/// <summary>
///     Creates a storage entry. <see cref="Name" /> applies to volumes; <see cref="Path" /> (relative to the resource
///     directory) to files and directories; <see cref="Content" /> to content files only. There is no host path.
/// </summary>
public sealed record CreateResourceStorageRequest(
    ResourceStorageType Type,
    string MountPath,
    string? Name = null,
    string? Path = null,
    string? Content = null,
    bool IsHostFile = false,
    string? ServiceName = null);

/// <summary>
///     Changes a storage entry; null fields are unchanged. <see cref="ConfirmRename" /> is required to rename a volume
///     (a new name mounts a new, empty volume). <see cref="ClearContent" /> removes the content of a content file.
/// </summary>
public sealed record UpdateResourceStorageRequest(
    string? MountPath = null,
    string? Name = null,
    string? Path = null,
    string? Content = null,
    bool ClearContent = false,
    string? ServiceName = null,
    bool ConfirmRename = false);

public sealed record ResourceStorageSummary(
    Guid Id,
    ResourceStorageType Type,
    string MountPath,
    string? ServiceName,
    string? Name,
    string? RuntimeName,
    string? Path,
    bool IsHostFile,
    bool HasContent,
    long ContentLength,
    bool ContentPendingWrite,
    Guid? BackupScheduleId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A storage entry with its file content (requires <c>secrets.read</c>; config files often hold secrets).</summary>
public sealed record ResourceStorageDetail(ResourceStorageSummary Storage, string? Content);
