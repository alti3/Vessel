using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Storage.Persistent;
using Vessel.Web.Controllers.Api.V1;

namespace Vessel.E2ETests.Api;

/// <summary>DATA-12 / DATA-09 / DATA-10 API contracts: host paths are rejected; writes need projects.write.</summary>
public sealed class StorageAndImportRouteTests
{
    [Theory]
    [InlineData("""{"type":"Directory","mountPath":"/data","path":"data","host_path":"/etc"}""")]
    [InlineData("""{"type":"Volume","mountPath":"/data","name":"data","hostPath":"/var/run/docker.sock"}""")]
    public async Task CreateStorage_RejectsHostPathField(string json)
    {
        CreateStorageBody body = JsonSerializer.Deserialize<CreateStorageBody>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(json.Contains("Volume", StringComparison.Ordinal) ? ResourceStorageType.Volume : ResourceStorageType.Directory,
            body.Type);
        // The services are never reached: the request is rejected before any storage logic runs.
        var controller = new ResourceStoragesController(null!, null!);
        ActionResult result = await controller.Create("applications", Guid.NewGuid(), body,
            TestContext.Current.CancellationToken);
        BadRequestObjectResult bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("host_path_not_supported", JsonSerializer.Serialize(bad.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void StateChangingEndpointsRequireWrite()
    {
        foreach (Type controller in new[]
                 {
                     typeof(ResourceStoragesController), typeof(VolumeBackupsController),
                     typeof(DatabaseImportsController)
                 })
        {
            Assert.Contains(controller.GetCustomAttributes<AuthorizeAttribute>(),
                attribute => attribute.Policy == VesselPermissions.ProjectsRead);
            foreach (MethodInfo method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                                                                BindingFlags.DeclaredOnly))
            {
                var writes = method.GetCustomAttributes().Any(attribute =>
                    attribute is HttpPostAttribute or HttpPatchAttribute or HttpPutAttribute or HttpDeleteAttribute);
                if (!writes) continue;
                Assert.Contains(method.GetCustomAttributes<AuthorizeAttribute>(),
                    attribute => attribute.Policy == VesselPermissions.ProjectsWrite);
            }
        }
    }
}
