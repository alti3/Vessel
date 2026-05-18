using System.Globalization;
using Serilog;
using Vessel.Infrastructure.Extensions;
using Vessel.Web.Extensions;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, loggerConfiguration) =>
    {
        loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", "Vessel.Web")
            .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName);
    });

    builder.Services
        .AddVesselWebHost(builder.Configuration, builder.Environment)
        .AddVesselInfrastructure(builder.Configuration);

    WebApplication app = builder.Build();

    app.UseVesselWebHost();
    app.MapVesselEndpoints();

    app.Run();
}
catch (Exception exception)
{
    Log.Fatal(exception, "Vessel host terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
