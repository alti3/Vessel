using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Proxy;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ServersRead)]
[Route("api/v1/servers/{serverId:guid}/proxy-configurations")]
public sealed class ProxyConfigurationsController(ProxyConfigurationService proxyConfigurations) : ControllerBase
{
    /// <summary>History without configuration text (newest first).</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<ProxyConfigurationSummary>> List(Guid serverId)
    {
        return Ok(proxyConfigurations.ListVersions(User.GetUserId(), User.GetTeamId(), new ServerId(serverId)));
    }

    /// <summary>Decrypted configuration text of one version; sensitive, so it requires server write access.</summary>
    [HttpGet("{versionId:guid}/content")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ProxyConfigurationContent>> ReadContent(
        Guid serverId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        return Ok(await proxyConfigurations.GetVersionContentAsync(User.GetUserId(), User.GetTeamId(),
            new ServerId(serverId), new ProxyConfigurationVersionId(versionId), cancellationToken));
    }

    [HttpPost("apply")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ProxyConfigurationSummary>> Apply(Guid serverId, CancellationToken cancellationToken)
    {
        return Ok(await proxyConfigurations.GenerateValidateAndApplyAsync(User.GetUserId(), User.GetTeamId(),
            new ServerId(serverId), cancellationToken));
    }

    /// <summary>Restores a version as a new version (history is never rewritten).</summary>
    [HttpPost("{versionId:guid}/restore")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ProxyConfigurationSummary>> Restore(
        Guid serverId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        return Ok(await proxyConfigurations.RestoreAsync(User.GetUserId(), User.GetTeamId(),
            new ServerId(serverId), new ProxyConfigurationVersionId(versionId), cancellationToken));
    }

    /// <summary>Compatibility alias of <see cref="Restore" />.</summary>
    [HttpPost("{versionId:guid}/rollback")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ProxyConfigurationSummary>> Rollback(
        Guid serverId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        return Ok(await proxyConfigurations.RestoreAsync(User.GetUserId(), User.GetTeamId(),
            new ServerId(serverId), new ProxyConfigurationVersionId(versionId), cancellationToken));
    }

    [HttpGet("dynamic")]
    public ActionResult<IReadOnlyList<ProxyDynamicConfigurationSummary>> ListDynamic(Guid serverId)
    {
        return Ok(proxyConfigurations.ListDynamicConfigurations(User.GetUserId(), User.GetTeamId(),
            new ServerId(serverId)));
    }

    [HttpGet("dynamic/{fileName}")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ProxyDynamicConfigurationContent>> GetDynamic(
        Guid serverId,
        string fileName,
        CancellationToken cancellationToken)
    {
        return Ok(await proxyConfigurations.GetDynamicConfigurationAsync(User.GetUserId(), User.GetTeamId(),
            new ServerId(serverId), fileName, cancellationToken));
    }

    [HttpPut("dynamic/{fileName}")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ProxyDynamicConfigurationSummary>> SaveDynamic(
        Guid serverId,
        string fileName,
        SaveProxyDynamicConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await proxyConfigurations.SaveDynamicConfigurationAsync(User.GetUserId(), User.GetTeamId(),
            new ServerId(serverId), fileName, request, cancellationToken));
    }

    [HttpDelete("dynamic/{fileName}")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<IActionResult> DeleteDynamic(Guid serverId, string fileName, CancellationToken cancellationToken)
    {
        await proxyConfigurations.DeleteDynamicConfigurationAsync(User.GetUserId(), User.GetTeamId(),
            new ServerId(serverId), fileName, cancellationToken);
        return NoContent();
    }
}
