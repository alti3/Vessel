using System.Net;

namespace Vessel.Application.OutboundRequests;

/// <summary>
///     Decides whether Vessel may open an outbound connection to an operator- or user-supplied endpoint
///     (notification webhooks, chat webhooks, SMTP hosts, and future Git/S3 endpoints).
/// </summary>
/// <remarks>
///     The policy blocks server-side request forgery: only <c>https</c> is accepted by default, the host is resolved,
///     and loopback, link-local (cloud metadata), multicast, and reserved addresses are always refused. Private
///     networks (RFC 1918, CGNAT, IPv6 unique local) are refused unless the instance allow-list names the host or
///     address. Infrastructure enforces the same decision again at connect time so DNS rebinding cannot bypass it.
///     Failure reasons never contain the URL path or query, which often embed channel secrets.
/// </remarks>
public interface IOutboundUrlPolicy
{
    /// <summary>Validates the URL scheme, shape, and every resolved address of its host.</summary>
    Task<OutboundTargetCheck> CheckUrlAsync(Uri url, CancellationToken cancellationToken = default);

    /// <summary>Validates a bare host name or IP literal (for example an SMTP host) and every resolved address.</summary>
    Task<OutboundTargetCheck> CheckHostAsync(string host, CancellationToken cancellationToken = default);

    /// <summary>Connect-time decision for one resolved address of <paramref name="host" />.</summary>
    bool IsAddressAllowed(string host, IPAddress address);
}

public sealed record OutboundTargetCheck(bool Allowed, string? Reason)
{
    public static OutboundTargetCheck Allow()
    {
        return new OutboundTargetCheck(true, null);
    }

    public static OutboundTargetCheck Deny(string reason)
    {
        return new OutboundTargetCheck(false, reason);
    }
}
