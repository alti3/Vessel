using Microsoft.Extensions.Options;
using Vessel.Application.Files;
using Vessel.Application.ManagedServices.DatabaseImports;
using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Infrastructure.Files;

namespace Vessel.Infrastructure.ManagedServices.DatabaseImports;

/// <summary>Database import settings (section <c>Vessel:DatabaseImports</c>).</summary>
public sealed class DatabaseImportOptions
{
    public const string SectionName = "Vessel:DatabaseImports";

    /// <summary>Owned staging directory; defaults to <c>{AppContext.BaseDirectory}/storage/import-staging</c>.</summary>
    public string? StagingDirectory { get; set; }

    public long MaxImportBytes { get; set; } = DatabaseImportUpload.DefaultMaxBytes;

    /// <summary>
    ///     Directories a server-path import may read from. Empty means any absolute path (server-path imports are limited
    ///     to instance administrators, who are host-root-equivalent); host-native installs should set e.g.
    ///     <c>/var/lib/vessel/imports</c>.
    /// </summary>
    public List<string> ServerPathRoots { get; set; } = [];

    public string ResolveStagingDirectory()
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(StagingDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "storage", "import-staging")
            : StagingDirectory);
    }
}

/// <summary>
///     Staging storage for chunked import uploads: one owner-only file per upload (named by its ID) in an owned
///     directory, appended sequentially and bounded per chunk and in total. Server-path imports are validated
///     (absolute, normalized, regular file after resolving links, inside a configured root, size limit).
/// </summary>
public sealed class FileDatabaseImportStaging(IOptions<DatabaseImportOptions> options, IPathSafetyService paths)
    : IDatabaseImportStaging
{
    private readonly DatabaseImportOptions _options = options.Value;
    private readonly string _root = options.Value.ResolveStagingDirectory();

    public long MaxImportBytes => _options.MaxImportBytes;

    public async Task<long> AppendAsync(DatabaseImportUploadId uploadId, long offset, Stream content, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var path = PathFor(uploadId);
        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows()) streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var file = new FileStream(path, streamOptions);
        if (file.Length < offset)
            throw new DomainException("The staged upload is shorter than the chunk offset; start a new upload.");
        // A failed earlier chunk may have left extra bytes: always continue at the recorded offset.
        file.SetLength(offset);
        file.Position = offset;
        var buffer = new byte[81920];
        long written = 0;
        try
        {
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (written + read > maxBytes)
                    throw new DomainException("The chunk is larger than allowed for this upload.");
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
            }

            await file.FlushAsync(cancellationToken);
            return written;
        }
        catch
        {
            file.SetLength(offset);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(DatabaseImportUploadId uploadId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(uploadId);
        if (!File.Exists(path)) throw new DomainException("The uploaded file no longer exists; upload it again.");
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public Task DeleteAsync(DatabaseImportUploadId uploadId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(uploadId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public IReadOnlyList<Guid> ListStaged()
    {
        if (!Directory.Exists(_root)) return [];
        return new DirectoryInfo(_root).EnumerateFiles("*.upload")
            .Select(file => Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out Guid id)
                ? id
                : Guid.Empty)
            .Where(id => id != Guid.Empty).ToArray();
    }

    public string ValidateServerPath(string path)
    {
        var value = path?.Trim() ?? string.Empty;
        if (value.Length is 0 or > 1024 || !value.StartsWith('/') || value.Any(char.IsControl) ||
            value.Split('/').Any(part => part is "." or ".."))
            throw new DomainException("The server path must be an absolute path without '.' or '..' segments.");
        DatabaseImportUpload.ValidateFileName(Path.GetFileName(value));
        var full = Path.GetFullPath(value);
        var info = new FileInfo(full);
        if (info.LinkTarget is not null)
        {
            var resolved = info.ResolveLinkTarget(true);
            info = resolved as FileInfo ?? throw new DomainException("The server path is not a regular file.");
        }

        if (_options.ServerPathRoots.Count > 0)
        {
            var allowed = false;
            foreach (var root in _options.ServerPathRoots)
                try
                {
                    // Resolves every existing link, so a symlink cannot leave the allowed root.
                    paths.EnsureOwnedPath(Path.GetFullPath(root), full, true);
                    allowed = true;
                    break;
                }
                catch (InvalidOperationException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

            if (!allowed) throw new DomainException("The server path is outside the allowed import directories.");
        }

        if (!info.Exists) throw new DomainException("The server path does not exist or is not a regular file.");
        if (info.Length > MaxImportBytes)
            throw new DomainException($"Imports are limited to {MaxImportBytes / (1024 * 1024)} MiB.");
        return full;
    }

    public Task<Stream> OpenServerFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var validated = ValidateServerPath(path);
        return Task.FromResult<Stream>(new FileStream(validated, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    private string PathFor(DatabaseImportUploadId uploadId)
    {
        Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return paths.EnsureOwnedPath(_root, Path.Combine(_root, $"{uploadId.Value:N}.upload"));
    }
}
