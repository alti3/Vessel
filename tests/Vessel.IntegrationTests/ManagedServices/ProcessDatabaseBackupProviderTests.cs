using System.Runtime.CompilerServices;
using Vessel.Application.Processes;
using Vessel.Domain;
using Vessel.Domain.Databases;
using Vessel.Domain.ValueObjects;
using Vessel.Infrastructure.ManagedServices;
using Vessel.Infrastructure.Security;

namespace Vessel.IntegrationTests.ManagedServices;

public sealed class ProcessDatabaseBackupProviderTests
{
    [Fact]
    public async Task BackupAsync_UsesDumpCommandOutputAsArtifact()
    {
        var runner = new FakeProcessRunner();
        var provider = new ProcessDatabaseBackupProvider(runner, new SecretRedactor(), TimeProvider.System);
        DatabaseResource database = CreateRunningDatabase(DatabaseEngine.PostgreSql);

        var artifact = await provider.BackupAsync(database, "secret", TestContext.Current.CancellationToken);

        Assert.Equal("dump-bytes", await ReadAsync(artifact.Content));
        Assert.Contains("pg_dump", runner.BinaryCommand?.Arguments ?? []);
        Assert.DoesNotContain("--version", runner.BinaryCommand?.Arguments ?? []);
    }

    [Fact]
    public async Task RestoreAsync_PipesArtifactIntoRestoreCommand()
    {
        var runner = new FakeProcessRunner();
        var provider = new ProcessDatabaseBackupProvider(runner, new SecretRedactor(), TimeProvider.System);
        DatabaseResource database = CreateRunningDatabase(DatabaseEngine.MySql);
        await using var artifact = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("restore-sql"));

        await provider.RestoreAsync(database, "secret", artifact, false, TestContext.Current.CancellationToken);

        Assert.NotNull(runner.TextWithInputCommand);
        Assert.Contains("mysql", runner.TextWithInputCommand.Arguments);
        Assert.Equal("restore-sql", runner.StandardInput);
    }

    private static DatabaseResource CreateRunningDatabase(DatabaseEngine engine)
    {
        DateTimeOffset now = new(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        var database = DatabaseResource.Create(
            EnvironmentId.New(),
            ServerId.New(),
            new ResourceName("database"),
            engine,
            new VersionLabel("latest"),
            new StorageConfiguration("db-data", "/data"),
            SecretReferenceId.New(),
            now);
        database.MarkProvisioning(now);
        database.MarkRunning("vessel-db", "docker-compose.yml", now.AddSeconds(1));
        return database;
    }

    private static async Task<string> ReadAsync(Stream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public ProcessCommand? BinaryCommand { get; private set; }

        public ProcessCommand? TextWithInputCommand { get; private set; }

        public string? StandardInput { get; private set; }

        public Task<ProcessResult> RunTextAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(
                command,
                new ProcessExitInfo(0, false, false, null),
                123,
                now,
                now,
                "version",
                string.Empty));
        }

        public Task<ProcessExitInfo> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProcessExitInfo(0, false, false, null));
        }

        public Task<ProcessBinaryResult> RunBinaryAsync(
            ProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            BinaryCommand = command;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessBinaryResult(
                command,
                new ProcessExitInfo(0, false, false, null),
                123,
                now,
                now,
                System.Text.Encoding.UTF8.GetBytes("dump-bytes"),
                ReadOnlyMemory<byte>.Empty));
        }

        public async Task<ProcessResult> RunTextWithInputAsync(
            ProcessCommand command,
            Stream standardInput,
            CancellationToken cancellationToken = default)
        {
            TextWithInputCommand = command;
            using var reader = new StreamReader(standardInput);
            StandardInput = await reader.ReadToEndAsync(cancellationToken);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new ProcessResult(
                command,
                new ProcessExitInfo(0, false, false, null),
                123,
                now,
                now,
                "restored",
                string.Empty);
        }

        public async IAsyncEnumerable<ProcessOutputLine> StreamLinesAsync(
            ProcessCommand command,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
