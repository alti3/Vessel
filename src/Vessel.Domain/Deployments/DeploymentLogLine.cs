namespace Vessel.Domain.Deployments;

public sealed class DeploymentLogLine
{
    private DeploymentLogLine()
    {
    }

    internal DeploymentLogLine(DeploymentId deploymentId, int sequence, string stream, string message,
        DateTimeOffset createdAt)
    {
        DeploymentId = deploymentId;
        Sequence = sequence;
        Stream = stream;
        Message = message;
        CreatedAt = createdAt;
    }

    public DeploymentId DeploymentId { get; private set; }

    public int Sequence { get; private set; }

    public string Stream { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }
}
