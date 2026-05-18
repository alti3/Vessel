namespace Vessel.Domain.Applications;

public readonly record struct DeploymentSettings(
    bool AutoDeployEnabled,
    bool PreviewDeploymentsEnabled,
    bool ForceRebuild,
    string? WatchPaths)
{
    public static DeploymentSettings Default => new(true, false, false, null);
}
