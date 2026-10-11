extern alias JetBrainsAnnotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MechJebLib.FuelFlowSimulation;
using MechJebLib.Primitives;
using MechJebLib.Utils;
using MechJebLibBindings;
using MechJebLibBindings.FuelFlowSimulation;
using Unity.Profiling;
using Debug = UnityEngine.Debug;

namespace MuMech
{
    public class MechJebModuleStageStats : ComputerModule
    {
        [ToggleInfoItem("#MechJeb_DVincludecosinelosses", InfoItem.Category.Thrust, showInEditor = true)] //ΔV include cosine losses
        public readonly bool DVLinearThrust = true;

        public CelestialBody EditorBody;
        public bool LiveSLT = true;
        public double AltSLT = 0;
        public double Mach = 0;

        public double AtmoT, VacT;
        public V3 AtmoR, VacR;
        public V3 AtmoV, VacV;
        public V3 AtmoU, VacU;

        private bool _needsStructuralRebuild = true;

        public MechJebModuleStageStats(MechJebCore core) : base(core)
        {
            Enabled = true;
        }

        //private bool _vesselModified = true;

        protected override void OnModuleEnabled() => _needsStructuralRebuild = true;

        protected override void OnModuleDisabled()
        {
            // unsure about this
            _vesselManagerAtmo.Release();
            _vesselManagerVac.Release();
        }

        private readonly SimVesselManager _vesselManagerAtmo = new SimVesselManager("StageStatsAtmo");
        private readonly SimVesselManager _vesselManagerVac = new SimVesselManager("StageStatsVac");

        //public override void OnFixedUpdate() => GetResults();

        //public override void OnUpdate() => GetResults();

        private static ProfilerMarker _newRunSimulationProfile = new ProfilerMarker("RunSimulation");
        private static ProfilerMarker _newBuildProfile = new ProfilerMarker("Build");
        //private static ProfilerMarker _newUpdateProfile = new ProfilerMarker("Update");
        private static ProfilerMarker _newVacProfile = new ProfilerMarker("Vac");
        private static ProfilerMarker _newAtmoProfile = new ProfilerMarker("Atmo");

        private StageStatsUnityDriver _unityDriver;

        private bool _runAlternateTick = false;
        // 1. PUBLIC FRONTIER: Downstream modules can read, count, and index, 
        // but they can NEVER mutate or clear the collection.
        public IReadOnlyList<FuelStats> AtmoStats => _publicAtmoStats;
        public IReadOnlyList<FuelStats> VacStats => _publicVacStats;

        // 2. INTERNAL BACKING CHANNELS: Standard lists for your high-speed pointer swaps
        private volatile List<FuelStats> _publicAtmoStats = new List<FuelStats>();
        private volatile List<FuelStats> _publicVacStats = new List<FuelStats>();

        // 3. RECIPROCAL SCRATCHPADS: The empty buckets traded back to the background threads
        private List<FuelStats> _atmoBuffer = new List<FuelStats>();
        private List<FuelStats> _vacBuffer = new List<FuelStats>();
        public int AtmoDataVersion => _atmoDataVersion;
        public int VacDataVersion => _vacDataVersion;

        private volatile int _atmoDataVersion = 0;
        private volatile int _vacDataVersion = 0;

        /// <summary>
        ///     Invoked by the driver proxy exclusively on every graphic render frame.
        ///     Maximizes FPS by decoupling calculations completely from physics time warp ticks.
        /// </summary>
        public void DriverUpdate()
        {
            // 1. Scene validation guards FIRST: Exit immediately if the current scene state is unstable
            if (HighLogic.LoadedSceneIsEditor)
            {
                if (EditorBody is null) return;
            }
            else
            {
                if (Vessel is null) return;
            }

            // 2. Harvest completed thread data at a clean, synchronized frame gateway
            // does nothing if FFS is not completed
            GetResults();

            // 3. Tick staggered simulation cycles safely
            ExecuteStaggeredSimulation();
        }

       

        /// <summary>
        ///     Core orchestration logic that safely handles the alternating frame runs.
        /// </summary>
        private void ExecuteStaggeredSimulation()
        {
            // 1. ISOLATION GUARD: If we are coasting in orbit with zero throttle, 
            // put the background threads to sleep to save main-thread frametime.
            if (!HighLogic.LoadedSceneIsEditor)
            {
                bool enginesActive = Vessel.ctrlState.mainThrottle > 0 || Vessel.ActionGroups[KSPActionGroup.RCS];
                if (!enginesActive && _needsStructuralRebuild == false)
                {
                    return; // Exit immediately. Leave the background threads resting.
                }
            }

            _runAlternateTick = !_runAlternateTick;

            if (_runAlternateTick)
            {
                if (_vesselManagerVac.FuelFlowSimulation.IsReady)
                {
                    RunVacuumSimulationOnly();
                }
            }
            else
            {
                if (_vesselManagerAtmo.FuelFlowSimulation.IsReady)
                {
                    RunAtmosphericSimulationOnly();
                }
            }
        }


        private void GetResults()
        {
            // --- Atmospheric Stream Processing ---
            var atmoState = _vesselManagerAtmo.FuelFlowSimulation.State;

            if (atmoState == PersistentAsyncJob.JobState.Completed)
            {
                // 1. Cross-Thread Swap: Grab the completed results from the simulation field.
                // Hand the simulation your empty '_atmoBuffer' container to use for its next background loop.
                List<FuelStats> freshAtmo = Interlocked.Exchange(ref _vesselManagerAtmo.FuelFlowSimulation.Segments, _atmoBuffer);

                // 2. Consumer Handoff: Atomically substitute the public reference pointer.
                List<FuelStats> oldAtmo = Interlocked.Exchange(ref _publicAtmoStats, freshAtmo);

                // 3. Recycle: Clear out the obsolete consumer list instance and store it as your next scratch pad.
                // This keeps your memory capacity expanded on the heap, ensuring absolute zero allocation churn.
                oldAtmo.Clear();
                _atmoBuffer = oldAtmo;

                Interlocked.Increment(ref _atmoDataVersion);

                AtmoT = _vesselManagerAtmo.T;
                AtmoR = _vesselManagerAtmo.R;
                AtmoV = _vesselManagerAtmo.V;
                AtmoU = _vesselManagerAtmo.U;

                _vesselManagerAtmo.FuelFlowSimulation.TryMarkReady();
            }
            else if (atmoState == PersistentAsyncJob.JobState.Faulted)
            {
                Debug.Log("[MechJebModuleStageStats] atmo stats failed");
                if (_vesselManagerAtmo.FuelFlowSimulation.Exception != null)
                    Debug.Log(_vesselManagerAtmo.FuelFlowSimulation.Exception);

                _vesselManagerAtmo.FuelFlowSimulation.TryMarkReady();
            }

            // --- Vacuum Stream Processing ---
            var vacState = _vesselManagerVac.FuelFlowSimulation.State;

            if (vacState == PersistentAsyncJob.JobState.Completed)
            {
                // 1. Cross-Thread Swap
                List<FuelStats> freshVac = Interlocked.Exchange(ref _vesselManagerVac.FuelFlowSimulation.Segments, _vacBuffer);

                // 2. Consumer Handoff
                List<FuelStats> oldVac = Interlocked.Exchange(ref _publicVacStats, freshVac);

                // 3. Recycle
                oldVac.Clear();
                _vacBuffer = oldVac;

                Interlocked.Increment(ref _vacDataVersion);

                VacT = _vesselManagerVac.T;
                VacR = _vesselManagerVac.R;
                VacV = _vesselManagerVac.V;
                VacU = _vesselManagerVac.U;

                _vesselManagerVac.FuelFlowSimulation.TryMarkReady();
            }
            else if (vacState == PersistentAsyncJob.JobState.Faulted)
            {
                Debug.Log("[MechJebModuleStageStats] vac stats failed");
                if (_vesselManagerVac.FuelFlowSimulation.Exception != null)
                    Debug.Log(_vesselManagerVac.FuelFlowSimulation.Exception);

                _vesselManagerVac.FuelFlowSimulation.TryMarkReady();
            }
        }


        private void RunVacuumSimulationOnly()
        {
            using ProfilerMarker.AutoScope auto = _newRunSimulationProfile.Auto();

            // Perform structural builds or updates before launching the job if needed
            if (_needsStructuralRebuild || HighLogic.LoadedSceneIsEditor)
            {
                using ProfilerMarker.AutoScope auto2 = _newBuildProfile.Auto();
                IShipconstruct v = HighLogic.LoadedSceneIsEditor ? (IShipconstruct)EditorLogic.fetch.ship : Vessel;

                _vesselManagerVac.Build(v);
                _needsStructuralRebuild = false;
            }

            using (_newVacProfile.Auto())
            {
                _vesselManagerVac.DVLinearThrust = DVLinearThrust;
                _vesselManagerVac.SetConditions(0, 0, 0);
                _vesselManagerVac.SetInitial(VesselState.Time, VesselState.OrbitalPosition.WorldToV3Rotated(),
                    VesselState.OrbitalVelocity.WorldToV3Rotated(), VesselState.Forward.WorldToV3Rotated());

                _vesselManagerVac.TryStartFuelFlowSimulationJob();
            }
        }

        private void RunAtmosphericSimulationOnly()
        {
            using ProfilerMarker.AutoScope auto = _newRunSimulationProfile.Auto();

            CelestialBody simBody = HighLogic.LoadedSceneIsEditor ? EditorBody : Vessel.mainBody;

            double staticPressureKpa = HighLogic.LoadedSceneIsEditor || !LiveSLT
                ? simBody.atmosphere ? simBody.GetPressure(AltSLT) : 0
                : Vessel.staticPressurekPa;
            double atmDensity = (HighLogic.LoadedSceneIsEditor || !LiveSLT
                ? simBody.GetDensity(simBody.GetPressure(AltSLT), simBody.GetTemperature(0))
                : Vessel.atmDensity) / 1.225;
            double mach = HighLogic.LoadedSceneIsEditor ? Mach : Vessel.mach;

            if (_needsStructuralRebuild || HighLogic.LoadedSceneIsEditor)
            {
                using ProfilerMarker.AutoScope auto2 = _newBuildProfile.Auto();
                IShipconstruct v = HighLogic.LoadedSceneIsEditor ? (IShipconstruct)EditorLogic.fetch.ship : Vessel;

                _vesselManagerAtmo.Build(v);
                _needsStructuralRebuild = false;
            }

            using (_newAtmoProfile.Auto())
            {
                _vesselManagerAtmo.DVLinearThrust = DVLinearThrust;
                _vesselManagerAtmo.SetConditions(atmDensity, staticPressureKpa * PhysicsGlobals.KpaToAtmospheres, mach);
                _vesselManagerAtmo.SetInitial(VesselState.Time, VesselState.OrbitalPosition.WorldToV3Rotated(),
                    VesselState.OrbitalVelocity.WorldToV3Rotated(), VesselState.Forward.WorldToV3Rotated());

                _vesselManagerAtmo.TryStartFuelFlowSimulationJob();
            }
        }

        public override void OnStart(PartModule.StartState state)
        {
            if (_unityDriver == null)
            {
                _unityDriver = HighLogic.LoadedSceneIsEditor
                    ? EditorLogic.fetch.gameObject.AddComponent<StageStatsUnityDriver>()
                    : Vessel.gameObject.AddComponent<StageStatsUnityDriver>();

                _unityDriver.Initialize(this);
            }

            // Structural flight listeners
            GameEvents.StageManager.OnGUIStageSequenceModified.Add(OnStructuralChange);
            GameEvents.onStageActivate.Add(OnStageActivated);
            GameEvents.onPartDie.Add(OnPartDestroyed);

            if (HighLogic.LoadedSceneIsEditor)
            {
                GameEvents.onEditorShipModified.Add(OnEditorShipModified);
                GameEvents.onPartCrossfeedStateChange.Add(OnPartCrossfeedStateChange);
            }
            else
            {
                GameEvents.onVesselWasModified.Add(OnVesselStructureChanged);
            }
        }

        public override void OnDestroy()
        {
            base.OnDestroy();

            // Destroy the proxy component to avoid memory leaks across scene switches
            if (_unityDriver != null)
            {
                UnityEngine.Object.Destroy(_unityDriver);
                _unityDriver = null!;
            }

            _vesselManagerAtmo.Dispose();
            _vesselManagerVac.Dispose();

            // Unregister flight structural listeners
            GameEvents.StageManager.OnGUIStageSequenceModified.Remove(OnStructuralChange);
            GameEvents.onStageActivate.Remove(OnStageActivated);
            GameEvents.onPartDie.Remove(OnPartDestroyed);

            // Unregister environment-specific listeners cleanly
            GameEvents.onEditorShipModified.Remove(OnEditorShipModified);
            GameEvents.onPartCrossfeedStateChange.Remove(OnPartCrossfeedStateChange);
            GameEvents.onVesselWasModified.Remove(OnVesselStructureChanged);
        }

        // Master structural handler
        private void OnStructuralChange() => _needsStructuralRebuild = true;

        // Explicit KSP parameter overloads to satisfy compiler method groups
        private void OnEditorShipModified(ShipConstruct data) => OnStructuralChange();
        private void OnPartCrossfeedStateChange(Part data) => OnStructuralChange();
        private void OnStageActivated(int stage) => OnStructuralChange();
        private void OnPartDestroyed(Part data) => OnStructuralChange();
        private void OnVesselStructureChanged(Vessel data)
        {
            if (data == Vessel) OnStructuralChange();
        }


        public void RequestUpdate()
        {
            //empty stub for backwards compat
            //GetResults();

            //TryStartSimulation();
        }
    }
}
