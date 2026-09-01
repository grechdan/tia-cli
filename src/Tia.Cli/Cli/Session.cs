using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaCli.Daemon;
using TiaCli.Protocol;

namespace TiaCli.Cli
{
    /// <summary>
    /// The session verbs. These are the ones that exist because a CLI process is short-lived and an
    /// Openness connection is not: a portal this tool starts belongs to the process that started it,
    /// so keeping one across commands means keeping a process around to hold it.
    /// </summary>
    internal static class Session
    {
        public static int Start(CommandLine cmd, Output output)
        {
            if (DaemonClient.IsAvailable())
            {
                output.Warn("A tia session is already running. Use 'tia session stop' first, " +
                            "or 'tia session status' to see it.");
                return Status(cmd, new DaemonExecutor(), output);
            }

            var serveArgs = new List<string>();
            var attach = cmd.NullableInt("attach");
            if (attach.HasValue) serveArgs.Add("--attach " + attach.Value);
            if (cmd.Has("new")) serveArgs.Add("--new");
            if (cmd.Has("headless")) serveArgs.Add("--headless");
            if (cmd.Has("idle")) serveArgs.Add("--idle " + cmd.Int("idle", 0));
            if (cmd.Has("openness-version")) serveArgs.Add("--openness-version " + cmd.Value("openness-version"));

            // Worth saying before the daemon goes into the background: the dialog it may block on
            // belongs to TIA Portal, and nothing in this terminal would explain the wait.
            if (!Openness.OpennessWhitelist.IsApproved(DaemonPaths.ExecutablePath))
                output.Warn(Openness.OpennessWhitelist.PendingApprovalNotice);

            DaemonClient.Start(serveArgs.ToArray());

            var executor = new DaemonExecutor();

            output.Detail(cmd.Has("new") || cmd.Has("headless")
                ? "Starting TIA Portal - the first launch takes a minute or two."
                : "Looking for a TIA Portal to join, and starting one if there is none.");

            try
            {
                Commands.Call(executor, "session.ensure");
            }
            catch
            {
                // A daemon with no session is a trap: the next command would fail the same way with
                // no explanation of where the stale process came from.
                try { Commands.Call(executor, "daemon.stop"); } catch { }
                throw;
            }

            var project = cmd.Value("project");
            if (!string.IsNullOrEmpty(project))
            {
                output.Detail("Opening " + project + " - this takes minutes, not seconds.");
                Commands.Call(executor, "project.open", new { path = Path.GetFullPath(project) });
            }

            return Status(cmd, executor, output);
        }

        public static int Stop(CommandLine cmd, Output output)
        {
            if (!DaemonClient.IsAvailable())
            {
                // A state file without a live pipe is what a killed daemon leaves behind.
                DaemonState.Delete();
                output.Line("No tia session is running.");
                return 0;
            }

            var executor = new DaemonExecutor();

            // Ask before stopping, so the message can say whether TIA Portal is about to close.
            SessionStateDto session = null;
            try
            {
                session = JsonUtil.To<DaemonStatusDto>(Commands.Call(executor, "daemon.status"))?.Session;
            }
            catch { }

            Commands.Call(executor, "daemon.stop");

            if (output.AsJson)
            {
                output.JsonValue(new { stopped = true });
                return 0;
            }

            if (session != null && session.Connected)
            {
                output.Line(session.OwnsPortal
                    ? $"Session stopped. TIA Portal (pid {session.ProcessId}) was started by this tool and is closing."
                    : $"Session stopped. TIA Portal (pid {session.ProcessId}) was already running and stays open.");
            }
            else
            {
                output.Line("Session stopped.");
            }

            return 0;
        }

        public static int Status(CommandLine cmd, IExecutor executor, Output output)
        {
            if (executor.Kind != "daemon")
            {
                // No daemon: the useful answer is what there is to attach to.
                if (output.AsJson)
                {
                    output.JsonValue(new DaemonStatusDto { Running = false, LogPath = DaemonPaths.LogFile });
                    return 0;
                }

                output.Line("No tia session is running (each command attaches on its own).");
                output.Line();
                output.Line("TIA Portal instances you can use:");
                return Commands.Execute(CommandLine.Parse(new[] { "portals" }), executor, output);
            }

            var result = Commands.Call(executor, "daemon.status");
            if (output.AsJson) { output.Json(result); return 0; }

            var status = JsonUtil.To<DaemonStatusDto>(result);
            var session = status.Session;

            if (status.Stopping)
            {
                // Nothing below would still be true by the time it was read.
                output.Line("Session stopping - it was asked to stop and is closing now.");
                return 0;
            }

            output.Line("Session running.");
            output.Pairs(new[]
            {
                Output.KV("daemon pid", status.ProcessId.ToString()),
                Output.KV("pipe", status.Pipe),
                Output.KV("started", status.StartedAt),
                Output.KV("log", status.LogPath),
            });

            output.Line();
            if (session == null || !session.Connected)
            {
                output.Line("No TIA Portal is attached yet - the next command will acquire one.");
                return 0;
            }

            output.Line("TIA Portal");
            output.Pairs(new[]
            {
                Output.KV("pid", session.ProcessId.ToString()),
                Output.KV("mode", session.WithUserInterface ? "with user interface" : "headless"),
                Output.KV("origin", session.Origin == "created"
                    ? "started by tia (closes on 'session stop')"
                    : "already running (left open on 'session stop')"),
                Output.KV("openness", session.OpennessVersion),
            });

            if (status.PortalExited)
            {
                output.Line();
                output.Warn("That process is gone - TIA Portal exited since the last command, and " +
                            "everything above describes the session as it was. Run 'tia session stop' " +
                            "and start a new one.");
                return 0;
            }

            if (session.Project != null)
            {
                output.Line();
                output.Line("Project");
                output.Pairs(new[]
                {
                    Output.KV("name", session.Project.Name),
                    Output.KV("path", session.Project.Path),
                    Output.KV("devices", session.Project.DeviceCount.ToString()),
                    Output.KV("unsaved changes", session.Project.IsModified ? "yes" : "no"),
                });
            }
            else
            {
                output.Line("No project open.");
            }

            return 0;
        }
    }
}
