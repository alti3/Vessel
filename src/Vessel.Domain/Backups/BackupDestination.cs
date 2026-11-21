using System.Net;
using System.Text.RegularExpressions;
using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Backups;

public enum BackupDestinationKind
{
    /// <summary>Any S3-compatible object store (AWS S3, MinIO, Cloudflare R2, Backblaze B2, Wasabi, ...).</summary>
    S3 = 0
}

/// <summary>
///     A team-owned remote target for backup copies (upstream Coolify <c>s3_storages</c>). Credentials live in the
///     secret vault and are referenced by <see cref="CredentialsReferenceId" />; this aggregate never holds them.
///     A connection test runs on save and on an hourly revalidation, and its outcome is kept in
///     <see cref="IsUsable" />.
/// </summary>
public sealed class BackupDestination : Entity<BackupDestinationId>
{
    public const string DefaultRegion = "us-east-1";

    public const int MaxValidationErrorLength = 500;

    private BackupDestination()
    {
    }

    private BackupDestination(BackupDestinationId id, TeamId teamId, ResourceName name, BackupDestinationKind kind,
        SecretReferenceId credentialsReferenceId, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        Name = name;
        Kind = kind;
        CredentialsReferenceId = credentialsReferenceId;
    }

    public TeamId TeamId { get; private set; }

    public ResourceName Name { get; private set; }

    public Description? Description { get; private set; }

    public BackupDestinationKind Kind { get; private set; }

    /// <summary>Absolute <c>http(s)</c> endpoint URL, normalized without a trailing slash, query or fragment.</summary>
    public string Endpoint { get; private set; } = string.Empty;

    public string Region { get; private set; } = DefaultRegion;

    public string Bucket { get; private set; } = string.Empty;

    /// <summary>Address the bucket as a path (<c>endpoint/bucket</c>) rather than a virtual host. Default on.</summary>
    public bool UsePathStyle { get; private set; } = true;

    /// <summary>
    ///     Accept any TLS certificate from the endpoint. Explicit opt-in for self-signed internal stores; every change is
    ///     audited because it removes protection against interception of backup data and credentials.
    /// </summary>
    public bool SkipTlsVerification { get; private set; }

    public SecretReferenceId CredentialsReferenceId { get; private set; }

    /// <summary>Result of the last connection test; unusable destinations are revalidated hourly.</summary>
    public bool IsUsable { get; private set; }

    public DateTimeOffset? LastValidatedAt { get; private set; }

    public string? LastValidationError { get; private set; }

    /// <summary>When the team was told the destination is unusable; reset once it validates again.</summary>
    public DateTimeOffset? UnusableNotifiedAt { get; private set; }

    public static BackupDestination Create(TeamId teamId, ResourceName name, string endpoint, string? region,
        string bucket, bool usePathStyle, bool skipTlsVerification, SecretReferenceId credentialsReferenceId,
        DateTimeOffset now, Description? description = null)
    {
        var destination = new BackupDestination(BackupDestinationId.New(), teamId, name, BackupDestinationKind.S3,
            credentialsReferenceId, now);
        destination.Configure(name, description, endpoint, region, bucket, usePathStyle, skipTlsVerification, now);
        return destination;
    }

    /// <summary>Changes connection settings. Any change invalidates the previous connection test.</summary>
    public void Configure(ResourceName name, Description? description, string endpoint, string? region,
        string bucket, bool usePathStyle, bool skipTlsVerification, DateTimeOffset now)
    {
        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        var normalizedRegion = NormalizeRegion(region);
        var normalizedBucket = BackupBucketName.Validate(bucket);
        var connectionChanged = !string.Equals(Endpoint, normalizedEndpoint, StringComparison.Ordinal)
                                || !string.Equals(Region, normalizedRegion, StringComparison.Ordinal)
                                || !string.Equals(Bucket, normalizedBucket, StringComparison.Ordinal)
                                || UsePathStyle != usePathStyle
                                || SkipTlsVerification != skipTlsVerification;
        Name = name;
        Description = description;
        Endpoint = normalizedEndpoint;
        Region = normalizedRegion;
        Bucket = normalizedBucket;
        UsePathStyle = usePathStyle;
        SkipTlsVerification = skipTlsVerification;
        if (connectionChanged) InvalidateValidation();
        Touch(now);
    }

    public void ReplaceCredentials(SecretReferenceId credentialsReferenceId, DateTimeOffset now)
    {
        CredentialsReferenceId = credentialsReferenceId;
        InvalidateValidation();
        Touch(now);
    }

    public void MarkUsable(DateTimeOffset now)
    {
        IsUsable = true;
        LastValidatedAt = now;
        LastValidationError = null;
        UnusableNotifiedAt = null;
        Touch(now);
    }

    /// <summary>Records a failed connection test.</summary>
    /// <returns><see langword="true" /> when the team has not been told yet and should be notified now.</returns>
    public bool MarkUnusable(string reason, DateTimeOffset now)
    {
        var message = string.IsNullOrWhiteSpace(reason) ? "Connection test failed." : reason.Trim();
        IsUsable = false;
        LastValidatedAt = now;
        LastValidationError = message.Length > MaxValidationErrorLength
            ? message[..MaxValidationErrorLength]
            : message;
        Touch(now);
        return UnusableNotifiedAt is null;
    }

    public void RecordUnusableNotified(DateTimeOffset now)
    {
        UnusableNotifiedAt = now;
        Touch(now);
    }

    private void InvalidateValidation()
    {
        IsUsable = false;
        LastValidatedAt = null;
        LastValidationError = null;
    }

    public static string NormalizeEndpoint(string endpoint)
    {
        var value = DomainValidation.Required(endpoint, nameof(Endpoint), 500);
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new DomainException("Backup destination endpoint must be an absolute http or https URL.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new DomainException("Backup destination endpoint must not contain credentials.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new DomainException("Backup destination endpoint must not contain a query or fragment.");
        var path = uri.AbsolutePath.TrimEnd('/');
        return uri.GetLeftPart(UriPartial.Authority) + path;
    }

    private static string NormalizeRegion(string? region)
    {
        var value = DomainValidation.Optional(region, nameof(Region), 64) ?? DefaultRegion;
        return Regex.IsMatch(value, "^[A-Za-z0-9-]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            ? value
            : throw new DomainException("Backup destination region may contain only letters, digits and hyphens.");
    }
}

/// <summary>
///     S3 bucket name rules shared with upstream Coolify's <c>ValidS3BucketName</c>: 3-63 letters, digits, dots or
///     hyphens; starts and ends with a letter or digit; no <c>..</c>, <c>.-</c> or <c>-.</c>; not an IPv4 address.
///     Upper-case letters are accepted because some providers and legacy buckets use them (upstream v4.3.19).
/// </summary>
public static partial class BackupBucketName
{
    public static bool IsValid(string? value)
    {
        if (value is null || !Pattern().IsMatch(value)) return false;
        if (value.Contains("..", StringComparison.Ordinal) || value.Contains(".-", StringComparison.Ordinal) ||
            value.Contains("-.", StringComparison.Ordinal))
            return false;
        return !(value.Count(character => character == '.') == 3 && IPAddress.TryParse(value, out IPAddress? ip) &&
                 ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    }

    public static string Validate(string? value)
    {
        var trimmed = value?.Trim();
        return IsValid(trimmed)
            ? trimmed!
            : throw new DomainException(
                "Bucket must be a valid S3 bucket name: 3-63 letters, numbers, dots, or hyphens; start and end with a " +
                "letter or number; no consecutive dots, dot-hyphen pairs, or IP address format.");
    }

    [GeneratedRegex(@"\A(?=.{3,63}\z)[A-Za-z0-9][A-Za-z0-9.-]*[A-Za-z0-9]\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
