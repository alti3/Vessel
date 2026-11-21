using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Servers;
using Vessel.Application.Ssh;
using Vessel.Application.Ssh.Keys;
using Vessel.Domain.Common;
using Vessel.Shared.Errors;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Maps application exceptions of the server, SSH key and terminal APIs to HTTP results. Messages of these exception
///     types are written for display and never contain secrets or key material.
/// </summary>
internal static class ApiErrors
{
    public static bool TryMap(ControllerBase controller, Exception exception, out ActionResult? result)
    {
        result = exception switch
        {
            UnauthorizedAccessException => controller.StatusCode(StatusCodes.Status403Forbidden,
                Error("forbidden", exception.Message)),
            KeyNotFoundException => controller.NotFound(Error("not_found", exception.Message)),
            ServerInUseException or SshKeyInUseException => controller.Conflict(Error("in_use", exception.Message)),
            SshHostKeyUnavailableException => controller.Conflict(Error("host_key_unavailable", exception.Message)),
            DomainException or ArgumentException => controller.BadRequest(Error("invalid_request", exception.Message)),
            InvalidOperationException => controller.Conflict(Error("invalid_state", exception.Message)),
            _ => null
        };
        return result is not null;
    }

    private static ApiErrorResponse Error(string code, string message)
    {
        return new ApiErrorResponse(new ApiError(code, message, new Dictionary<string, object?>()));
    }
}
