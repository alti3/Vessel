using Vessel.Domain;
using Vessel.Domain.Servers;

namespace Vessel.Application.Authorization;

/// <summary>
/// Central policy for host-local execution. Servers with a <see cref="ServerConnectionType.Local"/> or
/// <see cref="ServerConnectionType.DockerSocket"/> connection run work on the Vessel control-plane host,
/// which is host-root-equivalent (Docker socket access, host shell). Only instance administrators may
/// create, target, probe, or open terminals on such servers.
/// </summary>
public static class HostLocalExecutionGuard
{
    public const string InstanceAdminRequiredMessage =
        "Host-local servers (Local or DockerSocket connections) run on the Vessel control-plane host and require an instance administrator.";

    public const string UnattendedNotAuthorizedMessage =
        "Host-local execution is not authorized: it requires an instance administrator (the acting user, or for unattended work an instance-admin Owner/Admin of the owning team).";

    public static void RequireInstanceAdmin(
        VesselAuthorizationService authorization,
        UserId actorUserId,
        ServerConnectionType connectionType)
    {
        if (Server.IsHostLocalConnection(connectionType) && !authorization.IsInstanceAdmin(actorUserId))
            throw new UnauthorizedAccessException(InstanceAdminRequiredMessage);
    }

    public static void RequireInstanceAdmin(VesselAuthorizationService authorization, UserId actorUserId, Server server)
    {
        RequireInstanceAdmin(authorization, actorUserId, server.ConnectionType);
    }

    /// <summary>
    /// Authorizes host-local work that may run without an interactive actor (webhook deployments, scheduled
    /// polling). When an actor is known it must be an instance administrator; otherwise the owning team must
    /// include an instance administrator as Owner/Admin. Non host-local servers always pass.
    /// </summary>
    public static bool IsAuthorized(VesselAuthorizationService authorization, Server server, UserId? actorUserId)
    {
        if (!server.IsHostLocal) return true;
        return actorUserId.HasValue
            ? authorization.IsInstanceAdmin(actorUserId.Value)
            : authorization.TeamHasInstanceAdmin(server.TeamId);
    }
}
