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
}
