using Microsoft.EntityFrameworkCore;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Operations;
using Vessel.Domain.Users;

namespace Vessel.Infrastructure.Persistence;

/// <summary>
///     Control-plane operations model (OPS-01/OPS-02/DATA-13): dumps of Vessel's own database and upgrade status
///     records. Kept in its own part of the context so the additions stay reviewable.
/// </summary>
public sealed partial class VesselDbContext
{
    private IRepository<ControlPlaneBackup, ControlPlaneBackupId>? _controlPlaneBackupRepository;
    private IRepository<UpgradeRun, UpgradeRunId>? _upgradeRunRepository;

    public DbSet<ControlPlaneBackup> ControlPlaneBackupSet => Set<ControlPlaneBackup>();

    public DbSet<UpgradeRun> UpgradeRunSet => Set<UpgradeRun>();

    public IQueryable<ControlPlaneBackup> ControlPlaneBackups => ControlPlaneBackupSet;

    public IQueryable<UpgradeRun> UpgradeRuns => UpgradeRunSet;

    public IRepository<ControlPlaneBackup, ControlPlaneBackupId> ControlPlaneBackupRepository =>
        _controlPlaneBackupRepository ??= new EfRepository<ControlPlaneBackup, ControlPlaneBackupId>(this);

    public IRepository<UpgradeRun, UpgradeRunId> UpgradeRunRepository =>
        _upgradeRunRepository ??= new EfRepository<UpgradeRun, UpgradeRunId>(this);

    static partial void ConfigureOperations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ControlPlaneBackup>(builder =>
        {
            builder.ToTable("control_plane_backups");
            builder.HasKey(backup => backup.Id);
            builder.Property(backup => backup.Id)
                .HasConversion(id => id.Value, value => new ControlPlaneBackupId(value))
                .ValueGeneratedNever();
            builder.Property(backup => backup.Trigger).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(backup => backup.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(backup => backup.RelativePath).HasMaxLength(512);
            builder.Property(backup => backup.ChecksumSha256).HasMaxLength(64);
            builder.Property(backup => backup.FailureReason).HasMaxLength(ControlPlaneBackup.MaxFailureReasonLength);
            builder.Property(backup => backup.RequestedByUserId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new UserId(value.Value) : null);
            builder.Property(backup => backup.WorkerHost).HasMaxLength(128).IsRequired();
            builder.Property(backup => backup.StartedAt).IsRequired();
            builder.Property(backup => backup.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(backup => backup.DomainEvents);
            builder.HasIndex(backup => new { backup.Trigger, backup.StartedAt });
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(backup => backup.RequestedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UpgradeRun>(builder =>
        {
            builder.ToTable("upgrade_runs");
            builder.HasKey(run => run.Id);
            builder.Property(run => run.Id)
                .HasConversion(id => id.Value, value => new UpgradeRunId(value))
                .ValueGeneratedNever();
            builder.Property(run => run.FromVersion).HasMaxLength(128).IsRequired();
            builder.Property(run => run.TargetVersion).HasMaxLength(128).IsRequired();
            builder.Property(run => run.Channel).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(run => run.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(run => run.Message).HasMaxLength(UpgradeRun.MaxMessageLength).IsRequired();
            builder.Property(run => run.StartedByUserId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new UserId(value.Value) : null);
            builder.Property(run => run.WorkerHost).HasMaxLength(128).IsRequired();
            builder.Property(run => run.PreUpdateBackupId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new ControlPlaneBackupId(value.Value) : null);
            builder.Property(run => run.HeartbeatAt).IsRequired();
            builder.Property(run => run.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(run => run.IsTerminal);
            builder.Ignore(run => run.DomainEvents);
            builder.HasIndex(run => new { run.Status, run.CreatedAt });
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(run => run.StartedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne<ControlPlaneBackup>()
                .WithMany()
                .HasForeignKey(run => run.PreUpdateBackupId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
