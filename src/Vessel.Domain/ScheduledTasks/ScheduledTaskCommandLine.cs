using System.Text;
using Vessel.Domain.Common;

namespace Vessel.Domain.ScheduledTasks;

/// <summary>
///     Splits a command into arguments the way a POSIX shell splits words, without any expansion (no variables, globs,
///     pipes or redirects), for tasks that run without a shell (upstream <c>ScheduledTask::splitCommandArguments</c>).
///     Single quotes keep everything literally; inside double quotes a backslash escapes <c>"</c>, <c>\</c>, <c>$</c> and
///     <c>`</c>; outside quotes a backslash escapes the next character and a backslash-newline joins lines.
/// </summary>
public static class ScheduledTaskCommandLine
{
    public static IReadOnlyList<string> Split(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var arguments = new List<string>();
        StringBuilder? word = null;
        char? quote = null;
        for (var i = 0; i < command.Length; i++)
        {
            var character = command[i];
            char? next = i + 1 < command.Length ? command[i + 1] : null;
            if (quote == '\'')
            {
                if (character == '\'') quote = null;
                else word!.Append(character);
                continue;
            }

            if (quote == '"')
            {
                if (character == '"')
                {
                    quote = null;
                }
                else if (character == '\\' && next is '"' or '\\' or '$' or '`')
                {
                    word!.Append(next.Value);
                    i++;
                }
                else
                {
                    word!.Append(character);
                }

                continue;
            }

            if (character == '\\' && next == '\n')
            {
                i++;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (word is not null)
                {
                    arguments.Add(word.ToString());
                    word = null;
                }

                continue;
            }

            word ??= new StringBuilder();
            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '\\' && next is not null)
            {
                word.Append(next.Value);
                i++;
            }
            else
            {
                word.Append(character);
            }
        }

        if (quote is not null) throw new DomainException("The command has a quote that is not closed.");
        if (word is not null) arguments.Add(word.ToString());
        return arguments;
    }
}
