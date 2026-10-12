using System;
using System.Diagnostics;
using System.Threading;

namespace MechJebLib.Utils
{
    public abstract class PersistentAsyncJob : IDisposable
    {
        public enum JobState
        {
            Ready,
            Running,
            Completed,
            Faulted
        }

        public bool IsReady => State == JobState.Ready && _cooldownClock.ElapsedTicks >= _cooldownTicks;
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

        // High-precision real-world wall-clock pacing
        private readonly Stopwatch _cooldownClock = new Stopwatch();
        private readonly long _cooldownTicks;

        // Precision metrics clock matching AsyncJob tracking mechanics
        private readonly Stopwatch _lifecycleTimer = new Stopwatch();
        private readonly string _logTag;

        public abstract void Run(object? o = null);

        protected PersistentAsyncJob(string threadName, double minimumCooldownSeconds = 0.2)
        {
            // Standardize log tag formatting directly from the provided thread name
            _logTag = threadName.Replace("MechJebLib_", "").Replace("_", "");

            _cooldownTicks = (long)(minimumCooldownSeconds * Stopwatch.Frequency);
            _cooldownClock.Start();

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
        ///     Enforces a strict real-world wall-clock cadence completely independent of warp factors.
        /// </summary>
        public bool TryStartJob(object? o = null)
        {
            // 1. PURE REAL-TIME COOLDOWN SHIELD
            if (_cooldownClock.ElapsedTicks < _cooldownTicks)
                return false;

            // 2. ATOMIC STATE LOCK
            if (Interlocked.CompareExchange(ref _state, (int)JobState.Running, (int)JobState.Ready) != (int)JobState.Ready)
                return false;

            Exception = null;
            Volatile.Write(ref _pendingContext, o);

            // Reset the real-world stopwatch immediately upon a successful dispatch pass
            _cooldownClock.Restart();

            // Start the precision timer the exact millisecond we flag the background thread to awake
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

                _wakeupGate.Reset();

                // 1. Capture pure background thread wakeup latency from the gate signal
                double latency = _lifecycleTimer.Elapsed.TotalMilliseconds;

                // Instantly reset the stopwatch to isolate raw physics calculation duration
                _lifecycleTimer.Restart();

                object? context = Volatile.Read(ref _pendingContext);
                Volatile.Write(ref _pendingContext, null);

                try
                {
                    Run(context);

                    // 2. FREEZE THE CLOCK IMMEDIATELY. Do not let logging or strings bleed into it.
                    _lifecycleTimer.Stop();
                    double duration = _lifecycleTimer.Elapsed.TotalMilliseconds;

                    // Log safely using the processed thread name prefix
                    AsyncDevLogger.Log($"[{_logTag} Pure][Instance:{GetHashCode()}] Wakeup Latency: {latency:F4} ms | Execution: {duration:F4} ms");

                    Interlocked.Exchange(ref _state, (int)JobState.Completed);
                }
                catch (Exception ex)
                {
                    _lifecycleTimer.Stop();
                    double duration = _lifecycleTimer.Elapsed.TotalMilliseconds;

                    AsyncDevLogger.Log($"[{_logTag} Fault] Wakeup Latency: {latency:F4} ms | Execution Stalled at: {duration:F4} ms | Error: {ex.Message}");

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
