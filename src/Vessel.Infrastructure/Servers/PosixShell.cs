using System.Text;

namespace Vessel.Infrastructure.Servers;

/// <summary>
///     POSIX shell quoting for the single command string that OpenSSH hands to the remote login shell. Every argument
///     is single-quoted, so arguments are never interpreted by the remote shell.
/// </summary>
public static class PosixShell
{
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0'))
            throw new ArgumentException("Shell arguments must not contain NUL characters.", nameof(argument));
        return "'" + argument.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    public static string Join(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
            throw new ArgumentException("At least one argument is required.", nameof(arguments));
        var builder = new StringBuilder();
        for (var index = 0; index < arguments.Count; index++)
        {
            if (index > 0)
                builder.Append(' ');
            builder.Append(Quote(arguments[index]));
        }

        return builder.ToString();
    }
}
