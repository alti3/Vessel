using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Vessel.Application.Authorization;
using Vessel.Application.Terminals;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize]
[EnableRateLimiting("terminal")]
[Route("api/v1/terminals")]
public sealed class TerminalsController(TerminalSessionManager terminalSessionManager) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = VesselPermissions.TerminalsOpen)]
    public async Task<ActionResult<TerminalSessionDetails>> Open(
        OpenTerminalSessionRequest request,
        CancellationToken cancellationToken)
    {
        TerminalSessionDetails result = await terminalSessionManager.OpenAsync(
            User.GetUserId(),
            User.GetTeamId(),
            request,
            HttpContext.TraceIdentifier,
            cancellationToken);

        return AcceptedAtAction(nameof(Get), new { sessionId = result.Id }, result);
    }

    [HttpGet("{sessionId:guid}")]
    [Authorize(Policy = VesselPermissions.TerminalsOpen)]
    public ActionResult<TerminalSessionDetails> Get(Guid sessionId)
    {
        return Ok(terminalSessionManager.Get(
            User.GetUserId(),
            User.GetTeamId(),
            new TerminalSessionId(sessionId)));
    }

    [HttpPost("{sessionId:guid}/input")]
    [Authorize(Policy = VesselPermissions.TerminalsOpen)]
    public async Task<IActionResult> Input(Guid sessionId, TerminalInputRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SessionId != sessionId) return BadRequest();
        await terminalSessionManager.SendInputAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new TerminalSessionId(sessionId),
            request.Data,
            cancellationToken);
        return Accepted();
    }

    [HttpPost("{sessionId:guid}/resize")]
    [Authorize(Policy = VesselPermissions.TerminalsOpen)]
    public async Task<IActionResult> Resize(Guid sessionId, TerminalResizeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SessionId != sessionId) return BadRequest();
        await terminalSessionManager.ResizeAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new TerminalSessionId(sessionId),
            request.Columns,
            request.Rows,
            cancellationToken);
        return Accepted();
    }

    [HttpPost("{sessionId:guid}/close")]
    [Authorize(Policy = VesselPermissions.TerminalsOpen)]
    public async Task<IActionResult> Close(Guid sessionId, CancellationToken cancellationToken)
    {
        await terminalSessionManager.CloseAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new TerminalSessionId(sessionId),
            cancellationToken);
        return Accepted();
    }
}
