using Vessel.Application.EnvironmentVariables;
using Vessel.Domain.EnvironmentVariables;

namespace Vessel.Application.Deployments.BuildPacks;

/// <summary>Build values that reach an image build, and the reasons other build-time variables were skipped.</summary>
public sealed record BuildVariableSelection(
    IReadOnlyList<KeyValuePair<string, string>> Values,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyDictionary<string, string> AsDictionary()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in Values) values[key] = value;
        return values;
    }
}

/// <summary>
///     Which build-time variables are passed to a build (DEP-09). Only variables marked "available at build time" are
///     candidates (never runtime-only values). A build variable is skipped, with a warning that names it but never its
///     value, when:
///     <list type="bullet">
///         <item>its name has a dot (upstream <c>withoutDottedBuildVariables</c>: not a shell name; still passed to the container);</item>
///         <item>
///             it is reserved for the container runtime client or the process loader (upstream
///             <c>without_reserved_docker_client_variables</c>, v4.2.0 #10840, v4.7.0 #9726): build values are set in the
///             runtime CLI's environment, so a build variable named <c>DOCKER_HOST</c>, <c>DOCKER_CONFIG</c>,
///             <c>HOME</c> or <c>LD_PRELOAD</c> would redirect or hijack the CLI itself;
///         </item>
///         <item>it is a preserved Compose null (unset).</item>
///     </list>
/// </summary>
public static class BuildVariablePolicy
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Upstream DOCKER_CLIENT_ENV_KEYS plus the remaining Docker/Podman client switches.
        "BUILDKIT_HOST", "BUILDX_BUILDER", "BUILDX_CONFIG", "DOCKER_API_VERSION", "DOCKER_BUILDKIT",
        "DOCKER_CERT_PATH", "DOCKER_CLI_EXPERIMENTAL", "DOCKER_CONFIG", "DOCKER_CONTEXT", "DOCKER_HOST",
        "DOCKER_TLS", "DOCKER_TLS_VERIFY", "DOCKER_DEFAULT_PLATFORM", "DOCKER_CONTENT_TRUST",
        "DOCKER_CONTENT_TRUST_SERVER", "DOCKER_CUSTOM_HEADERS", "DOCKER_CLI_HINTS", "CONTAINER_HOST",
        "CONTAINER_CONNECTION", "CONTAINERS_CONF", "CONTAINERS_REGISTRIES_CONF", "CONTAINERS_STORAGE_CONF",
        "REGISTRY_AUTH_FILE",
        // The process and shell that run the CLI.
        "PATH", "HOME", "SHELL", "IFS", "ENV", "BASH_ENV", "CDPATH", "PS4", "TMPDIR", "USER", "LOGNAME", "PWD",
        "OLDPWD", "SSH_AUTH_SOCK", "SSL_CERT_FILE", "SSL_CERT_DIR", "GODEBUG", "GOFLAGS", "GOMAXPROCS",
        "GOTRACEBACK", "GOGC", "GOMEMLIMIT"
    };

    private static readonly string[] ReservedPrefixes = ["BUILDX_", "LD_", "DYLD_", "XDG_"];

    public static bool IsReserved(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ReservedNames.Contains(key) ||
               ReservedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static BuildVariableSelection Select(IEnumerable<ResolvedVariable> buildVariables, string builder)
    {
        ArgumentNullException.ThrowIfNull(buildVariables);
        var values = new List<KeyValuePair<string, string>>();
        var warnings = new List<string>();
        foreach (ResolvedVariable variable in buildVariables)
        {
            if (variable.Value is null) continue;
            if (!EnvironmentVariableKey.IsShellName(variable.Key))
            {
                warnings.Add(
                    $"Build-time variable {variable.Key} has a dot in its name, which {builder} cannot pass to the build. " +
                    $"It is skipped during the build but still passed to the container. Suggested name: {variable.Key.Replace('.', '_')}.");
                continue;
            }

            if (IsReserved(variable.Key))
            {
                warnings.Add(
                    $"Build-time variable {variable.Key} is reserved for the container runtime client and is not passed to the build.");
                continue;
            }

            values.Add(new KeyValuePair<string, string>(variable.Key, variable.Value));
        }

        return new BuildVariableSelection(values, warnings);
    }
}
