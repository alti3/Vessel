using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Team audit log (IAM-08, Coolify <c>GET /api/v1/audit-events</c>): owners and admins of the active team (or the
///     token's team). Actor token, client address, user agent, metadata and changes are returned only to callers that
///     may read sensitive data (<c>read:sensitive</c>/<c>root</c> tokens).
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.AuditRead)]
[Route("api/v1/audit-events")]
public sealed class AuditEventsController(AuditQueryService audit) : ControllerBase
{
    [HttpGet]
    public ActionResult<AuditEventPage> List(
        [FromQuery] string? search,
        [FromQuery] string? action,
        [FromQuery] string? source,
        [FromQuery] string? level,
        [FromQuery(Name = "actor_id")] Guid? actorId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery(Name = "per_page")] int perPage = AuditQueryService.DefaultPerPage)
    {
        AuditLevel? parsedLevel = null;
        if (!string.IsNullOrWhiteSpace(level) && level != "all")
        {
            if (!Enum.TryParse(level, true, out AuditLevel value) || !Enum.IsDefined(value))
                return Problem("Unknown level.", statusCode: StatusCodes.Status422UnprocessableEntity);
            parsedLevel = value;
        }

        try
        {
            return Ok(audit.Query(User.GetUserId(), User.GetTeamId(), new AuditEventQuery(search, action, source,
                parsedLevel, actorId is { } actor ? new UserId(actor) : null, from, to, page, perPage)));
        }
        catch (ArgumentException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
}
