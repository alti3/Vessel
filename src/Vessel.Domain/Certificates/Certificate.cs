using Vessel.Domain.Common;

namespace Vessel.Domain.Certificates;

/// <summary>
///     Inventory row for a TLS certificate managed by the server proxy. Traefik owns issuance and renewal and stores the
///     certificates in its ACME storage (<c>acme.json</c>); Vessel mirrors only metadata (main domain, SANs, resolver,
///     expiry, in-use) through the certificate sync job. Key material and certificate bodies are never persisted here.
///     A row without <see cref="AcmeKey" /> is an expected certificate for a TLS route that Traefik has not issued yet.
/// </summary>
public sealed class Certificate : Entity<CertificateId>
{
    public const int MaxHostLength = 253;
    public const int MaxSansLength = 8000;
    public const int MaxResolverLength = 120;
    public const int MaxAcmeKeyLength = 64;

    private Certificate()
    {
    }

    private Certificate(
        CertificateId id,
        TeamId teamId,
        ServerId serverId,
        ApplicationId? applicationId,
        string host,
        CertificateProvider provider,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        ServerId = serverId;
        ApplicationId = applicationId;
        Host = DomainValidation.Required(host, nameof(Host), MaxHostLength).ToLowerInvariant();
        Provider = provider;
        Status = CertificateStatus.Pending;
    }

    /// <summary>Team that owns the server (or the application the certificate serves).</summary>
    public TeamId TeamId { get; private set; }

    public ServerId ServerId { get; private set; }

    /// <summary>Application whose route uses the certificate, when one does.</summary>
    public ApplicationId? ApplicationId { get; private set; }

    /// <summary>Main domain of the certificate (or the expected host for a pending row).</summary>
    public string Host { get; private set; } = string.Empty;

    /// <summary>Subject alternative names, newline separated (without the main domain).</summary>
    public string? SubjectAlternativeNames { get; private set; }

    public CertificateProvider Provider { get; private set; }

    public CertificateStatus Status { get; private set; }

    /// <summary>Traefik certificate resolver that holds the certificate (for example <c>letsencrypt</c>).</summary>
    public string? Resolver { get; private set; }

    /// <summary>Stable identity of the certificate entry in <c>acme.json</c> (SHA-256 of resolver and entry).</summary>
    public string? AcmeKey { get; private set; }

    public DateTimeOffset? IssuedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>True when a route on the server matches the main domain or a SAN (wildcards cover one label).</summary>
    public bool InUse { get; private set; }

    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>Expiry the "expiring soon" alert was raised for; a renewed certificate (new expiry) re-arms the alert.</summary>
    public DateTimeOffset? ExpiryAlertSentFor { get; private set; }

    public DateTimeOffset? LastAttemptedAt { get; private set; }

    public string? LastError { get; private set; }

    public IReadOnlyList<string> SubjectAlternativeNameList =>
        string.IsNullOrEmpty(SubjectAlternativeNames)
            ? []
            : SubjectAlternativeNames.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>An expected certificate for a TLS route that Traefik has not issued yet.</summary>
    public static Certificate CreatePending(
        TeamId teamId,
        ServerId serverId,
        ApplicationId? applicationId,
        string host,
        DateTimeOffset now)
    {
        return new Certificate(CertificateId.New(), teamId, serverId, applicationId, host,
            CertificateProvider.TraefikAcme, now);
    }

    /// <summary>
    ///     Mirrors one <c>acme.json</c> entry. Status is <see cref="CertificateStatus.Expired" /> once the expiry passed.
    /// </summary>
    public void SyncFromAcme(
        string acmeKey,
        string resolver,
        string mainDomain,
        IReadOnlyList<string> subjectAlternativeNames,
        DateTimeOffset? issuedAt,
        DateTimeOffset? expiresAt,
        bool inUse,
        ApplicationId? applicationId,
        DateTimeOffset now)
    {
        AcmeKey = DomainValidation.Required(acmeKey, nameof(acmeKey), MaxAcmeKeyLength);
        Resolver = DomainValidation.Required(resolver, nameof(resolver), MaxResolverLength);
        Host = DomainValidation.Required(mainDomain, nameof(mainDomain), MaxHostLength).ToLowerInvariant();
        var sans = string.Join('\n', subjectAlternativeNames
            .Select(name => name.Trim().ToLowerInvariant())
            .Where(name => name.Length > 0 && !string.Equals(name, Host, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal));
        SubjectAlternativeNames = sans.Length == 0 ? null : DomainValidation.Optional(sans, nameof(sans), MaxSansLength);
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        InUse = inUse;
        ApplicationId = applicationId ?? ApplicationId;
        Status = expiresAt is not null && expiresAt <= now ? CertificateStatus.Expired : CertificateStatus.Issued;
        LastSyncedAt = now;
        LastError = null;
        Touch(now);
    }

    /// <summary>The entry disappeared from <c>acme.json</c> but a TLS route still expects a certificate.</summary>
    public void ResetToPending(DateTimeOffset now)
    {
        AcmeKey = null;
        Resolver = null;
        SubjectAlternativeNames = null;
        IssuedAt = null;
        ExpiresAt = null;
        ExpiryAlertSentFor = null;
        InUse = true;
        Status = CertificateStatus.Pending;
        LastSyncedAt = now;
        Touch(now);
    }

    public void MarkPendingSynced(bool inUse, ApplicationId? applicationId, DateTimeOffset now)
    {
        InUse = inUse;
        ApplicationId = applicationId ?? ApplicationId;
        LastSyncedAt = now;
        Touch(now);
    }

    /// <summary>True when the alert should be raised for the current expiry.</summary>
    public bool NeedsExpiryAlert(DateTimeOffset now, TimeSpan window)
    {
        return Status == CertificateStatus.Issued && InUse && ExpiresAt is not null &&
               ExpiresAt.Value - now <= window && ExpiryAlertSentFor != ExpiresAt;
    }

    public void MarkExpiryAlertSent(DateTimeOffset now)
    {
        ExpiryAlertSentFor = ExpiresAt;
        Touch(now);
    }

    public void MarkFailed(string safeError, DateTimeOffset now)
    {
        Status = CertificateStatus.Failed;
        LastAttemptedAt = now;
        LastError = DomainValidation.Required(safeError, nameof(safeError), 2000);
        Touch(now);
    }

    public void MarkIssuanceRequested(DateTimeOffset now)
    {
        LastAttemptedAt = now;
        Touch(now);
    }
}
