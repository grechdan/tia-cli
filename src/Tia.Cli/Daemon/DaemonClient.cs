using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using TiaCli.Protocol;

namespace TiaCli.Daemon
{
    /// <summary>
    /// Talks to a running daemon over its named pipe. One connection per request: the exchange is
    /// strictly request/response, and a fresh connection per command means a CLI process that dies
    /// mid-command cannot leave the daemon's reader half-consumed.
    ///
    /// Nothing in this class touches Openness, so a command that the daemon can answer never loads
    /// Siemens.Engineering into the CLI process at all - which is why routed commands come back in
    /// well under a second.
    /// </summary>
    internal static class DaemonClient
    {
        /// <summary>Cheap liveness probe. Used to decide whether to route a command to the daemon.</summary>
        public static bool IsAvailable(int timeoutMs = 250)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", DaemonPaths.PipeName, PipeDirection.InOut))
                {
                    pipe.Connect(timeoutMs);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Sends one request and waits for its answer. There is deliberately no read timeout:
        /// opening a project takes minutes, and cutting it off would leave the daemon working on a
        /// request nobody is listening for.
        /// </summary>
        public static WireResponse Send(WireRequest request, int connectTimeoutMs = 3000)
        {
            using (var pipe = new NamedPipeClientStream(".", DaemonPaths.PipeName, PipeDirection.InOut))
            {
                try
                {
                    pipe.Connect(connectTimeoutMs);
                }
                catch (TimeoutException)
                {
                    throw new WireException(WireErrorCodes.DaemonError,
                        "The tia daemon did not answer its pipe.",
                        "It may be busy with a long operation such as opening a project. " +
                        "Run 'tia session status' to check, or 'tia session stop' to end it.");
                }
                catch (IOException ex)
                {
                    throw new WireException(WireErrorCodes.DaemonError,
                        "Could not connect to the tia daemon: " + ex.Message);
                }

                var encoding = new UTF8Encoding(false);
                var writer = new StreamWriter(pipe, encoding) { AutoFlush = true };
                var reader = new StreamReader(pipe, encoding);

                writer.WriteLine(JsonSerializer.Serialize(request, WireJson.Options));

                var line = reader.ReadLine();
                if (line == null)
                    throw new WireException(WireErrorCodes.DaemonError,
                        "The tia daemon closed the connection without answering.",
                        "See " + DaemonPaths.LogFile + " for what it was doing.");

                return JsonSerializer.Deserialize<WireResponse>(line, WireJson.Options);
            }
        }

        /// <summary>
        /// Launches a detached daemon and waits for its pipe to come up. The daemon opens the pipe
        /// before it touches TIA Portal, so this returns quickly even though the portal behind it may
        /// still be starting - waiting for the portal itself is the caller's next request.
        /// </summary>
        public static void Start(string[] serveArgs, int readyTimeoutMs = 30000)
        {
            var exe = DaemonPaths.ExecutablePath;
            var arguments = "--serve " + string.Join(" ", serveArgs);

            var startInfo = new ProcessStartInfo(exe, arguments)
            {
                // ShellExecute detaches the daemon from this console: it must outlive the command
                // that started it, and it must not scribble on the user's terminal.
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            };

            Process process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                throw new WireException(WireErrorCodes.DaemonError,
                    "Could not start the tia daemon: " + ex.Message);
            }

            var deadline = Environment.TickCount + readyTimeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (IsAvailable(200)) return;

                if (process != null && process.HasExited)
                {
                    throw new WireException(WireErrorCodes.DaemonError,
                        $"The tia daemon exited immediately (code {process.ExitCode}).",
                        LogTail());
                }

                Thread.Sleep(100);
            }

            throw new WireException(WireErrorCodes.DaemonError,
                "The tia daemon did not open its pipe in time.", LogTail());
        }

        /// <summary>The last few log lines, so a failed start explains itself without a second command.</summary>
        public static string LogTail(int lines = 6)
        {
            try
            {
                if (!File.Exists(DaemonPaths.LogFile)) return null;
                var all = File.ReadAllLines(DaemonPaths.LogFile);
                var start = Math.Max(0, all.Length - lines);
                return string.Join(Environment.NewLine, all, start, all.Length - start);
            }
            catch
            {
                return null;
            }
        }
    }
}
