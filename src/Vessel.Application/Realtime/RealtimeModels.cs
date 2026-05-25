using Vessel.Domain;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Realtime;

public enum RealtimeGroupKind
{
    Team,
    Project,
    Server,
    Application,
    Deployment,
    Terminal,
    User
}

public sealed record RealtimeGroup(RealtimeGroupKind Kind, string Id)
{
    public override string ToString()
    {
        return $"{Kind.ToString().ToLowerInvariant()}:{Id}";
    }
}

public sealed record RealtimeMessage(string Type, object Payload);

public static class RealtimeGroupNames
{
    public static string Tenant(TeamId teamId)
    {
        return $"tenant:{teamId.Value:D}";
    }

    public static string Project(ProjectId projectId)
    {
        return $"project:{projectId.Value:D}";
    }

    public static string Server(ServerId serverId)
    {
        return $"server:{serverId.Value:D}";
    }

    public static string Application(ApplicationId applicationId)
    {
        return $"application:{applicationId.Value:D}";
    }

    public static string Deployment(DeploymentId deploymentId)
    {
        return $"deployment:{deploymentId.Value:D}";
    }

    public static string Terminal(Guid terminalSessionId)
    {
        return $"terminal:{terminalSessionId:D}";
    }

    public static string User(UserId userId)
    {
        return $"user:{userId.Value:D}";
    }
}
