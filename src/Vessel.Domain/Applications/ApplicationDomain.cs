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
        CreatedAt = createdAt;
    }

    public ApplicationId ApplicationId { get; private set; }

    public DomainName DomainName { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
