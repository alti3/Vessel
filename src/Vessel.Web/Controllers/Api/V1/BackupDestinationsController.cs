using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Backups;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Shared.Errors;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Team-scoped S3-compatible backup destinations. Listing needs <c>projects.read</c>; every change and the
///     connection test need <c>secrets.write</c>. Credentials are write-only and never returned.
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.ProjectsRead)]
[Route("api/v1/backup-destinations")]
public sealed class BackupDestinationsController(BackupDestinationService destinations) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<BackupDestinationSummary>> List()
    {
        return Guard(() => Ok(destinations.List(User.GetUserId(), User.GetTeamId())));
    }

    [HttpGet("{destinationId:guid}")]
    public ActionResult<BackupDestinationSummary> Get(Guid destinationId)
    {
        return Guard(() => Ok(destinations.Get(User.GetUserId(), User.GetTeamId(),
            new BackupDestinationId(destinationId))));
    }

    /// <summary>Creates a destination and runs the connection test; the result is in <c>isUsable</c>.</summary>
    [HttpPost]
    [Authorize(Policy = VesselPermissions.SecretsWrite)]
    public async Task<ActionResult<BackupDestinationSummary>> Create(CreateBackupDestinationRequest request,
        CancellationToken cancellationToken)
    {
        return await GuardAsync(async () =>
            Ok(await destinations.CreateAsync(User.GetUserId(), User.GetTeamId(), request, cancellationToken)));
    }

    [HttpPatch("{destinationId:guid}")]
    [Authorize(Policy = VesselPermissions.SecretsWrite)]
    public async Task<ActionResult<BackupDestinationSummary>> Update(Guid destinationId,
        UpdateBackupDestinationRequest request, CancellationToken cancellationToken)
    {
        return await GuardAsync(async () => Ok(await destinations.UpdateAsync(User.GetUserId(), User.GetTeamId(),
            new BackupDestinationId(destinationId), request, cancellationToken)));
    }

    [HttpPost("{destinationId:guid}/validate")]
    [Authorize(Policy = VesselPermissions.SecretsWrite)]
    public async Task<ActionResult<BackupDestinationSummary>> Validate(Guid destinationId,
        CancellationToken cancellationToken)
    {
        return await GuardAsync(async () => Ok(await destinations.ValidateAsync(User.GetUserId(), User.GetTeamId(),
            new BackupDestinationId(destinationId), cancellationToken)));
    }

    /// <summary>Deletes a destination and detaches it from schedules. Objects in the bucket are not touched.</summary>
    [HttpDelete("{destinationId:guid}")]
    [Authorize(Policy = VesselPermissions.SecretsWrite)]
    public async Task<ActionResult> Delete(Guid destinationId, CancellationToken cancellationToken)
    {
        return await GuardAsync(async () =>
        {
            await destinations.DeleteAsync(User.GetUserId(), User.GetTeamId(), new BackupDestinationId(destinationId),
                cancellationToken);
            return NoContent();
        });
    }

    private ActionResult Guard(Func<ActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (Map(exception) is { } result)
        {
            return result;
        }
    }

    private async Task<ActionResult> GuardAsync(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (Map(exception) is { } result)
        {
            return result;
        }
    }

    private ActionResult? Map(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => StatusCode(StatusCodes.Status403Forbidden),
            DomainException domain => BadRequest(new ApiErrorResponse(new ApiError("invalid_backup_destination",
                domain.Message, new Dictionary<string, object?>()))),
            BackupDestinationUnavailableException => null,
            InvalidOperationException => NotFound(),
            _ => null
        };
    }
}
