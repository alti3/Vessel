namespace Vessel.Domain.Terminals;

/// <summary>Interactive terminal mode: a new shell (<c>exec</c>) or the container's main process console (<c>attach</c>).</summary>
public enum TerminalMode
{
    Shell = 0,
    Attach = 1
}

/// <summary>The Vessel resource whose container a container terminal opens into.</summary>
public enum TerminalResourceKind
{
    Application = 0,
    Database = 1,
    Service = 2
}
