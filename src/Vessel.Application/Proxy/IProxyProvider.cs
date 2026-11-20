using Vessel.Domain;

namespace Vessel.Application.Proxy;

public interface IProxyProvider
{
    /// <summary>
    ///     Checks each route independently so one invalid domain cannot block routing for the rest of the server.
    /// </summary>
    IReadOnlyList<ProxyRouteRejection> ValidateRoutes(IReadOnlyList<ProxyRoute> routes);

    ProxyConfigurationDocument Generate(ServerId serverId, IReadOnlyList<ProxyRoute> routes);

    ProxyValidationResult Validate(ProxyConfigurationDocument document);

    Task<ProxyApplyResult> ApplyAsync(
        ProxyConfigurationDocument document,
        ProxyConfigurationDocument? previous,
        CancellationToken cancellationToken = default);

    Task<ProxyApplyResult> ReloadAsync(ServerId serverId, CancellationToken cancellationToken = default);

    Task<ProxyApplyResult> RollbackAsync(
        ProxyConfigurationDocument previous,
        CancellationToken cancellationToken = default);
}
