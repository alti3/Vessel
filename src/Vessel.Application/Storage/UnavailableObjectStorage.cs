namespace Vessel.Application.Storage;

public sealed class UnavailableObjectStorage : IObjectStorage
{
    public Task PutAsync(ObjectStoragePutRequest request, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Object storage is not configured.");
    }

    public Task<Stream> OpenReadAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Object storage is not configured.");
    }

    public Task<bool> ExistsAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task DeleteAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Object storage is not configured.");
    }
}
