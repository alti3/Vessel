namespace Vessel.Application.Proxy;

/// <summary>
///     Proxy files on a server, relative to the server file channel root (for example <c>/var/lib/vessel</c>).
/// </summary>
public static class ProxyLayout
{
    public const string Directory = "proxy";
    public const string StaticConfigurationFile = "proxy/traefik.yml";
    public const string ComposeFile = "proxy/docker-compose.yml";
    public const string DynamicDirectory = "proxy/dynamic";
    public const string DynamicConfigurationFile = "proxy/dynamic/vessel-routes.yml";
    public const string AcmeDirectory = "proxy/acme";
    public const string AcmeStorageFile = "proxy/acme/acme.json";

    /// <summary>
    ///     Backups of <see cref="AcmeStorageFile" /> taken before every write (owner-only). Outside the ACME directory so
    ///     they are not bind-mounted into the proxy container.
    /// </summary>
    public const string AcmeBackupDirectory = "proxy/acme-backups";

    /// <summary>Relative path of a user dynamic configuration file (name validated by the domain rule).</summary>
    public static string DynamicFile(string fileName)
    {
        return $"{DynamicDirectory}/{fileName}";
    }
}
