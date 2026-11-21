using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vessel.Application.Authorization;
using Vessel.Web.Controllers;
using Vessel.Web.Controllers.Api.V1;
using Vessel.Web.OpenApi;
using Vessel.Web.Security;

namespace Vessel.E2ETests.Api;

/// <summary>API-01: the OpenAPI document, its access annotations and the "every mutation declares access" contract.</summary>
public sealed class OpenApiDocumentTests
{
    private const string TestScheme = "Test";
    private const string TestHeader = "X-Test-User";

    /// <summary>
    ///     Mutating actions that intentionally use a plain <c>[Authorize]</c> (any authenticated principal) because the
    ///     application layer performs the decisive check. Each entry needs a documented reason.
    /// </summary>
    private static readonly Dictionary<string, string> PlainAuthorizeAllowList =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public async Task Document_IsNotServedToAnonymousCallers()
    {
        await using WebApplication app = await StartHostAsync();
        using HttpClient client = Client(app);

        using HttpResponseMessage response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Document_DescribesSecuritySchemesAndAccessAnnotations()
    {
        await using WebApplication app = await StartHostAsync();
        using HttpClient client = Client(app);
        client.DefaultRequestHeaders.Add(TestHeader, "1");

        using HttpResponseMessage response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;

        Assert.Equal("Vessel API", root.GetProperty("info").GetProperty("title").GetString());
        JsonElement schemes = root.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal("http", schemes.GetProperty("bearer").GetProperty("type").GetString());
        Assert.Equal("bearer", schemes.GetProperty("bearer").GetProperty("scheme").GetString());
        Assert.Equal("cookie", schemes.GetProperty("cookie").GetProperty("in").GetString());
        Assert.Equal("__Host-Vessel", schemes.GetProperty("cookie").GetProperty("name").GetString());

        JsonElement paths = root.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/projects", out _));
        Assert.DoesNotContain(paths.EnumerateObject(), path => path.Name.StartsWith("/hubs", StringComparison.Ordinal));
        Assert.False(paths.TryGetProperty("/health", out _));

        // Archive (DELETE) requires the delete permission: delete/root tokens, never a plain write token.
        JsonElement archive = paths.GetProperty("/api/v1/projects/{projectId}").GetProperty("delete");
        Assert.Contains(VesselPermissions.ProjectsDelete, Strings(archive.GetProperty("x-vessel-permissions")));
        IReadOnlyList<string> abilities = Strings(archive.GetProperty("x-vessel-token-abilities"));
        Assert.Contains(TokenScopeMapper.Delete, abilities);
        Assert.Contains(TokenScopeMapper.Root, abilities);
        Assert.DoesNotContain(TokenScopeMapper.Write, abilities);
        Assert.Equal(2, archive.GetProperty("security").GetArrayLength());

        // Token lifecycle edits are session-only: cookie security only, no token abilities.
        JsonElement pause = paths.GetProperty("/api/tokens/{id}/pause").GetProperty("post");
        Assert.True(pause.GetProperty("x-vessel-session-only").GetBoolean());
        Assert.Empty(Strings(pause.GetProperty("x-vessel-token-abilities")));
        JsonElement only = Assert.Single(pause.GetProperty("security").EnumerateArray());
        Assert.True(only.TryGetProperty("cookie", out _));

        // Anonymous endpoints carry no security requirement.
        JsonElement login = paths.GetProperty("/api/auth/login").GetProperty("post");
        Assert.True(login.GetProperty("x-vessel-anonymous").GetBoolean());
        Assert.False(login.TryGetProperty("security", out _));
    }

    [Fact]
    public void EndpointAccess_DerivesTokenAbilitiesFromTheAbilityHierarchy()
    {
        VesselEndpointAccess archive = Access(typeof(ProjectsController),
            nameof(ProjectsController.Archive));
        Assert.Equal([VesselPermissions.ProjectsDelete, VesselPermissions.ProjectsRead],
            archive.Permissions.Order(StringComparer.Ordinal));
        Assert.Equal([TokenScopeMapper.Delete, TokenScopeMapper.Root], archive.TokenAbilities);

        VesselEndpointAccess lifecycle = Access(typeof(DatabasesController), nameof(DatabasesController.Lifecycle));
        Assert.Contains(TokenScopeMapper.Deploy, lifecycle.TokenAbilities);
        Assert.Contains(TokenScopeMapper.Write, lifecycle.TokenAbilities);
        Assert.DoesNotContain(TokenScopeMapper.Read, lifecycle.TokenAbilities);

        VesselEndpointAccess instance = Access(typeof(InstanceController),
            nameof(InstanceController.GetSettings));
        Assert.True(instance.SessionOnly);
        Assert.Empty(instance.TokenAbilities);

        VesselEndpointAccess anonymous = Access(typeof(AuthController), nameof(AuthController.Login));
        Assert.True(anonymous.Anonymous);
    }

    [Fact]
    public void EveryMutatingApiAction_DeclaresItsAccessLevel()
    {
        var undeclared = new List<string>();
        foreach ((Type controller, MethodInfo action, string method, string route) in MutatingApiActions())
        {
            VesselEndpointAccess access = VesselEndpointAccess.From(Metadata(controller, action));
            if (access.DeclaresAccessLevel) continue;

            var key = $"{controller.Name}.{action.Name}";
            if (!PlainAuthorizeAllowList.ContainsKey(key)) undeclared.Add($"{method} /{route} ({key})");
        }

        Assert.True(undeclared.Count == 0,
            "Mutating API actions without a permission, session policy or [AllowAnonymous]: "
            + string.Join(", ", undeclared));
    }

    [Fact]
    public void MutatingApiActionsAreDiscovered()
    {
        Assert.Contains(MutatingApiActions(), item => item.Action.Name == nameof(ProjectsController.Archive));
        Assert.Contains(MutatingApiActions(), item => item.Route.StartsWith("api/tokens", StringComparison.Ordinal));
        Assert.Contains(MutatingApiActions(), item => item.Route.StartsWith("api/teams", StringComparison.Ordinal));
    }

    private static IEnumerable<(Type Controller, MethodInfo Action, string Method, string Route)> MutatingApiActions()
    {
        string[] prefixes = ["api/v1", "api/teams", "api/tokens"];
        IEnumerable<Type> controllers = typeof(AuthController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);

        foreach (Type controller in controllers)
        {
            var prefix = controller.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;
            foreach (MethodInfo action in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public
                                                                | BindingFlags.DeclaredOnly))
                foreach (HttpMethodAttribute verb in action.GetCustomAttributes<HttpMethodAttribute>(true))
                {
                    if (verb.HttpMethods.All(method => method is "GET" or "HEAD")) continue;

                    var template = verb.Template ?? string.Empty;
                    var route = template.StartsWith('/') ? template.TrimStart('/')
                        : string.IsNullOrEmpty(template) ? prefix : $"{prefix}/{template}";
                    if (!prefixes.Any(p => route.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

                    yield return (controller, action, string.Join('|', verb.HttpMethods), route);
                }
        }
    }

    private static VesselEndpointAccess Access(Type controller, string action)
    {
        return VesselEndpointAccess.From(Metadata(controller, controller.GetMethod(action)!));
    }

    private static List<object> Metadata(Type controller, MethodInfo action)
    {
        return controller.GetCustomAttributes(true).Concat(action.GetCustomAttributes(true)).ToList();
    }

    private static List<string> Strings(JsonElement array)
    {
        return array.EnumerateArray().Select(item => item.GetString()!).ToList();
    }

    private static async Task<WebApplication> StartHostAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(AuthController).Assembly.GetName().Name
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
        builder.Services.AddVesselOpenApi();
        builder.Services.AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(TestScheme, _ => { });
        builder.Services.AddAuthorization();

        WebApplication app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapVesselOpenApi();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpClient Client(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(TestHeader)) return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D"))],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity),
                TestScheme)));
        }
    }
}
