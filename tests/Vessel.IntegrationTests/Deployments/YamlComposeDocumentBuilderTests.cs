using Vessel.Application.Deployments;
using Vessel.Infrastructure.Deployments;
using YamlDotNet.RepresentationModel;

namespace Vessel.IntegrationTests.Deployments;

/// <summary>NET-01 (compose apps) and DEP-02: parsed compose normalization, network alias and path containment.</summary>
public sealed class YamlComposeDocumentBuilderTests
{
    private const string Network = "vessel-server";
    private const string Alias = "vessel-app-0123";
    private static readonly string Repository = Path.Combine(Path.GetTempPath(), "vessel-compose-repo");
    private readonly YamlComposeDocumentBuilder _builder = new();

    [Fact]
    public void Primary_service_joins_the_server_network_with_the_upstream_alias_and_keeps_default()
    {
        const string yaml = """
                            services:
                              web:
                                build: .
                                ports: ["8080:3000"]
                                volumes:
                                  - ./data:/data
                                  - cache:/cache
                              db:
                                image: postgres:17
                            volumes:
                              cache: {}
                            """;

        ComposeNormalizationResult result = _builder.NormalizeRepositoryCompose(yaml, Spec(3000));
        YamlMappingNode root = Parse(result.Yaml);
        var web = Service(root, "web");

        Assert.Equal("web", result.PrimaryService);
        var networks = (YamlMappingNode)web["networks"];
        Assert.True(networks.Children.ContainsKey(new YamlScalarNode("default")));
        var aliases = (YamlSequenceNode)((YamlMappingNode)networks[Network])["aliases"];
        Assert.Contains(aliases.Children, alias => ((YamlScalarNode)alias).Value == Alias);
        Assert.False(Service(root, "db").Children.ContainsKey(new YamlScalarNode("networks")));
        Assert.Equal("true", ((YamlScalarNode)((YamlMappingNode)((YamlMappingNode)root["networks"])[Network])["external"]).Value);
        Assert.Equal(Repository, ((YamlScalarNode)web["build"]).Value);
        var volumes = (YamlSequenceNode)web["volumes"];
        Assert.Equal(Path.Combine(Repository, "data") + ":/data", ((YamlScalarNode)volumes[0]).Value);
        Assert.Equal("cache:/cache", ((YamlScalarNode)volumes[1]).Value);
        Assert.Equal("8080:3000", ((YamlScalarNode)((YamlSequenceNode)web["ports"])[0]).Value);
    }

    [Fact]
    public void Primary_is_selected_by_exposed_port_and_existing_aliases_are_kept()
    {
        const string yaml = """
                            services:
                              worker:
                                build: ./worker
                              api:
                                image: api
                                expose: ["9000"]
                                networks:
                                  backend:
                                    aliases: [api-internal]
                            networks:
                              backend: {}
                            """;

        ComposeNormalizationResult result = _builder.NormalizeRepositoryCompose(yaml, Spec(9000));
        var networks = (YamlMappingNode)Service(Parse(result.Yaml), "api")["networks"];

        Assert.Equal("api", result.PrimaryService);
        Assert.True(networks.Children.ContainsKey(new YamlScalarNode("backend")));
        Assert.True(networks.Children.ContainsKey(new YamlScalarNode(Network)));
    }

    [Fact]
    public void Dots_in_values_are_not_treated_as_traversal()
    {
        const string yaml = """
                            services:
                              web:
                                image: "nginx:1.27"
                                command: ["echo", "Loading..."]
                            """;

        ComposeNormalizationResult result = _builder.NormalizeRepositoryCompose(yaml, Spec(80));

        Assert.Contains("Loading...", result.Yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("services:\n  web:\n    image: x\n    volumes: [\"../outside:/data\"]\n")]
    [InlineData("services:\n  web:\n    image: x\n    volumes: [\"/var/run/docker.sock:/var/run/docker.sock\"]\n")]
    [InlineData("services:\n  web:\n    image: x\n    volumes: [\"~/.ssh:/root/.ssh\"]\n")]
    [InlineData("services:\n  web:\n    image: x\n    volumes: [\"${HOME}:/host\"]\n")]
    [InlineData("services:\n  web:\n    image: x\n    volumes:\n      - type: bind\n        source: /etc\n        target: /etc\n")]
    [InlineData("services:\n  web:\n    build:\n      context: ../..\n")]
    [InlineData("services:\n  web:\n    build:\n      context: .\n      dockerfile: ../../Dockerfile\n")]
    [InlineData("services:\n  web:\n    image: x\n    env_file: [\"../../secrets.env\"]\n")]
    [InlineData("x-common: &common\n  volumes: [\"/:/host\"]\nservices:\n  web:\n    <<: *common\n    image: x\n")]
    [InlineData("include: [other.yml]\nservices:\n  web:\n    image: x\n")]
    [InlineData("services: {}\n")]
    [InlineData("services:\n  web:\n    image: x\n    network_mode: host\n")]
    [InlineData("services: [\n")]
    public void Unsafe_or_invalid_compose_is_rejected(string yaml)
    {
        Assert.Throws<ComposeValidationException>(() => _builder.NormalizeRepositoryCompose(yaml, Spec(80)));
    }

    [Fact]
    public void Compose_file_in_subdirectory_resolves_paths_relative_to_its_directory()
    {
        const string yaml = "services:\n  web:\n    build: ..\n    volumes: [\"./conf:/conf\"]\n";

        ComposeNormalizationResult result = _builder.NormalizeRepositoryCompose(yaml,
            Spec(80) with { ComposeFilePath = "deploy/docker-compose.yml" });
        var web = Service(Parse(result.Yaml), "web");

        Assert.Equal(Repository, ((YamlScalarNode)web["build"]).Value);
        Assert.Equal(Path.Combine(Repository, "deploy", "conf") + ":/conf",
            ((YamlScalarNode)((YamlSequenceNode)web["volumes"])[0]).Value);
    }

    [Fact]
    public void Preview_is_namespaced_and_publishes_no_fixed_host_ports()
    {
        const string yaml = """
                            services:
                              web:
                                build: .
                                container_name: shop
                                ports: ["80:3000"]
                              redis:
                                image: redis
                                ports: ["6379:6379"]
                            volumes:
                              data:
                                name: shop-data
                            """;

        ComposeNormalizationResult result = _builder.NormalizeRepositoryCompose(yaml,
            Spec(3000) with { PreviewSuffix = "pr-4", UpstreamAlias = "vessel-app-0123-pr-4" });
        YamlMappingNode root = Parse(result.Yaml);
        var web = Service(root, "web");

        Assert.Equal("shop-pr-4", ((YamlScalarNode)web["container_name"]).Value);
        Assert.Equal("127.0.0.1::3000", ((YamlScalarNode)((YamlSequenceNode)web["ports"])[0]).Value);
        Assert.False(Service(root, "redis").Children.ContainsKey(new YamlScalarNode("ports")));
        Assert.Equal("shop-data-pr-4",
            ((YamlScalarNode)((YamlMappingNode)((YamlMappingNode)root["volumes"])["data"])["name"]).Value);
        Assert.Contains("vessel-app-0123-pr-4", result.Yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("80:3000", result.Yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerfile_stack_quotes_user_values_so_they_cannot_inject_keys()
    {
        var yaml = _builder.CreateDockerfileStack(new DockerfileStackSpec("vessel-app-0123", "vessel/app:1",
            "./repository", "Dockerfile\nprivileged: true", Network, Alias, "127.0.0.1::8080",
            new Dictionary<string, string> { ["vessel.managed"] = "true" }));
        var service = Service(Parse(yaml), "vessel-app-0123");

        Assert.False(service.Children.ContainsKey(new YamlScalarNode("privileged")));
        Assert.Equal("Dockerfile\nprivileged: true",
            ((YamlScalarNode)((YamlMappingNode)service["build"])["dockerfile"]).Value);
        Assert.Equal("true", ((YamlScalarNode)((YamlMappingNode)service["labels"])["vessel.managed"]).Value);
        Assert.Equal("127.0.0.1::8080", ((YamlScalarNode)((YamlSequenceNode)service["ports"])[0]).Value);
    }

    private static RepositoryComposeSpec Spec(int port)
    {
        return new RepositoryComposeSpec(Repository, "docker-compose.yml", Network, Alias, port,
            new Dictionary<string, string> { ["vessel.managed"] = "true" }, null);
    }

    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Service(YamlMappingNode root, string name)
    {
        return (YamlMappingNode)((YamlMappingNode)root["services"])[name];
    }
}
