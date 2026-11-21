using System.Globalization;
using Vessel.Application.Ssh;

namespace Vessel.Infrastructure.Ssh;

/// <summary>
///     Builds OpenSSH argument vectors. Every connection gets the same fixed, hardened options (SEC-12): no user or
///     system ssh_config (<c>-F /dev/null</c>), batch mode, only the given identity, no password or keyboard-interactive
///     login, no agent/X11/port forwarding, connect and keep-alive timeouts, and host key verification against a
///     Vessel-managed known_hosts file. The destination always follows <c>--</c> so it can never be read as an option.
/// </summary>
public static class SshArguments
{
    public const int DefaultConnectTimeoutSeconds = 15;
    public const int DefaultServerAliveIntervalSeconds = 15;

    public static List<string> ForSsh(SshConnectionOptions options, string? remoteCommand, bool allocateTty = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> args = allocateTty ? ["-tt", "-o", "RequestTTY=force"] : ["-T", "-o", "RequestTTY=no"];
        args.AddRange(CommonOptions(options));
        args.AddRange(["-p", Port(options), "--", Destination(options)]);
        if (remoteCommand is not null)
            args.Add(remoteCommand);
        return args;
    }

    /// <summary><c>scp</c> takes the port with an upper-case <c>-P</c> (lower-case <c>-p</c> preserves file times).</summary>
    public static List<string> ForScp(SshConnectionOptions options, string source, string destination)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        var args = new List<string> { "-q" };
        args.AddRange(CommonOptions(options));
        args.AddRange(["-P", Port(options), "--", source, destination]);
        return args;
    }

    public static string RemotePath(SshConnectionOptions options, string remotePath)
    {
        return $"{Destination(options)}:{remotePath}";
    }

    private static IEnumerable<string> CommonOptions(SshConnectionOptions options)
    {
        var connectTimeout = (int)Math.Ceiling((options.ConnectTimeout ?? TimeSpan.FromSeconds(DefaultConnectTimeoutSeconds))
            .TotalSeconds);
        var aliveInterval =
            (int)Math.Ceiling((options.ServerAliveInterval ?? TimeSpan.FromSeconds(DefaultServerAliveIntervalSeconds))
                .TotalSeconds);
        List<string> args =
        [
            "-F", "/dev/null",
            "-o", "BatchMode=yes",
            "-o", "IdentitiesOnly=yes",
            "-o", "PasswordAuthentication=no",
            "-o", "KbdInteractiveAuthentication=no",
            "-o", "ClearAllForwardings=yes",
            "-o", "ForwardAgent=no",
            "-o", "ForwardX11=no",
            "-o", "PermitLocalCommand=no",
            "-o", "UpdateHostKeys=no",
            "-o", $"ConnectTimeout={Math.Max(1, connectTimeout).ToString(CultureInfo.InvariantCulture)}",
            "-o", $"ServerAliveInterval={Math.Max(1, aliveInterval).ToString(CultureInfo.InvariantCulture)}",
            "-o", "ServerAliveCountMax=3",
            "-o", "LogLevel=ERROR"
        ];
        args.AddRange(HostKeyOptions(options));
        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath))
            args.AddRange(["-i", options.PrivateKeyPath]);
        return args;
    }

    private static IEnumerable<string> HostKeyOptions(SshConnectionOptions options)
    {
        var hasPinnedFile = !string.IsNullOrWhiteSpace(options.KnownHostsPath);
        switch (options.HostIdentityPolicy)
        {
            case SshHostIdentityPolicy.InsecureSkipValidation:
                return
                [
                    "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=/dev/null", "-o",
                    "GlobalKnownHostsFile=/dev/null"
                ];
            case SshHostIdentityPolicy.WarnOnMismatch when hasPinnedFile:
                return PinnedFile(options, "no");
            case SshHostIdentityPolicy.WarnOnMismatch:
                return
                [
                    "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=/dev/null", "-o",
                    "GlobalKnownHostsFile=/dev/null"
                ];
            case SshHostIdentityPolicy.TrustOnFirstUse when hasPinnedFile:
                return PinnedFile(options, "accept-new");
            case SshHostIdentityPolicy.TrustOnFirstUse:
                return ["-o", "StrictHostKeyChecking=accept-new"];
            default:
                return hasPinnedFile ? PinnedFile(options, "yes") : ["-o", "StrictHostKeyChecking=yes"];
        }
    }

    private static List<string> PinnedFile(SshConnectionOptions options, string strictness)
    {
        List<string> args =
        [
            "-o", $"StrictHostKeyChecking={strictness}",
            "-o", $"UserKnownHostsFile={options.KnownHostsPath}",
            "-o", "GlobalKnownHostsFile=/dev/null"
        ];
        if (!string.IsNullOrWhiteSpace(options.HostKeyAlias))
            args.AddRange(["-o", $"HostKeyAlias={options.HostKeyAlias}"]);
        return args;
    }

    private static string Port(SshConnectionOptions options)
    {
        if (options.Port is < 1 or > 65535)
            throw new InvalidOperationException("SSH port is invalid.");
        return options.Port.ToString(CultureInfo.InvariantCulture);
    }

    private static string Destination(SshConnectionOptions options)
    {
        if (!SshTargetValidator.IsSafeHost(options.Host.Trim('[', ']')) && !SshTargetValidator.IsSafeHost(options.Host))
            throw new InvalidOperationException("SSH host is not a valid host name or address.");
        if (!SshTargetValidator.IsSafeUser(options.UserName))
            throw new InvalidOperationException("SSH user is invalid.");
        return $"{options.UserName}@{options.Host}";
    }
}
