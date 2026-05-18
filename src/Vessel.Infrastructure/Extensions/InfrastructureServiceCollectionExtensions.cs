using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vessel.Application.Auditing;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Infrastructure.Auditing;
using Vessel.Infrastructure.Configuration;
using Vessel.Infrastructure.HealthChecks;
using Vessel.Infrastructure.Persistence;
using Vessel.Infrastructure.Security;

namespace Vessel.Infrastructure.Extensions;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddVesselInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<Argon2Options>()
            .Bind(configuration.GetSection(Argon2Options.SectionName))
            .Validate(options => options.DegreeOfParallelism > 0, "Argon2 degree of parallelism must be positive.")
            .Validate(options => options.Iterations > 0, "Argon2 iterations must be positive.")
            .Validate(options => options.MemorySize >= 8192, "Argon2 memory size must be at least 8192 KiB.")
            .ValidateOnStart();

        services
            .AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>());

        services
            .AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RedisOptions>, RedisOptionsValidator>());

        services
            .AddOptions<HangfireStorageOptions>()
            .Bind(configuration.GetSection(HangfireStorageOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<HangfireStorageOptions>, HangfireStorageOptionsValidator>());

        services
            .AddOptions<ObjectStorageOptions>()
            .Bind(configuration.GetSection(ObjectStorageOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<ObjectStorageOptions>, ObjectStorageOptionsValidator>());

        DatabaseOptions databaseOptions = configuration
            .GetSection(DatabaseOptions.SectionName)
            .Get<DatabaseOptions>() ?? new DatabaseOptions();

        if (databaseOptions.Enabled && !string.IsNullOrWhiteSpace(databaseOptions.ConnectionString))
        {
            services.AddDbContext<VesselDbContext>(options => options.UseNpgsql(
                databaseOptions.ConnectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "vessel")));
            services.AddScoped<IVesselDbContext>(provider => provider.GetRequiredService<VesselDbContext>());
            services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<VesselDbContext>());
            services.AddScoped<IAuditWriter, EfAuditWriter>();
        }

        services.TryAddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.TryAddSingleton<ITokenGenerator, SecureTokenGenerator>();
        services.AddHttpClient(ObjectStorageHealthCheck.HttpClientName);

        return services;
    }
}
