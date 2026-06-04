using Vessel.Domain;
using Vessel.Domain.Servers;

namespace Vessel.Application.Monitoring;

public sealed record ServerHealthSnapshotModel(
    Guid ServerId,
    ServerStatus Status,
    decimal? CpuLoadPercent,
    long? MemoryUsedBytes,
    long? DiskUsedBytes,
    int RunningContainers,
    bool ProxyHealthy,
    bool CertificatesHealthy,
    DateTimeOffset CreatedAt);

public sealed record ServerHealthPollingResult(
    Guid ServerId,
    ServerStatus Status,
    int RunningContainers,
    bool RuntimeReachable);

public interface IServerHealthQuery
{
    IReadOnlyList<ServerHealthSnapshotModel> Latest(TeamId teamId);
}
