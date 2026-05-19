using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.UnitTests.Domain;

public sealed class DeploymentTests
{
    [Fact]
    public void Deployment_CanMoveThroughSuccessfulLifecycle()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var deployment = Deployment.Queue(
            AppId.New(),
            ServerId.New(),
            null,
            "abc123",
            now);

        deployment.Start(now.AddSeconds(1));
        deployment.MarkSucceeded("artifact://deployments/1", now.AddSeconds(2));

        Assert.Equal(DeploymentStatus.Succeeded, deployment.Status);
        Assert.NotNull(deployment.StartedAt);
        Assert.NotNull(deployment.FinishedAt);
    }

    [Fact]
    public void Deployment_RejectsSkippingInProgress()
    {
        var deployment = Deployment.Queue(
            AppId.New(),
            ServerId.New(),
            null,
            null,
            DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => deployment.MarkSucceeded(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Deployment_RejectsTransitionFromTerminalState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var deployment = Deployment.Queue(AppId.New(), ServerId.New(), null, null, now);
        deployment.CancelByUser(now.AddSeconds(1));

        Assert.Throws<DomainException>(() => deployment.Start(now.AddSeconds(2)));
    }

    [Fact]
    public void Deployment_RecordsSourceSnapshotAndCancellationRequest()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var deployment = Deployment.Queue(AppId.New(), ServerId.New(), UserId.New(), null, now);

        deployment.Start(now.AddSeconds(1));
        deployment.RecordSource("https://example.com/repo.git", "main", "abc123", "Initial commit", now.AddSeconds(2));
        deployment.RecordConfigurationSnapshot("snapshots/docker-compose.redacted.yml", now.AddSeconds(3));
        deployment.RequestCancellation(now.AddSeconds(4));
        deployment.CancelByUser(now.AddSeconds(5));

        Assert.Equal(DeploymentStatus.CanceledByUser, deployment.Status);
        Assert.Equal("main", deployment.CommitBranch);
        Assert.Equal("abc123", deployment.CommitSha);
        Assert.Equal("snapshots/docker-compose.redacted.yml", deployment.ConfigurationSnapshotReference);
        Assert.NotNull(deployment.CancellationRequestedAt);
        Assert.NotNull(deployment.FinishedAt);
    }
}
