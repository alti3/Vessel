using System.Globalization;
using System.Text.Json;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain.Secrets;
using Vessel.Domain.Services;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.ManagedServices.Templates;

/// <summary>
///     Resolves the per-instance values of an imported template's magic variables (TPL-01).
///     <list type="bullet">
///         <item>
///             Generated variables (<c>SERVICE_PASSWORD_*</c>, <c>SERVICE_USER_*</c>, ...) are generated once, stored as one
///             JSON secret in the vault and referenced from <see cref="ServiceResource.GeneratedSecretsReferenceId" />.
///             Re-provisioning reuses them; only variables a newer template version added are generated. Secret template
///             inputs live in the same vault entry (prefixed <see cref="SecretInputPrefix" />), never in the
///             configuration JSON.
///         </item>
///         <item>
///             Route variables (<c>SERVICE_URL_*</c>/<c>SERVICE_FQDN_*</c>) come from the service's domains (NET-06
///             <see cref="ServiceDomain" />) for the endpoint's Compose service, preferring the endpoint port. Without a
///             domain they fall back to the internal address (<c>http://&lt;service&gt;:&lt;port&gt;</c>).
///         </item>
///     </list>
/// </summary>
public sealed class ServiceTemplateRuntimeResolver(
    IVesselDbContext dbContext,
    ISecretVault secretVault,
    TimeProvider timeProvider)
{
    /// <summary>Prefix of secret template inputs inside the service's generated-secrets vault entry.</summary>
    public const string SecretInputPrefix = "input:";

    public async Task<ServiceTemplateRuntimeValues> ResolveAsync(
        ServiceResource service,
        CoolifyComposeTemplate template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(template);
        Dictionary<string, string> stored = await EnsureGeneratedAsync(service, template, cancellationToken);
        ServiceDomain[] domains = dbContext.ServiceDomains
            .Where(domain => domain.ServiceResourceId == service.Id)
            .ToArray();
        return new ServiceTemplateRuntimeValues(
            stored.Where(pair => !pair.Key.StartsWith(SecretInputPrefix, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            RouteValues(template, domains),
            stored.Where(pair => pair.Key.StartsWith(SecretInputPrefix, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key[SecretInputPrefix.Length..], pair => pair.Value,
                    StringComparer.Ordinal));
    }

    /// <summary>
    ///     Stores the secret inputs of a new imported-template service in its vault entry (they are never written to the
    ///     service configuration JSON).
    /// </summary>
    public async Task StoreSecretInputsAsync(ServiceResource service, IReadOnlyDictionary<string, string> secretInputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(secretInputs);
        if (secretInputs.Count == 0) return;
        if (service.GeneratedSecretsReferenceId.HasValue)
            throw new InvalidOperationException("Secret inputs are stored when the service is created.");
        var payload = JsonSerializer.Serialize(secretInputs.ToDictionary(pair => SecretInputPrefix + pair.Key,
            pair => pair.Value, StringComparer.Ordinal));
        await StoreAsync(service, payload, cancellationToken);
    }

    public static IReadOnlyDictionary<string, string> RouteValues(CoolifyComposeTemplate template,
        IReadOnlyCollection<ServiceDomain> domains)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(domains);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ServiceTemplateRouteVariable route in template.RouteVariables)
        {
            ServiceTemplateEndpoint endpoint = route.Endpoint;
            ServiceDomain? domain = domains
                .Where(candidate => string.Equals(candidate.ComposeService, endpoint.ComposeService,
                                        StringComparison.Ordinal) &&
                                    candidate.HostKind != RouteHostKind.Wildcard)
                .OrderBy(candidate => candidate.TargetPort == endpoint.Port ? 0 : 1)
                .ThenBy(candidate => candidate.HostKind == RouteHostKind.Dns ? 0 : 1)
                .ThenBy(candidate => candidate.Host, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.PathPrefix, StringComparer.Ordinal)
                .FirstOrDefault();
            string scheme, host;
            var path = (domain?.PathPrefix ?? string.Empty).TrimEnd('/') + (endpoint.Path ?? string.Empty);
            if (domain is null)
            {
                scheme = "http";
                host = endpoint.ComposeService + ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                scheme = domain.TlsEnabled ? "https" : "http";
                host = domain.HostKind == RouteHostKind.IPv6 ? $"[{domain.Host}]" : domain.Host;
            }

            values[route.Variable.Name] = route.Variable.Kind == ServiceMagicVariableKind.Url
                ? $"{scheme}://{host}{path}"
                : host + path;
        }

        return values;
    }

    private async Task<Dictionary<string, string>> EnsureGeneratedAsync(ServiceResource service,
        CoolifyComposeTemplate template, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (service.GeneratedSecretsReferenceId is { } existing)
        {
            var json = await secretVault.RevealForDeploymentAsync(service.TeamId, existing, cancellationToken);
            foreach ((var key, var value) in JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [])
                values[key] = value;
        }

        var added = false;
        foreach (ServiceMagicVariable variable in template.GeneratedVariables)
        {
            if (values.ContainsKey(variable.Name)) continue;
            values[variable.Name] = ServiceMagicValueGenerator.Generate(variable);
            added = true;
        }

        if (!added) return values;
        var payload = JsonSerializer.Serialize(values);
        if (service.GeneratedSecretsReferenceId is { } reference)
        {
            await secretVault.ReplaceAsync(reference, payload, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await StoreAsync(service, payload, cancellationToken);
        }

        return values;
    }

    private async Task StoreAsync(ServiceResource service, string payload, CancellationToken cancellationToken)
    {
        SecretReference secret = await secretVault.StoreAsync(service.TeamId, SecretScope.Environment,
            $"service-template:{service.Id.Value:N}", payload, new SecretPolicy(false, false, true),
            new SecretTarget(EnvironmentId: service.EnvironmentId), cancellationToken);
        service.AttachGeneratedSecrets(secret.Id, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
