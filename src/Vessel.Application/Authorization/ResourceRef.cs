using Vessel.Domain;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Authorization;

/// <summary>
///     Kinds of team-owned resources that resource-scoped authorization can resolve to an owning team.
/// </summary>
public enum ResourceKind
{
    Project,
    Environment,
    Server,
    Application,
    Deployment,
    Database
}

/// <summary>
///     A reference to a team-owned resource. Authorization resolves the owning team from the reference instead of
///     trusting the caller's active team, so a role held in one team can never be applied to another team's resource.
/// </summary>
public readonly record struct ResourceRef(ResourceKind Kind, Guid Id)
{
    public static ResourceRef Project(ProjectId id)
    {
        return new ResourceRef(ResourceKind.Project, id.Value);
    }

    public static ResourceRef Environment(EnvironmentId id)
    {
        return new ResourceRef(ResourceKind.Environment, id.Value);
    }

    public static ResourceRef Server(ServerId id)
    {
        return new ResourceRef(ResourceKind.Server, id.Value);
    }

    public static ResourceRef Application(ApplicationId id)
    {
        return new ResourceRef(ResourceKind.Application, id.Value);
    }

    public static ResourceRef Deployment(DeploymentId id)
    {
        return new ResourceRef(ResourceKind.Deployment, id.Value);
    }

    public static ResourceRef Database(DatabaseResourceId id)
    {
        return new ResourceRef(ResourceKind.Database, id.Value);
    }
}
