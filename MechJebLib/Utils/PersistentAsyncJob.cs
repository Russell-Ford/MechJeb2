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

        // High-precision real-world wall-clock pacing
        private readonly Stopwatch _cooldownClock = new Stopwatch();
        private readonly long _cooldownTicks;

        public abstract void Run(object? o = null);

        protected PersistentAsyncJob(string threadName, double minimumCooldownSeconds = 0.2)
        {
            // Convert seconds to high-precision Stopwatch ticks to avoid floating-point drift
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

                object? context = Volatile.Read(ref _pendingContext);
                Volatile.Write(ref _pendingContext, null);

                try
                {
                    Run(context);
                    Interlocked.Exchange(ref _state, (int)JobState.Completed);
                }
                catch (Exception ex)
                {
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
