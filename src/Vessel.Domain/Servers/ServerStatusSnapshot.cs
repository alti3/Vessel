using Vessel.Domain.Common;

namespace Vessel.Domain.Servers;

public sealed class ServerStatusSnapshot : Entity<ServerStatusSnapshotId>
{
    private ServerStatusSnapshot()
    {
    }

    private ServerStatusSnapshot(
        ServerStatusSnapshotId id,
        ServerId serverId,
        ServerStatus status,
        decimal? cpuLoadPercent,
        long? memoryUsedBytes,
        long? diskUsedBytes,
        int runningContainers,
        bool proxyHealthy,
        bool certificatesHealthy,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        ServerId = serverId;
        Status = status;
        CpuLoadPercent = cpuLoadPercent;
        MemoryUsedBytes = memoryUsedBytes;
        DiskUsedBytes = diskUsedBytes;
        RunningContainers = runningContainers;
        ProxyHealthy = proxyHealthy;
        CertificatesHealthy = certificatesHealthy;
    }

    public ServerId ServerId { get; private set; }

    public ServerStatus Status { get; private set; }

    public decimal? CpuLoadPercent { get; private set; }

    public long? MemoryUsedBytes { get; private set; }

    public long? DiskUsedBytes { get; private set; }

    public int RunningContainers { get; private set; }

    public bool ProxyHealthy { get; private set; }

    public bool CertificatesHealthy { get; private set; }

    public static ServerStatusSnapshot Create(
        ServerId serverId,
        ServerStatus status,
        decimal? cpuLoadPercent,
        long? memoryUsedBytes,
        long? diskUsedBytes,
        int runningContainers,
        bool proxyHealthy,
        bool certificatesHealthy,
        DateTimeOffset now)
    {
        if (runningContainers < 0) throw new DomainException("Running container count cannot be negative.");

        return new ServerStatusSnapshot(ServerStatusSnapshotId.New(), serverId, status, cpuLoadPercent,
            memoryUsedBytes, diskUsedBytes, runningContainers, proxyHealthy, certificatesHealthy, now);
    }
}
