using Vessel.Application.ManagedServices;
using Vessel.Application.ManagedServices.Templates;
using Vessel.Domain.Services;
using Vessel.Infrastructure.ManagedServices.Templates;
using YamlDotNet.RepresentationModel;

namespace Vessel.IntegrationTests.ManagedServices;

/// <summary>Golden tests over the curated Coolify templates shipped as embedded resources (TPL-01).</summary>
public sealed class CoolifyServiceTemplateImportTests
{
    private static readonly YamlServiceTemplateComposeProcessor Processor = new();
    private static readonly CoolifyServiceTemplateImporter Importer = new(Processor);

    public static TheoryData<string> EmbeddedTemplateNames()
    {
        var data = new TheoryData<string>();
        foreach (ServiceTemplateDocument document in new EmbeddedServiceTemplateSource().Load())
            data.Add(document.Name);
        return data;
    }

    [Fact]
    public void Catalog_ImportsAtLeast25UpstreamTemplatesWithoutErrors()
    {
        ServiceTemplateCatalog catalog = ServiceTemplateCatalog.Create(Processor, new EmbeddedServiceTemplateSource());

        Assert.Empty(catalog.ImportErrors);
        ServiceTemplateSummary[] imported = catalog.List().Where(template => template.Source == "coolify").ToArray();
        Assert.True(imported.Length >= 25, $"Only {imported.Length} templates were imported.");
        Assert.All(imported, template => Assert.StartsWith("coolify-", template.Key, StringComparison.Ordinal));
        // Built-in Vessel templates keep working next to the imported ones.
        Assert.Contains(catalog.List(), template => template.Key == "pgadmin" && template.Source == "vessel");
        Assert.Contains(catalog.List(), template => template.Key == "n8n" && template.Source == "vessel");
        Assert.Contains(imported, template => template.Key == "coolify-n8n");
    }

    [Theory]
    [MemberData(nameof(EmbeddedTemplateNames))]
    public void EmbeddedTemplate_ImportsToSafeRoutableCompose(string name)
    {
        ServiceTemplateDocument document = new EmbeddedServiceTemplateSource().Load().Single(item => item.Name == name);

        ServiceTemplateDefinition definition = Importer.Import(document);

        CoolifyComposeTemplate template = definition.Coolify!;
        YamlMappingNode services = Services(template.ComposeYaml);
        foreach ((YamlNode serviceName, YamlNode node) in services.Children)
        {
            var service = (YamlMappingNode)node;
            Assert.False(service.Children.ContainsKey(new YamlScalarNode("ports")), $"{name}/{serviceName} ports");
            Assert.False(service.Children.ContainsKey(new YamlScalarNode("container_name")),
                $"{name}/{serviceName} container_name");
        }

        Assert.DoesNotContain("coolify.", template.ComposeYaml, StringComparison.Ordinal);
        // Every image is pinned to a version, or the template carries a warning for it.
        foreach (var image in template.Images)
        {
            Assert.DoesNotContain(":latest", image, StringComparison.Ordinal);
            var warning = ServiceTemplateImagePolicy.Check("service", image);
            if (warning is not null)
                Assert.Contains(definition.Metadata!.Warnings, item => item.Contains(image, StringComparison.Ordinal));
        }

        // Magic declarations are resolved through the environment file.
        foreach (ServiceMagicVariable variable in template.GeneratedVariables)
            Assert.Contains("${" + variable.Name + "}", template.ComposeYaml.Replace("$" + variable.Name,
                "${" + variable.Name + "}", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.All(template.RouteVariables, route => Assert.Contains(route.Endpoint.ComposeService,
            services.Children.Keys.Select(key => ((YamlScalarNode)key).Value)));
        Assert.Equal(definition.Metadata!.Endpoints.Count > 0, template.RouteVariables.Count > 0);
        Assert.StartsWith("coolify-", definition.Version, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EmbeddedTemplateNames))]
    public void EmbeddedTemplate_PlansAnEnvironmentFileWithEveryVariable(string name)
    {
        ServiceTemplateDefinition definition =
            Importer.Import(new EmbeddedServiceTemplateSource().Load().Single(item => item.Name == name));
        var catalog = new ServiceTemplateCatalog([definition]);
        CoolifyComposeTemplate template = definition.Coolify!;
        var generated = template.GeneratedVariables.ToDictionary(variable => variable.Name,
            ServiceMagicValueGenerator.Generate, StringComparer.Ordinal);
        var inputs = definition.Inputs.Where(input => input.Required)
            .ToDictionary(input => input.Key, _ => "value", StringComparer.Ordinal);

        ServiceProvisioningPlan plan = catalog.CreatePlan(definition.Key, Guid.NewGuid(), name, inputs,
            new ServiceTemplateRuntimeValues(generated, ServiceTemplateRuntimeResolver.RouteValues(template, [])));

        var environment = ComposeEnvironmentFile.Format(plan.SecretValues);
        foreach (ServiceTemplateVariableReference reference in ServiceTemplateVariables.References(plan.ComposeYaml)
                     .Where(reference => ServiceMagicVariable.IsReserved(reference.Name)))
            Assert.Contains(reference.Name + "=", environment, StringComparison.Ordinal);
        foreach (var value in generated.Values) Assert.DoesNotContain(value, plan.ComposeYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Importer_StripsPortsContainerNamesAndCoolifyLabelsAndMapsEndpoint()
    {
        const string text = """
                            # slogan: Sample.
                            # port: 8080
                            services:
                              web-app:
                                image: example/web:1.4.2
                                container_name: fixed
                                ports:
                                  - "8080:8080"
                                labels:
                                  - coolify.managed=true
                                  - com.example.keep=yes
                                environment:
                                  - SERVICE_URL_WEBAPP_8080=/ui
                                  - SECRET=${SERVICE_PASSWORD_64_WEB}
                                  - MODE=${MODE:-prod}
                                  - TOKEN=${API_TOKEN:?}
                              db:
                                image: postgres:16.4-alpine
                                environment:
                                  POSTGRES_USER: $SERVICE_USER_DB
                                  POSTGRES_PASSWORD: ${SERVICE_PASSWORD_DB}
                            """;

        ServiceTemplateDefinition definition = Importer.Import(new ServiceTemplateDocument("sample", text));

        CoolifyComposeTemplate template = definition.Coolify!;
        Assert.DoesNotContain("8080:8080", template.ComposeYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("fixed", template.ComposeYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("coolify.managed", template.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("com.example.keep=yes", template.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("SERVICE_URL_WEBAPP_8080=${SERVICE_URL_WEBAPP_8080}", template.ComposeYaml,
            StringComparison.Ordinal);
        ServiceTemplateEndpoint endpoint = Assert.Single(definition.Metadata!.Endpoints);
        Assert.Equal(new ServiceTemplateEndpoint("web-app", 8080, "WEBAPP", "/ui"), endpoint);
        Assert.Equal(["SERVICE_PASSWORD_64_WEB", "SERVICE_PASSWORD_DB", "SERVICE_USER_DB"],
            template.GeneratedVariables.Select(variable => variable.Name));
        Assert.Equal("prod", definition.Inputs.Single(input => input.Key == "MODE").DefaultValue);
        Assert.True(definition.Inputs.Single(input => input.Key == "API_TOKEN").Required);
        Assert.True(definition.Inputs.Single(input => input.Key == "API_TOKEN").Secret);
        Assert.Contains(definition.Metadata.Warnings, warning => warning.Contains("ports", StringComparison.Ordinal));
        Assert.Equal("coolify-sample", definition.Key);
    }

    [Theory]
    [InlineData("services:\n  app:\n    image: example/app:latest\n", "floating image tag")]
    [InlineData("services:\n  app:\n    image: example/app\n", "untagged image")]
    [InlineData("services:\n  app:\n    build: .\n    image: example/app:1.0.0\n", "build")]
    [InlineData("services:\n  app:\n    image: example/app:1.0.0\n    volumes:\n      - /var/run/docker.sock:/var/run/docker.sock\n", "bind-mounts")]
    [InlineData("services:\n  app:\n    image: example/app:1.0.0\n    volumes:\n      - ./data:/data\n", "bind-mounts")]
    [InlineData("services:\n  app:\n    image: example/app:1.0.0\n    volumes:\n      - type: bind\n        source: /etc\n        target: /etc\n", "mount")]
    [InlineData("services:\n  app:\n    image: example/app:1.0.0\n    volumes:\n      - type: bind\n        source: ./x\n        target: /x\n        content: hi\n", "content")]
    [InlineData("services:\n  app:\n    image: example/app:1.0.0\n    privileged: true\n", "privileged")]
    [InlineData("services:\n  app:\n    image: example/app:1.0.0\n    network_mode: host\n", "network_mode")]
    [InlineData("services:\n  app:\n    image: example/app:1.0.0\n    deploy:\n      replicas: 3\n", "replicas")]
    [InlineData("services:\n  app:\n    image: example/app:1.0.0\n    scale: 2\n", "scale")]
    [InlineData("x-base: &base\n  image: example/app:1.0.0\nservices:\n  app: *base\n", "anchors")]
    [InlineData("services:\n  app:\n    image: example/app:1.0.0\n    environment:\n      - KEY=${SERVICE_SUPABASEANON_KONG}\n", "not supported")]
    [InlineData("services:\n  app:\n    image: example/app:1.0.0\n    environment:\n      - KEY=${COOLIFY_RESOURCE_UUID}\n", "COOLIFY_RESOURCE_UUID")]
    [InlineData("services:\n  app:\n    image: example/app:1.0.0\n    environment:\n      - KEY=${lower_case}\n", "upper case")]
    [InlineData("# ignore: true\nservices:\n  app:\n    image: example/app:1.0.0\n", "ignore")]
    [InlineData("services:\n  app:\n    image: example/app:1.0.0\n  worker:\n    image: example/worker:1.0.0\n    environment:\n      - URL=${SERVICE_URL_API}\n", "SERVICE_URL_API")]
    [InlineData("services:\n  app:\n    image: example/app:1.0.0\n    env_file: .env\n", "env_file")]
    public void Importer_RejectsUnsafeOrUnsupportedTemplates(string text, string expected)
    {
        ServiceTemplateImportException exception = Assert.Throws<ServiceTemplateImportException>(() =>
            Importer.Import(new ServiceTemplateDocument("bad", text)));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_RecordsRejectedDocumentsInsteadOfFailing()
    {
        var source = new StaticSource(
            new ServiceTemplateDocument("good", "services:\n  app:\n    image: example/app:1.0.0\n"),
            new ServiceTemplateDocument("bad", "services:\n  app:\n    image: example/app:latest\n"));

        ServiceTemplateCatalog catalog = ServiceTemplateCatalog.Create(Processor, source);

        Assert.Contains(catalog.List(), template => template.Key == "coolify-good");
        ServiceTemplateImportError error = Assert.Single(catalog.ImportErrors);
        Assert.Equal("bad", error.Name);
    }

    private static YamlMappingNode Services(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        return (YamlMappingNode)root.Children[new YamlScalarNode("services")];
    }

    private sealed class StaticSource(params ServiceTemplateDocument[] documents) : IServiceTemplateSource
    {
        public IReadOnlyList<ServiceTemplateDocument> Load()
        {
            return documents;
        }
    }
}
