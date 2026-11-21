using System.Text.RegularExpressions;
using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Services;

/// <summary>
///     A public route of a service-template resource: one Compose service of the stack and the internal port it
///     listens on (upstream per-service domains and <c>domain_port_overrides</c>). Identity is
///     <c>(ServiceResourceId, Host, PathPrefix)</c>.
/// </summary>
public sealed partial class ServiceDomain : Entity<ServiceDomainId>
{
    public const int MaxComposeServiceLength = 63;

    private ServiceDomain()
    {
    }

    private ServiceDomain(
        ServiceDomainId id,
        ServiceResourceId serviceResourceId,
        RouteAddress address,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        ServiceResourceId = serviceResourceId;
        Host = address.Host;
        HostKind = address.HostKind;
        PathPrefix = address.PathPrefix;
        Policy = RoutePolicy.Default;
    }

    public ServiceResourceId ServiceResourceId { get; private set; }

    /// <summary>Compose service name inside the template stack that receives the traffic.</summary>
    public string ComposeService { get; private set; } = string.Empty;

    public string Host { get; private set; } = string.Empty;

    public RouteHostKind HostKind { get; private set; }

    public string PathPrefix { get; private set; } = string.Empty;

    public RouteAddress Address => RouteAddress.FromParts(Host, PathPrefix);

    public int TargetPort { get; private set; }

    public bool TlsEnabled { get; private set; }

    public RoutePolicy Policy { get; private set; } = RoutePolicy.Default;

    public static ServiceDomain Create(
        ServiceResourceId serviceResourceId,
        RouteAddress address,
        string composeService,
        int targetPort,
        bool tlsEnabled,
        RoutePolicy? policy,
        DateTimeOffset now)
    {
        var domain = new ServiceDomain(ServiceDomainId.New(), serviceResourceId, address, now);
        domain.Update(composeService, targetPort, tlsEnabled, policy, now);
        return domain;
    }

    public bool Is(RouteAddress address)
    {
        return string.Equals(Host, address.Host, StringComparison.Ordinal) &&
               string.Equals(PathPrefix, address.PathPrefix, StringComparison.Ordinal);
    }

    public void Update(string composeService, int targetPort, bool tlsEnabled, RoutePolicy? policy,
        DateTimeOffset now)
    {
        var service = DomainValidation.Required(composeService, nameof(composeService), MaxComposeServiceLength);
        if (!ComposeServicePattern().IsMatch(service))
            throw new DomainException("Compose service name is invalid.");
        if (targetPort is < 1 or > 65535)
            throw new DomainException("Domain target port is invalid.");
        if (tlsEnabled && HostKind is RouteHostKind.IPv4 or RouteHostKind.IPv6)
            throw new DomainException("TLS certificates cannot be issued for IP address hosts; disable TLS for this route.");
        ComposeService = service;
        TargetPort = targetPort;
        TlsEnabled = tlsEnabled;
        if (policy is not null) Policy = policy;
        Touch(now);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ComposeServicePattern();
}
