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
        ProcessResult result = await _runner.RunTextAsync(Shell("echo vessel-phase-5"));

        Assert.True(result.Succeeded);
        Assert.Contains("vessel-phase-5", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, result.ExitInfo.ExitCode);
    }

    [Fact]
    public async Task RunTextAsync_RedactsExplicitSecretsAndTokenPatterns()
    {
        ProcessResult result = await _runner.RunTextAsync(Shell(
            "echo https://user:password@example.com && echo token=super-secret",
            new ProcessRedactionProfile(["super-secret"], [])));

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
                           outputMode: ProcessOutputMode.Lines)))
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
            outputMode: ProcessOutputMode.Binary));

        Assert.True(result.Succeeded);
        Assert.True(result.StandardOutput.Length > 0);
    }

    [Fact]
    public async Task RunTextAsync_ReportsTimeout()
    {
        ProcessResult result = await _runner.RunTextAsync(Shell(
            OperatingSystem.IsWindows()
                ? "ping -n 6 127.0.0.1 >nul"
                : "sleep 5",
            timeout: TimeSpan.FromMilliseconds(200)));

        Assert.True(result.ExitInfo.TimedOut);
        Assert.True(result.ExitInfo.Canceled);
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
