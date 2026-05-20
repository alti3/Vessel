using Vessel.Domain.Common;

namespace Vessel.Domain.Certificates;

public sealed class Certificate : Entity<CertificateId>
{
    private Certificate()
    {
    }

    private Certificate(
        CertificateId id,
        TeamId teamId,
        ApplicationId applicationId,
        string host,
        CertificateProvider provider,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        ApplicationId = applicationId;
        Host = DomainValidation.Required(host, nameof(Host), 253);
        Provider = provider;
        Status = CertificateStatus.Pending;
    }

    public TeamId TeamId { get; private set; }

    public ApplicationId ApplicationId { get; private set; }

    public string Host { get; private set; } = string.Empty;

    public CertificateProvider Provider { get; private set; }

    public CertificateStatus Status { get; private set; }

    public DateTimeOffset? IssuedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? RenewalDueAt { get; private set; }

    public DateTimeOffset? LastAttemptedAt { get; private set; }

    public string? LastError { get; private set; }

    public SecretReferenceId? CertificateSecretReferenceId { get; private set; }

    public SecretReferenceId? PrivateKeySecretReferenceId { get; private set; }

    public static Certificate Create(
        TeamId teamId,
        ApplicationId applicationId,
        string host,
        CertificateProvider provider,
        DateTimeOffset now)
    {
        return new Certificate(CertificateId.New(), teamId, applicationId, host, provider, now);
    }

    public void MarkIssued(
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        SecretReferenceId? certificateSecretReferenceId,
        SecretReferenceId? privateKeySecretReferenceId)
    {
        if (expiresAt <= issuedAt) throw new DomainException("Certificate expiry must be after issue time.");
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        RenewalDueAt = expiresAt.AddDays(-14);
        CertificateSecretReferenceId = certificateSecretReferenceId;
        PrivateKeySecretReferenceId = privateKeySecretReferenceId;
        Status = CertificateStatus.Issued;
        LastError = null;
        Touch(issuedAt);
    }

    public void QueueRenewal(DateTimeOffset now)
    {
        Status = CertificateStatus.RenewalQueued;
        LastAttemptedAt = now;
        Touch(now);
    }

    public void MarkFailed(string safeError, DateTimeOffset now)
    {
        Status = CertificateStatus.Failed;
        LastAttemptedAt = now;
        LastError = DomainValidation.Required(safeError, nameof(safeError), 2000);
        Touch(now);
    }
}
