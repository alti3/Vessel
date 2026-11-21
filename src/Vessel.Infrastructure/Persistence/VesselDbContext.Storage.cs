using Microsoft.EntityFrameworkCore;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Databases;
using Vessel.Domain.Storage;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;

namespace Vessel.Infrastructure.Persistence;

/// <summary>
///     Persistent storage model (DATA-12: volumes and file mounts), volume backups (DATA-09: schedules, executions,
///     replicas, restores) and database import uploads (DATA-10). Kept in its own part of the context so these changes
///     stay reviewable in one place.
/// </summary>
public sealed partial class VesselDbContext
{
    private IRepository<DatabaseImportUpload, DatabaseImportUploadId>? _databaseImportUploadRepository;
    private IRepository<FileMount, FileMountId>? _fileMountRepository;
    private IRepository<PersistentVolume, PersistentVolumeId>? _persistentVolumeRepository;
    private IRepository<ScheduledVolumeBackup, ScheduledVolumeBackupId>? _scheduledVolumeBackupRepository;
    private IRepository<VolumeBackupExecution, VolumeBackupExecutionId>? _volumeBackupExecutionRepository;
    private IRepository<VolumeBackupRestore, VolumeBackupRestoreId>? _volumeBackupRestoreRepository;

    public DbSet<PersistentVolume> PersistentVolumeSet => Set<PersistentVolume>();

    public DbSet<FileMount> FileMountSet => Set<FileMount>();

    public DbSet<ScheduledVolumeBackup> ScheduledVolumeBackupSet => Set<ScheduledVolumeBackup>();

    public DbSet<VolumeBackupExecution> VolumeBackupExecutionSet => Set<VolumeBackupExecution>();

    public DbSet<VolumeBackupRestore> VolumeBackupRestoreSet => Set<VolumeBackupRestore>();

    public DbSet<DatabaseImportUpload> DatabaseImportUploadSet => Set<DatabaseImportUpload>();

    public IQueryable<PersistentVolume> PersistentVolumes => PersistentVolumeSet;

    public IQueryable<FileMount> FileMounts => FileMountSet;

    public IQueryable<ScheduledVolumeBackup> ScheduledVolumeBackups => ScheduledVolumeBackupSet;

    public IQueryable<VolumeBackupExecution> VolumeBackupExecutions => VolumeBackupExecutionSet;

    public IQueryable<VolumeBackupRestore> VolumeBackupRestores => VolumeBackupRestoreSet;

    public IQueryable<DatabaseImportUpload> DatabaseImportUploads => DatabaseImportUploadSet;

    public IRepository<PersistentVolume, PersistentVolumeId> PersistentVolumeRepository =>
        _persistentVolumeRepository ??= new EfRepository<PersistentVolume, PersistentVolumeId>(this);

    public IRepository<FileMount, FileMountId> FileMountRepository =>
        _fileMountRepository ??= new EfRepository<FileMount, FileMountId>(this);

    public IRepository<ScheduledVolumeBackup, ScheduledVolumeBackupId> ScheduledVolumeBackupRepository =>
        _scheduledVolumeBackupRepository ??= new EfRepository<ScheduledVolumeBackup, ScheduledVolumeBackupId>(this);

    public IRepository<VolumeBackupExecution, VolumeBackupExecutionId> VolumeBackupExecutionRepository =>
        _volumeBackupExecutionRepository ??= new EfRepository<VolumeBackupExecution, VolumeBackupExecutionId>(this);

    public IRepository<VolumeBackupRestore, VolumeBackupRestoreId> VolumeBackupRestoreRepository =>
        _volumeBackupRestoreRepository ??= new EfRepository<VolumeBackupRestore, VolumeBackupRestoreId>(this);

    public IRepository<DatabaseImportUpload, DatabaseImportUploadId> DatabaseImportUploadRepository =>
        _databaseImportUploadRepository ??= new EfRepository<DatabaseImportUpload, DatabaseImportUploadId>(this);

    private static void ConfigurePersistentStorage(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PersistentVolume>(builder =>
        {
            builder.ToTable("persistent_volumes");
            builder.HasKey(volume => volume.Id);
            builder.Property(volume => volume.Id)
                .HasConversion(id => id.Value, value => new PersistentVolumeId(value));
            builder.Property(volume => volume.TeamId).HasTeamIdConversion();
            builder.Property(volume => volume.OwnerKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(volume => volume.Name).HasMaxLength(63).IsRequired();
            builder.Property(volume => volume.MountPath).HasMaxLength(StoragePaths.MaxMountPathLength).IsRequired();
            builder.Property(volume => volume.ServiceName).HasMaxLength(63);
            builder.Property(volume => volume.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(volume => volume.DomainEvents);
            builder.Ignore(volume => volume.RuntimeName);
            builder.HasIndex(volume => new { volume.OwnerKind, volume.OwnerId, volume.Name }).IsUnique();
            builder.HasIndex(volume => volume.TeamId);
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(volume => volume.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FileMount>(builder =>
        {
            builder.ToTable("file_mounts");
            builder.HasKey(mount => mount.Id);
            builder.Property(mount => mount.Id).HasConversion(id => id.Value, value => new FileMountId(value));
            builder.Property(mount => mount.TeamId).HasTeamIdConversion();
            builder.Property(mount => mount.OwnerKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(mount => mount.Path).HasMaxLength(StoragePaths.MaxRelativePathLength).IsRequired();
            builder.Property(mount => mount.MountPath).HasMaxLength(StoragePaths.MaxMountPathLength).IsRequired();
            builder.Property(mount => mount.ServiceName).HasMaxLength(63);
            // Encrypted envelope (key-ring protected); unbounded text, plaintext is capped at 5 MiB by the domain.
            builder.Property(mount => mount.ProtectedContent).HasColumnType("text");
            builder.Property(mount => mount.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(mount => mount.DomainEvents);
            builder.Ignore(mount => mount.HasContent);
            builder.HasIndex(mount => new { mount.OwnerKind, mount.OwnerId, mount.Path }).IsUnique();
            builder.HasIndex(mount => mount.TeamId);
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(mount => mount.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScheduledVolumeBackup>(builder =>
        {
            builder.ToTable("scheduled_volume_backups");
            builder.HasKey(schedule => schedule.Id);
            builder.Property(schedule => schedule.Id)
                .HasConversion(id => id.Value, value => new ScheduledVolumeBackupId(value));
            builder.Property(schedule => schedule.TeamId).HasTeamIdConversion();
            builder.Property(schedule => schedule.OwnerKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(schedule => schedule.TargetKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(schedule => schedule.CronExpression).HasMaxLength(120).IsRequired();
            builder.Property(schedule => schedule.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(schedule => schedule.DomainEvents);
            builder.Ignore(schedule => schedule.LocalRetention);
            builder.Ignore(schedule => schedule.RemoteRetention);
            builder.Ignore(schedule => schedule.DestinationIds);
            // One schedule per backupable target (upstream unique (backupable_type, backupable_id)).
            builder.HasIndex(schedule => new { schedule.TargetKind, schedule.TargetId }).IsUnique();
            builder.HasIndex(schedule => new { schedule.OwnerKind, schedule.OwnerId });
            builder.HasMany(schedule => schedule.Destinations)
                .WithOne()
                .HasForeignKey(destination => destination.ScheduledVolumeBackupId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Navigation(schedule => schedule.Destinations).UsePropertyAccessMode(PropertyAccessMode.Field)
                .AutoInclude();
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(schedule => schedule.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScheduledVolumeBackupDestination>(builder =>
        {
            builder.ToTable("scheduled_volume_backup_destinations");
            builder.HasKey(destination => new { destination.ScheduledVolumeBackupId, destination.BackupDestinationId });
            builder.Property(destination => destination.ScheduledVolumeBackupId)
                .HasConversion(id => id.Value, value => new ScheduledVolumeBackupId(value));
            builder.Property(destination => destination.BackupDestinationId).HasBackupDestinationIdConversion();
            builder.HasIndex(destination => destination.BackupDestinationId);
            builder.HasOne<BackupDestination>()
                .WithMany()
                .HasForeignKey(destination => destination.BackupDestinationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VolumeBackupExecution>(builder =>
        {
            builder.ToTable("volume_backup_executions");
            builder.HasKey(execution => execution.Id);
            builder.Property(execution => execution.Id)
                .HasConversion(id => id.Value, value => new VolumeBackupExecutionId(value));
            builder.Property(execution => execution.TeamId).HasTeamIdConversion();
            builder.Property(execution => execution.ScheduleId)
                .HasConversion(id => id.Value, value => new ScheduledVolumeBackupId(value));
            builder.Property(execution => execution.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(execution => execution.ArtifactBucket).HasMaxLength(160);
            builder.Property(execution => execution.ArtifactKey).HasMaxLength(512);
            builder.Property(execution => execution.ChecksumSha256).HasMaxLength(128);
            builder.Property(execution => execution.FailureReason).HasMaxLength(1000);
            builder.Property(execution => execution.Warning).HasMaxLength(1000);
            builder.Property(execution => execution.StoppedContainerIds)
                .HasMaxLength(VolumeBackupExecution.MaxContainerIdsLength);
            builder.Property(execution => execution.RecoveryError).HasMaxLength(1000);
            builder.Property(execution => execution.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(execution => execution.DomainEvents);
            builder.Ignore(execution => execution.HasLocalCopy);
            builder.Ignore(execution => execution.HasRestorableArtifact);
            builder.Ignore(execution => execution.IsActive);
            builder.Ignore(execution => execution.StoppedContainers);
            builder.OwnsMany(execution => execution.Replicas, replica =>
            {
                replica.ToTable("volume_backup_replicas");
                replica.WithOwner().HasForeignKey(item => item.VolumeBackupExecutionId);
                replica.HasKey(item => item.Id);
                replica.Property(item => item.Id).ValueGeneratedNever();
                replica.Property(item => item.VolumeBackupExecutionId)
                    .HasConversion(id => id.Value, value => new VolumeBackupExecutionId(value));
                replica.Property(item => item.BackupDestinationId)
                    .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                        value => value.HasValue ? new BackupDestinationId(value.Value) : null);
                replica.Property(item => item.Bucket).HasMaxLength(63).IsRequired();
                replica.Property(item => item.Message).HasMaxLength(BackupArtifactReplica.MaxMessageLength);
                replica.Ignore(item => item.IsAvailable);
                replica.HasIndex(item => item.BackupDestinationId);
                replica.HasOne<BackupDestination>()
                    .WithMany()
                    .HasForeignKey(item => item.BackupDestinationId)
                    .OnDelete(DeleteBehavior.SetNull);
            });
            builder.Navigation(execution => execution.Replicas).UsePropertyAccessMode(PropertyAccessMode.Field);
            builder.HasIndex(execution => new { execution.ScheduleId, execution.CreatedAt });
            // The recovery job scans unfinished executions and pending container restarts.
            builder.HasIndex(execution => new { execution.Status, execution.StopRecoveryPending });
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(execution => execution.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<ScheduledVolumeBackup>()
                .WithMany()
                .HasForeignKey(execution => execution.ScheduleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VolumeBackupRestore>(builder =>
        {
            builder.ToTable("volume_backup_restores");
            builder.HasKey(restore => restore.Id);
            builder.Property(restore => restore.Id)
                .HasConversion(id => id.Value, value => new VolumeBackupRestoreId(value));
            builder.Property(restore => restore.TeamId).HasTeamIdConversion();
            builder.Property(restore => restore.VolumeBackupExecutionId)
                .HasConversion(id => id.Value, value => new VolumeBackupExecutionId(value));
            builder.Property(restore => restore.RequestedByUserId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new UserId(value.Value) : null);
            builder.Property(restore => restore.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(restore => restore.Log).HasMaxLength(VolumeBackupRestore.MaxLogLength).IsRequired();
            builder.Property(restore => restore.FailureReason).HasMaxLength(1000);
            builder.Property(restore => restore.StoppedContainerIds)
                .HasMaxLength(VolumeBackupExecution.MaxContainerIdsLength);
            builder.Property(restore => restore.RecoveryError).HasMaxLength(1000);
            builder.Property(restore => restore.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(restore => restore.DomainEvents);
            builder.Ignore(restore => restore.IsActive);
            builder.Ignore(restore => restore.StoppedContainers);
            builder.HasIndex(restore => new { restore.VolumeBackupExecutionId, restore.Status });
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(restore => restore.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<VolumeBackupExecution>()
                .WithMany()
                .HasForeignKey(restore => restore.VolumeBackupExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(restore => restore.RequestedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DatabaseImportUpload>(builder =>
        {
            builder.ToTable("database_import_uploads");
            builder.HasKey(upload => upload.Id);
            builder.Property(upload => upload.Id)
                .HasConversion(id => id.Value, value => new DatabaseImportUploadId(value));
            builder.Property(upload => upload.TeamId).HasTeamIdConversion();
            builder.Property(upload => upload.DatabaseResourceId).HasDatabaseResourceIdConversion();
            builder.Property(upload => upload.CreatedByUserId).HasUserIdConversion();
            builder.Property(upload => upload.FileName).HasMaxLength(255).IsRequired();
            builder.Property(upload => upload.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(upload => upload.DomainEvents);
            builder.Ignore(upload => upload.IsComplete);
            builder.HasIndex(upload => upload.ExpiresAt);
            builder.HasIndex(upload => new { upload.DatabaseResourceId, upload.CreatedAt });
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(upload => upload.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<DatabaseResource>()
                .WithMany()
                .HasForeignKey(upload => upload.DatabaseResourceId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(upload => upload.CreatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
