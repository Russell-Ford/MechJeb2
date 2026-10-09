/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MechJebLib.Utils
{
    /// <summary>
    ///     Simple Async Job Runner implementation.  This uses a base class for code-sharing so is not
    ///     the best software design, but should meet the needs of this codebase.
    /// </summary>
    public abstract class AsyncJob
    {
        public enum JobState
        {
            Ready,
            Running,
            Completed,
            Faulted,
            Cancelled
        }

        public bool IsReady => State == JobState.Ready;
        public bool IsRunning => State == JobState.Running;
        public bool IsCompleted => State == JobState.Completed;
        public bool IsFaulted => State == JobState.Faulted;
        public bool IsCancelled => State == JobState.Cancelled;
        public bool IsStopped => State >= JobState.Completed;

        private Task? _task;
        private CancellationTokenSource? _cts;
        private int _state = (int)JobState.Ready;

        public JobState State => (JobState)Volatile.Read(ref _state);
        public Exception? Exception { get; private set; }

        protected CancellationToken CancelToken { get; private set; }

        public abstract void Run(object? o = null);

        private readonly Action<object?> _runWrapped;

        // Precision clock assigned once per runner context
        private readonly Stopwatch _lifecycleTimer = new Stopwatch();

        protected AsyncJob()
        {
            _runWrapped = RunWrapped;
        }

        /// <summary>
        ///     Attempts to start the job. Returns false if not in Ready state.
        /// </summary>
        public bool TryStartJob(object? o = null)
        {
            if (Interlocked.CompareExchange(ref _state, (int)JobState.Running, (int)JobState.Ready) != (int)JobState.Ready)
                return false;

            Exception = null;
            _cts = new CancellationTokenSource();
            CancelToken = _cts.Token;

            // Start the precision timer the exact millisecond we queue the task to the TPL scheduler
            _lifecycleTimer.Restart();

            _task = Task.Factory.StartNew(
                _runWrapped,
                o,
                _cts.Token,
                TaskCreationOptions.DenyChildAttach, // | TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
            return true;
        }

        private void RunWrapped(object? o = null)
        {
            // 1. Capture pure TPL scheduling wakeup latency
            double latency = _lifecycleTimer.Elapsed.TotalMilliseconds;

            // Instantly reset the stopwatch to isolate raw physics calculation duration
            _lifecycleTimer.Restart();

            try
            {
                Run(o);

                // 2. FREEZE THE CLOCK IMMEDIATELY. Do not let logging or strings bleed into it.
                _lifecycleTimer.Stop();
                double duration = _lifecycleTimer.Elapsed.TotalMilliseconds;

                // Log safely after the clock is completely frozen
                AsyncDevLogger.Log($"[PooledJob Pure] Wakeup Latency: {latency:F4} ms | Execution: {duration:F4} ms");

                Interlocked.Exchange(ref _state, (int)JobState.Completed);
            }
            catch (OperationCanceledException)
            {
                _lifecycleTimer.Stop();
                double duration = _lifecycleTimer.Elapsed.TotalMilliseconds;

                AsyncDevLogger.Log($"[PooledJob Cancelled] Wakeup Latency: {latency:F4} ms | Aborted after: {duration:F4} ms");

                Interlocked.Exchange(ref _state, (int)JobState.Cancelled);
            }
            catch (Exception ex)
            {
                _lifecycleTimer.Stop();
                double duration = _lifecycleTimer.Elapsed.TotalMilliseconds;

                AsyncDevLogger.Log($"[PooledJob Fault] Wakeup Latency: {latency:F4} ms | Execution Stalled at: {duration:F4} ms | Error: {ex.Message}");

                Exception = ex;
                Interlocked.Exchange(ref _state, (int)JobState.Faulted);
            }
        }

        /// <summary>
        ///     Consumer calls this after reading results to allow the next job to start.
        ///     Returns false if job is still running.
        /// </summary>
        public bool TryMarkReady()
        {
            int current = Volatile.Read(ref _state);
            if (current == (int)JobState.Running)
                return false;

            return Interlocked.CompareExchange(ref _state, (int)JobState.Ready, current) == current;
        }

        public void Cancel() => _cts?.Cancel();

        public void CancelAfter(TimeSpan timeout) => _cts?.CancelAfter(timeout);
    }
}
