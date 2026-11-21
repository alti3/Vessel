using Vessel.Application.Files;
using Vessel.Application.Storage;

namespace Vessel.Infrastructure.Storage;

public sealed class LocalObjectStorage(string rootDirectory, IPathSafetyService paths) : IObjectStorage
{
    public async Task PutAsync(ObjectStoragePutRequest request, CancellationToken cancellationToken = default)
    {
        var path = ToPath(request.Location);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write to a sibling temporary file and move it into place so readers never see a partial object.
        var temporary = paths.EnsureOwnedPath(rootDirectory, $"{path}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (FileStream file = File.Create(temporary))
            {
                await request.Content.CopyToAsync(file, cancellationToken);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task<Stream> OpenReadAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        Stream stream = File.OpenRead(ToPath(location));
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(ToPath(location)));
    }

    public Task DeleteAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        var path = ToPath(location);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string ToPath(ObjectStorageKey location)
    {
        var relative = Path.Combine(location.Bucket, location.Key.Replace('/', Path.DirectorySeparatorChar));
        return paths.EnsureOwnedRelativePath(rootDirectory, relative);
    }
}
