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

    /// <summary>
    /// Reads a file from an owned workspace (typically the cloned repository). Symbolic links anywhere on the
    /// path are rejected so repository content cannot redirect reads to host files.
    /// </summary>
    Task<string> ReadTextAsync(string rootDirectory, string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that <paramref name="relativePath"/> names a regular file inside <paramref name="rootDirectory"/>
    /// with no symbolic links on its path, and returns its full path.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    string EnsureRegularFile(string rootDirectory, string relativePath);

    Task CleanupAsync(DeploymentId deploymentId, CancellationToken cancellationToken = default);
}
