using System.Diagnostics;
using Vessel.Application.Processes;
using Vessel.Application.Security;
using AppProcessOutputLine = Vessel.Application.Processes.ProcessOutputLine;

namespace Vessel.Infrastructure.Processes;

public sealed class DotNetProcessRunner(ISecretRedactor redactor, TimeProvider timeProvider) : IProcessRunner
{
    public async Task<ProcessResult> RunTextAsync(
        ProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateCommand(command, ProcessOutputMode.Text);
        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        using CancellationTokenSource timeout = CreateTimeoutToken(command, cancellationToken);
        var startInfo = CreateStartInfo(command, redirectOutput: true, redirectError: true);

        try
        {
            ProcessTextOutput output = await Process.RunAndCaptureTextAsync(startInfo, timeout.Token);
            DateTimeOffset exitedAt = timeProvider.GetUtcNow();
            return new ProcessResult(
                command,
                MapExitStatus(output.ExitStatus, timedOut: false),
                output.ProcessId,
                startedAt,
                exitedAt,
                Redact(output.StandardOutput, command),
                Redact(output.StandardError, command));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && command.Timeout is not null)
        {
            DateTimeOffset exitedAt = timeProvider.GetUtcNow();
            return new ProcessResult(
                command,
                new ProcessExitInfo(-1, Canceled: true, TimedOut: true, TerminatingSignal: null),
                0,
                startedAt,
                exitedAt,
                string.Empty,
                "Process timed out.");
        }
    }

    public async Task<ProcessExitInfo> RunAsync(
        ProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateCommand(command, ProcessOutputMode.None);
        using CancellationTokenSource timeout = CreateTimeoutToken(command, cancellationToken);
        var startInfo = CreateStartInfo(command, redirectOutput: false, redirectError: false);
        using var nullInput = File.OpenNullHandle();
        using var nullOutput = File.OpenNullHandle();
        using var nullError = File.OpenNullHandle();
        startInfo.StandardInputHandle = nullInput;
        startInfo.StandardOutputHandle = nullOutput;
        startInfo.StandardErrorHandle = nullError;

        try
        {
            ProcessExitStatus status = await Process.RunAsync(startInfo, timeout.Token);
            return MapExitStatus(status, timedOut: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && command.Timeout is not null)
        {
            return new ProcessExitInfo(-1, Canceled: true, TimedOut: true, TerminatingSignal: null);
        }
    }

    public async Task<ProcessBinaryResult> RunBinaryAsync(
        ProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateCommand(command, ProcessOutputMode.Binary);
        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        using CancellationTokenSource timeout = CreateTimeoutToken(command, cancellationToken);
        var startInfo = CreateStartInfo(command, redirectOutput: true, redirectError: true);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{command.FileName}'.");

        try
        {
            (byte[] stdout, byte[] stderr) = await process.ReadAllBytesAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            EnforceOutputLimit(stdout.LongLength + stderr.LongLength, command.MaxOutputBytes);

            DateTimeOffset exitedAt = timeProvider.GetUtcNow();
            return new ProcessBinaryResult(
                command,
                new ProcessExitInfo(process.ExitCode, Canceled: false, TimedOut: false, TerminatingSignal: null),
                process.Id,
                startedAt,
                exitedAt,
                redactor.RedactUtf8(stdout, CreateRedactionContext(command)),
                redactor.RedactUtf8(stderr, CreateRedactionContext(command)));
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process, command.TerminationPolicy ?? ProcessTerminationPolicy.Default);
            DateTimeOffset exitedAt = timeProvider.GetUtcNow();
            return new ProcessBinaryResult(
                command,
                new ProcessExitInfo(-1, Canceled: true, TimedOut: !cancellationToken.IsCancellationRequested, TerminatingSignal: null),
                process.Id,
                startedAt,
                exitedAt,
                ReadOnlyMemory<byte>.Empty,
                ReadOnlyMemory<byte>.Empty);
        }
    }

    public async IAsyncEnumerable<AppProcessOutputLine> StreamLinesAsync(
        ProcessCommand command,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateCommand(command, ProcessOutputMode.Lines);
        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        using CancellationTokenSource timeout = CreateTimeoutToken(command, cancellationToken);
        var startInfo = CreateStartInfo(command, redirectOutput: true, redirectError: true);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{command.FileName}'.");

        long sequence = 0;
        try
        {
            await foreach (System.Diagnostics.ProcessOutputLine line in process.ReadAllLinesAsync(timeout.Token))
            {
                yield return new AppProcessOutputLine(
                    Interlocked.Increment(ref sequence),
                    timeProvider.GetUtcNow(),
                    line.StandardError ? ProcessStreamKind.StandardError : ProcessStreamKind.StandardOutput,
                    Redact(line.Content, command));
            }

            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                DateTimeOffset exitedAt = timeProvider.GetUtcNow();
                throw new ProcessExecutionException(new ProcessResult(
                    command,
                    new ProcessExitInfo(process.ExitCode, Canceled: false, TimedOut: false, TerminatingSignal: null),
                    process.Id,
                    startedAt,
                    exitedAt,
                    string.Empty,
                    $"Process exited with code {process.ExitCode}."));
            }
        }
        finally
        {
            if (!process.HasExited)
                await TerminateAsync(process, command.TerminationPolicy ?? ProcessTerminationPolicy.Default);
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        ProcessCommand command,
        bool redirectOutput,
        bool redirectError)
    {
        var startInfo = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectError,
            CreateNoWindow = true,
            WorkingDirectory = command.WorkingDirectory ?? string.Empty,
            InheritedHandles = [],
            KillOnParentExit = (command.TerminationPolicy ?? ProcessTerminationPolicy.Default).KillOnParentExit
        };

        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string key, string? value) in command.Environment ?? new Dictionary<string, string?>())
        {
            startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    private static void ValidateCommand(ProcessCommand command, ProcessOutputMode expectedMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FileName);
        if (command.OutputMode != expectedMode)
            throw new InvalidOperationException($"Command output mode must be {expectedMode} for this runner method.");
        if (command.MaxOutputBytes <= 0)
            throw new InvalidOperationException("Command max output bytes must be positive.");
    }

    private static CancellationTokenSource CreateTimeoutToken(ProcessCommand command, CancellationToken cancellationToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (command.Timeout is not null) source.CancelAfter(command.Timeout.Value);
        return source;
    }

    private static ProcessExitInfo MapExitStatus(ProcessExitStatus status, bool timedOut)
    {
        string? signal = status.Signal?.ToString();
        return new ProcessExitInfo(status.ExitCode, status.Canceled, timedOut, signal);
    }

    private static void EnforceOutputLimit(long length, long max)
    {
        if (length > max)
            throw new InvalidOperationException($"Process output exceeded the configured limit of {max} bytes.");
    }

    private string Redact(string value, ProcessCommand command) =>
        redactor.Redact(value, CreateRedactionContext(command));

    private static RedactionContext CreateRedactionContext(ProcessCommand command) =>
        new(command.Redaction?.SecretValues, command.Redaction?.PatternNames);

    private static async Task TerminateAsync(Process process, ProcessTerminationPolicy policy)
    {
        if (process.HasExited) return;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                process.CloseMainWindow();
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            await process.WaitForExitAsync(new CancellationTokenSource(policy.GracePeriod).Token);
            return;
        }
        catch (OperationCanceledException)
        {
        }

        if (!process.HasExited)
            process.Kill(policy.KillProcessTree);
    }
}
