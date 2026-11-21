using Vessel.Application.Processes;
using Vessel.Application.Ssh;

namespace Vessel.Infrastructure.Ssh;

/// <summary>
///     Reads the host key a server presents (upstream Coolify's TOFU probe): <c>ssh</c> with every login method off and
///     <c>StrictHostKeyChecking=accept-new</c> against an empty scratch known_hosts file. OpenSSH stores the host key
///     during the key exchange, then the login fails by design; no key is offered and no command runs.
/// </summary>
public sealed class SshHostKeyScanner(IProcessRunner processRunner, SshRuntimeFiles files) : ISshHostKeyScanner
{
    private const int ConnectTimeoutSeconds = 10;

    public async Task<SshHostKeyScanResult> ScanAsync(
        string host,
        int port,
        string user,
        string hostKeyAlias,
        CancellationToken cancellationToken = default)
    {
        if (!SshTargetValidator.IsSafeHost(host) || !SshTargetValidator.IsSafeUser(user) || port is < 1 or > 65535)
            return new SshHostKeyScanResult(null, "Server address or user contains characters that are not allowed for SSH.");

        var scanFile = files.NewScanFile();
        try
        {
            List<string> args = BuildArguments(host, port, user, hostKeyAlias, scanFile);
            ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
                "ssh",
                args,
                Timeout: TimeSpan.FromSeconds(ConnectTimeoutSeconds + 5)), cancellationToken);
            var content = File.Exists(scanFile) ? await File.ReadAllTextAsync(scanFile, cancellationToken) : null;
            var keys = SshKeyFormats.NormalizeHostKeys(content);
            if (keys is not null) return new SshHostKeyScanResult(keys, null);
            var error = result.ExitInfo.TimedOut ? "The connection timed out." : result.StandardError.Trim();
            return new SshHostKeyScanResult(null,
                string.IsNullOrEmpty(error) ? "The server did not present a host key." : Truncate(error, 300));
        }
        finally
        {
            files.Delete(scanFile);
        }
    }

    public static List<string> BuildArguments(string host, int port, string user, string hostKeyAlias, string scanFile)
    {
        return
        [
            "-T",
            "-F", "/dev/null",
            "-o", $"HostKeyAlias={hostKeyAlias}",
            "-o", $"UserKnownHostsFile={scanFile}",
            "-o", "GlobalKnownHostsFile=/dev/null",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "HashKnownHosts=no",
            "-o", "BatchMode=yes",
            "-o", "PubkeyAuthentication=no",
            "-o", "PasswordAuthentication=no",
            "-o", "KbdInteractiveAuthentication=no",
            "-o", "HostbasedAuthentication=no",
            "-o", "ClearAllForwardings=yes",
            "-o", "ForwardAgent=no",
            "-o", $"ConnectTimeout={ConnectTimeoutSeconds}",
            "-o", "LogLevel=ERROR",
            "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--",
            $"{user}@{host}",
            "true"
        ];
    }

    private static string Truncate(string value, int length)
    {
        return value.Length <= length ? value : value[..length];
    }
}
