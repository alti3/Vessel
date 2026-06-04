using System.Text;
using Vessel.Application.Processes;
using Vessel.Infrastructure.Processes;
using Vessel.Infrastructure.Security;

namespace Vessel.IntegrationTests.Processes;

public sealed class DotNetProcessRunnerTests
{
    private readonly DotNetProcessRunner _runner = new(new SecretRedactor(), TimeProvider.System);

    [Fact]
    public async Task RunTextAsync_CapturesOutputAndExitCode()
    {
        ProcessResult result = await _runner.RunTextAsync(
            Shell("echo vessel-phase-5"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Contains("vessel-phase-5", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, result.ExitInfo.ExitCode);
    }

    [Fact]
    public async Task RunTextAsync_RedactsExplicitSecretsAndTokenPatterns()
    {
        ProcessResult result = await _runner.RunTextAsync(Shell(
            "echo https://user:password@example.com && echo token=super-secret",
            new ProcessRedactionProfile(["super-secret"], [])), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("password", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("<REDACTED>", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamLinesAsync_StreamsStdoutAndStderrWithSequence()
    {
        List<ProcessOutputLine> lines = [];
        await foreach (ProcessOutputLine line in _runner.StreamLinesAsync(Shell(
                           OperatingSystem.IsWindows()
                               ? "echo out& echo err 1>&2"
                               : "echo out; echo err 1>&2",
                           outputMode: ProcessOutputMode.Lines), TestContext.Current.CancellationToken))
            lines.Add(line);

        Assert.Contains(lines,
            line => line.Stream == ProcessStreamKind.StandardOutput &&
                    line.Content.Contains("out", StringComparison.Ordinal));
        Assert.Contains(lines,
            line => line.Stream == ProcessStreamKind.StandardError &&
                    line.Content.Contains("err", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.True(line.Sequence > 0));
    }

    [Fact]
    public async Task RunBinaryAsync_CapturesBinaryOutput()
    {
        ProcessBinaryResult result = await _runner.RunBinaryAsync(Shell(
            "echo binary",
            outputMode: ProcessOutputMode.Binary), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.StandardOutput.Length > 0);
    }

    [Fact]
    public async Task RunBinaryAsync_DoesNotRedactStandardOutputBytes()
    {
        ProcessBinaryResult result = await _runner.RunBinaryAsync(Shell(
            OperatingSystem.IsWindows()
                ? "echo token=super-secret & echo token=super-secret 1>&2"
                : "echo token=super-secret; echo token=super-secret 1>&2",
            new ProcessRedactionProfile(["super-secret"], []),
            ProcessOutputMode.Binary), TestContext.Current.CancellationToken);

        string stdout = Encoding.UTF8.GetString(result.StandardOutput.Span);
        string stderr = Encoding.UTF8.GetString(result.StandardError.Span);

        Assert.Contains("token=super-secret", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", stderr, StringComparison.Ordinal);
        Assert.Contains("<REDACTED>", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTextWithInputAsync_PipesStandardInput()
    {
        await using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("restore-payload"));

        ProcessResult result = await _runner.RunTextWithInputAsync(
            Shell(OperatingSystem.IsWindows() ? "more" : "cat"),
            input,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Contains("restore-payload", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTextAsync_ReportsTimeout()
    {
        ProcessResult result = await _runner.RunTextAsync(Shell(
            OperatingSystem.IsWindows()
                ? "ping -n 6 127.0.0.1 >nul"
                : "sleep 5",
            timeout: TimeSpan.FromMilliseconds(200)), TestContext.Current.CancellationToken);

        Assert.True(result.ExitInfo.TimedOut);
        Assert.True(result.ExitInfo.Canceled);
    }

    [Fact]
    public async Task StartInteractiveAsync_KillsProcessWhenTimeoutExpires()
    {
        await using IInteractiveProcess process = await _runner.StartInteractiveAsync(Shell(
            OperatingSystem.IsWindows()
                ? "ping -n 6 127.0.0.1 >nul"
                : "sleep 5",
            outputMode: ProcessOutputMode.None,
            timeout: TimeSpan.FromMilliseconds(200)), TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            process.WaitForExitAsync(TestContext.Current.CancellationToken));

        for (int attempt = 0; attempt < 20 && !process.HasExited; attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        Assert.True(process.HasExited);
    }

    private static ProcessCommand Shell(
        string command,
        ProcessRedactionProfile? redaction = null,
        ProcessOutputMode outputMode = ProcessOutputMode.Text,
        TimeSpan? timeout = null)
    {
        return OperatingSystem.IsWindows()
            ? new ProcessCommand(
                "cmd.exe",
                ["/c", command],
                Timeout: timeout ?? TimeSpan.FromSeconds(10),
                OutputMode: outputMode,
                Redaction: redaction)
            : new ProcessCommand(
                "/bin/sh",
                ["-c", command],
                Timeout: timeout ?? TimeSpan.FromSeconds(10),
                OutputMode: outputMode,
                Redaction: redaction);
    }
}
