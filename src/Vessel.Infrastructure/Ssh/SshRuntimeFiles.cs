using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Vessel.Application.Ssh;
using Vessel.Domain;

namespace Vessel.Infrastructure.Ssh;

/// <summary>
///     Owned runtime directory for SSH material on the control-plane host: per-connection private key files
///     (<c>keys/</c>) and per-server known_hosts files (<c>known_hosts/</c>). Directories are created 0700 and files
///     0600; the root must be absolute and must not be a symbolic link. Paths handed to OpenSSH are always generated
///     here, never taken from input.
/// </summary>
public sealed class SshRuntimeFiles
{
    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public SshRuntimeFiles(IOptions<SshOptions> options)
    {
        var configured = options.Value.RuntimeDirectory;
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "storage", "ssh")
            : configured));
        if (!Path.IsPathRooted(Root))
            throw new InvalidOperationException("The SSH runtime directory must be an absolute path.");
    }

    public string Root { get; }

    public string KeysDirectory => Path.Combine(Root, "keys");

    public string KnownHostsDirectory => Path.Combine(Root, "known_hosts");

    /// <summary>Writes a private key to a new owner-only file with a random name and returns its path.</summary>
    public string WritePrivateKey(string privateKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(privateKey);
        EnsureDirectory(KeysDirectory);
        var path = Path.Combine(KeysDirectory, $"key_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}");
        WriteNew(path, SshKeyFormats.NormalizePrivateKey(privateKey));
        return path;
    }

    /// <summary>
    ///     Writes the known_hosts file of a server. The file name carries a hash of the content, so a process holding an
    ///     older pinned key never overwrites the file of the current key; files of older keys are removed.
    /// </summary>
    public string WriteKnownHosts(ServerId serverId, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        EnsureDirectory(KnownHostsDirectory);
        var prefix = SshRuntimeNames.KnownHostsPrefix(serverId);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];
        var path = Path.Combine(KnownHostsDirectory, prefix + hash);
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        {
            var temporary = $"{path}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp";
            WriteNew(temporary, content);
            File.Move(temporary, path, true);
        }

        foreach (var stale in Directory.EnumerateFiles(KnownHostsDirectory, prefix + "*"))
            if (!string.Equals(stale, path, StringComparison.Ordinal) && !stale.EndsWith(".tmp", StringComparison.Ordinal))
                TryDelete(stale);
        return path;
    }

    /// <summary>A fresh path for a host key scan's known_hosts output (not created).</summary>
    public string NewScanFile()
    {
        EnsureDirectory(KnownHostsDirectory);
        return Path.Combine(KnownHostsDirectory, $"scan_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}");
    }

    /// <summary>A fresh owner-only scratch directory for <c>ssh-keygen</c> (deleted by the caller).</summary>
    public string NewScratchDirectory()
    {
        EnsureDirectory(KeysDirectory);
        var path = Path.Combine(KeysDirectory, $"gen_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}");
        CreateOwnerOnlyDirectory(path);
        return path;
    }

    /// <summary>Deletes a file or directory created by this class; paths outside the runtime root are ignored.</summary>
    public void Delete(string? path)
    {
        if (string.IsNullOrEmpty(path) || !IsInside(path)) return;
        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return;
        }

        TryDelete(path);
        TryDelete(path + ".old");
    }

    /// <summary>Removes key files left behind by a previous process (crash or kill).</summary>
    public void PurgeKeys()
    {
        if (!Directory.Exists(KeysDirectory)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(KeysDirectory))
            Delete(entry);
    }

    private bool IsInside(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private void EnsureDirectory(string directory)
    {
        if (!Directory.Exists(Root)) CreateOwnerOnlyDirectory(Root);
        RejectSymlink(Root);
        if (!Directory.Exists(directory)) CreateOwnerOnlyDirectory(directory);
        RejectSymlink(directory);
    }

    private static void CreateOwnerOnlyDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, OwnerOnlyDirectory);
    }

    private static void RejectSymlink(string directory)
    {
        if (new DirectoryInfo(directory).LinkTarget is not null)
            throw new InvalidOperationException($"The SSH runtime directory '{directory}' must not be a symbolic link.");
    }

    private static void WriteNew(string path, string content)
    {
        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            streamOptions.UnixCreateMode = OwnerOnlyFile;
        using (var stream = new FileStream(path, streamOptions))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(content);
        }

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, OwnerOnlyFile);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
