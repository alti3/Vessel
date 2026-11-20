namespace Vessel.Application.Processes;

public enum ProcessOutputMode
{
    None,
    Text,
    Binary,
    Lines,
    Stream
}

public enum ProcessStreamKind
{
    StandardOutput,
    StandardError
}

public sealed record ProcessCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null,
    ProcessOutputMode OutputMode = ProcessOutputMode.Text,
    ProcessTerminationPolicy? TerminationPolicy = null,
    ProcessRedactionProfile? Redaction = null,
    long MaxOutputBytes = 10 * 1024 * 1024,
    IReadOnlyDictionary<string, string>? AuditMetadata = null,
    bool InheritEnvironment = true)
{
    // InheritEnvironment = false starts the child with an empty environment plus only Environment, so daemon
    // secrets (master keys, connection strings) are never inherited. Required for interactive terminals.

    public static ProcessCommand Create(string fileName, params string[] arguments)
    {
        return new ProcessCommand(fileName, arguments);
    }
}

/// <summary>
/// Caller-owned streams for <see cref="IProcessRunner.RunStreamingAsync" />. Standard input is copied into the
/// process and then closed; standard output is copied into <see cref="StandardOutput" /> without buffering it in
/// memory. When <see cref="StandardOutput" /> is null, standard output is captured as bounded, redacted text.
/// Neither stream is disposed by the runner.
/// </summary>
public sealed record ProcessStreams(Stream? StandardInput = null, Stream? StandardOutput = null);

public sealed record ProcessTerminationPolicy(
    TimeSpan GracePeriod,
    bool KillProcessTree,
    bool KillOnParentExit)
{
    public static ProcessTerminationPolicy Default { get; } = new(TimeSpan.FromSeconds(5), true, true);
}

/// <summary>
///     How command output is redacted: <paramref name="SecretValues" /> are always hidden, <paramref name="KnownValues" />
///     (plain environment values) only when long enough to be matched safely. <paramref name="IsLogOutput" /> marks
///     output persisted to logs (ANSI/control characters stripped, emails hidden).
/// </summary>
public sealed record ProcessRedactionProfile(
    IReadOnlyList<string> SecretValues,
    IReadOnlyList<string> PatternNames,
    IReadOnlyList<string>? KnownValues = null,
    bool IsLogOutput = false)
{
    public static ProcessRedactionProfile Empty { get; } = new([], []);
}

public sealed record ProcessExitInfo(
    int ExitCode,
    bool Canceled,
    bool TimedOut,
    string? TerminatingSignal);

public sealed record ProcessOutputLine(
    long Sequence,
    DateTimeOffset Timestamp,
    ProcessStreamKind Stream,
    string Content);

public sealed record ProcessResult(
    ProcessCommand Command,
    ProcessExitInfo ExitInfo,
    int ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset ExitedAt,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => ExitInfo.ExitCode == 0 && !ExitInfo.Canceled && !ExitInfo.TimedOut;
}

public sealed record ProcessBinaryResult(
    ProcessCommand Command,
    ProcessExitInfo ExitInfo,
    int ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset ExitedAt,
    ReadOnlyMemory<byte> StandardOutput,
    ReadOnlyMemory<byte> StandardError)
{
    public bool Succeeded => ExitInfo.ExitCode == 0 && !ExitInfo.Canceled && !ExitInfo.TimedOut;
}

public sealed class ProcessExecutionException(ProcessResult result)
    : InvalidOperationException($"Process '{result.Command.FileName}' exited with code {result.ExitInfo.ExitCode}.")
{
    public ProcessResult Result { get; } = result;
}
