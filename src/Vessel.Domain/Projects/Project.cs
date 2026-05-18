using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Projects;

public sealed class Project : Entity<ProjectId>
{
    private Project()
    {
    }

    private Project(ProjectId id, TeamId teamId, ResourceName name, Description? description, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        Name = name;
        Description = description;
    }

    public TeamId TeamId { get; private set; }

    public ResourceName Name { get; private set; }

    public Description? Description { get; private set; }

    public static Project Create(TeamId teamId, ResourceName name, DateTimeOffset now, Description? description = null)
    {
        var project = new Project(ProjectId.New(), teamId, name, description, now);
        project.AddDomainEvent(new ProjectCreatedEvent(project.Id, teamId, now));

        return project;
    }

    public void Rename(ResourceName name, DateTimeOffset now)
    {
        Name = name;
        Touch(now);
    }
}
