using Vessel.Domain.Common;

namespace Vessel.Domain.Terminals;

public sealed class TerminalSession : Entity<TerminalSessionId>
{
    private TerminalSession()
    {
    }

    private TerminalSession(
        TerminalSessionId id,
        TeamId teamId,
        UserId ownerUserId,
        ServerId serverId,
        TerminalTargetType targetType,
        string? containerName,
        string command,
        int columns,
        int rows,
        DateTimeOffset startedAt)
        : base(id, startedAt)
    {
        TeamId = teamId;
        OwnerUserId = ownerUserId;
        ServerId = serverId;
        TargetType = targetType;
        ContainerName = containerName;
        Command = command;
        Columns = columns;
        Rows = rows;
        StartedAt = startedAt;
        LastActivityAt = startedAt;
        Status = TerminalSessionStatus.Opening;
    }

    public TeamId TeamId { get; private set; }

    public UserId OwnerUserId { get; private set; }

    public ServerId ServerId { get; private set; }

    public TerminalTargetType TargetType { get; private set; }

    public string? ContainerName { get; private set; }

    public string Command { get; private set; } = string.Empty;

    public TerminalSessionStatus Status { get; private set; }

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public DateTimeOffset LastActivityAt { get; private set; }

    public string? FailureReason { get; private set; }

    public static TerminalSession Open(
        TeamId teamId,
        UserId ownerUserId,
        ServerId serverId,
        TerminalTargetType targetType,
        string? containerName,
        string command,
        int columns,
        int rows,
        DateTimeOffset now)
    {
        if (targetType == TerminalTargetType.Container && string.IsNullOrWhiteSpace(containerName))
            throw new DomainException("Container terminal sessions require a container name.");
        if (columns is < 20 or > 300) throw new DomainException("Terminal columns must be between 20 and 300.");
        if (rows is < 5 or > 120) throw new DomainException("Terminal rows must be between 5 and 120.");

        string safeCommand = DomainValidation.Required(command, nameof(Command), 240);
        string? safeContainer = DomainValidation.Optional(containerName, nameof(ContainerName), 255);

        return new TerminalSession(
            TerminalSessionId.New(),
            teamId,
            ownerUserId,
            serverId,
            targetType,
            safeContainer,
            safeCommand,
            columns,
            rows,
            now);
    }

    public void MarkConnected(DateTimeOffset now)
    {
        if (IsTerminal(Status)) return;
        Status = TerminalSessionStatus.Connected;
        RecordActivity(now);
    }

    public void Resize(int columns, int rows, DateTimeOffset now)
    {
        if (IsTerminal(Status)) throw new DomainException("Cannot resize a terminal session that has ended.");
        if (columns is < 20 or > 300) throw new DomainException("Terminal columns must be between 20 and 300.");
        if (rows is < 5 or > 120) throw new DomainException("Terminal rows must be between 5 and 120.");
        Columns = columns;
        Rows = rows;
        RecordActivity(now);
    }

    public void RecordInput(DateTimeOffset now)
    {
        if (IsTerminal(Status)) throw new DomainException("Cannot write to a terminal session that has ended.");
        RecordActivity(now);
    }

    public void BeginClose(DateTimeOffset now)
    {
        if (IsTerminal(Status)) return;
        Status = TerminalSessionStatus.Closing;
        RecordActivity(now);
    }

    public void Close(DateTimeOffset now)
    {
        if (IsTerminal(Status)) return;
        Status = TerminalSessionStatus.Closed;
        EndedAt = now;
        RecordActivity(now);
    }

    public void Cancel(DateTimeOffset now)
    {
        if (IsTerminal(Status)) return;
        Status = TerminalSessionStatus.Canceled;
        EndedAt = now;
        RecordActivity(now);
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        if (IsTerminal(Status)) return;
        FailureReason = DomainValidation.Optional(reason, nameof(reason), 512);
        Status = TerminalSessionStatus.Failed;
        EndedAt = now;
        RecordActivity(now);
    }

    public static bool IsTerminal(TerminalSessionStatus status)
    {
        return status is TerminalSessionStatus.Closed or TerminalSessionStatus.Failed or TerminalSessionStatus.Canceled;
    }

    private void RecordActivity(DateTimeOffset now)
    {
        LastActivityAt = now;
        Touch(now);
    }
}
