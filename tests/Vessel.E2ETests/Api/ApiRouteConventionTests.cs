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
            typeof(ApplicationDomainsController),
            typeof(DeploymentsController),
            typeof(DatabasesController),
            typeof(NotificationsController),
            typeof(SettingsController),
            typeof(ProxyConfigurationsController)
        ];

        foreach (Type controller in controllers)
        {
            var route = Assert.Single(controller.GetCustomAttributes(typeof(RouteAttribute), inherit: false));
            Assert.StartsWith("api/v1/", ((RouteAttribute)route).Template, StringComparison.Ordinal);
            Assert.EndsWith("Controller", controller.Name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Phase10ControllersProtectReadAndWriteEndpointsWithExpectedPolicies()
    {
        AssertControllerPolicy<ApplicationDomainsController>(Vessel.Application.Authorization.VesselPermissions.ApplicationsRead);
        AssertActionPolicy<ApplicationDomainsController>(
            nameof(ApplicationDomainsController.Configure),
            Vessel.Application.Authorization.VesselPermissions.ApplicationsWrite);
        AssertActionPolicy<ApplicationDomainsController>(
            nameof(ApplicationDomainsController.Remove),
            Vessel.Application.Authorization.VesselPermissions.ApplicationsWrite);
        AssertActionPolicy<ApplicationDomainsController>(
            nameof(ApplicationDomainsController.QueueCertificate),
            Vessel.Application.Authorization.VesselPermissions.ApplicationsWrite);

        AssertControllerPolicy<ProxyConfigurationsController>(Vessel.Application.Authorization.VesselPermissions.ServersRead);
        AssertActionPolicy<ProxyConfigurationsController>(
            nameof(ProxyConfigurationsController.Apply),
            Vessel.Application.Authorization.VesselPermissions.ServersWrite);
        AssertActionPolicy<ProxyConfigurationsController>(
            nameof(ProxyConfigurationsController.Rollback),
            Vessel.Application.Authorization.VesselPermissions.ServersWrite);
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

    private static void AssertControllerPolicy<TController>(string policy)
    {
        var attribute = Assert.Single(typeof(TController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());
        Assert.Equal(policy, attribute.Policy);
    }

    private static void AssertActionPolicy<TController>(string actionName, string policy)
    {
        var method = typeof(TController).GetMethods().Single(method => method.Name == actionName);
        var attribute = Assert.Single(method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());
        Assert.Equal(policy, attribute.Policy);
    }
}
