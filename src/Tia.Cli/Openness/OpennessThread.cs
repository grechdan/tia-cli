using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TiaCli.Openness
{
    /// <summary>
    /// Runs every Openness call on one dedicated thread.
    ///
    /// The Openness object model is not thread-safe and hands out objects bound to the thread that
    /// created them, so a TiaPortal created on one thread and used from another produces intermittent
    /// RemotingExceptions rather than a clean failure. Funnelling all access through a single thread
    /// removes that class of bug, and it serialises requests, which is what we want anyway: TIA cannot
    /// service two operations at once. In the daemon this matters twice over, because several CLI
    /// invocations can be connected at the same time.
    /// </summary>
    internal sealed class OpennessThread : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;

        public OpennessThread()
        {
            _thread = new Thread(Pump)
            {
                Name = "openness",
                IsBackground = true,
            };
            // Openness hosts WPF dialogs (UMAC login, upgrade prompts) when started with a UI.
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void Pump()
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try { work(); }
                catch { /* every work item captures its own faults into its TCS */ }
            }
        }

        public Task<T> Run<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();
            _queue.Add(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        public Task Run(Action action)
        {
            return Run<object>(() => { action(); return null; });
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(30));
            _queue.Dispose();
        }
    }
}
