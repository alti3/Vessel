using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Domain;

/// <summary>Domain rules for DATA-03/04/06/07/08/11.</summary>
public sealed class BackupParityDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("abc", true)]
    [InlineData("My.Bucket-01", true)]
    [InlineData("ab", false)]
    [InlineData("a..b", false)]
    [InlineData("a.-b", false)]
    [InlineData("a-.b", false)]
    [InlineData("-abc", false)]
    [InlineData("abc-", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("under_score", false)]
    public void BucketName_FollowsUpstreamRules(string bucket, bool valid)
    {
        Assert.Equal(valid, BackupBucketName.IsValid(bucket));
    }

    [Fact]
    public void BucketName_RejectsMoreThan63Characters()
    {
        Assert.False(BackupBucketName.IsValid(new string('a', 64)));
        Assert.True(BackupBucketName.IsValid(new string('a', 63)));
    }

    [Theory]
    [InlineData("https://user:pass@s3.example.com")]
    [InlineData("ftp://s3.example.com")]
    [InlineData("https://s3.example.com/?x=1")]
    [InlineData("not a url")]
    public void Destination_RejectsUnsafeEndpoints(string endpoint)
    {
        Assert.Throws<DomainException>(() => CreateDestination(endpoint));
    }

    [Fact]
    public void Destination_NormalizesEndpointAndKeepsPathEndpoints()
    {
        Assert.Equal("https://project.supabase.co/storage/v1/s3",
            CreateDestination("https://project.supabase.co/storage/v1/s3/").Endpoint);
    }

    [Fact]
    public void Destination_NotifiesUnusableOnceUntilItValidatesAgain()
    {
        BackupDestination destination = CreateDestination("https://s3.example.com");

        Assert.True(destination.MarkUnusable("403", Now));
        destination.RecordUnusableNotified(Now);
        Assert.False(destination.MarkUnusable("403 again", Now));
        destination.MarkUsable(Now);
        Assert.Null(destination.UnusableNotifiedAt);
        Assert.True(destination.MarkUnusable("down", Now));
    }

    [Fact]
    public void Destination_ConnectionChangesInvalidatePreviousValidation()
    {
        BackupDestination destination = CreateDestination("https://s3.example.com");
        destination.MarkUsable(Now);

        destination.Configure(destination.Name, null, "https://s3.example.com", null, "other-bucket", true, false,
            Now);

        Assert.False(destination.IsUsable);
        Assert.Null(destination.LastValidatedAt);
    }

    [Fact]
    public void DatabaseSelection_RoundTripsAndValidatesNames()
    {
        Assert.Equal(BackupDatabaseSelectionMode.All, BackupDatabaseSelection.FromStorage("*").Mode);
        Assert.Equal(BackupDatabaseSelectionMode.Default, BackupDatabaseSelection.FromStorage("").Mode);
        BackupDatabaseSelection named = BackupDatabaseSelection.Named(["app", "shop", "app"]);
        Assert.Equal(["app", "shop"], BackupDatabaseSelection.FromStorage(named.ToStorage()).Names);
        Assert.Throws<DomainException>(() => BackupDatabaseSelection.Named(["--all"]));
        Assert.Throws<DomainException>(() => BackupDatabaseSelection.Named(["bad name"]));
        Assert.Throws<DomainException>(() => BackupDatabaseSelection.Named([]));
    }

    [Fact]
    public void Retention_CombinesCountDaysAndSize()
    {
        var items = Enumerable.Range(0, 5)
            .Select(index => (CreatedAt: Now.AddDays(-index), Size: 10L))
            .ToArray();

        Assert.Equal(2, new BackupRetentionPolicy(3, 0, 0).SelectExpired(items, i => i.CreatedAt, i => i.Size, Now).Count);
        Assert.Equal(2, new BackupRetentionPolicy(0, 2, 0).SelectExpired(items, i => i.CreatedAt, i => i.Size, Now).Count);
        Assert.Equal(3, new BackupRetentionPolicy(0, 0, 25).SelectExpired(items, i => i.CreatedAt, i => i.Size, Now).Count);
        Assert.Empty(new BackupRetentionPolicy(0, 0, 0).SelectExpired(items, i => i.CreatedAt, i => i.Size, Now));
        Assert.Throws<DomainException>(() => new BackupRetentionPolicy(-1, 0, 0));
    }

    [Fact]
    public void Schedule_ThrottlesFailureNotificationsAndReportsRecoveryOnce()
    {
        BackupSchedule schedule = CreateSchedule();

        Assert.True(schedule.RecordFailure(Now));
        Assert.False(schedule.RecordFailure(Now.AddHours(5)));
        Assert.True(schedule.RecordFailure(Now.AddDays(1)));
        Assert.Equal(Now, schedule.FailingSince);
        Assert.True(schedule.RecordSuccess(Now.AddDays(1).AddHours(1)));
        Assert.False(schedule.RecordSuccess(Now.AddDays(2)));
        Assert.True(schedule.RecordFailure(Now.AddDays(3)));
    }

    [Fact]
    public void Schedule_MissingBackupAlertsOncePerIdlePeriod()
    {
        BackupSchedule schedule = CreateSchedule();
        schedule.Configure(schedule.Name, schedule.CronExpression, BackupDatabaseSelection.All,
            BackupDumpFormat.Native, schedule.LocalRetention, schedule.RemoteRetention, [], false,
            schedule.TimeoutSeconds, 2, Now);

        Assert.False(schedule.IsBackupMissing(Now.AddDays(1)));
        Assert.True(schedule.IsBackupMissing(Now.AddDays(2)));
        schedule.RecordMissingAlertSent(Now.AddDays(2));
        Assert.False(schedule.IsBackupMissing(Now.AddDays(5)));
        schedule.RecordSuccess(Now.AddDays(6));
        Assert.False(schedule.IsBackupMissing(Now.AddDays(7)));
        Assert.True(schedule.IsBackupMissing(Now.AddDays(8)));
        schedule.Disable(Now.AddDays(8));
        Assert.False(schedule.IsBackupMissing(Now.AddDays(9)));
    }

    [Fact]
    public void Schedule_RemoteOnlyRequiresADestinationAndDetachRestoresLocalCopy()
    {
        BackupSchedule schedule = CreateSchedule();
        var destination = BackupDestinationId.New();

        Assert.Throws<DomainException>(() => schedule.Configure(schedule.Name, schedule.CronExpression,
            BackupDatabaseSelection.All, BackupDumpFormat.Native, schedule.LocalRetention, schedule.RemoteRetention,
            [], true, schedule.TimeoutSeconds, 0, Now));
        schedule.Configure(schedule.Name, schedule.CronExpression, BackupDatabaseSelection.All,
            BackupDumpFormat.Native, schedule.LocalRetention, schedule.RemoteRetention, [destination], true,
            schedule.TimeoutSeconds, 0, Now);
        Assert.Equal(BackupStorageKind.ObjectStorage, schedule.StorageKind);

        schedule.DetachDestination(destination, Now);

        Assert.Empty(schedule.DestinationIds);
        Assert.False(schedule.DisableLocalCopy);
        Assert.Equal(BackupStorageKind.Local, schedule.StorageKind);
    }

    [Fact]
    public void Execution_SkipIsTerminalAndNotAFailure()
    {
        var execution = BackupExecution.Queue(TeamId.New(), DatabaseResourceId.New(), null, BackupStorageKind.Local,
            Now);

        execution.Skip("busy", Now);

        Assert.Equal(BackupExecutionStatus.Skipped, execution.Status);
        Assert.Throws<DomainException>(() => execution.Start(Now));
        Assert.Throws<DomainException>(() => execution.Fail("late", Now));
    }

    [Fact]
    public void Execution_RemoteOnlyCopyIsRestorableAndPrunedWhenLastCopyGoes()
    {
        var destination = BackupDestinationId.New();
        var execution = BackupExecution.Queue(TeamId.New(), DatabaseResourceId.New(), null, BackupStorageKind.Local,
            Now, "app");
        execution.Start(Now);
        execution.RecordArtifact("database/x/1.pgdump", 10, "ab", "postgresql-custom", Now);
        Assert.Throws<DomainException>(() => execution.Complete(null, Now));
        execution.RecordReplica(destination, "bucket", true, null, Now);
        execution.Complete(null, Now);

        Assert.True(execution.HasRestorableArtifact);
        Assert.False(execution.HasLocalCopy);
        execution.MarkReplicaDeleted(destination, Now);
        Assert.Equal(BackupExecutionStatus.Pruned, execution.Status);
        Assert.False(execution.HasRestorableArtifact);
    }

    [Fact]
    public void Execution_RejectsUnsafeDatabaseNames()
    {
        Assert.Throws<DomainException>(() => BackupExecution.Queue(TeamId.New(), DatabaseResourceId.New(), null,
            BackupStorageKind.Local, Now, "-oops"));
    }

    [Theory]
    [InlineData("postgres:18-alpine", "postgres", "18-alpine")]
    [InlineData("valkey/valkey:9-alpine", "valkey/valkey", "9-alpine")]
    [InlineData("docker.dragonflydb.io/dragonflydb/dragonfly:v1.34.1", "docker.dragonflydb.io/dragonflydb/dragonfly", "v1.34.1")]
    [InlineData("registry.local:5000/team/postgres:16", "registry.local:5000/team/postgres", "16")]
    public void DatabaseImage_ParsesPinnedReferences(string reference, string repository, string tag)
    {
        DatabaseImage image = DatabaseImage.Parse(reference);

        Assert.Equal(repository, image.Repository);
        Assert.Equal(tag, image.Tag);
        Assert.Equal(reference, image.Value);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("postgres:latest")]
    [InlineData("registry.local:5000/postgres")]
    [InlineData("Postgres:16")]
    [InlineData("postgres:16; rm -rf /")]
    public void DatabaseImage_RejectsFloatingOrInvalidReferences(string reference)
    {
        Assert.Throws<DomainException>(() => DatabaseImage.Parse(reference));
    }

    [Fact]
    public void DatabaseImage_AcceptsDigestPins()
    {
        var digest = "sha256:" + new string('a', 64);
        DatabaseImage image = DatabaseImage.Parse("postgres@" + digest);

        Assert.Null(image.Tag);
        Assert.Equal(digest, image.Digest);
    }

    [Fact]
    public void Database_StartsInStartingHealthAndOnlyRunningDatabasesTakeProbes()
    {
        DatabaseResource database = DatabaseResource.Create(EnvironmentId.New(), ServerId.New(),
            new ResourceName("main"), DatabaseEngine.PostgreSql, new VersionLabel("18-alpine"),
            new StorageConfiguration("data", "/var/lib/postgresql"), SecretReferenceId.New(), Now);

        Assert.False(database.RecordHealthProbe(DatabaseHealthState.Healthy, Now));
        database.MarkProvisioning(Now);
        database.MarkRunning("container", "docker-compose.yml", Now);
        Assert.Equal(DatabaseHealthState.Starting, database.HealthState);
        Assert.True(database.RecordHealthProbe(DatabaseHealthState.Healthy, Now));
        Assert.Equal(DatabaseHealthState.Healthy, database.HealthState);

        database.ChangeHealthCheck(new DatabaseHealthCheckPolicy(false, 15, 5, 5, 5), Now);
        database.MarkRestarting(Now);
        database.MarkRunning("container", "docker-compose.yml", Now);
        Assert.Equal(DatabaseHealthState.Unknown, database.HealthState);
    }

    [Fact]
    public void HealthCheckPolicy_DefaultsMatchUpstreamAndValidatesRanges()
    {
        Assert.Equal(new DatabaseHealthCheckPolicy(true, 15, 5, 5, 5), DatabaseHealthCheckPolicy.Default);
        Assert.Throws<DomainException>(() => new DatabaseHealthCheckPolicy(true, 0, 5, 5, 5));
        Assert.Throws<DomainException>(() => new DatabaseHealthCheckPolicy(true, 15, 5, 0, 5));
    }

    private static BackupDestination CreateDestination(string endpoint)
    {
        return BackupDestination.Create(TeamId.New(), new ResourceName("offsite"), endpoint, null, "bucket", true,
            false, SecretReferenceId.New(), Now);
    }

    private static BackupSchedule CreateSchedule()
    {
        return BackupSchedule.Create(TeamId.New(), DatabaseResourceId.New(), new ResourceName("daily"), "0 2 * * *",
            7, BackupStorageKind.Local, Now);
    }
}
