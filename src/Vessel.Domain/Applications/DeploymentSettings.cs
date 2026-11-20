namespace Vessel.Domain.Applications;

/// <summary>
///     Deployment behavior of an application. <see cref="PublicPreviewsEnabled" /> is false by default: only pull
///     requests whose source repository equals the application repository get preview deployments and fork pull requests
///     are skipped. When true, fork pull requests deploy too, but never receive production values (Coolify
///     <c>is_pr_deployments_public_enabled</c>).
/// </summary>
public readonly record struct DeploymentSettings(
    bool AutoDeployEnabled,
    bool PreviewDeploymentsEnabled,
    bool ForceRebuild,
    string? WatchPaths,
    bool PublicPreviewsEnabled = false)
{
    public static DeploymentSettings Default => new(true, false, false, null);
}
