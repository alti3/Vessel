using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Vessel.Application.Backups;
using Vessel.Application.Files;
using Vessel.Application.OutboundRequests;
using Vessel.Application.Storage;
using Vessel.Infrastructure.OutboundRequests;

namespace Vessel.Infrastructure.Storage;

public sealed class BackupStorageOptions
{
    public const string SectionName = "Vessel:Backups";

    /// <summary>Owned directory for local backup copies; defaults to <c>{app}/storage/backups</c>.</summary>
    public string? LocalRootDirectory { get; init; }

    /// <summary>Per-request timeout for destination control calls (list, delete, metadata).</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Per-request timeout for uploads and downloads (multipart parts).</summary>
    public int TransferTimeoutSeconds { get; init; } = 900;
}

/// <summary>
///     Local backup copies live in an owned directory on the control-plane host; remote copies go to team
///     destinations through per-destination S3 clients. Every destination connection goes through the guarded
///     outbound handler (address pinning against SSRF and DNS rebinding, no proxies, no redirects) and TLS
///     verification is skipped only for destinations that explicitly opted in.
/// </summary>
public sealed class BackupStorageProvider : IBackupStorageProvider
{
    /// <summary>Bucket (top-level directory) of local copies under the local root.</summary>
    public const string LocalBucket = "database-backups";

    private readonly IOutboundUrlPolicy _policy;
    private readonly IHostAddressResolver _resolver;
    private readonly BackupStorageOptions _options;

    public BackupStorageProvider(IOptions<BackupStorageOptions> options, IPathSafetyService paths,
        IOutboundUrlPolicy policy, IHostAddressResolver resolver)
    {
        _options = options.Value;
        _policy = policy;
        _resolver = resolver;
        var root = Path.GetFullPath(_options.LocalRootDirectory ??
                                    Path.Combine(AppContext.BaseDirectory, "storage", "backups"));
        Directory.CreateDirectory(root);
        Local = new BackupLocalStore(new LocalObjectStorage(root, paths), LocalBucket);
    }

    public BackupLocalStore Local { get; }

    public IBackupDestinationClient Connect(BackupDestinationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var factory = new GuardedS3HttpClientFactory(_policy, _resolver, connection.SkipTlsVerification);
        var credentials = new BasicAWSCredentials(connection.AccessKeyId, connection.SecretAccessKey);
        try
        {
            var control = new AmazonS3Client(credentials, CreateConfig(connection, factory, _options.TimeoutSeconds));
            var transfer = new AmazonS3Client(credentials,
                CreateConfig(connection, factory, Math.Max(_options.TimeoutSeconds, _options.TransferTimeoutSeconds)));
            return new S3BackupDestinationClient(new S3ObjectStorage(control, transfer), connection.Bucket, factory);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    internal static AmazonS3Config CreateConfig(BackupDestinationConnection connection, HttpClientFactory factory,
        int timeoutSeconds)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = connection.Endpoint,
            ForcePathStyle = connection.UsePathStyle,
            AuthenticationRegion = connection.Region,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            HttpClientFactory = factory,
            // Many S3-compatible stores reject the SDK's default CRC trailers; send checksums only when required.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            MaxErrorRetry = 3
        };
        if (!string.IsNullOrWhiteSpace(connection.Region) &&
            connection.Endpoint.Contains("amazonaws.com", StringComparison.OrdinalIgnoreCase))
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(connection.Region);
        return config;
    }
}

/// <summary>An S3 destination client: object operations plus a list-based connection test.</summary>
internal sealed class S3BackupDestinationClient(S3ObjectStorage storage, string bucket, IDisposable httpFactory)
    : IBackupDestinationClient
{
    public Task PutAsync(ObjectStoragePutRequest request, CancellationToken cancellationToken = default)
    {
        return Guard(() => storage.PutAsync(request, cancellationToken));
    }

    public Task<Stream> OpenReadAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        return Guard(() => storage.OpenReadAsync(location, cancellationToken));
    }

    public Task<bool> ExistsAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        return Guard(() => storage.ExistsAsync(location, cancellationToken));
    }

    public Task DeleteAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        return Guard(() => storage.DeleteAsync(location, cancellationToken));
    }

    public Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        return Guard(() => storage.Control.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 }, cancellationToken));
    }

    public void Dispose()
    {
        storage.Dispose();
        httpFactory.Dispose();
    }

    private static async Task Guard(Func<Task> action)
    {
        await Guard(async () =>
        {
            await action();
            return true;
        });
    }

    /// <summary>Maps SDK and transport errors to messages that never contain keys, signatures or URL paths.</summary>
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (AmazonS3Exception exception)
        {
            var code = string.IsNullOrWhiteSpace(exception.ErrorCode) ? "Error" : exception.ErrorCode;
            var hint = exception.StatusCode switch
            {
                HttpStatusCode.Forbidden => " Check the access key, secret key and bucket permissions.",
                HttpStatusCode.NotFound => " Check that the bucket exists.",
                HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect =>
                    " Check the region and the path-style setting.",
                _ => string.Empty
            };
            throw new BackupDestinationUnavailableException(
                $"The destination rejected the request ({(int)exception.StatusCode} {code}).{hint}", exception);
        }
        catch (OutboundRequestBlockedException exception)
        {
            throw new BackupDestinationUnavailableException(exception.Message, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new BackupDestinationUnavailableException(
                $"The destination could not be reached ({exception.HttpRequestError}).", exception);
        }
        catch (AmazonServiceException exception)
        {
            throw new BackupDestinationUnavailableException(
                $"The destination request failed ({exception.GetType().Name}).", exception);
        }
        catch (AmazonClientException exception)
        {
            throw new BackupDestinationUnavailableException(
                $"The destination request failed ({exception.GetType().Name}).", exception);
        }
    }
}

/// <summary>
///     Gives the AWS SDK one guarded <see cref="HttpClient" /> per destination client. The handler pins connections to
///     addresses the outbound policy allows; certificate validation is disabled only when the destination opted in.
/// </summary>
internal sealed class GuardedS3HttpClientFactory(
    IOutboundUrlPolicy policy,
    IHostAddressResolver resolver,
    bool skipTlsVerification) : HttpClientFactory, IDisposable
{
    private readonly Dictionary<TimeSpan, HttpClient> _clients = [];
    private readonly Lock _gate = new();

    public override HttpClient CreateHttpClient(IClientConfig clientConfig)
    {
        TimeSpan timeout = clientConfig.Timeout ?? TimeSpan.FromSeconds(100);
        lock (_gate)
        {
            if (_clients.TryGetValue(timeout, out HttpClient? existing)) return existing;
            SocketsHttpHandler handler = GuardedOutboundHttpHandler.Create(policy, resolver);
            if (skipTlsVerification)
                // Explicit, audited per-destination opt-in for self-signed internal stores.
#pragma warning disable CA5359
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
            var client = new HttpClient(handler, true) { Timeout = timeout };
            _clients[timeout] = client;
            return client;
        }
    }

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig)
    {
        return false;
    }

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig)
    {
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (HttpClient client in _clients.Values) client.Dispose();
            _clients.Clear();
        }
    }
}
