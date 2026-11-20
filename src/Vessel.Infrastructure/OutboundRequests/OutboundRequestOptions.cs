namespace Vessel.Infrastructure.OutboundRequests;

/// <summary>
///     Instance-level SSRF policy for outbound requests to user-supplied endpoints (notification channels today).
///     Mirrors upstream Coolify's <c>webhook_allowed_internal_hosts</c> / <c>webhook_allow_localhost</c> settings.
/// </summary>
public sealed class OutboundRequestOptions
{
    public const string SectionName = "Vessel:OutboundRequests";

    /// <summary>Accept plain <c>http</c> URLs for every host. Off by default; allow-listed hosts may always use http.</summary>
    public bool AllowHttp { get; set; }

    /// <summary>Permit loopback targets (127.0.0.0/8, ::1, <c>localhost</c>). Off by default; for development only.</summary>
    public bool AllowLoopback { get; set; }

    /// <summary>
    ///     Internal endpoints the operator trusts. Entries may be a host name (<c>alerts.corp.example</c>), a
    ///     wildcard suffix (<c>*.corp.example</c>), an IP address, or a CIDR range (<c>10.20.0.0/16</c>). Matching
    ///     targets may resolve to private (RFC 1918, CGNAT, IPv6 ULA) addresses. Link-local/cloud-metadata,
    ///     multicast, and reserved ranges are never allowed.
    /// </summary>
    public List<string> AllowedInternalHosts { get; set; } = [];
}
