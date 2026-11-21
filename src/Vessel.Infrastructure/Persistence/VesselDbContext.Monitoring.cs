using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Servers;

namespace Vessel.Infrastructure.Persistence;

/// <summary>
///     Monitoring model (SRV-03/04/07/08): time-bucketed server metric samples (raw 24 h, hourly rollups 30 days),
///     container samples (24 h), per-server monitoring settings with the Docker cleanup policy and alert state, resource
///     runtime status, and Docker cleanup executions. Status and sample rows carry no concurrency token: they are
///     written by collectors only, and EF updates only the columns that changed.
/// </summary>
public sealed partial class VesselDbContext
{
    private static readonly ValueConverter<ServerMetricSampleId, Guid> ServerMetricSampleIdConverter =
        new(id => id.Value, value => new ServerMetricSampleId(value));

    private static readonly ValueConverter<ContainerMetricSampleId, Guid> ContainerMetricSampleIdConverter =
        new(id => id.Value, value => new ContainerMetricSampleId(value));

    private static readonly ValueConverter<DockerCleanupExecutionId, Guid> DockerCleanupExecutionIdConverter =
        new(id => id.Value, value => new DockerCleanupExecutionId(value));

    private static readonly ValueConverter<ResourceRuntimeStatusId, Guid> ResourceRuntimeStatusIdConverter =
        new(id => id.Value, value => new ResourceRuntimeStatusId(value));

    private IRepository<ServerMetricSample, ServerMetricSampleId>? _serverMetricSampleRepository;
    private IRepository<ContainerMetricSample, ContainerMetricSampleId>? _containerMetricSampleRepository;
    private IRepository<ServerMonitoringSettings, ServerId>? _serverMonitoringSettingsRepository;
    private IRepository<ResourceRuntimeStatus, ResourceRuntimeStatusId>? _resourceRuntimeStatusRepository;
    private IRepository<DockerCleanupExecution, DockerCleanupExecutionId>? _dockerCleanupExecutionRepository;

    public DbSet<ServerMetricSample> ServerMetricSampleSet => Set<ServerMetricSample>();

    public DbSet<ContainerMetricSample> ContainerMetricSampleSet => Set<ContainerMetricSample>();

    public IQueryable<ServerMetricSample> ServerMetricSamples => ServerMetricSampleSet;

    public IQueryable<ContainerMetricSample> ContainerMetricSamples => ContainerMetricSampleSet;

    public IQueryable<ServerMonitoringSettings> ServerMonitoringSettings => Set<ServerMonitoringSettings>();

    public IQueryable<ResourceRuntimeStatus> ResourceRuntimeStatuses => Set<ResourceRuntimeStatus>();

    public IQueryable<DockerCleanupExecution> DockerCleanupExecutions => Set<DockerCleanupExecution>();

    public IRepository<ServerMetricSample, ServerMetricSampleId> ServerMetricSampleRepository =>
        _serverMetricSampleRepository ??= new EfRepository<ServerMetricSample, ServerMetricSampleId>(this);

    public IRepository<ContainerMetricSample, ContainerMetricSampleId> ContainerMetricSampleRepository =>
        _containerMetricSampleRepository ??= new EfRepository<ContainerMetricSample, ContainerMetricSampleId>(this);

    public IRepository<ServerMonitoringSettings, ServerId> ServerMonitoringSettingsRepository =>
        _serverMonitoringSettingsRepository ??= new EfRepository<ServerMonitoringSettings, ServerId>(this);

    public IRepository<ResourceRuntimeStatus, ResourceRuntimeStatusId> ResourceRuntimeStatusRepository =>
        _resourceRuntimeStatusRepository ??= new EfRepository<ResourceRuntimeStatus, ResourceRuntimeStatusId>(this);

    public IRepository<DockerCleanupExecution, DockerCleanupExecutionId> DockerCleanupExecutionRepository =>
        _dockerCleanupExecutionRepository ??= new EfRepository<DockerCleanupExecution, DockerCleanupExecutionId>(this);

    private static void ConfigureMonitoring(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServerMetricSample>(builder =>
        {
            builder.ToTable("server_metric_samples");
            builder.HasKey(sample => sample.Id);
            builder.Property(sample => sample.Id).HasConversion(ServerMetricSampleIdConverter).ValueGeneratedNever();
            builder.Property(sample => sample.ServerId).HasServerIdConversion();
            builder.Property(sample => sample.Resolution).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(sample => sample.CpuPercent).HasPrecision(7, 2);
            builder.Property(sample => sample.CpuPercentMax).HasPrecision(7, 2);
            builder.Property(sample => sample.Load1).HasPrecision(9, 2);
            builder.Property(sample => sample.Load5).HasPrecision(9, 2);
            builder.Property(sample => sample.Load15).HasPrecision(9, 2);
            builder.Property(sample => sample.DiskMount).HasMaxLength(255);
            builder.Ignore(sample => sample.MemoryPercent);
            builder.Ignore(sample => sample.DiskPercent);
            builder.Ignore(sample => sample.DomainEvents);
            // One rollup per server and hour; raw samples are queried by server, resolution and time.
            builder.HasIndex(sample => new { sample.ServerId, sample.Resolution, sample.BucketStart }).IsUnique();
            builder.HasIndex(sample => new { sample.Resolution, sample.BucketStart });
            builder.HasOne<Server>().WithMany().HasForeignKey(sample => sample.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContainerMetricSample>(builder =>
        {
            builder.ToTable("container_metric_samples");
            builder.HasKey(sample => sample.Id);
            builder.Property(sample => sample.Id).HasConversion(ContainerMetricSampleIdConverter)
                .ValueGeneratedNever();
            builder.Property(sample => sample.ServerId).HasServerIdConversion();
            builder.Property(sample => sample.ContainerId).HasMaxLength(128).IsRequired();
            builder.Property(sample => sample.ContainerName).HasMaxLength(255).IsRequired();
            builder.Property(sample => sample.ResourceKind).HasConversion<string>().HasMaxLength(16);
            builder.Property(sample => sample.CpuPercent).HasPrecision(9, 2);
            builder.Ignore(sample => sample.DomainEvents);
            builder.HasIndex(sample => new { sample.ServerId, sample.CollectedAt });
            builder.HasIndex(sample => sample.CollectedAt);
            builder.HasOne<Server>().WithMany().HasForeignKey(sample => sample.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServerMonitoringSettings>(builder =>
        {
            builder.ToTable("server_monitoring_settings");
            builder.HasKey(settings => settings.Id);
            builder.Property(settings => settings.Id).HasColumnName("ServerId").HasServerIdConversion()
                .ValueGeneratedNever();
            builder.Ignore(settings => settings.ServerId);
            builder.Ignore(settings => settings.DomainEvents);
            builder.OwnsOne(settings => settings.DockerCleanup, policy =>
            {
                policy.Property(item => item.ScheduleEnabled).HasColumnName("docker_cleanup_schedule_enabled");
                policy.Property(item => item.CronExpression).HasColumnName("docker_cleanup_cron").HasMaxLength(120)
                    .IsRequired();
                policy.Property(item => item.ThresholdPercent).HasColumnName("docker_cleanup_threshold_percent");
                policy.Property(item => item.ForceScheduledCleanup).HasColumnName("docker_cleanup_force_scheduled");
                policy.Property(item => item.DeleteUnusedVolumes).HasColumnName("docker_cleanup_delete_unused_volumes");
                policy.Property(item => item.DeleteUnusedNetworks)
                    .HasColumnName("docker_cleanup_delete_unused_networks");
            });
            builder.Navigation(settings => settings.DockerCleanup).IsRequired();
            builder.HasOne<Server>().WithOne().HasForeignKey<ServerMonitoringSettings>(settings => settings.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ResourceRuntimeStatus>(builder =>
        {
            builder.ToTable("resource_runtime_statuses");
            builder.HasKey(status => status.Id);
            builder.Property(status => status.Id).HasConversion(ResourceRuntimeStatusIdConverter).ValueGeneratedNever();
            builder.Property(status => status.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(status => status.TeamId).HasTeamIdConversion();
            builder.Property(status => status.ServerId).HasServerIdConversion();
            builder.Property(status => status.State).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(status => status.Health).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Ignore(status => status.IsUp);
            builder.Ignore(status => status.DomainEvents);
            builder.HasIndex(status => new { status.Kind, status.ResourceId }).IsUnique();
            builder.HasIndex(status => status.ResourceId);
            builder.HasIndex(status => status.ServerId);
            builder.HasOne<Server>().WithMany().HasForeignKey(status => status.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DockerCleanupExecution>(builder =>
        {
            builder.ToTable("docker_cleanup_executions");
            builder.HasKey(execution => execution.Id);
            builder.Property(execution => execution.Id).HasConversion(DockerCleanupExecutionIdConverter)
                .ValueGeneratedNever();
            builder.Property(execution => execution.ServerId).HasServerIdConversion();
            builder.Property(execution => execution.RequestedByUserId).HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new UserId(value.Value) : null);
            builder.Property(execution => execution.Trigger).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(execution => execution.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(execution => execution.Log).HasMaxLength(DockerCleanupExecution.MaxLogLength).IsRequired();
            builder.Property(execution => execution.Message).HasMaxLength(DockerCleanupExecution.MaxMessageLength);
            builder.Property(execution => execution.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(execution => execution.DomainEvents);
            builder.HasIndex(execution => new { execution.ServerId, execution.StartedAt });
            builder.HasIndex(execution => execution.Status);
            builder.HasOne<Server>().WithMany().HasForeignKey(execution => execution.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
