using Vessel.Domain.Common;

namespace Vessel.Domain.Applications;

/// <summary>
///     Deployment behavior of an application. <see cref="PublicPreviewsEnabled" /> is false by default: only pull
///     requests whose source repository equals the application repository get preview deployments and fork pull requests
///     are skipped. When true, fork pull requests deploy too, but never receive production values (Coolify
///     <c>is_pr_deployments_public_enabled</c>).
/// </summary>
/// <param name="AutoDeployEnabled">Push webhooks deploy the application automatically.</param>
/// <param name="PreviewDeploymentsEnabled">Pull requests get preview deployments.</param>
/// <param name="ForceRebuild">Deployments rebuild without cache.</param>
/// <param name="WatchPaths">Newline-separated globs; a push deploys only when its net change touches one.</param>
/// <param name="PublicPreviewsEnabled">Fork pull requests get previews too (never with production values).</param>
/// <param name="AutoCancelQueuedDeployments">
///     Coolify <c>is_auto_cancel_queued_deployments_enabled</c> (default on): a newer webhook deployment cancels older
///     queued webhook deployments of the same application, pull request and server.
/// </param>
/// <param name="AutoCancelBuildingDeployments">
///     Coolify <c>is_auto_cancel_building_deployments_enabled</c> (opt-in): also stops older webhook deployments that
///     are still building, never one that already started replacing containers.
/// </param>
/// <param name="RedactAllEnvValuesInLogs">
///     Coolify <c>redact_all_env_values_in_logs</c> (default true, as upstream). Secret values are always hidden in
///     deployment logs; when true, every resolved environment value (plain values too) is hidden as well.
/// </param>
/// <param name="ImagesToKeep">
///     Coolify <c>docker_images_to_keep</c>: how many images of earlier successful production deployments are kept
///     locally as rollback targets.
/// </param>
public readonly record struct DeploymentSettings(
    bool AutoDeployEnabled,
    bool PreviewDeploymentsEnabled,
    bool ForceRebuild,
    string? WatchPaths,
    bool PublicPreviewsEnabled = false,
    bool AutoCancelQueuedDeployments = true,
    bool AutoCancelBuildingDeployments = false,
    bool RedactAllEnvValuesInLogs = true,
    int ImagesToKeep = DeploymentSettings.DefaultImagesToKeep)
{
    public const int DefaultImagesToKeep = 2;
    public const int MaxImagesToKeep = 50;

    public static DeploymentSettings Default => new(true, false, false, null);

    /// <summary>Validates the settings (image retention bounds, watch path size).</summary>
    public DeploymentSettings Validated()
    {
        if (ImagesToKeep is < 0 or > MaxImagesToKeep)
            throw new DomainException($"Images to keep must be between 0 and {MaxImagesToKeep}.");
        return this with { WatchPaths = DomainValidation.Optional(WatchPaths, nameof(WatchPaths), 512) };
    }
}
