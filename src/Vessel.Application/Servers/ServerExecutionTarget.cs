using Vessel.Domain;
using Vessel.Domain.Servers;

namespace Vessel.Application.Servers;

/// <summary>
///     Where server-scoped work (file delivery, runtime commands) executes. <see cref="ServerConnectionType.Local" /> and
///     <see cref="ServerConnectionType.DockerSocket" /> run on the control-plane host; <see cref="ServerConnectionType.Ssh" />
///     runs on the remote host over SSH.
/// </summary>
public sealed record ServerExecutionTarget(
    ServerId ServerId,
    ServerConnectionType ConnectionType,
    string Host,
    int Port,
    string? User,
    ContainerRuntimeKind Runtime)
{
    public bool IsRemote => ConnectionType == ServerConnectionType.Ssh;

    public static ServerExecutionTarget From(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return new ServerExecutionTarget(
            server.Id,
            server.ConnectionType,
            server.Address.Host,
            server.Address.Port.Value,
            server.Address.User,
            server.Runtime);
    }
}
