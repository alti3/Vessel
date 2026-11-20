using Vessel.Domain.Applications;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;

namespace Vessel.Application.Deployments.Queue;

/// <summary>
///     Which older deployments a new deployment cancels (Coolify <c>CancelPreviousDeployments</c>). A building
///     deployment is cancelled only before it starts to replace containers, and cancelling builds also cancels queued
///     deployments.
/// </summary>
public enum CancelPreviousDeployments
{
    None = 0,
    Queued = 1,
    Building = 2
}

/// <summary>
///     Pure auto-cancel rules of Coolify v4.7.0 (<c>cancel_superseded_deployments</c>). A new deployment can replace
///     older deployments of the same application, pull request (preview) and server:
///     <list type="bullet">
///         <item>
///             A webhook deployment replaces older webhook deployments, as the application settings allow
///             (<see cref="DeploymentSettings.AutoCancelQueuedDeployments" />, default on;
///             <see cref="DeploymentSettings.AutoCancelBuildingDeployments" />, opt-in).
///         </item>
///         <item>
///             An API deployment replaces older webhook and API deployments only as its caller asks with
///             <c>cancel_previous</c> (default none), so callers that wait for their deployment keep working.
///         </item>
///         <item>
///             Manual, restart, rollback and preview-redeploy deployments are never cancelled, and restarts and
///             rollbacks never cancel others.
///         </item>
///     </list>
///     A running deployment is cancelled only before its deploy phase started, so a cancel never leaves an application
///     half deployed.
/// </summary>
public static class SupersededDeploymentPolicy
{
    public static CancelPreviousDeployments Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return CancelPreviousDeployments.None;
        return value.Trim().ToLowerInvariant() switch
        {
            "none" => CancelPreviousDeployments.None,
            "queued" => CancelPreviousDeployments.Queued,
            "building" => CancelPreviousDeployments.Building,
            _ => throw new DomainException("cancel_previous must be one of: none, queued, building.")
        };
    }

    /// <summary>The cancel mode that applies to <paramref name="newDeployment" />.</summary>
    public static CancelPreviousDeployments ModeFor(Deployment newDeployment, DeploymentSettings settings,
        CancelPreviousDeployments requested)
    {
        if (newDeployment.IsRollback || newDeployment.Trigger is DeploymentTrigger.Rollback
                or DeploymentTrigger.Restart)
            return CancelPreviousDeployments.None;
        return newDeployment.Trigger switch
        {
            DeploymentTrigger.Webhook when settings.AutoCancelBuildingDeployments => CancelPreviousDeployments.Building,
            DeploymentTrigger.Webhook when settings.AutoCancelQueuedDeployments => CancelPreviousDeployments.Queued,
            DeploymentTrigger.Api => requested,
            _ => CancelPreviousDeployments.None
        };
    }

    /// <summary>Selects the older deployments the new deployment supersedes.</summary>
    public static IReadOnlyList<Deployment> Select(Deployment newDeployment, DeploymentSettings settings,
        CancelPreviousDeployments requested, IEnumerable<Deployment> existing)
    {
        CancelPreviousDeployments mode = ModeFor(newDeployment, settings, requested);
        if (mode == CancelPreviousDeployments.None) return [];
        var cancelBuilding = mode == CancelPreviousDeployments.Building;
        var fromWebhook = newDeployment.Trigger == DeploymentTrigger.Webhook;

        return existing
            .Where(old => old.Id != newDeployment.Id)
            .Where(old => old.ApplicationId == newDeployment.ApplicationId &&
                          old.PreviewId == newDeployment.PreviewId &&
                          old.ServerId == newDeployment.ServerId)
            .Where(old => fromWebhook
                ? old.Trigger == DeploymentTrigger.Webhook
                : old.Trigger is DeploymentTrigger.Webhook or DeploymentTrigger.Api)
            .Where(old => !old.IsRollback)
            .Where(old => old.CanBeSuperseded(cancelBuilding))
            .OrderBy(old => old.CreatedAt)
            .ToArray();
    }
}
