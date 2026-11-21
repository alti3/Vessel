using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

/// <summary>Kind of host a public route answers on.</summary>
public enum RouteHostKind
{
    /// <summary>A DNS name with at least two labels (underscores and punycode TLDs allowed).</summary>
    Dns = 0,

    /// <summary>A <c>*.</c> wildcard over a DNS name (one label deep, like a wildcard certificate).</summary>
    Wildcard = 1,

    IPv4 = 2,

    /// <summary>An IPv6 address, stored without brackets (Traefik matches IPv6 hosts without brackets).</summary>
    IPv6 = 3
}

/// <summary>Scheme written by the user. <c>ws</c>/<c>wss</c> are normalized to <c>http</c>/<c>https</c>.</summary>
public enum RouteScheme
{
    Http = 0,
    Https = 1
}

/// <summary>
///     A public routing address (Coolify "domain" URL): optional scheme, host and optional path prefix. The routing
///     identity (<see cref="Identity" />) ignores the scheme, so <c>http://</c> and <c>https://</c> forms of a host are the
///     same route. Paths keep their case and are routed as a Traefik <c>PathPrefix</c>. A port in the input is the internal
///     target port (public URLs stay portless), never a public listener.
/// </summary>
public readonly partial record struct RouteAddress
{
    public const int MaxInputLength = 2048;
    public const int MaxPathLength = 1024;

    private RouteAddress(RouteScheme? scheme, RouteHostKind hostKind, string host, string pathPrefix, int? port)
    {
        Scheme = scheme;
        HostKind = hostKind;
        Host = host;
        PathPrefix = pathPrefix;
        Port = port;
    }

    /// <summary>Scheme from the input, or <see langword="null" /> when the input had none.</summary>
    public RouteScheme? Scheme { get; }

    public RouteHostKind HostKind { get; }

    /// <summary>Normalized host: lower case, punycode (IDN), wildcard <c>*.</c> prefix kept, IPv6 without brackets.</summary>
    public string Host { get; }

    /// <summary>Case-preserved path prefix without a trailing slash, or empty for the root.</summary>
    public string PathPrefix { get; }

    /// <summary>Internal target port written in the input (for example <c>https://app.example.com:3000</c>).</summary>
    public int? Port { get; }

    /// <summary>Scheme-independent routing identity: <c>host</c> + <c>path</c>.</summary>
    public string Identity => Host + PathPrefix;

    /// <summary>Host as written in a URL (IPv6 bracketed).</summary>
    public string UrlHost => HostKind == RouteHostKind.IPv6 ? $"[{Host}]" : Host;

    public static RouteAddress Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new DomainException("Domain is required.");
        var value = input.Trim();
        if (value.Length > MaxInputLength)
            throw new DomainException($"Domain must be {MaxInputLength} characters or fewer.");
        if (value.Any(char.IsControl) || value.Any(char.IsWhiteSpace))
            throw new DomainException("Domain must not contain whitespace or control characters.");

        RouteScheme? scheme = null;
        var schemeSeparator = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            scheme = value[..schemeSeparator].ToLowerInvariant() switch
            {
                "http" or "ws" => RouteScheme.Http,
                "https" or "wss" => RouteScheme.Https,
                _ => throw new DomainException("Domain scheme must be http, https, ws or wss.")
            };
            value = value[(schemeSeparator + 3)..];
        }

        if (value.IndexOfAny(['?', '#']) >= 0)
            throw new DomainException("Domain must not include a query string or fragment.");

        var pathStart = value.IndexOf('/', StringComparison.Ordinal);
        var authority = pathStart >= 0 ? value[..pathStart] : value;
        var path = pathStart >= 0 ? value[pathStart..] : string.Empty;
        if (authority.Contains('@', StringComparison.Ordinal))
            throw new DomainException("Domain must not include credentials.");

        (var hostText, int? port) = SplitAuthority(authority);
        (RouteHostKind kind, var host) = NormalizeHost(hostText);
        return new RouteAddress(scheme, kind, host, NormalizePath(path), port);
    }

    /// <summary>Rebuilds an address from persisted parts (already normalized; validated again).</summary>
    public static RouteAddress FromParts(string host, string? pathPrefix)
    {
        (RouteHostKind kind, var normalizedHost) = NormalizeHost(host);
        return new RouteAddress(null, kind, normalizedHost, NormalizePath(pathPrefix ?? string.Empty), null);
    }

    public static RouteAddress FromDomainName(DomainName domainName)
    {
        return new RouteAddress(null, RouteHostKind.Dns, domainName.Value, string.Empty, null);
    }

    public RouteAddress WithoutPort()
    {
        return new RouteAddress(Scheme, HostKind, Host, PathPrefix, null);
    }

    public string ToUrl(bool tls)
    {
        return $"{(tls ? "https" : "http")}://{UrlHost}{PathPrefix}";
    }

    /// <summary>True when this route answers for <paramref name="hostName" /> (wildcards cover exactly one label).</summary>
    public bool MatchesHost(string hostName)
    {
        var candidate = hostName.Trim().ToLowerInvariant();
        if (HostKind != RouteHostKind.Wildcard) return string.Equals(Host, candidate, StringComparison.Ordinal);
        var suffix = Host[1..];
        if (!candidate.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var label = candidate[..^suffix.Length];
        return label.Length > 0 && !label.Contains('.', StringComparison.Ordinal);
    }

    public override string ToString()
    {
        return Identity;
    }

    private static (string Host, int? Port) SplitAuthority(string authority)
    {
        if (authority.Length == 0) throw new DomainException("Domain host is required.");
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0) throw new DomainException("Domain IPv6 address is missing a closing bracket.");
            var rest = authority[(close + 1)..];
            if (rest.Length == 0) return (authority[1..close], null);
            if (!rest.StartsWith(':')) throw new DomainException("Domain host is invalid.");
            return (authority[1..close], ParsePort(rest[1..]));
        }

        var colons = authority.Count(character => character == ':');
        if (colons > 1) return (authority, null); // bare IPv6 without brackets (no port possible)
        if (colons == 0) return (authority, null);
        var separator = authority.IndexOf(':', StringComparison.Ordinal);
        return (authority[..separator], ParsePort(authority[(separator + 1)..]));
    }

    private static int ParsePort(string text)
    {
        if (!PortPattern().IsMatch(text) ||
            !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
            throw new DomainException("Domain port must be between 1 and 65535.");
        return port;
    }

    private static (RouteHostKind Kind, string Host) NormalizeHost(string hostText)
    {
        if (string.IsNullOrWhiteSpace(hostText)) throw new DomainException("Domain host is required.");
        var candidate = hostText.Trim().TrimEnd('.');

        if (candidate.Contains(':', StringComparison.Ordinal))
        {
            if (candidate.Contains('%', StringComparison.Ordinal) ||
                !IPAddress.TryParse(candidate, out IPAddress? address) ||
                address.AddressFamily != AddressFamily.InterNetworkV6)
                throw new DomainException("Domain IPv6 address is invalid.");
            return (RouteHostKind.IPv6, address.ToString().ToLowerInvariant());
        }

        if (DottedNumericPattern().IsMatch(candidate))
        {
            if (!IPv4Pattern().IsMatch(candidate)) throw new DomainException("Domain IPv4 address is invalid.");
            return (RouteHostKind.IPv4, candidate);
        }

        var wildcard = candidate.StartsWith("*.", StringComparison.Ordinal);
        var dnsPart = wildcard ? candidate[2..] : candidate;
        if (dnsPart.Contains('*', StringComparison.Ordinal))
            throw new DomainException("Wildcard domains must start with '*.' and contain a single wildcard.");

        string ascii;
        try
        {
            ascii = dnsPart.Any(character => character > 0x7F)
                ? new IdnMapping { AllowUnassigned = false, UseStd3AsciiRules = false }.GetAscii(dnsPart)
                : dnsPart;
        }
        catch (ArgumentException)
        {
            throw new DomainException("Domain name is invalid.");
        }

        var dns = new DomainName(ascii).Value;
        return wildcard ? (RouteHostKind.Wildcard, "*." + dns) : (RouteHostKind.Dns, dns);
    }

    private static string NormalizePath(string path)
    {
        if (path.Length == 0 || path == "/") return string.Empty;
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length > MaxPathLength)
            throw new DomainException($"Domain path must be {MaxPathLength} characters or fewer.");
        if (!trimmed.StartsWith('/') || !PathPattern().IsMatch(trimmed))
            throw new DomainException(
                "Domain path may only contain letters, digits and - . _ ~ % ! $ & ( ) * + , ; = : @ separated by '/'.");
        foreach (var segment in trimmed[1..].Split('/'))
            if (segment.Length == 0 || segment is "." or "..")
                throw new DomainException("Domain path must not contain empty, '.' or '..' segments.");
        return trimmed;
    }

    [GeneratedRegex("^[0-9]{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex PortPattern();

    [GeneratedRegex(@"^[0-9.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DottedNumericPattern();

    [GeneratedRegex(@"^((25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])\.){3}(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IPv4Pattern();

    // Backticks, quotes, backslashes and whitespace are excluded: they could break out of a Traefik rule literal.
    [GeneratedRegex(@"^(/[A-Za-z0-9\-._~%!$&()*+,;=:@]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}
