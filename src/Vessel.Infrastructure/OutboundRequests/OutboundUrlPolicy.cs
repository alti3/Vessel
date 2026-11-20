using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Vessel.Application.OutboundRequests;

namespace Vessel.Infrastructure.OutboundRequests;

/// <summary>
///     SSRF guard for user-supplied outbound endpoints. See <see cref="IOutboundUrlPolicy" /> for the contract and
///     <see cref="OutboundRequestOptions" /> for the instance allow-list. Behavior follows upstream Coolify's
///     <c>App\Rules\SafeWebhookUrl</c>: loopback, link-local (cloud metadata), and reserved targets are blocked;
///     private networks need an explicit allow-list entry.
/// </summary>
public sealed class OutboundUrlPolicy : IOutboundUrlPolicy
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> MetadataHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata",
        "metadata.google.internal",
        "metadata.goog",
        "instance-data",
        "instance-data.ec2.internal"
    };

    // AWS IMDS over IPv6 lives inside the ULA range; treat it as metadata, never as an allow-listable private address.
    private static readonly IPAddress AwsMetadataIpv6 = IPAddress.Parse("fd00:ec2::254");

    private readonly IHostAddressResolver _resolver;
    private readonly bool _allowHttp;
    private readonly bool _allowLoopback;
    private readonly HashSet<string> _allowedHostNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _allowedHostSuffixes = [];
    private readonly List<(IPAddress Network, int PrefixLength)> _allowedNetworks = [];

    public OutboundUrlPolicy(IOptions<OutboundRequestOptions> options, IHostAddressResolver resolver)
    {
        _resolver = resolver;
        OutboundRequestOptions value = options.Value;
        _allowHttp = value.AllowHttp;
        _allowLoopback = value.AllowLoopback;

        foreach (var raw in value.AllowedInternalHosts)
        {
            var entry = raw?.Trim().TrimEnd('.').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(entry)) continue;

            if (TryParseNetwork(entry, out var network, out var prefixLength))
                _allowedNetworks.Add((network, prefixLength));
            else if (entry.StartsWith("*.", StringComparison.Ordinal) && entry.Length > 2)
                _allowedHostSuffixes.Add(entry[1..]);
            else
                _allowedHostNames.Add(entry);
        }
    }

    public async Task<OutboundTargetCheck> CheckUrlAsync(Uri url, CancellationToken cancellationToken = default)
    {
        OutboundTargetCheck shape = CheckUrlShape(url);
        if (!shape.Allowed) return shape;

        return await CheckHostAsync(url.IdnHost, cancellationToken);
    }

    /// <summary>Scheme, user-info, and host-shape checks that need no DNS lookup.</summary>
    public OutboundTargetCheck CheckUrlShape(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri)
            return OutboundTargetCheck.Deny("the URL must be absolute.");

        var host = NormalizeHost(url.IdnHost);
        if (string.IsNullOrEmpty(host) || url.Host.EndsWith('.'))
            return OutboundTargetCheck.Deny("the URL must contain a valid host without a trailing dot.");

        if (!string.IsNullOrEmpty(url.UserInfo))
            return OutboundTargetCheck.Deny("the URL must not embed credentials.");

        if (string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return OutboundTargetCheck.Allow();

        if (string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && (_allowHttp || IsHostNameAllowListed(host) ||
                (IPAddress.TryParse(host, out IPAddress? literal) && IsNetworkAllowListed(literal))))
            return OutboundTargetCheck.Allow();

        return OutboundTargetCheck.Deny("only https URLs are allowed.");
    }

    public async Task<OutboundTargetCheck> CheckHostAsync(string host, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized))
            return OutboundTargetCheck.Deny("a host is required.");

        if (IPAddress.TryParse(normalized, out IPAddress? literal))
            return IsAddressAllowed(normalized, literal)
                ? OutboundTargetCheck.Allow()
                : OutboundTargetCheck.Deny(DenyReason(Classify(literal)));

        if (MetadataHostNames.Contains(normalized))
            return OutboundTargetCheck.Deny(DenyReason(AddressClass.LinkLocal));

        IPAddress[] addresses;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResolveTimeout);
            addresses = await _resolver.ResolveAsync(normalized, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return OutboundTargetCheck.Deny("the host could not be resolved.");
        }

        if (addresses.Length == 0)
            return OutboundTargetCheck.Deny("the host could not be resolved.");

        // Reject when any answer is unsafe: a mixed answer is a classic DNS-rebinding setup.
        foreach (IPAddress address in addresses)
            if (!IsAddressAllowed(normalized, address))
                return OutboundTargetCheck.Deny(DenyReason(Classify(address)));

        return OutboundTargetCheck.Allow();
    }

    public bool IsAddressAllowed(string host, IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var normalizedHost = NormalizeHost(host);

        return Classify(address) switch
        {
            AddressClass.Public => true,
            AddressClass.Loopback => _allowLoopback || IsNetworkAllowListed(address),
            AddressClass.Private => IsHostNameAllowListed(normalizedHost) || IsNetworkAllowListed(address),
            _ => false
        };
    }

    internal static AddressClass Classify(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return ClassifyIpv4(address.GetAddressBytes());

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return AddressClass.Blocked;

        if (address.Equals(IPAddress.IPv6Loopback)) return AddressClass.Loopback;
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)) return AddressClass.Blocked;
        if (address.Equals(AwsMetadataIpv6)) return AddressClass.LinkLocal;
        if (address.IsIPv6LinkLocal) return AddressClass.LinkLocal;
        if (address.IsIPv6Multicast || address.IsIPv6SiteLocal) return AddressClass.Blocked;

        var bytes = address.GetAddressBytes();
        // fc00::/7 unique local addresses.
        if ((bytes[0] & 0xFE) == 0xFC) return AddressClass.Private;
        // 2001:db8::/32 documentation.
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) return AddressClass.Blocked;
        // 2001::/23 IETF protocol assignments (Teredo, benchmarking, ORCHID).
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 0x02) return AddressClass.Blocked;
        // 100::/64 discard-only.
        if (bytes[0] == 0x01 && bytes[1] == 0x00 && bytes.Skip(2).Take(6).All(b => b == 0)) return AddressClass.Blocked;
        // 64:ff9b::/96 NAT64 embeds an IPv4 address in the last 32 bits.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B &&
            bytes.Skip(4).Take(8).All(b => b == 0))
            return ClassifyIpv4(bytes[12..16]);
        // 2002::/16 6to4 embeds an IPv4 address in bits 16-47.
        if (bytes[0] == 0x20 && bytes[1] == 0x02) return ClassifyIpv4(bytes[2..6]);
        // ::/96 deprecated IPv4-compatible addresses.
        if (bytes.Take(12).All(b => b == 0)) return AddressClass.Blocked;

        return AddressClass.Public;
    }

    private static AddressClass ClassifyIpv4(byte[] b)
    {
        return b[0] switch
        {
            0 => AddressClass.Blocked,
            10 => AddressClass.Private,
            100 when b[1] is >= 64 and <= 127 => AddressClass.Private,
            127 => AddressClass.Loopback,
            169 when b[1] == 254 => AddressClass.LinkLocal,
            172 when b[1] is >= 16 and <= 31 => AddressClass.Private,
            192 when b[1] == 0 && b[2] is 0 or 2 => AddressClass.Blocked,
            192 when b[1] == 88 && b[2] == 99 => AddressClass.Blocked,
            192 when b[1] == 168 => AddressClass.Private,
            198 when b[1] is 18 or 19 => AddressClass.Blocked,
            198 when b[1] == 51 && b[2] == 100 => AddressClass.Blocked,
            203 when b[1] == 0 && b[2] == 113 => AddressClass.Blocked,
            >= 224 => AddressClass.Blocked,
            _ => AddressClass.Public
        };
    }

    private bool IsHostNameAllowListed(string host)
    {
        if (string.IsNullOrEmpty(host) || IPAddress.TryParse(host, out _)) return false;
        return _allowedHostNames.Contains(host) ||
               _allowedHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsNetworkAllowListed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        // Link-local/metadata and reserved ranges stay blocked even when an allow-list entry overlaps them.
        if (Classify(address) is AddressClass.LinkLocal or AddressClass.Blocked) return false;
        return _allowedNetworks.Any(entry => IsInNetwork(address, entry.Network, entry.PrefixLength));
    }

    private static bool IsInNetwork(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily) return false;
        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
            if (addressBytes[i] != networkBytes[i])
                return false;

        var remainingBits = prefixLength % 8;
        if (remainingBits == 0) return true;
        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    private static bool TryParseNetwork(string entry, out IPAddress network, out int prefixLength)
    {
        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        var addressPart = slash < 0 ? entry : entry[..slash];
        if (!IPAddress.TryParse(addressPart.Trim('[', ']'), out IPAddress? parsed))
        {
            network = IPAddress.None;
            prefixLength = 0;
            return false;
        }

        if (parsed.IsIPv4MappedToIPv6) parsed = parsed.MapToIPv4();
        var maxPrefix = parsed.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        prefixLength = maxPrefix;
        if (slash >= 0 && (!int.TryParse(entry[(slash + 1)..], out prefixLength) || prefixLength < 0 ||
                           prefixLength > maxPrefix))
        {
            network = IPAddress.None;
            return false;
        }

        network = parsed;
        return true;
    }

    private static string NormalizeHost(string? host)
    {
        return (host ?? string.Empty).Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
    }

    private static string DenyReason(AddressClass addressClass)
    {
        return addressClass switch
        {
            AddressClass.Loopback => "the host resolves to a loopback address.",
            AddressClass.LinkLocal => "the host resolves to a link-local or cloud metadata address.",
            AddressClass.Private =>
                "the host resolves to a private network address; add it to Vessel:OutboundRequests:AllowedInternalHosts to allow it.",
            _ => "the host resolves to a reserved or non-routable address."
        };
    }
}

internal enum AddressClass
{
    Public,
    Private,
    Loopback,
    LinkLocal,
    Blocked
}

/// <summary>DNS seam so the SSRF policy can be tested without network access.</summary>
public interface IHostAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class DnsHostAddressResolver : IHostAddressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        return Dns.GetHostAddressesAsync(host, cancellationToken);
    }
}
