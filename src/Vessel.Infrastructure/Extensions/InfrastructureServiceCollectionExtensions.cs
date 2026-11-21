using Vessel.Application.Monitoring.HostMetrics;
using Vessel.Infrastructure.Monitoring;
using Vessel.Application.Backups.Volumes;
using Vessel.Application.ManagedServices.DatabaseImports;
using Vessel.Infrastructure.Backups.Volumes;
using Vessel.Infrastructure.ManagedServices.DatabaseImports;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vessel.Application.Auditing;
using Vessel.Application.Backups;
using Vessel.Application.Dashboard;
using Vessel.Application.Deployments;
using Vessel.Application.Docker;
using Vessel.Application.Files;
using Vessel.Application.Git;
using Vessel.Application.Jobs;
using Vessel.Application.ManagedServices;
using Vessel.Application.ManagedServices.Templates;
using Vessel.Application.Monitoring;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Processes;
using Vessel.Application.Proxy;
using Vessel.Application.Proxy.Certificates;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Servers;
using Vessel.Application.Servers.Files;
using Vessel.Application.Ssh;
using Vessel.Application.Storage;
using Vessel.Application.Terminals;
using Vessel.Infrastructure.Auditing;
using Vessel.Infrastructure.Configuration;
using Vessel.Infrastructure.Dashboard;
using Vessel.Infrastructure.Deployments;
using Vessel.Infrastructure.Docker;
using Vessel.Infrastructure.Files;
using Vessel.Infrastructure.Git;
using Vessel.Infrastructure.HealthChecks;
using Vessel.Infrastructure.Jobs;
using Vessel.Infrastructure.ManagedServices;
using Vessel.Infrastructure.ManagedServices.Templates;
using Vessel.Infrastructure.Notifications;
using Vessel.Infrastructure.Operations;
using Vessel.Infrastructure.Persistence;
using Vessel.Infrastructure.Processes;
using Vessel.Infrastructure.Proxy;
using Vessel.Infrastructure.Redis;
using Vessel.Infrastructure.Security;
using Vessel.Infrastructure.Servers;
using Vessel.Infrastructure.Ssh;
using Vessel.Infrastructure.Storage;
using Vessel.Infrastructure.Sources;

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

        bool isDevelopmentEnvironment = SecretStorageOptionsValidator.IsDevelopment(configuration);
        services
            .AddOptions<SecretStorageOptions>()
            .Bind(configuration.GetSection(SecretStorageOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SecretStorageOptions>>(
            new SecretStorageOptionsValidator(isDevelopmentEnvironment)));
        services.TryAddSingleton(provider => SecretKeyRing.Create(
            provider.GetRequiredService<IOptions<SecretStorageOptions>>().Value,
            isDevelopmentEnvironment));
        services.TryAddSingleton<ISecretCipher, AesGcmSecretCipher>();

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
            services.AddScoped<INotificationCatalogQuery>(provider =>
                provider.GetRequiredService<EfDashboardQueries>());
            services.AddScoped<ISettingsCatalogQuery>(provider => provider.GetRequiredService<EfDashboardQueries>());
            services.AddScoped<IServerHealthQuery>(provider => provider.GetRequiredService<EfDashboardQueries>());
            // SRV-06: ends terminal sessions whose user lost access (needs persistence).
            services.AddHostedService<TerminalSessionRevalidationService>();
        }

        services.TryAddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.TryAddSingleton<ITokenGenerator, SecureTokenGenerator>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISecretRedactor, SecretRedactor>();
        services.AddScoped<ISecretVault, AesSecretVault>();
        services.TryAddSingleton<IPathSafetyService, PathSafetyService>();
        services.TryAddSingleton<IDeploymentWorkspaceManager, LocalDeploymentWorkspaceManager>();
        services.TryAddSingleton<IComposeDocumentBuilder, YamlComposeDocumentBuilder>();
        services.TryAddSingleton<IServiceTemplateComposeProcessor, YamlServiceTemplateComposeProcessor>();
        services.TryAddSingleton<IServiceTemplateSource, EmbeddedServiceTemplateSource>();
        services.TryAddSingleton<IManagedServiceWorkspace, LocalManagedServiceWorkspace>();
        services.TryAddSingleton<IProcessRunner, DotNetProcessRunner>();
        if (OperatingSystem.IsLinux())
            services.AddSingleton<ITerminalProcessBridge, PtyTerminalBridge>();
        else
            services.AddSingleton<ITerminalProcessBridge, InteractiveTerminalProcessBridge>();
        services.TryAddSingleton<DockerCliContainerRuntimeClient>();
        services.TryAddSingleton<IContainerRuntimeClient, DockerApiContainerRuntimeClient>();
        // SRV-03: agentless host telemetry over the server's own connection, and set-based metric retention.
        services.TryAddSingleton<IServerTelemetryCollector, AgentlessTelemetryCollector>();
        services.AddScoped<IServerMetricsPruner, EfServerMetricsPruner>();
        services.TryAddSingleton<IGitClient, GitProcessClient>();
        services.TryAddSingleton<ISshClient, SshProcessClient>();
        services.AddOptions<SshOptions>().Bind(configuration.GetSection(SshOptions.SectionName));
        services.TryAddSingleton<SshRuntimeFiles>();
        services.TryAddSingleton<SshHostKeyScanThrottle>();
        services.TryAddSingleton<ISshHostKeyScanner, SshHostKeyScanner>();
        services.TryAddSingleton<ISshKeyTool, SshKeygenTool>();
        services.TryAddSingleton<IServerSshConnectionFactory, ServerSshConnectionFactory>();
        services.TryAddSingleton<IProxyProvider, TraefikProxyProvider>();
        services.AddOptions<ServerFilesOptions>().Bind(configuration.GetSection(ServerFilesOptions.SectionName));
        services.TryAddSingleton<ServerCommandExecutor>();
        services.TryAddSingleton<IServerCommandRunner>(provider => provider.GetRequiredService<ServerCommandExecutor>());
        services.TryAddSingleton<IServerDirectoryTransfer, SshServerDirectoryTransfer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServerFileChannel, LocalServerFileChannel>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServerFileChannel, SshServerFileChannel>());
        services.TryAddSingleton<IProxyContainerRuntime, DockerProxyContainerRuntime>();
        services.TryAddSingleton<IProxyRuntimeRenderer, TraefikRuntimeRenderer>();
        services.TryAddSingleton<ITraefikAcmeStore, TraefikAcmeStore>();
        services.AddSingleton<IDatabaseBackupProvider, ProcessDatabaseBackupProvider>();
        // DATA-09 volume archives (helper container) and DATA-10 import staging.
        services.TryAddSingleton<IVolumeArchiveRunner, ContainerVolumeArchiveRunner>();
        services.AddOptions<DatabaseImportOptions>().Bind(configuration.GetSection(DatabaseImportOptions.SectionName));
        services.TryAddSingleton<IDatabaseImportStaging, FileDatabaseImportStaging>();
        services.AddVesselNotificationInfrastructure(configuration);
        services.AddVesselGitSourceInfrastructure();
        services.AddOptions<BackupStorageOptions>().Bind(configuration.GetSection(BackupStorageOptions.SectionName));
        services.AddSingleton<IBackupStorageProvider, BackupStorageProvider>();
        services.AddHttpClient(ObjectStorageHealthCheck.HttpClientName);
        services.AddVesselSchedulingInfrastructure(configuration);
        services.AddVesselOperationsInfrastructure(configuration);

        RedisOptions redisOptions = configuration
            .GetSection(RedisOptions.SectionName)
            .Get<RedisOptions>() ?? new RedisOptions();
        if (redisOptions.Enabled)
        {
            services.TryAddSingleton<RedisConnectionProvider>();
            // Replaces the in-process fallback the Web host registers when Redis is disabled.
            services.AddSingleton<IRedisCache, RedisCache>();
            services.AddSingleton<IDistributedLockManager, RedisDistributedLockManager>();
        }

        ObjectStorageOptions objectStorageOptions = configuration
            .GetSection(ObjectStorageOptions.SectionName)
            .Get<ObjectStorageOptions>() ?? new ObjectStorageOptions();
        if (objectStorageOptions.Enabled)
        {
            if (string.Equals(objectStorageOptions.Provider, "Local", StringComparison.OrdinalIgnoreCase))
                services.AddSingleton<IObjectStorage>(provider =>
                {
                    var root = objectStorageOptions.LocalRootDirectory
                               ?? Path.Combine(AppContext.BaseDirectory, "storage", "objects");
                    return new LocalObjectStorage(root, provider.GetRequiredService<IPathSafetyService>());
                });
            else
                services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        }

        HangfireStorageOptions hangfireOptions = configuration
            .GetSection(HangfireStorageOptions.SectionName)
            .Get<HangfireStorageOptions>() ?? new HangfireStorageOptions();
        if (hangfireOptions.Enabled && !string.IsNullOrWhiteSpace(hangfireOptions.ConnectionString))
        {
            // NTF-08: per-job retry policy (scheduled occurrences and tasks are never retried into duplicates).
            VesselRetryPolicyFilter.Install(GlobalJobFilters.Filters);
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
            services.AddSingleton<IRecurringJobScheduler, HangfireRecurringJobScheduler>();
        }

        return services;
    }
}
