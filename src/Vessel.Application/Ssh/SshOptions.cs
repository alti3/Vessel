using Vessel.Domain.Servers;

namespace Vessel.Application.Ssh;

/// <summary>Instance-wide SSH settings (<c>Vessel:Ssh</c>).</summary>
public sealed class SshOptions
{
    public const string SectionName = "Vessel:Ssh";

    public const string DefaultHostRuntimeDirectory = "/var/lib/vessel/ssh";

    /// <summary>
    ///     <c>enforce</c> (default), <c>warn</c> or <c>off</c>. New servers start with this mode. <c>off</c> is an
    ///     instance-wide opt-out that disables host key checking for every server. A team may make a server stricter
    ///     than this value; only an instance administrator may make it looser.
    /// </summary>
    public string HostKeyChecking { get; set; } = "enforce";

    /// <summary>
    ///     Owned directory for per-connection private key files and per-server known_hosts files (created 0700, files
    ///     0600). Defaults to <c>{AppContext.BaseDirectory}/storage/ssh</c>; host-native installs should use
    ///     <see cref="DefaultHostRuntimeDirectory" />.
    /// </summary>
    public string? RuntimeDirectory { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 15;

    public int ServerAliveIntervalSeconds { get; set; } = 15;

    /// <summary>Minimum time between two host key scans of the same server (each scan is a failed login).</summary>
    public int HostKeyScanIntervalSeconds { get; set; } = 600;

    public SshHostKeyCheckingMode ResolveInstanceMode()
    {
        return ParseMode(HostKeyChecking) ?? SshHostKeyCheckingMode.Enforce;
    }

    public static SshHostKeyCheckingMode? ParseMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "enforce" => SshHostKeyCheckingMode.Enforce,
            "warn" => SshHostKeyCheckingMode.Warn,
            "off" => SshHostKeyCheckingMode.Off,
            _ => null
        };
    }

    /// <summary>
    ///     The mode used for a connection: an instance-wide <c>off</c> disables checking; otherwise the server's own
    ///     mode applies (new servers start at the instance mode, and only instance administrators may loosen it).
    /// </summary>
    public static SshHostKeyCheckingMode Effective(SshHostKeyCheckingMode instanceMode, SshHostKeyCheckingMode serverMode)
    {
        return instanceMode == SshHostKeyCheckingMode.Off ? SshHostKeyCheckingMode.Off : serverMode;
    }
}
