using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Sources;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Private Git sources (DEP-08): GitHub Apps, GitLab/Gitea apps, deploy keys and HTTPS credentials. Secrets are
///     accepted on create/update and never returned. Team sources need team-wide <c>applications.write</c>;
///     system-wide sources need an instance administrator (enforced in the application service).
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.ApplicationsRead)]
[Route("api/v1/sources")]
public sealed class GitSourcesController(GitSourceService sources) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<GitSourceSummary>> List()
    {
        return Guard(() => Ok(sources.List(User.GetUserId(), User.GetTeamId())));
    }

    [HttpGet("{sourceId:guid}")]
    public ActionResult<GitSourceSummary> Get(Guid sourceId)
    {
        return Guard(() => Ok(sources.Get(User.GetUserId(), User.GetTeamId(), new GitSourceConnectionId(sourceId))));
    }

    [HttpPost]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public Task<ActionResult> Create(CreateGitSourceRequest request, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
            Ok(await sources.CreateAsync(User.GetUserId(), User.GetTeamId(), request, cancellationToken)));
    }

    [HttpPatch("{sourceId:guid}")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public Task<ActionResult> Update(Guid sourceId, UpdateGitSourceRequest request,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await sources.UpdateAsync(User.GetUserId(), User.GetTeamId(),
            new GitSourceConnectionId(sourceId), request, cancellationToken)));
    }

    [HttpDelete("{sourceId:guid}")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public Task<ActionResult> Delete(Guid sourceId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
        {
            await sources.DeleteAsync(User.GetUserId(), User.GetTeamId(), new GitSourceConnectionId(sourceId),
                cancellationToken);
            return NoContent();
        });
    }

    [HttpGet("{sourceId:guid}/repositories")]
    public Task<ActionResult> Repositories(Guid sourceId, [FromQuery] int page, CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await sources.ListRepositoriesAsync(User.GetUserId(), User.GetTeamId(),
            new GitSourceConnectionId(sourceId), page <= 0 ? 1 : page, cancellationToken)));
    }

    /// <summary>Branches of <c>repository</c> (<c>owner/name</c> or repository URL).</summary>
    [HttpGet("{sourceId:guid}/branches")]
    public Task<ActionResult> Branches(Guid sourceId, [FromQuery] string repository,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await sources.ListBranchesAsync(User.GetUserId(), User.GetTeamId(),
            new GitSourceConnectionId(sourceId), repository, cancellationToken)));
    }

    [HttpGet("known-hosts")]
    public ActionResult<IReadOnlyList<GitKnownHostSummary>> KnownHosts()
    {
        return Guard(() => Ok(sources.ListKnownHosts(User.GetUserId(), User.GetTeamId())));
    }

    [HttpPost("known-hosts")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public Task<ActionResult> PinKnownHost(AddGitKnownHostRequest request, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
            Ok(await sources.PinKnownHostAsync(User.GetUserId(), User.GetTeamId(), request, cancellationToken)));
    }

    [HttpDelete("known-hosts/{hostId:guid}")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public Task<ActionResult> RemoveKnownHost(Guid hostId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
        {
            await sources.RemoveKnownHostAsync(User.GetUserId(), User.GetTeamId(), new GitKnownHostId(hostId),
                cancellationToken);
            return NoContent();
        });
    }

    /// <summary>Selects (or clears with <c>sourceId: null</c>) the Git source of an application.</summary>
    [HttpPut("/api/v1/applications/{applicationId:guid}/source")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public Task<ActionResult> AssignToApplication(Guid applicationId, AssignGitSourceRequest request,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
        {
            GitSourceSummary? source = await sources.AssignToApplicationAsync(User.GetUserId(), User.GetTeamId(),
                new Vessel.Domain.ApplicationId(applicationId), request, cancellationToken);
            return source is null ? NoContent() : Ok(source);
        });
    }

    private ActionResult Guard(Func<ActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (ApiErrors.TryMap(this, exception, out ActionResult? result))
        {
            return result!;
        }
    }

    private async Task<ActionResult> GuardAsync(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (GitProviderApiException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception exception) when (ApiErrors.TryMap(this, exception, out ActionResult? result))
        {
            return result!;
        }
    }
}
