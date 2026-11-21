using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

/// <summary>Transport of a <see cref="RepositoryUrl" />.</summary>
public enum RepositoryTransport
{
    Https = 0,
    Http = 1,

    /// <summary><c>ssh://[user@]host[:port]/path</c> or SCP-like <c>user@host:path</c>.</summary>
    Ssh = 2
}

/// <summary>
///     A Git repository location that is safe to pass to git as a positional argument (SEC-10): HTTP(S), <c>ssh://</c>
///     or SCP-like <c>user@host:path</c> only (no <c>file://</c>, <c>ext::</c> or other transports), no whitespace or
///     control characters, nothing that starts with <c>-</c>, and a host name or IP literal git cannot read as an
///     option. <see cref="ParseInput" /> additionally rejects credentials embedded in the URL; private repositories
///     use a Git source (DEP-08) whose credentials are injected at clone time and never stored in the URL.
/// </summary>
public readonly partial record struct RepositoryUrl
{
    public const int MaxLength = 2048;

    public RepositoryUrl(string value)
    {
        var normalized = DomainValidation.Required(value, nameof(RepositoryUrl), MaxLength);
        if (!TryAnalyze(normalized, out RepositoryUrlParts parts, out var error))
            throw new DomainException(error);

        Value = normalized;
        Transport = parts.Transport;
        Host = parts.Host;
        Port = parts.Port;
        UserName = parts.UserName;
        Path = parts.Path;
        HasEmbeddedPassword = parts.HasPassword;
    }

    public string Value { get; }

    public RepositoryTransport Transport { get; }

    /// <summary>Lower-case host name or IP literal (without brackets).</summary>
    public string Host { get; } = string.Empty;

    /// <summary>Explicit port, or <c>null</c> for the transport default.</summary>
    public int? Port { get; }

    /// <summary>User of an SSH URL (<c>git</c> in <c>git@github.com:org/repo.git</c>), or the HTTP user-info user.</summary>
    public string? UserName { get; }

    /// <summary>Repository path without the leading slash (<c>org/repo.git</c>).</summary>
    public string Path { get; } = string.Empty;

    /// <summary>True when an HTTP(S) URL embeds a password or token in its user-info.</summary>
    public bool HasEmbeddedPassword { get; }

    public bool IsSsh => Transport == RepositoryTransport.Ssh;

    /// <summary>The port git connects to (explicit port or the transport default).</summary>
    public int EffectivePort => Port ?? Transport switch
    {
        RepositoryTransport.Ssh => 22,
        RepositoryTransport.Http => 80,
        _ => 443
    };

    /// <summary>
    ///     Owner (first path segment) and repository name (last path segment without <c>.git</c>) of the path, or
    ///     <c>null</c> when the path has fewer than two segments.
    /// </summary>
    public (string Owner, string Name)? OwnerAndName
    {
        get
        {
            var segments = Path.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) return null;
            var name = segments[^1];
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            return (segments[0], name);
        }
    }

    /// <summary>
    ///     Validates user input (API, UI, import): the general rules plus no credentials embedded in the URL.
    /// </summary>
    public static RepositoryUrl ParseInput(string? value)
    {
        var url = new RepositoryUrl(value ?? string.Empty);
        if (url.HasEmbeddedPassword)
            throw new DomainException(
                "Repository URL must not contain a password or token. Use a Git source with HTTPS credentials instead.");
        return url;
    }

    public static bool TryParse(string? value, out RepositoryUrl url, out string error)
    {
        url = default;
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = "Repository URL is required.";
            return false;
        }

        if (trimmed.Length > MaxLength)
        {
            error = $"Repository URL must be {MaxLength} characters or fewer.";
            return false;
        }

        if (!TryAnalyze(trimmed, out _, out error)) return false;
        url = new RepositoryUrl(trimmed);
        return true;
    }

    /// <summary>
    ///     The URL in a form <see cref="Uri" /> accepts: SCP-like SSH locations become <c>ssh://user@host/path</c>.
    /// </summary>
    public Uri ToTransportUri()
    {
        if (Transport != RepositoryTransport.Ssh || Value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            return new Uri(Value, UriKind.Absolute);

        var host = Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host;
        var user = string.IsNullOrEmpty(UserName) ? string.Empty : UserName + "@";
        return new Uri($"ssh://{user}{host}/{Path}", UriKind.Absolute);
    }

    public override string ToString()
    {
        return Value;
    }

    private static bool TryAnalyze(string value, out RepositoryUrlParts parts, out string error)
    {
        parts = default;
        if (value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            error = "Repository URL must not contain whitespace or control characters.";
            return false;
        }

        if (value.StartsWith('-'))
        {
            error = "Repository URL must not start with a hyphen.";
            return false;
        }

        if (value.Contains("://", StringComparison.Ordinal))
            return TryAnalyzeUri(value, out parts, out error);

        // SCP-like syntax: user@host:path (upstream v4.3.19 accepts custom SSH users).
        Match scp = ScpPattern().Match(value);
        if (!scp.Success)
        {
            error = "Repository URL must be an HTTP(S) or SSH Git URL.";
            return false;
        }

        var host = scp.Groups["host"].Value.Trim('[', ']').ToLowerInvariant();
        var path = scp.Groups["path"].Value;
        if (!IsValidHost(host) || !IsValidPath(path))
        {
            error = "Repository URL has an invalid SSH host or path.";
            return false;
        }

        parts = new RepositoryUrlParts(RepositoryTransport.Ssh, host, null, scp.Groups["user"].Value,
            path.TrimStart('/'), false);
        error = string.Empty;
        return true;
    }

    private static bool TryAnalyzeUri(string value, out RepositoryUrlParts parts, out string error)
    {
        parts = default;
        // Uri silently resolves dot segments; a URL that needs them is refused instead.
        if (value.Contains("/../", StringComparison.Ordinal) || value.Contains("/./", StringComparison.Ordinal) ||
            value.EndsWith("/..", StringComparison.Ordinal) || value.Contains("%2e", StringComparison.OrdinalIgnoreCase))
        {
            error = "Repository URL must not contain dot path segments.";
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            error = "Repository URL must be an HTTP(S) or SSH Git URL.";
            return false;
        }

        RepositoryTransport? transport = uri.Scheme switch
        {
            "https" => RepositoryTransport.Https,
            "http" => RepositoryTransport.Http,
            "ssh" => RepositoryTransport.Ssh,
            _ => null
        };
        if (transport is null)
        {
            error = "Repository URL must be an HTTP(S) or SSH Git URL.";
            return false;
        }

        var host = uri.Host.Trim('[', ']').ToLowerInvariant();
        if (!IsValidHost(host))
        {
            error = "Repository URL has an invalid host.";
            return false;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (path.Length == 0 || !IsValidPath(path) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "Repository URL must name a repository path without a query or fragment.";
            return false;
        }

        string? user = null;
        var hasPassword = false;
        if (uri.UserInfo.Length > 0)
        {
            var separator = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
            user = Uri.UnescapeDataString(separator < 0 ? uri.UserInfo : uri.UserInfo[..separator]);
            hasPassword = separator >= 0;
            if (transport == RepositoryTransport.Ssh && (hasPassword || !UserPattern().IsMatch(user)))
            {
                error = "Repository URL has an invalid SSH user.";
                return false;
            }
        }

        parts = new RepositoryUrlParts(transport.Value, host, uri.IsDefaultPort ? null : uri.Port, user, path,
            hasPassword);
        error = string.Empty;
        return true;
    }

    private static bool IsValidHost(string host)
    {
        if (host.Length is 0 or > 253 || host.StartsWith('-')) return false;
        if (System.Net.IPAddress.TryParse(host, out _)) return true;
        return HostPattern().IsMatch(host);
    }

    private static bool IsValidPath(string path)
    {
        var trimmed = path.TrimStart('/');
        return trimmed.Length > 0 && !trimmed.StartsWith('-') &&
               !trimmed.Split('/').Any(segment => segment is ".." or "." || segment.StartsWith('-'));
    }

    [GeneratedRegex("^(?<user>[A-Za-z0-9_][A-Za-z0-9._-]{0,63})@(?<host>[A-Za-z0-9.-]+|\\[[0-9A-Fa-f:.]+\\]):(?<path>[^:].*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ScpPattern();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserPattern();

    [GeneratedRegex("^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    private readonly record struct RepositoryUrlParts(
        RepositoryTransport Transport,
        string Host,
        int? Port,
        string? UserName,
        string Path,
        bool HasPassword);
}
