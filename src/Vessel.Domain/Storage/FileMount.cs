using Vessel.Domain.Common;

namespace Vessel.Domain.Storage;

/// <summary>
///     A file or directory bind-mounted from the resource's owned directory (upstream <c>local_file_volumes</c>).
///     <list type="bullet">
///         <item>A content file stores its content (encrypted at rest, at most <see cref="MaxContentBytes" />) and is
///             written to the resource directory before containers start.</item>
///         <item>A host file (<see cref="IsHostFile" />) is managed on the server: Vessel never writes its content (it
///             creates an empty file only when the path is missing, so the runtime does not create a directory).</item>
///         <item>A directory mount (<see cref="IsDirectory" />) is created when missing and can be backed up.</item>
///     </list>
///     <see cref="Path" /> is always relative to the resource directory; arbitrary host paths do not exist.
/// </summary>
public sealed class FileMount : Entity<FileMountId>
{
    /// <summary>Upstream <c>LocalFileVolume::MAX_CONTENT_SIZE</c> (5 MiB).</summary>
    public const int MaxContentBytes = 5 * 1024 * 1024;

    private FileMount()
    {
    }

    private FileMount(FileMountId id, TeamId teamId, StorageOwnerKind ownerKind, Guid ownerId, string path,
        string mountPath, bool isDirectory, bool isHostFile, string? serviceName, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        OwnerKind = ownerKind;
        OwnerId = ownerId;
        Path = StoragePaths.ValidateRelativePath(path);
        MountPath = StoragePaths.ValidateMountPath(mountPath);
        IsDirectory = isDirectory;
        IsHostFile = isHostFile;
        ServiceName = StoragePaths.ValidateServiceName(serviceName);
    }

    public TeamId TeamId { get; private set; }

    public StorageOwnerKind OwnerKind { get; private set; }

    public Guid OwnerId { get; private set; }

    /// <summary>Path relative to the resource directory.</summary>
    public string Path { get; private set; } = string.Empty;

    public string MountPath { get; private set; } = string.Empty;

    public bool IsDirectory { get; private set; }

    public bool IsHostFile { get; private set; }

    public string? ServiceName { get; private set; }

    /// <summary>Content encrypted with the secret key ring (never plaintext); <see langword="null" /> for none.</summary>
    public string? ProtectedContent { get; private set; }

    /// <summary>UTF-8 byte length of the plaintext content.</summary>
    public long ContentLength { get; private set; }

    /// <summary>
    ///     True after the content changed: the next deployment overwrites the file on the server. Otherwise a file is
    ///     written only when missing (upstream <c>EnsureContentFilesOnServer</c>).
    /// </summary>
    public bool ContentPendingWrite { get; private set; }

    public bool HasContent => ProtectedContent is not null;

    public static FileMount CreateFile(TeamId teamId, StorageOwnerKind ownerKind, Guid ownerId, string path,
        string mountPath, string? serviceName, string? protectedContent, long contentLength, DateTimeOffset now)
    {
        if (ownerId == Guid.Empty) throw new DomainException("Storage owner is required.");
        var mount = new FileMount(FileMountId.New(), teamId, ownerKind, ownerId, path, mountPath, false, false,
            serviceName, now);
        mount.SetContent(protectedContent, contentLength);
        return mount;
    }

    public static FileMount CreateHostFile(TeamId teamId, StorageOwnerKind ownerKind, Guid ownerId, string path,
        string mountPath, string? serviceName, DateTimeOffset now)
    {
        if (ownerId == Guid.Empty) throw new DomainException("Storage owner is required.");
        return new FileMount(FileMountId.New(), teamId, ownerKind, ownerId, path, mountPath, false, true,
            serviceName, now);
    }

    public static FileMount CreateDirectory(TeamId teamId, StorageOwnerKind ownerKind, Guid ownerId, string path,
        string mountPath, string? serviceName, DateTimeOffset now)
    {
        if (ownerId == Guid.Empty) throw new DomainException("Storage owner is required.");
        return new FileMount(FileMountId.New(), teamId, ownerKind, ownerId, path, mountPath, true, false,
            serviceName, now);
    }

    /// <summary>Changes where the mount lives and where it is mounted. A new path is written on the next deploy.</summary>
    public void Relocate(string path, string mountPath, string? serviceName, DateTimeOffset now)
    {
        var validated = StoragePaths.ValidateRelativePath(path);
        if (!string.Equals(validated, Path, StringComparison.Ordinal) && HasContent) ContentPendingWrite = true;
        Path = validated;
        MountPath = StoragePaths.ValidateMountPath(mountPath);
        ServiceName = StoragePaths.ValidateServiceName(serviceName);
        Touch(now);
    }

    /// <summary>Replaces the content (already encrypted by the caller). Only content files have content.</summary>
    public void ReplaceContent(string? protectedContent, long contentLength, DateTimeOffset now)
    {
        SetContent(protectedContent, contentLength);
        Touch(now);
    }

    public void MarkContentWritten(DateTimeOffset now)
    {
        if (!ContentPendingWrite) return;
        ContentPendingWrite = false;
        Touch(now);
    }

    private void SetContent(string? protectedContent, long contentLength)
    {
        if (IsDirectory || IsHostFile)
        {
            if (protectedContent is not null)
                throw new DomainException(IsDirectory
                    ? "Directory mounts have no content."
                    : "Host files are managed on the server; Vessel does not store their content.");
            return;
        }

        if (contentLength is < 0 or > MaxContentBytes)
            throw new DomainException($"File content must be at most {MaxContentBytes / (1024 * 1024)} MiB.");
        ProtectedContent = protectedContent;
        ContentLength = protectedContent is null ? 0 : contentLength;
        ContentPendingWrite = true;
    }
}
