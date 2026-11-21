using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Web.Controllers.Api.V1;

namespace Vessel.E2ETests.Api;

/// <summary>NTF-06: instance email settings are a settings.manage surface; writes also need secrets.write.</summary>
public sealed class InstanceEmailSettingsRouteTests
{
    [Fact]
    public void EmailSettingsUseTheUpstreamPathAndSensitiveWritePolicies()
    {
        var route = Assert.Single(typeof(InstanceEmailSettingsController).GetCustomAttributes<RouteAttribute>());
        Assert.Equal("api/v1/settings/email", route.Template);
        Assert.Contains(typeof(InstanceEmailSettingsController).GetCustomAttributes<AuthorizeAttribute>(),
            attribute => attribute.Policy == VesselPermissions.SettingsManage);

        foreach (var action in new[] { nameof(InstanceEmailSettingsController.Update), nameof(InstanceEmailSettingsController.SendTest) })
        {
            var method = typeof(InstanceEmailSettingsController).GetMethod(action)!;
            Assert.Contains(method.GetCustomAttributes<AuthorizeAttribute>(),
                attribute => attribute.Policy == VesselPermissions.SecretsWrite);
        }

        Assert.NotNull(typeof(InstanceEmailSettingsController).GetMethod(nameof(InstanceEmailSettingsController.Update))!
            .GetCustomAttribute<HttpPatchAttribute>());
    }

    [Fact]
    public void NotificationEventCatalogIsReadable()
    {
        var method = typeof(NotificationsController).GetMethod(nameof(NotificationsController.ListEvents))!;
        Assert.Equal("event-types", method.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Contains(typeof(NotificationsController).GetCustomAttributes<AuthorizeAttribute>(),
            attribute => attribute.Policy == VesselPermissions.NotificationsRead);
    }
}
