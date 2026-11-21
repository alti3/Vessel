using Vessel.Domain;

namespace Vessel.Application.Proxy;

/// <summary>
///     Generates and validates the dynamic (routes) document for a server. Delivery to the server and the proxy
///     container lifecycle live in <see cref="ProxyLifecycleService" />.
/// </summary>
public interface IProxyProvider
{
    /// <summary>
    ///     Checks each route independently so one invalid domain cannot block routing for the rest of the server.
    /// </summary>
    IReadOnlyList<ProxyRouteRejection> ValidateRoutes(IReadOnlyList<ProxyRoute> routes);

    ProxyConfigurationDocument Generate(ServerId serverId, IReadOnlyList<ProxyRoute> routes);

    ProxyValidationResult Validate(ProxyConfigurationDocument document);

    /// <summary>
    ///     Validates a user-managed dynamic configuration file: it must parse as a single YAML mapping of provider
    ///     sections and must not carry private key material.
    /// </summary>
    ProxyValidationResult ValidateUserDynamicConfiguration(string contents);
}
