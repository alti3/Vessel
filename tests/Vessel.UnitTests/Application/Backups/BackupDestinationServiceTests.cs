using Vessel.Application.Auditing;
using Vessel.Application.Backups;
using Vessel.Application.Notifications;
using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;

namespace Vessel.UnitTests.Application.Backups;

/// <summary>DATA-06: team-scoped S3 destinations, credential handling, validation state and permissions.</summary>
public sealed class BackupDestinationServiceTests
{
    private readonly BackupFixture _f = new();

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private static CreateBackupDestinationRequest Request(string bucket = "Vessel-Backups", bool skipTls = false) =>
        new("offsite", "https://s3.example.com/", bucket, "AKIA123", "super-secret-key", "eu-central-1", true,
            skipTls);

    [Fact]
    public async Task Create_StoresCredentialsInTheVaultValidatesAndNeverReturnsThem()
    {
        BackupDestinationService service = _f.Destinations();

        BackupDestinationSummary created = await service.CreateAsync(_f.Owner.Id, _f.Team.Id, Request(), Token);

        Assert.True(created.IsUsable);
        Assert.Equal("https://s3.example.com", created.Endpoint);
        Assert.Equal("Vessel-Backups", created.Bucket);
        Assert.DoesNotContain("super-secret-key", System.Text.Json.JsonSerializer.Serialize(created),
            StringComparison.Ordinal);
        Assert.Contains(_f.Vault.Secrets.Values, value => value.Contains("super-secret-key", StringComparison.Ordinal));
        BackupDestinationConnection connection = Assert.Single(_f.Storage.Connections);
        Assert.Equal("super-secret-key", connection.SecretAccessKey);
        Assert.DoesNotContain("super-secret-key", connection.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Member_CannotCreateDestinations_ButCanListThem()
    {
        BackupDestinationService service = _f.Destinations();
        await service.CreateAsync(_f.Owner.Id, _f.Team.Id, Request(), Token);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateAsync(_f.Member.Id, _f.Team.Id, Request(), Token));
        Assert.Single(service.List(_f.Member.Id, _f.Team.Id));
        Assert.Empty(service.List(_f.Outsider.Id, _f.OtherTeam.Id));
    }

    [Fact]
    public async Task OtherTeamsDestination_IsNotFound()
    {
        BackupDestination foreign = await _f.AddDestinationAsync("foreign", _f.OtherTeam);
        BackupDestinationService service = _f.Destinations();

        Assert.Throws<InvalidOperationException>(() => service.Get(_f.Owner.Id, _f.Team.Id, foreign.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(_f.Owner.Id, _f.Team.Id, foreign.Id, Token));
        Assert.Single(_f.Db.DestinationItems);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("bad..bucket")]
    [InlineData("bad.-bucket")]
    [InlineData("192.168.1.10")]
    [InlineData("-leading")]
    public async Task Create_RejectsInvalidBucketNamesBeforeStoringSecrets(string bucket)
    {
        BackupDestinationService service = _f.Destinations();

        await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateAsync(_f.Owner.Id, _f.Team.Id, Request(bucket), Token));
        Assert.Empty(_f.Vault.Secrets);
    }

    [Fact]
    public async Task Create_RejectsEndpointsTheOutboundPolicyBlocks()
    {
        _f.Policy.Deny = "the host resolves to a private address.";
        BackupDestinationService service = _f.Destinations();

        DomainException error = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateAsync(_f.Owner.Id, _f.Team.Id, Request(), Token));
        Assert.Contains("not allowed", error.Message, StringComparison.Ordinal);
        Assert.Empty(_f.Db.DestinationItems);
        Assert.Empty(_f.Vault.Secrets);
    }

    [Fact]
    public async Task FailedValidation_MarksUnusableAndNotifiesOnce()
    {
        _f.Storage.FailValidation = true;
        BackupDestinationService service = _f.Destinations();

        BackupDestinationSummary created = await service.CreateAsync(_f.Owner.Id, _f.Team.Id, Request(), Token);
        await service.RevalidateUnusableAsync(Token);

        Assert.False(created.IsUsable);
        Assert.Contains("403", created.LastValidationError, StringComparison.Ordinal);
        Assert.Single(_f.Notifications.Published,
            item => item.Notification.EventType == NotificationEventTypes.BackupDestinationUnusable);

        _f.Storage.FailValidation = false;
        Assert.Equal(1, await service.RevalidateUnusableAsync(Token));
        Assert.True(Assert.Single(_f.Db.DestinationItems).IsUsable);
    }

    [Fact]
    public async Task SkippingTlsVerification_IsAudited()
    {
        BackupDestinationService service = _f.Destinations();

        BackupDestinationSummary created =
            await service.CreateAsync(_f.Owner.Id, _f.Team.Id, Request(skipTls: true), Token);

        Assert.True(created.SkipTlsVerification);
        Assert.True(Assert.Single(_f.Storage.Connections).SkipTlsVerification);
        Assert.Contains(_f.Audit.Records,
            record => record.Action == AuditActions.BackupDestinationTlsVerificationChanged);
    }

    [Fact]
    public async Task Delete_DetachesSchedulesAndRestoresTheirLocalCopy()
    {
        BackupDestination destination = await _f.AddDestinationAsync();
        DatabaseBackupService backups = _f.Backups();
        BackupScheduleSummary schedule = await backups.CreateScheduleAsync(_f.Owner.Id, _f.Team.Id,
            new CreateBackupScheduleRequest(_f.Database.Id.Value, "remote", "0 2 * * *",
                DestinationIds: [destination.Id.Value], DisableLocalCopy: true), Token);

        await _f.Destinations().DeleteAsync(_f.Owner.Id, _f.Team.Id, destination.Id, Token);

        BackupSchedule updated = Assert.Single(_f.Db.ScheduleItems);
        Assert.Equal(schedule.Id, updated.Id.Value);
        Assert.Empty(updated.DestinationIds);
        Assert.False(updated.DisableLocalCopy);
        Assert.Empty(_f.Db.DestinationItems);
        Assert.DoesNotContain(_f.Db.SecretReferenceItems, item => item.Id == destination.CredentialsReferenceId);
    }
}
