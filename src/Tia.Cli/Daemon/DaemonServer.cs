using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using TiaCli.Openness;
using TiaCli.Protocol;

namespace TiaCli.Daemon
{
    internal sealed class ServeOptions
    {
        public AcquireOptions Acquire { get; set; } = new AcquireOptions();
        /// <summary>Shut down after this many idle minutes. Zero or less means never.</summary>
        public int IdleMinutes { get; set; }
    }

    /// <summary>
    /// The long-lived half of the CLI. It holds one <see cref="SessionHost"/> open so the TIA Portal
    /// connection - and, more importantly, the minutes spent opening a project - survive between
    /// commands. Each CLI invocation then costs a pipe round trip instead of a fresh attach.
    ///
    /// Daemon-level requests (ping, status, stop) are answered on the connection thread rather than
    /// queued behind Openness work, so 'tia session status' still answers while a project is opening.
    /// </summary>
    internal sealed class DaemonServer
    {
        private const int MaxInstances = 8;

        private readonly ServeOptions _options;
        private readonly SessionHost _host;
        private readonly DateTime _startedAt = DateTime.Now;

        private volatile bool _stopping;

        /// <summary>
        /// Set the moment a stop is accepted, which is a little before the process actually goes.
        /// Without it, a 'session status' racing a 'session stop' - the obvious thing to type next,
        /// and what a script does - gets a confident "running" from a daemon that is already leaving.
        /// </summary>
        private volatile bool _stopRequested;
        private int _inFlight;
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;

        public DaemonServer(ServeOptions options)
        {
            _options = options;
            _host = new SessionHost(options.Acquire);
        }

        public int Run()
        {
            DaemonPaths.TrimLog();

            if (DaemonClient.IsAvailable(400))
            {
                DaemonPaths.Log("refusing to start: a daemon is already listening on " + DaemonPaths.PipeName);
                return 3;
            }

            DaemonState.Write(new DaemonState
            {
                Pipe = DaemonPaths.PipeName,
                ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                StartedAt = _startedAt.ToString("yyyy-MM-dd'T'HH:mm:ss"),
            });

            DaemonPaths.Log($"daemon up on {DaemonPaths.PipeName} (pid {System.Diagnostics.Process.GetCurrentProcess().Id}, " +
                            $"openness {OpennessResolver.ResolvedVersion})");

            try
            {
                AcceptLoop();
            }
            catch (Exception ex)
            {
                DaemonPaths.Log("accept loop failed: " + ex);
                Shutdown("accept loop failure");
                return 1;
            }

            Shutdown("loop ended");
            return 0;
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                var server = CreateInstance();
                var pending = server.BeginWaitForConnection(null, null);

                while (!pending.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
                {
                    if (_stopping)
                    {
                        server.Dispose();
                        return;
                    }

                    if (IdleExpired())
                    {
                        server.Dispose();
                        Shutdown($"idle for {_options.IdleMinutes} minutes");
                        return;
                    }
                }

                server.EndWaitForConnection(pending);
                Touch();

                var accepted = server;
                ThreadPool.QueueUserWorkItem(_ => Handle(accepted));
            }
        }

        private static NamedPipeServerStream CreateInstance()
        {
            // Only the account that started the daemon may drive it: this pipe is a remote control
            // for an engineering tool that can change and download PLC projects.
            var security = new PipeSecurity();
            var self = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new PipeAccessRule(self,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

            return new NamedPipeServerStream(
                DaemonPaths.PipeName,
                PipeDirection.InOut,
                MaxInstances,
                PipeTransmissionMode.Byte,
                // Asynchronous is what makes BeginWaitForConnection - and therefore the idle timer
                // and a clean stop - possible at all.
                PipeOptions.Asynchronous,
                64 * 1024,
                64 * 1024,
                security);
        }

        private void Handle(NamedPipeServerStream pipe)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                var encoding = new UTF8Encoding(false);
                var reader = new StreamReader(pipe, encoding);
                var writer = new StreamWriter(pipe, encoding) { AutoFlush = true };

                var line = reader.ReadLine();
                if (line == null) return;

                WireResponse response;
                long id = 0;
                var stopRequested = false;

                try
                {
                    var request = JsonSerializer.Deserialize<WireRequest>(line, WireJson.Options);
                    id = request.Id;
                    stopRequested = request.Method == "daemon.stop";

                    var result = HandleMethod(request);
                    response = new WireResponse { Id = id, Ok = true, Result = JsonUtil.ToElement(result) };
                }
                catch (Exception ex)
                {
                    // Expected refusals stay one line. Anything else gets its whole chain: Openness
                    // hides the real reason several inner exceptions down, and a log that keeps only
                    // the outermost message cannot explain a crash afterwards. SessionException is
                    // matched by name, as in ErrorTranslator, to keep Openness types out of this file.
                    var expected = ex is WireException || ex.GetType().Name == "SessionException";
                    DaemonPaths.Log(expected
                        ? $"request failed: {ex.GetType().Name}: {ex.Message}"
                        : "request failed: " + ex);
                    response = new WireResponse { Id = id, Ok = false, Error = ErrorTranslator.Describe(ex) };
                }

                writer.WriteLine(JsonSerializer.Serialize(response, WireJson.Options));
                try { pipe.WaitForPipeDrain(); } catch { /* client may already be gone */ }

                if (stopRequested && response.Ok)
                {
                    // Answer first, exit second, so 'tia session stop' does not look like a failure.
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(200);
                        Shutdown("stop requested");
                    });
                }
            }
            catch (IOException)
            {
                // A client that walked away mid-exchange is normal; nothing to report.
            }
            catch (Exception ex)
            {
                DaemonPaths.Log("connection failed: " + ex);
            }
            finally
            {
                try { if (pipe.IsConnected) pipe.Disconnect(); } catch { }
                pipe.Dispose();
                Interlocked.Decrement(ref _inFlight);
                Touch();
            }
        }

        private object HandleMethod(WireRequest request)
        {
            switch (request.Method)
            {
                // Answered here rather than through the host, so they stay responsive while the
                // Openness thread is busy with something long.
                case "daemon.ping":
                    return new { ok = true };

                case "daemon.stop":
                    _stopRequested = true;
                    // Dropped now rather than in Shutdown, so nothing can find a state file
                    // describing a daemon that is on its way out.
                    DaemonState.Delete();
                    return new { ok = true, stopping = true };

                case "daemon.status":
                {
                    // The cached snapshot, not a live call: a live one would queue behind an
                    // in-progress project open and defeat the point of asking.
                    var session = _host.Snapshot;
                    return new DaemonStatusDto
                    {
                        Running = true,
                        Stopping = _stopRequested,
                        ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                        Pipe = DaemonPaths.PipeName,
                        StartedAt = _startedAt.ToString("yyyy-MM-dd'T'HH:mm:ss"),
                        LogPath = DaemonPaths.LogFile,
                        Session = session,
                        // Being cached, that snapshot would otherwise report a portal that has since
                        // died as healthy. Whether the pid still exists is a plain OS question, so
                        // asking it here cannot queue behind anything.
                        PortalExited = session != null && session.Connected && !ProcessAlive(session.ProcessId),
                    };
                }

                default:
                    return _host.Invoke(request);
            }
        }

        private static bool ProcessAlive(int pid)
        {
            if (pid <= 0) return true;
            try
            {
                using (System.Diagnostics.Process.GetProcessById(pid)) return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch
            {
                // Any other failure means we could not tell; do not cry wolf over it.
                return true;
            }
        }

        private bool IdleExpired()
        {
            if (_options.IdleMinutes <= 0) return false;
            if (Interlocked.CompareExchange(ref _inFlight, 0, 0) > 0) return false;

            var last = new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
            return DateTime.UtcNow - last > TimeSpan.FromMinutes(_options.IdleMinutes);
        }

        private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        private void Shutdown(string reason)
        {
            if (_stopping) return;
            _stopping = true;

            DaemonPaths.Log("shutting down: " + reason);
            try { _host.Dispose(); } catch (Exception ex) { DaemonPaths.Log("dispose failed: " + ex.Message); }
            DaemonState.Delete();
            DaemonPaths.Log("daemon down");

            // The accept loop may be parked inside a pipe wait; there is nothing left worth
            // unwinding gracefully once the session is disposed.
            Environment.Exit(0);
        }
    }
}
