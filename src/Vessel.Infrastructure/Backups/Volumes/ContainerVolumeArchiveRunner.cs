using System.Text.RegularExpressions;
using Vessel.Application.Backups.Volumes;
using Vessel.Application.Common;
using Vessel.Application.Files;
using Vessel.Application.Processes;
using Vessel.Application.Servers;
using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Servers;
using Vessel.Infrastructure.ManagedServices.Backups;

namespace Vessel.Infrastructure.Backups.Volumes;

/// <summary>
///     Archives and restores volumes and directory mounts with a short-lived BusyBox helper container
///     (<see cref="ComponentVersions.VolumeBackupHelperImage" />) through the server's runtime CLI. Every command is a
///     structured argument vector run by <see cref="IServerCommandRunner" /> (locally, or over SSH with every argument
///     quoted), and archives stream through the command's standard output into an owned scratch file while they are
///     hashed; nothing is buffered in memory.
/// </summary>
/// <remarks>
///     The helper runs without a network and with <c>no-new-privileges</c>; it only sees the target at <c>/volume</c>
///     (read-only for backups). Runtime access is host-root-equivalent; see
///     <c>docs/deployment/phase-11b-storage-volume-backups-imports.md</c>.
/// </remarks>
public sealed partial class ContainerVolumeArchiveRunner : IVolumeArchiveRunner
{
    /// <summary>
    ///     tar exits with 1 when a file changed while it was read (a live volume); the archive is still valid, so that
    ///     exit is reported as a warning (upstream v4.5.0).
    /// </summary>
    public const string ArchiveScript =
        "tar -czf - -C /volume .; status=$?; if [ \"$status\" -eq 1 ]; then echo 'vessel-warning: files changed while they were archived' >&2; exit 0; fi; exit $status";

    /// <summary>Restores the archive from standard input; mode <c>replace</c> empties the target first.</summary>
    public const string RestoreScript =
        "set -e; if [ \"$1\" = replace ]; then find /volume -mindepth 1 -delete; fi; tar -xzf - -C /volume";

    private const string WarningMarker = "vessel-warning:";

    private static readonly TimeSpan ControlTimeout = TimeSpan.FromMinutes(2);

    private readonly IServerCommandRunner _commands;
    private readonly IPathSafetyService _paths;
    private readonly string _scratchRoot;

    public ContainerVolumeArchiveRunner(IServerCommandRunner commands, IPathSafetyService paths)
        : this(commands, paths, Path.Combine(AppContext.BaseDirectory, "storage", "backup-scratch"))
    {
    }

    public ContainerVolumeArchiveRunner(IServerCommandRunner commands, IPathSafetyService paths, string scratchRoot)
    {
        _commands = commands;
        _paths = paths;
        _scratchRoot = Path.GetFullPath(scratchRoot);
    }

    public async Task<bool> SourceExistsAsync(ServerExecutionTarget server, VolumeArchiveSource source,
        CancellationToken cancellationToken = default)
    {
        var mount = Validate(source);
        IReadOnlyList<string> arguments = source.Kind == VolumeBackupTargetKind.PersistentVolume
            ? [Cli(server), "volume", "inspect", mount]
            : ["test", "-d", mount];
        ServerCommandResult result = await _commands.RunAsync(server, arguments, ControlTimeout, null,
            cancellationToken);
        return result.Succeeded;
    }

    public async Task<IReadOnlyList<string>> ListContainersUsingAsync(ServerExecutionTarget server,
        VolumeArchiveSource source, CancellationToken cancellationToken = default)
    {
        var mount = Validate(source);
        ServerCommandResult running = await _commands.RunAsync(server, [Cli(server), "ps", "-q", "--no-trunc"],
            ControlTimeout, null, cancellationToken);
        EnsureSucceeded(running, "list running containers");
        string[] ids = ParseIds(running.StandardOutput);
        if (ids.Length == 0) return [];
        List<string> arguments =
            [Cli(server), "inspect", "--format", "{{.Id}}{{range .Mounts}}\t{{.Name}}\t{{.Source}}{{end}}"];
        arguments.AddRange(ids);
        ServerCommandResult inspected = await _commands.RunAsync(server, arguments, ControlTimeout, null,
            cancellationToken);
        // Containers can exit between ps and inspect; a partial answer still lists the ones that exist.
        return ParseMountingContainers(inspected.StandardOutput, mount);
    }

    public async Task StopContainersAsync(ServerExecutionTarget server, IReadOnlyList<string> containerIds,
        CancellationToken cancellationToken = default)
    {
        if (containerIds.Count == 0) return;
        ServerCommandResult result = await _commands.RunAsync(server,
            [Cli(server), "stop", .. ValidateIds(containerIds)], TimeSpan.FromMinutes(5), null, cancellationToken);
        EnsureSucceeded(result, "stop the containers that use the storage");
    }

    public async Task StartContainersAsync(ServerExecutionTarget server, IReadOnlyList<string> containerIds,
        CancellationToken cancellationToken = default)
    {
        if (containerIds.Count == 0) return;
        ServerCommandResult result = await _commands.RunAsync(server,
            [Cli(server), "start", .. ValidateIds(containerIds)], TimeSpan.FromMinutes(5), null, cancellationToken);
        EnsureSucceeded(result, "start the stopped containers");
    }

    public async Task<VolumeArchive> ArchiveAsync(ServerExecutionTarget server, VolumeArchiveSource source,
        Guid operationId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> arguments = ArchiveArguments(server, source, operationId);
        BackupScratchDirectory scratch = BackupScratchDirectory.Create(_scratchRoot, "volume", operationId, _paths);
        try
        {
            const string fileName = "archive.tar.gz";
            long size;
            string sha256;
            ServerCommandResult result;
            await using (FileStream file = scratch.CreateFile(fileName))
            await using (var hashing = new HashingWriteStream(file, true))
            {
                result = await _commands.RunStreamingAsync(server, arguments, timeout,
                    new ProcessStreams(StandardOutput: hashing), cancellationToken);
                await hashing.FlushAsync(cancellationToken);
                size = hashing.BytesWritten;
                sha256 = hashing.GetSha256Hex();
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureSucceeded(result, "archive the storage");
            if (size == 0) throw new InvalidOperationException("The storage archive is empty.");
            var warning = result.StandardError.Contains(WarningMarker, StringComparison.Ordinal)
                ? "Files changed while they were archived; the archive holds the state at the time each file was read."
                : null;
            return new VolumeArchive(size, sha256, warning, () => scratch.OpenRead(fileName), scratch.DisposeAsync);
        }
        catch
        {
            await scratch.DisposeAsync();
            throw;
        }
    }

    public async Task RestoreAsync(ServerExecutionTarget server, VolumeArchiveSource source, Stream archive,
        string? expectedSha256, bool replaceExisting, Guid operationId, TimeSpan timeout,
        Func<string, CancellationToken, Task> log, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> arguments = RestoreArguments(server, source, operationId, replaceExisting);
        await using BackupScratchDirectory scratch =
            BackupScratchDirectory.Create(_scratchRoot, "volume-restore", operationId, _paths);
        const string fileName = "archive.tar.gz";

        await log("Downloading the backup archive.", cancellationToken);
        string sha256;
        await using (FileStream file = scratch.CreateFile(fileName))
        await using (var hashing = new HashingWriteStream(file, true))
        {
            await archive.CopyToAsync(hashing, cancellationToken);
            await hashing.FlushAsync(cancellationToken);
            sha256 = hashing.GetSha256Hex();
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(expectedSha256, sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The archive checksum does not match the checksum recorded when the backup was taken. Nothing was changed.");
        await log("SHA-256 checksum verified.", cancellationToken);
        await using (FileStream compressed = scratch.OpenRead(fileName))
        {
            await BackupArtifactInspector.DecompressGzipAsync(compressed, Stream.Null, cancellationToken);
        }

        await log("Gzip integrity verified; extracting into the storage.", cancellationToken);
        ServerCommandResult result;
        await using (FileStream input = scratch.OpenRead(fileName))
        {
            result = await _commands.RunStreamingAsync(server, arguments, timeout,
                new ProcessStreams(input), cancellationToken);
        }

        EnsureSucceeded(result, "restore the archive");
    }

    /// <summary>Argument vector of the backup helper container (exposed for tests).</summary>
    public static IReadOnlyList<string> ArchiveArguments(ServerExecutionTarget server, VolumeArchiveSource source,
        Guid operationId)
    {
        var mount = Validate(source);
        return
        [
            Cli(server), "run", "--rm", "--name", $"vessel-volume-backup-{operationId:N}", "--network", "none",
            "--security-opt", "no-new-privileges", "--volume", $"{mount}:/volume:ro",
            ComponentVersions.VolumeBackupHelperImage, "sh", "-c", ArchiveScript
        ];
    }

    /// <summary>Argument vector of the restore helper container (exposed for tests).</summary>
    public static IReadOnlyList<string> RestoreArguments(ServerExecutionTarget server, VolumeArchiveSource source,
        Guid operationId, bool replaceExisting)
    {
        var mount = Validate(source);
        return
        [
            Cli(server), "run", "--rm", "--interactive", "--name", $"vessel-volume-restore-{operationId:N}",
            "--network", "none", "--security-opt", "no-new-privileges", "--volume", $"{mount}:/volume",
            ComponentVersions.VolumeBackupHelperImage, "sh", "-c", RestoreScript, "vessel",
            replaceExisting ? "replace" : "merge"
        ];
    }

    /// <summary>Containers whose mounts include <paramref name="mount" /> (volume name or source path).</summary>
    public static IReadOnlyList<string> ParseMountingContainers(string inspectOutput, string mount)
    {
        var result = new List<string>();
        foreach (var line in inspectOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 2 || !ContainerIdPattern().IsMatch(fields[0])) continue;
            if (fields.Skip(1).Any(field => string.Equals(field, mount, StringComparison.Ordinal)))
                result.Add(fields[0]);
        }

        return result;
    }

    /// <summary>
    ///     A source can never be read as an option or change the mount syntax: volume names are Vessel runtime names and
    ///     host paths are absolute paths of conservative segments (they come from the owned file channel).
    /// </summary>
    public static string Validate(VolumeArchiveSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var valid = source.Kind == VolumeBackupTargetKind.PersistentVolume
            ? VolumeNamePattern().IsMatch(source.Source)
            : HostPathPattern().IsMatch(source.Source) && !source.Source.Split('/').Any(part => part is "." or "..");
        return valid
            ? source.Source
            : throw new InvalidOperationException("The storage source is not a Vessel-managed volume or directory.");
    }

    private static string Cli(ServerExecutionTarget server)
    {
        return server.Runtime == ContainerRuntimeKind.Podman ? "podman" : "docker";
    }

    private static string[] ParseIds(string output)
    {
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => ContainerIdPattern().IsMatch(id)).ToArray();
    }

    private static IEnumerable<string> ValidateIds(IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
            if (!ContainerIdPattern().IsMatch(id))
                throw new InvalidOperationException("Container IDs must be hexadecimal.");
        return ids;
    }

    private static void EnsureSucceeded(ServerCommandResult result, string action)
    {
        if (result.Succeeded) return;
        var detail = result.StandardError.Trim();
        if (detail.Length > 500) detail = detail[..500];
        throw new InvalidOperationException(
            $"Could not {action} (exit code {result.ExitCode}){(detail.Length > 0 ? ": " + detail : ".")}");
    }

    [GeneratedRegex("^[0-9a-f]{12,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerIdPattern();

    [GeneratedRegex("^vessel-[0-9a-f]{32}-[a-z0-9][a-z0-9_.-]{0,62}(-pr-[0-9]{1,9})?$", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeNamePattern();

    [GeneratedRegex("^(/[A-Za-z0-9_][A-Za-z0-9._@+-]{0,254})+$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPathPattern();
}
