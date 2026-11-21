using System.Globalization;
using Vessel.Domain.Servers;

namespace Vessel.Application.Servers.Validation;

public sealed record OsRelease(string? Id, string? IdLike, string? VersionId, string? PrettyName)
{
    public IEnumerable<string> Family()
    {
        if (!string.IsNullOrWhiteSpace(Id)) yield return Id.ToLowerInvariant();
        foreach (var like in (IdLike ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            yield return like.ToLowerInvariant();
    }
}

/// <summary>
///     Pure rules of server validation (upstream Coolify <c>ValidateServer</c> / <c>validateDockerEngineVersion</c>):
///     <c>/etc/os-release</c> parsing with <c>ID_LIKE</c> derivatives, supported OS families, the minimum runtime version
///     gate, and install guidance per package manager.
/// </summary>
public static class ServerValidationRules
{
    public const int MinimumDockerMajor = 24;
    public const int MinimumPodmanMajor = 4;

    private static readonly string[] SupportedFamilies =
    [
        "debian", "ubuntu", "raspbian", "rhel", "centos", "fedora", "rocky", "almalinux", "ol", "amzn", "sles", "suse",
        "opensuse", "opensuse-leap", "opensuse-tumbleweed", "alpine", "arch", "manjaro", "endeavouros"
    ];

    public static OsRelease ParseOsRelease(string? content)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in (content ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            values[key] = value.Replace("\\\"", "\"", StringComparison.Ordinal);
        }

        return new OsRelease(Get("ID"), Get("ID_LIKE"), Get("VERSION_ID"), Get("PRETTY_NAME"));

        string? Get(string key)
        {
            return values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
        }
    }

    public static bool IsSupported(OsRelease release)
    {
        return release.Family().Any(family => SupportedFamilies.Contains(family, StringComparer.Ordinal));
    }

    /// <summary>
    ///     Returns null when <paramref name="version" /> satisfies the minimum for <paramref name="runtime" />, otherwise
    ///     the reason. Docker Engine must be 24 or newer; Podman 4 or newer.
    /// </summary>
    public static string? CheckRuntimeVersion(ContainerRuntimeKind runtime, string? version)
    {
        var minimum = runtime == ContainerRuntimeKind.Podman ? MinimumPodmanMajor : MinimumDockerMajor;
        var name = runtime == ContainerRuntimeKind.Podman ? "Podman" : "Docker Engine";
        var major = Major(version);
        if (major is null) return $"Could not read the {name} version of the server.";
        return major.Value >= minimum
            ? null
            : $"{name} {version} is too old. Vessel requires {name} {minimum} or newer; upgrade it on the server.";
    }

    public static int? Major(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var text = version.Trim().TrimStart('v', 'V');
        var end = 0;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        return end > 0 && int.TryParse(text[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            ? major
            : null;
    }

    /// <summary>
    ///     Install commands for Docker Engine on the detected package manager, as a POSIX script run with root privileges.
    ///     Returns null when Vessel cannot install Docker on this system.
    /// </summary>
    public static string? DockerInstallScript(string? packageManager)
    {
        return packageManager switch
        {
            "apt-get" or "dnf" or "yum" or "zypper" =>
                "set -eu; tmp=$(mktemp); trap 'rm -f \"$tmp\"' EXIT; " +
                "if command -v curl >/dev/null 2>&1; then curl -fsSL https://get.docker.com -o \"$tmp\"; " +
                "else wget -qO \"$tmp\" https://get.docker.com; fi; sh \"$tmp\"; " +
                "(systemctl enable --now docker >/dev/null 2>&1 || true)",
            "apk" =>
                "set -eu; apk add --no-cache docker docker-cli-compose; rc-update add docker default; " +
                "(service docker start || rc-service docker start)",
            "pacman" =>
                "set -eu; pacman -Sy --noconfirm --needed docker docker-compose; systemctl enable --now docker",
            _ => null
        };
    }

    public static string InstallGuidance(string? packageManager, ContainerRuntimeKind runtime)
    {
        if (runtime == ContainerRuntimeKind.Podman)
            return "Install Podman 4 or newer with the package manager of the server, then validate again.";
        return packageManager switch
        {
            "apt-get" or "dnf" or "yum" or "zypper" =>
                "Install Docker Engine 24 or newer: curl -fsSL https://get.docker.com | sudo sh — or validate again with installDocker=true to let Vessel run it.",
            "apk" =>
                "Install Docker: apk add docker docker-cli-compose && rc-update add docker default && service docker start — or validate again with installDocker=true.",
            "pacman" =>
                "Install Docker: pacman -Sy docker docker-compose && systemctl enable --now docker — or validate again with installDocker=true.",
            _ => "Install Docker Engine 24 or newer (https://docs.docker.com/engine/install/), then validate again."
        };
    }
}
