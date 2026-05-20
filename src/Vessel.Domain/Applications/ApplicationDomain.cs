using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Applications;

public sealed class ApplicationDomain
{
    private ApplicationDomain()
    {
    }

    internal ApplicationDomain(ApplicationId applicationId, DomainName domainName, DateTimeOffset createdAt)
    {
        ApplicationId = applicationId;
        DomainName = domainName;
        TlsEnabled = true;
        Canonical = false;
        RedirectToCanonical = false;
        CreatedAt = createdAt;
    }

    public ApplicationId ApplicationId { get; private set; }

    public DomainName DomainName { get; private set; }

    public int? TargetPort { get; private set; }

    public bool TlsEnabled { get; private set; }

    public bool Canonical { get; private set; }

    public bool RedirectToCanonical { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal void UpdateRouting(int? targetPort, bool tlsEnabled, bool canonical, bool redirectToCanonical)
    {
        if (targetPort is < 1 or > 65535)
            throw new DomainException("Domain target port is invalid.");
        TargetPort = targetPort;
        TlsEnabled = tlsEnabled;
        Canonical = canonical;
        RedirectToCanonical = redirectToCanonical;
    }
}
