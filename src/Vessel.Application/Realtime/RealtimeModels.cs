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
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{Id}";
}

public sealed record RealtimeMessage(string Type, object Payload);
