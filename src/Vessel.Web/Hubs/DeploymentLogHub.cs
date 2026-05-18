using Microsoft.AspNetCore.Authorization;
using Vessel.Domain;
using Vessel.Application.Authorization;

namespace Vessel.Web.Hubs;

[Authorize(Policy = VesselPermissions.DeploymentsReadLogs)]
public sealed class DeploymentLogHub : AuthorizedResourceHub
{
    public DeploymentLogHub(VesselAuthorizationService authorizationService)
        : base(authorizationService)
    {
    }

    public async Task<bool> JoinDeployment(Guid deploymentId)
    {
        return await JoinDeploymentGroupAsync(new DeploymentId(deploymentId));
    }

    public async Task<bool> JoinApplication(Guid applicationId)
    {
        return await JoinApplicationGroupAsync(new Vessel.Domain.ApplicationId(applicationId));
    }
}
