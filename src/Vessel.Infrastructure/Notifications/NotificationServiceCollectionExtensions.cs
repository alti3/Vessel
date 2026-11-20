using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vessel.Application.Notifications;
using Vessel.Application.OutboundRequests;
using Vessel.Infrastructure.OutboundRequests;

namespace Vessel.Infrastructure.Notifications;

internal static class NotificationServiceCollectionExtensions
{
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddVesselNotificationInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<OutboundRequestOptions>()
            .Bind(configuration.GetSection(OutboundRequestOptions.SectionName));
        services.TryAddSingleton<IHostAddressResolver, DnsHostAddressResolver>();
        services.TryAddSingleton<OutboundUrlPolicy>();
        services.TryAddSingleton<IOutboundUrlPolicy>(provider => provider.GetRequiredService<OutboundUrlPolicy>());
        services.TryAddTransient<OutboundUrlGuardHandler>();

        // Added (not TryAdded) so it replaces any host-level fallback registration.
        services.AddScoped<INotificationEventPublisher, BackgroundNotificationEventPublisher>();

        services.AddTransient<INotificationProvider, EmailNotificationProvider>();
        AddGuardedProvider<HttpWebhookNotificationProvider>(services, client => client.Timeout = ProviderTimeout);
        AddGuardedProvider<DiscordNotificationProvider>(services, client => client.Timeout = ProviderTimeout);
        AddGuardedProvider<SlackNotificationProvider>(services, client => client.Timeout = ProviderTimeout);
        AddGuardedProvider<TelegramNotificationProvider>(services, client =>
        {
            client.BaseAddress = new Uri("https://api.telegram.org");
            client.Timeout = ProviderTimeout;
        });

        return services;
    }

    private static void AddGuardedProvider<TProvider>(IServiceCollection services, Action<HttpClient> configure)
        where TProvider : class, INotificationProvider
    {
        services.AddHttpClient<INotificationProvider, TProvider>(typeof(TProvider).Name, configure)
            // Default IHttpClientFactory logging writes the full request URI (webhook path secrets, bot tokens).
            .RemoveAllLoggers()
            .AddHttpMessageHandler<OutboundUrlGuardHandler>()
            .ConfigurePrimaryHttpMessageHandler(provider => GuardedOutboundHttpHandler.Create(
                provider.GetRequiredService<IOutboundUrlPolicy>(),
                provider.GetRequiredService<IHostAddressResolver>()));
    }
}
