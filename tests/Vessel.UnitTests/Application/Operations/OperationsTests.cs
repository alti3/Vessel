using System.Security.Cryptography;
using System.Text;
using Vessel.Application.Notifications;
using Vessel.Application.Operations;
using Vessel.Application.Operations.ControlPlaneBackups;
using Vessel.Application.Operations.Draining;
using Vessel.Application.Operations.Updates;
using Vessel.Application.Operations.Upgrades;
using Vessel.Domain.Operations;
using Vessel.UnitTests.Application.Backups;
using Vessel.UnitTests.Application.Scheduling;

namespace Vessel.UnitTests.Application.Operations;

/// <summary>OPS-01 / OPS-02 / DATA-13: update policy and manifest checks, control-plane dumps, upgrades, draining.</summary>
public sealed class OperationsTests
{
    private readonly SchedulingFixture _s = new();

    private BackupFixture F => _s.B;

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private FakeDumper Dumper { get; } = new();

    private FakeManifests Manifests { get; } = new();

    private FakeVersion Version { get; } = new();

    private PlatformNotificationPublisher Platform => new(F.Db, F.Authorization, F.Notifications);

    private UpdatePolicyService Policies => new(F.Db, _s.Store, F.Authorization, Version,
        new NotConfiguredManifestSignatureVerifier(), F.Audit);

    private UpdateCheckService Checks => new(Policies, F.Authorization, Manifests,
        new NotConfiguredManifestSignatureVerifier(), Version, Platform, F.Audit, F.Time);

    private ControlPlaneBackupService Backups => new(F.Db, _s.Store, F.Authorization, Dumper, F.Locks, F.Jobs,
        new PassthroughRedactorForOps(), Platform, new FixedWorkerHost(), F.Audit, F.Time);

    private DrainService Drain(params IInFlightWorkProbe[] probes)
    {
        return new DrainService(F.Db, _s.Store, F.Authorization, probes, new FixedWorkerHost(), F.Audit, F.Time);
    }

    private UpgradeCoordinator Upgrades(DrainService? drain = null)
    {
        return new UpgradeCoordinator(F.Db, F.Authorization, Backups, drain ?? Drain(), Policies, Version, F.Locks,
            new FixedWorkerHost(), Platform, F.Audit, F.Time);
    }

    [Fact]
    public async Task EveryEntryPoint_RequiresAnInstanceAdministrator()
    {
        var member = F.Member.Id;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Policies.GetAsync(member, Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Policies.UpdateAsync(member, new UpdateUpdatePolicyRequest(AutoUpdateEnabled: true), null, Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Checks.CheckAsync(member, null, Token));
        Assert.Throws<UnauthorizedAccessException>(() => Backups.List(member));
        Assert.Throws<UnauthorizedAccessException>(() => Backups.GetSettings(member));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Backups.RunAsync(member, null, Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Drain().StartAsync(member, null, null, Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Upgrades().BeginAsync(member, new BeginUpgradeRequest("1.2.0"), null, Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Upgrades().GetStatusAsync(member, Token));
        Assert.Empty(Dumper.Requests);
        Assert.Empty(F.Db.UpgradeRunItems);
    }

    [Fact]
    public async Task DefaultPolicy_IsOff_AndTheCheckScheduleIsJitteredAndStable()
    {
        UpdatePolicySummary first = await Policies.GetAsync(F.Owner.Id, Token);
        UpdatePolicySummary second = await Policies.GetAsync(F.Owner.Id, Token);

        Assert.False(first.AutoUpdateEnabled);
        Assert.Null(first.ManifestUrl);
        Assert.Equal(UpdateChannel.Stable, first.Channel);
        Assert.Equal(first.CheckCron, second.CheckCron);
        Assert.True(Vessel.Domain.ValueObjects.CronSchedule.IsValid(first.CheckCron));
        // No manifest URL: no outbound traffic and no scheduled check.
        UpdateCheckResult result = await Checks.CheckAsync(F.Owner.Id, null, Token);
        Assert.Equal(UpdateCheckStatus.NotConfigured, result.Status);
        Assert.Empty(Manifests.Fetched);
        Assert.Empty(new UpdateCheckScheduleSource(_s.Store, Checks).ListActiveSchedules());
        Assert.NotEqual("0 0 * * *", UpdatePolicyService.GenerateCheckCron());
    }

    [Fact]
    public void SwitchingToAMoreStableChannel_RemembersThePreviousChannel()
    {
        var nightly = new UpdatePolicy { Channel = UpdateChannel.Nightly };

        UpdatePolicy stable = UpdatePolicyService.SwitchChannel(nightly, UpdateChannel.Stable);
        Assert.Equal(UpdateChannel.Nightly, stable.PreviousChannel);
        UpdatePolicy preview = UpdatePolicyService.SwitchChannel(stable, UpdateChannel.Preview);
        Assert.Null(preview.PreviousChannel);
    }

    [Fact]
    public async Task UpdateCheck_VerifiesTheChecksumListAndNotifiesOncePerVersion()
    {
        Version.CurrentVersion = "1.0.0";
        await Policies.UpdateAsync(F.Owner.Id,
            new UpdateUpdatePolicyRequest(ManifestUrl: "https://releases.example.com/releases.json"), null, Token);
        Manifests.Respond("""{"schemaVersion":1,"channels":{"stable":{"version":"1.1.0"}}}""");

        UpdateCheckResult first = await Checks.CheckAsync(F.Owner.Id, null, Token);
        UpdateCheckResult second = await Checks.CheckAsync(F.Owner.Id, null, Token);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, first.Status);
        Assert.Equal("1.1.0", first.LatestVersion);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, second.Status);
        Assert.Single(F.Notifications.Published,
            item => item.Notification.EventType == NotificationEventTypes.PlatformUpdateAvailable);
        Assert.Single(new UpdateCheckScheduleSource(_s.Store, Checks).ListActiveSchedules());

        Manifests.Respond("""{"schemaVersion":1,"channels":{"stable":{"version":"9.9.9"}}}""", tamper: true);
        UpdateCheckResult tampered = await Checks.CheckAsync(F.Owner.Id, null, Token);
        Assert.Equal(UpdateCheckStatus.Failed, tampered.Status);
        Assert.Contains("does not match", tampered.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash  releases.json")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef releases.json")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  ../releases.json")]
    public void ChecksumList_RejectsMalformedInput(string list)
    {
        Assert.Throws<ManifestVerificationException>(() => ManifestChecksumVerifier.ParseChecksumList(list));
    }

    [Fact]
    public void ChecksumList_RequiresAMatchingEntryForTheFile()
    {
        var content = Encoding.UTF8.GetBytes("{}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));

        ManifestChecksumVerifier.Verify($"{hash}  releases.json\n", "releases.json", content);
        Assert.Throws<ManifestVerificationException>(() =>
            ManifestChecksumVerifier.Verify($"{hash}  other.json\n", "releases.json", content));
        Assert.Throws<ManifestVerificationException>(() =>
            ManifestChecksumVerifier.Verify($"{hash}  releases.json\n", "releases.json", "{ }"u8));
    }

    [Theory]
    [InlineData("1.2.0", "1.10.0", -1)]
    [InlineData("1.2.0-rc.1", "1.2.0", -1)]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.10", -1)]
    [InlineData("v1.2.0", "1.2.0", 0)]
    public void Versions_CompareSemantically(string left, string right, int expected)
    {
        Assert.True(VesselVersion.TryParse(left, out VesselVersion? a));
        Assert.True(VesselVersion.TryParse(right, out VesselVersion? b));
        Assert.Equal(expected, Math.Sign(a!.CompareTo(b)));
    }

    [Fact]
    public async Task ControlPlaneBackup_RecordsChecksumAndAppliesRetention()
    {
        await Backups.UpdateSettingsAsync(F.Owner.Id, new UpdateInstanceBackupSettingsRequest(RetentionCount: 2),
            null, Token);
        for (var i = 0; i < 3; i++)
        {
            F.Time.Advance(TimeSpan.FromMinutes(1));
            ControlPlaneBackupSummary summary = await Backups.RunAsync(F.Owner.Id, null, Token);
            Assert.Equal(ControlPlaneBackupStatus.Succeeded, summary.Status);
        }

        Assert.Equal(2, F.Db.ControlPlaneBackupItems.Count);
        Assert.Single(Dumper.Deleted);
        Assert.All(F.Db.ControlPlaneBackupItems, backup => Assert.Equal(64, backup.ChecksumSha256!.Length));
    }

    [Fact]
    public async Task FailedPreUpdateBackup_AbortsTheUpgradeAndNotifies()
    {
        Dumper.Fail = true;

        UpgradeRunSummary run = await Upgrades().BeginAsync(F.Owner.Id, new BeginUpgradeRequest("1.2.0"), null, Token);

        Assert.Equal(UpgradeRunStatus.Failed, run.Status);
        Assert.StartsWith(UpgradeCoordinator.BackupFailedMessage, run.Message, StringComparison.Ordinal);
        Assert.False(ControlPlaneDrainGate.Read(F.Db).Active);
        Assert.Contains(F.Notifications.Published,
            item => item.Notification.EventType == NotificationEventTypes.ControlPlaneBackupFailed);
        Assert.Contains(F.Notifications.Published,
            item => item.Notification.EventType == NotificationEventTypes.PlatformUpdateFailed);
    }

    [Fact]
    public async Task Upgrades_AreSingleFlight_AndStaleRunsAreRecovered()
    {
        UpgradeRunSummary first = await Upgrades().BeginAsync(F.Owner.Id, new BeginUpgradeRequest("1.2.0"), null,
            Token);
        Assert.Equal(UpgradeRunStatus.ReadyForSwap, first.Status);
        Assert.Single(Dumper.Requests);
        Assert.True(ControlPlaneDrainGate.Read(F.Db).Active);

        await Assert.ThrowsAsync<UpgradeInProgressException>(() =>
            Upgrades().BeginAsync(F.Owner.Id, new BeginUpgradeRequest("1.2.0"), null, Token));

        F.Time.Advance(UpgradeRun.StaleAfter + TimeSpan.FromMinutes(1));
        UpgradeRunSummary second = await Upgrades().BeginAsync(F.Owner.Id,
            new BeginUpgradeRequest("1.2.0", SkipBackup: true), null, Token);
        Assert.Equal(UpgradeRunStatus.ReadyForSwap, second.Status);
        Assert.Equal(UpgradeRunStatus.Failed, F.Db.UpgradeRunItems.Single(item => item.Id.Value == first.Id).Status);
        Assert.Single(Dumper.Requests);
    }

    [Fact]
    public async Task Draining_RejectsNewDeploymentsUntilReleased()
    {
        await Drain().StartAsync(F.Owner.Id, "maintenance", null, Token);

        Assert.Throws<ControlPlaneDrainingException>(() => ControlPlaneDrainGate.ThrowIfDraining(F.Db));

        await Drain().ReleaseAsync(F.Owner.Id, null, Token);
        ControlPlaneDrainGate.ThrowIfDraining(F.Db);
    }

    [Fact]
    public async Task UpgradeDrain_TimesOutWhileWorkIsRunningOnThisHost()
    {
        var busy = new BusyProbe(1);

        UpgradeRunSummary run = await Upgrades(Drain(busy)).BeginAsync(F.Owner.Id,
            new BeginUpgradeRequest("1.2.0", SkipBackup: true, DrainTimeoutSeconds: 0), null, Token);

        Assert.Equal(UpgradeRunStatus.Failed, run.Status);
        Assert.Equal("worker-1", busy.AskedHost);
        Assert.False(ControlPlaneDrainGate.Read(F.Db).Active);
    }

    private sealed class FakeDumper : IControlPlaneDatabaseDumper
    {
        public List<ControlPlaneDumpRequest> Requests { get; } = [];

        public List<string> Deleted { get; } = [];

        public bool Fail { get; set; }

        public Task<ControlPlaneDumpResult> DumpAsync(ControlPlaneDumpRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Fail) throw new ControlPlaneDumpException("pg_dump exited with code 1: connection refused");
            return Task.FromResult(new ControlPlaneDumpResult(request.RelativePath, 1024, new string('a', 64)));
        }

        public Stream OpenRead(string relativePath)
        {
            return new MemoryStream();
        }

        public void Delete(string relativePath)
        {
            Deleted.Add(relativePath);
        }
    }

    private sealed class FakeManifests : IVersionManifestClient
    {
        private VersionManifestDocument? _document;

        public List<Uri> Fetched { get; } = [];

        public void Respond(string json, bool tamper = false)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var hash = Convert.ToHexStringLower(SHA256.HashData(tamper ? "other"u8.ToArray() : bytes));
            _document = new VersionManifestDocument(new Uri("https://releases.example.com/releases.json"), bytes,
                $"{hash}  releases.json\n");
        }

        public Task<VersionManifestDocument> FetchAsync(Uri manifestUrl, Uri checksumsUrl,
            CancellationToken cancellationToken = default)
        {
            Fetched.Add(manifestUrl);
            return Task.FromResult(_document ?? throw new InvalidOperationException("no manifest"));
        }
    }

    private sealed class FakeVersion : IVesselVersionProvider
    {
        public string CurrentVersion { get; set; } = "1.0.0";
    }

    private sealed class BusyProbe(int count) : IInFlightWorkProbe
    {
        public string? AskedHost { get; private set; }

        public string Name => "busy";

        public Task<int> CountInFlightAsync(string workerHost, CancellationToken cancellationToken = default)
        {
            AskedHost = workerHost;
            return Task.FromResult(count);
        }
    }

    private sealed class PassthroughRedactorForOps : Vessel.Application.Security.ISecretRedactor
    {
        public string Redact(string value, Vessel.Application.Security.RedactionContext? context = null)
        {
            return value;
        }

        public byte[] RedactUtf8(byte[] value, Vessel.Application.Security.RedactionContext? context = null)
        {
            return value;
        }
    }
}
