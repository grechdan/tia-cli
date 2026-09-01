using System;
using System.Linq;
using TiaCli.Cli;
using TiaCli.Daemon;
using TiaCli.Openness;
using TiaCli.Protocol;

namespace TiaCli
{
    /// <summary>
    /// Entry point and routing. Nothing in this file may reference a type that transitively holds a
    /// Siemens.Engineering type in a field: the JIT resolves a method's types when the method is
    /// entered, so that would fault before OpennessResolver has had a chance to run. Everything that
    /// does is reached through ExecutorFactory, which installs the resolver first.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var output = new Output();

            try
            {
                if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "help")
                {
                    Help.Print(output);
                    return 0;
                }

                if (args[0] == "--version")
                {
                    Console.WriteLine("tia " + Help.Version);
                    return 0;
                }

                // The daemon is this same exe, re-invoked detached.
                if (args[0] == "--serve") return Serve(args);

                var cmd = CommandLine.Parse(args);
                output.AsJson = cmd.Has("json");
                output.Quiet = cmd.Has("quiet");

                return Run(cmd, output);
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
                return ErrorTranslator.ExitCodeFor(ex.Code);
            }
            catch (OpennessSetupException ex)
            {
                output.Error(new WireError { Code = WireErrorCodes.Internal, Message = ex.Message });
                return 1;
            }
            catch (Exception ex)
            {
                var error = ErrorTranslator.Describe(ex);
                output.Error(error);
                return ErrorTranslator.ExitCodeFor(error.Code);
            }
        }

        private static int Run(CommandLine cmd, Output output)
        {
            // These two manage the daemon itself and must not go through it.
            if (cmd.Verb == "session start") return Session.Start(cmd, output);
            if (cmd.Verb == "session stop") return Session.Stop(cmd, output);

            using (var executor = CreateExecutor(cmd, output))
            {
                if (cmd.Verb == "session status") return Session.Status(cmd, executor, output);
                if (cmd.Verb == "shell") return Shell.Run(executor, output);
                return Commands.Execute(cmd, executor, output);
            }
        }

        /// <summary>
        /// Picks who does the work. A running session wins, because it already holds a portal - and
        /// possibly an open project that cost minutes to load.
        /// </summary>
        private static IExecutor CreateExecutor(CommandLine cmd, Output output)
        {
            if (!cmd.Has("no-daemon") && DaemonClient.IsAvailable())
            {
                var ignored = new[] { "attach", "start", "new", "headless", "openness-version" }
                    .Where(cmd.Has)
                    .ToList();

                if (ignored.Count > 0)
                {
                    output.Warn("Ignoring " + string.Join(", ", ignored.Select(f => "--" + f)) +
                                ": the running session decides that. Add --no-daemon to bypass it.");
                }

                return new DaemonExecutor();
            }

            // Enumerating processes needs no approval, so warning about a dialog that will not appear
            // is worse than staying quiet - and it teaches people to ignore the warning that matters.
            // Confirmed by observation: an enumeration that hung was parked behind a dialog raised by
            // something else, and it completed with this build still absent from the whitelist.
            var enumeratesOnly = cmd.Verb == "portals" || cmd.Verb == "session status";

            if (!enumeratesOnly && !OpennessWhitelist.IsApproved(DaemonPaths.ExecutablePath))
                output.Warn(OpennessWhitelist.PendingApprovalNotice);

            var options = new AcquireOptions
            {
                AttachToProcessId = cmd.NullableInt("attach"),
                AllowCreate = cmd.Has("start") || cmd.Has("new"),
                ForceCreate = cmd.Has("new"),
                WithUserInterface = !cmd.Has("headless"),
            };

            return ExecutorFactory.CreateDirect(options, cmd.Value("openness-version"));
        }

        private static int Serve(string[] args)
        {
            var cmd = CommandLine.Parse(args.Skip(1).ToArray());

            var options = new ServeOptions
            {
                Acquire = new AcquireOptions
                {
                    AttachToProcessId = cmd.NullableInt("attach"),
                    // A session exists to hold a portal, so it may always make one.
                    AllowCreate = true,
                    ForceCreate = cmd.Has("new"),
                    WithUserInterface = !cmd.Has("headless"),
                },
                IdleMinutes = cmd.Int("idle", 0),
            };

            try
            {
                return ExecutorFactory.Serve(options, cmd.Value("openness-version"));
            }
            catch (Exception ex)
            {
                // Nobody is watching this process's console; the log is the only way this is seen.
                DaemonPaths.Log("daemon failed to start: " + ex);
                DaemonState.Delete();
                return 2;
            }
        }
    }
}
