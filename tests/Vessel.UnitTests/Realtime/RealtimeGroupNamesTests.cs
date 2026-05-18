using Vessel.Application.Realtime;
using Vessel.Domain;

namespace Vessel.UnitTests.Realtime;

public sealed class RealtimeGroupNamesTests
{
    [Fact]
    public void GroupNamesAreDeterministic()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal("tenant:11111111-2222-3333-4444-555555555555", RealtimeGroupNames.Tenant(new TeamId(id)));
        Assert.Equal("project:11111111-2222-3333-4444-555555555555", RealtimeGroupNames.Project(new ProjectId(id)));
        Assert.Equal("server:11111111-2222-3333-4444-555555555555", RealtimeGroupNames.Server(new ServerId(id)));
        Assert.Equal("application:11111111-2222-3333-4444-555555555555", RealtimeGroupNames.Application(new Vessel.Domain.ApplicationId(id)));
        Assert.Equal("deployment:11111111-2222-3333-4444-555555555555", RealtimeGroupNames.Deployment(new DeploymentId(id)));
        Assert.Equal("terminal:11111111-2222-3333-4444-555555555555", RealtimeGroupNames.Terminal(id));
        Assert.Equal("user:11111111-2222-3333-4444-555555555555", RealtimeGroupNames.User(new UserId(id)));
    }
}
