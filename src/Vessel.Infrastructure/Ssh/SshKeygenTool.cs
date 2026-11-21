using Vessel.Application.Processes;
using Vessel.Application.Ssh;

namespace Vessel.Infrastructure.Ssh;

/// <summary>
///     <c>ssh-keygen</c> through <see cref="IProcessRunner" />. Private keys only ever exist in an owner-only scratch
///     directory under <see cref="SshRuntimeFiles" /> for the duration of the call.
/// </summary>
public sealed class SshKeygenTool(IProcessRunner processRunner, SshRuntimeFiles files) : ISshKeyTool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<GeneratedSshKey> GenerateEd25519Async(string comment, CancellationToken cancellationToken = default)
    {
        var safeComment = new string((comment ?? string.Empty)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '@')
            .Take(64).ToArray());
        var directory = files.NewScratchDirectory();
        try
        {
            var keyPath = Path.Combine(directory, "id");
            ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
                "ssh-keygen",
                ["-q", "-t", "ed25519", "-N", string.Empty, "-C", safeComment.Length == 0 ? "vessel" : safeComment,
                    "-f", keyPath],
                Timeout: Timeout,
                InheritEnvironment: false,
                Environment: MinimalEnvironment()), cancellationToken);
            if (!result.Succeeded)
                throw new InvalidOperationException("ssh-keygen could not generate an Ed25519 key.");
            var privateKey = await File.ReadAllTextAsync(keyPath, cancellationToken);
            var publicKey = (await File.ReadAllTextAsync(keyPath + ".pub", cancellationToken)).Trim();
            return new GeneratedSshKey(privateKey, publicKey);
        }
        finally
        {
            files.Delete(directory);
        }
    }

    public async Task<string?> DerivePublicKeyAsync(string privateKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(privateKey);
        var directory = files.NewScratchDirectory();
        try
        {
            var keyPath = Path.Combine(directory, "id");
            await WriteOwnerOnlyAsync(keyPath, SshKeyFormats.NormalizePrivateKey(privateKey), cancellationToken);
            // An empty passphrase (-P "") makes ssh-keygen fail for encrypted keys instead of prompting.
            ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
                "ssh-keygen",
                ["-y", "-P", string.Empty, "-f", keyPath],
                Timeout: Timeout,
                InheritEnvironment: false,
                Environment: MinimalEnvironment(),
                Redaction: new ProcessRedactionProfile([privateKey], [])), cancellationToken);
            if (!result.Succeeded) return null;
            var line = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        finally
        {
            files.Delete(directory);
        }
    }

    private static IReadOnlyDictionary<string, string?> MinimalEnvironment()
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin",
            ["LANG"] = "C.UTF-8"
        };
    }

    private static async Task WriteOwnerOnlyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }
}
