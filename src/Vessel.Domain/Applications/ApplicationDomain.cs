using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Applications;

/// <summary>
///     A public route of an application: host (DNS, wildcard, IPv4 or IPv6) plus an optional case-preserved path prefix.
///     Identity within the application is <c>(Host, PathPrefix)</c>; the scheme is expressed by <see cref="TlsEnabled" />.
/// </summary>
public sealed class ApplicationDomain
{
    private ApplicationDomain()
    {
    }

    internal ApplicationDomain(ApplicationId applicationId, RouteAddress address, DateTimeOffset createdAt)
    {
        ApplicationId = applicationId;
        Host = address.Host;
        HostKind = address.HostKind;
        PathPrefix = address.PathPrefix;
        TlsEnabled = true;
        Canonical = false;
        RedirectToCanonical = false;
        Policy = RoutePolicy.Default;
        CreatedAt = createdAt;
    }

    public ApplicationId ApplicationId { get; private set; }

    /// <summary>Normalized host (lower case, punycode, IPv6 without brackets). Indexed for conflict lookups.</summary>
    public string Host { get; private set; } = string.Empty;

    public RouteHostKind HostKind { get; private set; }

    /// <summary>Case-preserved path prefix, or empty for the root.</summary>
    public string PathPrefix { get; private set; } = string.Empty;

    public RouteAddress Address => RouteAddress.FromParts(Host, PathPrefix);

    public int? TargetPort { get; private set; }

    public bool TlsEnabled { get; private set; }

    public bool Canonical { get; private set; }

    public bool RedirectToCanonical { get; private set; }

    public RoutePolicy Policy { get; private set; } = RoutePolicy.Default;

    public DateTimeOffset CreatedAt { get; private set; }

    internal bool Is(RouteAddress address)
    {
        return string.Equals(Host, address.Host, StringComparison.Ordinal) &&
               string.Equals(PathPrefix, address.PathPrefix, StringComparison.Ordinal);
    }

    internal void UpdateRouting(int? targetPort, bool tlsEnabled, bool canonical, bool redirectToCanonical)
    {
        if (targetPort is < 1 or > 65535)
            throw new DomainException("Domain target port is invalid.");
        if (tlsEnabled && HostKind is RouteHostKind.IPv4 or RouteHostKind.IPv6)
            throw new DomainException("TLS certificates cannot be issued for IP address hosts; disable TLS for this route.");
        TargetPort = targetPort;
        TlsEnabled = tlsEnabled;
        Canonical = canonical;
        RedirectToCanonical = redirectToCanonical;
    }

    internal void UpdatePolicy(RoutePolicy policy)
    {
        Policy = policy ?? throw new DomainException("Route policy is required.");
    }
}
