using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vessel.Application.Auditing;
using Vessel.Application.Dashboard;
using Vessel.Application.Deployments;
using Vessel.Application.Docker;
using Vessel.Application.Files;
using Vessel.Application.Git;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Processes;
using Vessel.Application.Proxy;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Ssh;
using Vessel.Application.Storage;
using Vessel.Infrastructure.Auditing;
using Vessel.Infrastructure.Configuration;
using Vessel.Infrastructure.Dashboard;
using Vessel.Infrastructure.Deployments;
using Vessel.Infrastructure.Docker;
using Vessel.Infrastructure.Files;
using Vessel.Infrastructure.Git;
using Vessel.Infrastructure.HealthChecks;
using Vessel.Infrastructure.Jobs;
using Vessel.Infrastructure.Persistence;
using Vessel.Infrastructure.Processes;
using Vessel.Infrastructure.Proxy;
using Vessel.Infrastructure.Redis;
using Vessel.Infrastructure.Security;
using Vessel.Infrastructure.Ssh;
using Vessel.Infrastructure.Storage;

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

        services
            .AddOptions<SecretStorageOptions>()
            .Bind(configuration.GetSection(SecretStorageOptions.SectionName))
            .Validate(options => string.IsNullOrWhiteSpace(options.MasterKey)
                                 || Convert.FromBase64String(options.MasterKey).Length == 32,
                "Secrets:MasterKey must be a base64-encoded 256-bit key when configured.")
            .ValidateOnStart();

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
            services.AddScoped<EfDashboardQueries>();
            services.AddScoped<IDashboardOverviewQuery>(provider => provider.GetRequiredService<EfDashboardQueries>());
            services.AddScoped<IProjectCatalogQuery>(provider => provider.GetRequiredService<EfDashboardQueries>());
            services.AddScoped<IServerCatalogQuery>(provider => provider.GetRequiredService<EfDashboardQueries>());
            services.AddScoped<IApplicationCatalogQuery>(provider => provider.GetRequiredService<EfDashboardQueries>());
            services.AddScoped<IDeploymentCatalogQuery>(provider => provider.GetRequiredService<EfDashboardQueries>());
            services.AddScoped<IDatabaseCatalogQuery>(provider => provider.GetRequiredService<EfDashboardQueries>());
            services.AddScoped<INotificationCatalogQuery>(provider => provider.GetRequiredService<EfDashboardQueries>());
            services.AddScoped<ISettingsCatalogQuery>(provider => provider.GetRequiredService<EfDashboardQueries>());
        }

        services.TryAddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.TryAddSingleton<ITokenGenerator, SecureTokenGenerator>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISecretRedactor, SecretRedactor>();
        services.AddScoped<ISecretVault, AesSecretVault>();
        services.TryAddSingleton<IPathSafetyService, PathSafetyService>();
        services.TryAddSingleton<IDeploymentWorkspaceManager, LocalDeploymentWorkspaceManager>();
        services.TryAddSingleton<IProcessRunner, DotNetProcessRunner>();
        services.TryAddSingleton<DockerCliContainerRuntimeClient>();
        services.TryAddSingleton<IContainerRuntimeClient, DockerApiContainerRuntimeClient>();
        services.TryAddSingleton<IGitClient, GitProcessClient>();
        services.TryAddSingleton<ISshClient, SshProcessClient>();
        services.TryAddSingleton<IProxyProvider, TraefikProxyProvider>();
        services.AddHttpClient(ObjectStorageHealthCheck.HttpClientName);

        RedisOptions redisOptions = configuration
            .GetSection(RedisOptions.SectionName)
            .Get<RedisOptions>() ?? new RedisOptions();
        if (redisOptions.Enabled)
        {
            services.TryAddSingleton<RedisConnectionProvider>();
            services.TryAddSingleton<IRedisCache, RedisCache>();
            services.AddSingleton<IDistributedLockManager, RedisDistributedLockManager>();
        }

        ObjectStorageOptions objectStorageOptions = configuration
            .GetSection(ObjectStorageOptions.SectionName)
            .Get<ObjectStorageOptions>() ?? new ObjectStorageOptions();
        if (objectStorageOptions.Enabled)
        {
            if (string.Equals(objectStorageOptions.Provider, "Local", StringComparison.OrdinalIgnoreCase))
            {
                services.TryAddSingleton<IObjectStorage>(provider =>
                {
                    string root = objectStorageOptions.LocalRootDirectory
                                  ?? Path.Combine(AppContext.BaseDirectory, "storage", "objects");
                    return new LocalObjectStorage(root, provider.GetRequiredService<IPathSafetyService>());
                });
            }
            else
            {
                services.TryAddSingleton<IObjectStorage, S3ObjectStorage>();
            }
        }

        HangfireStorageOptions hangfireOptions = configuration
            .GetSection(HangfireStorageOptions.SectionName)
            .Get<HangfireStorageOptions>() ?? new HangfireStorageOptions();
        if (hangfireOptions.Enabled && !string.IsNullOrWhiteSpace(hangfireOptions.ConnectionString))
        {
            services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(hangfireOptions.ConnectionString)));
            services.AddHangfireServer(options =>
            {
                options.Queues = ["critical", "deployments", "default", "maintenance"];
                options.WorkerCount = Math.Max(1, Environment.ProcessorCount);
            });
            services.AddSingleton<IBackgroundJobDispatcher, HangfireBackgroundJobDispatcher>();
        }

        return services;
    }
}
