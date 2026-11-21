using Vessel.Domain.Common;

namespace Vessel.Domain.Sources;

/// <summary>How a pinned Git SSH host key was recorded.</summary>
public enum GitKnownHostOrigin
{
    /// <summary>Trust on first use: recorded on the team's first SSH connection to the host, then enforced.</summary>
    FirstUse = 0,

    /// <summary>Entered (or confirmed) by an operator.</summary>
    Operator = 1
}

/// <summary>
///     A pinned SSH host key of a Git host for one team (DEP-08). Hosts whose keys are published by the provider
///     (GitHub, GitLab.com, Bitbucket) use <see cref="GitHostKeys.Published" /> and are never pinned here. Pins are
///     per team so one team's first connection can never decide which key another team trusts.
/// </summary>
public sealed class GitKnownHost : Entity<GitKnownHostId>
{
    public const int MaxHostLength = 253;
    public const int MaxKeyLength = 8192;

    private GitKnownHost()
    {
    }

    private GitKnownHost(GitKnownHostId id, TeamId teamId, string host, int port, string keyType, string key,
        GitKnownHostOrigin origin, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        Host = host;
        Port = port;
        KeyType = keyType;
        Key = key;
        Origin = origin;
    }

    public TeamId TeamId { get; private set; }

    /// <summary>Lower-case host name or IP literal.</summary>
    public string Host { get; private set; } = string.Empty;

    public int Port { get; private set; }

    public string KeyType { get; private set; } = string.Empty;

    /// <summary>Base64 public host key.</summary>
    public string Key { get; private set; } = string.Empty;

    public GitKnownHostOrigin Origin { get; private set; }

    public string KnownHostsLine => $"{GitHostKeys.HostPattern(Host, Port)} {KeyType} {Key}";

    public static GitKnownHost Create(TeamId teamId, string host, int port, string keyType, string key,
        GitKnownHostOrigin origin, DateTimeOffset now)
    {
        var normalizedHost = DomainValidation.Required(host, nameof(Host), MaxHostLength).Trim('[', ']')
            .ToLowerInvariant();
        if (port is < 1 or > 65535) throw new DomainException("SSH port must be 1-65535.");
        var line = $"{GitHostKeys.HostPattern(normalizedHost, port)} {keyType} {key}";
        if (!GitHostKeys.IsValidLine(line) || key.Length > MaxKeyLength)
            throw new DomainException("Host key must be an OpenSSH public key (key type and base64 key).");
        return new GitKnownHost(GitKnownHostId.New(), teamId, normalizedHost, port, keyType, key, origin, now);
    }

    public void Confirm(DateTimeOffset now)
    {
        Origin = GitKnownHostOrigin.Operator;
        Touch(now);
    }
}
