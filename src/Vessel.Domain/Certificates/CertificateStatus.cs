namespace Vessel.Domain.Certificates;

/// <summary>
///     Certificate inventory status. Traefik renews certificates itself; Vessel only mirrors <c>acme.json</c>, so
///     <see cref="RenewalDue" />, <see cref="RenewalQueued" /> and <see cref="Revoked" /> are legacy values that are no
///     longer produced (rows holding them are rewritten by the next certificate sync).
/// </summary>
public enum CertificateStatus
{
    Pending,
    Issued,
    RenewalDue,
    RenewalQueued,
    Failed,
    Revoked,
    Expired
}
