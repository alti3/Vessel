using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using Vessel.Application.Processes;
using Vessel.Application.Security;
using AppProcessOutputLine = Vessel.Application.Processes.ProcessOutputLine;
using ProcessOutputLine = System.Diagnostics.ProcessOutputLine;

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
        ProcessStartInfo startInfo = CreateStartInfo(command, true, true);

        try
        {
            ProcessTextOutput output = await Process.RunAndCaptureTextAsync(startInfo, timeout.Token);
            DateTimeOffset exitedAt = timeProvider.GetUtcNow();
            return new ProcessResult(
                command,
                MapExitStatus(output.ExitStatus, false),
                output.ProcessId,
                startedAt,
                exitedAt,
                Redact(output.StandardOutput, command),
                Redact(output.StandardError, command));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                 command.Timeout is not null)
        {
            DateTimeOffset exitedAt = timeProvider.GetUtcNow();
            return new ProcessResult(
                command,
                new ProcessExitInfo(-1, true, true, null),
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
        ProcessStartInfo startInfo = CreateStartInfo(command, false, false);
        using SafeFileHandle nullInput = File.OpenNullHandle();
        using SafeFileHandle nullOutput = File.OpenNullHandle();
        using SafeFileHandle nullError = File.OpenNullHandle();
        startInfo.StandardInputHandle = nullInput;
        startInfo.StandardOutputHandle = nullOutput;
        startInfo.StandardErrorHandle = nullError;

        try
        {
            ProcessExitStatus status = await Process.RunAsync(startInfo, timeout.Token);
            return MapExitStatus(status, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                 command.Timeout is not null)
        {
            return new ProcessExitInfo(-1, true, true, null);
        }
    }

    public async Task<ProcessBinaryResult> RunBinaryAsync(
        ProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateCommand(command, ProcessOutputMode.Binary);
        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        using CancellationTokenSource timeout = CreateTimeoutToken(command, cancellationToken);
        ProcessStartInfo startInfo = CreateStartInfo(command, true, true);

        using Process process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException(
                                    $"Failed to start process '{command.FileName}'.");

        try
        {
            var (stdout, stderr) = await process.ReadAllBytesAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            EnforceOutputLimit(stdout.LongLength + stderr.LongLength, command.MaxOutputBytes);

            DateTimeOffset exitedAt = timeProvider.GetUtcNow();
            return new ProcessBinaryResult(
                command,
                new ProcessExitInfo(process.ExitCode, false, false, null),
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
                new ProcessExitInfo(-1, true, !cancellationToken.IsCancellationRequested, null),
                process.Id,
                startedAt,
                exitedAt,
                ReadOnlyMemory<byte>.Empty,
                ReadOnlyMemory<byte>.Empty);
        }
    }

    public async IAsyncEnumerable<AppProcessOutputLine> StreamLinesAsync(
        ProcessCommand command,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateCommand(command, ProcessOutputMode.Lines);
        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        using CancellationTokenSource timeout = CreateTimeoutToken(command, cancellationToken);
        ProcessStartInfo startInfo = CreateStartInfo(command, true, true);
        using Process process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException(
                                    $"Failed to start process '{command.FileName}'.");

        long sequence = 0;
        try
        {
            await foreach (ProcessOutputLine line in process.ReadAllLinesAsync(timeout.Token))
                yield return new AppProcessOutputLine(
                    Interlocked.Increment(ref sequence),
                    timeProvider.GetUtcNow(),
                    line.StandardError ? ProcessStreamKind.StandardError : ProcessStreamKind.StandardOutput,
                    Redact(line.Content, command));

            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                DateTimeOffset exitedAt = timeProvider.GetUtcNow();
                throw new ProcessExecutionException(new ProcessResult(
                    command,
                    new ProcessExitInfo(process.ExitCode, false, false, null),
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

        foreach (var argument in command.Arguments) startInfo.ArgumentList.Add(argument);

        foreach (var (key, value) in command.Environment ?? new Dictionary<string, string?>())
            startInfo.Environment[key] = value;

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

    private static CancellationTokenSource CreateTimeoutToken(ProcessCommand command,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (command.Timeout is not null) source.CancelAfter(command.Timeout.Value);
        return source;
    }

    private static ProcessExitInfo MapExitStatus(ProcessExitStatus status, bool timedOut)
    {
        var signal = status.Signal?.ToString();
        return new ProcessExitInfo(status.ExitCode, status.Canceled, timedOut, signal);
    }

    private static void EnforceOutputLimit(long length, long max)
    {
        if (length > max)
            throw new InvalidOperationException($"Process output exceeded the configured limit of {max} bytes.");
    }

    private string Redact(string value, ProcessCommand command)
    {
        return redactor.Redact(value, CreateRedactionContext(command));
    }

    private static RedactionContext CreateRedactionContext(ProcessCommand command)
    {
        return new RedactionContext(command.Redaction?.SecretValues, command.Redaction?.PatternNames);
    }

    private static async Task TerminateAsync(Process process, ProcessTerminationPolicy policy)
    {
        if (process.HasExited) return;

        if (OperatingSystem.IsWindows())
            try
            {
                process.CloseMainWindow();
            }
            catch (InvalidOperationException)
            {
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
