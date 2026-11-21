using Vessel.Application.Deployments;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Application.Servers;
using Vessel.Application.Servers.Files;
using Vessel.Domain.Storage;

namespace Vessel.Application.Storage.Persistent;

/// <summary>
///     Turns a resource's storage entries into compose mounts and prepares them on the server before containers start
///     (upstream <c>EnsureContentFilesOnServer</c>). Everything lives in the resource's owned directory on the server's
///     file channel root, so paths are validated and symlink-checked by the channel; files are written atomically.
/// </summary>
public sealed class ResourceStorageProvisioner(
    IVesselDbContext dbContext,
    IEnumerable<IServerFileChannel> channels,
    ISecretCipher cipher,
    TimeProvider timeProvider)
{
    public bool HasStorage(StorageOwnerKind kind, Guid ownerId)
    {
        return dbContext.PersistentVolumes.Any(volume => volume.OwnerKind == kind && volume.OwnerId == ownerId) ||
               dbContext.FileMounts.Any(mount => mount.OwnerKind == kind && mount.OwnerId == ownerId);
    }

    /// <summary>
    ///     Compose mounts for one stack. Previews (<paramref name="previewSuffix" />) get their own volumes and their own
    ///     resource directory, so they never share data with production.
    /// </summary>
    public ComposeStorageSpec BuildSpec(StorageOwnerKind kind, Guid ownerId, ServerExecutionTarget target,
        string? previewSuffix, string? defaultService)
    {
        IServerFileChannel channel = channels.For(target);
        var directory = StoragePaths.ResourceDirectory(kind, ownerId, previewSuffix);
        var mounts = new List<ComposeStorageMount>();
        foreach (PersistentVolume volume in dbContext.PersistentVolumes
                     .Where(volume => volume.OwnerKind == kind && volume.OwnerId == ownerId)
                     .AsEnumerable().OrderBy(volume => volume.CreatedAt))
            mounts.Add(new ComposeStorageMount(ComposeStorageMountKind.Volume, volume.ServiceName,
                volume.RuntimeNameFor(previewSuffix), volume.MountPath));
        foreach (FileMount mount in dbContext.FileMounts
                     .Where(mount => mount.OwnerKind == kind && mount.OwnerId == ownerId)
                     .AsEnumerable().OrderBy(mount => mount.CreatedAt))
            mounts.Add(new ComposeStorageMount(ComposeStorageMountKind.Bind, mount.ServiceName,
                channel.ResolveHostPath(target, $"{directory}/{mount.Path}"), mount.MountPath));
        return new ComposeStorageSpec(defaultService, mounts);
    }

    /// <summary>
    ///     Creates the resource directory, directory mounts and missing files. A content file is overwritten only when its
    ///     content changed since the last production deployment; otherwise an existing file is kept. Host files are only
    ///     created (empty) when missing. Problems with one file are logged as warnings (never with content).
    /// </summary>
    /// <returns>The number of files written.</returns>
    public async Task<int> EnsureOnServerAsync(StorageOwnerKind kind, Guid ownerId, ServerExecutionTarget target,
        string? previewSuffix, Func<string, bool, CancellationToken, Task> log, CancellationToken cancellationToken)
    {
        FileMount[] mounts = dbContext.FileMounts
            .Where(mount => mount.OwnerKind == kind && mount.OwnerId == ownerId)
            .AsEnumerable().OrderBy(mount => mount.Path, StringComparer.Ordinal).ToArray();
        if (mounts.Length == 0) return 0;
        IServerFileChannel channel = channels.For(target);
        var directory = StoragePaths.ResourceDirectory(kind, ownerId, previewSuffix);
        await channel.EnsureDirectoryAsync(target, directory, ServerFileModes.WorldReadableDirectory,
            cancellationToken);

        var written = 0;
        var changed = false;
        foreach (FileMount mount in mounts)
        {
            var relative = $"{directory}/{mount.Path}";
            try
            {
                if (mount.IsDirectory)
                {
                    await channel.EnsureDirectoryAsync(target, relative, ServerFileModes.WorldReadableDirectory,
                        cancellationToken);
                    continue;
                }

                var parent = mount.Path.LastIndexOf('/');
                if (parent > 0)
                    await channel.EnsureDirectoryAsync(target, $"{directory}/{mount.Path[..parent]}",
                        ServerFileModes.WorldReadableDirectory, cancellationToken);
                var content = mount.ProtectedContent is null
                    ? string.Empty
                    : cipher.Unprotect(mount.ProtectedContent, ResourceStorageService.ContentPurpose(mount.Id));
                var overwrite = !mount.IsHostFile && mount.ContentPendingWrite;
                ServerFileWriteResult result = await channel.WriteAsync(target,
                    new ServerFileWrite(relative, content, ServerFileModes.WorldReadable, !overwrite),
                    cancellationToken);
                if (result == ServerFileWriteResult.Written) written++;
                // Previews write their own copy; only production clears the pending flag.
                if (overwrite && previewSuffix is null)
                {
                    mount.MarkContentWritten(timeProvider.GetUtcNow());
                    changed = true;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await log($"Warning: could not prepare the storage file '{mount.Path}' for {mount.MountPath}: " +
                          exception.Message, true, cancellationToken);
            }
        }

        if (changed) await dbContext.SaveChangesAsync(cancellationToken);
        if (written > 0)
            await log(written == 1 ? "Wrote 1 storage file." : $"Wrote {written} storage files.", false,
                cancellationToken);
        return written;
    }
}
