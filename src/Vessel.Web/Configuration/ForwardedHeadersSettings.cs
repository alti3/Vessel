using System.Net;

namespace Vessel.Web.Configuration;

/// <summary>
///     SEC-07: real client addresses behind a reverse proxy. <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c> are
///     honoured <b>only</b> from the explicitly listed proxies or networks (for example the managed Traefik network or
///     <c>127.0.0.1</c> when Traefik runs on the host). Nothing is trusted by default — not even loopback — so a client
///     can never rotate its rate-limit bucket by sending its own forwarding headers.
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "Vessel:ForwardedHeaders";

    public bool Enabled { get; init; }

    /// <summary>Trusted proxy addresses, e.g. <c>127.0.0.1</c>.</summary>
    public IReadOnlyList<string> KnownProxies { get; init; } = [];

    /// <summary>Trusted proxy networks in CIDR notation, e.g. <c>172.18.0.0/16</c>.</summary>
    public IReadOnlyList<string> KnownNetworks { get; init; } = [];

    /// <summary>How many proxy hops to unwind (one per trusted proxy in the chain).</summary>
    public int ForwardLimit { get; init; } = 1;

    public bool HasTrustedProxies => KnownProxies.Count > 0 || KnownNetworks.Count > 0;

    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        foreach (var proxy in KnownProxies)
            if (!IPAddress.TryParse(proxy, out _))
                failures.Add($"ForwardedHeaders:KnownProxies entry '{proxy}' is not an IP address.");
        foreach (var network in KnownNetworks)
            if (!IPNetwork.TryParse(network, out _))
                failures.Add($"ForwardedHeaders:KnownNetworks entry '{network}' is not a CIDR network.");
        if (ForwardLimit is < 1 or > 10)
            failures.Add("ForwardedHeaders:ForwardLimit must be between 1 and 10.");
        if (Enabled && !HasTrustedProxies)
            failures.Add("ForwardedHeaders is enabled but no KnownProxies or KnownNetworks are configured.");
        return failures;
    }
}
