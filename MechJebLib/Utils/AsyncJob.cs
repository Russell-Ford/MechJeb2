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

        public bool IsReady     => State == JobState.Ready;
        public bool IsRunning   => State == JobState.Running;
        public bool IsCompleted => State == JobState.Completed;
        public bool IsFaulted   => State == JobState.Faulted;
        public bool IsCancelled => State == JobState.Cancelled; //good luck cancelling fast enough
        public bool IsStopped   => State >= JobState.Completed; //not yet implemented
        

        //private Task? _task = null;
 

        public JobState State     => (JobState)Volatile.Read(ref _state);
        public Exception? Exception { get; private set; }

        protected CancellationToken CancelToken { get; private set; }

        public abstract void Run(object? o = null);

        private int _state = (int)JobState.Ready;
        private readonly Action<object?> _runWrapped;
        private CancellationTokenSource? _cts;
        private object lastUsedObj = null;


        protected AsyncJob()
        {
            _runWrapped = RunWrapped;
        }


        /// <summary>
        ///     Attempts to start a NEW Task. Returns false if Running.
        ///     FIXME: Create listeners inside Task for re-use and implement TaskCreationOptions.LongRunning
        ///     This reduces our latency/startup time from microseconds to sub-microseconds
        /// </summary>
        public bool TryStartJob(object? o = null)
        {
            //if we don't have an object that's doing something separate from ksp here then let's just fail and not check the lock
            //easiest way to avoid this ever happening is to force the call through SimVesselManager
            if (o == null)
                return false;
            if (Interlocked.CompareExchange(ref _state, (int)JobState.Running, (int)JobState.Ready) != (int)JobState.Ready)
                return false;



            Exception = null;
            _cts = new CancellationTokenSource();
            CancelToken = _cts.Token;
            Task.Factory.StartNew(
                _runWrapped,
                o,
                _cts.Token,
                TaskCreationOptions.DenyChildAttach,// | TaskCreationOptions.LongRunning,  //we can't use this until we actually implement persistent threads
                TaskScheduler.Default
            );

            return true;
        }

        /// <summary>
        ///     Consumer calls this after reading results to allow the next job to start.
        ///     Refactored into a stub for TryStartJob to maintain backwards compatibility
        /// </summary>
        public bool TryMarkReady(object? simVessel = null)
        {
            return TryStartJob(simVessel);
            // int current = Volatile.Read(ref _state);
            // if (current == (int)JobState.Running)
            //     return false;

            // return Interlocked.CompareExchange(ref _state, (int)JobState.Ready, current) == current;
        }

        public void Cancel() => _cts?.Cancel();

        public void CancelAfter(TimeSpan timeout) => _cts?.CancelAfter(timeout);

        private void RunWrapped(object? o = null)
        {
            try
            {
                Run(o);
                Interlocked.Exchange(ref _state, (int)JobState.Completed);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _state, (int)JobState.Cancelled);
            }
            catch (Exception ex)
            {
                Exception = ex;
                Interlocked.Exchange(ref _state, (int)JobState.Faulted);
            }
        }
    }
}
