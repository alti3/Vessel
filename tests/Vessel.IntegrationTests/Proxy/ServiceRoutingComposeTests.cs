using Vessel.Application.Deployments;
using Vessel.Application.Proxy;
using Vessel.Domain;
using Vessel.Infrastructure.Deployments;
using Vessel.Infrastructure.Files;
using YamlDotNet.RepresentationModel;

namespace Vessel.IntegrationTests.Proxy;

/// <summary>NET-06: service-template stacks join the server network under per-service aliases.</summary>
public sealed class ServiceRoutingComposeTests
{
    private const string Template = """
                                    services:
                                      pgadmin:
                                        image: dpage/pgadmin4:latest
                                        ports:
                                          - "5050:80"
                                      worker:
                                        image: busybox
                                    """;

    private readonly YamlComposeDocumentBuilder _builder = new(new PathSafetyService());

    [Fact]
    public void AttachServicesToNetwork_AddsAliasOnExternalServerNetworkAndKeepsDefaultNetwork()
    {
        var serviceId = ServiceResourceId.New();
        var serverId = ServerId.New();
        var network = ProxyNaming.ServerNetwork(serverId);
        var alias = ProxyNaming.ServiceUpstreamAlias(serviceId, "pgadmin");

        var yaml = _builder.AttachServicesToNetwork(Template, network,
            new Dictionary<string, string> { ["pgadmin"] = alias });

        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var pgadmin = (YamlMappingNode)((YamlMappingNode)root["services"])["pgadmin"];
        var networks = (YamlMappingNode)pgadmin["networks"];
        Assert.True(networks.Children.ContainsKey(new YamlScalarNode("default")));
        var aliases = (YamlSequenceNode)((YamlMappingNode)networks[network])["aliases"];
        Assert.Equal(alias, ((YamlScalarNode)aliases.Children.Single()).Value);
        Assert.Equal("true", ((YamlScalarNode)((YamlMappingNode)((YamlMappingNode)root["networks"])[network])["external"]).Value);
        var worker = (YamlMappingNode)((YamlMappingNode)root["services"])["worker"];
        Assert.False(worker.Children.ContainsKey(new YamlScalarNode("networks")));
    }

    [Fact]
    public void AttachServicesToNetwork_RejectsUnknownComposeServices()
    {
        Assert.Throws<ComposeValidationException>(() => _builder.AttachServicesToNetwork(Template, "vessel-x",
            new Dictionary<string, string> { ["missing"] = "vessel-svc-x" }));
    }

    [Fact]
    public void ServiceUpstreamAlias_IsAValidBoundedLabelPerService()
    {
        var serviceId = ServiceResourceId.New();
        var alias = ProxyNaming.ServiceUpstreamAlias(serviceId, "Some_Weird.Service");

        Assert.Matches("^[a-z0-9][a-z0-9-]{0,62}$", alias);
        Assert.NotEqual(alias, ProxyNaming.ServiceUpstreamAlias(serviceId, "other"));
        Assert.NotEqual(alias, ProxyNaming.ServiceUpstreamAlias(ServiceResourceId.New(), "Some_Weird.Service"));
    }
}
