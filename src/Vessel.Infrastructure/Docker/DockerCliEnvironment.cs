using Vessel.Application.Docker;

namespace Vessel.Infrastructure.Docker;

/// <summary>
///     The process environment of a local container runtime CLI that reads user content (compose files, builds). It
///     starts from an allowlist instead of the daemon's environment, so a repository compose file such as
///     <c>${ConnectionStrings__Vessel}</c> or a build can never read control-plane secrets through interpolation, and a
///     daemon-level <c>COMPOSE_*</c> or <c>BUILDX_*</c> setting cannot change how user content is processed (SEC-10).
///     Build values (<see cref="BuildEnvironment" />) are added on top.
/// </summary>
public static class DockerCliEnvironment
{
    /// <summary>Non-secret variables the runtime CLIs need, copied from the daemon environment when present.</summary>
    public static readonly IReadOnlySet<string> AllowedInheritedVariables = new HashSet<string>(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
    {
        "PATH", "HOME", "USER", "LOGNAME", "LANG", "LC_ALL", "LC_CTYPE", "TZ", "TMPDIR",
        // Where the runtime is and how the CLI reaches it (configured by the operator, not by users).
        "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CONFIG", "DOCKER_CERT_PATH", "DOCKER_TLS_VERIFY", "DOCKER_TLS",
        "DOCKER_API_VERSION", "CONTAINER_HOST", "CONTAINER_CONNECTION", "CONTAINERS_CONF",
        "CONTAINERS_REGISTRIES_CONF", "CONTAINERS_STORAGE_CONF", "REGISTRY_AUTH_FILE", "XDG_RUNTIME_DIR",
        "XDG_CONFIG_HOME", "XDG_DATA_HOME",
        // Outbound proxy and trust settings of the host.
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy", "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        // Windows process essentials (development hosts).
        "SystemRoot", "SystemDrive", "ComSpec", "PATHEXT", "WINDIR", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "ProgramData", "ProgramFiles", "TEMP", "TMP"
    };

    public static IReadOnlyDictionary<string, string?> Build(BuildEnvironment? buildEnvironment = null,
        Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var environment = new Dictionary<string, string?>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in AllowedInheritedVariables)
        {
            var value = read(name);
            if (!string.IsNullOrEmpty(value)) environment[name] = value;
        }

        if (!environment.ContainsKey("PATH") && !OperatingSystem.IsWindows())
            environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
        environment.TryAdd("LANG", "C.UTF-8");
        foreach (var (key, value) in buildEnvironment?.Values ?? [])
            environment[key] = value;
        return environment;
    }
}
