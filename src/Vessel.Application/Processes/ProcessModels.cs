using System.Collections.ObjectModel;

namespace Vessel.Application.Processes;

public enum ProcessOutputMode
{
    None,
    Text,
    Binary,
    Lines
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
    IReadOnlyDictionary<string, string>? AuditMetadata = null)
{
    public static ProcessCommand Create(string fileName, params string[] arguments) => new(fileName, arguments);
}

public sealed record ProcessTerminationPolicy(
    TimeSpan GracePeriod,
    bool KillProcessTree,
    bool KillOnParentExit)
{
    public static ProcessTerminationPolicy Default { get; } = new(TimeSpan.FromSeconds(5), true, true);
}

public sealed record ProcessRedactionProfile(
    IReadOnlyList<string> SecretValues,
    IReadOnlyList<string> PatternNames)
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
