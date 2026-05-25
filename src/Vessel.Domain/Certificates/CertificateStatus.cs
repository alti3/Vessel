namespace Vessel.Domain.Certificates;

public enum CertificateStatus
{
    Pending,
    Issued,
    RenewalDue,
    RenewalQueued,
    Failed,
    Revoked
}
