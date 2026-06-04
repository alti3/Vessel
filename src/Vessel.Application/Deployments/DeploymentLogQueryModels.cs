using Vessel.Domain.Deployments;

namespace Vessel.Application.Deployments;

public sealed record DeploymentLogQuery(
    int? AfterSequence = null,
    string? Search = null,
    string? Stream = null,
    int PageSize = 200,
    bool Descending = false);

public sealed record DeploymentLogPage(
    Guid DeploymentId,
    IReadOnlyList<DeploymentLogEntry> Entries,
    int? NextSequence,
    bool HasMore,
    DeploymentStatus Status);

public sealed record DeploymentLogRetentionOptions
{
    public static DeploymentLogRetentionOptions Default { get; } = new(TimeSpan.FromDays(30), 1000);

    public DeploymentLogRetentionOptions(TimeSpan retentionPeriod, int batchSize)
    {
        if (retentionPeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retentionPeriod), "Retention period must be positive.");
        if (batchSize < 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size cannot be negative.");

        RetentionPeriod = retentionPeriod;
        BatchSize = batchSize;
    }

    public TimeSpan RetentionPeriod { get; }

    public int BatchSize { get; }
}

public sealed record DeploymentLogRetentionResult(int DeletedLineCount, DateTimeOffset Cutoff);
