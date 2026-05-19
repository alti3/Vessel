using Vessel.Domain;
using Vessel.Domain.Deployments;

namespace Vessel.Application.Deployments;

public sealed record StartDeploymentRequest(
    Guid ApplicationId,
    bool ForceRebuild = false,
    string? CommitSha = null,
    Guid? PreviewId = null,
    Guid? WebhookEventId = null);

public sealed record StartDeploymentResult(
    Guid DeploymentId,
    Guid ApplicationId,
    DeploymentStatus Status,
    string Message);

public sealed record DeploymentDetails(
    Guid Id,
    Guid ApplicationId,
    Guid ServerId,
    DeploymentStatus Status,
    string? RepositoryUrl,
    string? CommitBranch,
    string? CommitSha,
    string? CommitMessage,
    bool IsPreview,
    bool IsWebhookTriggered,
    string? ArtifactReference,
    string? ConfigurationSnapshotReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? CancellationRequestedAt,
    IReadOnlyList<DeploymentLogEntry> Logs);

public sealed record DeploymentLogEntry(
    int Sequence,
    string Stream,
    string Message,
    DateTimeOffset CreatedAt);

public sealed record DeploymentEnvironmentFile(
    string FileName,
    string Contents,
    string RedactedContents);

public sealed record DeploymentComposeSnapshot(
    string FileName,
    string Contents,
    string RedactedContents);

public sealed record DeploymentWorkspace(
    string RootDirectory,
    string RepositoryDirectory,
    string ComposeFilePath,
    string EnvironmentFilePath);

public sealed record DeploymentRuntimePlan(
    string ProjectName,
    string ServiceName,
    string ImageName,
    string NetworkName,
    string ComposeYaml,
    string RedactedComposeYaml,
    string EnvironmentFile,
    string RedactedEnvironmentFile,
    string HealthCheckUrl);

public interface IDeploymentRunner
{
    Task RunAsync(DeploymentId deploymentId, CancellationToken cancellationToken = default);
}

public interface IDeploymentWorkspaceManager
{
    Task<DeploymentWorkspace> PrepareAsync(DeploymentId deploymentId, CancellationToken cancellationToken = default);

    Task WriteTextAsync(
        string rootDirectory,
        string relativePath,
        string contents,
        bool restrictToOwner,
        CancellationToken cancellationToken = default);

    Task<string> ReadTextAsync(string rootDirectory, string relativePath, CancellationToken cancellationToken = default);

    Task CleanupAsync(DeploymentId deploymentId, CancellationToken cancellationToken = default);
}
