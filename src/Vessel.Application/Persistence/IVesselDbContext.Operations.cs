using Vessel.Domain.Operations;

namespace Vessel.Application.Persistence;

/// <summary>Control-plane operations persistence (OPS-01/OPS-02/DATA-13): own database backups and upgrade runs.</summary>
public partial interface IVesselDbContext
{
    IQueryable<ControlPlaneBackup> ControlPlaneBackups => Enumerable.Empty<ControlPlaneBackup>().AsQueryable();

    IQueryable<UpgradeRun> UpgradeRuns => Enumerable.Empty<UpgradeRun>().AsQueryable();

    IRepository<ControlPlaneBackup, ControlPlaneBackupId> ControlPlaneBackupRepository =>
        throw new InvalidOperationException("Control-plane backup persistence is not available.");

    IRepository<UpgradeRun, UpgradeRunId> UpgradeRunRepository =>
        throw new InvalidOperationException("Upgrade run persistence is not available.");
}
