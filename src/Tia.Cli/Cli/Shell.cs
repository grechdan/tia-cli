using System;
using TiaCli.Protocol;

namespace TiaCli.Cli
{
    /// <summary>
    /// An interactive prompt over a single executor. Its value is the same as the daemon's, without
    /// the background process: one session is acquired at the first command and held until you leave,
    /// so a project stays open between commands.
    /// </summary>
    internal static class Shell
    {
        public static int Run(IExecutor executor, Output output)
        {
            output.Line(executor.Kind == "daemon"
                ? "tia shell - commands run against the session started with 'tia session start'."
                : "tia shell - one session is held open until you exit.");
            output.Detail("Type a command without the leading 'tia'. 'exit' to leave.");
            output.Line();

            while (true)
            {
                Console.Write("tia> ");
                var line = Console.ReadLine();

                // Ctrl+Z / Ctrl+D closes stdin and is a normal way to leave.
                if (line == null) { output.Line(); return 0; }

                line = line.Trim();
                if (line.Length == 0) continue;
                if (line == "exit" || line == "quit") return 0;
                if (line == "help" || line == "?") { Help.Print(output); continue; }

                try
                {
                    var cmd = CommandLine.Parse(CommandLine.Tokenize(line));
                    if (cmd.Verb == "shell") { output.Warn("Already in a shell."); continue; }
                    if (cmd.Verb == "session stop")
                    {
                        output.Warn("Leave the shell with 'exit'; 'session stop' would end the daemon.");
                        continue;
                    }

                    if (cmd.Verb == "session status") Session.Status(cmd, executor, output);
                    else Commands.Execute(cmd, executor, output);
                }
                catch (WireException ex)
                {
                    output.Error(new WireError
                    {
                        Code = ex.Code,
                        Message = ex.Message,
                        Detail = ex.Detail,
                        Hint = ex.Hint,
                    });
                }
                catch (Exception ex)
                {
                    // One bad command must not end the session the shell exists to hold open.
                    output.Error(ErrorTranslator.Describe(ex));
                }
            }
        }
    }
}
