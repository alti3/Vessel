using Vessel.Domain.Operations;

namespace Vessel.Application.Operations.ControlPlaneBackups;

/// <summary>
///     Instance backup settings (DATA-13; upstream Settings → Backup). <see cref="BeforeUpdate" /> (default on) makes
///     every upgrade dump the database first and fail closed when the dump fails. <see cref="Enabled" /> and
///     <see cref="Schedule" /> control only the scheduled dumps; pre-update and manual dumps always work.
/// </summary>
public sealed record InstanceBackupSettings
{
    public const int DefaultRetentionCount = 10;

    public const int MaxRetentionCount = 500;

    public bool Enabled { get; init; }

    public bool BeforeUpdate { get; init; } = true;

    /// <summary>Optional five-field cron (UTC) of scheduled dumps; null = no schedule.</summary>
    public string? Schedule { get; init; }

    /// <summary>Newest dumps kept per trigger (manual, pre-update, scheduled).</summary>
    public int RetentionCount { get; init; } = DefaultRetentionCount;
}

public sealed record UpdateInstanceBackupSettingsRequest(
    bool? Enabled = null,
    bool? BeforeUpdate = null,
    string? Schedule = null,
    int? RetentionCount = null);

public sealed record ControlPlaneBackupSummary(
    Guid Id,
    ControlPlaneBackupTrigger Trigger,
    ControlPlaneBackupStatus Status,
    string? FileName,
    long SizeBytes,
    string? ChecksumSha256,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? FailureReason,
    Guid? RequestedByUserId,
    string WorkerHost);

public sealed record ControlPlaneBackupDownload(Stream Content, string FileName, long SizeBytes);

/// <summary>One dump request: the file is written to <see cref="RelativePath" /> below the owned backup root.</summary>
public sealed record ControlPlaneDumpRequest(string RelativePath, TimeSpan? Timeout = null);

public sealed record ControlPlaneDumpResult(string RelativePath, long SizeBytes, string Sha256);

/// <summary>Raised by the dumper with a message that is safe to store and show (redacted, no connection details).</summary>
public sealed class ControlPlaneDumpException(string message) : InvalidOperationException(message);

/// <summary>
///     Dumps Vessel's own PostgreSQL database (<c>pg_dump --format=custom</c> through <c>IProcessRunner</c>) into the
///     owned control-plane backup directory and manages the files there. Paths are relative to that root and are
///     validated by the implementation.
/// </summary>
public interface IControlPlaneDatabaseDumper
{
    Task<ControlPlaneDumpResult> DumpAsync(ControlPlaneDumpRequest request,
        CancellationToken cancellationToken = default);

    Stream OpenRead(string relativePath);

    void Delete(string relativePath);
}

public sealed class UnavailableControlPlaneDatabaseDumper : IControlPlaneDatabaseDumper
{
    private const string Message = "Control-plane database backups are unavailable because no database is configured.";

    public Task<ControlPlaneDumpResult> DumpAsync(ControlPlaneDumpRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new ControlPlaneDumpException(Message);
    }

    public Stream OpenRead(string relativePath)
    {
        throw new InvalidOperationException(Message);
    }

    public void Delete(string relativePath)
    {
    }
}
