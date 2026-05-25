using Vessel.Application.Files;
using Vessel.Application.Storage;

namespace Vessel.Infrastructure.Storage;

public sealed class LocalObjectStorage(string rootDirectory, IPathSafetyService paths) : IObjectStorage
{
    public async Task PutAsync(ObjectStoragePutRequest request, CancellationToken cancellationToken = default)
    {
        var path = ToPath(request.Location);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using FileStream file = File.Create(path);
        await request.Content.CopyToAsync(file, cancellationToken);
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
