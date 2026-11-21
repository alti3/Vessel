using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vessel.Application.Operations.ControlPlaneBackups;
using Vessel.Application.Processes;
using Vessel.Infrastructure.Configuration;
using Vessel.Infrastructure.Files;
using Vessel.Infrastructure.Operations;

namespace Vessel.IntegrationTests.Operations;

/// <summary>DATA-13: pg_dump of Vessel's own database through IProcessRunner; the password never reaches argv or logs.</summary>
public sealed class PgDumpControlPlaneDatabaseDumperTests : IDisposable
{
    private const string Password = "s3cr3t-db-password";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vessel-cp-" + Guid.NewGuid().ToString("N"));

    private readonly RecordingRunner _runner = new();

    private CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private PgDumpControlPlaneDatabaseDumper Dumper()
    {
        return new PgDumpControlPlaneDatabaseDumper(_runner, new PathSafetyService(),
            Options.Create(new ControlPlaneBackupOptions { Directory = _root }),
            Options.Create(new DatabaseOptions
            {
                Enabled = true,
                ConnectionString = $"Host=db.internal;Port=5433;Database=vessel;Username=vessel;Password={Password}"
            }),
            NullLogger<PgDumpControlPlaneDatabaseDumper>.Instance);
    }

    [Fact]
    public async Task Dump_UsesStructuredArgumentsAndPassesThePasswordOnlyThroughTheEnvironment()
    {
        ControlPlaneDumpResult result = await Dumper().DumpAsync(new ControlPlaneDumpRequest("manual/a.dump"), Token);

        ProcessCommand command = Assert.Single(_runner.Commands);
        Assert.Equal("pg_dump", command.FileName);
        Assert.Contains("--format=custom", command.Arguments);
        Assert.Contains("--host=db.internal", command.Arguments);
        Assert.Contains("--port=5433", command.Arguments);
        Assert.Contains("--dbname=vessel", command.Arguments);
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains(Password, StringComparison.Ordinal));
        Assert.Equal(Password, command.Environment!["PGPASSWORD"]);
        Assert.False(command.InheritEnvironment);
        Assert.Contains(Password, command.Redaction!.SecretValues);

        var dump = Path.Combine(_root, "manual", "a.dump");
        Assert.True(File.Exists(dump));
        Assert.False(File.Exists(dump + ".partial"));
        Assert.Equal($"{result.Sha256}  a.dump\n", await File.ReadAllTextAsync(dump + ".sha256", Token));
        Assert.Equal(64, result.Sha256.Length);
    }

    [Theory]
    [InlineData("../escape.dump")]
    [InlineData("/etc/passwd")]
    public async Task Dump_RejectsPathsOutsideTheOwnedDirectory(string relativePath)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Dumper().DumpAsync(new ControlPlaneDumpRequest(relativePath),
            Token));
        Assert.Empty(_runner.Commands);
    }

    [Fact]
    public async Task FailedDump_ReportsTheExitCodeAndLeavesNoFile()
    {
        _runner.ExitCode = 1;

        ControlPlaneDumpException failure = await Assert.ThrowsAsync<ControlPlaneDumpException>(() =>
            Dumper().DumpAsync(new ControlPlaneDumpRequest("manual/b.dump"), Token));

        Assert.Contains("exited with code 1", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "manual", "b.dump")));
        Assert.False(File.Exists(Path.Combine(_root, "manual", "b.dump.partial")));
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessCommand> Commands { get; } = [];

        public int ExitCode { get; set; }

        public async Task<ProcessResult> RunTextAsync(ProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            var file = command.Arguments.Single(argument => argument.StartsWith("--file=", StringComparison.Ordinal))
                ["--file=".Length..];
            if (ExitCode == 0) await File.WriteAllTextAsync(file, "PGDMP", cancellationToken);
            return new ProcessResult(command, new ProcessExitInfo(ExitCode, false, false, null), 1,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, string.Empty,
                ExitCode == 0 ? string.Empty : "connection refused");
        }

        public Task<ProcessExitInfo> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ProcessBinaryResult> RunBinaryAsync(ProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ProcessResult> RunTextWithInputAsync(ProcessCommand command, Stream standardInput,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<ProcessOutputLine> StreamLinesAsync(ProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IInteractiveProcess> StartInteractiveAsync(ProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
