using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vessel.Domain.Services;

namespace Vessel.Application.ManagedServices.Templates;

/// <summary>A template document that was not imported, and why (shown to operators, never secrets).</summary>
public sealed record ServiceTemplateImportError(string Name, string Reason);

public sealed record ServiceTemplateImportResult(
    IReadOnlyList<ServiceTemplateDefinition> Templates,
    IReadOnlyList<ServiceTemplateImportError> Errors);

/// <summary>
///     Imports templates in Coolify's compose format into <see cref="ServiceTemplateDefinition" />s (TPL-01): header
///     metadata, Compose normalization (<see cref="IServiceTemplateComposeProcessor" />), image pinning
///     (<see cref="ServiceTemplateImagePolicy" />), magic variables (<see cref="ServiceMagicVariable" />) and the remaining
///     <c>${VAR}</c> references, which become optional inputs (required for <c>${VAR:?}</c>) with the template default.
/// </summary>
public sealed partial class CoolifyServiceTemplateImporter(IServiceTemplateComposeProcessor processor)
{
    public ServiceTemplateImportResult ImportAll(IServiceTemplateSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var templates = new List<ServiceTemplateDefinition>();
        var errors = new List<ServiceTemplateImportError>();
        foreach (ServiceTemplateDocument document in source.Load())
            try
            {
                templates.Add(Import(document));
            }
            catch (ServiceTemplateImportException ex)
            {
                errors.Add(new ServiceTemplateImportError(document.Name, ex.Message));
            }

        return new ServiceTemplateImportResult(templates, errors);
    }

    /// <exception cref="ServiceTemplateImportException">The document cannot be imported.</exception>
    public ServiceTemplateDefinition Import(ServiceTemplateDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!TemplateNamePattern().IsMatch(document.Name))
            throw new ServiceTemplateImportException($"Template name '{document.Name}' is invalid.");
        ServiceTemplateHeader header = ServiceTemplateHeaderParser.Parse(document.Text);
        if (header.Ignore)
            throw new ServiceTemplateImportException("The template is marked '# ignore: true' upstream.");
        if (header.EnvironmentFile is not null)
            throw new ServiceTemplateImportException("Templates with an '# env_file:' header are not supported yet.");

        ServiceTemplateComposeResult compose = processor.Process(document.Text);
        if (compose.Services.Count == 0)
            throw new ServiceTemplateImportException("The template defines no services.");
        var warnings = new List<string>(compose.Warnings);
        foreach (ServiceTemplateComposeService service in compose.Services)
        {
            var warning = ServiceTemplateImagePolicy.Check(service.Name, service.Image);
            if (warning is not null) warnings.Add(warning);
        }

        IReadOnlyList<ServiceTemplateVariableReference> references =
            ServiceTemplateVariables.References(compose.ComposeYaml);
        var magic = new Dictionary<string, ServiceMagicVariable>(StringComparer.Ordinal);
        var inputs = new Dictionary<string, ServiceTemplateInput>(StringComparer.Ordinal);
        foreach (var name in references.Select(reference => reference.Name)
                     .Concat(compose.Services.SelectMany(service => service.DeclaredEnvironment.Keys)
                         .Where(ServiceMagicVariable.IsReserved)))
        {
            if (ServiceMagicVariable.IsReserved(name))
            {
                if (!ServiceMagicVariable.TryParse(name, out ServiceMagicVariable? variable, out var error))
                    throw new ServiceTemplateImportException($"Magic variable '{name}' is not supported: {error}");
                magic.TryAdd(name, variable!);
                continue;
            }

            if (name.StartsWith("COOLIFY_", StringComparison.Ordinal))
                throw new ServiceTemplateImportException($"Coolify variable '{name}' is not supported.");
            if (!string.Equals(ServiceTemplateCatalog.EnvironmentKey(name), name, StringComparison.Ordinal))
                throw new ServiceTemplateImportException(
                    $"Variable '{name}' must be upper case to be passed through the environment file.");
            if (inputs.ContainsKey(name)) continue;
            ServiceTemplateVariableReference[] uses = references.Where(reference => reference.Name == name).ToArray();
            var defaultValue = uses.Select(use => use.Default).FirstOrDefault(value => !string.IsNullOrEmpty(value));
            inputs[name] = new ServiceTemplateInput(name, Label(name), uses.Any(use => use.Required), IsSecret(name),
                defaultValue);
        }

        IReadOnlyList<ServiceTemplateRouteVariable> routes = ResolveRoutes(magic.Values, compose, header);
        ServiceMagicVariable[] generated = magic.Values
            .Where(variable => variable.Kind == ServiceMagicVariableKind.Generated)
            .OrderBy(variable => variable.Name, StringComparer.Ordinal)
            .ToArray();
        ServiceTemplateEndpoint[] endpoints = routes.Select(route => route.Endpoint).Distinct()
            .OrderBy(endpoint => endpoint.Identifier, StringComparer.Ordinal).ToArray();

        var key = ServiceTemplateSources.CoolifyKeyPrefix + document.Name;
        var metadata = new ServiceTemplateMetadata(ServiceTemplateSources.Coolify, header.Documentation,
            header.Slogan, header.Category, header.Tags, header.Logo, header.Port ?? endpoints.FirstOrDefault()?.Port,
            header.MinimumVersion, warnings, endpoints);
        var template = new CoolifyComposeTemplate(compose.ComposeYaml, generated, routes,
            compose.Services.Select(service => service.Image).ToArray());
        var yaml = compose.ComposeYaml;
        return new ServiceTemplateDefinition(key, DisplayName(document.Name),
            header.Slogan ?? $"{DisplayName(document.Name)} (Coolify template).", Version(document.Text),
            inputs.Values.OrderBy(input => input.Key, StringComparer.Ordinal).ToArray(), _ => yaml, metadata,
            template);
    }

    private static List<ServiceTemplateRouteVariable> ResolveRoutes(
        IEnumerable<ServiceMagicVariable> variables,
        ServiceTemplateComposeResult compose,
        ServiceTemplateHeader header)
    {
        var routes = new List<ServiceTemplateRouteVariable>();
        foreach (IGrouping<string, ServiceMagicVariable> family in variables.Where(variable => variable.IsRoute)
                     .GroupBy(variable => variable.Identifier, StringComparer.Ordinal))
        {
            // The service that declares a variable of the family receives the traffic (upstream: the declaring
            // service application gets the FQDN).
            ServiceTemplateComposeService? declaring = compose.Services.FirstOrDefault(service =>
                service.DeclaredEnvironment.Keys.Any(key =>
                    ServiceMagicVariable.TryParse(key, out ServiceMagicVariable? declared) && declared!.IsRoute &&
                    declared.Identifier == family.Key));
            declaring ??= compose.Services.FirstOrDefault(service =>
                Normalize(service.Name) == Normalize(family.Key));
            declaring ??= compose.Services.Count == 1 ? compose.Services[0] : null;
            if (declaring is null)
                throw new ServiceTemplateImportException(
                    $"No Compose service receives the traffic of SERVICE_URL_{family.Key}.");

            var port = family.Select(variable => variable.Port).FirstOrDefault(value => value.HasValue) ??
                       declaring.DeclaredEnvironment.Keys
                           .Select(key => ServiceMagicVariable.TryParse(key, out ServiceMagicVariable? declared) &&
                                          declared!.IsRoute && declared.Identifier == family.Key
                               ? declared.Port
                               : null)
                           .FirstOrDefault(value => value.HasValue) ??
                       header.Port ??
                       throw new ServiceTemplateImportException(
                           $"SERVICE_URL_{family.Key} has no port and the template has no '# port:' header.");
            var path = declaring.DeclaredEnvironment
                .Where(pair => ServiceMagicVariable.TryParse(pair.Key, out ServiceMagicVariable? declared) &&
                               declared!.IsRoute && declared.Identifier == family.Key)
                .Select(pair => pair.Value)
                .FirstOrDefault(value => value is { Length: > 1 } && value.StartsWith('/') &&
                                         PathPattern().IsMatch(value));
            var endpoint = new ServiceTemplateEndpoint(declaring.Name, port, family.Key, path);
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (ServiceMagicVariable variable in family) names.UnionWith(variable.RouteFamily());
            names.UnionWith(ServiceMagicVariable.Parse($"SERVICE_URL_{family.Key}_" +
                                                        port.ToString(CultureInfo.InvariantCulture)).RouteFamily());
            routes.AddRange(names.Select(name => new ServiceTemplateRouteVariable(ServiceMagicVariable.Parse(name),
                endpoint)));
        }

        return routes;
    }

    private static string Normalize(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }

    private static string DisplayName(string name)
    {
        return string.Join(' ', name.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length <= 2 && word.All(char.IsLetter)
                ? word.ToUpperInvariant()
                : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string Label(string name)
    {
        return name.Replace('_', ' ');
    }

    private static bool IsSecret(string name)
    {
        return name.Contains("PASSWORD", StringComparison.Ordinal) || name.Contains("SECRET", StringComparison.Ordinal) ||
               name.Contains("TOKEN", StringComparison.Ordinal) || name.EndsWith("_KEY", StringComparison.Ordinal);
    }

    /// <summary>Content version of an imported template (it changes whenever the upstream file changes).</summary>
    public static string Version(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n")));
        return "coolify-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateNamePattern();

    [GeneratedRegex("^/[A-Za-z0-9._~/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}
