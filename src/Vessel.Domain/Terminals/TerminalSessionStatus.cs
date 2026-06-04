namespace Vessel.Domain.Terminals;

public enum TerminalSessionStatus
{
    Opening = 0,
    Connected = 1,
    Closing = 2,
    Closed = 3,
    Failed = 4,
    Canceled = 5
}
