using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Services;
using Vessel.Domain.ValueObjects;
using Vessel.Domain.Webhooks;

namespace Vessel.Application.Proxy.Domains;

/// <summary>
///     Save-time conflict detection for route identities (host + path, scheme ignored) across every application,
///     open preview and service-template resource on every server (upstream: domain usage checked across all
///     resources; http and https forms are one identity). Owners in another team are reported without their names.
/// </summary>
public static class DomainConflicts
{
    /// <summary>Returns every other resource that claims <paramref name="address" />.</summary>
    public static IReadOnlyList<DomainConflict> Find(
        IVesselDbContext dbContext,
        TeamId callerTeamId,
        RouteAddress address,
        RouteOwnerKind ownerKind,
        Guid ownerId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var conflicts = new List<DomainConflict>();

        var applicationRows = (
                from domain in dbContext.ApplicationDomains
                where domain.Host == address.Host && domain.PathPrefix == address.PathPrefix
                join application in dbContext.Applications on domain.ApplicationId equals application.Id
                join environment in dbContext.Environments on application.EnvironmentId equals environment.Id
                join project in dbContext.Projects on environment.ProjectId equals project.Id
                select new { Application = application, project.TeamId })
            .AsEnumerable()
            .Select(row => new { row.Application.Id, Name = row.Application.Name.Value, row.TeamId })
            .ToArray();
        foreach (var row in applicationRows)
        {
            if (ownerKind == RouteOwnerKind.Application && row.Id.Value == ownerId) continue;
            conflicts.Add(new DomainConflict(RouteOwnerKind.Application, row.Id.Value,
                row.TeamId == callerTeamId ? row.Name : "a resource in another team", address.Identity));
        }

        ServiceDomain[] serviceDomains = dbContext.ServiceDomains
            .Where(domain => domain.Host == address.Host && domain.PathPrefix == address.PathPrefix)
            .ToArray();
        foreach (ServiceDomain domain in serviceDomains)
        {
            if (ownerKind == RouteOwnerKind.Service && domain.ServiceResourceId.Value == ownerId) continue;
            ServiceResource? service = dbContext.ServiceResources
                .SingleOrDefault(item => item.Id == domain.ServiceResourceId);
            conflicts.Add(new DomainConflict(RouteOwnerKind.Service, domain.ServiceResourceId.Value,
                service is not null && service.TeamId == callerTeamId ? service.Name.Value : "a resource in another team",
                address.Identity));
        }

        // Preview hosts are derived ({pr}.{domain}); compare against the recorded preview URLs of open previews.
        ApplicationPreview[] previews = dbContext.ApplicationPreviews
            .Where(preview => preview.Status == ApplicationPreviewStatus.Open && preview.PreviewUrl != null)
            .ToArray();
        foreach (ApplicationPreview preview in previews)
        {
            if (!TryParse(preview.PreviewUrl!, out RouteAddress previewAddress) ||
                !string.Equals(previewAddress.Identity, address.Identity, StringComparison.Ordinal))
                continue;
            if (ownerKind == RouteOwnerKind.Preview && preview.Id.Value == ownerId) continue;
            conflicts.Add(new DomainConflict(RouteOwnerKind.Preview, preview.Id.Value,
                $"preview #{preview.PullRequestNumber}", address.Identity));
        }

        return conflicts;
    }

    public static string Describe(IReadOnlyList<DomainConflict> conflicts)
    {
        return "Domain is already used by " +
               string.Join(", ", conflicts.Select(conflict =>
                   $"{conflict.OwnerKind.ToString().ToLowerInvariant()} '{conflict.OwnerName}' ({conflict.Identity})")) +
               ". Set forceDomainOverride to save anyway.";
    }

    private static bool TryParse(string url, out RouteAddress address)
    {
        try
        {
            address = RouteAddress.Parse(url);
            return true;
        }
        catch (DomainException)
        {
            address = default;
            return false;
        }
    }
}
