using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Vessel.Application.Storage;
using Vessel.Infrastructure.Configuration;

namespace Vessel.Infrastructure.Storage;

public sealed class S3ObjectStorage : IObjectStorage
{
    private readonly IAmazonS3 _client;

    [Obsolete]
    public S3ObjectStorage(IOptionsMonitor<ObjectStorageOptions> options)
    {
        ObjectStorageOptions storageOptions = options.CurrentValue;
        var config = new AmazonS3Config
        {
            ForcePathStyle = storageOptions.ForcePathStyle,
            Timeout = TimeSpan.FromSeconds(storageOptions.TimeoutSeconds)
        };
        if (!string.IsNullOrWhiteSpace(storageOptions.Endpoint))
            config.ServiceURL = storageOptions.Endpoint;
        if (!string.IsNullOrWhiteSpace(storageOptions.Region))
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(storageOptions.Region);

        _client = new AmazonS3Client(FallbackCredentialsFactory.GetCredentials(), config);
    }

    public Task PutAsync(ObjectStoragePutRequest request, CancellationToken cancellationToken = default)
    {
        var put = new PutObjectRequest
        {
            BucketName = request.Location.Bucket,
            Key = request.Location.Key,
            InputStream = request.Content,
            ContentType = request.ContentType
        };
        foreach (var (key, value) in request.Metadata ?? new Dictionary<string, string>())
            put.Metadata[key] = value;
        return _client.PutObjectAsync(put, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        GetObjectResponse response = await _client.GetObjectAsync(location.Bucket, location.Key, cancellationToken);
        return response.ResponseStream;
    }

    public async Task<bool> ExistsAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.GetObjectMetadataAsync(location.Bucket, location.Key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public Task DeleteAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        return _client.DeleteObjectAsync(location.Bucket, location.Key, cancellationToken);
    }
}
