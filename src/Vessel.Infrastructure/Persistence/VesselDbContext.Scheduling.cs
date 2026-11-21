using Microsoft.EntityFrameworkCore;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.ScheduledTasks;
using Vessel.Domain.Scheduling;
using Vessel.Domain.Teams;

namespace Vessel.Infrastructure.Persistence;

/// <summary>
///     Scheduled job ledger (<c>scheduled_job_runs</c>, NTF-08) and user scheduled tasks (<c>scheduled_tasks</c>,
///     <c>scheduled_task_executions</c>, NTF-07). Kept in its own part of the context so the additions stay reviewable.
/// </summary>
public sealed partial class VesselDbContext
{
    private IRepository<ScheduledJobRun, ScheduledJobRunId>? _scheduledJobRunRepository;
    private IRepository<ScheduledTaskExecution, ScheduledTaskExecutionId>? _scheduledTaskExecutionRepository;
    private IRepository<ScheduledTask, ScheduledTaskId>? _scheduledTaskRepository;

    public DbSet<ScheduledJobRun> ScheduledJobRunSet => Set<ScheduledJobRun>();

    public DbSet<ScheduledTask> ScheduledTaskSet => Set<ScheduledTask>();

    public DbSet<ScheduledTaskExecution> ScheduledTaskExecutionSet => Set<ScheduledTaskExecution>();

    public IQueryable<ScheduledJobRun> ScheduledJobRuns => ScheduledJobRunSet;

    public IQueryable<ScheduledTask> ScheduledTasks => ScheduledTaskSet;

    public IQueryable<ScheduledTaskExecution> ScheduledTaskExecutions => ScheduledTaskExecutionSet;

    public IRepository<ScheduledJobRun, ScheduledJobRunId> ScheduledJobRunRepository =>
        _scheduledJobRunRepository ??= new EfRepository<ScheduledJobRun, ScheduledJobRunId>(this);

    public IRepository<ScheduledTask, ScheduledTaskId> ScheduledTaskRepository =>
        _scheduledTaskRepository ??= new EfRepository<ScheduledTask, ScheduledTaskId>(this);

    public IRepository<ScheduledTaskExecution, ScheduledTaskExecutionId> ScheduledTaskExecutionRepository =>
        _scheduledTaskExecutionRepository ??= new EfRepository<ScheduledTaskExecution, ScheduledTaskExecutionId>(this);

    static partial void ConfigureScheduling(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BackupSchedule>(builder =>
        {
            builder.Property(schedule => schedule.TimeZone).HasMaxLength(64);
            builder.Property(schedule => schedule.MissedRunPolicy).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<ScheduledJobRun>(builder =>
        {
            builder.ToTable("scheduled_job_runs");
            builder.HasKey(run => run.Id);
            builder.Property(run => run.Id)
                .HasConversion(id => id.Value, value => new ScheduledJobRunId(value))
                .ValueGeneratedNever();
            builder.Property(run => run.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(run => run.ScheduleId).IsRequired();
            builder.Property(run => run.TeamId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new TeamId(value.Value) : null);
            builder.Property(run => run.ScheduledFor).IsRequired();
            builder.Property(run => run.Trigger).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(run => run.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(run => run.TimeoutSeconds).IsRequired();
            builder.Property(run => run.EnqueueCount).HasDefaultValue(0).IsRequired();
            builder.Property(run => run.WorkerHost).HasMaxLength(ScheduledJobRun.MaxWorkerHostLength);
            builder.Property(run => run.Message).HasMaxLength(ScheduledJobRun.MaxMessageLength);
            builder.Property(run => run.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(run => run.IsTerminal);
            builder.Ignore(run => run.DomainEvents);
            // One scheduled occurrence per schedule and instant, whatever the number of dispatchers (NTF-08).
            builder.HasIndex(run => new { run.Kind, run.ScheduleId, run.ScheduledFor })
                .IsUnique()
                .HasFilter("\"Trigger\" = 'Scheduled'")
                .HasDatabaseName("ux_scheduled_job_runs_occurrence");
            builder.HasIndex(run => new { run.Status, run.EnqueuedAt });
            builder.HasIndex(run => new { run.Status, run.WorkerHost });
            builder.HasIndex(run => run.TeamId);
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(run => run.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScheduledTask>(builder =>
        {
            builder.ToTable("scheduled_tasks");
            builder.HasKey(task => task.Id);
            builder.Property(task => task.Id)
                .HasConversion(id => id.Value, value => new ScheduledTaskId(value))
                .ValueGeneratedNever();
            builder.Property(task => task.TeamId).HasConversion(id => id.Value, value => new TeamId(value))
                .IsRequired();
            builder.Property(task => task.OwnerKind).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(task => task.OwnerId).IsRequired();
            builder.Property(task => task.Name).HasMaxLength(ScheduledTask.MaxNameLength).IsRequired();
            builder.Property(task => task.Command).HasMaxLength(ScheduledTask.MaxCommandLength).IsRequired();
            builder.Property(task => task.CronExpression).HasMaxLength(Domain.ValueObjects.CronSchedule.MaxLength)
                .IsRequired();
            builder.Property(task => task.TimeZone).HasMaxLength(64);
            builder.Property(task => task.ContainerName).HasMaxLength(ScheduledTask.MaxContainerNameLength);
            builder.Property(task => task.TimeoutSeconds).IsRequired();
            builder.Property(task => task.UseShell).HasDefaultValue(true).IsRequired();
            builder.Property(task => task.Enabled).IsRequired();
            builder.Property(task => task.MissedRunPolicy).HasConversion<string>().HasMaxLength(16);
            builder.Property(task => task.ConsecutiveFailures).HasDefaultValue(0).IsRequired();
            builder.Property(task => task.DisabledReason).HasMaxLength(ScheduledTask.MaxDisabledReasonLength);
            builder.Property(task => task.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(task => task.DomainEvents);
            builder.HasIndex(task => new { task.OwnerKind, task.OwnerId });
            builder.HasIndex(task => new { task.TeamId, task.Enabled });
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(task => task.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScheduledTaskExecution>(builder =>
        {
            builder.ToTable("scheduled_task_executions");
            builder.HasKey(execution => execution.Id);
            builder.Property(execution => execution.Id)
                .HasConversion(id => id.Value, value => new ScheduledTaskExecutionId(value))
                .ValueGeneratedNever();
            builder.Property(execution => execution.ScheduledTaskId)
                .HasConversion(id => id.Value, value => new ScheduledTaskId(value))
                .IsRequired();
            builder.Property(execution => execution.TeamId)
                .HasConversion(id => id.Value, value => new TeamId(value))
                .IsRequired();
            builder.Property(execution => execution.RunId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new ScheduledJobRunId(value.Value) : null);
            builder.Property(execution => execution.Trigger).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(execution => execution.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(execution => execution.ContainerName).HasMaxLength(ScheduledTask.MaxContainerNameLength);
            builder.Property(execution => execution.Output).HasMaxLength(
                ScheduledTaskExecution.MaxOutputLength + ScheduledTaskExecution.TruncationNotice.Length).IsRequired();
            builder.Property(execution => execution.Message).HasMaxLength(ScheduledTaskExecution.MaxMessageLength);
            builder.Property(execution => execution.WorkerHost).HasMaxLength(ScheduledJobRun.MaxWorkerHostLength);
            builder.Property(execution => execution.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(execution => execution.DomainEvents);
            builder.HasIndex(execution => new { execution.ScheduledTaskId, execution.StartedAt });
            builder.HasIndex(execution => execution.RunId);
            builder.HasOne<ScheduledTask>()
                .WithMany()
                .HasForeignKey(execution => execution.ScheduledTaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
