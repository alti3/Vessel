using System.Globalization;
using System.Text.RegularExpressions;
using Vessel.Application.ManagedServices.Templates;
using Vessel.Domain.Services;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Vessel.Infrastructure.ManagedServices.Templates;

/// <summary>
///     YamlDotNet implementation of <see cref="IServiceTemplateComposeProcessor" /> for imported Coolify templates (TPL-01).
///     Templates never publish host ports or pin container names (two instances on one server would collide and bypass
///     the proxy), never build, bind-mount host paths, write files, use host namespaces or run privileged, and run one
///     replica per service. <c>coolify.*</c> labels are dropped; <c>SERVICE_*</c> environment declarations become
///     <c>KEY=${KEY}</c> so their per-instance values come from the generated <c>.env</c> file.
/// </summary>
public sealed partial class YamlServiceTemplateComposeProcessor : IServiceTemplateComposeProcessor
{
    private const int MaxNodes = 20_000;
    private const int MaxDepth = 32;
    private const int MaxTemplateLength = 256 * 1024;

    private static readonly string[] RejectedServiceKeys =
        ["build", "env_file", "extends", "privileged", "cap_add", "devices", "security_opt", "sysctls", "userns_mode",
         "configs", "secrets", "volumes_from"];

    private static readonly string[] HostNamespaceKeys = ["network_mode", "pid", "ipc", "uts", "cgroup"];

    public ServiceTemplateComposeResult Process(string composeYaml)
    {
        ArgumentNullException.ThrowIfNull(composeYaml);
        if (composeYaml.Length > MaxTemplateLength)
            throw new ServiceTemplateImportException("The template is too large.");
        YamlMappingNode root = Load(composeYaml);
        var budget = 0;
        Inspect(root, 0, ref budget);

        foreach (var key in root.Children.Keys.Select(KeyText))
            if (key is not ("services" or "volumes" or "networks") && !key.StartsWith("x-", StringComparison.Ordinal))
                throw new ServiceTemplateImportException($"Top-level Compose key '{key}' is not supported.");
        if (!TryGet(root, "services", out YamlNode? servicesNode) || servicesNode is not YamlMappingNode services ||
            services.Children.Count == 0)
            throw new ServiceTemplateImportException("The template must define services.");
        if (TryGet(root, "volumes", out YamlNode? volumesNode) && volumesNode is YamlMappingNode volumes)
            foreach ((YamlNode name, YamlNode definition) in volumes.Children)
                if (definition is YamlMappingNode volume &&
                    (TryGet(volume, "driver_opts", out _) || TryGet(volume, "external", out _)))
                    throw new ServiceTemplateImportException(
                        $"Volume '{KeyText(name)}' must be a plain named volume.");

        var warnings = new List<string>();
        var results = new List<ServiceTemplateComposeService>();
        foreach ((YamlNode nameNode, YamlNode serviceNode) in services.Children)
        {
            var name = KeyText(nameNode);
            if (!ServiceNamePattern().IsMatch(name))
                throw new ServiceTemplateImportException($"Compose service name '{name}' is invalid.");
            if (serviceNode is not YamlMappingNode service)
                throw new ServiceTemplateImportException($"Compose service '{name}' must be a mapping.");
            results.Add(ProcessService(name, service, warnings));
        }

        return new ServiceTemplateComposeResult(Serialize(root), results, warnings);
    }

    private static ServiceTemplateComposeService ProcessService(string name, YamlMappingNode service,
        List<string> warnings)
    {
        foreach (var key in RejectedServiceKeys)
            if (TryGet(service, key, out YamlNode? value) && !IsFalse(value))
                throw new ServiceTemplateImportException($"Service '{name}' uses '{key}', which templates may not use.");
        foreach (var key in HostNamespaceKeys)
            if (TryGet(service, key, out YamlNode? value) && !IsNull(value))
                throw new ServiceTemplateImportException($"Service '{name}' sets '{key}'; host namespaces are not allowed.");

        if (!TryGet(service, "image", out YamlNode? imageNode) || imageNode is not YamlScalarNode { Value: { } image } ||
            string.IsNullOrWhiteSpace(image))
            throw new ServiceTemplateImportException($"Service '{name}' must use an image.");

        if (Remove(service, "ports"))
            warnings.Add($"Service '{name}': published host ports were removed; route it through a service domain.");
        if (Remove(service, "container_name"))
            warnings.Add($"Service '{name}': the fixed container name was removed so instances do not collide.");
        if (TryGet(service, "scale", out YamlNode? scale) && Number(scale) > 1)
            throw new ServiceTemplateImportException($"Service '{name}' sets scale above 1.");
        if (TryGet(service, "deploy", out YamlNode? deployNode) && deployNode is YamlMappingNode deploy &&
            TryGet(deploy, "replicas", out YamlNode? replicas) && Number(replicas) > 1)
            throw new ServiceTemplateImportException($"Service '{name}' sets replicas above 1.");

        CheckVolumes(name, service);
        StripCoolifyLabels(service);
        Dictionary<string, string?> declared = RewriteEnvironment(name, service);
        return new ServiceTemplateComposeService(name, image.Trim(), declared);
    }

    private static void CheckVolumes(string name, YamlMappingNode service)
    {
        if (!TryGet(service, "volumes", out YamlNode? volumesNode) || IsNull(volumesNode)) return;
        if (volumesNode is not YamlSequenceNode volumes)
            throw new ServiceTemplateImportException($"Service '{name}' volumes must be a list.");
        foreach (YamlNode entry in volumes.Children)
            switch (entry)
            {
                case YamlScalarNode { Value: { } text }:
                    {
                        var source = text.Split(':')[0];
                        if (text.Contains(':', StringComparison.Ordinal) && !NamedVolumePattern().IsMatch(source))
                            throw new ServiceTemplateImportException(
                                $"Service '{name}' bind-mounts '{source}'; templates may use named volumes only.");
                        break;
                    }
                case YamlMappingNode mount:
                    {
                        if (TryGet(mount, "content", out _) || TryGet(mount, "is_directory", out _))
                            throw new ServiceTemplateImportException(
                                $"Service '{name}' writes files through volume content, which is not supported.");
                        var type = TryGet(mount, "type", out YamlNode? typeNode) && typeNode is YamlScalarNode t
                            ? t.Value
                            : "volume";
                        var source = TryGet(mount, "source", out YamlNode? sourceNode) &&
                                     sourceNode is YamlScalarNode s
                            ? s.Value ?? string.Empty
                            : string.Empty;
                        if (type is not ("volume" or "tmpfs") ||
                            (type == "volume" && source.Length > 0 && !NamedVolumePattern().IsMatch(source)))
                            throw new ServiceTemplateImportException(
                                $"Service '{name}' uses a '{type}' mount; templates may use named volumes only.");
                        break;
                    }
                default:
                    throw new ServiceTemplateImportException($"Service '{name}' has an invalid volume entry.");
            }
    }

    private static void StripCoolifyLabels(YamlMappingNode service)
    {
        if (!TryGet(service, "labels", out YamlNode? labelsNode)) return;
        switch (labelsNode)
        {
            case YamlMappingNode labels:
                foreach (YamlNode key in labels.Children.Keys.Where(key => IsCoolifyLabel(KeyText(key))).ToArray())
                    labels.Children.Remove(key);
                if (labels.Children.Count == 0) Remove(service, "labels");
                break;
            case YamlSequenceNode list:
                {
                    YamlNode[] kept = list.Children
                        .Where(item => item is not YamlScalarNode { Value: { } text } || !IsCoolifyLabel(text))
                        .ToArray();
                    list.Children.Clear();
                    foreach (YamlNode item in kept) list.Children.Add(item);
                    if (kept.Length == 0) Remove(service, "labels");
                    break;
                }
        }
    }

    private static bool IsCoolifyLabel(string label)
    {
        return label.StartsWith("coolify.", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string?> RewriteEnvironment(string name, YamlMappingNode service)
    {
        var declared = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!TryGet(service, "environment", out YamlNode? environmentNode) || IsNull(environmentNode))
            return declared;
        switch (environmentNode)
        {
            case YamlSequenceNode list:
                for (var index = 0; index < list.Children.Count; index++)
                {
                    if (list.Children[index] is not YamlScalarNode { Value: { } entry })
                        throw new ServiceTemplateImportException($"Service '{name}' has an invalid environment entry.");
                    var separator = entry.IndexOf('=', StringComparison.Ordinal);
                    var key = (separator < 0 ? entry : entry[..separator]).Trim();
                    string? value = separator < 0 ? null : entry[(separator + 1)..];
                    if (!declared.TryAdd(key, value))
                        throw new ServiceTemplateImportException($"Service '{name}' declares '{key}' twice.");
                    if (IsMagicDeclaration(key, value))
                        list.Children[index] = new YamlScalarNode($"{key}=${{{key}}}");
                }

                break;
            case YamlMappingNode mapping:
                foreach (YamlNode keyNode in mapping.Children.Keys.ToArray())
                {
                    var key = KeyText(keyNode);
                    var value = mapping.Children[keyNode] is YamlScalarNode scalar ? scalar.Value : null;
                    declared[key] = string.IsNullOrEmpty(value) ? null : value;
                    if (IsMagicDeclaration(key, value))
                        mapping.Children[keyNode] = new YamlScalarNode($"${{{key}}}");
                }

                break;
            default:
                throw new ServiceTemplateImportException($"Service '{name}' environment must be a list or mapping.");
        }

        return declared;
    }

    /// <summary>
    ///     A magic variable declared without a value or with a literal value (a route path such as <c>/api</c>) is
    ///     resolved by Vessel; a declaration that already interpolates something else is left alone.
    /// </summary>
    private static bool IsMagicDeclaration(string key, string? value)
    {
        return ServiceMagicVariable.IsReserved(key) &&
               (string.IsNullOrEmpty(value) || !value.Contains('$', StringComparison.Ordinal));
    }

    private static YamlMappingNode Load(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml.TrimStart('﻿'));
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new ServiceTemplateImportException($"The template is not valid YAML: {ex.Message}");
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new ServiceTemplateImportException("The template must contain exactly one YAML mapping document.");
        return root;
    }

    /// <summary>Bounds the document and rejects anchors, aliases and merge keys (they would bypass the checks).</summary>
    private static void Inspect(YamlNode node, int depth, ref int budget)
    {
        if (++budget > MaxNodes) throw new ServiceTemplateImportException("The template is too large.");
        if (depth > MaxDepth) throw new ServiceTemplateImportException("The template is nested too deeply.");
        if (!node.Anchor.IsEmpty)
            throw new ServiceTemplateImportException("YAML anchors and aliases are not supported in templates.");
        switch (node)
        {
            case YamlSequenceNode sequence:
                foreach (YamlNode child in sequence.Children) Inspect(child, depth + 1, ref budget);
                break;
            case YamlMappingNode mapping:
                foreach ((YamlNode key, YamlNode value) in mapping.Children)
                {
                    if (key is YamlScalarNode { Value: "<<" })
                        throw new ServiceTemplateImportException("YAML merge keys are not supported in templates.");
                    Inspect(key, depth + 1, ref budget);
                    Inspect(value, depth + 1, ref budget);
                }

                break;
        }
    }

    private static string Serialize(YamlMappingNode root)
    {
        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, false);
        var text = writer.ToString();
        return text.EndsWith("...\n", StringComparison.Ordinal) || text.EndsWith("...\r\n", StringComparison.Ordinal)
            ? text[..text.LastIndexOf("...", StringComparison.Ordinal)]
            : text;
    }

    private static bool TryGet(YamlMappingNode mapping, string key, out YamlNode? value)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out value);
    }

    private static bool Remove(YamlMappingNode mapping, string key)
    {
        return mapping.Children.Remove(new YamlScalarNode(key));
    }

    private static string KeyText(YamlNode node)
    {
        return node is YamlScalarNode { Value: { } value }
            ? value
            : throw new ServiceTemplateImportException("Compose keys must be scalars.");
    }

    private static bool IsNull(YamlNode? node)
    {
        return node is null or YamlScalarNode { Value: null or "" or "~" or "null" };
    }

    private static bool IsFalse(YamlNode? node)
    {
        return node is YamlScalarNode { Value: "false" or "False" or "FALSE" };
    }

    private static int Number(YamlNode? node)
    {
        return node is YamlScalarNode { Value: { } text } &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : int.MaxValue;
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNamePattern();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamedVolumePattern();
}
