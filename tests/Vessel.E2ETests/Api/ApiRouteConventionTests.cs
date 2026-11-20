using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Vessel.Application.Authorization;
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
            typeof(ProxyConfigurationsController),
            typeof(ServerProxyController),
            typeof(TerminalsController),
            typeof(ServerHealthController)
        ];

        foreach (Type controller in controllers)
        {
            var route = Assert.Single(controller.GetCustomAttributes(typeof(RouteAttribute), false));
            Assert.StartsWith("api/v1/", ((RouteAttribute)route).Template, StringComparison.Ordinal);
            Assert.EndsWith("Controller", controller.Name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Phase10ControllersProtectReadAndWriteEndpointsWithExpectedPolicies()
    {
        AssertControllerPolicy<ApplicationDomainsController>(VesselPermissions.ApplicationsRead);
        AssertActionPolicy<ApplicationDomainsController>(
            nameof(ApplicationDomainsController.Configure),
            VesselPermissions.ApplicationsWrite);
        AssertActionPolicy<ApplicationDomainsController>(
            nameof(ApplicationDomainsController.Remove),
            VesselPermissions.ApplicationsDelete);
        AssertActionPolicy<ApplicationDomainsController>(
            nameof(ApplicationDomainsController.QueueCertificate),
            VesselPermissions.ApplicationsWrite);

        AssertControllerPolicy<ProxyConfigurationsController>(VesselPermissions.ServersRead);
        AssertActionPolicy<ProxyConfigurationsController>(
            nameof(ProxyConfigurationsController.Apply),
            VesselPermissions.ServersWrite);
        AssertActionPolicy<ProxyConfigurationsController>(
            nameof(ProxyConfigurationsController.Rollback),
            VesselPermissions.ServersWrite);

        AssertControllerPolicy<ServerProxyController>(VesselPermissions.ServersRead);
        AssertActionPolicy<ServerProxyController>(nameof(ServerProxyController.Update), VesselPermissions.ServersWrite);
        AssertActionPolicy<ServerProxyController>(nameof(ServerProxyController.Start), VesselPermissions.ServersWrite);
        AssertActionPolicy<ServerProxyController>(nameof(ServerProxyController.Stop), VesselPermissions.ServersWrite);
        AssertActionPolicy<ServerProxyController>(nameof(ServerProxyController.Restart),
            VesselPermissions.ServersWrite);
    }

    [Fact]
    public void Phase12ControllersProtectTerminalAndHealthEndpointsWithExpectedPolicies()
    {
        AssertActionPolicy<TerminalsController>(nameof(TerminalsController.Open), VesselPermissions.TerminalsOpen);
        AssertActionPolicy<TerminalsController>(nameof(TerminalsController.Input), VesselPermissions.TerminalsOpen);
        AssertActionPolicy<TerminalsController>(nameof(TerminalsController.Resize), VesselPermissions.TerminalsOpen);
        AssertActionPolicy<TerminalsController>(nameof(TerminalsController.Close), VesselPermissions.TerminalsOpen);
        AssertActionPolicy<ServerHealthController>(nameof(ServerHealthController.Latest), VesselPermissions.ServersRead);
        AssertActionPolicy<ServerHealthController>(nameof(ServerHealthController.Poll), VesselPermissions.ServersWrite);
        AssertActionPolicy<DeploymentsController>(nameof(DeploymentsController.Logs), VesselPermissions.DeploymentsReadLogs);
        AssertActionPolicy<DeploymentsController>(nameof(DeploymentsController.ExportLogs), VesselPermissions.DeploymentsReadLogs);
    }

    [Fact]
    public void NotificationEndpointsRequireNotificationPermissions()
    {
        AssertControllerPolicy<NotificationsController>(VesselPermissions.NotificationsRead);
        AssertActionPolicy<NotificationsController>(nameof(NotificationsController.UpsertTarget),
            VesselPermissions.NotificationsWrite);
        AssertActionPolicy<NotificationsController>(nameof(NotificationsController.SendTest),
            VesselPermissions.NotificationsWrite);
    }

    [Fact]
    public void NotificationEventsCannotBeRaisedThroughThePublicApi()
    {
        IEnumerable<string?> templates = typeof(NotificationsController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>(true))
            .Select(attribute => attribute.Template);

        Assert.DoesNotContain(templates, template => template is not null &&
                                                     template.StartsWith("events", StringComparison.Ordinal));
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

        foreach (Type hub in hubs) Assert.NotEmpty(hub.GetCustomAttributes(typeof(AuthorizeAttribute), true));
    }

    private static void AssertControllerPolicy<TController>(string policy)
    {
        AuthorizeAttribute? attribute = Assert.Single(typeof(TController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>());
        Assert.Equal(policy, attribute.Policy);
    }

    private static void AssertActionPolicy<TController>(string actionName, string policy)
    {
        MethodInfo method = typeof(TController).GetMethods().Single(method => method.Name == actionName);
        AuthorizeAttribute? attribute = Assert.Single(method.GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>());
        Assert.Equal(policy, attribute.Policy);
    }
}
