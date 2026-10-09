using System;
using System.Diagnostics;
using System.Threading;

namespace MechJebLib.Utils
{
    /// <summary>
    ///     High-performance, persistent thread-bound job runner.
    ///     Spawns exactly ONE dedicated background thread for its entire lifecycle
    ///     and utilizes a kernel-level AutoResetEvent gate to sleep/wake efficiently.
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
        private readonly AutoResetEvent _wakeupGate = new AutoResetEvent(false);

        // Insulated state storage updated atomically before poking the gate
        private object? _pendingContext;

        // performance tracking
        private readonly Stopwatch _lifecycleTimer = new Stopwatch();

        private double _startupLatencyMs;
        private double _executionDurationMs;

        public double StartupLatencyMs => _startupLatencyMs;
        public double ExecutionDurationMs => _executionDurationMs;


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

        /// <summary>
        ///     Signals the permanent background thread to wake up and execute work.
        ///     Returns false if a job is already actively running.
        /// </summary>
        public bool TryStartJob(object? o = null)
        {
            if (Interlocked.CompareExchange(ref _state, (int)JobState.Running, (int)JobState.Ready) != (int)JobState.Ready)
                return false;

            Exception = null;

            // Hand off the context reference safely before the worker wakes up
            Volatile.Write(ref _pendingContext, o);

            // Pulse the kernel event gate to wake up the worker loop
            _wakeupGate.Set();
            return true;
        }

        private void ThreadLoop()
        {
            while (Volatile.Read(ref _isDisposed) == 0)
            {
                _wakeupGate.WaitOne();

                if (Volatile.Read(ref _isDisposed) != 0)
                    break;

                // 1. Capture the exact millisecond the kernel gate broke
                double latency = _lifecycleTimer.Elapsed.TotalMilliseconds;

                object? context = Volatile.Read(ref _pendingContext);
                Volatile.Write(ref _pendingContext, null);

                // Restart the precision clock for raw physics execution profiling
                _lifecycleTimer.Restart();

                try
                {
                    Run(context);

                    double duration = _lifecycleTimer.Elapsed.TotalMilliseconds;

                    // 2. Log the successful run to AsyncDevLogger for your direct benchmark profile
                    AsyncDevLogger.Log($"[PersistentJob Benchmark] Wakeup Latency: {latency:F4} ms | Execution: {duration:F4} ms");

                    Interlocked.Exchange(ref _state, (int)JobState.Completed);
                }
                catch (Exception ex)
                {
                    double duration = _lifecycleTimer.Elapsed.TotalMilliseconds;

                    // Log the failure to the same stream to track down if stalls correlate with engine faults
                    AsyncDevLogger.Log($"[PersistentJob Fault] Wakeup Latency: {latency:F4} ms | Execution Stalled at: {duration:F4} ms | Error: {ex.Message}");

                    Exception = ex;
                    Interlocked.Exchange(ref _state, (int)JobState.Faulted);
                }
                finally
                {
                    _lifecycleTimer.Stop();
                }
            }
        }

        /// <summary>
        ///     Resets the job state back to Ready so it can accept a new signal pass.
        /// </summary>
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
                _wakeupGate.Set(); // Break the WaitOne kernel block to allow loop shutdown
                _workerThread.Join(500); // Give the OS half a second to clean up the handle
                _wakeupGate.Dispose();
            }
        }
    }
}
