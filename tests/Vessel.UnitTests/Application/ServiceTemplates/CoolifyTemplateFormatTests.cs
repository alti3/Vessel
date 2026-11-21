using System.Text.Json;
using Vessel.Application.ManagedServices;
using Vessel.Application.ManagedServices.Templates;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Services;
using Vessel.Domain.ValueObjects;
using Vessel.UnitTests.Application.Backups;

namespace Vessel.UnitTests.Application.ServiceTemplates;

public sealed class CoolifyTemplateFormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("SERVICE_URL_APP_3000", ServiceMagicVariableKind.Url, "APP", 3000)]
    [InlineData("SERVICE_URL_APP", ServiceMagicVariableKind.Url, "APP", null)]
    [InlineData("SERVICE_FQDN_UPTIME_KUMA_3001", ServiceMagicVariableKind.Fqdn, "UPTIME_KUMA", 3001)]
    [InlineData("SERVICE_FQDN_N8N", ServiceMagicVariableKind.Fqdn, "N8N", null)]
    public void MagicVariable_ParsesRouteVariables(string name, ServiceMagicVariableKind kind, string identifier,
        int? port)
    {
        ServiceMagicVariable variable = ServiceMagicVariable.Parse(name);

        Assert.Equal(kind, variable.Kind);
        Assert.Equal(identifier, variable.Identifier);
        Assert.Equal(port, variable.Port);
        Assert.True(variable.IsRoute);
    }

    [Theory]
    [InlineData("SERVICE_PASSWORD_POSTGRES", "PASSWORD", "POSTGRES", 32)]
    [InlineData("SERVICE_PASSWORD_64_ADMIN", "PASSWORD_64", "ADMIN", 64)]
    [InlineData("SERVICE_USER_MYSQL", "USER", "MYSQL", 16)]
    [InlineData("SERVICE_BASE64_APPKEY", "BASE64", "APPKEY", 32)]
    [InlineData("SERVICE_BASE64_64_KEY", "BASE64_64", "KEY", 64)]
    [InlineData("SERVICE_BASE64_128_KEY", "BASE64_128", "KEY", 128)]
    [InlineData("SERVICE_REALBASE64_64_KEY", "REALBASE64_64", "KEY", 64)]
    [InlineData("SERVICE_HEX_32_SALT", "HEX_32", "SALT", 32)]
    [InlineData("SERVICE_PASSWORDWITHSYMBOLS_64_ROOT", "PASSWORDWITHSYMBOLS_64", "ROOT", 64)]
    [InlineData("SERVICE_LOWERCASEUSER_DB", "LOWERCASEUSER", "DB", 16)]
    public void MagicVariable_ParsesGeneratorCommandsLongestFirst(string name, string command, string identifier,
        int length)
    {
        ServiceMagicVariable variable = ServiceMagicVariable.Parse(name);

        Assert.Equal(ServiceMagicVariableKind.Generated, variable.Kind);
        Assert.Equal(command, variable.Command);
        Assert.Equal(identifier, variable.Identifier);
        Assert.Equal(length, variable.Generator!.Length);
    }

    [Theory]
    [InlineData("SERVICE_")]
    [InlineData("SERVICE_PASSWORD")]
    [InlineData("SERVICE_PASSWORD_")]
    [InlineData("SERVICE_URL_")]
    [InlineData("SERVICE_URL_APP_70000")]
    [InlineData("SERVICE_URL_APP_0")]
    [InlineData("SERVICE_password_db")]
    [InlineData("SERVICE_SUPABASEANON_KONG")]
    [InlineData("SERVICE_UNKNOWN_THING")]
    [InlineData("SERVICE_URL_APP-1")]
    [InlineData("OTHER_PASSWORD_DB")]
    public void MagicVariable_RejectsMalformedOrUnsupportedNames(string name)
    {
        Assert.False(ServiceMagicVariable.TryParse(name, out _));
        Assert.Throws<DomainException>(() => ServiceMagicVariable.Parse(name));
    }

    [Fact]
    public void MagicVariable_RouteFamilyCoversUrlAndFqdnWithAndWithoutPort()
    {
        IReadOnlyList<string> family = ServiceMagicVariable.Parse("SERVICE_URL_APP_8080").RouteFamily();

        Assert.Equal(["SERVICE_URL_APP", "SERVICE_FQDN_APP", "SERVICE_URL_APP_8080", "SERVICE_FQDN_APP_8080"],
            family);
    }

    [Theory]
    [InlineData("SERVICE_PASSWORD_DB", 32, "^[A-Za-z0-9]+$")]
    [InlineData("SERVICE_PASSWORD_64_DB", 64, "^[A-Za-z0-9]+$")]
    [InlineData("SERVICE_USER_DB", 16, "^[A-Za-z0-9]+$")]
    [InlineData("SERVICE_LOWERCASEUSER_DB", 16, "^[a-z0-9]+$")]
    [InlineData("SERVICE_HEX_64_DB", 64, "^[0-9a-f]+$")]
    [InlineData("SERVICE_BASE64_128_DB", 128, "^[A-Za-z0-9]+$")]
    [InlineData("SERVICE_PASSWORDWITHSYMBOLS_DB", 32, "^[A-Za-z0-9!%^&*()\\-_=+\\[\\]{}:,.?~]+$")]
    public void Generator_ProducesExpectedLengthAndCharset(string name, int length, string pattern)
    {
        ServiceMagicVariable variable = ServiceMagicVariable.Parse(name);

        var first = ServiceMagicValueGenerator.Generate(variable);
        var second = ServiceMagicValueGenerator.Generate(variable);

        Assert.Equal(length, first.Length);
        Assert.Matches(pattern, first);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain('$', first);
        Assert.DoesNotContain('"', first);
        Assert.DoesNotContain('\\', first);
    }

    [Fact]
    public void Generator_RealBase64IsBase64OfRequestedBytes()
    {
        var value = ServiceMagicValueGenerator.Generate(ServiceMagicVariable.Parse("SERVICE_REALBASE64_64_KEY"));

        Assert.Equal(64, Convert.FromBase64String(value).Length);
    }

    [Fact]
    public void HeaderParser_ReadsCoolifyHeaderComments()
    {
        const string text = """
                            # documentation: https://n8n.io
                            # slogan: n8n is an extendable workflow automation tool.
                            # category: automation
                            # tags: n8n,Workflow, automation ,
                            # logo: svgs/n8n.png
                            # port: 5678
                            # minversion: 4.0.0-beta.300

                            services:
                              # comment inside the body: not a header
                              n8n:
                                image: n8nio/n8n:2.47.7
                            """;

        ServiceTemplateHeader header = ServiceTemplateHeaderParser.Parse(text);

        Assert.Equal("https://n8n.io", header.Documentation);
        Assert.Equal("n8n is an extendable workflow automation tool.", header.Slogan);
        Assert.Equal("automation", header.Category);
        Assert.Equal(["n8n", "workflow", "automation"], header.Tags);
        Assert.Equal("svgs/n8n.png", header.Logo);
        Assert.Equal(5678, header.Port);
        Assert.Equal("4.0.0-beta.300", header.MinimumVersion);
        Assert.False(header.Ignore);
    }

    [Fact]
    public void HeaderParser_ReadsIgnoreAndRejectsInvalidPort()
    {
        Assert.True(ServiceTemplateHeaderParser.Parse("# ignore: true\nservices: {}\n").Ignore);
        Assert.Throws<ServiceTemplateImportException>(() =>
            ServiceTemplateHeaderParser.Parse("# port: 99999\nservices: {}\n"));
    }

    [Fact]
    public void VariableReferences_ReadsDefaultsRequiredAndSkipsEscapedDollars()
    {
        IReadOnlyList<ServiceTemplateVariableReference> references = ServiceTemplateVariables.References(
            "a=${A:-one} b=${B-two} c=${C:?} d=$D_E:x e=$${NOT_A_VAR} f=${SERVICE_URL_APP}");

        Assert.Equal(["A", "B", "C", "D_E", "SERVICE_URL_APP"], references.Select(item => item.Name));
        Assert.Equal("one", references[0].Default);
        Assert.Equal("two", references[1].Default);
        Assert.True(references[2].Required);
        Assert.Null(references[3].Default);
    }

    [Theory]
    [InlineData("n8nio/n8n:2.47.7")]
    [InlineData("ghcr.io/umami-software/umami:3.5.0")]
    [InlineData("registry.example.com:5000/team/app:1.2.3")]
    [InlineData("mysql:8.0")]
    [InlineData("redis:8.10-alpine")]
    public void ImagePolicy_AcceptsVersionPinnedImages(string image)
    {
        Assert.Null(ServiceTemplateImagePolicy.Check("svc", image));
    }

    [Theory]
    [InlineData("ghost:6")]
    [InlineData("postgres:16-alpine")]
    [InlineData("app:latest@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void ImagePolicy_WarnsForMajorOnlyOrDigestPinnedFloatingTags(string image)
    {
        Assert.NotNull(ServiceTemplateImagePolicy.Check("svc", image));
    }

    [Theory]
    [InlineData("dpage/pgadmin4:latest")]
    [InlineData("dpage/pgadmin4")]
    [InlineData("registry.example.com:5000/team/app")]
    [InlineData("nginx:stable")]
    [InlineData("${IMAGE}")]
    [InlineData("app:${TAG:-1.0}")]
    public void ImagePolicy_RejectsUnpinnedImages(string image)
    {
        Assert.Throws<ServiceTemplateImportException>(() => ServiceTemplateImagePolicy.Check("svc", image));
    }

    [Fact]
    public void Catalog_ImportedTemplate_PlansWithRuntimeValuesAndValidatesInputs()
    {
        ServiceTemplateDefinition definition = SampleDefinition();
        var catalog = new ServiceTemplateCatalog([definition]);
        var serviceId = Guid.NewGuid();

        ServiceProvisioningPlan plan = catalog.CreatePlan(definition.Key, serviceId, "My App",
            new Dictionary<string, string> { ["MODE"] = "fast" },
            new ServiceTemplateRuntimeValues(
                new Dictionary<string, string> { ["SERVICE_PASSWORD_APP"] = "generated-secret" },
                new Dictionary<string, string> { ["SERVICE_URL_APP_3000"] = "https://app.example.com" },
                new Dictionary<string, string> { ["SMTP_PASSWORD"] = "smtp-secret" }));

        Assert.Equal("generated-secret", plan.SecretValues["SERVICE_PASSWORD_APP"]);
        Assert.Equal("https://app.example.com", plan.SecretValues["SERVICE_URL_APP_3000"]);
        Assert.Equal("fast", plan.SecretValues["MODE"]);
        Assert.Equal("smtp-secret", plan.SecretValues["SMTP_PASSWORD"]);
        Assert.Equal("UTC", plan.SecretValues["TZ"]);
        Assert.DoesNotContain("generated-secret", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains(catalog.List(), template => template.Key == definition.Key && template.Source == "coolify");
        Assert.Contains(catalog.List(), template => template.Key == "pgadmin" && template.Source == "vessel");
        Assert.Equal(("app", 3000), (catalog.DefaultEndpoint(definition.Key)!.ComposeService,
            catalog.DefaultEndpoint(definition.Key)!.Port));
    }

    [Fact]
    public void Catalog_ImportedTemplate_RejectsMagicAndUnknownInputs()
    {
        ServiceTemplateDefinition definition = SampleDefinition();
        var catalog = new ServiceTemplateCatalog([definition]);

        Assert.Throws<DomainException>(() => catalog.ValidateInputs(definition.Key,
            new Dictionary<string, string> { ["SERVICE_PASSWORD_APP"] = "chosen" }));
        Assert.Throws<DomainException>(() => catalog.ValidateInputs(definition.Key,
            new Dictionary<string, string> { ["UNKNOWN"] = "x" }));
        Assert.Throws<DomainException>(() => catalog.CreatePlan(definition.Key, Guid.NewGuid(), "x",
            new Dictionary<string, string>()));
        (Dictionary<string, string> plain, Dictionary<string, string> secret) = catalog.SplitInputs(definition.Key,
            new Dictionary<string, string> { ["MODE"] = "slow", ["SMTP_PASSWORD"] = "pw" });
        Assert.Equal(["MODE"], plain.Keys);
        Assert.Equal(["SMTP_PASSWORD"], secret.Keys);
    }

    [Fact]
    public void Catalog_RejectsDuplicateKeys()
    {
        ServiceTemplateDefinition definition = SampleDefinition();

        Assert.Throws<InvalidOperationException>(() => new ServiceTemplateCatalog([definition, definition]));
    }

    [Fact]
    public async Task RuntimeResolver_GeneratesOnceStoresInVaultAndReusesOnReprovision()
    {
        var db = new BackupTestDbContext();
        var vault = new DictionaryVault(db);
        var resolver = new ServiceTemplateRuntimeResolver(db, vault, TimeProvider.System);
        ServiceResource service = ServiceResource.Create(TeamId.New(), EnvironmentId.New(), ServerId.New(),
            new ResourceName("app"), "coolify-sample", "coolify-1", "{}", Now);
        CoolifyComposeTemplate template = SampleDefinition().Coolify!;

        ServiceTemplateRuntimeValues first = await resolver.ResolveAsync(service, template);
        SecretReferenceId reference = service.GeneratedSecretsReferenceId!.Value;
        ServiceTemplateRuntimeValues second = await resolver.ResolveAsync(service, template);

        Assert.Equal(first.GeneratedValues["SERVICE_PASSWORD_APP"], second.GeneratedValues["SERVICE_PASSWORD_APP"]);
        Assert.Equal(reference, service.GeneratedSecretsReferenceId);
        Assert.Equal(32, first.GeneratedValues["SERVICE_PASSWORD_APP"].Length);
        Dictionary<string, string> stored = JsonSerializer.Deserialize<Dictionary<string, string>>(
            vault.Secrets[reference])!;
        Assert.Equal(first.GeneratedValues["SERVICE_PASSWORD_APP"], stored["SERVICE_PASSWORD_APP"]);
        // Without a service domain the route variables fall back to the internal address.
        Assert.Equal("http://app:3000", first.RouteValues["SERVICE_URL_APP_3000"]);
        Assert.Equal("app:3000", first.RouteValues["SERVICE_FQDN_APP"]);
    }

    [Fact]
    public async Task RuntimeResolver_GeneratesOnlyVariablesANewTemplateVersionAdded()
    {
        var db = new BackupTestDbContext();
        var vault = new DictionaryVault(db);
        var resolver = new ServiceTemplateRuntimeResolver(db, vault, TimeProvider.System);
        ServiceResource service = ServiceResource.Create(TeamId.New(), EnvironmentId.New(), ServerId.New(),
            new ResourceName("app"), "coolify-sample", "coolify-1", "{}", Now);
        await resolver.StoreSecretInputsAsync(service, new Dictionary<string, string> { ["SMTP_PASSWORD"] = "pw" });
        CoolifyComposeTemplate template = SampleDefinition().Coolify!;
        ServiceTemplateRuntimeValues first = await resolver.ResolveAsync(service, template);
        CoolifyComposeTemplate newer = template with
        {
            GeneratedVariables = [.. template.GeneratedVariables, ServiceMagicVariable.Parse("SERVICE_USER_APP")]
        };

        ServiceTemplateRuntimeValues second = await resolver.ResolveAsync(service, newer);

        Assert.Equal(first.GeneratedValues["SERVICE_PASSWORD_APP"], second.GeneratedValues["SERVICE_PASSWORD_APP"]);
        Assert.Equal(16, second.GeneratedValues["SERVICE_USER_APP"].Length);
        Assert.Equal("pw", second.SecretInputs!["SMTP_PASSWORD"]);
        Assert.DoesNotContain(second.GeneratedValues.Keys, key => key.StartsWith("input:", StringComparison.Ordinal));
    }

    [Fact]
    public void RouteValues_UseServiceDomainForEndpointServiceAndPort()
    {
        CoolifyComposeTemplate template = SampleDefinition().Coolify!;
        var serviceId = ServiceResourceId.New();
        ServiceDomain other = ServiceDomain.Create(serviceId, RouteAddress.Parse("other.example.com"), "worker",
            9000, true, null, Now);
        ServiceDomain domain = ServiceDomain.Create(serviceId, RouteAddress.Parse("app.example.com"), "app", 3000,
            true, null, Now);

        IReadOnlyDictionary<string, string> values = ServiceTemplateRuntimeResolver.RouteValues(template,
            [other, domain]);

        Assert.Equal("https://app.example.com", values["SERVICE_URL_APP"]);
        Assert.Equal("https://app.example.com", values["SERVICE_URL_APP_3000"]);
        Assert.Equal("app.example.com", values["SERVICE_FQDN_APP_3000"]);
    }

    private static ServiceTemplateDefinition SampleDefinition()
    {
        var endpoint = new ServiceTemplateEndpoint("app", 3000, "APP", null);
        ServiceTemplateRouteVariable[] routes = ServiceMagicVariable.Parse("SERVICE_URL_APP_3000").RouteFamily()
            .Select(name => new ServiceTemplateRouteVariable(ServiceMagicVariable.Parse(name), endpoint))
            .ToArray();
        const string compose = """
                               services:
                                 app:
                                   image: example/app:1.2.3
                                   environment:
                                   - SERVICE_URL_APP_3000=${SERVICE_URL_APP_3000}
                                   - PASSWORD=${SERVICE_PASSWORD_APP}
                               """;
        var coolify = new CoolifyComposeTemplate(compose, [ServiceMagicVariable.Parse("SERVICE_PASSWORD_APP")],
            routes, ["example/app:1.2.3"]);
        var metadata = new ServiceTemplateMetadata("coolify", null, "Sample", null, [], null, 3000, null, [],
            [endpoint]);
        return new ServiceTemplateDefinition("coolify-sample", "Sample", "Sample", "coolify-1",
        [
            new ServiceTemplateInput("MODE", "MODE", false, false, null),
            new ServiceTemplateInput("SMTP_PASSWORD", "SMTP PASSWORD", false, true, null),
            new ServiceTemplateInput("TZ", "TZ", false, false, "UTC")
        ], _ => compose, metadata, coolify);
    }
}
