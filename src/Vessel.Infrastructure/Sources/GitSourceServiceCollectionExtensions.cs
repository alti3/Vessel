using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vessel.Application.OutboundRequests;
using Vessel.Application.Sources;
using Vessel.Infrastructure.OutboundRequests;

namespace Vessel.Infrastructure.Sources;

public static class GitSourceServiceCollectionExtensions
{
    /// <summary>
    ///     Provider API client for Git sources (DEP-08): user-supplied provider URLs (GitHub Enterprise, self-hosted
    ///     GitLab/Gitea) are called only through the SSRF-guarded handler, and request logging is removed so bearer
    ///     tokens and JWTs never reach logs. Requires the outbound request policy (registered by the notification
    ///     infrastructure).
    /// </summary>
    public static IServiceCollection AddVesselGitSourceInfrastructure(this IServiceCollection services)
    {
        services.TryAddTransient<OutboundUrlGuardHandler>();
        services.AddHttpClient<IGitProviderApi, GitProviderApiClient>(GitProviderApiClient.HttpClientName,
                client => client.Timeout = TimeSpan.FromSeconds(30))
            .RemoveAllLoggers()
            .AddHttpMessageHandler<OutboundUrlGuardHandler>()
            .ConfigurePrimaryHttpMessageHandler(provider => GuardedOutboundHttpHandler.Create(
                provider.GetRequiredService<IOutboundUrlPolicy>(),
                provider.GetRequiredService<IHostAddressResolver>()));
        return services;
    }
}
