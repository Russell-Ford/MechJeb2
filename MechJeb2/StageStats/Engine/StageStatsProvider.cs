using System.Collections.Generic;
using MechJebLib.FuelFlowSimulation;
using MechJebLib.Primitives;
using MechJebLib.Utils;
using MechJebLibBindings.FuelFlowSimulation;

namespace MuMech
{
    public static class StageStatsProvider
    {
        // 1. THE STABLE CONSUMER FRONTIER: ONLY updated on demand at the start of a GUI Layout pass
        private static List<FuelStats> _atmoStats = new List<FuelStats>();
        private static List<FuelStats> _vacStats = new List<FuelStats>();

        public static IReadOnlyList<FuelStats> AtmoStats => _atmoStats;
        public static IReadOnlyList<FuelStats> VacStats => _vacStats;

        public static int AtmoDataVersion;
        public static int VacDataVersion;

        public static double AtmoT, VacT;
        public static V3 AtmoR, VacR;
        public static V3 AtmoV, VacV;
        public static V3 AtmoU, VacU;

        // 2. THE BACKGROUND ARRIVAL MAILBOX: Safe to swap at any random millisecond
        public static volatile List<FuelStats> ThreadBufferAtmo = new List<FuelStats>();
        public static volatile List<FuelStats> ThreadBufferVac = new List<FuelStats>();

        // --- Persistent Allocation Channels ---
        public static readonly SimVesselManager ManagerAtmo = new SimVesselManager("SharedStatsAtmo");
        public static readonly SimVesselManager ManagerVac = new SimVesselManager("SharedStatsVac");

        private static List<FuelStats> _internalScratchpadAtmo = new List<FuelStats>();
        private static List<FuelStats> _internalScratchpadVac = new List<FuelStats>();

        private static bool _hasSubscribedToGlobalSceneSwitch = false;

        public static void EnsureGlobalSceneListener()
        {
            if (_hasSubscribedToGlobalSceneSwitch) return;

            if (GameEvents.onGameSceneSwitchRequested != null)
            {
                GameEvents.onGameSceneSwitchRequested.Add(OnGlobalSceneSwitch);
                _hasSubscribedToGlobalSceneSwitch = true;
            }
        }

        private static void OnGlobalSceneSwitch(GameEvents.FromToAction<GameScenes, GameScenes> sceneData)
        {
            if (sceneData.to == GameScenes.MAINMENU)
            {
                ManagerAtmo?.Dispose();
                ManagerVac?.Dispose();
                _hasSubscribedToGlobalSceneSwitch = false;
            }
        }

        /// <summary>
        /// Asynchronously harvests completed background chunks safely into the thread buffer mailbox.
        /// </summary>
        public static void TryHarvestAtmo()
        {
            if (ManagerAtmo.FuelFlowSimulation.State == PersistentAsyncJob.JobState.Completed)
            {
                List<FuelStats> fresh = System.Threading.Interlocked.Exchange(ref ManagerAtmo.FuelFlowSimulation.Segments, _internalScratchpadAtmo);
                _internalScratchpadAtmo = System.Threading.Interlocked.Exchange(ref ThreadBufferAtmo, fresh);
                _internalScratchpadAtmo.Clear();

                AtmoT = ManagerAtmo.T; AtmoR = ManagerAtmo.R; AtmoV = ManagerAtmo.V; AtmoU = ManagerAtmo.U;
                System.Threading.Interlocked.Increment(ref AtmoDataVersion);
                ManagerAtmo.FuelFlowSimulation.TryMarkReady();
            }
            else if (ManagerAtmo.FuelFlowSimulation.State == PersistentAsyncJob.JobState.Faulted)
            {
                ManagerAtmo.FuelFlowSimulation.TryMarkReady();
            }
        }

        /// <summary>
        /// Asynchronously harvests completed background chunks safely into the thread buffer mailbox.
        /// </summary>
        public static void TryHarvestVac()
        {
            if (ManagerVac.FuelFlowSimulation.State == PersistentAsyncJob.JobState.Completed)
            {
                List<FuelStats> fresh = System.Threading.Interlocked.Exchange(ref ManagerVac.FuelFlowSimulation.Segments, _internalScratchpadVac);
                _internalScratchpadVac = System.Threading.Interlocked.Exchange(ref ThreadBufferVac, fresh);
                _internalScratchpadVac.Clear();

                VacT = ManagerVac.T; VacR = ManagerVac.R; VacV = ManagerVac.V; VacU = ManagerVac.U;
                System.Threading.Interlocked.Increment(ref VacDataVersion);
                ManagerVac.FuelFlowSimulation.TryMarkReady();
            }
            else if (ManagerVac.FuelFlowSimulation.State == PersistentAsyncJob.JobState.Faulted)
            {
                ManagerVac.FuelFlowSimulation.TryMarkReady();
            }
        }

        /// <summary>
        /// THE GATEKEEPER: Invoked exclusively on the main thread right before layout updates begin.
        /// Flushes the mailbox into the stable consumer array safely.
        /// </summary>
        public static void FlushMailboxToGUI()
        {
            // Atomically grab whatever is sitting in the thread arrival mailbox right now
            List<FuelStats> freshAtmo = System.Threading.Interlocked.Exchange(ref ThreadBufferAtmo, null);
            if (freshAtmo != null)
            {
                _atmoStats.Clear();
                _atmoStats.AddRange(freshAtmo);
                // Return the tracking container allocation back to the background mailbox channel
                System.Threading.Interlocked.Exchange(ref ThreadBufferAtmo, freshAtmo);
            }

            List<FuelStats> freshVac = System.Threading.Interlocked.Exchange(ref ThreadBufferVac, null);
            if (freshVac != null)
            {
                _vacStats.Clear();
                _vacStats.AddRange(freshVac);
                System.Threading.Interlocked.Exchange(ref ThreadBufferVac, freshVac);
            }
        }
    }
}
