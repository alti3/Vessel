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

public sealed record DeploymentLogRetentionOptions(
    TimeSpan RetentionPeriod,
    int BatchSize)
{
    public static DeploymentLogRetentionOptions Default { get; } = new(TimeSpan.FromDays(30), 1000);
}

public sealed record DeploymentLogRetentionResult(int DeletedLineCount, DateTimeOffset Cutoff);
