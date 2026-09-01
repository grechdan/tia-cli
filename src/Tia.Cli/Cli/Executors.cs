using System;
using System.Runtime.CompilerServices;
using System.Threading;
using TiaCli.Daemon;
using TiaCli.Openness;
using TiaCli.Protocol;

namespace TiaCli.Cli
{
    /// <summary>
    /// Where a command's work actually happens. Commands are written against this and never learn
    /// whether they were served by a daemon or by this very process.
    /// </summary>
    internal interface IExecutor : IDisposable
    {
        WireResponse Send(string method, object parameters);

        /// <summary>Shown by 'session status' so it is always clear which half answered.</summary>
        string Kind { get; }
    }

    /// <summary>Routes to a running daemon. Loads no Openness assembly at all.</summary>
    internal sealed class DaemonExecutor : IExecutor
    {
        private long _id;

        public string Kind => "daemon";

        public WireResponse Send(string method, object parameters)
        {
            var request = new WireRequest
            {
                Id = Interlocked.Increment(ref _id),
                Method = method,
                Params = parameters == null ? (System.Text.Json.JsonElement?)null : JsonUtil.ToElement(parameters),
            };

            return DaemonClient.Send(request);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Does the work in this process. The session it acquires dies when the command ends - fine for
    /// attaching to a portal that is already running, which is the common case, and the reason
    /// 'session start' exists for everything else.
    /// </summary>
    internal sealed class DirectExecutor : IExecutor
    {
        private readonly SessionHost _host;
        private long _id;

        public DirectExecutor(AcquireOptions options)
        {
            _host = new SessionHost(options);
        }

        public string Kind => "direct";

        public WireResponse Send(string method, object parameters)
        {
            var request = new WireRequest
            {
                Id = Interlocked.Increment(ref _id),
                Method = method,
                Params = parameters == null ? (System.Text.Json.JsonElement?)null : JsonUtil.ToElement(parameters),
            };

            try
            {
                var result = _host.Invoke(request);
                return new WireResponse { Id = request.Id, Ok = true, Result = JsonUtil.ToElement(result) };
            }
            catch (Exception ex)
            {
                return new WireResponse { Id = request.Id, Ok = false, Error = ErrorTranslator.Describe(ex) };
            }
        }

        public void Dispose() => _host.Dispose();
    }

    /// <summary>
    /// Builds executors while respecting the one hard startup rule: no method that references an
    /// Openness-backed type may be *entered* before OpennessResolver.Install has run, because the JIT
    /// resolves a method's types when the method starts, not when a given line executes. Hence the
    /// deliberate two-step with a NoInlining core.
    /// </summary>
    internal static class ExecutorFactory
    {
        public static IExecutor CreateDirect(AcquireOptions options, string opennessVersion)
        {
            OpennessResolver.Install(opennessVersion);
            return CreateDirectCore(options);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IExecutor CreateDirectCore(AcquireOptions options) => new DirectExecutor(options);

        public static int Serve(ServeOptions options, string opennessVersion)
        {
            OpennessResolver.Install(opennessVersion);
            return ServeCore(options);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ServeCore(ServeOptions options) => new DaemonServer(options).Run();
    }
}
