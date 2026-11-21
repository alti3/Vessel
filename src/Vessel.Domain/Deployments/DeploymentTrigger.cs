namespace Vessel.Domain.Deployments;

/// <summary>
///     What queued a deployment (Coolify <c>is_webhook</c>/<c>is_api</c>/<c>restart_only</c>/<c>rollback</c>). The
///     trigger decides auto-cancel behavior: only webhook deployments are cancelled by newer pushes, API deployments
///     only when the newer API request asks for it, and manual, restart, rollback and preview redeploys never are.
/// </summary>
public enum DeploymentTrigger
{
    Manual = 0,
    Api = 1,
    Webhook = 2,
    Restart = 3,
    Rollback = 4,

    /// <summary>A redeploy of an existing preview requested through the preview API.</summary>
    Preview = 5
}
