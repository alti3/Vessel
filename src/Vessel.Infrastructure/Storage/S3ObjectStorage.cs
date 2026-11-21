using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Options;
using Vessel.Application.Storage;
using Vessel.Infrastructure.Configuration;

namespace Vessel.Infrastructure.Storage;

public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    /// <summary>S3 multipart minimum part size (all parts except the last).</summary>
    public const long MinimumPartSizeBytes = 5L * 1024 * 1024;

    /// <summary>Uploads are split into at most this many parts (S3 allows 10,000; upstream Coolify uses 1,000).</summary>
    public const int MaximumPartCount = 1000;

    private const long MebiByte = 1024 * 1024;

    private readonly IAmazonS3 _control;
    private readonly IAmazonS3 _transfer;

    [Obsolete]
    public S3ObjectStorage(IOptionsMonitor<ObjectStorageOptions> options)
    {
        ObjectStorageOptions storageOptions = options.CurrentValue;
        AWSCredentials credentials = FallbackCredentialsFactory.GetCredentials();
        _control = new AmazonS3Client(credentials, CreateConfig(storageOptions, storageOptions.TimeoutSeconds));
        _transfer = new AmazonS3Client(credentials,
            CreateConfig(storageOptions, Math.Max(storageOptions.TimeoutSeconds, storageOptions.TransferTimeoutSeconds)));
    }

    /// <summary>Wraps explicit clients (per-destination backup storage). The instance owns and disposes them.</summary>
    internal S3ObjectStorage(IAmazonS3 control, IAmazonS3 transfer)
    {
        _control = control;
        _transfer = transfer;
    }

    internal IAmazonS3 Control => _control;

    public async Task PutAsync(ObjectStoragePutRequest request, CancellationToken cancellationToken = default)
    {
        long? length = request.ContentLength ?? (request.Content.CanSeek
            ? request.Content.Length - request.Content.Position
            : null);
        using var transfer = new TransferUtility(_transfer);
        var upload = new TransferUtilityUploadRequest
        {
            BucketName = request.Location.Bucket,
            Key = request.Location.Key,
            InputStream = request.Content,
            ContentType = request.ContentType,
            AutoCloseStream = false,
            PartSize = CalculatePartSize(length)
        };
        foreach (var (key, value) in request.Metadata ?? new Dictionary<string, string>())
            upload.Metadata[key] = value;
        await transfer.UploadAsync(upload, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        GetObjectResponse response = await _transfer.GetObjectAsync(location.Bucket, location.Key, cancellationToken);
        return response.ResponseStream;
    }

    public async Task<bool> ExistsAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        try
        {
            await _control.GetObjectMetadataAsync(location.Bucket, location.Key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public Task DeleteAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        return _control.DeleteObjectAsync(location.Bucket, location.Key, cancellationToken);
    }

    public void Dispose()
    {
        _control.Dispose();
        _transfer.Dispose();
    }

    /// <summary>
    /// Multipart part size that keeps an upload at or under <see cref="MaximumPartCount" /> parts, rounded up to a
    /// whole MiB and never below the S3 minimum.
    /// </summary>
    public static long CalculatePartSize(long? contentLength)
    {
        if (contentLength is not > 0) return MinimumPartSizeBytes;
        var perPart = (contentLength.Value + MaximumPartCount - 1) / MaximumPartCount;
        var rounded = (perPart + MebiByte - 1) / MebiByte * MebiByte;
        return Math.Max(MinimumPartSizeBytes, rounded);
    }

    private static AmazonS3Config CreateConfig(ObjectStorageOptions options, int timeoutSeconds)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
            config.ServiceURL = options.Endpoint;
        if (!string.IsNullOrWhiteSpace(options.Region))
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        return config;
    }
}
