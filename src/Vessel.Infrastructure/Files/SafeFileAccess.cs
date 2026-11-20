using System.Text;
using Vessel.Application.Files;

namespace Vessel.Infrastructure.Files;

/// <summary>
/// File reads and writes inside owned roots that never traverse symbolic links. Reads are verified after
/// open (on Linux via <c>/proc/self/fd</c>) so a link swapped in between validation and open is still
/// rejected; writes go to a fresh temporary file that is atomically renamed over the destination, which
/// replaces a directory entry rather than writing through it.
/// </summary>
internal static class SafeFileAccess
{
    public const long DefaultMaxReadBytes = 2 * 1024 * 1024;

    public static async Task<string> ReadAllTextAsync(
        IPathSafetyService paths,
        string rootDirectory,
        string relativePath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        string path = paths.EnsureNoSymlinks(rootDirectory, relativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("File was not found inside the owned root directory.", relativePath);

        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        VerifyOpenedPath(stream, path);
        if (stream.Length > maxBytes)
            throw new InvalidOperationException($"File exceeds the maximum supported size of {maxBytes} bytes.");

        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public static async Task WriteAllTextAsync(
        IPathSafetyService paths,
        string rootDirectory,
        string relativePath,
        string contents,
        bool restrictToOwner,
        CancellationToken cancellationToken)
    {
        string destination = paths.EnsureNoSymlinks(rootDirectory, relativePath);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        // Re-validate after creating intermediate directories.
        destination = paths.EnsureNoSymlinks(rootDirectory, relativePath);

        string temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        if (restrictToOwner && !OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            await using (var stream = new FileStream(temporary, options))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(contents);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (restrictToOwner)
                OwnerOnlyFilePermissions.Apply(temporary);

            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void VerifyOpenedPath(FileStream stream, string expectedPath)
    {
        if (!OperatingSystem.IsLinux()) return;

        string descriptorLink = $"/proc/self/fd/{stream.SafeFileHandle.DangerousGetHandle()}";
        string? opened = new FileInfo(descriptorLink).LinkTarget;
        if (opened is null) return;

        string expected = PathSafetyService.ResolveRealPath(expectedPath);
        if (!string.Equals(opened, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("File changed to a symbolic link while it was being opened.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
