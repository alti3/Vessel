using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Web.Controllers.Api.V1;
using Vessel.Web.Hubs;

namespace Vessel.E2ETests.Api;

public sealed class ApiRouteConventionTests
{
    [Fact]
    public void Phase6ControllersUseVersionedApiRoutes()
    {
        Type[] controllers =
        [
            typeof(DashboardController),
            typeof(ProjectsController),
            typeof(ServersController),
            typeof(ApplicationsController),
            typeof(ApplicationWebhooksController),
            typeof(DeploymentsController),
            typeof(DatabasesController),
            typeof(NotificationsController),
            typeof(SettingsController)
        ];

        foreach (Type controller in controllers)
        {
            var route = Assert.Single(controller.GetCustomAttributes(typeof(RouteAttribute), inherit: false));
            Assert.StartsWith("api/v1/", ((RouteAttribute)route).Template, StringComparison.Ordinal);
            Assert.EndsWith("Controller", controller.Name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Phase6HubsRequireAuthorization()
    {
        Type[] hubs =
        [
            typeof(DeploymentLogHub),
            typeof(TerminalHub),
            typeof(ServerStatusHub),
            typeof(NotificationHub)
        ];

        foreach (Type hub in hubs)
        {
            Assert.NotEmpty(hub.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));
        }
    }
}
