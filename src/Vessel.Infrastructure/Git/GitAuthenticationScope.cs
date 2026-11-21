using System.Text;
using Vessel.Application.Git;
using Vessel.Domain.Sources;

namespace Vessel.Infrastructure.Git;

/// <summary>
///     Materializes <see cref="GitAuthentication" /> for one git operation (DEP-08) in a private (<c>0700</c>)
///     temporary directory that is deleted when the scope is disposed:
///     <list type="bullet">
///         <item>
///             HTTPS: a <c>GIT_ASKPASS</c> helper that reads the username and token from the git process environment
///             and answers only prompts for the bound host, so the token never appears in the URL, the command line,
///             <c>.git/config</c> or logs, and a submodule or redirect to another host never receives it. Credential
///             helpers configured on the host are disabled for the operation.
///         </item>
///         <item>SSH: the deploy key as a <c>0600</c> identity file with <c>IdentitiesOnly</c>.</item>
///         <item>
///             Host trust: a dedicated <c>known_hosts</c> file with only the trusted keys and
///             <c>StrictHostKeyChecking=yes</c>, or <c>accept-new</c> for trust on first use, after which the recorded
///             key is reported for pinning.
///         </item>
///     </list>
/// </summary>
public sealed class GitAuthenticationScope : IDisposable
{
    /// <summary>
    ///     The askpass helper. Prompts look like <c>Username for 'https://host': </c> and
    ///     <c>Password for 'https://user@host': </c>; anything else (another host, another scheme) gets no answer.
    /// </summary>
    public const string AskPassScript = """
                                        #!/bin/sh
                                        # Vessel git credential prompt helper. Secrets come from the environment of the
                                        # git process only and are answered for the bound host only.
                                        h="$VESSEL_GIT_HOST"
                                        u="$VESSEL_GIT_USERNAME"
                                        case "$1" in
                                          "Username for 'https://$h': ")
                                            printf '%s\n' "$u" ;;
                                          "Password for 'https://$h': "|"Password for 'https://$u@$h': ")
                                            printf '%s\n' "$VESSEL_GIT_PASSWORD" ;;
                                          *)
                                            exit 1 ;;
                                        esac
                                        """;

    private readonly GitAuthentication? _authentication;
    private readonly string? _directory;
    private readonly string? _knownHostsPath;

    private GitAuthenticationScope(GitAuthentication? authentication, string? directory, string? knownHostsPath,
        IReadOnlyDictionary<string, string?> environment)
    {
        _authentication = authentication;
        _directory = directory;
        _knownHostsPath = knownHostsPath;
        Environment = environment;
    }

    /// <summary>Environment entries the git process needs (empty for anonymous operations).</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }

    public IReadOnlyList<string> SecretValues => _authentication?.SecretValues ?? [];

    public string? DirectoryPath => _directory;

    public void Dispose()
    {
        if (_directory is null) return;
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best effort; the directory is private to the daemon user and lives in the temp directory.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static GitAuthenticationScope Create(GitAuthentication? authentication, string? temporaryRoot = null)
    {
        if (authentication is null ||
            (authentication.Https is null && authentication.Ssh is null && authentication.HostTrust is null))
            return new GitAuthenticationScope(null, null, null, new Dictionary<string, string?>());

        var root = Path.GetFullPath(temporaryRoot ?? Path.GetTempPath());
        var directory = Path.Combine(root, "vessel-git-auth-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(directory);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Never consult (or store into) credential helpers configured on the host.
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "credential.helper",
            ["GIT_CONFIG_VALUE_0"] = string.Empty,
            // Credentialed operations (including submodules) use only HTTPS and SSH transports.
            ["GIT_ALLOW_PROTOCOL"] = "https:ssh"
        };

        string? knownHostsPath = null;
        try
        {
            if (authentication.Https is { } https)
            {
                ValidateHttps(https);
                var askPass = Path.Combine(directory, "askpass.sh");
                WritePrivateFile(askPass, AskPassScript.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n",
                    executable: true);
                environment["GIT_ASKPASS"] = askPass;
                environment["SSH_ASKPASS"] = null;
                environment["VESSEL_GIT_HOST"] = https.Host.ToLowerInvariant();
                environment["VESSEL_GIT_USERNAME"] = https.Username;
                environment["VESSEL_GIT_PASSWORD"] = https.Password;
            }

            var ssh = new StringBuilder("ssh -o BatchMode=yes -o PasswordAuthentication=no");
            var customSsh = false;
            if (authentication.Ssh is { } key)
            {
                var identity = Path.Combine(directory, "identity");
                var material = key.PrivateKey.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n";
                WritePrivateFile(identity, material, executable: false);
                ssh.Append(" -o IdentitiesOnly=yes -i ").Append(Quote(identity));
                customSsh = true;
            }

            if (authentication.HostTrust is { } trust)
            {
                knownHostsPath = Path.Combine(directory, "known_hosts");
                var lines = trust.KnownHostLines.Where(GitHostKeys.IsValidLine).ToArray();
                WritePrivateFile(knownHostsPath, lines.Length == 0 ? string.Empty : string.Join('\n', lines) + "\n",
                    executable: false);
                var strict = lines.Length == 0 && trust.AllowFirstUse ? "accept-new" : "yes";
                ssh.Append(" -o UserKnownHostsFile=").Append(Quote(knownHostsPath))
                    .Append(" -o GlobalKnownHostsFile=/dev/null -o HashKnownHosts=no -o UpdateHostKeys=no")
                    .Append(" -o StrictHostKeyChecking=").Append(strict);
                customSsh = true;
            }

            if (customSsh) environment["GIT_SSH_COMMAND"] = ssh.ToString();
        }
        catch
        {
            TryDelete(directory);
            throw;
        }

        return new GitAuthenticationScope(authentication, directory, knownHostsPath, environment);
    }

    /// <summary>
    ///     After a successful trust-on-first-use connection, reports the host keys ssh recorded for the trusted host so
    ///     the caller pins them. Keys recorded for other hosts (for example submodules) are ignored.
    /// </summary>
    public async Task ReportFirstUseAsync(CancellationToken cancellationToken)
    {
        GitSshHostTrust? trust = _authentication?.HostTrust;
        if (trust is null || !trust.AllowFirstUse || trust.OnFirstUse is null || _knownHostsPath is null) return;
        if (trust.KnownHostLines.Count > 0 || !File.Exists(_knownHostsPath)) return;

        var pattern = GitHostKeys.HostPattern(trust.Host, trust.Port);
        KnownHostsEntry[] learned = (await File.ReadAllLinesAsync(_knownHostsPath, cancellationToken))
            .Select(line => GitHostKeys.TryParse(line, out KnownHostsEntry entry) ? entry : null)
            .OfType<KnownHostsEntry>()
            .Where(entry => entry.Hosts.Contains(pattern, StringComparer.Ordinal))
            .ToArray();
        if (learned.Length > 0) await trust.OnFirstUse(learned, cancellationToken);
    }

    private static void ValidateHttps(GitHttpsCredential https)
    {
        if (string.IsNullOrWhiteSpace(https.Host) || https.Host.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or ':' or '[' or ']')))
            throw new InvalidOperationException("Git credential host is invalid.");
        if (https.Username.Any(char.IsControl) || https.Password.Any(character => character is '\n' or '\r' or '\0'))
            throw new InvalidOperationException("Git credentials must not contain line breaks.");
    }

    private static string Quote(string path)
    {
        if (path.Contains('\'', StringComparison.Ordinal))
            throw new InvalidOperationException("Temporary git credential paths must not contain quotes.");
        return "'" + path + "'";
    }

    private static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WritePrivateFile(string path, string content, bool executable)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = executable
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch (IOException)
        {
        }
    }
}
