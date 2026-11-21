using System.Globalization;
using System.Text.RegularExpressions;

namespace Vessel.Domain.Sources;

/// <summary>A parsed <c>known_hosts</c> line without markers, hashed hosts or comments.</summary>
public sealed record KnownHostsEntry(IReadOnlyList<string> Hosts, string KeyType, string Key)
{
    public string Line => $"{string.Join(',', Hosts)} {KeyType} {Key}";
}

/// <summary>
///     SSH host keys for Git over SSH (DEP-08, upstream v4.7.0 <c>GIT_PINNED_HOST_KEYS</c>). Connections to the large
///     providers accept only their published keys; other hosts are pinned per team on first use
///     (<see cref="GitKnownHost" />) and enforced afterwards. Update <see cref="Published" /> when a provider rotates
///     its host keys.
/// </summary>
public static partial class GitHostKeys
{
    public const int DefaultSshPort = 22;

    /// <summary>
    ///     Published host keys of GitHub, GitLab.com and Bitbucket, including their SSH-over-443 endpoints.
    ///     Sources: docs.github.com (githubs-ssh-key-fingerprints, api.github.com/meta), docs.gitlab.com
    ///     (gitlab_com ssh known_hosts entries), bitbucket.org/site/ssh.
    /// </summary>
    public static readonly IReadOnlyList<string> Published =
    [
        "github.com,ssh.github.com,[ssh.github.com]:443 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl",
        "github.com,ssh.github.com,[ssh.github.com]:443 ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBEmKSENjQEezOmxkZMy7opKgwFB9nkt5YRrYMjNuG5N87uRgg6CLrbo5wAdT/y6v0mKV0U2w0WZ2YB/++Tpockg=",
        "github.com,ssh.github.com,[ssh.github.com]:443 ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQCj7ndNxQowgcQnjshcLrqPEiiphnt+VTTvDP6mHBL9j1aNUkY4Ue1gvwnGLVlOhGeYrnZaMgRK6+PKCUXaDbC7qtbW8gIkhL7aGCsOr/C56SJMy/BCZfxd1nWzAOxSDPgVsmerOBYfNqltV9/hWCqBywINIR+5dIg6JTJ72pcEpEjcYgXkE2YEFXV1JHnsKgbLWNlhScqb2UmyRkQyytRLtL+38TGxkxCflmO+5Z8CSSNY7GidjMIZ7Q4zMjA2n1nGrlTDkzwDCsw+wqFPGQA179cnfGWOWRVruj16z6XyvxvjJwbz0wQZ75XK5tKSb7FNyeIEs4TT4jk+S4dhPeAUC5y+bDYirYgM4GC7uEnztnZyaVWQ7B381AK4Qdrwt51ZqExKbQpTUNn+EjqoTwvqNj4kqx5QUCI0ThS/YkOxJCXmPUWZbhjpCg56i+2aB6CmK2JGhn57K5mj0MNdBXA4/WnwH6XoPWJzK5Nyu2zB3nAZp+S5hpQs+p1vN1/wsjk=",
        "gitlab.com,[altssh.gitlab.com]:443 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAfuCHKVTjquxvt6CM6tdG4SLp1Btn/nOeHHE5UOzRdf",
        "gitlab.com,[altssh.gitlab.com]:443 ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBFSMqzJeV9rUzU4kWitGjeR4PWSa29SPqJ1fVkhtj3Hw9xjLVXVYrU9QlYWrOLXBpQ6KWjbjTDTdDkoohFzgbEY=",
        "gitlab.com,[altssh.gitlab.com]:443 ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQCsj2bNKTBSpIYDEGk9KxsGh3mySTRgMtXL583qmBpzeQ+jqCMRgBqB98u3z++J1sKlXHWfM9dyhSevkMwSbhoR8XIq/U0tCNyokEi/ueaBMCvbcTHhO7FcwzY92WK4Yt0aGROY5qX2UKSeOvuP4D6TPqKF1onrSzH9bx9XUf2lEdWT/ia1NEKjunUqu1xOB/StKDHMoX4/OKyIzuS0q/T1zOATthvasJFoPrAjkohTyaDUz2LN5JoH839hViyEG82yB+MjcFV5MU3N1l1QL3cVUCh93xSaua1N85qivl+siMkPGbO5xR/En4iEY6K2XPASUEMaieWVNTRCtJ4S8H+9",
        "bitbucket.org,[altssh.bitbucket.org]:443 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIIazEu89wgQZ4bqs3d63QSMzYVa0MuJ2e2gKTKqu+UUO",
        "bitbucket.org,[altssh.bitbucket.org]:443 ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBPIQmuzMBuKdWeF4+a2sjSSpBK0iqitSQ+5BM9KhpexuGt20JpTVM7u5BDZngncgrqDMbWdxMWWOGtZ9UgbqgZE=",
        "bitbucket.org,[altssh.bitbucket.org]:443 ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQDQeJzhupRu0u0cdegZIa8e86EG2qOCsIsD1Xw0xSeiPDlCr7kq97NLmMbpKTX6Esc30NuoqEEHCuc7yWtwp8dI76EEEB1VqY9QJq6vk+aySyboD5QF61I/1WeTwu+deCbgKMGbUijeXhtfbxSxm6JwGrXrhBdofTsbKRUsrN1WoNgUa8uqN1Vx6WAJw1JHPhglEGGHea6QICwJOAr/6mrui/oB7pkaWKHj3z7d1IC4KWLtY47elvjbaTlkN04Kc/5LFEirorGYVbt15kAUlqGM65pk6ZBxtaO3+30LVlORZkxOh+LKL/BvbZ/iRNhItLqNyieoQj/uh/7Iv4uyH/cV/0b4WDSd3DptigWq84lJubb9t/DnZlrJazxyDCulTmKdOR7vs9gMTo+uoIrPSb8ScTtvw65+odKAlBj59dhnVp9zd7QUojOpXlL62Aw56U4oO+FALuevvMjiWeavKhJqlR7i5n9srYcrNV7ttmDw7kf/97P5zauIhxcjX+xHv4M="
    ];

    /// <summary>Host key types git connections accept (no DSA).</summary>
    public static readonly IReadOnlySet<string> KeyTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521",
        "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com"
    };

    /// <summary><c>host</c> for port 22, otherwise <c>[host]:port</c> (OpenSSH known_hosts syntax).</summary>
    public static string HostPattern(string host, int port)
    {
        var normalized = host.Trim('[', ']').ToLowerInvariant();
        return port == DefaultSshPort
            ? normalized
            : string.Create(CultureInfo.InvariantCulture, $"[{normalized}]:{port}");
    }

    /// <summary>A known_hosts line without markers, hashed host names or comments: <c>host[,host] key-type key</c>.</summary>
    public static bool IsValidLine(string? line)
    {
        return line is not null && LinePattern().IsMatch(line) && TryParse(line, out _);
    }

    public static bool TryParse(string? line, out KnownHostsEntry entry)
    {
        entry = null!;
        if (line is null) return false;
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !KeyTypes.Contains(parts[1])) return false;
        if (!LinePattern().IsMatch(string.Join(' ', parts))) return false;
        try
        {
            if (Convert.FromBase64String(parts[2]).Length < 16) return false;
        }
        catch (FormatException)
        {
            return false;
        }

        entry = new KnownHostsEntry(parts[0].ToLowerInvariant().Split(','), parts[1], parts[2]);
        return true;
    }

    /// <summary>True when the provider publishes the host keys of <paramref name="host" /> on <paramref name="port" />.</summary>
    public static bool HasPublishedKeys(string host, int port)
    {
        return PublishedFor(host, port).Count > 0;
    }

    /// <summary>Published lines for <paramref name="host" />:<paramref name="port" />.</summary>
    public static IReadOnlyList<string> PublishedFor(string host, int port)
    {
        var pattern = HostPattern(host, port);
        return Published.Where(line => TryParse(line, out KnownHostsEntry entry) &&
                                       entry.Hosts.Contains(pattern, StringComparer.Ordinal))
            .ToArray();
    }

    [GeneratedRegex(
        "^(?:[A-Za-z0-9._:-]+|\\[[A-Za-z0-9._:-]+\\]:[0-9]{1,5})(?:,(?:[A-Za-z0-9._:-]+|\\[[A-Za-z0-9._:-]+\\]:[0-9]{1,5}))* [A-Za-z0-9@.-]+ [A-Za-z0-9+/]+={0,2}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();
}
