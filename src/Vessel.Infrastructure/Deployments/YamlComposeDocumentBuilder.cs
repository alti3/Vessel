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
    private const int MaxIncludeDepth = 5;

    /// <summary>Capabilities that give a container host-level power (SEC-11); <c>ALL</c> includes them.</summary>
    private static readonly HashSet<string> DangerousCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALL", "SYS_ADMIN", "SYS_MODULE", "SYS_RAWIO", "SYS_PTRACE", "SYS_BOOT", "SYS_TIME", "SYS_CHROOT",
        "NET_ADMIN", "NET_RAW", "DAC_READ_SEARCH", "DAC_OVERRIDE", "MAC_ADMIN", "MAC_OVERRIDE", "BPF", "PERFMON",
        "SYSLOG", "MKNOD", "SETFCAP", "LINUX_IMMUTABLE", "AUDIT_CONTROL", "WAKE_ALARM", "BLOCK_SUSPEND",
        "CHECKPOINT_RESTORE"
    };

    private static readonly string[] HostNamespaceKeys = ["network_mode", "pid", "ipc", "uts", "userns_mode", "cgroup"];

    public string CreateDockerfileStack(DockerfileStackSpec spec)
    {
        var service = new YamlMappingNode { { "image", Str(spec.ImageName) } };
        // Images Vessel built itself have no build section, so `compose up` never rebuilds them without build values.
        if (spec.BuildContext is not null)
            service.Add("build", new YamlMappingNode
            {
                { "context", Str(spec.BuildContext) },
                { "dockerfile", Str(spec.DockerfilePath ?? "Dockerfile") }
            });
        service.Add("env_file", new YamlSequenceNode(Str(".env")));
        service.Add("labels", Labels(spec.Labels));
        service.Add("networks", new YamlMappingNode
        {
            { spec.NetworkName, new YamlMappingNode { { "aliases", new YamlSequenceNode(Str(spec.UpstreamAlias)) } } }
        });
        service.Add("ports", new YamlSequenceNode(Str(spec.HostPortBinding)));
        service.Add("restart", Str("unless-stopped"));

        var root = new YamlMappingNode
        {
            { "services", new YamlMappingNode { { spec.ServiceName, service } } },
            { "networks", ExternalNetworks(spec.NetworkName) }
        };
        return Serialize(root);
    }

    public IReadOnlyList<string> ListIncludes(string yaml, string repositoryDirectory, string composeFilePath)
    {
        var repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryDirectory));
        var composeFile = ResolveInside(repository, repository, composeFilePath, "compose file path");
        YamlMappingNode root = Load(yaml);
        return IncludePaths(root, repository, Path.GetDirectoryName(composeFile) ?? repository)
            .Select(path => Relative(repository, path))
            .ToArray();
    }

    public ComposeNormalizationResult NormalizeRepositoryCompose(string yaml, RepositoryComposeSpec spec)
    {
        var warnings = new List<string>();
        var elevated = new List<string>();
        var repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spec.RepositoryDirectory));
        var composeFile = ResolveInside(repository, repository, spec.ComposeFilePath, "compose file path");
        var composeDirectory = Path.GetDirectoryName(composeFile) ?? repository;
        var resolved = new List<string>();
        var policy = new PolicyContext(spec.AllowElevated, warnings, elevated);

        YamlMappingNode root = Load(yaml);
        var context = new PathContext(repository, composeDirectory, spec.HostRepositoryDirectory, resolved, policy);
        // Included files are flattened (upstream feat-compose-include-support): each is normalized against its own
        // directory, then merged; a service or top-level name defined twice is an error, as in Compose.
        var preNormalized = new HashSet<string>(StringComparer.Ordinal);
        MergeIncludes(root, spec, context, preNormalized, [Relative(repository, composeFile)], 0);
        if (!TryGet(root, "services", out YamlNode? servicesNode) || servicesNode is not YamlMappingNode services ||
            services.Children.Count == 0)
            throw new ComposeValidationException("Compose file must define at least one service under 'services'.");

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

        string primary;
        if (spec.PrimaryService is not null)
        {
            if (!serviceNames.Contains(spec.PrimaryService, StringComparer.Ordinal))
                throw new ComposeValidationException(
                    $"The configured public service '{spec.PrimaryService}' does not exist in the compose file.");
            primary = spec.PrimaryService;
        }
        else
        {
            primary = SelectPrimaryService(services, serviceNames, spec.ExposedPort);
        }

        foreach (var name in serviceNames)
        {
            var service = (YamlMappingNode)services.Children[Key(name)];
            ValidateScale(name, service);
            CheckServicePolicy(name, service, policy);
            if (!preNormalized.Contains(name)) NormalizeServicePaths(name, service, context);
            InjectBuildArguments(service, spec.BuildArgumentKeys);
            AddLabels(service, spec.Labels);
            if (TryGetScalar(service, "container_name", out var containerName) &&
                !Vessel.Domain.ValueObjects.ContainerName.IsValid(containerName))
                throw new ComposeValidationException($"Service '{name}' container_name is invalid.");
            if (spec.PreviewSuffix is not null)
                IsolatePreviewService(name, service, spec.PreviewSuffix, name == primary, spec.ExposedPort, warnings);
        }

        CheckTopLevelVolumes(root, policy);
        CheckTopLevelNetworks(root);
        NormalizeTopLevelFiles(root, "secrets", context);
        NormalizeTopLevelFiles(root, "configs", context);
        // Lexical containment is not enough for a cloned (untrusted) tree: a symlink such as ./data -> / would
        // escape. Every resolved host path must also stay inside the repository after resolving existing links.
        foreach (var path in resolved)
        {
            try
            {
                paths.EnsureOwnedPath(repository, path);
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
        return new ComposeNormalizationResult(Serialize(root), primary, warnings.Distinct(StringComparer.Ordinal).ToArray())
        {
            ElevatedSettings = elevated.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private void MergeIncludes(YamlMappingNode root, RepositoryComposeSpec spec, PathContext context,
        HashSet<string> preNormalized, List<string> chain, int depth)
    {
        if (!TryGet(root, "include", out YamlNode? _)) return;
        if (depth >= MaxIncludeDepth)
            throw new ComposeValidationException("Compose 'include' is nested too deeply.");
        IReadOnlyList<string> includes = IncludePaths(root, context.Repository, context.ComposeDirectory);
        root.Children.Remove(Key("include"));
        foreach (var include in includes)
        {
            var relative = Relative(context.Repository, include);
            if (chain.Contains(relative, StringComparer.Ordinal))
                throw new ComposeValidationException($"Compose 'include' of '{relative}' is circular.");
            if (!spec.IncludedFiles.TryGetValue(relative, out var content))
                throw new ComposeValidationException($"Included compose file '{relative}' was not found in the repository.");
            context.Resolved.Add(include);
            YamlMappingNode included = Load(content);
            var includedContext = context.ForDirectory(Path.GetDirectoryName(include) ?? context.Repository);
            var nested = new HashSet<string>(StringComparer.Ordinal);
            MergeIncludes(included, spec, includedContext, nested, [.. chain, relative], depth + 1);
            if (TryGet(included, "services", out YamlNode? includedServices) &&
                includedServices is YamlMappingNode includedMap)
                foreach (var (keyNode, value) in includedMap.Children)
                {
                    var name = ((YamlScalarNode)keyNode).Value ?? string.Empty;
                    if (value is YamlMappingNode service && !nested.Contains(name))
                        NormalizeServicePaths(name, service, includedContext);
                }

            NormalizeTopLevelFiles(included, "secrets", includedContext);
            NormalizeTopLevelFiles(included, "configs", includedContext);
            foreach (var section in new[] { "services", "volumes", "networks", "secrets", "configs" })
            {
                if (!TryGet(included, section, out YamlNode? node) || node is not YamlMappingNode entries) continue;
                if (!TryGet(root, section, out YamlNode? target) || target is not YamlMappingNode targetMap)
                {
                    targetMap = new YamlMappingNode();
                    root.Children[Key(section)] = targetMap;
                }

                foreach (var (key, value) in entries.Children)
                {
                    if (targetMap.Children.ContainsKey(key))
                        throw new ComposeValidationException(
                            $"Compose {section[..^1]} '{key}' is defined both in the compose file and in '{relative}'.");
                    targetMap.Children[key] = value;
                    if (section == "services") preNormalized.Add(((YamlScalarNode)key).Value ?? string.Empty);
                }
            }
        }
    }

    private static List<string> IncludePaths(YamlMappingNode root, string repository, string composeDirectory)
    {
        var result = new List<string>();
        if (!TryGet(root, "include", out YamlNode? node) || IsNull(node)) return result;
        if (node is not YamlSequenceNode list)
            throw new ComposeValidationException("Compose 'include' must be a list.");
        foreach (YamlNode entry in list.Children)
            switch (entry)
            {
                case YamlScalarNode { Value: not null } scalar:
                    result.Add(ResolveInside(repository, composeDirectory, scalar.Value, "include path"));
                    break;
                case YamlMappingNode mapping when TryGet(mapping, "path", out YamlNode? pathNode):
                    if (TryGet(mapping, "project_directory", out _) || TryGet(mapping, "env_file", out _))
                        throw new ComposeValidationException(
                            "Compose 'include' with project_directory or env_file is not supported; include the file by path.");
                    IEnumerable<YamlNode> pathNodes = pathNode is YamlSequenceNode many ? many.Children : [pathNode!];
                    foreach (YamlNode item in pathNodes)
                        result.Add(item is YamlScalarNode { Value: not null } path
                            ? ResolveInside(repository, composeDirectory, path.Value, "include path")
                            : throw new ComposeValidationException("Compose 'include' path is invalid."));
                    break;
                default:
                    throw new ComposeValidationException("Compose 'include' entry is invalid.");
            }

        return result;
    }

    private static string Relative(string repository, string fullPath)
    {
        return Path.GetRelativePath(repository, fullPath).Replace('\\', '/');
    }

    /// <summary>
    ///     Upstream v4.5.0 (#12213, breaking): more than one replica is rejected. Vessel routes the public service through
    ///     one network alias and namespaces container names for previews, so replicas would collide.
    /// </summary>
    private static void ValidateScale(string name, YamlMappingNode service)
    {
        if (TryGetScalar(service, "scale", out var scale) && IsGreaterThanOne(scale))
            throw new ComposeValidationException(
                $"Service \"{name}\" sets scale to {scale}. Vessel runs one container per service; remove scale, or define each instance as a separate service.");
        if (TryGet(service, "deploy", out YamlNode? deploy) && deploy is YamlMappingNode deployMap &&
            TryGetScalar(deployMap, "replicas", out var replicas) && IsGreaterThanOne(replicas))
            throw new ComposeValidationException(
                $"Service \"{name}\" sets deploy.replicas to {replicas}. Vessel runs one container per service; remove deploy.replicas, or define each instance as a separate service.");
    }

    private static bool IsGreaterThanOne(string value)
    {
        return long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number > 1
            : value.Contains('$', StringComparison.Ordinal)
                ? throw new ComposeValidationException("Replica counts must not use variable interpolation.")
                : false;
    }

    /// <summary>
    ///     Host-level service settings (SEC-11). They are refused unless the deployment is authorized by an instance
    ///     administrator, in which case each one is reported for the deployment log and audit.
    /// </summary>
    private static void CheckServicePolicy(string name, YamlMappingNode service, PolicyContext policy)
    {
        if (TryGetScalar(service, "privileged", out var privileged) && IsTrue(privileged))
            policy.Elevate($"service '{name}' runs privileged");
        foreach (var key in HostNamespaceKeys)
            if (TryGetScalar(service, key, out var mode) && string.Equals(mode.Trim(), "host", StringComparison.OrdinalIgnoreCase))
                policy.Elevate($"service '{name}' uses the host {key.Replace("_mode", string.Empty, StringComparison.Ordinal)} namespace");
        if (TryGet(service, "cap_add", out YamlNode? caps) && caps is YamlSequenceNode capList)
            foreach (YamlScalarNode cap in capList.Children.OfType<YamlScalarNode>())
            {
                var capability = (cap.Value ?? string.Empty).Trim();
                if (capability.StartsWith("CAP_", StringComparison.OrdinalIgnoreCase)) capability = capability[4..];
                if (DangerousCapabilities.Contains(capability))
                    policy.Elevate($"service '{name}' adds capability {capability.ToUpperInvariant()}");
            }

        if (TryGet(service, "devices", out YamlNode? devices) && devices is YamlSequenceNode { Children.Count: > 0 })
            policy.Elevate($"service '{name}' maps host devices");
        if (TryGet(service, "device_cgroup_rules", out YamlNode? rules) && rules is YamlSequenceNode { Children.Count: > 0 })
            policy.Elevate($"service '{name}' sets device cgroup rules");
        if (TryGet(service, "gpus", out _))
            policy.Elevate($"service '{name}' requests host GPUs");
        if (TryGet(service, "security_opt", out YamlNode? security) && security is YamlSequenceNode securityList)
            foreach (YamlScalarNode option in securityList.Children.OfType<YamlScalarNode>())
            {
                var value = (option.Value ?? string.Empty).Replace(':', '=').Trim();
                if (value.EndsWith("=unconfined", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("label=disable", StringComparison.OrdinalIgnoreCase))
                    policy.Elevate($"service '{name}' disables a security profile ({option.Value})");
            }

        if (TryGet(service, "build", out YamlNode? build) && build is YamlMappingNode buildMap)
        {
            if (TryGetScalar(buildMap, "privileged", out var buildPrivileged) && IsTrue(buildPrivileged))
                policy.Elevate($"service '{name}' builds privileged");
            if (TryGetScalar(buildMap, "network", out var network) &&
                string.Equals(network.Trim(), "host", StringComparison.OrdinalIgnoreCase))
                policy.Elevate($"service '{name}' builds on the host network");
            if (TryGet(buildMap, "entitlements", out YamlNode? entitlements) &&
                entitlements is YamlSequenceNode { Children.Count: > 0 })
                policy.Elevate($"service '{name}' requests build entitlements");
        }

        if (TryGet(service, "volumes", out YamlNode? volumes) && volumes is YamlSequenceNode volumeList)
            foreach (YamlNode volume in volumeList.Children)
                if (volume is YamlScalarNode { Value: not null } scalar && !scalar.Value.Contains(':', StringComparison.Ordinal))
                    policy.Warn(
                        $"Service '{name}' uses an anonymous volume ({scalar.Value}); its data is not kept when the container is recreated.");
    }

    /// <summary>
    ///     Top-level volumes: external volumes are used as declared (never created or removed by Vessel); a local volume
    ///     whose driver options bind a host path (<c>o: bind</c>, <c>device: /path</c>) is a host mount (SEC-11).
    /// </summary>
    private static void CheckTopLevelVolumes(YamlMappingNode root, PolicyContext policy)
    {
        if (!TryGet(root, "volumes", out YamlNode? node) || node is not YamlMappingNode volumes) return;
        foreach (var (key, value) in volumes.Children)
        {
            if (value is not YamlMappingNode volume) continue;
            if (TryGetScalar(volume, "external", out var external) && IsTrue(external))
            {
                policy.Warn($"Volume '{key}' is external: Vessel uses it as declared and never creates or removes it.");
                continue;
            }

            if (!TryGet(volume, "driver_opts", out YamlNode? optionsNode) || optionsNode is not YamlMappingNode options)
                continue;
            var bind = (TryGetScalar(options, "o", out var o) && o.Split(',').Any(part => part.Trim() == "bind")) ||
                       (TryGetScalar(options, "type", out var type) && type.Trim() is "none" or "bind");
            if (bind || TryGetScalar(options, "device", out _))
                policy.Elevate($"volume '{key}' mounts a host path through driver options");
            else
                policy.Warn($"Volume '{key}' keeps its declared driver options.");
        }
    }

    private static void CheckTopLevelNetworks(YamlMappingNode root)
    {
        if (!TryGet(root, "networks", out YamlNode? node) || node is not YamlMappingNode networks) return;
        foreach (var (key, value) in networks.Children)
        {
            var name = ((YamlScalarNode)key).Value ?? string.Empty;
            if (!Vessel.Domain.ValueObjects.ContainerNetworkName.IsValid(name))
                throw new ComposeValidationException($"Compose network name '{name}' is invalid.");
            if (value is YamlMappingNode network && TryGetScalar(network, "name", out var explicitName) &&
                !Vessel.Domain.ValueObjects.ContainerNetworkName.IsValid(explicitName))
                throw new ComposeValidationException($"Compose network '{name}' has an invalid name.");
        }
    }

    /// <summary>Adds <c>KEY: ${KEY}</c> build arguments (values come from the exact env file) unless declared.</summary>
    private static void InjectBuildArguments(YamlMappingNode service, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0 || !TryGet(service, "build", out YamlNode? build)) return;
        if (build is YamlScalarNode { Value: not null } scalar)
        {
            if (IsRemoteContext(scalar.Value)) return;
            build = new YamlMappingNode { { "context", scalar } };
            service.Children[Key("build")] = build;
        }

        if (build is not YamlMappingNode buildMap) return;
        if (!TryGet(buildMap, "args", out YamlNode? argsNode) || IsNull(argsNode))
        {
            argsNode = new YamlMappingNode();
            buildMap.Children[Key("args")] = argsNode;
        }

        switch (argsNode)
        {
            case YamlMappingNode map:
                foreach (var key in keys)
                    if (!map.Children.ContainsKey(Key(key)))
                        map.Children[Key(key)] = Str("${" + key + "}");
                break;
            case YamlSequenceNode list:
                var declared = list.Children.OfType<YamlScalarNode>()
                    .Select(item => (item.Value ?? string.Empty).Split('=')[0])
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var key in keys.Where(key => !declared.Contains(key)))
                    list.Add(Str(key + "=${" + key + "}"));
                break;
        }
    }

    private static bool IsTrue(string value)
    {
        return value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Trim() == "1";
    }

    public string AttachServicesToNetwork(string yaml, string networkName,
        IReadOnlyDictionary<string, string> aliasesByService)
    {
        ArgumentNullException.ThrowIfNull(aliasesByService);
        YamlMappingNode root = Load(yaml);
        if (!TryGet(root, "services", out YamlNode? servicesNode) || servicesNode is not YamlMappingNode services)
            throw new ComposeValidationException("Compose file must define services.");
        foreach ((var serviceName, var alias) in aliasesByService.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!services.Children.TryGetValue(Key(serviceName), out YamlNode? serviceNode) ||
                serviceNode is not YamlMappingNode service)
                throw new ComposeValidationException($"Compose service '{serviceName}' does not exist.");
            AttachPrimaryToServerNetwork(service, serviceName,
                new RepositoryComposeSpec(string.Empty, string.Empty, networkName, alias, 0,
                    new Dictionary<string, string>(), null));
        }

        if (!TryGet(root, "networks", out YamlNode? networksNode) || networksNode is not YamlMappingNode networks)
        {
            if (networksNode is not null && networksNode is not YamlMappingNode && !IsNull(networksNode))
                throw new ComposeValidationException("Compose top-level 'networks' must be a mapping.");
            networks = new YamlMappingNode();
            root.Children[Key("networks")] = networks;
        }

        networks.Children[Key(networkName)] = new YamlMappingNode { { "external", Bool(true) } };
        return Serialize(root);
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
                Str(context.ResolveForHost(context.ComposeDirectory, extendsFile,
                    $"service '{name}' extends file"));
    }

    private static void NormalizeBuild(string name, YamlMappingNode service, YamlNode build, PathContext context)
    {
        if (build is YamlScalarNode { Value: not null } scalar)
        {
            service.Children[Key("build")] = IsRemoteContext(scalar.Value)
                ? scalar
                : Str(context.ResolveForHost(context.ComposeDirectory, scalar.Value,
                    $"service '{name}' build context"));
            return;
        }

        if (build is not YamlMappingNode buildMap)
            throw new ComposeValidationException($"Service '{name}' build must be a path or a mapping.");

        var contextValue = TryGetScalar(buildMap, "context", out var configured) ? configured : ".";
        if (IsRemoteContext(contextValue)) return;

        var resolvedContext = context.Resolve(context.ComposeDirectory, contextValue,
            $"service '{name}' build context");
        buildMap.Children[Key("context")] = Str(context.ToHost(resolvedContext));
        if (TryGetScalar(buildMap, "dockerfile", out var dockerfile))
            context.Resolve(resolvedContext, dockerfile, $"service '{name}' dockerfile");

        if (TryGet(buildMap, "additional_contexts", out YamlNode? additional) && additional is YamlMappingNode extra)
            foreach (YamlNode key in extra.Children.Keys.ToArray())
                if (extra.Children[key] is YamlScalarNode { Value: not null } value && !IsRemoteContext(value.Value) &&
                    !value.Value.Contains(':', StringComparison.Ordinal))
                    extra.Children[key] = Str(context.ResolveForHost(context.ComposeDirectory, value.Value,
                        $"service '{name}' additional build context"));
    }

    private static YamlNode NormalizeEnvFiles(string name, YamlNode envFile, PathContext context)
    {
        string Resolve(string path)
        {
            return context.ResolveForHost(context.ComposeDirectory, path, $"service '{name}' env_file");
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
            if (IsAbsoluteHostPath(source))
            {
                context.Policy.ElevateHostBind(name, source);
                return scalar;
            }

            var resolved = context.Resolve(context.ComposeDirectory, source,
                $"service '{name}' bind mount source");
            context.Policy.WarnRepositoryBind(name, source);
            return Str(context.ToHost(resolved) + value[separator..]);
        }

        if (volume is YamlMappingNode mapping)
        {
            var type = TryGetScalar(mapping, "type", out var configuredType) ? configuredType : null;
            if (string.Equals(type, "npipe", StringComparison.Ordinal))
                context.Policy.Elevate($"service '{name}' mounts a host named pipe");
            if (!TryGetScalar(mapping, "source", out var source)) return mapping;
            var isBind = string.Equals(type, "bind", StringComparison.Ordinal) ||
                         (type is null && IsHostPathSource(source));
            if (!isBind) return mapping;
            if (IsAbsoluteHostPath(source))
            {
                context.Policy.ElevateHostBind(name, source);
                return mapping;
            }

            context.Policy.WarnRepositoryBind(name, source);
            return WithValue(mapping, "source",
                Str(context.ResolveForHost(context.ComposeDirectory, source,
                    $"service '{name}' bind mount source")));
        }

        throw new ComposeValidationException($"Service '{name}' has an invalid volume entry.");
    }

    /// <summary>An absolute host path without interpolation or home-directory expansion (elevated bind source).</summary>
    private static bool IsAbsoluteHostPath(string source)
    {
        var trimmed = source.Trim();
        return trimmed.StartsWith('/') && !trimmed.Contains('$', StringComparison.Ordinal) &&
               !trimmed.Any(char.IsControl);
    }

    private static void NormalizeTopLevelFiles(YamlMappingNode root, string section, PathContext context)
    {
        if (!TryGet(root, section, out YamlNode? node) || node is not YamlMappingNode entries) return;
        foreach (var (key, value) in entries.Children)
            if (value is YamlMappingNode entry && TryGetScalar(entry, "file", out var file))
                entry.Children[Key("file")] = Str(context.ResolveForHost(context.ComposeDirectory, file,
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

    /// <summary>
    ///     Policy decisions of one normalization: host-level settings throw unless elevation is allowed, in which case
    ///     they are recorded; warnings are collected for the deployment log.
    /// </summary>
    private sealed class PolicyContext(bool allowElevated, List<string> warnings, List<string> elevated)
    {
        public void Elevate(string setting)
        {
            if (!allowElevated)
                throw new ComposeValidationException(
                    $"Compose {setting}, which gives containers host-level access. Only an instance administrator can deploy it.");
            elevated.Add(setting);
        }

        public void ElevateHostBind(string service, string source)
        {
            var trimmed = source.Trim().TrimEnd('/');
            Elevate(trimmed.EndsWith("docker.sock", StringComparison.Ordinal) ||
                    trimmed.EndsWith("podman.sock", StringComparison.Ordinal)
                ? $"service '{service}' mounts the container runtime socket {trimmed} (host-root equivalent)"
                : $"service '{service}' bind-mounts the host path {trimmed}");
        }

        public void WarnRepositoryBind(string service, string source)
        {
            Warn(
                $"Service '{service}' bind-mounts {source} from the repository checkout, which is removed after the deployment; use a named volume for data that must persist.");
        }

        public void Warn(string warning)
        {
            warnings.Add(warning);
        }
    }

    private sealed class PathContext(
        string repository,
        string composeDirectory,
        string? hostRepository,
        List<string> resolved,
        PolicyContext policy)
    {
        /// <summary>
        ///     Maps a validated local path to the path the runtime host sees: unchanged locally, or under the uploaded
        ///     repository directory of an SSH server (POSIX separators).
        /// </summary>
        public string ToHost(string localPath)
        {
            if (string.IsNullOrEmpty(hostRepository)) return localPath;
            var relative = localPath.Length == Repository.Length ? string.Empty : localPath[(Repository.Length + 1)..];
            relative = relative.Replace('\\', '/');
            return relative.Length == 0 ? hostRepository : $"{hostRepository.TrimEnd('/')}/{relative}";
        }

        public string ResolveForHost(string baseDirectory, string rawPath, string field)
        {
            return ToHost(Resolve(baseDirectory, rawPath, field));
        }

        public string Repository { get; } = repository;

        public string ComposeDirectory { get; } = composeDirectory;

        public List<string> Resolved { get; } = resolved;

        public PolicyContext Policy { get; } = policy;

        /// <summary>The same repository and policy, with paths relative to another (included) compose file.</summary>
        public PathContext ForDirectory(string directory)
        {
            return new PathContext(Repository, directory, hostRepository, Resolved, Policy);
        }

        public string Resolve(string baseDirectory, string rawPath, string field)
        {
            var path = ResolveInside(Repository, baseDirectory, rawPath, field);
            Resolved.Add(path);
            return path;
        }
    }
}
