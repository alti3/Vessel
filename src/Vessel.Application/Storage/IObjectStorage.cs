namespace Vessel.Application.Storage;

public interface IObjectStorage
{
    Task PutAsync(ObjectStoragePutRequest request, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(ObjectStorageKey location, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(ObjectStorageKey location, CancellationToken cancellationToken = default);

    Task DeleteAsync(ObjectStorageKey location, CancellationToken cancellationToken = default);
}
