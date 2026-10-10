using System;
using System.Diagnostics;
using System.Threading;

namespace MechJebLib.Utils
{
    /// <summary>
    ///     High-performance, persistent thread-bound job runner using ManualResetEventSlim.
    ///     Includes isolated stopwatch telemetry to test the hybrid user-mode synchronization handoff.
    /// </summary>
    public abstract class PersistentAsyncJob : IDisposable
    {
        public enum JobState
        {
            Ready,
            Running,
            Completed,
            Faulted
        }

        public bool IsReady => State == JobState.Ready;
        public bool IsRunning => State == JobState.Running;
        public bool IsCompleted => State == JobState.Completed;
        public bool IsFaulted => State == JobState.Faulted;

        private int _state = (int)JobState.Ready;
        private int _isDisposed = 0;

        public JobState State => (JobState)Volatile.Read(ref _state);
        public Exception? Exception { get; private set; }

        private readonly Thread _workerThread;
        private readonly ManualResetEventSlim _wakeupGate = new ManualResetEventSlim(false);
        private object? _pendingContext;

        // High-precision tracking clock
        private readonly Stopwatch _lifecycleTimer = new Stopwatch();

        public abstract void Run(object? o = null);

        protected PersistentAsyncJob(string threadName = "PersistentAsyncJobWorker")
        {
            _workerThread = new Thread(ThreadLoop)
            {
                Name = threadName,
                IsBackground = true,
                Priority = ThreadPriority.Normal
            };
            _workerThread.Start();
        }

        public bool TryStartJob(object? o = null)
        {
            if (Interlocked.CompareExchange(ref _state, (int)JobState.Running, (int)JobState.Ready) != (int)JobState.Ready)
                return false;

            Exception = null;
            Volatile.Write(ref _pendingContext, o);

            // Start the clock the exact instant we poke the user-mode volatile flag
            _lifecycleTimer.Restart();

            _wakeupGate.Set();
            return true;
        }

        private void ThreadLoop()
        {
            while (Volatile.Read(ref _isDisposed) == 0)
            {
                _wakeupGate.Wait();

                if (Volatile.Read(ref _isDisposed) != 0)
                    break;

                // Capture pure hybrid-mode scheduling wakeup latency immediately
                double latency = _lifecycleTimer.Elapsed.TotalMilliseconds;

                _wakeupGate.Reset();

                object? context = Volatile.Read(ref _pendingContext);
                Volatile.Write(ref _pendingContext, null);

                // Instantly isolate the stopwatch for the active execution pass
                _lifecycleTimer.Restart();

                try
                {
                    Run(context);

                    // FREEZE THE CLOCK IMMEDIATELY. Do not let logging strings bleed into the metrics.
                    _lifecycleTimer.Stop();
                    double duration = _lifecycleTimer.Elapsed.TotalMilliseconds;

                    // Log after the stopwatch is safely frozen
                    AsyncDevLogger.Log($"[PersistentSlim Pure] Wakeup Latency: {latency:F4} ms | Execution: {duration:F4} ms");

                    Interlocked.Exchange(ref _state, (int)JobState.Completed);
                }
                catch (Exception ex)
                {
                    _lifecycleTimer.Stop();
                    double duration = _lifecycleTimer.Elapsed.TotalMilliseconds;

                    AsyncDevLogger.Log($"[PersistentSlim Fault] Wakeup Latency: {latency:F4} ms | Stalled at: {duration:F4} ms | Error: {ex.Message}");

                    Exception = ex;
                    Interlocked.Exchange(ref _state, (int)JobState.Faulted);
                }
            }
        }

        public bool TryMarkReady()
        {
            int current = Volatile.Read(ref _state);
            if (current == (int)JobState.Running)
                return false;

            return Interlocked.CompareExchange(ref _state, (int)JobState.Ready, current) == current;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                _wakeupGate.Set();
                _workerThread.Join(500);
                _wakeupGate.Dispose();
            }
        }
    }
}
