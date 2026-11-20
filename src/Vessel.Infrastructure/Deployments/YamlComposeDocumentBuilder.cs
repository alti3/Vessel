using System.Globalization;
using System.Text.RegularExpressions;
using Vessel.Application.Deployments;
using Vessel.Application.Files;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Vessel.Infrastructure.Deployments;

/// <summary>
///     Builds and normalizes deployment compose documents with YamlDotNet's representation model. Repository compose
///     files are parsed (BOM-safe, alias/merge-key expanded with a node budget), validated for path containment and
///     rewritten so the primary service joins the server network under the application's upstream alias.
/// </summary>
public sealed partial class YamlComposeDocumentBuilder(IPathSafetyService paths) : IComposeDocumentBuilder
{
    private const int MaxNodes = 20_000;
    private const int MaxDepth = 64;

    public string CreateDockerfileStack(DockerfileStackSpec spec)
    {
        var service = new YamlMappingNode
        {
            { "image", Str(spec.ImageName) },
            {
                "build", new YamlMappingNode
                {
                    { "context", Str(spec.BuildContext) },
                    { "dockerfile", Str(spec.DockerfilePath) }
                }
            },
            { "env_file", new YamlSequenceNode(Str(".env")) },
            { "labels", Labels(spec.Labels) },
            {
                "networks", new YamlMappingNode
                {
                    { spec.NetworkName, new YamlMappingNode { { "aliases", new YamlSequenceNode(Str(spec.UpstreamAlias)) } } }
                }
            },
            { "ports", new YamlSequenceNode(Str(spec.HostPortBinding)) },
            { "restart", Str("unless-stopped") }
        };

        var root = new YamlMappingNode
        {
            { "services", new YamlMappingNode { { spec.ServiceName, service } } },
            { "networks", ExternalNetworks(spec.NetworkName) }
        };
        return Serialize(root);
    }

    public ComposeNormalizationResult NormalizeRepositoryCompose(string yaml, RepositoryComposeSpec spec)
    {
        var warnings = new List<string>();
        var repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spec.RepositoryDirectory));
        var composeFile = ResolveInside(repository, repository, spec.ComposeFilePath, "compose file path");
        var composeDirectory = Path.GetDirectoryName(composeFile) ?? repository;

        YamlMappingNode root = Load(yaml);
        if (root.Children.ContainsKey(Key("include")))
            throw new ComposeValidationException("Compose 'include' is not supported; inline the included services.");
        if (!TryGet(root, "services", out YamlNode? servicesNode) || servicesNode is not YamlMappingNode services ||
            services.Children.Count == 0)
            throw new ComposeValidationException("Compose file must define at least one service under 'services'.");

        var context = new PathContext(repository, composeDirectory);
        var serviceNames = new List<string>();
        foreach (var (keyNode, valueNode) in services.Children)
        {
            var name = ((YamlScalarNode)keyNode).Value ?? string.Empty;
            if (!ServiceNamePattern().IsMatch(name))
                throw new ComposeValidationException($"Compose service name '{name}' is invalid.");
            if (valueNode is not YamlMappingNode)
                throw new ComposeValidationException($"Compose service '{name}' must be a mapping.");
            serviceNames.Add(name);
        }

        var primary = SelectPrimaryService(services, serviceNames, spec.ExposedPort);
        foreach (var name in serviceNames)
        {
            var service = (YamlMappingNode)services.Children[Key(name)];
            NormalizeServicePaths(name, service, context);
            AddLabels(service, spec.Labels);
            if (spec.PreviewSuffix is not null)
                IsolatePreviewService(name, service, spec.PreviewSuffix, name == primary, spec.ExposedPort, warnings);
        }

        NormalizeTopLevelFiles(root, "secrets", context);
        NormalizeTopLevelFiles(root, "configs", context);
        // Lexical containment is not enough for a cloned (untrusted) tree: a symlink such as ./data -> / would
        // escape. Every resolved host path must also stay inside the repository after resolving existing links.
        foreach (var resolved in context.Resolved)
        {
            try
            {
                paths.EnsureOwnedPath(repository, resolved);
            }
            catch (InvalidOperationException)
            {
                throw new ComposeValidationException(
                    "A compose path resolves outside the repository through a symbolic link.");
            }
        }

        if (spec.PreviewSuffix is not null)
        {
            NamespaceTopLevelNames(root, "volumes", spec.PreviewSuffix, warnings);
            NamespaceTopLevelNames(root, "networks", spec.PreviewSuffix, warnings);
        }

        AttachPrimaryToServerNetwork((YamlMappingNode)services.Children[Key(primary)], primary, spec);
        if (!TryGet(root, "networks", out YamlNode? networksNode) || networksNode is not YamlMappingNode networks)
        {
            if (networksNode is not null && networksNode is not YamlMappingNode && !IsNull(networksNode))
                throw new ComposeValidationException("Compose top-level 'networks' must be a mapping.");
            networks = new YamlMappingNode();
            root.Children[Key("networks")] = networks;
        }

        networks.Children[Key(spec.NetworkName)] = new YamlMappingNode { { "external", Bool(true) } };
        return new ComposeNormalizationResult(Serialize(root), primary, warnings);
    }

    private static YamlMappingNode Load(string yaml)
    {
        var text = yaml.TrimStart('﻿');
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(text);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new ComposeValidationException($"Compose file is not valid YAML: {ex.Message}");
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new ComposeValidationException("Compose file must contain exactly one YAML mapping document.");

        var budget = 0;
        return (YamlMappingNode)Materialize(root, 0, ref budget);
    }

    /// <summary>
    ///     Deep-copies the document so aliases become independent nodes and <c>&lt;&lt;</c> merge keys are applied. Without
    ///     this, a bind mount merged in through an anchor would bypass validation. The node budget bounds alias expansion.
    /// </summary>
    private static YamlNode Materialize(YamlNode node, int depth, ref int budget)
    {
        if (++budget > MaxNodes)
            throw new ComposeValidationException("Compose file is too large or expands too many YAML aliases.");
        if (depth > MaxDepth)
            throw new ComposeValidationException("Compose file is nested too deeply.");

        switch (node)
        {
            case YamlScalarNode scalar:
                return new YamlScalarNode(scalar.Value) { Style = scalar.Style, Tag = scalar.Tag };
            case YamlSequenceNode sequence:
            {
                var copy = new YamlSequenceNode { Tag = sequence.Tag };
                foreach (YamlNode child in sequence.Children)
                    copy.Add(Materialize(child, depth + 1, ref budget));
                return copy;
            }
            case YamlMappingNode mapping:
            {
                var copy = new YamlMappingNode { Tag = mapping.Tag };
                var merges = new List<YamlMappingNode>();
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is YamlScalarNode { Value: "<<" } && key.Tag.IsEmpty)
                    {
                        YamlNode merged = Materialize(value, depth + 1, ref budget);
                        if (merged is YamlMappingNode single) merges.Add(single);
                        else if (merged is YamlSequenceNode many && many.Children.All(item => item is YamlMappingNode))
                            merges.AddRange(many.Children.Cast<YamlMappingNode>());
                        else throw new ComposeValidationException("Compose merge key '<<' must reference mappings.");
                        continue;
                    }

                    YamlNode keyCopy = Materialize(key, depth + 1, ref budget);
                    if (copy.Children.ContainsKey(keyCopy))
                        throw new ComposeValidationException($"Compose file has a duplicate key '{key}'.");
                    copy.Children[keyCopy] = Materialize(value, depth + 1, ref budget);
                }

                foreach (YamlMappingNode merge in merges)
                foreach (var (key, value) in merge.Children)
                    if (!copy.Children.ContainsKey(key))
                        copy.Children[key] = value;
                return copy;
            }
            default:
                throw new ComposeValidationException("Compose file contains an unsupported YAML node.");
        }
    }

    private static string SelectPrimaryService(YamlMappingNode services, List<string> names, int port)
    {
        var portText = port.ToString(CultureInfo.InvariantCulture);
        foreach (var name in names)
        {
            var service = (YamlMappingNode)services.Children[Key(name)];
            if (ContainerPorts(service).Contains(portText)) return name;
        }

        foreach (var name in names)
        {
            var service = (YamlMappingNode)services.Children[Key(name)];
            if (TryGet(service, "build", out _) || ContainerPorts(service).Count > 0) return name;
        }

        return names[0];
    }

    private static HashSet<string> ContainerPorts(YamlMappingNode service)
    {
        var ports = new HashSet<string>(StringComparer.Ordinal);
        if (TryGet(service, "ports", out YamlNode? portsNode) && portsNode is YamlSequenceNode portList)
            foreach (YamlNode item in portList.Children)
            {
                if (item is YamlScalarNode { Value: not null } scalar)
                {
                    var spec = scalar.Value.Split('/')[0];
                    var container = spec[(spec.LastIndexOf(':') + 1)..];
                    ports.Add(container.Split('-')[0].Trim());
                }
                else if (item is YamlMappingNode mapping && TryGetScalar(mapping, "target", out var target))
                {
                    ports.Add(target.Trim());
                }
            }

        if (TryGet(service, "expose", out YamlNode? exposeNode) && exposeNode is YamlSequenceNode exposeList)
            foreach (YamlScalarNode item in exposeList.Children.OfType<YamlScalarNode>())
                if (item.Value is not null)
                    ports.Add(item.Value.Split('/')[0].Split('-')[0].Trim());
        return ports;
    }

    private static void NormalizeServicePaths(string name, YamlMappingNode service, PathContext context)
    {
        if (TryGet(service, "build", out YamlNode? build))
            NormalizeBuild(name, service, build!, context);

        if (TryGet(service, "env_file", out YamlNode? envFile))
            service.Children[Key("env_file")] = NormalizeEnvFiles(name, envFile!, context);

        if (TryGet(service, "volumes", out YamlNode? volumes))
        {
            if (volumes is not YamlSequenceNode volumeList)
                throw new ComposeValidationException($"Service '{name}' volumes must be a list.");
            for (var index = 0; index < volumeList.Children.Count; index++)
                volumeList.Children[index] = NormalizeVolume(name, volumeList.Children[index], context);
        }

        if (TryGet(service, "extends", out YamlNode? extends) && extends is YamlMappingNode extendsMap &&
            TryGetScalar(extendsMap, "file", out var extendsFile))
            extendsMap.Children[Key("file")] =
                Str(context.Resolve(context.ComposeDirectory, extendsFile,
                    $"service '{name}' extends file"));
    }

    private static void NormalizeBuild(string name, YamlMappingNode service, YamlNode build, PathContext context)
    {
        if (build is YamlScalarNode { Value: not null } scalar)
        {
            service.Children[Key("build")] = IsRemoteContext(scalar.Value)
                ? scalar
                : Str(context.Resolve(context.ComposeDirectory, scalar.Value,
                    $"service '{name}' build context"));
            return;
        }

        if (build is not YamlMappingNode buildMap)
            throw new ComposeValidationException($"Service '{name}' build must be a path or a mapping.");

        var contextValue = TryGetScalar(buildMap, "context", out var configured) ? configured : ".";
        if (IsRemoteContext(contextValue)) return;

        var resolvedContext = context.Resolve(context.ComposeDirectory, contextValue,
            $"service '{name}' build context");
        buildMap.Children[Key("context")] = Str(resolvedContext);
        if (TryGetScalar(buildMap, "dockerfile", out var dockerfile))
            context.Resolve(resolvedContext, dockerfile, $"service '{name}' dockerfile");

        if (TryGet(buildMap, "additional_contexts", out YamlNode? additional) && additional is YamlMappingNode extra)
            foreach (YamlNode key in extra.Children.Keys.ToArray())
                if (extra.Children[key] is YamlScalarNode { Value: not null } value && !IsRemoteContext(value.Value) &&
                    !value.Value.Contains(':', StringComparison.Ordinal))
                    extra.Children[key] = Str(context.Resolve(context.ComposeDirectory, value.Value,
                        $"service '{name}' additional build context"));
    }

    private static YamlNode NormalizeEnvFiles(string name, YamlNode envFile, PathContext context)
    {
        string Resolve(string path)
        {
            return context.Resolve(context.ComposeDirectory, path, $"service '{name}' env_file");
        }

        switch (envFile)
        {
            case YamlScalarNode { Value: not null } scalar:
                return Str(Resolve(scalar.Value));
            case YamlSequenceNode list:
                for (var index = 0; index < list.Children.Count; index++)
                    list.Children[index] = list.Children[index] switch
                    {
                        YamlScalarNode { Value: not null } item => Str(Resolve(item.Value)),
                        YamlMappingNode item when TryGetScalar(item, "path", out var path) => WithValue(item, "path",
                            Str(Resolve(path))),
                        _ => throw new ComposeValidationException($"Service '{name}' env_file entry is invalid.")
                    };
                return list;
            default:
                throw new ComposeValidationException($"Service '{name}' env_file is invalid.");
        }
    }

    private static YamlNode NormalizeVolume(string name, YamlNode volume, PathContext context)
    {
        if (volume is YamlScalarNode { Value: not null } scalar)
        {
            var value = scalar.Value;
            var separator = value.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0) return scalar; // anonymous volume (container path only)
            var source = value[..separator];
            if (!IsHostPathSource(source))
                return scalar; // named volume
            var resolved = context.Resolve(context.ComposeDirectory, source,
                $"service '{name}' bind mount source");
            return Str(resolved + value[separator..]);
        }

        if (volume is YamlMappingNode mapping)
        {
            var type = TryGetScalar(mapping, "type", out var configuredType) ? configuredType : null;
            if (!TryGetScalar(mapping, "source", out var source)) return mapping;
            var isBind = string.Equals(type, "bind", StringComparison.Ordinal) ||
                         (type is null && IsHostPathSource(source));
            if (!isBind) return mapping;
            return WithValue(mapping, "source",
                Str(context.Resolve(context.ComposeDirectory, source,
                    $"service '{name}' bind mount source")));
        }

        throw new ComposeValidationException($"Service '{name}' has an invalid volume entry.");
    }

    private static void NormalizeTopLevelFiles(YamlMappingNode root, string section, PathContext context)
    {
        if (!TryGet(root, section, out YamlNode? node) || node is not YamlMappingNode entries) return;
        foreach (var (key, value) in entries.Children)
            if (value is YamlMappingNode entry && TryGetScalar(entry, "file", out var file))
                entry.Children[Key("file")] = Str(context.Resolve(context.ComposeDirectory, file,
                    $"{section} '{key}' file"));
    }

    private static void IsolatePreviewService(string name, YamlMappingNode service, string suffix, bool primary,
        int port, List<string> warnings)
    {
        if (TryGetScalar(service, "container_name", out var containerName))
            service.Children[Key("container_name")] = Str($"{containerName}-{suffix}");

        if (service.Children.Remove(Key("ports")) && !primary)
            warnings.Add($"Preview: removed published host ports from service '{name}' to avoid collisions.");
        if (primary)
            service.Children[Key("ports")] =
                new YamlSequenceNode(Str("127.0.0.1::" + port.ToString(CultureInfo.InvariantCulture)));
    }

    private static void NamespaceTopLevelNames(YamlMappingNode root, string section, string suffix,
        List<string> warnings)
    {
        if (!TryGet(root, section, out YamlNode? node) || node is not YamlMappingNode entries) return;
        foreach (var (key, value) in entries.Children)
        {
            if (value is not YamlMappingNode entry) continue;
            var external = TryGetScalar(entry, "external", out var externalValue) &&
                           string.Equals(externalValue, "true", StringComparison.OrdinalIgnoreCase);
            if (external)
            {
                warnings.Add($"Preview: external {section[..^1]} '{key}' is shared with production.");
                continue;
            }

            if (TryGetScalar(entry, "name", out var explicitName))
                entry.Children[Key("name")] = Str($"{explicitName}-{suffix}");
        }
    }

    private static void AttachPrimaryToServerNetwork(YamlMappingNode service, string name, RepositoryComposeSpec spec)
    {
        if (TryGet(service, "network_mode", out _))
            throw new ComposeValidationException(
                $"Primary service '{name}' sets network_mode and cannot join the server network for routing.");

        YamlMappingNode networks;
        if (!TryGet(service, "networks", out YamlNode? existing) || IsNull(existing))
        {
            networks = new YamlMappingNode { { "default", new YamlMappingNode() } };
        }
        else if (existing is YamlSequenceNode list)
        {
            networks = new YamlMappingNode();
            foreach (YamlScalarNode item in list.Children.OfType<YamlScalarNode>())
                networks.Children[Key(item.Value ?? string.Empty)] = new YamlMappingNode();
        }
        else if (existing is YamlMappingNode map)
        {
            networks = map;
        }
        else
        {
            throw new ComposeValidationException($"Service '{name}' networks must be a list or a mapping.");
        }

        var attachment = networks.Children.TryGetValue(Key(spec.NetworkName), out YamlNode? current) &&
                         current is YamlMappingNode currentMap
            ? currentMap
            : new YamlMappingNode();
        var aliases = TryGet(attachment, "aliases", out YamlNode? aliasNode) && aliasNode is YamlSequenceNode aliasList
            ? aliasList
            : new YamlSequenceNode();
        if (!aliases.Children.OfType<YamlScalarNode>().Any(alias => alias.Value == spec.UpstreamAlias))
            aliases.Add(Str(spec.UpstreamAlias));
        attachment.Children[Key("aliases")] = aliases;
        networks.Children[Key(spec.NetworkName)] = attachment;
        service.Children[Key("networks")] = networks;
    }

    private static void AddLabels(YamlMappingNode service, IReadOnlyDictionary<string, string> labels)
    {
        if (labels.Count == 0) return;
        if (!TryGet(service, "labels", out YamlNode? existing) || IsNull(existing))
        {
            service.Children[Key("labels")] = Labels(labels);
            return;
        }

        switch (existing)
        {
            case YamlMappingNode map:
                foreach (var (key, value) in labels) map.Children[Key(key)] = Str(value);
                break;
            case YamlSequenceNode list:
                foreach (var (key, value) in labels) list.Add(Str($"{key}={value}"));
                break;
            default:
                throw new ComposeValidationException("Compose service labels must be a list or a mapping.");
        }
    }

    /// <summary>
    ///     Resolves a compose-relative path and requires it to stay inside the repository. Absolute host paths, home
    ///     directory paths and variable-interpolated paths are rejected because they cannot be contained.
    /// </summary>
    private static string ResolveInside(string repository, string baseDirectory, string rawPath, string field)
    {
        var path = rawPath.Trim();
        if (path.Length == 0) throw new ComposeValidationException($"The {field} is empty.");
        if (path.Contains('$', StringComparison.Ordinal))
            throw new ComposeValidationException($"The {field} must not use variable interpolation.");
        if (path.StartsWith('~'))
            throw new ComposeValidationException($"The {field} must not reference a home directory.");
        if (Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\') ||
            (path.Length > 1 && path[1] == ':'))
            throw new ComposeValidationException(
                $"The {field} must be relative to the repository; absolute host paths are not allowed.");
        if (path.Any(char.IsControl))
            throw new ComposeValidationException($"The {field} contains control characters.");

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(baseDirectory, path)));
        if (!string.Equals(full, repository, StringComparison.Ordinal) &&
            !full.StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ComposeValidationException($"The {field} resolves outside the repository.");
        return full;
    }

    private static bool IsHostPathSource(string source)
    {
        return source.StartsWith('.') || source.StartsWith('/') || source.StartsWith('~') ||
               source.StartsWith('\\') || source.Contains('/', StringComparison.Ordinal) ||
               source.Contains('$', StringComparison.Ordinal);
    }

    private static bool IsRemoteContext(string value)
    {
        return value.Contains("://", StringComparison.Ordinal) ||
               value.StartsWith("git@", StringComparison.OrdinalIgnoreCase);
    }

    private static YamlMappingNode WithValue(YamlMappingNode mapping, string key, YamlNode value)
    {
        mapping.Children[Key(key)] = value;
        return mapping;
    }

    private static bool TryGet(YamlMappingNode mapping, string key, out YamlNode? value)
    {
        return mapping.Children.TryGetValue(Key(key), out value);
    }

    private static bool TryGetScalar(YamlMappingNode mapping, string key, out string value)
    {
        if (mapping.Children.TryGetValue(Key(key), out YamlNode? node) && node is YamlScalarNode { Value: not null } scalar)
        {
            value = scalar.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsNull(YamlNode? node)
    {
        return node is null or YamlScalarNode { Value: null or "" or "~" or "null" };
    }

    private static YamlScalarNode Key(string value)
    {
        return new YamlScalarNode(value);
    }

    private static YamlScalarNode Str(string value)
    {
        return new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted };
    }

    private static YamlScalarNode Bool(bool value)
    {
        return new YamlScalarNode(value ? "true" : "false") { Style = ScalarStyle.Plain };
    }

    private static YamlMappingNode Labels(IReadOnlyDictionary<string, string> labels)
    {
        var node = new YamlMappingNode();
        foreach (var (key, value) in labels.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            node.Children[Key(key)] = Str(value);
        return node;
    }

    private static YamlMappingNode ExternalNetworks(string networkName)
    {
        return new YamlMappingNode { { networkName, new YamlMappingNode { { "external", Bool(true) } } } };
    }

    private static string Serialize(YamlMappingNode root)
    {
        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, false);
        var text = writer.ToString();
        // YamlStream terminates documents with an explicit end marker; compose files do not need it.
        return text.EndsWith("...\n", StringComparison.Ordinal) || text.EndsWith("...\r\n", StringComparison.Ordinal)
            ? text[..text.LastIndexOf("...", StringComparison.Ordinal)]
            : text;
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNamePattern();

    private sealed class PathContext(string repository, string composeDirectory)
    {
        public string Repository { get; } = repository;

        public string ComposeDirectory { get; } = composeDirectory;

        public List<string> Resolved { get; } = [];

        public string Resolve(string baseDirectory, string rawPath, string field)
        {
            var resolved = ResolveInside(Repository, baseDirectory, rawPath, field);
            Resolved.Add(resolved);
            return resolved;
        }
    }
}
