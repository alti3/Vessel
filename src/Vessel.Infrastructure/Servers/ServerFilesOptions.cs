namespace Vessel.Infrastructure.Servers;

/// <summary>Owned root directories for files Vessel delivers to servers (proxy configuration and similar).</summary>
public sealed class ServerFilesOptions
{
    public const string SectionName = "Vessel:ServerFiles";

    public const string DefaultRemoteRootDirectory = "/var/lib/vessel";

    /// <summary>
    ///     Root on the control-plane host for <c>Local</c>/<c>DockerSocket</c> servers. Defaults to
    ///     <c>{AppContext.BaseDirectory}/storage/server-files</c>; host-native installs should set <c>/var/lib/vessel</c>.
    /// </summary>
    public string? LocalRootDirectory { get; set; }

    /// <summary>Absolute POSIX root on SSH servers.</summary>
    public string RemoteRootDirectory { get; set; } = DefaultRemoteRootDirectory;

    public string ResolveLocalRoot()
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(LocalRootDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "storage", "server-files")
            : LocalRootDirectory);
    }
}
