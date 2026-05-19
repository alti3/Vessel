using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Projects;

public sealed class Environment : Entity<EnvironmentId>
{
    private Environment()
    {
    }

    private Environment(
        EnvironmentId id,
        ProjectId projectId,
        Slug name,
        EnvironmentKind kind,
        Description? description,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        ProjectId = projectId;
        Name = name;
        Kind = kind;
        Description = description;
    }

    public ProjectId ProjectId { get; private set; }

    public Slug Name { get; private set; }

    public EnvironmentKind Kind { get; private set; }

    public Description? Description { get; private set; }

    public static Environment CreateProduction(ProjectId projectId, DateTimeOffset now)
    {
        return new Environment(EnvironmentId.New(), projectId, new Slug("production"), EnvironmentKind.Production, null,
            now);
    }

    public static Environment Create(ProjectId projectId, Slug name, EnvironmentKind kind, DateTimeOffset now)
    {
        return new Environment(EnvironmentId.New(), projectId, name, kind, null, now);
    }

    public void Update(Slug name, EnvironmentKind kind, Description? description, DateTimeOffset now)
    {
        Name = name;
        Kind = kind;
        Description = description;
        Touch(now);
    }
}
