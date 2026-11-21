using Vessel.Domain.Services;

namespace Vessel.Application.ManagedServices.Templates;

/// <summary>A template document could not be imported into the catalog.</summary>
public sealed class ServiceTemplateImportException(string message) : Exception(message);

/// <summary>Where a catalog template comes from.</summary>
public static class ServiceTemplateSources
{
    /// <summary>Hard-coded Vessel templates (<see cref="ServiceTemplateCatalog" />).</summary>
    public const string Vessel = "vessel";

    /// <summary>Templates in Coolify's compose format (<c>templates/compose/*.yaml</c>), imported as data.</summary>
    public const string Coolify = "coolify";

    /// <summary>Catalog key prefix of imported Coolify templates; keeps them apart from built-in Vessel keys.</summary>
    public const string CoolifyKeyPrefix = "coolify-";
}

/// <summary>Raw template text and the catalog key it is imported under.</summary>
public sealed record ServiceTemplateDocument(string Name, string Text);

/// <summary>Supplies template documents (embedded resources today; a refreshed catalog later).</summary>
public interface IServiceTemplateSource
{
    IReadOnlyList<ServiceTemplateDocument> Load();
}

/// <summary>
///     A public endpoint of a template: the Compose service that receives traffic for a <c>SERVICE_URL_*</c>/<c>SERVICE_FQDN_*</c>
///     family and the internal port it listens on. Service domains (NET-06) for this service and port feed the variables.
/// </summary>
public sealed record ServiceTemplateEndpoint(string ComposeService, int Port, string Identifier, string? Path);

/// <summary>Display and routing metadata of a catalog template.</summary>
public sealed record ServiceTemplateMetadata(
    string Source,
    string? Documentation,
    string? Slogan,
    string? Category,
    IReadOnlyList<string> Tags,
    string? Logo,
    int? Port,
    string? MinimumVersion,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ServiceTemplateEndpoint> Endpoints);

/// <summary>A route magic variable of an imported template and the endpoint that resolves it.</summary>
public sealed record ServiceTemplateRouteVariable(ServiceMagicVariable Variable, ServiceTemplateEndpoint Endpoint);

/// <summary>
///     The processed form of an imported Coolify template: normalized Compose (no host ports, no <c>coolify.*</c> labels,
///     magic declarations rewritten to <c>KEY=${KEY}</c>), plus the magic variables Vessel resolves per service instance.
/// </summary>
public sealed record CoolifyComposeTemplate(
    string ComposeYaml,
    IReadOnlyList<ServiceMagicVariable> GeneratedVariables,
    IReadOnlyList<ServiceTemplateRouteVariable> RouteVariables,
    IReadOnlyList<string> Images);

/// <summary>
///     Per-instance values of an imported template's magic variables (secrets: never log). <c>SecretInputs</c> are the
///     secret template inputs read back from the vault (never stored in the configuration JSON).
/// </summary>
public sealed record ServiceTemplateRuntimeValues(
    IReadOnlyDictionary<string, string> GeneratedValues,
    IReadOnlyDictionary<string, string> RouteValues,
    IReadOnlyDictionary<string, string>? SecretInputs = null);

/// <summary>
///     Structural view of one service of a template's Compose document, produced by
///     <see cref="IServiceTemplateComposeProcessor" />. <c>DeclaredEnvironment</c> holds the environment keys the service
///     declares with their literal value (null when bare).
/// </summary>
public sealed record ServiceTemplateComposeService(
    string Name,
    string Image,
    IReadOnlyDictionary<string, string?> DeclaredEnvironment);

public sealed record ServiceTemplateComposeResult(
    string ComposeYaml,
    IReadOnlyList<ServiceTemplateComposeService> Services,
    IReadOnlyList<string> Warnings);

/// <summary>
///     Structural Compose processing of imported templates (YAML lives in Infrastructure). Implementations remove host
///     <c>ports:</c>, <c>container_name</c> and <c>coolify.*</c> labels, rewrite <c>SERVICE_*</c> environment declarations
///     to <c>KEY=${KEY}</c> so the values come from the generated <c>.env</c>, and reject what a one-click template must
///     not do: builds, bind mounts, file content, env files, extends, YAML anchors, host namespaces, privileged mode and
///     <c>scale</c>/<c>replicas</c> above one.
/// </summary>
public interface IServiceTemplateComposeProcessor
{
    /// <exception cref="ServiceTemplateImportException">The template uses an unsupported or unsafe construct.</exception>
    ServiceTemplateComposeResult Process(string composeYaml);
}
